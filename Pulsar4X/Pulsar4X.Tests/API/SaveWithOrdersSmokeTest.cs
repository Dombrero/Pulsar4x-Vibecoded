using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class SaveWithOrdersSmokeTest
    {
        [Test]
        public void Save_after_standing_refuel_and_cargo_transfer_does_not_throw()
        {
            var game = TestingUtilities.CreateTestUniverse(1, new DateTime(2050, 1, 1));
            game.Settings.EnforceSingleThread = true;
            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var system = game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var data = faction.GetDataBlob<FactionInfoDB>().Data;

            ICargoable fuel;
            if (data.CargoGoods.Contains("hydrolox"))
                fuel = data.CargoGoods.GetAny("hydrolox")!;
            else if (data.LockedCargoGoods.Contains("hydrolox"))
            {
                data.Unlock("hydrolox");
                fuel = data.CargoGoods.GetAny("hydrolox")!;
            }
            else
            {
                var material = data.LockedCargoGoods.GetMaterialsList().First();
                data.Unlock(material.UniqueID);
                fuel = data.CargoGoods.GetAny(material.UniqueID)!;
            }

            var earth = Entity.Create();
            system.AddEntity(earth, new List<BaseDataBlob>
            {
                new NameDB("E", faction.Id, "E"),
                new PositionDB(new Vector3(1.5e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
            });

            var colonyStore = new CargoStorageDB(fuel.CargoTypeID, 1e12)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            var colony = Entity.Create(faction.Id);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), earth),
                colonyStore,
                new PositionDB(earth.GetDataBlob<PositionDB>().AbsolutePosition, earth),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("HQ", faction.Id, "HQ"),
                new OrderableDB(),
            });
            colonyStore.AddCargoByUnit(fuel, 1_000_000);

            var shipStore = new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            shipStore.AddCargoByUnit(fuel, 100_000);

            var ship = Entity.Create(faction.Id);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                shipStore,
                new PositionDB(new Vector3(3.8e11, 0, 0), star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("S", faction.Id, "S"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8, EnergyType = fuel.UniqueID },
                new EnergyGenAbilityDB(game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID] = 1e15 },
                },
            });

            var fleet = Entity.Create(faction.Id);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("F", faction.Id, "F"),
                new OrderableDB(),
                new PositionDB(new Vector3(3.8e11, 0, 0), star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var actions = new SafeList<EntityCommand>
            {
                MoveToNearestColonyAction.CreateCommand(faction.Id, fleet),
                RefuelAction.CreateCommand(faction.Id, fleet),
            };
            var condition = new CompoundCondition();
            condition.ConditionItems.Add(new ConditionItem(new FuelCondition(30f, ComparisonType.LessThan)));
            fleetDb.StandingOrders.Add(new ConditionalOrder(condition, actions) { Name = "refuel" });

            fleet.GetDataBlob<OrderableDB>().ActionList.Add(
                RefuelWhenAtColonyOrder.CreateCommand(faction.Id, fleet, colony));
            CargoTransferOrder.CreateCommands(
                faction.Id, ship, colony, fuel, CargoTransferOrder.Conditionals.WaitTillFull);

            string? json = null;
            Assert.DoesNotThrow(() => json = Game.Save(game), "Game.Save threw with active refuel/cargo orders");
            Assert.That(json, Is.Not.Null.And.Not.Empty);
            TestContext.WriteLine($"Saved length: {json!.Length}");
            Assert.DoesNotThrow(() => Game.Load(json));
        }
    }
}
