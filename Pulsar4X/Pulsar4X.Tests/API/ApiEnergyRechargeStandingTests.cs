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
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class ApiEnergyRechargeStandingTests : ApiTestBase
    {
        [Test]
        public void EnergyCondition_LessThan_ignores_ships_with_onboard_generation()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("GenShip", session.FactionId, "GenShip"),
                new PositionDB(Vector3.Zero, star),
                new MassVolumeDB { MassDry = 1000 },
                new ShipInfoDB(),
                new OrderableDB(),
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    MaxOutputFromReactor = 500,
                    EnergyStored = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 100 },
                    EnergyStoreMax = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 10_000 },
                },
            });

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Fleet", session.FactionId, "Fleet"),
                new OrderableDB(),
                new PositionDB(Vector3.Zero, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            Assert.That(new EnergyCondition(30f, ComparisonType.LessThan).Evaluate(fleet), Is.False,
                "generator ships must wait in place — standing recharge ENTER stays false");
        }

        [Test]
        public void EnergyCondition_LessThan_triggers_for_battery_only_hull()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("BatShip", session.FactionId, "BatShip"),
                new PositionDB(Vector3.Zero, star),
                new MassVolumeDB { MassDry = 1000 },
                new ShipInfoDB(),
                new OrderableDB(),
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    MaxOutputFromReactor = 0,
                    EnergyStored = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 100 },
                    EnergyStoreMax = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 10_000 },
                },
            });

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Fleet", session.FactionId, "Fleet"),
                new OrderableDB(),
                new PositionDB(Vector3.Zero, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            Assert.That(new EnergyCondition(30f, ComparisonType.LessThan).Evaluate(fleet), Is.True);
        }

        [Test]
        public void CreateRechargeFleetCommand_skips_generator_ships()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var planet = Entity.Create();
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("Body", session.FactionId, "Body"),
                new PositionDB(Vector3.Zero, star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
            });

            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new NameDB("Colony", session.FactionId, "Colony"),
                new ColonyInfoDB(new Dictionary<int, long>(), planet),
                new PositionDB(Vector3.Zero, star),
                new ColonyPowerDB
                {
                    StorageCapacityKJ = 1_000_000,
                    EnergyStoredKJ = 1_000_000,
                    DockChargeRateKW = 1000,
                },
            });

            var genShip = Entity.Create(session.FactionId);
            system.AddEntity(genShip, new List<BaseDataBlob>
            {
                new NameDB("Gen", session.FactionId, "Gen"),
                new PositionDB(Vector3.Zero, planet),
                new MassVolumeDB { MassDry = 1000 },
                new ShipInfoDB(),
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    MaxOutputFromReactor = 200,
                    EnergyStored = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 0 },
                    EnergyStoreMax = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 50_000 },
                },
            });

            var batShip = Entity.Create(session.FactionId);
            system.AddEntity(batShip, new List<BaseDataBlob>
            {
                new NameDB("Bat", session.FactionId, "Bat"),
                new PositionDB(Vector3.Zero, planet),
                new MassVolumeDB { MassDry = 1000 },
                new ShipInfoDB(),
                new ComponentInstancesDB(),
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    MaxOutputFromReactor = 0,
                    EnergyStored = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 0 },
                    EnergyStoreMax = new Dictionary<string, double> { [EnergyRechargeHelper.EnergyTypeId] = 50_000 },
                },
            });

            // Accept rate requires an EnergyStoreAtb battery instance.
            var batDesign = new Pulsar4X.Components.ComponentDesign { UniqueID = "bat", Name = "Bat" };
            batDesign.AttributesByType[typeof(EnergyStoreAtb)] =
                new EnergyStoreAtb(EnergyRechargeHelper.EnergyTypeId, 50_000);
            batShip.AddComponent(batDesign);

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Fleet", session.FactionId, "Fleet"),
                new OrderableDB(),
                new PositionDB(Vector3.Zero, planet),
            });
            fleetDb.AddChild(genShip);
            fleetDb.AddChild(batShip);

            Assert.That(EnergyRechargeHelper.CreateRechargeFleetCommand(colony, fleet), Is.True);
            Assert.That(genShip.HasDataBlob<EnergyRechargeDB>(), Is.False,
                "generator ships must not receive colony dock recharge");
            Assert.That(batShip.HasDataBlob<EnergyRechargeDB>(), Is.True);
        }

        [Test]
        public void WarpMove_with_generator_catchup_fills_enough_to_start()
        {
            var session = Connect();
            var data = _game.Factions[session.FactionId].GetDataBlob<Pulsar4X.Factions.FactionInfoDB>().Data;
            var energyGood = data.CargoGoods.GetAll().Values
                .Concat(data.LockedCargoGoods.GetAll().Values)
                .First(g => g.UniqueID == EnergyRechargeHelper.EnergyTypeId
                            || g.UniqueID.Contains("electric", System.StringComparison.OrdinalIgnoreCase));

            var star = _game.Systems[0].GetFirstEntityWithDataBlob<StarInfoDB>();
            var target = Entity.Create();
            _game.Systems[0].AddEntity(target, new List<BaseDataBlob>
            {
                new NameDB("Target", session.FactionId, "Target"),
                new PositionDB(new Vector3(1e9, 0, 0), star),
                new MassVolumeDB { MassDry = 1e24 },
            });

            var ship = Entity.Create(session.FactionId);
            _game.Systems[0].AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("Scout", session.FactionId, "Scout"),
                new PositionDB(Vector3.Zero, star),
                new MassVolumeDB { MassDry = 1000 },
                new OrderableDB(),
                new WarpAbilityDB
                {
                    MaxSpeed = 1e8,
                    EnergyType = energyGood.UniqueID,
                    BubbleCreationCost = 1_000_000,
                    BubbleSustainCost = 0,
                },
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = energyGood,
                    MaxOutputFromReactor = 50_000, // no battery AcceptRate → CatchUp uses full reactor
                    EnergyStored = new Dictionary<string, double> { [energyGood.UniqueID] = 100_000 },
                    EnergyStoreMax = new Dictionary<string, double> { [energyGood.UniqueID] = 2_000_000 },
                },
            });

            var warpCmd = Pulsar4X.Movement.WarpMoveCommand.CreateCommandEZ(ship, target, ship.StarSysDateTime);
            Assert.That(QueueOrder(warpCmd), Is.True);

            _game.Settings.EnforceSingleThread = true;
            _game.TimePulse.Ticklength = TimeSpan.FromHours(1);
            _game.TimePulse.TimeStep();

            Assert.That(ship.HasDataBlob<WarpMovingDB>(), Is.True,
                "generator CatchUp should fill capacitors and start the warp");
            Assert.That(ship.GetDataBlob<EnergyGenAbilityDB>().EnergyStored[energyGood.UniqueID],
                Is.GreaterThanOrEqualTo(1_000_000).Within(1));
        }
    }
}
