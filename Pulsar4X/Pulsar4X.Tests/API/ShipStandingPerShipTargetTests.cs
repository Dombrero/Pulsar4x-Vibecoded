using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class ShipStandingPerShipTargetTests : ApiTestBase
    {
        private static void InstallGeoStanding(Entity fleet)
        {
            var surveyActions = new SafeList<EntityCommand>
            {
                MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var surveyCond = new CompoundCondition();
            surveyCond.ConditionItems.Add(new ConditionItem(
                new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan)));
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(surveyCond, surveyActions)
            {
                Name = "geo survey",
            });
        }

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
            InstallGeoStanding(fleet);

            Assert.That(ShipStandingEvaluator.CountEligibleGeo(ship1), Is.GreaterThanOrEqualTo(2),
                "Test bodies must be eligible geo targets.");
            Assert.That(
                ShipStandingEvaluator.FindFirstMatchingOrderIndex(ship1, fleetDb, false),
                Is.EqualTo(0),
                "Geo standing must ENTER for survey-capable ship.");

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(ShipStandingDirector.GetOrAddState(ship1).ActiveStandingOrderIndex, Is.EqualTo(0));
            Assert.That(ShipStandingDirector.GetOrAddState(ship2).ActiveStandingOrderIndex, Is.EqualTo(0));

            var geoOrders = new List<(int shipId, int targetId)>();
            foreach (var ship in new[] { ship1, ship2 })
            {
                foreach (var geo in ship.GetDataBlob<OrderableDB>().ActionList.OfType<GeoSurveyOrder>())
                    geoOrders.Add((ship.Id, geo.Target.Id));
            }

            var targets = new HashSet<int>();
            foreach (var g in geoOrders)
                targets.Add(g.targetId);
            foreach (var ship in new[] { ship1, ship2 })
            {
                foreach (var warp in ship.GetDataBlob<OrderableDB>().ActionList.OfType<WarpMoveCommand>())
                    targets.Add(warp.TargetEntityGuid);
            }

            Assert.That(geoOrders.Count, Is.GreaterThanOrEqualTo(2),
                "Each ship must receive its own GeoSurveyOrder from the standing template. "
                + $"geoOrders={geoOrders.Count}");
            Assert.That(targets.Count, Is.GreaterThanOrEqualTo(2),
                "Per-ship nearest must assign BodyA and BodyB independently. "
                + $"targets=[{string.Join(",", targets)}]");
        }

        [Test]
        public void Low_fuel_ship_refuels_while_sibling_keeps_surveying()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;
            if (!data.CargoGoods.Contains("hydrolox") && data.LockedCargoGoods.Contains("hydrolox"))
                data.Unlock("hydrolox");
            var fuel = data.CargoGoods.GetAny("hydrolox")
                       ?? data.CargoGoods.GetAny(data.LockedCargoGoods.GetMaterialsList().First().UniqueID)!;
            if (!data.CargoGoods.Contains(fuel.UniqueID))
                data.Unlock(fuel.UniqueID);

            var earthPos = new Vector3(1.5e11, 0, 0);
            var earth = Entity.Create();
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("Earth", session.FactionId, "Earth"),
                new PositionDB(earthPos, star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
            });

            var colonyStore = new CargoStorageDB(fuel.CargoTypeID, 1e12)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };
            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), earth),
                colonyStore,
                new PositionDB(earthPos, earth),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("HQ", session.FactionId, "HQ"),
                new OrderableDB(),
            });
            colonyStore.AddCargoByUnit(fuel, 50_000_000);

            var body = Entity.Create();
            system.AddEntity(body, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6e23, 3.4e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            Entity MakeShip(string name, Vector3 absPos, long fuelUnits)
            {
                var store = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
                {
                    TransferRate = 5000,
                    TransferRangeDv_mps = 1e12,
                };
                if (fuelUnits > 0)
                    store.AddCargoByUnit(fuel, fuelUnits);

                var ship = Entity.Create(session.FactionId);
                system.AddEntity(ship, new List<BaseDataBlob>
                {
                    store,
                    new PositionDB(absPos, star),
                    new MassVolumeDB { MassDry = 10000 },
                    new NameDB(name, session.FactionId, name),
                    new OrderableDB(),
                    new ShipInfoDB(),
                    new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID },
                    new GeoSurveyAbilityDB { Speed = 50 },
                });
                return ship;
            }

            var thirsty = MakeShip("Thirsty", earthPos + new Vector3(1e7, 0, 0), fuelUnits: 0);
            thirsty.GetDataBlob<PositionDB>().SetParent(colony);
            var surveyor = MakeShip("Surveyor", new Vector3(2.29e11, 0, 0), fuelUnits: 0);
            // Fill after create so volume capacity matches AddCargoByUnit.
            {
                var store = surveyor.GetDataBlob<CargoStorageDB>();
                long free = CargoMath.GetFreeUnitSpace(store, fuel, includeEscro: false);
                store.AddCargoByUnit(fuel, Math.Max(1, (long)(free * 0.9)));
            }
            Assert.That(surveyor.GetFuelPercent(
                    _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data.CargoGoods),
                Is.GreaterThan(50));

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Split Fleet", session.FactionId, "Split Fleet"),
                new PositionDB(earthPos, star),
            });
            fleetDb.FlagShipID = surveyor.Id;
            fleetDb.AddChild(thirsty);
            fleetDb.AddChild(surveyor);

            var refuelActions = new SafeList<EntityCommand>
            {
                RefuelAction.CreateCommand(session.FactionId, fleet),
            };
            var refuelCond = new CompoundCondition();
            refuelCond.ConditionItems.Add(new ConditionItem(new FuelCondition(30f, ComparisonType.LessThan)));
            fleetDb.StandingOrders.Add(new ConditionalOrder(refuelCond, refuelActions) { Name = "refuel" });
            InstallGeoStanding(fleet);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(ShipStandingDirector.GetOrAddState(thirsty).ActiveStandingOrderIndex, Is.EqualTo(0),
                "Empty ship must commit to Refuel independently.");
            Assert.That(ShipStandingDirector.GetOrAddState(surveyor).ActiveStandingOrderIndex, Is.EqualTo(1),
                "Fueled sibling must keep Geo Survey.");
            Assert.That(
                thirsty.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>().Any()
                || thirsty.HasDataBlob<CargoTransferDB>(),
                Is.True,
                "Thirsty ship must start colony transfer.");
            Assert.That(
                surveyor.GetDataBlob<OrderableDB>().ActionList.OfType<GeoSurveyOrder>().Any(),
                Is.True,
                "Survey sibling must keep surveying.");
        }

        [Test]
        public void Issued_fleet_order_blocks_standing_until_cleared()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var body = Entity.Create();
            system.AddEntity(body, new List<BaseDataBlob>
            {
                new NameDB("Body", session.FactionId, "Body"),
                new PositionDB(new Vector3(1.0e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(1e23, 1e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new PositionDB(new Vector3(1.01e11, 0, 0), star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("S", session.FactionId, "S"),
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
                new NameDB("F", session.FactionId, "F"),
                new PositionDB(new Vector3(1.01e11, 0, 0), star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);
            InstallGeoStanding(fleet);

            var issued = MoveToNearestGeoSurveyAction.CreateCommand(session.FactionId, fleet);
            issued.Source = OrderSource.Issued;
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(issued);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.Count, Is.EqualTo(0),
                "Standing must not run while Issued fleet work is queued.");

            fleet.GetDataBlob<OrderableDB>().ActionList.Clear();
            new FleetOrderProcessor().ProcessEntity(fleet, 0);
            Assert.That(
                ship.GetDataBlob<OrderableDB>().ActionList.OfType<GeoSurveyOrder>().Any(),
                Is.True,
                "After Issued clears, ship resumes standing independently.");
        }
    }
}
