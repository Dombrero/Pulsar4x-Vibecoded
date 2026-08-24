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
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Per-ship standing fuel: sibling defer, suppress wake, jump-home StatusMessage, 2× reserve.
    /// </summary>
    [TestFixture]
    public class StandingStrandingFuelTests : ApiTestBase
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

        private Entity MakeShip(
            PlayerSession session,
            EntityManager system,
            Entity star,
            ICargoable fuel,
            string name,
            Vector3 absPos,
            long fuelUnits,
            long tankUnits = 100_000)
        {
            double tankVolume = fuel.VolumePerUnit * tankUnits;
            var storage = new CargoStorageDB(fuel.CargoTypeID, tankVolume)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };
            if (fuelUnits > 0)
                storage.AddCargoByUnit(fuel, Math.Min(fuelUnits, tankUnits));

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                storage,
                new PositionDB(absPos, star) { MoveType = PositionDB.MoveTypes.Warp },
                new MassVolumeDB { MassDry = 10000 },
                new NameDB(name, session.FactionId, name),
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
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID!] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID!] = 1e15 },
                },
                new GeoSurveyAbilityDB { Speed = 50 },
            });
            return ship;
        }

        [Test]
        public void CanAffordNextActionForShip_false_at_1x_hop_true_at_2x_reserve()
        {
            var session = Connect();
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

            var targetPos = new Vector3(3.0e11, 0, 0);
            var target = Entity.Create();
            system.AddEntity(target, new List<BaseDataBlob>
            {
                new NameDB("SurveyTarget", session.FactionId, "SurveyTarget"),
                new PositionDB(targetPos, star),
                MassVolumeDB.NewFromMassAndRadius_m(1e23, 1e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var ship = MakeShip(session, system, star, fuel, "Surveyor", earthPos + new Vector3(8e6, 0, 0), fuelUnits: 100_000);
            ship.GetDataBlob<PositionDB>().SetParent(earth);

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Fleet", session.FactionId, "Fleet"),
                new OrderableDB(),
                new PositionDB(earthPos, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);
            fleetDb.LastRefuelColonyId = colony.Id;
            fleetDb.LastRefuelSystemId = system.ManagerID;
            InstallRefuelThenGeoSurvey(fleet);

            var surveyOrder = fleetDb.StandingOrders[1];
            double dist = (targetPos - ship.GetDataBlob<PositionDB>().AbsolutePosition).Length();
            var fullHop = MissionFuelEstimator.EvaluateTravelHop(ship, dist);
            Assert.That(fullHop.OutboundNeedUnits, Is.GreaterThan(0), fullHop.Reason);

            long keep1x = Math.Max(1, fullHop.OutboundNeedUnits);
            var storage = ship.GetDataBlob<CargoStorageDB>();
            long stored = storage.GetUnitsStored(fuel, includeEscro: false);
            long remove = stored - keep1x;
            if (remove > 0)
                CargoTransferProcessor.AddRemoveCargoMass(ship, fuel, -remove * fuel.MassPerUnit);

            Assert.That(
                StandingActionAffordability.CanAffordNextActionForShip(ship, fleet, surveyOrder),
                Is.False,
                "1× hop fuel must not satisfy standing 2× reserve");

            long need2x = fullHop.OutboundNeedUnits * 2;
            long now = storage.GetUnitsStored(fuel, includeEscro: false);
            if (now < need2x)
                storage.AddCargoByUnit(fuel, need2x - now);

            Assert.That(
                StandingActionAffordability.CanAffordNextActionForShip(ship, fleet, surveyOrder),
                Is.True,
                "2× hop fuel must cover standing round-trip reserve");
        }

        [Test]
        public void Sibling_low_fuel_refuels_while_full_sibling_keeps_surveying()
        {
            var session = Connect();
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

            var bodyFar = Entity.Create();
            system.AddEntity(bodyFar, new List<BaseDataBlob>
            {
                new NameDB("FarBody", session.FactionId, "FarBody"),
                new PositionDB(new Vector3(4.0e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(1e23, 1e6),
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            // Abroad near survey target — A full, B at ~10% (below ENTER 30%).
            var abroad = new Vector3(3.9e11, 0, 0);
            var shipA = MakeShip(session, system, star, fuel, "Surveyor A", abroad, fuelUnits: 100_000);
            var shipB = MakeShip(session, system, star, fuel, "Surveyor B", abroad + new Vector3(1e8, 0, 0), fuelUnits: 10_000);

            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Science Fleet", session.FactionId, "Science Fleet"),
                new OrderableDB(),
                new PositionDB(abroad, star),
            });
            fleetDb.FlagShipID = shipA.Id;
            fleetDb.AddChild(shipA);
            fleetDb.AddChild(shipB);
            fleetDb.LastRefuelColonyId = colony.Id;
            fleetDb.LastRefuelSystemId = system.ManagerID;
            InstallRefuelThenGeoSurvey(fleet);

            var stateA = ShipStandingDirector.GetOrAddState(shipA);
            var stateB = ShipStandingDirector.GetOrAddState(shipB);
            stateA.ActiveStandingOrderIndex = 1;
            stateB.ActiveStandingOrderIndex = 1;

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(stateB.ActiveStandingOrderIndex, Is.EqualTo(0),
                "Low-fuel sibling must switch to Refuel");
            Assert.That(
                shipB.HasDataBlob<CargoTransferDB>()
                || (shipB.TryGetDataBlob<OrderableDB>(out var qB)
                    && (qB.ActionList.OfType<CargoTransferOrder>().Any()
                        || qB.ActionList.OfType<WarpMoveCommand>().Any())),
                Is.True,
                "Low-fuel sibling must enqueue refuel travel/transfer");

            Assert.That(stateA.ActiveStandingOrderIndex, Is.EqualTo(1),
                "Full sibling must keep surveying — fleet affordability must not defer B's refuel into silence");
        }

        [Test]
        public void Suppress_no_targets_clears_when_fuel_below_enter()
        {
            var session = Connect();
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

            var ship = MakeShip(
                session, system, star, fuel, "Surveyor",
                earthPos + new Vector3(5e10, 0, 0), fuelUnits: 10_000);
            var fleet = Entity.Create(session.FactionId);
            var fleetDb = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new NameDB("Fleet", session.FactionId, "Fleet"),
                new OrderableDB(),
                new PositionDB(ship.GetDataBlob<PositionDB>().AbsolutePosition, star),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);
            fleetDb.LastRefuelColonyId = colony.Id;
            InstallRefuelThenGeoSurvey(fleet);

            var state = ShipStandingDirector.GetOrAddState(ship);
            state.SuppressUntil = ship.StarSysDateTime + TimeSpan.FromDays(1);
            state.StatusMessage = "Can't find more survey targets";
            state.ActiveStandingOrderIndex = -1;

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(state.SuppressUntil, Is.Null, "Low fuel must clear suppress");
            Assert.That(state.ActiveStandingOrderIndex, Is.EqualTo(0));
            Assert.That(
                ship.HasDataBlob<CargoTransferDB>()
                || (ship.TryGetDataBlob<OrderableDB>(out var q)
                    && (q.ActionList.OfType<CargoTransferOrder>().Any()
                        || q.ActionList.OfType<WarpMoveCommand>().Any())),
                Is.True,
                "Refuel must start after suppress wake");
        }

        [Test]
        public void JumpHome_unaffordable_sets_StatusMessage()
        {
            var start = new DateTime(2100, 1, 1);
            var game = TestingUtilities.CreateTestUniverse(2, start, false);
            game.Settings.EnforceSingleThread = true;

            var systems = game.Systems.Distinct().ToArray();
            var home = systems[0];
            var remote = systems[1];
            home.SetActivityState(SystemActivityState.Foreground);
            remote.SetActivityState(SystemActivityState.Foreground);

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();

            Entity srcJp = remote.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
                ?? CreateJp(remote, "JP-Remote");
            Entity dstJp = home.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
                ?? CreateJp(home, "JP-Home");
            srcJp.GetDataBlob<JumpPointDB>().DestinationId = dstJp.Id;
            dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;
            srcJp.GetDataBlob<JumpPointDB>().IsDiscovered.Add(faction.Id);
            dstJp.GetDataBlob<JumpPointDB>().IsDiscovered.Add(faction.Id);

            var fuel = UnlockAnyFuel(factionInfo);
            var homeStar = home.GetFirstEntityWithDataBlob<StarInfoDB>();
            var colonyStorage = new CargoStorageDB(fuel.CargoTypeID, 1e12)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };
            var colony = Entity.Create(faction.Id);
            home.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), homeStar),
                colonyStorage,
                new PositionDB(0, 0, 0, homeStar),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("Home HQ", faction.Id, "Home HQ"),
                new OrderableDB(),
            });
            colonyStorage.AddCargoByUnit(fuel, 50_000_000);
            factionInfo.Colonies.Add(colony);

            // Far from gate with empty tanks — jump-home cannot warp to gate.
            if (!srcJp.TryGetDataBlob<PositionDB>(out var gatePos))
                throw new InvalidOperationException("gate needs PositionDB");
            var shipAbs = gatePos.AbsolutePosition + new Vector3(1.5e11, 0, 0);

            const long tankUnits = 100_000;
            double tankVolume = fuel.VolumePerUnit * tankUnits;
            var shipStorage = new CargoStorageDB(fuel.CargoTypeID, tankVolume)
            {
                TransferRate = 5000,
                TransferRangeDv_mps = 1e12,
            };

            var remoteStar = remote.GetFirstEntityWithDataBlob<StarInfoDB>();
            var ship = Entity.Create(faction.Id);
            remote.AddEntity(ship, new List<BaseDataBlob>
            {
                shipStorage,
                new PositionDB(shipAbs, remoteStar) { MoveType = PositionDB.MoveTypes.Warp },
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Stranded", faction.Id, "Stranded"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB
                {
                    MaxSpeed = 1e9,
                    EnergyType = fuel.UniqueID,
                    BubbleCreationCost = 1,
                    BubbleSustainCost = 0,
                },
                new EnergyGenAbilityDB(game.TimePulse.GameGlobalDateTime)
                {
                    EnergyType = fuel,
                    MaxOutputFromReactor = 1000,
                    EnergyStored = new Dictionary<string, double> { [fuel.UniqueID!] = 1e15 },
                    EnergyStoreMax = new Dictionary<string, double> { [fuel.UniqueID!] = 1e15 },
                },
            });

            var fleet = Entity.Create(faction.Id);
            var fleetDB = new FleetDB
            {
                LastRefuelSystemId = home.ManagerID,
                LastRefuelColonyId = colony.Id,
            };
            remote.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDB,
                new OrderableDB(),
                new NameDB("Survey Fleet", faction.Id, "Survey Fleet"),
                new PositionDB(shipAbs, remoteStar),
            });
            fleetDB.FlagShipID = ship.Id;
            fleetDB.AddChild(ship);

            var refuelActions = new SafeList<EntityCommand>
            {
                RefuelAction.CreateCommand(faction.Id, fleet),
            };
            var refuelCond = new CompoundCondition();
            refuelCond.ConditionItems.Add(new ConditionItem(new FuelCondition(30f, ComparisonType.LessThan)));
            fleetDB.StandingOrders.Add(new ConditionalOrder(refuelCond, refuelActions) { Name = "refuel" });

            var state = ShipStandingDirector.GetOrAddState(ship);
            state.ActiveStandingOrderIndex = 0;

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            Assert.That(state.StatusMessage, Is.Not.Null.And.Not.Empty,
                "Jump-home fail must set a visible StatusMessage");
            Assert.That(state.StatusMessage!, Does.Contain("fuel").IgnoreCase
                .Or.Contain("gate").IgnoreCase
                .Or.Contain("refuel").IgnoreCase
                .Or.Contain("empty").IgnoreCase);
        }

        private static ICargoable UnlockAnyFuel(FactionInfoDB factionInfo)
        {
            var data = factionInfo.Data;
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

        private static Entity CreateJp(StarSystem system, string name)
        {
            var jp = Entity.Create();
            jp.FactionOwnerID = Game.NeutralFactionId;
            system.AddEntity(jp, new List<BaseDataBlob>
            {
                new NameDB(name),
                new PositionDB(Distance.AuToMt(2), 0, 0),
                new JumpPointDB(),
            });
            return jp;
        }
    }
}
