using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Factories;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Modding;
using Pulsar4X.People;

namespace Pulsar4X.Tests;

[TestFixture]
internal class TutorialModStartTests
{
    const string TutorialColonyId = "tutorial-start-guided-economy";

    static ModDataStore LoadBasemodAndTutorial()
    {
        var modLoader = new ModLoader();
        var modData = new ModDataStore();
        modLoader.LoadModManifest("Data/basemod/modInfo.json", modData);
        modLoader.LoadModManifest("Data/tutorial-mod/modInfo.json", modData);
        return modData;
    }

    [Test]
    public void TutorialColony_IsRegistered_WhenBothModsLoaded()
    {
        var modData = LoadBasemodAndTutorial();
        Assert.That(modData.Colonies.ContainsKey(TutorialColonyId), Is.True);
    }

    [Test]
    public void TutorialColony_CreateFromBlueprint_DoesNotThrow()
    {
        var modData = LoadBasemodAndTutorial();
        var settings = new NewGameSettings
        {
            MaxSystems = 2,
            DefaultSolStart = true,
            CreatePlayerFaction = true,
            EleStart = true,
        };
        var game = GameFactory.CreateGame(modData, settings);

        foreach (var systemId in new[] { "system-sol" })
            StarSystemFactory.LoadFromBlueprint(game, modData.Systems[systemId]);

        var startingSystem = game.Systems.First(s => s.ManagerID == "system-sol");
        var startingBodyBlueprint = modData.SystemBodies["planet-earth"];
        Entity? startingBody = null;
        foreach (var systemBody in startingSystem.GetAllDataBlobsOfType<SystemBodyInfoDB>())
        {
            if (systemBody.OwningEntity?.GetDefaultName()?.Equals(startingBodyBlueprint.Name) == true)
                startingBody = systemBody.OwningEntity;
        }
        Assert.That(startingBody, Is.Not.Null);

        var faction = FactionFactory.CreateBasicFaction(game, "Tutorial Test", "TUT", 100_000_000);
        Assert.That(faction, Is.Not.Null);

        var speciesId = modData.Species.Keys.First(k => modData.Species[k].Playable);
        var species = SpeciesFactory.CreateFromBlueprint(startingSystem, modData.Species[speciesId]);
        species.FactionOwnerID = faction!.Id;

        var colonyBp = modData.Colonies[TutorialColonyId];
        Assert.DoesNotThrow(() =>
            ColonyFactory.CreateFromBlueprint(game, faction, species, startingSystem, startingBody!, colonyBp));

        Assert.That(startingBody!.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(faction.Id), Is.True,
            "Starting colony world must be surveyed so fog-of-war does not grey Earth.");

        Entity? luna = null;
        foreach (var systemBody in startingSystem.GetAllDataBlobsOfType<SystemBodyInfoDB>())
        {
            if (systemBody.OwningEntity?.GetDefaultName()?.Equals("Luna") == true)
                luna = systemBody.OwningEntity;
        }
        Assert.That(luna, Is.Not.Null);
        Assert.That(luna!.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(faction.Id), Is.False,
            "Moons of the homeworld stay unsurveyed until surveyed.");
    }

    [Test]
    public void SyncAllColonyWorldSurveys_resolves_stub_PlanetEntity_without_Manager()
    {
        var modData = LoadBasemodAndTutorial();
        var settings = new NewGameSettings
        {
            MaxSystems = 2,
            DefaultSolStart = true,
            CreatePlayerFaction = true,
        };
        var game = GameFactory.CreateGame(modData, settings);
        StarSystemFactory.LoadFromBlueprint(game, modData.Systems["system-sol"]);

        var startingSystem = game.Systems.First(s => s.ManagerID == "system-sol");
        Entity? earth = null;
        foreach (var systemBody in startingSystem.GetAllDataBlobsOfType<SystemBodyInfoDB>())
        {
            if (systemBody.OwningEntity?.GetDefaultName()?.Equals("Earth") == true)
                earth = systemBody.OwningEntity;
        }
        Assert.That(earth, Is.Not.Null);

        var faction = FactionFactory.CreateBasicFaction(game, "Stub Test", "STB", 1);
        Assert.That(faction, Is.Not.Null);
        var speciesId = modData.Species.Keys.First(k => modData.Species[k].Playable);
        var species = SpeciesFactory.CreateFromBlueprint(startingSystem, modData.Species[speciesId]);
        species.FactionOwnerID = faction!.Id;

        // Colony without MarkColonyWorldSurveyed path: leave Earth unsurveyed, then stub the link.
        var colony = ColonyFactory.CreateColony(faction, species, earth!, 1000);
        earth!.GetDataBlob<GeoSurveyableDB>().GeoSurveyStatus.Remove(faction.Id);
        Assert.That(earth.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(faction.Id), Is.False);

        // Simulate save/load: PlanetEntity becomes an id-only stub (no Manager).
        var stub = Newtonsoft.Json.JsonConvert.DeserializeObject<Entity>(
            $"{{\"Id\":{earth.Id},\"IsValid\":true}}")!;
        Assert.That(stub.Manager, Is.Null);
        Assert.That(stub.Id, Is.EqualTo(earth.Id));
        colony.GetDataBlob<ColonyInfoDB>().PlanetEntity = stub;

        ColonyFactory.SyncAllColonyWorldSurveys(game);

        Assert.That(earth.GetDataBlob<GeoSurveyableDB>().IsSurveyComplete(faction.Id), Is.True,
            "Sync must resolve live Earth by id even when PlanetEntity is a manager-less stub.");
    }
}
