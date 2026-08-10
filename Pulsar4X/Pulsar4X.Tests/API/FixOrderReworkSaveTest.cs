using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Patches order_rework_08_08_26.sav: clear stuck warps, park Surveyors at Earth,
    /// raise warp MaxSpeed so Luna intercepts stay local (~Earth-Moon) under the period-sweep solver.
    /// </summary>
    [TestFixture]
    public class FixOrderReworkSaveTest
    {
        private const int TargetWarpMaxSpeed = 120_000;

        private static readonly string SaveFileName = "order_rework_08_08_26.sav";

        private static readonly string AppDataSavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves", SaveFileName);

        [Test]
        public void Fix_surveyors_speed_and_clear_stuck_warps()
        {
            Assert.That(File.Exists(AppDataSavePath), Is.True, $"Missing save: {AppDataSavePath}");

            var game = Game.Load(File.ReadAllText(AppDataSavePath));
            game.Settings.EnforceSingleThread = true;

            var faction = game.Factions.Values
                .Where(f => f.Id != game.GameMasterFaction.Id)
                .First(f => f.TryGetDataBlob<FactionInfoDB>(out var info) && info.KnownSystems.Count > 0);
            var system = game.Systems.First(s => s.ID == faction.GetDataBlob<FactionInfoDB>().KnownSystems[0]);
            var earth = system.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>()
                .First(b => b.GetOwnersName().Equals("Earth", StringComparison.OrdinalIgnoreCase));
            var luna = system.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>()
                .First(b => b.GetOwnersName().Equals("Luna", StringComparison.OrdinalIgnoreCase));

            var surveyors = system.GetAllEntitiesWithDataBlob<ShipInfoDB>()
                .Where(s => s.GetOwnersName().Contains("Surveyor", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.That(surveyors, Is.Not.Empty);

            foreach (var ship in surveyors)
            {
                if (ship.HasDataBlob<WarpMovingDB>())
                    ship.RemoveDataBlob<WarpMovingDB>();
                if (ship.TryGetDataBlob<OrderableDB>(out var q))
                    q.ActionList.Clear();

                if (ship.HasDataBlob<OrbitDB>())
                    ship.RemoveDataBlob<OrbitDB>();
                if (ship.HasDataBlob<OrbitUpdateOftenDB>())
                    ship.RemoveDataBlob<OrbitUpdateOftenDB>();

                var pos = ship.GetDataBlob<PositionDB>();
                pos.SetParent(earth);
                pos.RelativePosition = new Orbital.Vector3(OrbitMath.LowOrbitRadius(earth), 0, 0);
                ship.SetDataBlob(OrbitDB.FromPosition(earth, ship, ship.StarSysDateTime));

                var warp = ship.GetDataBlob<WarpAbilityDB>();
                double mass = ship.GetDataBlob<MassVolumeDB>().MassTotal;
                warp.TotalWarpPower = Math.Max(warp.TotalWarpPower, TargetWarpMaxSpeed * mass / 1000.0);
                warp.MaxSpeed = TargetWarpMaxSpeed;

                var (exit, _) = WarpMath.GetInterceptPosition(ship, luna, ship.StarSysDateTime);
                double hopAu = (exit - pos.AbsolutePosition).Length() / 1.496e11;
                TestContext.WriteLine(
                    $"{ship.GetOwnersName()}: max={warp.MaxSpeed} parent={pos.Parent?.GetOwnersName()} hopLunaAU={hopAu:0.####}");
                Assert.That(hopAu, Is.LessThan(0.03),
                    $"{ship.GetOwnersName()} Luna hop still too long after speed boost ({hopAu:0.####} AU)");
            }

            string json = Game.Save(game);
            var destinations = new[]
            {
                AppDataSavePath,
                Path.GetFullPath(Path.Combine(
                    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Saves", SaveFileName)),
            };

            foreach (var path in destinations.Distinct())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                TestContext.WriteLine($"Wrote {path} ({new FileInfo(path).Length} bytes)");
            }
        }
    }
}
