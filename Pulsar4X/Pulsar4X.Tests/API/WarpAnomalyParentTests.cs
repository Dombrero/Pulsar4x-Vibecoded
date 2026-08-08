using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class WarpAnomalyParentTests : ApiTestBase
    {
        [Test]
        public void Leaving_grav_anomaly_hover_does_not_double_offset_position()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;
            var energyGood = data.CargoGoods.GetAll().Values
                .Concat(data.LockedCargoGoods.GetAll().Values)
                .First(g => g.UniqueID == "electricity" || g.UniqueID.Contains("electric"));

            var anomalyPos = new Vector3(1.5e12, 0, 0); // ~10 AU
            var anomaly = Entity.Create();
            system.AddEntity(anomaly, new List<BaseDataBlob>
            {
                new NameDB("Gravitational Anomaly #1", session.FactionId, "Gravitational Anomaly #1"),
                new PositionDB(anomalyPos.X, anomalyPos.Y, anomalyPos.Z)
                {
                    MoveType = PositionDB.MoveTypes.None,
                },
                MassVolumeDB.NewFromMassAndRadius_m(1, 1),
            });

            var target = Entity.Create();
            system.AddEntity(target, new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2.3e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(6e23, 3.4e6),
            });

            ICargoable fuel;
            foreach (var id in new[] { "hydrolox", "rp-1", "methalox" })
            {
                if (data.CargoGoods.Contains(id))
                {
                    fuel = data.CargoGoods.GetAny(id)!;
                    goto haveFuel;
                }
                if (data.LockedCargoGoods.Contains(id))
                {
                    data.Unlock(id);
                    fuel = data.CargoGoods.GetAny(id)!;
                    goto haveFuel;
                }
            }
            var material = data.LockedCargoGoods.GetMaterialsList().First();
            data.Unlock(material.UniqueID);
            fuel = data.CargoGoods.GetAny(material.UniqueID)!;
            haveFuel:

            var storage = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            storage.AddCargoByUnit(fuel, 1_000_000);

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                storage,
                new NameDB("Surveyor", session.FactionId, "Surveyor"),
                // Relative zero under anomaly → Absolute == anomaly (legacy hover parenting).
                new PositionDB(Vector3.Zero, anomaly) { MoveType = PositionDB.MoveTypes.Warp },
                new MassVolumeDB { MassDry = 1000 },
                new ShipInfoDB(),
                new OrderableDB(),
                new WarpAbilityDB
                {
                    MaxSpeed = 1e8,
                    EnergyType = energyGood.UniqueID,
                    BubbleCreationCost = 1,
                    BubbleSustainCost = 0,
                },
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = energyGood,
                    MaxOutputFromReactor = 1000,
                    EnergyStored = new Dictionary<string, double> { [energyGood.UniqueID] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [energyGood.UniqueID] = 1e15 },
                },
            });

            Assert.That(ship.GetDataBlob<PositionDB>().Parent?.Id, Is.EqualTo(anomaly.Id));
            Assert.That(ship.GetDataBlob<PositionDB>().AbsolutePosition.X, Is.EqualTo(anomalyPos.X).Within(1));

            var warpCmd = Pulsar4X.Movement.WarpMoveCommand.CreateCommandEZ(ship, target, ship.StarSysDateTime);
            Assert.That(QueueOrder(warpCmd), Is.True);

            _game.Settings.EnforceSingleThread = true;
            _game.TimePulse.Ticklength = TimeSpan.FromHours(1);
            _game.TimePulse.TimeStep();

            var pos = ship.GetDataBlob<PositionDB>();
            Assert.That(pos.AbsolutePosition.Length(), Is.LessThan(anomalyPos.Length() * 1.5),
                $"AbsolutePosition length {pos.AbsolutePosition.Length()} looks double-offset from anomaly");
            Assert.That(pos.Parent?.Id, Is.Not.EqualTo(anomaly.Id),
                "Next hop must leave the anomaly parent for the heliocentric warp frame");
        }

        [Test]
        public void Grav_anomaly_hover_bills_tank_fuel_only_once()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var data = _game.Factions[session.FactionId].GetDataBlob<FactionInfoDB>().Data;

            ICargoable fuel;
            foreach (var id in new[] { "hydrolox", "rp-1", "methalox" })
            {
                if (data.CargoGoods.Contains(id))
                {
                    fuel = data.CargoGoods.GetAny(id)!;
                    goto haveFuel;
                }
                if (data.LockedCargoGoods.Contains(id))
                {
                    data.Unlock(id);
                    fuel = data.CargoGoods.GetAny(id)!;
                    goto haveFuel;
                }
            }
            var material = data.LockedCargoGoods.GetMaterialsList().First();
            data.Unlock(material.UniqueID);
            fuel = data.CargoGoods.GetAny(material.UniqueID)!;
            haveFuel:

            var anomalyPos = new Vector3(3e11, 0, 0); // ~2 AU
            var anomaly = Entity.Create();
            system.AddEntity(anomaly, new List<BaseDataBlob>
            {
                new NameDB("Gravitational Anomaly #2", session.FactionId, "Gravitational Anomaly #2"),
                new PositionDB(anomalyPos.X, anomalyPos.Y, anomalyPos.Z)
                {
                    MoveType = PositionDB.MoveTypes.None,
                },
                MassVolumeDB.NewFromMassAndRadius_m(1, 1),
            });

            const long tankUnits = 2_000_000;
            var storage = new CargoStorageDB(fuel.CargoTypeID, tankUnits)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            storage.AddCargoByUnit(fuel, tankUnits);

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                storage,
                new NameDB("Surveyor", session.FactionId, "Surveyor"),
                new PositionDB(Vector3.Zero, star) { MoveType = PositionDB.MoveTypes.Warp },
                new MassVolumeDB { MassDry = 1000 },
                new ShipInfoDB(),
                new OrderableDB(),
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

            Assert.That(QueueOrder(
                Pulsar4X.Movement.WarpMoveCommand.CreateCommandEZ(ship, anomaly, ship.StarSysDateTime)), Is.True);

            _game.Settings.EnforceSingleThread = true;
            _game.TimePulse.Ticklength = TimeSpan.FromMinutes(5);

            bool arrived = false;
            for (int i = 0; i < 200; i++)
            {
                _game.TimePulse.TimeStep();
                if (ship.TryGetDataBlob<WarpMovingDB>(out var move) && move.IsAtTarget)
                {
                    arrived = true;
                    break;
                }
            }

            Assert.That(arrived, Is.True, "Ship must arrive and hover at the grav anomaly");
            Assert.That(ship.HasDataBlob<WarpMovingDB>(), Is.True, "Hover keeps WarpMovingDB");

            long afterArrive = storage.GetUnitsStored(fuel, includeEscro: false);
            Assert.That(afterArrive, Is.LessThan(tankUnits), "Arrival must bill tank fuel once");
            Assert.That(ship.GetDataBlob<WarpMovingDB>().WarpTankFuelConsumed, Is.True);

            for (int i = 0; i < 40; i++)
                _game.TimePulse.TimeStep();

            long afterHover = storage.GetUnitsStored(fuel, includeEscro: false);
            Assert.That(afterHover, Is.EqualTo(afterArrive),
                "Hover ticks must not re-bill the same warp hop");
        }
    }
}
