using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
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

namespace Pulsar4X.Tests
{
    /// <summary>
    /// End-to-end + edge-case runtime coverage for standing survey ↔ refuel.
    /// Guards the stuck states from live logs: fuel=0% + "still affordable", SOI dock rate 0,
    /// grav hover re-billing, and opportunity top-off.
    /// </summary>
    [TestFixture]
    public class StandingSurveyRefuelRuntimeTests : ApiTestBase
    {
        private ICargoable UnlockFuel(PlayerSession session)
        {
            var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;
            foreach (var id in new[] { "hydrolox", "rp-1", "methalox" })
            {
                if (data.CargoGoods.Contains(id))
                    return data.CargoGoods.GetAny(id)!;
                if (data.LockedCargoGoods.Contains(id))
                {
                    data.Unlock(id);
                    return data.CargoGoods.GetAny(id)!;
                }
            }

            var material = data.LockedCargoGoods.GetMaterialsList().First();
            data.Unlock(material.UniqueID);
            return data.CargoGoods.GetAny(material.UniqueID)!;
        }

        private (Entity colony, Entity earth, Entity fleet, Entity ship, ICargoable fuel) MakeFleetAtEarth(
            PlayerSession session,
            long shipFuelUnits,
            bool starParentNearEarth = false)
        {
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var fuel = UnlockFuel(session);
            var earthPos = new Vector3(1.5e11, 0, 0);

            var earth = Entity.Create();
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("EarthBody", session.FactionId, "EarthBody"),
                new PositionDB(earthPos, star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
            });

            var colonyStorage = new CargoStorageDB(fuel.CargoTypeID, 1e12)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };
            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), earth),
                colonyStorage,
                new PositionDB(earthPos, earth),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("Earth HQ", session.FactionId, "Earth HQ"),
                new OrderableDB(),
            });
            colonyStorage.AddCargoByUnit(fuel, 50_000_000);

            var shipStorage = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };
            if (shipFuelUnits > 0)
                shipStorage.AddCargoByUnit(fuel, shipFuelUnits);

            Entity posParent = starParentNearEarth ? star : earth;
            Vector3 shipAbs = earthPos + new Vector3(8e6, 0, 0);

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                shipStorage,
                new PositionDB(shipAbs, posParent) { MoveType = PositionDB.MoveTypes.Warp },
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB
                {
                    MaxSpeed = 1e9,
                    EnergyType = fuel.UniqueID,
                    BubbleCreationCost = 1,
                    BubbleSustainCost = 0,
                },
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    MaxOutputFromReactor = 1000,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                },
            });
            if (starParentNearEarth)
                ship.GetDataBlob<PositionDB>().AbsolutePosition = shipAbs;

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Science Fleet", session.FactionId, "Science Fleet"),
                new OrderableDB(),
                new PositionDB(shipAbs, posParent),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            return (colony, earth, fleet, ship, fuel);
        }

        private static void InstallRefuelThenGeoSurvey(Entity fleet)
        {
            var refuelActions = new SafeList<EntityCommand>
            {
                RefuelAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var refuelCond = new CompoundCondition();
            refuelCond.ConditionItems.Add(new ConditionItem(new FuelCondition(30f, ComparisonType.LessThan)));
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(refuelCond, refuelActions)
            {
                Name = "refuel",
            });

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

        private static bool IsRefuelWork(EntityCommand order)
            => order is RefuelAction
               || order is RefuelWhenAtColonyOrder
               || order is WarpFleetTowardsTargetOrder;

        private void PumpStandingAndTime(Entity fleet, int steps, TimeSpan tick)
        {
            _game.Settings.EnforceSingleThread = true;
            _game.TimePulse.Ticklength = tick;
            var standing = new FleetOrderProcessor();

            for (int i = 0; i < steps; i++)
            {
                standing.ProcessEntity(fleet, 0);
                // Full engine pulse: OrderableProcessor + CargoTransferProcessor hotloops.
                _game.TimePulse.TimeStep();
            }
        }

        [Test]
        public void Edge_empty_tanks_parented_to_unfinished_geo_must_preempt_refuel()
        {
            // Live log: defer "still affordable; fuel=0%" while warp blocked empty.
            var session = Connect();
            var (colony, earth, fleet, ship, fuel) = MakeFleetAtEarth(session, shipFuelUnits: 0);

            var mars = Entity.Create();
            _game.Systems[0].AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>()),
                MassVolumeDB.NewFromMassAndRadius_m(6e23, 3.4e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            // Stuck near an unfinished body without an active GeoSurveyingDB (next hop wants Mars).
            ship.GetDataBlob<PositionDB>().SetParent(earth);
            earth.SetDataBlob(new GeoSurveyableDB { PointsRequired = 500 });

            InstallRefuelThenGeoSurvey(fleet);
            fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex = 1;
            var running = MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet);
            running.Source = OrderSource.Standing;
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(running);

            Assert.That(FleetFuel.AveragePercent(fleet), Is.EqualTo(0).Within(0.1));
            Assert.That(WarpMoveProcessor.HasWarpTankFuel(ship), Is.False);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders.OfType<MoveToNearestGeoSurveyAction>().Any(), Is.False,
                "Empty tanks must not keep survey when next hop needs warp.");
            Assert.That(orders.Any(IsRefuelWork), Is.True,
                "Refuel must preempt — matches fuel=0% log stuck state.");
            Assert.That(fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex, Is.EqualTo(0));
            Assert.That(colony.IsValid, Is.True);
        }

        [Test]
        public void Edge_active_geo_scan_with_empty_tanks_may_finish_before_refuel()
        {
            var session = Connect();
            var (_, earth, fleet, ship, _) = MakeFleetAtEarth(session, shipFuelUnits: 0);

            earth.SetDataBlob(new GeoSurveyableDB { PointsRequired = 500 });
            ship.GetDataBlob<PositionDB>().SetParent(earth);
            ship.SetDataBlob(new GeoSurveyingDB { TargetId = earth.Id });

            InstallRefuelThenGeoSurvey(fleet);
            fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex = 1;
            var running = MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet);
            running.Source = OrderSource.Standing;
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(running);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.OfType<MoveToNearestGeoSurveyAction>().Any(), Is.True,
                "Active local GeoSurveyingDB still finishes before tank fill.");
            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.Any(IsRefuelWork), Is.False);
        }

        [Test]
        public void Runtime_soi_docked_empty_ship_refuels_to_full_then_can_leave()
        {
            var session = Connect();
            var (colony, _, fleet, ship, fuel) = MakeFleetAtEarth(
                session, shipFuelUnits: 0, starParentNearEarth: true);

            Assert.That(FleetOrderCleanup.IsShipAtColony(ship, colony), Is.True);
            Assert.That(CargoTransferProcessor.CalcDVDifference_m(ship, colony), Is.EqualTo(0).Within(1e-6));

            InstallRefuelThenGeoSurvey(fleet);

            PumpStandingAndTime(fleet, steps: 40, tick: TimeSpan.FromMinutes(5));
            double midPct = FleetFuel.AveragePercent(fleet);
            long midStored = ship.GetDataBlob<CargoStorageDB>().GetUnitsStored(fuel, includeEscro: false);
            Assert.That(midStored, Is.GreaterThan(0), "SOI-docked refuel must move mass (not rate=0 stuck)");
            Assert.That(midPct, Is.GreaterThan(0));

            PumpStandingAndTime(fleet, steps: 80, tick: TimeSpan.FromMinutes(5));
            double laterPct = FleetFuel.AveragePercent(fleet);
            Assert.That(laterPct, Is.GreaterThan(midPct).Or.EqualTo(100).Within(0.5),
                $"Transfer must keep filling or finish (mid={midPct:0.#} later={laterPct:0.#})");
        }

        [Test]
        public void Runtime_opportunity_top_off_at_colony_before_survey_restart()
        {
            var session = Connect();
            var (colony, _, fleet, ship, fuel) = MakeFleetAtEarth(session, shipFuelUnits: 0);
            ship.GetDataBlob<PositionDB>().SetParent(colony);

            // Fill to ~55% of real unit capacity (volume/unit can shrink nominal 2M tanks).
            var store = ship.GetDataBlob<CargoStorageDB>();
            long free0 = CargoMath.GetFreeUnitSpace(store, fuel, includeEscro: false);
            long fill = Math.Max(1, (long)(free0 * 0.55));
            store.AddCargoByUnit(fuel, fill);

            var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
            _game.Systems[0].AddEntity(Entity.Create(), new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6e23, 3.4e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            InstallRefuelThenGeoSurvey(fleet);
            fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex = 1;
            var running = MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet);
            running.Source = OrderSource.Standing;
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(running);

            Assert.That(FleetFuel.AnyBelow(fleet, 30f), Is.False, $"fuel%={FleetFuel.AveragePercent(fleet):0.#}");
            Assert.That(FleetFuel.HasOpportunityTopOff(fleet), Is.True);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.Any(IsRefuelWork), Is.True,
                "Docked with free tank space must top off before next survey hop.");
            Assert.That(fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex, Is.EqualTo(0));
        }

        // Grav hover one-bill coverage lives in WarpAnomalyParentTests.Grav_anomaly_hover_bills_tank_fuel_only_once.
    }
}
