using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Factories;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
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
    }
}
