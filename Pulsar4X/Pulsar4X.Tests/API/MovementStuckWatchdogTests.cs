using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Events;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class MovementStuckWatchdogTests : ApiTestBase
    {
        [SetUp]
        public void WatchdogSetUp() => MovementStuckWatchdog.ResetForTests();

        [TearDown]
        public void WatchdogTearDown() => MovementStuckWatchdog.ResetForTests();

        [Test]
        public void MicroHopLoop_pauses_and_publishes_event()
        {
            var session = Connect();
            var system = _game.Systems[0];
            var star = system.GetFirstEntityWithDataBlob<StarInfoDB>();
            var origin = star.GetDataBlob<PositionDB>().AbsolutePosition;

            var target = Entity.Create();
            system.AddEntity(target, new List<BaseDataBlob>
            {
                new NameDB("Stuck Moon", session.FactionId, "Stuck Moon"),
                new PositionDB(origin + new Vector3(1e9, 0, 0), star),
                MassVolumeDB.NewFromMassAndRadius_m(1e20, 1e6),
            });

            var ship = Entity.Create(session.FactionId);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                // Far from target so IsShipAtBody is false for micro-hop detection.
                new PositionDB(origin, star),
                new MassVolumeDB { MassDry = 10000 },
                new NameDB("Looper", session.FactionId, "Looper"),
                new OrderableDB(),
                new ShipInfoDB(),
                new WarpAbilityDB { MaxSpeed = 25712 },
            });

            var faction = _game.Factions[session.FactionId];
            var log = (FactionEventLog)faction.GetDataBlob<FactionInfoDB>().EventLog!;
            if (!log.HaltsOn(EventType.OrdersNotPossible))
                log.ToggleHaltsOn(EventType.OrdersNotPossible);

            int before = log.GetEvents().Count;
            var now = ship.StarSysDateTime;

            for (int i = 0; i < 12; i++)
                MovementStuckWatchdog.NoteWarpHopCompleted(ship, target, 1e5, now + TimeSpan.FromMinutes(i * 10));

            Assert.That(log.GetEvents().Count, Is.GreaterThan(before));
            var last = log.GetEvents()[^1];
            Assert.That(last.EventType, Is.EqualTo(EventType.OrdersNotPossible));
            Assert.That(last.Message, Does.Contain("SHIP STUCK").IgnoreCase);
            Assert.That(last.Message, Does.Contain("Micro-hop").IgnoreCase);
            Assert.That(last.EntityId, Is.EqualTo(ship.Id));
        }
    }
}
