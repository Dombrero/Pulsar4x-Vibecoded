using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
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
