using System;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Engine;
using Pulsar4X.Factions;

namespace Pulsar4X.Tests;

[TestFixture]
public class ActiveSaveLoadSmokeTest
{
    [Test]
    public void SaveAndLoad_PreservesKnownSystems_AndLoadSelectsPlayerFaction()
    {
        var game = TestingUtilities.CreateTestUniverse(1, new DateTime(2050, 1, 1));
        game.Settings.EnforceSingleThread = true;

        var player = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
        Assert.That(player.TryGetDataBlob<FactionInfoDB>(out var preInfo), Is.True);
        preInfo!.KnownSystems.Clear();
        preInfo.KnownSystems.Add(game.Systems[0].ID);

        string json = Game.Save(game);
        Assert.That(json, Is.Not.Null.And.Not.Empty, "Save produced empty JSON");
        TestContext.WriteLine($"Save JSON length: {json.Length}");

        var loaded = Game.Load(json);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Systems, Is.Not.Empty);
        Assert.That(loaded.Factions, Is.Not.Empty);

        // Mimic (and improve) the host LoadGame faction pick: never GameMaster-only,
        // fall back to any faction that has known systems / any system id.
        var loadedPlayer = loaded.Factions.Values
            .Where(f => f.Id != loaded.GameMasterFaction.Id)
            .FirstOrDefault(f => f.TryGetDataBlob<FactionInfoDB>(out var i) && i.KnownSystems.Count > 0)
            ?? loaded.Factions.Values.FirstOrDefault(f => f.Id != loaded.GameMasterFaction.Id)
            ?? loaded.GameMasterFaction;

        Assert.That(loadedPlayer.TryGetDataBlob<FactionInfoDB>(out var postInfo), Is.True);
        Assert.That(postInfo!.KnownSystems, Is.Not.Empty, "KnownSystems lost after load — LoadGame would IndexOutOfRange");
        Assert.That(postInfo.KnownSystems[0], Is.EqualTo(game.Systems[0].ID));
    }

    [Test]
    public void SaveAndLoad_TimeStep_StillAdvances()
    {
        var game = TestingUtilities.CreateTestUniverse(1, new DateTime(2050, 1, 1));
        game.Settings.EnforceSingleThread = true;
        game.TimePulse.Ticklength = TimeSpan.FromDays(1);

        var beforeSave = game.TimePulse.GameGlobalDateTime;
        game.TimePulse.TimeStep();
        Assert.That(game.TimePulse.GameGlobalDateTime, Is.EqualTo(beforeSave + TimeSpan.FromDays(1)),
            "pre-save timestep should advance");

        string json = Game.Save(game);
        var loaded = Game.Load(json);
        Assert.That(loaded, Is.Not.Null);
        loaded!.Settings.EnforceSingleThread = true;
        loaded.TimePulse.Ticklength = TimeSpan.FromDays(1);

        var before = loaded.TimePulse.GameGlobalDateTime;
        Assert.DoesNotThrow(() => loaded.TimePulse.TimeStep(), "TimeStep after load threw");
        Assert.That(loaded.TimePulse.IsRunning, Is.False, "single-thread TimeStep should finish");
        Assert.That(loaded.TimePulse.GameGlobalDateTime, Is.EqualTo(before + TimeSpan.FromDays(1)),
            "time must advance after load");
    }
}
