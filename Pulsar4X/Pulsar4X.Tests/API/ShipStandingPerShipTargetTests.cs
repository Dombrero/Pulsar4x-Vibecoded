using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class ShipStandingPerShipTargetTests : ApiTestBase
    {
        [Test]
        public void Two_survey_ships_get_different_nearest_geo_targets()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var bodyA = Entity.Create();
            system.AddEntity(bodyA, new List<BaseDataBlob>
            {
                new NameDB("BodyA", session.FactionId, "BodyA"),
                new PositionDB(new Vector3(1.0e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(1e23, 1e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 1e23,
                    1.0e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var bodyB = Entity.Create();
            system.AddEntity(bodyB, new List<BaseDataBlob>
            {
                new NameDB("BodyB", session.FactionId, "BodyB"),
                new PositionDB(new Vector3(3.0e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(1e23, 1e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 1e23,
                    3.0e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            Entity MakeShip(string name, Vector3 absPos)
            {
                var ship = Entity.Create(session.FactionId);
                system.AddEntity(ship, new List<BaseDataBlob>
                {
                    new PositionDB(absPos, star),
                    new MassVolumeDB { MassDry = 10000 },
                    new NameDB(name, session.FactionId, name),
                    new OrderableDB(),
                    new ShipInfoDB(),
                    new WarpAbilityDB { MaxSpeed = 1e8 },
                    new GeoSurveyAbilityDB { Speed = 50 },
                });
                return ship;
            }

            // Ship1 near BodyA, Ship2 near BodyB — flagship-shared targeting would send both to A.
            var ship1 = MakeShip("Surveyor A", new Vector3(1.01e11, 0, 0));
            var ship2 = MakeShip("Surveyor B", new Vector3(2.99e11, 0, 0));

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(new Vector3(1.01e11, 0, 0), star),
            });
            fleetDb.FlagShipID = ship1.Id;
            fleetDb.AddChild(ship1);
            fleetDb.AddChild(ship2);

            var action = MoveToNearestGeoSurveyAction.CreateCommand(session.FactionId, fleet);
            Assert.That(QueueOrder(action), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);
            // Ship queues receive GeoSurveyOrder via inbox drain during Standing enqueue.
            processor.ProcessEntity(ship1, 0);
            processor.ProcessEntity(ship2, 0);

            var geoOrders = new List<(int shipId, int targetId)>();
            foreach (var ship in new[] { ship1, ship2 })
            {
                foreach (var geo in ship.GetDataBlob<OrderableDB>().ActionList.OfType<GeoSurveyOrder>())
                    geoOrders.Add((ship.Id, geo.Target.Id));
            }

            var warps = new List<(int shipId, int targetId)>();
            foreach (var ship in new[] { ship1, ship2 })
            {
                foreach (var warp in ship.GetDataBlob<OrderableDB>().ActionList.OfType<WarpMoveCommand>())
                    warps.Add((ship.Id, warp.TargetEntityGuid));
            }

            // At least one ship should be surveying/warping; if already "at" via parent, check surveying DB.
            var targets = new HashSet<int>();
            if (ship1.TryGetDataBlob<GeoSurveyingDB>(out var s1))
                targets.Add(s1.TargetId);
            if (ship2.TryGetDataBlob<GeoSurveyingDB>(out var s2))
                targets.Add(s2.TargetId);
            foreach (var g in geoOrders)
                targets.Add(g.targetId);
            foreach (var w in warps)
                targets.Add(w.targetId);

            Assert.That(geoOrders.Count, Is.GreaterThanOrEqualTo(2),
                "Each ship must receive its own GeoSurveyOrder on the ship ActionList (visible progress). "
                + $"geoOrders={geoOrders.Count} warps={warps.Count}");
            Assert.That(targets.Count, Is.GreaterThanOrEqualTo(2),
                "Per-ship nearest must assign BodyA and BodyB, not one shared flagship target. "
                + $"targets=[{string.Join(",", targets)}] geo={geoOrders.Count} warps={warps.Count}");
        }
    }
}
