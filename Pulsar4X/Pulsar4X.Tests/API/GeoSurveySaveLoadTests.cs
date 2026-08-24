using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Factories;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Modding;
using Pulsar4X.Names;

namespace Pulsar4X.Tests;

[TestFixture]
public class GeoSurveySaveLoadTests
{
    [Test]
    public void SafeDictionary_int_uint_roundtrips_with_game_json_settings()
    {
        var status = new SafeDictionary<int, uint> { [42] = 0, [7] = 1500u };
        var db = new GeoSurveyableDB { PointsRequired = 1000, GeoSurveyStatus = status };

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            TypeNameHandling = TypeNameHandling.Objects,
            ContractResolver = new NonPublicResolver(),
        };

        string json = JsonConvert.SerializeObject(db, settings);
        TestContext.WriteLine(json);

        var loaded = JsonConvert.DeserializeObject<GeoSurveyableDB>(json, settings);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.GeoSurveyStatus.Count, Is.EqualTo(2), json);
        Assert.That(loaded.IsSurveyComplete(42), Is.True, json);
        Assert.That(loaded.HasSurveyStarted(7), Is.True);
        Assert.That(loaded.GeoSurveyStatus[7], Is.EqualTo(1500u));
    }

    [Test]
    public void Game_SaveLoad_preserves_body_geo_survey_status()
    {
        var modLoader = new ModLoader();
        var modData = new ModDataStore();
        modLoader.LoadModManifest("Data/basemod/modInfo.json", modData);

        var settings = new NewGameSettings
        {
            MaxSystems = 1,
            DefaultSolStart = true,
            CreatePlayerFaction = true,
        };
        var game = GameFactory.CreateGame(modData, settings);
        StarSystemFactory.LoadFromBlueprint(game, modData.Systems["system-sol"]);

        var sol = game.Systems.First(s => s.ManagerID == "system-sol");
        Entity? earth = null;
        Entity? mars = null;
        Entity? mercury = null;
        foreach (var info in sol.GetAllDataBlobsOfType<SystemBodyInfoDB>())
        {
            string? n = info.OwningEntity?.GetDefaultName();
            if (n == "Earth") earth = info.OwningEntity;
            if (n == "Mars") mars = info.OwningEntity;
            if (n == "Mercury") mercury = info.OwningEntity;
        }
        Assert.That(earth, Is.Not.Null);
        Assert.That(mars, Is.Not.Null);
        Assert.That(mercury, Is.Not.Null);

        var faction = FactionFactory.CreateBasicFaction(game, "SurveySave", "SVY", 1)!;
        int fid = faction.Id;

        SystemBodyFactory.EnsureGeoSurveyable(earth!);
        SystemBodyFactory.EnsureGeoSurveyable(mars!);
        SystemBodyFactory.EnsureGeoSurveyable(mercury!);

        earth!.GetDataBlob<GeoSurveyableDB>().GeoSurveyStatus[fid] = 0;
        mars!.GetDataBlob<GeoSurveyableDB>().GeoSurveyStatus[fid] = 0;
        // Mercury intentionally unsurveyed
        mercury!.GetDataBlob<GeoSurveyableDB>().GeoSurveyStatus.Remove(fid);

        Assert.That(earth.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(fid), Is.True);
        Assert.That(mars.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(fid), Is.True);
        Assert.That(mercury.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(fid), Is.False);

        string json = Game.Save(game);
        var loaded = Game.Load(json);

        var loadedSol = loaded.Systems.First(s => s.ManagerID == "system-sol");
        Entity? le = null, lm = null, lme = null;
        foreach (var info in loadedSol.GetAllDataBlobsOfType<SystemBodyInfoDB>())
        {
            string? n = info.OwningEntity?.GetDefaultName();
            if (n == "Earth") le = info.OwningEntity;
            if (n == "Mars") lm = info.OwningEntity;
            if (n == "Mercury") lme = info.OwningEntity;
        }
        Assert.That(le, Is.Not.Null);
        Assert.That(lm, Is.Not.Null);
        Assert.That(lme, Is.Not.Null);

        // Faction id must still match after load (same entity id).
        Assert.That(loaded.Factions.ContainsKey(fid), Is.True, "Faction id changed on load");

        var earthGeo = le!.GetDataBlob<GeoSurveyableDB>();
        var marsGeo = lm!.GetDataBlob<GeoSurveyableDB>();
        var mercuryGeo = lme!.GetDataBlob<GeoSurveyableDB>();

        TestContext.WriteLine(
            $"Earth keys=[{string.Join(",", earthGeo.GeoSurveyStatus.Select(kv => kv.Key + ":" + kv.Value))}]");
        TestContext.WriteLine(
            $"Mars keys=[{string.Join(",", marsGeo.GeoSurveyStatus.Select(kv => kv.Key + ":" + kv.Value))}]");
        TestContext.WriteLine(
            $"Mercury keys=[{string.Join(",", mercuryGeo.GeoSurveyStatus.Select(kv => kv.Key + ":" + kv.Value))}]");

        Assert.That(earthGeo.IsSurveyComplete(fid), Is.True, "Earth survey must survive save/load");
        Assert.That(marsGeo.IsSurveyComplete(fid), Is.True, "Mars survey must survive save/load");
        Assert.That(mercuryGeo.IsSurveyComplete(fid), Is.False, "Mercury must stay unsurveyed");

        // JSON must actually contain the faction keys (catches empty-dict serialization).
        Assert.That(json, Does.Contain($"\"{fid}\": 0").Or.Contain($"\"{fid}\":0"),
            "Save JSON must persist GeoSurveyStatus entries, not empty dictionaries");
    }
}
