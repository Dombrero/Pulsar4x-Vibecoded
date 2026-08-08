using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Factions;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class LoadOrderReworkSaveTest
    {
        [Test]
        public void Load_order_rework_save_is_client_ready()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Pulsar4X", "Pulsar4X", "Saves", "order_rework_08_08_26.sav");
            Assert.That(File.Exists(path), Is.True, $"Missing save at {path}");

            var loaded = Game.Load(File.ReadAllText(path));
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(4_000_000),
                "Save looks like the tiny hand-built crash save — regenerate from goal integration.");

            var faction = loaded.Factions.Values
                .Where(f => f.Id != loaded.GameMasterFaction.Id)
                .First(f => f.TryGetDataBlob<FactionInfoDB>(out var info) && info.KnownSystems.Count > 0);
            var info = faction.GetDataBlob<FactionInfoDB>();

            Assert.That(info.KnownSystems.Count, Is.GreaterThan(0));
            Assert.That(info.Colonies.Count, Is.GreaterThan(0));

            var projector = new GameProjector(loaded);
            Assert.That(projector.ProjectSystem(info.KnownSystems[0], faction.Id), Is.Not.Null);

            var fleets = projector.ProjectFleetHierarchy(faction.Id).Fleets;
            Assert.That(fleets.Any(f => f.Name.Contains("Survey", StringComparison.OrdinalIgnoreCase)), Is.True);

            loaded.Settings.EnforceSingleThread = true;
            loaded.TimePulse.Ticklength = TimeSpan.FromMinutes(5);
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 3; i++)
                    loaded.TimePulse.TimeStep();
            });
        }
    }
}
