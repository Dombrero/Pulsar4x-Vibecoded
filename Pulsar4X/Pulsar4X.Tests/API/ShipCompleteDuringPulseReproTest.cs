using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Industry;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using Entity = Pulsar4X.Engine.Entity;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Full pulse path: industry completes a Surveyor mid-timestep (the crash the player hits).
    /// </summary>
    [TestFixture]
    public class ShipCompleteDuringPulseReproTest
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves", "Testsavefile 5_ship_prod.sav");

        [Test]
        public void Completing_surveyor_during_TimeStep_does_not_crash()
        {
            if (!File.Exists(SavePath))
                Assert.Ignore($"Save not found: {SavePath}");

            Exception? unhandled = null;
            void OnUnhandled(object? s, UnhandledExceptionEventArgs e)
                => unhandled = e.ExceptionObject as Exception;

            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            try
            {
                var game = Game.Load(File.ReadAllText(SavePath));
                Assert.That(game, Is.Not.Null);
                game!.Settings.EnforceSingleThread = true;
                game.TimePulse.Ticklength = TimeSpan.FromDays(1);

                var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
                var factionInfo = faction.GetDataBlob<FactionInfoDB>();
                var design = (ShipDesign)factionInfo.IndustryDesigns["default-ship-design-surveyor"];
                var colony = factionInfo.Colonies.First();
                var industry = colony.GetDataBlob<IndustryAbilityDB>();
                var shipLine = industry.ProductionLines.First(kv =>
                    kv.Value.IndustryTypeRates.ContainsKey("ship-assembly"));

                // Near-complete job: resources paid, 1 IP left — finishes on next industry tick.
                var job = new IndustryJob(factionInfo, design.UniqueID);
                job.InitialiseJob(1, false);
                job.ProductionPointsLeft = 1;
                foreach (var key in job.ResourcesRequiredRemaining.Keys.ToList())
                    job.ResourcesRequiredRemaining[key] = 0;

                // Ensure line can spend ship-assembly points this day.
                shipLine.Value.IndustryTypeRates["ship-assembly"] = Math.Max(
                    shipLine.Value.IndustryTypeRates["ship-assembly"], 100);
                shipLine.Value.Jobs.Clear();
                shipLine.Value.Jobs.Add(job);

                var before = game.TimePulse.GameGlobalDateTime;
                Assert.DoesNotThrow(() => game.TimePulse.TimeStep(),
                    "TimeStep while completing ship threw");
                Assert.That(unhandled, Is.Null, $"Unhandled exception: {unhandled}");
                Assert.That(game.TimePulse.GameGlobalDateTime, Is.EqualTo(before + TimeSpan.FromDays(1)));

                // Another day — exercises GetNextInterupt after mid-pulse ship blob scheduling.
                Assert.DoesNotThrow(() => game.TimePulse.TimeStep(),
                    "Second TimeStep after ship spawn threw");
                Assert.That(unhandled, Is.Null, $"Unhandled on second step: {unhandled}");

                TestContext.WriteLine($"Job status={job.Status} completed={job.NumberCompleted}/{job.NumberOrdered}");
            }
            finally
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            }
        }
    }
}
