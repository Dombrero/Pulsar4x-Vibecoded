using System;
using System.IO;
using NUnit.Framework;
using Pulsar4X.Engine;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class LoadUserSave2Test
    {
        [Test]
        public void Load_Testsavefile_2()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Pulsar4X", "Pulsar4X", "Saves", "Testsavefile 2.sav");
            Assert.That(File.Exists(path), Is.True, "save missing: " + path);

            try
            {
                var loaded = Game.Load(File.ReadAllText(path));
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.Factions.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(loaded.Systems.Count, Is.GreaterThanOrEqualTo(1));
                TestContext.WriteLine(
                    $"Loaded OK. Factions={loaded.Factions.Count}, Systems={loaded.Systems.Count}, GitHash={loaded.LastSaveGitHash}");
            }
            catch (Exception ex)
            {
                var dump = Path.Combine(Path.GetTempPath(), "pulsar4x-load-fail.txt");
                File.WriteAllText(dump, ex.ToString());
                Assert.Fail($"Load failed. Full exception written to {dump}\n\n{ex}");
            }
        }
    }
}
