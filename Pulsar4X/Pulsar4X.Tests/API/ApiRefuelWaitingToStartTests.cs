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
using Pulsar4X.Industry;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Refuel-at must not issue WaitTillFull transfers while out of range, and Clear Fleet Orders
    /// must also clear ship cargo transfers that lock the Movement lane.
    /// </summary>
    [TestFixture]
    public class ApiRefuelWaitingToStartTests : ApiTestBase
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

        private (Entity colony, Entity fleet, Entity ship, ICargoable fuel) MakeFleetAndColony(
            PlayerSession session,
            Vector3 shipAbsolutePosition,
            double transferRangeDv)
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
                TransferRangeDv_mps = transferRangeDv,
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
                TransferRangeDv_mps = transferRangeDv,
            };

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                shipStorage,
                new PositionDB(shipAbsolutePosition, star),
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
                new PositionDB(shipAbsolutePosition, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            return (colony, fleet, ship, fuel);
        }

        private static void DumpOrders(string label, Entity entity)
        {
            if (!entity.TryGetDataBlob<OrderableDB>(out var orderable))
            {
                TestContext.WriteLine($"{label}: no OrderableDB");
                return;
            }

            TestContext.WriteLine($"{label}: {orderable.ActionList.Count} order(s)");
            foreach (var o in orderable.ActionList)
                TestContext.WriteLine($"  - {o.Name} | running={o.IsRunning} | finished={o.GetIsFinished} | {o.Details}");
        }

        [Test]
        public void Far_from_colony_RefuelAt_does_not_queue_ship_transfers_yet()
        {
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(3.8e11, 0, 0),
                transferRangeDv: 3000);

            var result = _server.SubmitCommand(session, new RefuelAtCommand(fleet.Id, colony.Id));
            Assert.That(result.Accepted, Is.True, result.RejectionReason);

            DumpOrders("Fleet after RefuelAt (far)", fleet);
            DumpOrders("Ship after RefuelAt (far)", ship);

            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Empty,
                "Transfers must wait until the fleet is at the colony.");
            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList.OfType<RefuelWhenAtColonyOrder>(), Is.Not.Empty);
        }

        [Test]
        public void At_colony_RefuelWhenAt_issues_transfers()
        {
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(1.5e11, 0, 0),
                transferRangeDv: 1e12);

            var earth = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;
            ship.GetDataBlob<PositionDB>().SetParent(earth);

            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.True);

            var order = RefuelWhenAtColonyOrder.CreateCommand(session.FactionId, fleet, colony);
            Assert.That(QueueOrder(order), Is.True);

            DumpOrders("Ship at colony after RefuelWhenAt", ship);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Not.Empty);
            Assert.That(order.GetIsFinished, Is.False,
                "Order stays active until refuel cargo transfers complete.");
        }

        [Test]
        public void At_colony_RefuelAt_command_starts_transfer_and_moves_fuel()
        {
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(1.5e11, 0, 0),
                transferRangeDv: 3000);

            var earth = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;
            ship.GetDataBlob<PositionDB>().SetParent(earth);
            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.True);

            var shipStore = ship.GetDataBlob<CargoStorageDB>();
            long fuelBefore = shipStore.GetUnitsStored(fuel, true);
            Assert.That(CargoMath.GetFreeUnitSpace(shipStore, fuel), Is.GreaterThan(0));

            var result = _server.SubmitCommand(session, new RefuelAtCommand(fleet.Id, colony.Id));
            Assert.That(result.Accepted, Is.True, result.RejectionReason);

            DumpOrders("Fleet", fleet);
            DumpOrders("Ship", ship);

            var xfer = ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>().FirstOrDefault();
            Assert.That(xfer, Is.Not.Null, "ship should have CargoTransfer");
            TestContext.WriteLine($"IsRunning={xfer!.IsRunning} Details={xfer.Details}");
            Assert.That(xfer.IsRunning, Is.True, "transfer must start immediately when already at colony");

            _game.Settings.EnforceSingleThread = true;
            _game.TimePulse.Ticklength = TimeSpan.FromHours(1);
            for (int i = 0; i < 5; i++)
                _game.TimePulse.TimeStep();

            long fuelAfter = ship.GetDataBlob<CargoStorageDB>().GetUnitsStored(fuel, true);
            TestContext.WriteLine($"fuel before={fuelBefore} after={fuelAfter}");
            Assert.That(fuelAfter, Is.GreaterThan(fuelBefore), "fuel should increase");
        }

        [Test]
        public void Parked_sibling_at_colony_does_not_count_as_fleet_at_colony()
        {
            // Science Fleet: Surveyor (warp) at Mercury, SensorSat (no warp) still at Earth.
            // Old IsFleetAtColony used .Any() → skipped return warp → Earth HQ transfer at Mercury.
            // Now: every warp-capable ship must be on-station; sats alone do not count.
            var session = Connect();
            var (colony, fleet, flagship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(5.8e10, 0, 0), // Mercury-ish
                transferRangeDv: 3000);

            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var earth = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;

            var mercury = Entity.Create();
            system.AddEntity(mercury, new List<BaseDataBlob>
            {
                new NameDB("Mercury", session.FactionId, "Mercury"),
                new PositionDB(new Vector3(5.8e10, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(3.3e23, 2.4e6),
            });
            flagship.GetDataBlob<PositionDB>().SetParent(mercury);

            var satStorage = new CargoStorageDB(fuel.CargoTypeID, 100_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 3000,
            };
            var sensorSat = Entity.Create(session.FactionId);
            system.AddEntity(sensorSat, new List<BaseDataBlob>
            {
                satStorage,
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 100 },
                new NameDB("SensorSat 1", session.FactionId, "SensorSat 1"),
                new OrderableDB(),
                new ShipInfoDB(),
            });
            fleet.GetDataBlob<FleetDB>().AddChild(sensorSat);

            Assert.That(FleetOrderCleanup.IsShipAtColony(sensorSat, colony), Is.True);
            Assert.That(FleetOrderCleanup.IsShipAtColony(flagship, colony), Is.False);
            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.False,
                "Warp hull away → fleet not at colony; parked sat alone does not count.");

            // Second warp ship also away → still not at colony (per-ship, not flagship-only).
            var escortStorage = new CargoStorageDB(fuel.CargoTypeID, 500_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 3000,
            };
            var escort = Entity.Create(session.FactionId);
            system.AddEntity(escort, new List<BaseDataBlob>
            {
                escortStorage,
                new PositionDB(new Vector3(5.8e10, 1e6, 0), mercury),
                new MassVolumeDB { MassDry = 5000 },
                new NameDB("Escort", session.FactionId, "Escort"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID },
            });
            fleet.GetDataBlob<FleetDB>().AddChild(escort);
            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.False);

            // Bring flagship home but leave escort at Mercury — still not "at colony".
            flagship.GetDataBlob<PositionDB>().SetParent(earth);
            Assert.That(FleetOrderCleanup.IsShipAtColony(flagship, colony), Is.True);
            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.False,
                "Every warp ship must be at the colony; flagship alone is not enough.");

            Assert.That(
                CargoTransferOrder.CreateRefuelFleetCommand(colony, fleet, OrderSource.Standing),
                Is.True,
                "Only the on-station flagship should get a transfer.");
            Assert.That(flagship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Not.Empty);
            Assert.That(escort.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Empty,
                "Escort still at Mercury must not get an Earth HQ hose.");
        }

        [Test]
        public void Ship_parented_to_colony_entity_counts_as_at_colony_and_refuels()
        {
            // FinishWarpAtStaticTarget parents the ship to the colony entity (not the planet).
            // Parent-only planet checks used to skip every hull → success=False forever.
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(1.5e11, 0, 0),
                transferRangeDv: 3000);

            ship.GetDataBlob<PositionDB>().SetParent(colony);

            Assert.That(FleetOrderCleanup.IsShipAtColony(ship, colony), Is.True);
            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.True);

            Assert.That(
                CargoTransferOrder.CreateRefuelFleetCommand(colony, fleet, OrderSource.Standing),
                Is.True);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Not.Empty);
        }

        [Test]
        public void Orphan_escrow_does_not_block_refuel_free_space_check()
        {
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(1.5e11, 0, 0),
                transferRangeDv: 3000);

            var earth = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;
            ship.GetDataBlob<PositionDB>().SetParent(earth);

            // Simulate a dead transfer that left escrow claiming the whole free tank.
            CargoTransferOrder.CreateCommands(
                fleet.FactionOwnerID, ship, colony, fuel, CargoTransferOrder.Conditionals.WaitTillFull);
            FleetOrderCleanup.AbortCargoTransfersOnFleetShips(fleet);

            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Empty);
            Assert.That(
                CargoTransferOrder.CreateRefuelFleetCommand(colony, fleet, OrderSource.Standing),
                Is.True,
                "After abort, tank room must still allow a new WaitTillFull.");
        }

        [Test]
        public void Non_fuel_sibling_does_not_dilute_fleet_fuel_percent_or_standing_exit()
        {
            // Surveyor full + SensorSat with ShipInfo but no fuel → old average was 50% →
            // standing kept restarting Refuel while CreateRefuel reported full=1.
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(1.5e11, 0, 0),
                transferRangeDv: 3000);

            var earth = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;
            ship.GetDataBlob<PositionDB>().SetParent(earth);

            // Fill surveyor tanks completely.
            var shipStore = ship.GetDataBlob<CargoStorageDB>();
            long free = CargoMath.GetFreeUnitSpace(shipStore, fuel, includeEscro: false);
            if (free > 0)
                shipStore.AddCargoByUnit(fuel, free);

            var sat = Entity.Create(session.FactionId);
            _game.Systems[0].AddEntity(sat, new List<BaseDataBlob>
            {
                new CargoStorageDB("mineral", 1000) { TransferRate = 1, TransferRangeDv_mps = 1 },
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 100 },
                new NameDB("SensorSat", session.FactionId, "SensorSat"),
                new OrderableDB(),
                new ShipInfoDB(),
            });
            fleet.GetDataBlob<FleetDB>().AddChild(sat);

            Assert.That(FleetOrderProcessor.GetFleetAverageFuelPercent(fleet), Is.EqualTo(100).Within(0.1),
                "SensorSat without fuel tanks must not pull the average to 50%.");
            Assert.That(FleetFuel.AnyHasFreeTankSpace(fleet), Is.False);
            Assert.That(FleetFuel.AnyBelow(fleet, 100f), Is.False);

            Assert.That(
                CargoTransferOrder.CreateRefuelFleetCommand(colony, fleet, OrderSource.Standing),
                Is.False,
                "Nothing to fill when the only fuel hull is full.");

            var actions = new SafeList<EntityCommand>
            {
                RefuelAction.CreateCommand(session.FactionId, fleet),
            };
            var condition = new CompoundCondition();
            condition.ConditionItems.Add(new ConditionItem(new FuelCondition(30f, ComparisonType.LessThan)));
            var standing = new ConditionalOrder(condition, actions) { Name = "refuel" };
            Assert.That(FleetOrderProcessor.OrderStillNeedsAction(fleet, standing), Is.False,
                "Exit must release once every fuel-capable hull is full.");
            Assert.That(standing.Condition.Evaluate(fleet), Is.False,
                "FuelCondition ENTER must ignore non-fuel siblings.");
        }

        [Test]
        public void Needy_ship_at_colony_refuels_while_full_sibling_stays_elsewhere()
        {
            var session = Connect();
            var (colony, fleet, needy, fuel) = MakeFleetAndColony(
                session,
                shipAbsolutePosition: new Vector3(1.5e11, 0, 0),
                transferRangeDv: 1e12);

            var earth = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;
            var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
            needy.GetDataBlob<PositionDB>().SetParent(earth);

            // Full sibling parked far away — must not block WaitTillFull for the needy hull.
            var fullStore = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            long fill = CargoMath.GetFreeUnitSpace(fullStore, fuel, includeEscro: false);
            if (fill > 0)
                fullStore.AddCargoByUnit(fuel, fill);
            var full = Entity.Create(session.FactionId);
            _game.Systems[0].AddEntity(full, new List<BaseDataBlob>
            {
                fullStore,
                new PositionDB(new Vector3(3.8e11, 0, 0), star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Full Escort", session.FactionId, "Full Escort"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID },
            });
            fleet.GetDataBlob<FleetDB>().AddChild(full);

            var cargoLib = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            Assert.That(FleetFuel.NeedsRefuel(full, cargoLib), Is.False, "Escort tanks must be completely full.");
            Assert.That(FleetFuel.NeedsRefuel(needy, cargoLib), Is.True);

            Assert.That(FleetOrderCleanup.IsFleetAtColony(fleet, colony), Is.False,
                "Whole-fleet gate would falsely wait for the full escort.");
            Assert.That(FleetFuel.AreNeedyShipsAtColony(fleet, colony), Is.True);

            var order = RefuelWhenAtColonyOrder.CreateCommand(session.FactionId, fleet, colony);
            Assert.That(QueueOrder(order), Is.True);

            Assert.That(needy.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Not.Empty,
                "Needy on-station hull must start WaitTillFull without waiting for full siblings.");
            Assert.That(full.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Empty);
            Assert.That(order.GetIsFinished, Is.False);
        }

        [Test]
        public void ClearFleetOrders_removes_ship_CargoTransfers()
        {
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session, new Vector3(3.8e11, 0, 0), transferRangeDv: 3000);

            // Simulate the old stuck-transfer state.
            CargoTransferOrder.CreateCommands(
                fleet.FactionOwnerID, ship, colony, fuel, CargoTransferOrder.Conditionals.WaitTillFull);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Not.Empty);

            var result = _server.SubmitCommand(session, new ClearFleetOrdersCommand(fleet.Id));
            Assert.That(result.Accepted, Is.True, result.RejectionReason);

            DumpOrders("Fleet after clear", fleet);
            DumpOrders("Ship after clear", ship);

            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList, Is.Empty);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList, Is.Empty);
            Assert.That(colony.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Empty,
                "Partner colony transfer must be aborted too.");
        }

        [Test]
        public void MoveToBody_aborts_stuck_ship_transfers_first()
        {
            var session = Connect();
            var (colony, fleet, ship, fuel) = MakeFleetAndColony(
                session, new Vector3(3.8e11, 0, 0), transferRangeDv: 3000);

            CargoTransferOrder.CreateCommands(
                fleet.FactionOwnerID, ship, colony, fuel, CargoTransferOrder.Conditionals.WaitTillFull);
            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Not.Empty);

            var bodyId = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity.Id;
            var result = _server.SubmitCommand(session, new MoveToBodyCommand(fleet.Id, bodyId));
            Assert.That(result.Accepted, Is.True, result.RejectionReason);

            Assert.That(ship.GetDataBlob<OrderableDB>().ActionList.OfType<CargoTransferOrder>(), Is.Empty,
                "Move must clear stuck cargo transfers so warp can use the Movement lane.");
        }
    }
}
