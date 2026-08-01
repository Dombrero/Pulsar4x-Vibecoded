using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Components;
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

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class StandingOrderConditionEvaluateTests : ApiTestBase
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

        private (Entity fleet, Entity ship) MakeFleetWithShip(
            PlayerSession session,
            CargoStorageDB? shipStorage = null,
            ComponentInstancesDB? components = null)
        {
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var fuel = UnlockFuel(session);

            shipStorage ??= new CargoStorageDB(fuel.CargoTypeID, 2_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };

            var blobs = new List<BaseDataBlob>
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
            };
            if (components != null)
                blobs.Add(components);

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, blobs);

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

            return (fleet, ship);
        }

        [Test]
        public void HealthCondition_evaluates_fleet_average_health()
        {
            var session = Connect();
            var design = new ComponentDesign
            {
                UniqueID = "test-hull-plate",
                Name = "Hull Plate",
            };
            var damaged = new ComponentInstance(design);
            damaged.HealthPercent = 0.4f;

            var (fleet, ship) = MakeFleetWithShip(session, components: new ComponentInstancesDB());
            ship.AddComponent(damaged);

            var healthyCondition = new HealthCondition(50f, ComparisonType.GreaterThanOrEqual);
            Assert.That(healthyCondition.Evaluate(fleet), Is.False,
                "40% health should not satisfy >= 50");

            var damagedCondition = new HealthCondition(50f, ComparisonType.LessThan);
            Assert.That(damagedCondition.Evaluate(fleet), Is.True,
                "40% health should satisfy < 50");
        }

        [Test]
        public void CargoFillCondition_evaluates_volume_fill()
        {
            var session = Connect();
            var fuel = UnlockFuel(session);
            var storage = new CargoStorageDB(fuel.CargoTypeID, 1_000_000)
            {
                TransferRate = 1000,
                TransferRangeDv_mps = 1e12,
            };
            // Force a known fill level (FreeVolume is internal; tests have InternalsVisibleTo).
            storage.TypeStores[fuel.CargoTypeID].FreeVolume = 50_000; // 95% full

            var (fleet, _) = MakeFleetWithShip(session, shipStorage: storage);

            Assert.That(new CargoFillCondition(50f, ComparisonType.GreaterThanOrEqual).Evaluate(fleet), Is.True);
            Assert.That(new CargoFillCondition(99f, ComparisonType.GreaterThan).Evaluate(fleet), Is.False);
        }

        [Test]
        public void UnsurveyedGeoCondition_counts_targets_in_flagship_system()
        {
            var session = Connect();
            var (fleet, _) = MakeFleetWithShip(session);
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            int before = GeoSurveyTargets.CountEligible(system, session.FactionId);

            system.AddEntity(Entity.Create(), new List<BaseDataBlob>
            {
                new NameDB("Mars", session.FactionId, "Mars"),
                new PositionDB(new Vector3(2e11, 0, 0), star),
                new MassVolumeDB { MassDry = 6e23 },
                new GeoSurveyableDB { PointsRequired = 1000 },
            });

            Assert.That(new UnsurveyedGeoCondition(before, ComparisonType.GreaterThan).Evaluate(fleet), Is.True);
            Assert.That(new UnsurveyedGeoCondition(before + 1f, ComparisonType.EqualTo).Evaluate(fleet), Is.True);
            Assert.That(new UnsurveyedGeoCondition(before + 2f, ComparisonType.GreaterThanOrEqual).Evaluate(fleet), Is.False);
        }

        [Test]
        public void UnsurveyedAnomalyCondition_counts_jp_surveyables()
        {
            var session = Connect();
            var (fleet, _) = MakeFleetWithShip(session);
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            int before = system.GetAllEntitiesWithDataBlob<JPSurveyableDB>()
                .Count(e => !e.GetDataBlob<JPSurveyableDB>().IsSurveyComplete(session.FactionId));

            system.AddEntity(Entity.Create(), new List<BaseDataBlob>
            {
                new NameDB("Anomaly", session.FactionId, "Anomaly"),
                new PositionDB(new Vector3(2.5e11, 0, 0), star),
                new MassVolumeDB { MassDry = 1 },
                new JPSurveyableDB(100, new SafeDictionary<int, uint>(), 0),
            });

            Assert.That(new UnsurveyedAnomalyCondition(before, ComparisonType.GreaterThan).Evaluate(fleet), Is.True);
            Assert.That(new UnsurveyedAnomalyCondition(before + 1f, ComparisonType.EqualTo).Evaluate(fleet), Is.True);
        }

        [Test]
        public void HealthCondition_triggers_standing_enqueue()
        {
            var session = Connect();
            var design = new ComponentDesign
            {
                UniqueID = "test-hull-plate-2",
                Name = "Hull Plate",
            };
            var damaged = new ComponentInstance(design);
            damaged.HealthPercent = 0.2f;

            var (fleet, ship) = MakeFleetWithShip(session, components: new ComponentInstancesDB());
            ship.AddComponent(damaged);

            var actions = new SafeList<EntityCommand>
            {
                MoveToNearestColonyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var condition = new CompoundCondition();
            condition.ConditionItems.Add(new ConditionItem(new HealthCondition(50f, ComparisonType.LessThan)));
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(condition, actions)
            {
                Name = "retreat",
            });

            Assert.That(fleet.GetDataBlob<OrderableDB>().ActionList, Is.Empty);
            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders, Is.Not.Empty);
            Assert.That(orders.All(o => o.Source == OrderSource.Standing), Is.True);
            Assert.That(orders[0], Is.TypeOf<MoveToNearestColonyAction>());
        }

        [Test]
        public void UnsurveyedGeoCondition_triggers_standing_enqueue()
        {
            var session = Connect();
            var (fleet, _) = MakeFleetWithShip(session);
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            system.AddEntity(Entity.Create(), new List<BaseDataBlob>
            {
                new NameDB("Venus", session.FactionId, "Venus"),
                new PositionDB(new Vector3(1e11, 0, 0), star),
                new MassVolumeDB { MassDry = 5e24 },
                new GeoSurveyableDB { PointsRequired = 500 },
            });

            var actions = new SafeList<EntityCommand>
            {
                MoveToNearestGeoSurveyAction.CreateCommand(fleet.FactionOwnerID, fleet),
            };
            var condition = new CompoundCondition();
            condition.ConditionItems.Add(new ConditionItem(
                new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan)));
            fleet.GetDataBlob<FleetDB>().StandingOrders.Add(new ConditionalOrder(condition, actions)
            {
                Name = "survey",
            });

            new FleetOrderProcessor().ProcessEntity(fleet, 0);

            var orders = fleet.GetDataBlob<OrderableDB>().ActionList.ToList();
            Assert.That(orders, Is.Not.Empty);
            Assert.That(orders[0], Is.TypeOf<MoveToNearestGeoSurveyAction>());
            Assert.That(orders[0].Source, Is.EqualTo(OrderSource.Standing));
        }
    }
}
