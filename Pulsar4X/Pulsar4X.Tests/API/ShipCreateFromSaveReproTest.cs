using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Orbital;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Repro for pad-launch NullReferenceException when assembling Surveyor from a real save.
    /// </summary>
    [TestFixture]
    public class ShipCreateFromSaveReproTest
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves", "Testsavefile 5_ship_prod.sav");

        [Test]
        public void Pad_style_CreateShip_Surveyor_from_user_save()
        {
            if (!File.Exists(SavePath))
                Assert.Ignore($"Save not found: {SavePath}");

            var game = Game.Load(File.ReadAllText(SavePath));
            Assert.That(game, Is.Not.Null);
            game!.Settings.EnforceSingleThread = true;

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();
            Assert.That(factionInfo.IndustryDesigns.ContainsKey("default-ship-design-surveyor"), Is.True);

            var design = (ShipDesign)factionInfo.IndustryDesigns["default-ship-design-surveyor"];
            TestContext.WriteLine($"Components={design.Components?.Count}, DamageProfile={(design.DamageProfileDB != null)}, Mass={design.MassPerUnit}");
            foreach (var c in design.Components)
                TestContext.WriteLine($"  {c.count}x {(c.design?.Name ?? "NULL")} attrs={c.design?.AttributesByType?.Count}");

            var colony = factionInfo.Colonies.First();
            var planet = colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;
            Assert.That(planet, Is.Not.Null);

            var position = new Vector3(OrbitMath.LowOrbitRadius(planet), 0, 0);

            Exception? padEx = null;
            try
            {
                var ship = ShipFactory.CreateShip(design, faction, position, planet, "Repro Pad Ship");
                TestContext.WriteLine($"Pad-style OK ship#{ship.Id}");
            }
            catch (Exception ex)
            {
                padEx = ex;
                TestContext.WriteLine($"Pad-style FAIL: {ex}");
            }

            Exception? parkEx = null;
            try
            {
                var ship2 = ShipFactory.CreateShip(design, faction, planet, "Repro Park Ship");
                TestContext.WriteLine($"Parking OK ship#{ship2.Id}");
            }
            catch (Exception ex)
            {
                parkEx = ex;
                TestContext.WriteLine($"Parking FAIL: {ex}");
            }

            Assert.That(padEx, Is.Null, $"Pad-style CreateShip threw: {padEx}");
            Assert.That(parkEx, Is.Null, $"Parking CreateShip threw: {parkEx}");
        }

        [Test]
        public void DeliverAssembledShip_and_TryLaunch_from_user_save()
        {
            if (!File.Exists(SavePath))
                Assert.Ignore($"Save not found: {SavePath}");

            var game = Game.Load(File.ReadAllText(SavePath));
            Assert.That(game, Is.Not.Null);
            game!.Settings.EnforceSingleThread = true;

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();
            var design = (ShipDesign)factionInfo.IndustryDesigns["default-ship-design-surveyor"];
            var colony = factionInfo.Colonies.First();

            Assert.That(colony.HasDataBlob<LaunchComplexDB>(), Is.True,
                "colony needs a launch complex for this repro");
            var launchDB = colony.GetDataBlob<LaunchComplexDB>();
            TestContext.WriteLine($"Pads={launchDB.Pads.Count} queue={launchDB.LaunchQueue.Count}");
            foreach (var pad in launchDB.Pads)
                TestContext.WriteLine($"  pad {pad.Key}: max={pad.Value.MaxTonnage} design={pad.Value.ShipDesignId} ready={pad.Value.ReadyToLaunch}");

            Exception? deliverEx = null;
            Entity? delivered = null;
            try
            {
                delivered = LaunchComplexProcessor.DeliverAssembledShip(colony, design, "Repro Silent Reverie");
                TestContext.WriteLine($"Deliver OK ship#{delivered.Id}");
            }
            catch (Exception ex)
            {
                deliverEx = ex;
                TestContext.WriteLine($"Deliver FAIL: {ex}");
            }

            Assert.That(deliverEx, Is.Null, $"DeliverAssembledShip threw: {deliverEx}");
            Assert.That(delivered, Is.Not.Null);
        }
    }
}
