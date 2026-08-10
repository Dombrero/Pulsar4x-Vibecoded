using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Runtime warp-exit checks on the playtest save: planets must stay good;
    /// moons/asteroids must land inside SOI (no GeoSurvey re-warp loop).
    /// </summary>
    [TestFixture]
    public class WarpExitBodyKindRuntimeTests
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves", "order_rework_08_08_26.sav");

        private Game _game = null!;
        private Entity _ship = null!;
        private StarSystem _system = null!;

        [SetUp]
        public void SetUp()
        {
            if (!File.Exists(SavePath))
                Assert.Ignore($"Missing save: {SavePath}");

            _game = Game.Load(File.ReadAllText(SavePath));
            _game.Settings.EnforceSingleThread = true;

            var faction = _game.Factions.Values
                .Where(f => f.Id != _game.GameMasterFaction.Id)
                .First(f => f.TryGetDataBlob<FactionInfoDB>(out var info) && info.KnownSystems.Count > 0);
            _system = _game.Systems.First(s => s.ID == faction.GetDataBlob<FactionInfoDB>().KnownSystems[0]);

            _ship = _system.GetAllEntitiesWithDataBlob<ShipInfoDB>()
                .FirstOrDefault(s => s.GetOwnersName().Contains("Surveyor 1", StringComparison.OrdinalIgnoreCase))
                ?? _system.GetAllEntitiesWithDataBlob<ShipInfoDB>().First(s => s.HasDataBlob<WarpAbilityDB>());

            // Park ship at Earth so every hop starts from the same place.
            var earth = FindBody("Earth");
            Assert.That(earth, Is.Not.Null);
            var pos = _ship.GetDataBlob<PositionDB>();
            if (_ship.HasDataBlob<WarpMovingDB>())
                _ship.RemoveDataBlob<WarpMovingDB>();
            if (_ship.HasDataBlob<OrbitDB>())
                _ship.RemoveDataBlob<OrbitDB>();
            if (_ship.HasDataBlob<OrbitUpdateOftenDB>())
                _ship.RemoveDataBlob<OrbitUpdateOftenDB>();
            if (_ship.TryGetDataBlob<OrderableDB>(out var q))
                q.ActionList.Clear();
            pos.SetParent(earth);
            pos.RelativePosition = new Orbital.Vector3(OrbitMath.LowOrbitRadius(earth!), 0, 0);
            _ship.SetDataBlob(OrbitDB.FromPosition(earth!, _ship, _ship.StarSysDateTime));
        }

        private Entity? FindBody(string name)
            => _system.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>()
                .FirstOrDefault(b => b.GetOwnersName().Equals(name, StringComparison.OrdinalIgnoreCase));

        private (double exitToBody_m, double soi_m, double hop_m, bool atBodyAfterArrival) MeasureWarp(Entity body)
        {
            // Clear prior warp leftovers.
            if (_ship.HasDataBlob<WarpMovingDB>())
                _ship.RemoveDataBlob<WarpMovingDB>();

            var now = _ship.StarSysDateTime;
            var bodyAbs = body.GetDataBlob<PositionDB>().AbsolutePosition;
            var shipAbs = _ship.GetDataBlob<PositionDB>().AbsolutePosition;

            var cmd = WarpMoveCommand.CreateCommandEZ(_ship, body, now);
            Assert.That(OrderEnqueue.Enqueue(_game, cmd), Is.True);

            var processor = new OrderableProcessor();
            processor.Init(_game);
            processor.ProcessEntity(_ship, 0);

            Assert.That(_ship.TryGetDataBlob<WarpMovingDB>(out var warp), Is.True, $"No WarpMovingDB for {body.GetOwnersName()}");

            double exitToBody = (warp!.ExitPointAbsolute - bodyAbs).Length();
            // Recompute body abs at PredictedExit for fairness on long hops.
            var bodyAtExit = (Orbital.Vector3)MoveMath.GetAbsoluteFuturePosition(body, warp.PredictedExitTime);
            double exitToBodyAtEti = (warp.ExitPointAbsolute - bodyAtExit).Length();
            double hop = (warp.ExitPointAbsolute - warp.EntryPointAbsolute).Length();
            double soi = body.GetSOI_m();

            // Instant-arrive at the predicted exit time (not later — body would drift off Exit).
            warp.LastProcessDateTime = now - TimeSpan.FromDays(1);
            WarpMoveProcessor.ProcessEntity(_ship, warp.PredictedExitTime);

            bool atBody = FleetOrderCleanup.IsShipAtBody(_ship, body);
            string parentName = _ship.GetDataBlob<PositionDB>().Parent?.GetOwnersName() ?? "-";
            TestContext.WriteLine(
                $"{body.GetOwnersName(),-12} kind={body.GetDataBlob<SystemBodyInfoDB>().BodyType,-12} " +
                $"soiAU={soi / 1.496e11:0.####} exitEtiAU={exitToBodyAtEti / 1.496e11:0.####} " +
                $"hopAU={hop / 1.496e11:0.####} atBody={atBody} parent={parentName} " +
                $"warpMax={_ship.GetDataBlob<WarpAbilityDB>().MaxSpeed}");

            return (exitToBodyAtEti, soi, hop, atBody);
        }

        private void ParkAtEarth()
        {
            var earth = FindBody("Earth")!;
            if (_ship.HasDataBlob<WarpMovingDB>())
                _ship.RemoveDataBlob<WarpMovingDB>();
            if (_ship.TryGetDataBlob<OrderableDB>(out var q))
                q.ActionList.Clear();
            if (_ship.HasDataBlob<OrbitDB>())
                _ship.RemoveDataBlob<OrbitDB>();
            if (_ship.HasDataBlob<OrbitUpdateOftenDB>())
                _ship.RemoveDataBlob<OrbitUpdateOftenDB>();
            var pos = _ship.GetDataBlob<PositionDB>();
            pos.SetParent(earth);
            pos.RelativePosition = new Orbital.Vector3(OrbitMath.LowOrbitRadius(earth), 0, 0);
            _ship.SetDataBlob(OrbitDB.FromPosition(earth, _ship, _ship.StarSysDateTime));
        }

        [Test]
        public void Planet_warps_exit_inside_SOI_and_arrive_at_body()
        {
            foreach (var name in new[] { "Mercury", "Venus", "Mars" })
            {
                var body = FindBody(name);
                Assert.That(body, Is.Not.Null, name);
                ParkAtEarth();

                var (exitTo, soi, hop, atBody) = MeasureWarp(body!);
                Assert.That(exitTo, Is.LessThan(Math.Max(soi * 5, 5e8)),
                    $"{name}: exit too far from body at ETI ({exitTo / 1.496e11:0.###} AU)");
                Assert.That(hop, Is.LessThan(5 * 1.496e11), $"{name}: absurd hop {hop / 1.496e11:0.###} AU");
                Assert.That(atBody, Is.True, $"{name}: ship not at body after warp arrival (parent wrong)");
            }
        }

        [Test]
        public void Moon_Luna_warp_is_short_hop_and_arrives_at_Luna()
        {
            var luna = FindBody("Luna");
            Assert.That(luna, Is.Not.Null);
            ParkAtEarth();

            var (exitTo, soi, hop, atBody) = MeasureWarp(luna!);
            TestContext.WriteLine($"Luna detail: exitTo={exitTo:0} m soi={soi:0} m hop={hop:0} m");

            Assert.That(hop, Is.LessThan(0.03 * 1.496e11),
                $"Luna hop must be local (~Earth-Moon scale), was {hop / 1.496e11:0.####} AU");
            Assert.That(exitTo, Is.LessThan(Math.Max(soi * 5, 5e8)),
                $"Luna: exit outside moon SOI band (exit={exitTo / 1.496e11:0.####} AU)");
            Assert.That(atBody, Is.True, "Luna: ship not counted at Luna after arrival — GeoSurvey will loop");
        }

        [Test]
        public void Asteroid_or_dwarf_warp_exit_reasonable()
        {
            var body = FindBody("Ceres")
                ?? _system.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>()
                    .FirstOrDefault(b => b.GetDataBlob<SystemBodyInfoDB>().BodyType == BodyType.Asteroid)
                ?? _system.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>()
                    .FirstOrDefault(b => b.GetDataBlob<SystemBodyInfoDB>().BodyType == BodyType.DwarfPlanet);

            if (body == null)
                Assert.Ignore("No asteroid/dwarf in save");

            ParkAtEarth();

            var (exitTo, soi, hop, atBody) = MeasureWarp(body);
            Assert.That(exitTo, Is.LessThan(Math.Max(soi * 10, 5e9)),
                $"{body.GetOwnersName()}: exit too far");
            Assert.That(hop, Is.LessThan(10 * 1.496e11), $"{body.GetOwnersName()}: absurd hop");
            Assert.That(atBody, Is.True, $"{body.GetOwnersName()}: not at body after arrival");
        }

        [Test]
        public void Report_all_major_bodies_warp_exit_matrix()
        {
            var names = new[]
            {
                "Mercury", "Venus", "Luna", "Mars",
                "Ceres", "Jupiter", "Io", "Europa", "Ganymede", "Callisto"
            };

            var rows = new List<string>();
            foreach (var name in names)
            {
                var body = FindBody(name);
                if (body == null)
                {
                    rows.Add($"{name}: MISSING");
                    continue;
                }

                ParkAtEarth();
                try
                {
                    var (exitTo, soi, hop, atBody) = MeasureWarp(body);
                    bool shortEnough = hop < 10 * 1.496e11;
                    bool ok = atBody && exitTo < Math.Max(soi * 5, 5e8) && shortEnough;
                    rows.Add($"{name}: ok={ok} hopAU={hop / 1.496e11:0.####} exitEtiAU={exitTo / 1.496e11:0.####} atBody={atBody}");
                }
                catch (Exception ex)
                {
                    rows.Add($"{name}: EX {ex.GetType().Name}: {ex.Message}");
                }
            }

            TestContext.WriteLine(string.Join("\n", rows));
            // Soft report — individual tests assert; this prints the matrix.
            Assert.Pass(string.Join(" | ", rows));
        }
    }
}
