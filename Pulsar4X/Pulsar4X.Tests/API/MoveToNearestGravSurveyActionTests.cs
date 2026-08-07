using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class MoveToNearestGravSurveyActionTests : ApiTestBase
    {
        [Test]
        public void Starts_survey_on_nearest_unsurveyed_anomaly_when_already_in_range()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var origin = star.GetDataBlob<PositionDB>().AbsolutePosition;

            var near = Entity.Create();
            system.AddEntity(near, new List<BaseDataBlob>
            {
                new NameDB("Near Anomaly", session.FactionId, "Near Anomaly"),
                new PositionDB(origin + new Vector3(50_000, 0, 0), star) { MoveType = PositionDB.MoveTypes.None },
                MassVolumeDB.NewFromMassAndRadius_m(1, 1),
                new JPSurveyableDB(400, new SafeDictionary<int, uint>(), 10_000_000),
            });

            var far = Entity.Create();
            system.AddEntity(far, new List<BaseDataBlob>
            {
                new NameDB("Far Anomaly", session.FactionId, "Far Anomaly"),
                new PositionDB(origin + new Vector3(5e11, 0, 0), star) { MoveType = PositionDB.MoveTypes.None },
                MassVolumeDB.NewFromMassAndRadius_m(1, 1),
                new JPSurveyableDB(400, new SafeDictionary<int, uint>(), 10_000_000),
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(origin, star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Grav Surveyor 1", session.FactionId, "Grav Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
                new JPSurveyAbilityDB { Speed = 50 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Grav Survey Fleet", session.FactionId, "Grav Survey Fleet"),
                new PositionDB(origin, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var action = MoveToNearestGravSurveyAction.CreateCommand(session.FactionId, fleet);
            Assert.That(QueueOrder(action), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);

            Assert.That(action.Name, Does.Contain("Near Anomaly").IgnoreCase,
                "Must target the nearer anomaly, not the far one.");
            Assert.That(ship.HasDataBlob<JPSurveyDB>(), Is.True,
                "Already in survey range — must start surveying in place.");
            Assert.That(ship.GetDataBlob<JPSurveyDB>().TargetId, Is.EqualTo(near.Id));
            Assert.That(action.GetIsFinished, Is.False);
            Assert.That(
                fleet.GetDataBlob<OrderableDB>().ActionList.OfType<MoveToNearestGravSurveyAction>().Any(),
                Is.True);
        }

        [Test]
        public void Queues_warp_toward_nearest_anomaly_when_out_of_range()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var origin = star.GetDataBlob<PositionDB>().AbsolutePosition;

            var data = _game.Factions[session.FactionId].GetDataBlob<Pulsar4X.Factions.FactionInfoDB>().Data;
            var fuel = data.CargoGoods.GetAll().Values.Concat(data.LockedCargoGoods.GetAll().Values).First();
            if (data.LockedCargoGoods.Contains(fuel.UniqueID))
                data.Unlock(fuel.UniqueID);
            fuel = data.CargoGoods.GetAny(fuel.UniqueID)!;

            var anomaly = Entity.Create();
            system.AddEntity(anomaly, new List<BaseDataBlob>
            {
                new NameDB("Distant Anomaly", session.FactionId, "Distant Anomaly"),
                new PositionDB(origin + new Vector3(2e11, 0, 0), star) { MoveType = PositionDB.MoveTypes.None },
                MassVolumeDB.NewFromMassAndRadius_m(1, 1),
                new JPSurveyableDB(400, new SafeDictionary<int, uint>(), 10_000_000),
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(origin, star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Grav Surveyor 1", session.FactionId, "Grav Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID, BubbleCreationCost = 1 },
                new Pulsar4X.Energy.EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID] = 1e9 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID] = 1e9 },
                },
                new JPSurveyAbilityDB { Speed = 50 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Grav Survey Fleet", session.FactionId, "Grav Survey Fleet"),
                new PositionDB(origin, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var action = MoveToNearestGravSurveyAction.CreateCommand(session.FactionId, fleet);
            Assert.That(QueueOrder(action), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);

            Assert.That(action.Name, Does.Contain("en route").IgnoreCase);
            Assert.That(ship.HasDataBlob<JPSurveyDB>(), Is.False,
                "Must not start surveying remotely.");
            Assert.That(
                ship.GetDataBlob<OrderableDB>().ActionList.OfType<WarpMoveCommand>().Any()
                || ship.HasDataBlob<WarpMovingDB>(),
                Is.True,
                "Ship must warp toward the anomaly. Orders: "
                + string.Join(", ", ship.GetDataBlob<OrderableDB>().ActionList.Select(o => o.Name)));
        }
    }
}
