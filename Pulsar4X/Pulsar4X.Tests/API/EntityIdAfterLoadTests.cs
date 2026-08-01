using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Ships;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class EntityIdAfterLoadTests
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves", "Testsavefile 5_ship_prod.sav");

        [Test]
        public void Load_resyncs_entity_id_generator_above_existing_ids()
        {
            if (!File.Exists(SavePath))
                Assert.Ignore($"Save not found: {SavePath}");

            // Simulate a cold start: counter at 0 like a fresh process before load.
            EntityIDGenerator.Reset(0);

            var game = Game.Load(File.ReadAllText(SavePath));
            Assert.That(game, Is.Not.Null);

            int maxExisting = game!.GlobalManagerDictionary.Values
                .SelectMany(m => m.GetAllEntites())
                .Select(e => e.Id)
                .DefaultIfEmpty(-1)
                .Max();

            Assert.That(EntityIDGenerator.NextId, Is.GreaterThan(maxExisting),
                "after load, NextId must be past every entity already in the save");

            var faction = game.Factions.Values.First(f => f.Id != game.GameMasterFaction.Id);
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();
            var design = (ShipDesign)factionInfo.IndustryDesigns["default-ship-design-surveyor"];
            var colony = factionInfo.Colonies.First();
            var parent = colony.GetSOIParentEntity()
                         ?? colony.GetDataBlob<ColonyInfoDB>().PlanetEntity;

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 5; i++)
                    ShipFactory.CreateShip(design, faction, parent, $"IdTest Ship {i}");
            }, "creating ships after load must not hit 'Entity ID already exists'");
        }
    }
}
