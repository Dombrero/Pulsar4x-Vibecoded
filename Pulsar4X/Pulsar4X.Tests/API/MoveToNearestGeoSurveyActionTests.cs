using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
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
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class MoveToNearestGeoSurveyActionTests : ApiTestBase
    {
        [Test]
        public void Skips_colony_world_and_starts_survey_on_nearest_eligible()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var earth = Entity.Create();
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("Earth", session.FactionId, "Earth"),
                new PositionDB(new Vector3(1.5e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), earth),
                new CargoStorageDB("fuel-storage", 1e12),
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("Earth HQ", session.FactionId, "Earth HQ"),
                new OrderableDB(),
            });

            var luna = Entity.Create();
            system.AddEntity(luna, new List<BaseDataBlob>
            {
                new NameDB("Luna", session.FactionId, "Luna"),
                new PositionDB(new Vector3(1.504e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(7.3e22, 1.7e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 7.3e22,
                    1.504e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 100 },
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
                new GeoSurveyAbilityDB { Speed = 50 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            Assert.That(GeoSurveyTargets.IsEligible(earth, session.FactionId), Is.False);
            Assert.That(GeoSurveyTargets.IsEligible(luna, session.FactionId), Is.True);

            var action = MoveToNearestGeoSurveyAction.CreateCommand(session.FactionId, fleet);
            Assert.That(QueueOrder(action), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);

            Assert.That(action.Name, Does.Contain("Luna").IgnoreCase,
                "Must target Luna, not the colonised Earth.");
            Assert.That(
                fleet.GetDataBlob<OrderableDB>().ActionList.OfType<MoveToNearestGeoSurveyAction>().Any(),
                Is.True,
                "Must stay queued until the survey finishes — not complete after a move-only pass.");
            Assert.That(action.GetIsFinished, Is.False);
        }

        [Test]
        public void Already_at_eligible_body_surveys_in_place_instead_of_finishing()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var mars = Entity.Create();
            system.AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6.4e23, 3.4e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 6.4e23,
                    2.3e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 1000 },
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition, mars),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
                new GeoSurveyAbilityDB { Speed = 50 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(mars.GetDataBlob<PositionDB>().AbsolutePosition, mars),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var action = MoveToNearestGeoSurveyAction.CreateCommand(session.FactionId, fleet);
            Assert.That(QueueOrder(action), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);
            processor.ProcessEntity(fleet, (int)TimeSpan.FromDays(1).TotalSeconds);

            Assert.That(action.GetIsFinished, Is.False,
                "Being already at the target must start surveying, not finish the order.");
            Assert.That(action.Name, Does.Contain("Mars").IgnoreCase);
            Assert.That(mars.GetDataBlob<GeoSurveyableDB>().HasSurveyStarted(session.FactionId), Is.True);
        }

        [Test]
        public void Survey_aborts_blocking_cargo_transfer_and_queues_warp()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var data = _game.Factions[session.FactionId].GetDataBlob<Pulsar4X.Factions.FactionInfoDB>().Data;
            var fuel = data.CargoGoods.GetAll().Values.Concat(data.LockedCargoGoods.GetAll().Values).First();
            if (data.LockedCargoGoods.Contains(fuel.UniqueID))
                data.Unlock(fuel.UniqueID);
            fuel = data.CargoGoods.GetAny(fuel.UniqueID)!;

            var earth = Entity.Create();
            var earthPos = new PositionDB(new Vector3(1.5e11, 0, 0), star) { MoveType = PositionDB.MoveTypes.Orbit };
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("Earth", session.FactionId, "Earth"),
                earthPos,
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), earth),
                new CargoStorageDB(fuel.CargoTypeID, 1e12) { TransferRate = 100, TransferRangeDv_mps = 1e6 },
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("Earth HQ", session.FactionId, "Earth HQ"),
                new OrderableDB(),
            });
            colony.GetDataBlob<CargoStorageDB>().AddCargoByUnit(fuel, 1_000_000);

            var venus = Entity.Create();
            var venusPos = new PositionDB(new Vector3(1.0e11, 0, 0), star) { MoveType = PositionDB.MoveTypes.Orbit };
            system.AddEntity(venus, new List<BaseDataBlob>
            {
                new NameDB("Venus", session.FactionId, "Venus"),
                venusPos,
                MassVolumeDB.NewFromMassAndRadius_m(4.8e24, 6.0e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 4.8e24,
                    1.0e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
                new GeoSurveyableDB { PointsRequired = 200 },
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new CargoStorageDB(fuel.CargoTypeID, 2_000_000) { TransferRate = 100, TransferRangeDv_mps = 1e6 },
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID, BubbleCreationCost = 1 },
                new Pulsar4X.Energy.EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID] = 1e9 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID] = 1e9 },
                },
                new GeoSurveyAbilityDB { Speed = 50 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Survey Fleet", session.FactionId, "Survey Fleet"),
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            // Simulate leftover Refuel cargo transfer holding the Movement lane.
            CargoTransferOrder.CreateCommands(
                session.FactionId, ship, colony, fuel, CargoTransferOrder.Conditionals.WaitTillFull);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>().Any(), Is.True);

            var survey = GeoSurveyOrder.CreateCommand(session.FactionId, fleet, venus);
            Assert.That(QueueOrder(survey), Is.True);

            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>().Any(), Is.False,
                "Survey travel must clear cargo transfers that block the Movement lane.");
            Assert.That(
                ship.GetDataBlob<OrderableDB>().ActionList.OfType<WarpMoveCommand>().Any()
                || ship.HasDataBlob<WarpMovingDB>(),
                Is.True,
                "Ship must warp toward Venus instead of surveying remotely from Earth. Orders: "
                + string.Join(", ", ship.GetDataBlob<OrderableDB>().ActionList.Select(o => o.Name)));
            Assert.That(survey.Name, Does.Contain("en route").IgnoreCase);
        }
    }
}
