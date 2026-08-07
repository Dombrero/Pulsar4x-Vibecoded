using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Energy;
using Pulsar4X.Fleets;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Galaxy;
using Pulsar4X.Orbital;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class JumpTimeAdvanceTests
    {
        [Test]
        public void CatchUpFromStasisNotifiesSystemDateChanged()
        {
            var start = new DateTime(2100, 1, 1);
            var game = TestingUtilities.CreateTestUniverse(2, start, false);
            game.Settings.EnforceSingleThread = true;
            game.TimePulse.Ticklength = TimeSpan.FromHours(1);

            var systems = game.Systems.Distinct().ToArray();
            var dest = systems[1];
            dest.SetActivityState(SystemActivityState.Stasis);

            for (int i = 0; i < 3; i++)
                game.TimePulse.TimeStep();

            DateTime? notified = null;
            dest.ManagerSubpulses.SystemDateChangedEvent += dt => notified = dt;

            dest.SetActivityState(SystemActivityState.Background);

            Assert.IsNotNull(notified, "CatchUp/FastForward must fire SystemDateChangedEvent for client sync");
            Assert.AreEqual(game.TimePulse.GameGlobalDateTime, notified);
        }

        [Test]
        public void TimeAdvancesAfterTransferIntoStasisSystem()
        {
            var start = new DateTime(2100, 1, 1);
            var game = TestingUtilities.CreateTestUniverse(2, start, false);
            game.Settings.EnforceSingleThread = true;
            game.TimePulse.Ticklength = TimeSpan.FromHours(1);

            var systems = game.Systems.Distinct().ToArray();
            var source = systems[0];
            var dest = systems[1];

            source.SetActivityState(SystemActivityState.Foreground);
            dest.SetActivityState(SystemActivityState.Stasis);

            for (int i = 0; i < 5; i++)
                game.TimePulse.TimeStep();

            var globalAfterAdvance = game.TimePulse.GameGlobalDateTime;
            Assert.Greater(globalAfterAdvance, start);
            Assert.Less(dest.StarSysDateTime, globalAfterAdvance);

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var entity = Entity.Create();
            entity.FactionOwnerID = faction.Id;
            source.AddEntity(entity, new BaseDataBlob[]
            {
                new NameDB("Jumper"),
                new PositionDB(0, 0, 0),
            });

            dest.Transfer(entity);
            Assert.AreEqual(SystemActivityState.Background, dest.ActivityState);
            Assert.AreEqual(globalAfterAdvance, dest.StarSysDateTime);

            dest.IncrementExternalObserver(true);

            var before = game.TimePulse.GameGlobalDateTime;
            var stepTask = Task.Run(() => game.TimePulse.TimeStep());
            var completed = stepTask.Wait(TimeSpan.FromSeconds(10));
            Assert.IsTrue(completed, "TimeStep hung after jump/transfer into previously-stasis system");
            if (stepTask.IsFaulted)
                Assert.Fail(stepTask.Exception!.GetBaseException().ToString());

            Assert.Greater(game.TimePulse.GameGlobalDateTime, before);
            Assert.AreEqual(game.TimePulse.GameGlobalDateTime, dest.StarSysDateTime);
        }

        [Test]
        public void TimeAdvancesAfterRealShipJumpCommand()
        {
            var start = new DateTime(2100, 1, 1);
            var game = TestingUtilities.CreateTestUniverse(2, start, false);
            game.Settings.EnforceSingleThread = true;
            game.TimePulse.Ticklength = TimeSpan.FromHours(1);

            var systems = game.Systems.Distinct().ToArray();
            var source = systems[0];
            var dest = systems[1];
            source.SetActivityState(SystemActivityState.Foreground);
            dest.SetActivityState(SystemActivityState.Stasis);

            for (int i = 0; i < 24; i++)
                game.TimePulse.TimeStep();

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);

            // Link jump points between systems (use existing gen JPs if present, else create)
            Entity srcJp = source.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
                ?? CreateJp(source, "JP-Src");
            Entity dstJp = dest.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault()
                ?? CreateJp(dest, "JP-Dst");
            srcJp.GetDataBlob<JumpPointDB>().DestinationId = dstJp.Id;
            dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;

            var star = source.GetFirstEntityWithDataBlob<StarInfoDB>();
            var ship = Entity.Create();
            ship.FactionOwnerID = faction.Id;
            var shipMass = MassVolumeDB.NewFromMassAndRadius_m(1e6, 10);
            var pos = new PositionDB(0, 0, 0, srcJp);
            var orbit = OrbitDB.FromPosition(star, new Vector3(1e11, 0, 0), shipMass.MassDry, source.StarSysDateTime);
            source.AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("Scout"),
                pos,
                shipMass,
                orbit,
                new OrderableDB(),
                new ShipInfoDB(),
                new EnergyGenAbilityDB(source.StarSysDateTime),
                new WarpAbilityDB(),
            });

            var jumpCmd = ShipJumpCommand.Create(ship, srcJp.GetDataBlob<JumpPointDB>());
            Assert.IsTrue(OrderEnqueue.Issued(game, jumpCmd));

            // Process orders / jump via time step on source
            var jumpTask = Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                    game.TimePulse.TimeStep();
            });
            Assert.IsTrue(jumpTask.Wait(TimeSpan.FromSeconds(15)), "Hung while processing jump");
            if (jumpTask.IsFaulted)
                Assert.Fail(jumpTask.Exception!.GetBaseException().ToString());

            Assert.AreSame(dest, ship.Manager, "Ship should be in destination system after jump");

            // Focus destination like the client does
            dest.IncrementExternalObserver(true);

            var before = game.TimePulse.GameGlobalDateTime;
            var afterJumpTask = Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                    game.TimePulse.TimeStep();
            });
            Assert.IsTrue(afterJumpTask.Wait(TimeSpan.FromSeconds(15)),
                "Time hung after arriving in destination system");
            if (afterJumpTask.IsFaulted)
                Assert.Fail(afterJumpTask.Exception!.GetBaseException().ToString());

            Assert.Greater(game.TimePulse.GameGlobalDateTime, before);
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
