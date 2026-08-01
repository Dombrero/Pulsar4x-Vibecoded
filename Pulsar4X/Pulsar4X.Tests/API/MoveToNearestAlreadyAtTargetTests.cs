using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
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
    public class MoveToNearestAlreadyAtTargetTests : ApiTestBase
    {
        [Test]
        public void MoveToNearestColony_AlreadyOrbitingColony_FinishesImmediately()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();

            var planet = Entity.Create();
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("EarthBody", session.FactionId, "EarthBody"),
                new PositionDB(new Vector3(1.5e11, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(5.972e24, 6.371e6),
                OrbitDB.FromAsteroidFormat_r(
                    star, star.GetDataBlob<MassVolumeDB>().MassTotal, 5.972e24,
                    1.5e11, 0, 0, 0, 0, 0, system.StarSysDateTime),
            });

            var colony = Entity.Create(session.FactionId);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new ColonyInfoDB(new Dictionary<int, long>(), planet),
                new CargoStorageDB("fuel-storage", 1e12),
                new PositionDB(planet.GetDataBlob<PositionDB>().AbsolutePosition, planet),
                new MassVolumeDB { MassDry = 1e9 },
                new NameDB("Earth HQ", session.FactionId, "Earth HQ"),
                new OrderableDB(),
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new CargoStorageDB("fuel-storage", 2_000_000),
                new PositionDB(planet.GetDataBlob<PositionDB>().AbsolutePosition, planet),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Surveyor 1", session.FactionId, "Surveyor 1"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 1e8 },
            });

            var fleetDb = new FleetDB();
            var fleet = Entity.Create(session.FactionId);
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDb,
                new OrderableDB(),
                new NameDB("Tutorial Survey Fleet", session.FactionId, "Tutorial Survey Fleet"),
                new PositionDB(planet.GetDataBlob<PositionDB>().AbsolutePosition, planet),
            });
            fleetDb.FlagShipID = ship.Id;
            fleetDb.AddChild(ship);

            var move = MoveToNearestColonyAction.CreateCommand(session.FactionId, fleet);
            fleet.Manager.Game.OrderHandler.HandleOrder(move);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(fleet, 0);

            Assert.That(
                fleet.GetDataBlob<OrderableDB>().ActionList.OfType<MoveToNearestColonyAction>().Any(),
                Is.False,
                "Move-to-nearest must complete when the fleet is already at the colony, otherwise Refuel never runs.");
        }

        [Test]
        public void EnsureMinimumTransferCapability_RaisesZeroRates()
        {
            var storage = new CargoStorageDB("fuel-storage", 1000)
            {
                TransferRate = 0,
                TransferRangeDv_mps = 0,
            };

            CargoTransferOrder.EnsureMinimumTransferCapability(storage);

            Assert.That(storage.TransferRate, Is.GreaterThan(0));
            Assert.That(storage.TransferRangeDv_mps, Is.GreaterThan(0));
        }
    }
}
