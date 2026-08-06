using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests;

/// <summary>
/// Headless check: multi-system gen, jump into a stasis system, focus, time advance,
/// and body/anomaly counts (mirrors what the map shows after a jump).
/// </summary>
[TestFixture]
public class SystemSwitchPlanetGenDiagnosticsTests
{
    [Test]
    public void GeneratedSystemsHaveBodiesAndSurviveJumpFocusTime()
    {
        var start = new DateTime(2100, 1, 1);
        var game = TestingUtilities.CreateTestUniverse(8, start, false);
        game.Settings.EnforceSingleThread = true;
        game.TimePulse.Ticklength = TimeSpan.FromHours(1);

        var systems = game.Systems.Distinct().ToArray();
        Assert.GreaterOrEqual(systems.Length, 8, "Expected multiple generated systems");

        TestContext.WriteLine("=== System body census (engine, pre-jump) ===");
        var reports = new List<string>();
        int emptyBodySystems = 0;
        foreach (var sys in systems)
        {
            var report = DescribeSystem(sys);
            reports.Add(report);
            TestContext.WriteLine(report);
            if (CountBodies(sys) == 0)
                emptyBodySystems++;
        }

        // Procedural RNG can produce empty systems (~20% PlanetGenerationChance miss),
        // but most of 8 systems should have at least one SystemBodyInfoDB.
        Assert.Less(emptyBodySystems, systems.Length,
            "Every generated system had zero planets/asteroids/comets — generation likely broken");
        Assert.LessOrEqual(emptyBodySystems, systems.Length / 2,
            $"Too many empty systems ({emptyBodySystems}/{systems.Length}). Reports:\n{string.Join("\n", reports)}");

        // Leave most systems in stasis like an unvisited galaxy, keep one active.
        var home = systems[0];
        home.SetActivityState(SystemActivityState.Foreground);
        for (int i = 1; i < systems.Length; i++)
            systems[i].SetActivityState(SystemActivityState.Stasis);

        for (int i = 0; i < 12; i++)
            game.TimePulse.TimeStep();

        var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
        var dest = systems.Skip(1).First(s => CountBodies(s) > 0);

        Entity srcJp = home.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault() ?? CreateJp(home, "JP-Home");
        Entity dstJp = dest.GetAllEntitiesWithDataBlob<JumpPointDB>().FirstOrDefault() ?? CreateJp(dest, "JP-Dest");
        srcJp.GetDataBlob<JumpPointDB>().DestinationId = dstJp.Id;
        dstJp.GetDataBlob<JumpPointDB>().DestinationId = srcJp.Id;

        var star = home.GetFirstEntityWithDataBlob<StarInfoDB>();
        var ship = Entity.Create();
        ship.FactionOwnerID = faction.Id;
        var shipMass = MassVolumeDB.NewFromMassAndRadius_m(1e6, 10);
        home.AddEntity(ship, new List<BaseDataBlob>
        {
            new NameDB("Diag-Scout"),
            new PositionDB(0, 0, 0, srcJp),
            shipMass,
            OrbitDB.FromPosition(star, new Vector3(1e11, 0, 0), shipMass.MassDry, home.StarSysDateTime),
            new OrderableDB(),
            new ShipInfoDB(),
            new EnergyGenAbilityDB(home.StarSysDateTime),
            new WarpAbilityDB(),
        });

        var bodiesBeforeJump = CountBodies(dest);
        var anomaliesBeforeJump = CountAnomalies(dest);
        TestContext.WriteLine($"Dest before jump: bodies={bodiesBeforeJump}, anomalies={anomaliesBeforeJump}, state={dest.ActivityState}, sysTime={dest.StarSysDateTime:u}, global={game.TimePulse.GameGlobalDateTime:u}");

        Assert.IsTrue(game.OrderHandler.HandleOrder(ShipJumpCommand.Create(ship, srcJp.GetDataBlob<JumpPointDB>())));

        // Mirror JP-survey discovery: destination must be in KnownSystems for faction projection.
        faction.GetDataBlob<FactionInfoDB>().KnownSystems.Add(dest.ID);

        var jumpTask = Task.Run(() =>
        {
            for (int i = 0; i < 6; i++)
                game.TimePulse.TimeStep();
        });
        Assert.IsTrue(jumpTask.Wait(TimeSpan.FromSeconds(20)), "Time hung during jump");
        if (jumpTask.IsFaulted)
            Assert.Fail(jumpTask.Exception!.GetBaseException().ToString());

        Assert.AreSame(dest, ship.Manager, "Ship should be in destination after jump");

        // Client-equivalent: focus destination after arrival
        using var server = new EngineGameServer(game);
        var connect = server.Connect(new ConnectRequest { PlayerName = "Diag", FactionId = faction.Id });
        Assert.IsTrue(connect.Success, connect.FailureReason);
        server.SetSystemFocus(connect.Session!, dest.ID);

        var bodiesAfterFocus = CountBodies(dest);
        var anomaliesAfterFocus = CountAnomalies(dest);
        TestContext.WriteLine($"Dest after focus: bodies={bodiesAfterFocus}, anomalies={anomaliesAfterFocus}, state={dest.ActivityState}, sysTime={dest.StarSysDateTime:u}");

        Assert.AreEqual(bodiesBeforeJump, bodiesAfterFocus, "Jump/focus must not destroy system bodies");
        Assert.AreEqual(anomaliesBeforeJump, anomaliesAfterFocus, "Jump/focus must not destroy grav anomalies");
        Assert.Greater(bodiesAfterFocus, 0, "Destination should still have planets/bodies after jump");
        Assert.Greater(anomaliesAfterFocus, 0, "Destination should have gravitational anomalies");

        var projector = new GameProjector(game);
        var snapshot = projector.ProjectSystem(dest, faction.Id);
        int snapBodies = snapshot.Entities.Count(e => e.HasView<BodyView>() && e.Kind != BodyKind.Star);
        int snapStars = snapshot.Entities.Count(e => e.Kind == BodyKind.Star);
        int snapGrav = snapshot.Entities.Count(e => e.HasView<GravSurveyView>());
        TestContext.WriteLine($"Faction projection: stars={snapStars}, bodies={snapBodies}, gravAnomalies={snapGrav}, totalEntities={snapshot.Entities.Count}, date={snapshot.DateTime:u}");

        Assert.Greater(snapStars, 0, "Projected system must include the star");
        Assert.Greater(snapBodies, 0, "Projected system must include planets/bodies (visibility/KnownSystems)");
        Assert.Greater(snapGrav, 0, "Projected system must include gravitational anomalies");
        Assert.AreEqual(game.TimePulse.GameGlobalDateTime, snapshot.DateTime,
            "After focus/catch-up, projected system DateTime should match global clock");

        var before = game.TimePulse.GameGlobalDateTime;
        var timeTask = Task.Run(() =>
        {
            for (int i = 0; i < 5; i++)
                game.TimePulse.TimeStep();
        });
        Assert.IsTrue(timeTask.Wait(TimeSpan.FromSeconds(20)), "Time hung after system switch");
        if (timeTask.IsFaulted)
            Assert.Fail(timeTask.Exception!.GetBaseException().ToString());

        Assert.Greater(game.TimePulse.GameGlobalDateTime, before);
        Assert.AreEqual(game.TimePulse.GameGlobalDateTime, dest.StarSysDateTime);

        TestContext.WriteLine($"OK — time advanced to {game.TimePulse.GameGlobalDateTime:u}; dest bodies still {CountBodies(dest)}");
        foreach (var e in DebugTraceLog.Snapshot().TakeLast(20))
            TestContext.WriteLine($"[trace {e.Level}] {e.Category}: {e.Message}");
    }

    private static string DescribeSystem(StarSystem sys)
    {
        int stars = sys.GetAllEntitiesWithDataBlob<StarInfoDB>().Count();
        int bodies = CountBodies(sys);
        int anomalies = CountAnomalies(sys);
        int jps = sys.GetAllEntitiesWithDataBlob<JumpPointDB>().Count();
        var byType = sys.GetAllDataBlobsOfType<SystemBodyInfoDB>()
            .GroupBy(b => b.BodyType)
            .OrderBy(g => g.Key.ToString())
            .Select(g => $"{g.Key}={g.Count()}");
        return $"{sys.NameDB?.DefaultName}: stars={stars}, bodies={bodies}, anomalies={anomalies}, jps={jps}, [{string.Join(", ", byType)}]";
    }

    private static int CountBodies(StarSystem sys)
        => sys.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>().Count();

    private static int CountAnomalies(StarSystem sys)
        => sys.GetAllEntitiesWithDataBlob<JPSurveyableDB>().Count();

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
