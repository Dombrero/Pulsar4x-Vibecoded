using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Galaxy;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Extends a real client save with a 5-ship Survey Fleet, then writes order_rework_08_08_26.sav.
    /// </summary>
    [TestFixture]
    public class GenerateOrderReworkSaveTest
    {
        private static readonly string SourceSavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulsar4X", "Pulsar4X", "Saves",
            "goal integration test 1 - 2050-01-08_20-00-00.sav");

        [Test]
        public void Write_order_rework_08_08_26_from_goal_integration_save()
        {
            Assert.That(File.Exists(SourceSavePath), Is.True, $"Missing base save: {SourceSavePath}");

            var game = Game.Load(File.ReadAllText(SourceSavePath));
            game.Settings.EnforceSingleThread = true;

            var faction = game.Factions.Values
                .Where(f => f.Id != game.GameMasterFaction.Id)
                .First(f => f.TryGetDataBlob<FactionInfoDB>(out var info) && info.KnownSystems.Count > 0);
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();

            string systemId = factionInfo.KnownSystems[0];
            var system = game.Systems.First(s => s.ID == systemId);

            Entity parentBody = factionInfo.Colonies
                .Select(c => c.GetDataBlob<ColonyInfoDB>().PlanetEntity)
                .FirstOrDefault(p => p is { IsValid: true } && p.Manager == system)
                ?? system.GetAllEntitiesWithDataBlob<SystemBodyInfoDB>()
                    .FirstOrDefault(b => b.GetOwnersName().Contains("Earth", StringComparison.OrdinalIgnoreCase))
                ?? system.GetFirstEntityWithDataBlob<StarInfoDB>();

            Assert.That(parentBody.IsValid, Is.True, "Need a planet/star to park the survey fleet.");

            var design = PickShipDesign(factionInfo);
            Assert.That(design, Is.Not.Null, "Faction has no ship designs to clone.");

            var surveyFleet = FleetFactory.Create(system, faction.Id, "Survey Fleet");
            FleetHierarchy.EnsureFleetRegistered(faction, surveyFleet);
            var fleetDB = surveyFleet.GetDataBlob<FleetDB>();

            var ships = new List<Entity>();
            for (int i = 1; i <= 5; i++)
            {
                double angle = i * (Math.PI * 2.0 / 5.0);
                var ship = ShipFactory.CreateShip(design!, faction, parentBody, angle, $"Surveyor {i}");
                if (!ship.HasDataBlob<GeoSurveyAbilityDB>())
                    ship.SetDataBlob(new GeoSurveyAbilityDB { Speed = 40 });
                if (!ship.HasJPSurveyAbililty())
                    ship.SetDataBlob(new JPSurveyAbilityDB { Speed = 40 });
                TopOffFuel(ship, factionInfo);
                fleetDB.AddChild(ship);
                ships.Add(ship);
            }

            fleetDB.FlagShipID = ships[0].Id;

            // Standing only if empty — don't stomp whatever the base save already had.
            if (fleetDB.StandingOrders.Count == 0)
            {
                fleetDB.StandingOrders.Add(MakeStanding(
                    "Refuel",
                    new FuelCondition(30f, ComparisonType.LessThan),
                    RefuelAction.CreateCommand(faction.Id, surveyFleet)));
                fleetDB.StandingOrders.Add(MakeStanding(
                    "Geo Survey",
                    new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan),
                    MoveToNearestGeoSurveyAction.CreateCommand(faction.Id, surveyFleet)));
                fleetDB.StandingOrders.Add(MakeStanding(
                    "Grav Survey",
                    new UnsurveyedAnomalyCondition(0f, ComparisonType.GreaterThan),
                    MoveToNearestGravSurveyAction.CreateCommand(faction.Id, surveyFleet)));
            }

            string json = Game.Save(game);
            var reloaded = Game.Load(json);
            var reloadFaction = reloaded.Factions[faction.Id];
            var reloadInfo = reloadFaction.GetDataBlob<FactionInfoDB>();
            Assert.That(reloadInfo.KnownSystems.Count, Is.GreaterThan(0));
            Assert.That(reloadInfo.Colonies.Count, Is.GreaterThan(0));

            var projector = new GameProjector(reloaded);
            Assert.That(projector.ProjectSystem(reloadInfo.KnownSystems[0], faction.Id), Is.Not.Null);
            var fleets = projector.ProjectFleetHierarchy(faction.Id).Fleets;
            Assert.That(fleets.Any(f => f.Name.Contains("Survey", StringComparison.OrdinalIgnoreCase)), Is.True);

            string fileName = "order_rework_08_08_26.sav";
            var destinations = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Pulsar4X", "Pulsar4X", "Saves", fileName),
                Path.GetFullPath(Path.Combine(
                    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Saves", fileName)),
            };

            foreach (var path in destinations.Distinct())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                TestContext.WriteLine($"Wrote save: {path} ({new FileInfo(path).Length} bytes)");
            }

            TestContext.WriteLine(
                $"Base={Path.GetFileName(SourceSavePath)}; Survey Fleet ships: {string.Join(", ", ships.Select(s => s.GetOwnersName()))}");
            Assert.That(new FileInfo(destinations[0]).Length, Is.GreaterThan(4_000_000),
                "Extended save should stay near the size of a real game save, not a tiny hand-built one.");
        }

        private static ShipDesign? PickShipDesign(FactionInfoDB info)
        {
            // Prefer a design that already has geo survey if any.
            foreach (var design in info.ShipDesigns.Values)
            {
                if (design?.DamageProfileDB == null)
                    continue;
                if (design.Components.Any(c =>
                        c.design?.Name?.Contains("survey", StringComparison.OrdinalIgnoreCase) == true
                        || c.design?.UniqueID?.Contains("geoSurvey", StringComparison.OrdinalIgnoreCase) == true
                        || c.design?.UniqueID?.Contains("jpSurvey", StringComparison.OrdinalIgnoreCase) == true))
                    return design;
            }

            return info.ShipDesigns.Values.FirstOrDefault(d => d?.DamageProfileDB != null);
        }

        private static void TopOffFuel(Entity ship, FactionInfoDB factionInfo)
        {
            try
            {
                var (fuel, _) = ship.GetFuelInfo(factionInfo.Data.CargoGoods);
                if (fuel == null || !ship.TryGetDataBlob<CargoStorageDB>(out var store))
                    return;
                long free = CargoMath.GetFreeUnitSpace(store, fuel, includeEscro: false);
                if (free > 0)
                    store.AddCargoByUnit(fuel, free);
            }
            catch
            {
                // Design may lack tanks — still fine for geo via ability blob.
            }
        }

        private static ConditionalOrder MakeStanding(string name, ComparisonCondition condition, EntityCommand action)
        {
            var cond = new CompoundCondition();
            cond.ConditionItems.Add(new ConditionItem(condition));
            return new ConditionalOrder(cond, new SafeList<EntityCommand> { action }) { Name = name };
        }
    }
}
