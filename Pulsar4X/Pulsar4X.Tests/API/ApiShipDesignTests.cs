using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Engine.Api;
using Pulsar4X.Factions;

namespace Pulsar4X.Tests
{
    /// <summary>The ship-design write surface: save (create/update), delete, obsolete. The
    /// interactive designer runs client-side; the server resolves the referenced component/armor
    /// ids, recalculates and validates on save.</summary>
    [TestFixture]
    public class ApiShipDesignTests : ApiTestBase
    {
        private FactionInfoDB Info(PlayerSession session)
            => _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>();

        /// <summary>Unlock the full catalogue (incl. armor) and register one component design the
        /// ship can be built from; returns (componentDesignId, armorId).</summary>
        private (string ComponentId, string ArmorId) SetUpDesignData(PlayerSession session)
        {
            var info = Info(session);
            var data = info.Data;
            foreach (var id in data.LockedComponentTemplates.Keys.ToList())
                data.Unlock(id);
            foreach (var uniqueId in data.LockedCargoGoods.GetAll().Values.Select(c => c.UniqueID).ToList())
                data.Unlock(uniqueId);
            foreach (var id in data.LockedTechs.Keys.ToList())
                data.Unlock(id);
            foreach (var id in data.Techs.Keys.ToList())
                data.IncrementTechLevel(id);
            foreach (var id in data.LockedArmor.Keys.ToList())
                data.Unlock(id);

            Assert.That(data.Armor, Is.Not.Empty, "expected armor blueprints in the mod data");
            string armorId = data.Armor.ContainsKey("plastic-armor") ? "plastic-armor" : data.Armor.Keys.First();

            // Register a component design through the same seam the client uses.
            foreach (var templateId in data.ComponentTemplates.Keys.ToList())
            {
                var result = _server.SubmitCommand(session, new CreateComponentDesignCommand(
                    session.FactionId, templateId, "Ship Part", Array.Empty<DesignerInput>()));
                if (!result.Accepted) continue;

                var component = info.ComponentDesigns.Values.First(d => d.Name == "Ship Part");
                return (component.UniqueID, armorId);
            }

            Assert.Inconclusive("no component template in the test mod data evaluates cleanly");
            return ("", "");
        }

        [Test]
        public void SaveShipDesign_creates_and_then_updates_in_place()
        {
            var session = Connect();
            var (componentId, armorId) = SetUpDesignData(session);
            var info = Info(session);

            var create = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, "Test Class",
                new[] { new ShipComponentCount(componentId, 2) }, armorId, 3, IsObsolete: false));
            Assert.That(create.Accepted, Is.True, create.RejectionReason);

            var design = info.ShipDesigns.Values.FirstOrDefault(d => d.Name == "Test Class");
            Assert.That(design, Is.Not.Null, "expected the design registered on the faction");
            Assert.That(design!.MassPerUnit, Is.GreaterThan(0), "expected recalculated mass");
            Assert.That(design.IsValid, Is.False, "a single non-propulsion component can't make a valid ship");
            Assert.That(info.IndustryDesigns.ContainsKey(design.UniqueID), Is.True,
                "a registered design is constructible by industry");

            var update = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, design.UniqueID, "Test Class II",
                new[] { new ShipComponentCount(componentId, 4) }, armorId, 5, IsObsolete: false));
            Assert.That(update.Accepted, Is.True, update.RejectionReason);

            Assert.That(info.ShipDesigns.Values.Count(d => d.Name.StartsWith("Test Class")), Is.EqualTo(1),
                "updating must edit the design in place, not duplicate it");
            var updated = info.ShipDesigns[design.UniqueID];
            Assert.That(updated.Name, Is.EqualTo("Test Class II"));
            Assert.That(updated.Components.Single().count, Is.EqualTo(4));
            Assert.That(updated.MassPerUnit, Is.GreaterThan(design.MassPerUnit / 2), "mass recalculated from new counts");
        }

        [Test]
        public void DeleteShipDesign_removes_it_and_obsolete_marks_it()
        {
            var session = Connect();
            var (componentId, armorId) = SetUpDesignData(session);
            var info = Info(session);

            _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, "Keeper", new[] { new ShipComponentCount(componentId, 1) }, armorId, 3, false));
            _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, "Goner", new[] { new ShipComponentCount(componentId, 1) }, armorId, 3, false));

            var keeper = info.ShipDesigns.Values.First(d => d.Name == "Keeper");
            var goner = info.ShipDesigns.Values.First(d => d.Name == "Goner");

            var delete = _server.SubmitCommand(session, new DeleteShipDesignCommand(session.FactionId, goner.UniqueID));
            Assert.That(delete.Accepted, Is.True, delete.RejectionReason);
            Assert.That(info.ShipDesigns.ContainsKey(goner.UniqueID), Is.False);
            Assert.That(info.IndustryDesigns.ContainsKey(goner.UniqueID), Is.False,
                "deleted designs must leave industry constructibles");

            var obsolete = _server.SubmitCommand(session, new SetShipDesignObsoleteCommand(session.FactionId, keeper.UniqueID));
            Assert.That(obsolete.Accepted, Is.True, obsolete.RejectionReason);
            Assert.That(info.ShipDesigns[keeper.UniqueID].IsObsolete, Is.True);
            Assert.That(info.ShipDesigns[keeper.UniqueID].IsValid, Is.False,
                "obsolete designs must not stay constructible in shipyards");
        }

        [Test]
        public void SaveShipDesign_rejects_bad_references()
        {
            var session = Connect();
            var (componentId, armorId) = SetUpDesignData(session);

            var noName = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, " ", Array.Empty<ShipComponentCount>(), armorId, 3, false));
            Assert.That(noName.Accepted, Is.False);

            var badArmor = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, "Name", Array.Empty<ShipComponentCount>(), "no-such-armor", 3, false));
            Assert.That(badArmor.Accepted, Is.False);

            var badComponent = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, "Name", new[] { new ShipComponentCount("no-such-component", 1) }, armorId, 3, false));
            Assert.That(badComponent.Accepted, Is.False);

            var badDesignId = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, "no-such-design", "Name", new[] { new ShipComponentCount(componentId, 1) }, armorId, 3, false));
            Assert.That(badDesignId.Accepted, Is.False);
        }

        [Test]
        public void SaveShipDesign_pushes_colony_refresh_without_waiting_for_tick()
        {
            var session = Connect();
            var (componentId, armorId) = SetUpDesignData(session);

            // Need a subscribed client and at least one colony so RefreshColonies has something to push.
            var received = new System.Collections.Generic.List<GameEventEnvelope>();
            using var _ = _server.Subscribe(session, received.Add);

            var info = Info(session);
            if (info.Colonies.Count == 0)
                Assert.Inconclusive("test universe has no colonies to refresh");

            received.Clear();
            var create = _server.SubmitCommand(session, new SaveShipDesignCommand(
                session.FactionId, null, "Yard Visible Class",
                new[] { new ShipComponentCount(componentId, 1) }, armorId, 3, false));
            Assert.That(create.Accepted, Is.True, create.RejectionReason);

            var colonyIds = info.Colonies.Select(c => c.Id).ToHashSet();
            Assert.That(received.Any(e => e.Type == GameEventType.EntityChanged
                    && e.EntityId is int id && colonyIds.Contains(id)),
                Is.True,
                "saving a ship design must push colony EntityChanged so shipyards refresh Constructibles immediately");
        }
    }
}
