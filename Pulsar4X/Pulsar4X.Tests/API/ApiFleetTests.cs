using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Fleets;
using Pulsar4X.Messaging;
using Pulsar4X.Names;

namespace Pulsar4X.Tests
{
    /// <summary>The fleet command surface and the fleet-hierarchy push.</summary>
    [TestFixture]
    public class ApiFleetTests : ApiTestBase
    {
        [Test]
        public async Task FleetReorganized_message_pushes_a_FleetsChanged_delta()
        {
            // Fleet ops (create fleet, assign/transfer ship, …) reshape the tree without an entity
            // add/remove, so the engine signals them with a FleetReorganized message. Verify the server
            // turns that into a FleetsChanged push (the bug: the list didn't update on those ops).
            var session = Connect();
            var received = new List<GameEventEnvelope>();
            using (_server.Subscribe(session, received.Add))
            {
                received.Clear(); // discard the initial connect push

                await MessagePublisher.Instance.Publish(
                    Message.Create(MessageTypes.FleetReorganized, factionId: session.FactionId));

                Assert.That(received.Any(e => e.Type == GameEventType.FleetsChanged && e.Fleets != null), Is.True);
            }
        }

        [Test]
        public void CreateFleet_command_creates_and_pushes_the_fleet()
        {
            var session = Connect();
            var received = new List<GameEventEnvelope>();
            using (_server.Subscribe(session, received.Add))
            {
                received.Clear(); // discard the initial connect push

                var result = _server.SubmitCommand(session,
                    new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
                Assert.That(result.Accepted, Is.True, result.RejectionReason);

                var (fleets, unattached) = _projector.ProjectFleetHierarchy(session.FactionId);
                Assert.That(fleets, Has.Count.EqualTo(1));
                Assert.That(fleets[0].SystemId, Is.EqualTo(_game.Systems[0].ID));
                Assert.That(unattached, Is.Empty);

                // The fleet op raised FleetReorganized, which became a self-contained FleetsChanged push.
                var push = received.LastOrDefault(e => e.Type == GameEventType.FleetsChanged);
                Assert.That(push, Is.Not.Null, "expected a FleetsChanged push");
                Assert.That(push!.Fleets, Has.Count.EqualTo(1));
                Assert.That(push.UnattachedShips, Is.Not.Null);
            }
        }

        [Test]
        public async Task OrdersChanged_message_pushes_a_FleetsChanged_delta()
        {
            // Orders are carried on the fleet snapshot, so a queue mutation (queue/cancel/pause/
            // standing-order swap) signals OrdersChanged — which, like FleetReorganized, re-pushes
            // the whole fleet tree. Without this the order list froze while paused (the bug).
            var session = Connect();
            var received = new List<GameEventEnvelope>();
            using (_server.Subscribe(session, received.Add))
            {
                received.Clear(); // discard the initial connect push

                await MessagePublisher.Instance.Publish(
                    Message.Create(MessageTypes.OrdersChanged, factionId: session.FactionId));

                Assert.That(received.Any(e => e.Type == GameEventType.FleetsChanged && e.Fleets != null), Is.True);
            }
        }

        [Test]
        public void Queue_mutating_command_pushes_the_fleet_tree_while_paused()
        {
            // End to end while paused: a command that changes a fleet's order state must re-push the
            // fleet tree even though no clock advance occurs. SetStandingOrders is such a command — it
            // mutates the fleet's standing-order list directly and publishes OrdersChanged.
            var session = Connect();
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Pause));
            _server.SubmitCommand(session, new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
            var (fleets, _) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets, Has.Count.EqualTo(1));

            var received = new List<GameEventEnvelope>();
            using (_server.Subscribe(session, received.Add))
            {
                received.Clear(); // discard the initial connect push

                var result = _server.SubmitCommand(session,
                    new SetStandingOrdersCommand(fleets[0].Id, new List<StandingOrder>()));
                Assert.That(result.Accepted, Is.True, result.RejectionReason);

                var push = received.LastOrDefault(e => e.Type == GameEventType.FleetsChanged);
                Assert.That(push, Is.Not.Null, "expected a FleetsChanged push from the order-queue change");
                Assert.That(push!.Fleets, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void DisbandFleet_command_removes_the_fleet()
        {
            var session = Connect();
            _server.SubmitCommand(session, new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
            var (fleets, _) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets, Has.Count.EqualTo(1));

            var result = _server.SubmitCommand(session, new DisbandFleetCommand(fleets[0].Id));

            Assert.That(result.Accepted, Is.True, result.RejectionReason);
            Assert.That(_projector.ProjectFleetHierarchy(session.FactionId).Fleets, Is.Empty);
        }

        [Test]
        public void ChangeFleetParent_command_nests_a_fleet()
        {
            var session = Connect();
            _server.SubmitCommand(session, new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
            _server.SubmitCommand(session, new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
            var (fleets, _) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets, Has.Count.EqualTo(2));

            var result = _server.SubmitCommand(session,
                new ChangeFleetParentCommand(fleets[1].Id, fleets[0].Id));

            Assert.That(result.Accepted, Is.True, result.RejectionReason);
            var (nested, _) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(nested, Has.Count.EqualTo(1));
            Assert.That(nested[0].SubFleets, Has.Count.EqualTo(1));
            Assert.That(nested[0].SubFleets[0].Id, Is.EqualTo(fleets[1].Id));
        }

        [Test]
        public void ReassignShip_moves_unattached_ship_into_fleet_and_back()
        {
            var session = Connect();
            Assert.That(_game.Factions.TryGetValue(session.FactionId, out var faction), Is.True);

            // Place a ship-like entity on the faction root (unattached).
            var ship = Entity.Create(session.FactionId);
            _game.Systems[0].AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("Test Courier", session.FactionId, "Test Courier"),
            });
            Assert.That(faction.TryGetDataBlob<FleetDB>(out var factionFleet), Is.True);
            factionFleet!.AddChild(ship);

            _server.SubmitCommand(session, new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
            var (fleets, unattached) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets, Has.Count.EqualTo(1));
            Assert.That(unattached.Any(s => s.Id == ship.Id), Is.True);

            var assign = _server.SubmitCommand(session, new ReassignShipCommand(ship.Id, fleets[0].Id));
            Assert.That(assign.Accepted, Is.True, assign.RejectionReason);

            (fleets, unattached) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(unattached.Any(s => s.Id == ship.Id), Is.False);
            Assert.That(fleets[0].Ships.Any(s => s.Id == ship.Id), Is.True);
            Assert.That(fleets[0].FlagshipId, Is.EqualTo(ship.Id));

            var unassign = _server.SubmitCommand(session, new ReassignShipCommand(ship.Id, session.FactionId));
            Assert.That(unassign.Accepted, Is.True, unassign.RejectionReason);

            (fleets, unattached) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets[0].Ships.Any(s => s.Id == ship.Id), Is.False);
            Assert.That(unattached.Any(s => s.Id == ship.Id), Is.True);
        }

        [Test]
        public void RenameFleet_updates_projected_fleet_name()
        {
            var session = Connect();
            _server.SubmitCommand(session, new CreateFleetCommand(session.FactionId, _game.Systems[0].ID));
            var (fleets, _) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets, Has.Count.EqualTo(1));

            var result = _server.SubmitCommand(session, new Pulsar4X.Api.RenameCommand(fleets[0].Id, "Celestial Commanders"));
            Assert.That(result.Accepted, Is.True, result.RejectionReason);

            (fleets, _) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(fleets[0].Name, Is.EqualTo("Celestial Commanders"));
        }

        [Test]
        public async Task AttachUnattachedShip_pushes_FleetsChanged_with_the_ship()
        {
            // Mirrors post-construction / post-launch membership: EntityAdded alone projects the
            // tree before AddChild, so FleetHierarchy.AttachUnattachedShip must raise FleetReorganized.
            var session = Connect();
            Assert.That(_game.Factions.TryGetValue(session.FactionId, out var faction), Is.True);

            var ship = Entity.Create(session.FactionId);
            _game.Systems[0].AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("Fresh Hull", session.FactionId, "Fresh Hull"),
            });

            var received = new List<GameEventEnvelope>();
            using (_server.Subscribe(session, received.Add))
            {
                received.Clear();
                FleetHierarchy.AttachUnattachedShip(faction, ship);

                // Publish is async; allow the FleetReorganized → FleetsChanged bridge to land.
                await Task.Delay(50);

                var push = received.LastOrDefault(e => e.Type == GameEventType.FleetsChanged);
                Assert.That(push, Is.Not.Null, "expected a FleetsChanged push after AttachUnattachedShip");
                Assert.That(push!.UnattachedShips!.Any(s => s.Id == ship.Id), Is.True);
            }

            var (_, unattached) = _projector.ProjectFleetHierarchy(session.FactionId);
            Assert.That(unattached.Any(s => s.Id == ship.Id), Is.True);
        }
    }
}
