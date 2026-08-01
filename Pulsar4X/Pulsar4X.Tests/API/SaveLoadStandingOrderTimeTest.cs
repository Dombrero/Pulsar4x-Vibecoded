using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Datablobs;
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
    public class SaveLoadStandingOrderTimeTest
    {
        [Test]
        public void SaveLoad_with_standing_move_to_colony_still_timesteps()
        {
            var game = TestingUtilities.CreateTestUniverse(1, new DateTime(2050, 1, 1));
            game.Settings.EnforceSingleThread = true;
            game.TimePulse.Ticklength = TimeSpan.FromHours(1);

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var system = game.Systems[0];
            system.IncrementExternalObserver(true);

            // Minimal fleet with a standing Move-to-Nearest-Colony (the save/load NRE path).
            var fleet = Entity.Create(faction.Id);
            var fleetDB = new FleetDB();
            system.AddEntity(fleet, new List<BaseDataBlob>
            {
                fleetDB,
                new NameDB("F", faction.Id, "F"),
                new OrderableDB(),
                new PositionDB(new Vector3(1e11, 0, 0), system.GetFirstEntityWithDataBlob<StarInfoDB>()),
            });

            var ship = Entity.Create(faction.Id);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("S", faction.Id, "S"),
                new OrderableDB(),
                new ShipInfoDB(),
                new PositionDB(new Vector3(1e11, 0, 0), system.GetFirstEntityWithDataBlob<StarInfoDB>()),
                new MassVolumeDB { MassDry = 1000 },
            });
            fleetDB.FlagShipID = ship.Id;
            fleetDB.AddChild(ship);

            var actions = new SafeList<EntityCommand>
            {
                MoveToNearestColonyAction.CreateCommand(faction.Id, fleet),
            };
            // Always-true fuel condition (threshold high) so standing fires after load.
            var condition = new CompoundCondition();
            condition.ConditionItems.Add(new ConditionItem(
                new FuelCondition(100f, ComparisonType.LessThanOrEqual)));
            fleetDB.StandingOrders.Add(new ConditionalOrder(condition, actions) { Name = "move" });

            string json = Game.Save(game);
            var loaded = Game.Load(json);
            loaded.Settings.EnforceSingleThread = true;
            loaded.TimePulse.Ticklength = TimeSpan.FromHours(1);

            var loadedSys = loaded.Systems[0];
            loadedSys.IncrementExternalObserver(true);

            var before = loaded.TimePulse.GameGlobalDateTime;
            Assert.DoesNotThrow(() => loaded.TimePulse.TimeStep());
            Assert.That(loaded.TimePulse.GameGlobalDateTime, Is.EqualTo(before + TimeSpan.FromHours(1)));
        }

        [Test]
        public void Real_user_save_timesteps_when_present()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Pulsar4X", "Pulsar4X", "Saves", "Testsavefile 1.sav");
            if (!File.Exists(path))
                Assert.Ignore("user save not present");

            var loaded = Game.Load(File.ReadAllText(path));
            loaded.Settings.EnforceSingleThread = true;
            loaded.TimePulse.Ticklength = TimeSpan.FromDays(1);

            var faction = loaded.Factions.Values.First(f => f.Id != loaded.GameMasterFaction.Id);
            if (faction.TryGetDataBlob<FactionInfoDB>(out var info) && info.KnownSystems.Count > 0)
            {
                var sol = loaded.Systems.First(s => s.ID == info.KnownSystems[0]);
                sol.IncrementExternalObserver(true);
            }

            var before = loaded.TimePulse.GameGlobalDateTime;
            Assert.DoesNotThrow(() => loaded.TimePulse.TimeStep());
            Assert.That(loaded.TimePulse.GameGlobalDateTime, Is.EqualTo(before + TimeSpan.FromDays(1)));
        }
    }
}
