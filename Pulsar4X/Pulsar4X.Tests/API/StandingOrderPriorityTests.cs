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
    [TestFixture]
    public class StandingOrderPriorityTests : ApiTestBase
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

        private (Entity colony, Entity fleet, Entity ship) MakeLowFuelFleet(PlayerSession session)
        {
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var fuel = UnlockFuel(session);

            var earth = Entity.Create();
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("EarthBody", session.FactionId, "EarthBody"),
                new PositionDB(new Vector3(1.5e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
            });

            var colonyStorage = new CargoStorageDB(fuel.CargoTypeID, 1e12)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), earth),
                colonyStorage,
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("Earth HQ", session.FactionId, "Earth HQ"),
                new OrderableDB(),
            });
            colonyStorage.AddCargoByUnit(fuel, 1_000_000);

            var shipStorage = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            shipStorage.AddCargoByUnit(fuel, 340_000);

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                shipStorage,
                new PositionDB(new Vector3(3.8e11, 0, 0), star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID },
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                },
            });

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Science Fleet", session.FactionId, "Science Fleet"),
                new OrderableDB(),
                new PositionDB(new Vector3(3.8e11, 0, 0), star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            return (colony, fleet, ship);
        }

        private static void InstallRefuelStandingOrder(Entity fleet)
        {
            // Legacy player template: Move + Refuel + Resupply — processor must normalize to Refuel only.
            var actions = new SafeList<EntityCommand>
            {
                MoveToNearestColonyAction.CreateCommand(fleet.FactionOwnerID, fleet),
                RefuelAction.CreateCommand(fleet.FactionOwnerID, fleet),
                ResupplyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var condition = new CompoundCondition();
            condition.ConditionItems.Add(new ConditionItem(new FuelCondition(30f, ComparisonType.LessThan)));
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(condition, actions)
            {
                Name = "refuel",
            });
        }

        [Test]
        public void NormalizeStandingActions_drops_resupply_and_redundant_move()
        {
            var session = Connect();
            var (_, fleet, _) = MakeLowFuelFleet(session);
            var raw = new List<EntityCommand>
            {
                MoveToNearestColonyAction.CreateCommand(fleet.FactionOwnerID, fleet),
                RefuelAction.CreateCommand(fleet.FactionOwnerID, fleet),
                ResupplyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };

            var normalized = FleetOrderProcessor.NormalizeStandingActions(raw);

            Assert.That(normalized, Has.Count.EqualTo(1));
            Assert.That(normalized[0], Is.TypeOf<RefuelAction>());
        }

        private static bool IsRefuelStandingWork(EntityCommand order)
            => order is RefuelAction
               || order is WarpFleetTowardsTargetOrder
               || order is RefuelWhenAtColonyOrder
               || order is MoveToNearestColonyAction;

        [Test]
        public void Standing_refuel_does_not_loop_while_fuel_transfer_is_active()
        {
            var session = Connect();
            var (colony, fleet, ship) = MakeLowFuelFleet(session);
            // Park at the colony planet so a transfer is in range.
            ship.GetDataBlob<PositionDB>().SetParent(colony.GetDataBlob<ColonyInfoDB>().PlanetEntity);
            InstallRefuelStandingOrder(fleet);

            var processor = new FleetOrderProcessor();
            processor.ProcessEntity(fleet, 0);

            // RefuelAction executes immediately via OrderableProcessor and starts ship transfers.
            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.OfType<ResupplyAction>().Any(), Is.False);
            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.OfType<MoveToNearestColonyAction>().Any(), Is.False);
            Assert.That(
                ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>().Any()
                || ship.HasDataBlob<CargoTransferDB>()
                || fleet.GetDataBlob<OrderableDB>().ActionList.Any(IsRefuelStandingWork),
                Is.True,
                "Refuel standing response must start a transfer or leave follow-up work.");

            // Clear fleet queue; leave ship transfer as the only in-progress signal.
            fleet.GetDataBlob<OrderableDB>().ActionList.Clear();
            if (!ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>().Any()
                && !ship.HasDataBlob<CargoTransferDB>())
            {
                Assert.That(CargoTransferOrder.CreateRefuelFleetCommand(colony, fleet), Is.True);
            }

            for (int i = 0; i < 20; i++)
                processor.ProcessEntity(fleet, 0);

            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList, Is.Empty,
                "While a fuel transfer runs, standing Refuel must not re-enqueue (loop).");
        }

        [Test]
        public void Standing_refuel_commitment_blocks_survey_until_fuel_exit_band()
        {
            var session = Connect();
            var (colony, fleet, ship) = MakeLowFuelFleet(session);
            ship.GetDataBlob<PositionDB>().SetParent(colony.GetDataBlob<ColonyInfoDB>().PlanetEntity);

            InstallRefuelStandingOrder(fleet);

            var surveyActions = new SafeList<EntityCommand>
            {
                MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var surveyCondition = new CompoundCondition();
            surveyCondition.ConditionItems.Add(new ConditionItem(
                new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan)));
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(surveyCondition, surveyActions)
            {
                Name = "survey",
            });

            var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
            _game.Systems[0].AddEntity(Entity.Create(), new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                new MassVolumeDB { MassDry = 6e23 },
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var processor = new FleetOrderProcessor();
            processor.ProcessEntity(fleet, 0);
            Assert.That(fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex, Is.EqualTo(0));

            // Simulate: transfer done early, tanks only ~35% (above ENTER 30, still free space).
            fleet.GetDataBlob<OrderableDB>().ActionList.Clear();
            var fuel = UnlockFuel(session);
            // Capacity 2_000_000; add up to ~35%.
            long have = ship.GetDataBlob<CargoStorageDB>().GetUnitsStored(fuel, includeEscro: false);
            long want = 700_000;
            if (want > have)
                ship.GetDataBlob<CargoStorageDB>().AddCargoByUnit(fuel, want - have);

            for (int i = 0; i < 10; i++)
                processor.ProcessEntity(fleet, 0);

            Assert.That(fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex, Is.EqualTo(0),
                "Refuel commitment must hold until tanks are completely full.");
            Assert.That(
                fleet.GetDataBlob<OrderableDB>().ActionList.OfType<MoveToNearestGeoSurveyAction>().Any(),
                Is.False,
                "Survey must not start while tanks are only partially filled.");
            Assert.That(
                fleet.GetDataBlob<OrderableDB>().ActionList.Any(IsRefuelStandingWork),
                Is.True,
                "Refuel should restart until every fuel tank is full.");
        }

        [Test]
        public void Standing_order_does_not_preempt_issued_geo_survey()
        {
            var session = Connect();
            var (_, fleet, _) = MakeLowFuelFleet(session);
            InstallRefuelStandingOrder(fleet);

            var luna = Entity.Create();
            _game.Systems[0].AddEntity(luna, new List<BaseDataBlob>
            {
                new NameDB("Luna", session.FactionId, "Luna"),
                new PositionDB(new Vector3(3.8e11, 0, 0), _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>()),
                new MassVolumeDB { MassDry = 7e22 },
                new GeoSurveyableDB { PointsRequired = 1000 },
            });

            var survey = GeoSurveyOrder.CreateCommand(session.FactionId, fleet, luna);
            Assert.That(survey.Source, Is.EqualTo(OrderSource.Issued));
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(survey);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders, Has.Count.EqualTo(1));
            Assert.That(orders[0], Is.TypeOf<GeoSurveyOrder>(),
                "Issue Orders outrank Standing — standing must not insert while Issued work is queued.");
        }

        [Test]
        public void Standing_order_fires_when_issued_queue_is_empty()
        {
            var session = Connect();
            var (_, fleet, _) = MakeLowFuelFleet(session);
            InstallRefuelStandingOrder(fleet);

            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList, Is.Empty);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders.All(o => o.Source == OrderSource.Standing), Is.True);
            Assert.That(orders.Any(IsRefuelStandingWork), Is.True,
                "Standing Refuel must enqueue Refuel and/or its warp/arrive follow-ups.");
            Assert.That(orders.OfType<ResupplyAction>().Any(), Is.False);
            Assert.That(orders.OfType<MoveToNearestColonyAction>().Any(), Is.False);
        }

        [Test]
        public void Standing_order_does_not_spam_when_already_handling_refuel()
        {
            var session = Connect();
            var (_, fleet, _) = MakeLowFuelFleet(session);
            InstallRefuelStandingOrder(fleet);

            var processor = new FleetOrderProcessor();
            processor.ProcessEntity(fleet, 0);
            int afterFirst = fleet.GetDataBlob<OrderableDB>().ActionList.Count;
            Assert.That(afterFirst, Is.GreaterThan(0));

            for (int i = 0; i < 50; i++)
                processor.ProcessEntity(fleet, 0);

            int afterSpam = fleet.GetDataBlob<OrderableDB>().ActionList.Count;
            Assert.That(afterSpam, Is.EqualTo(afterFirst),
                "Re-processing while standing response is in progress must not stack more orders.");
        }

        [Test]
        public void Higher_priority_refuel_preempts_standing_survey()
        {
            var session = Connect();
            var (_, fleet, ship) = MakeLowFuelFleet(session);

            // Drain tanks so any survey hop is unaffordable (fuel still <30% ENTER).
            var fuel = UnlockFuel(session);
            var store = ship.GetDataBlob<CargoStorageDB>();
            long stored = store.GetUnitsStored(fuel, includeEscro: false);
            if (stored > 1000)
                CargoTransferProcessor.AddRemoveCargoMass(ship, fuel, -(stored - 1000) * fuel.MassPerUnit);

            // Also make warp capacitors unable to cover bubble creation (no generator catch-up).
            ship.GetDataBlob<WarpAbilityDB>().BubbleCreationCost = 1_000_000;
            var power = ship.GetDataBlob<EnergyGenAbilityDB>();
            power.MaxOutputFromReactor = 0;
            power.EnergyStored[fuel.UniqueID] = 100;
            power.EnergyStoreMax[fuel.UniqueID] = 500_000;

            var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
            _game.Systems[0].AddEntity(Entity.Create(), new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                new MassVolumeDB { MassDry = 6e23 },
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var surveyActions = new SafeList<EntityCommand>
            {
                MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var surveyCondition = new CompoundCondition();
            surveyCondition.ConditionItems.Add(new ConditionItem(
                new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan)));

            // Refuel first (higher priority), survey second.
            InstallRefuelStandingOrder(fleet);
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(surveyCondition, surveyActions)
            {
                Name = "survey",
            });

            // Survey already running as standing work while fuel is low (~17%).
            var runningSurvey = MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet);
            runningSurvey.Source = OrderSource.Standing;
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(runningSurvey);
            fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex = 1;

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders.OfType<MoveToNearestGeoSurveyAction>().Any(), Is.False,
                "Unaffordable next survey hop must allow refuel preempt.");
            Assert.That(orders.Any(IsRefuelStandingWork), Is.True,
                "Refuel standing order should replace the survey queue.");
            Assert.That(orders.OfType<ResupplyAction>().Any(), Is.False);
        }

        [Test]
        public void Refuel_does_not_preempt_on_site_survey_when_local_action_is_free()
        {
            var session = Connect();
            var (_, fleet, ship) = MakeLowFuelFleet(session);

            var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
            var mars = Entity.Create();
            _game.Systems[0].AddEntity(mars, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6e23, 3.4e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            // Already on-station — next action is local scan (0 fuel / 0 warp energy).
            ship.GetDataBlob<PositionDB>().SetParent(mars);
            ship.SetDataBlob(new GeoSurveyingDB { TargetId = mars.Id });

            var surveyActions = new SafeList<EntityCommand>
            {
                MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var surveyCondition = new CompoundCondition();
            surveyCondition.ConditionItems.Add(new ConditionItem(
                new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan)));

            InstallRefuelStandingOrder(fleet);
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(surveyCondition, surveyActions)
            {
                Name = "survey",
            });
            fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex = 1;

            var runningSurvey = MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet);
            runningSurvey.Source = OrderSource.Standing;
            fleet.GetDataBlob<OrderableDB>().ActionList.Add(runningSurvey);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders.OfType<MoveToNearestGeoSurveyAction>().Any(), Is.True,
                "On-site survey must finish before refuel when the local action is free.");
            Assert.That(orders.Any(IsRefuelStandingWork), Is.False,
                "Refuel must not preempt an affordable on-site survey.");
            Assert.That(fleet.GetDataBlob<FleetDB>().ActiveStandingOrderIndex, Is.EqualTo(1));
        }

        [Test]
        public void Issued_order_drops_queued_standing_actions()
        {
            var session = Connect();
            var (_, fleet, _) = MakeLowFuelFleet(session);
            InstallRefuelStandingOrder(fleet);

            new FleetOrderProcessor().ProcessEntity(fleet, 0);
            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.Any(a => a.Source == OrderSource.Standing), Is.True);

            var luna = Entity.Create();
            _game.Systems[0].AddEntity(luna, new List<BaseDataBlob>
            {
                new NameDB("Luna", session.FactionId, "Luna"),
                new PositionDB(new Vector3(3.8e11, 0, 0), _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>()),
                new MassVolumeDB { MassDry = 7e22 },
                new GeoSurveyableDB { PointsRequired = 1000 },
            });

            Assert.That(QueueOrder(
                GeoSurveyOrder.CreateCommand(session.FactionId, fleet, luna)), Is.True);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders.Any(a => a.Source == OrderSource.Standing), Is.False,
                "Issuing a player order must clear standing-sourced queue entries.");
            Assert.That(orders.OfType<GeoSurveyOrder>().Count(), Is.EqualTo(1));
        }
    }
}
