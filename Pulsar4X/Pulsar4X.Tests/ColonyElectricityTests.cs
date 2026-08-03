using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Galaxy;
using Pulsar4X.Industry;
using Pulsar4X.Names;
using Pulsar4X.Orbital;
using Pulsar4X.DataStructures;
using Pulsar4X.Movement;
using Pulsar4X.Factions;
using Pulsar4X.People;
using Pulsar4X.Technology;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class ColonyElectricityTests
    {
        private Game _game = null!;

        [SetUp]
        public void SetUp()
        {
            _game = TestingUtilities.CreateTestUniverse(1, new DateTime(2050, 1, 1));
            _game.Settings.EnforceSingleThread = true;
        }

        [Test]
        public void Electricity_material_is_unconstructable_in_basemod()
        {
            var materials = _game.StartingGameData.ProcessedMaterials;
            Assert.That(materials.ContainsKey("electricity"), Is.True);
            var elec = materials["electricity"];
            Assert.That(string.IsNullOrEmpty(elec.IndustryTypeID), Is.True,
                "electricity must not be refinable");
        }

        [Test]
        public void EnergyStoreAtb_on_colony_uses_ColonyPowerDB_not_EnergyGenAbilityDB()
        {
            var faction = _game.GameMasterFaction;
            var system = _game.Systems[0];

            var planet = Entity.Create(faction.Id);
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("Body", faction.Id, "Body"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new MassVolumeDB { MassDry = 1e24 },
                new SystemBodyInfoDB { LengthOfDay = TimeSpan.FromHours(24) },
            });

            var colony = Entity.Create(faction.Id);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new NameDB("Colony", faction.Id, "Colony"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new ColonyInfoDB(new Dictionary<int, long>(), planet),
                new ComponentInstancesDB(),
                new MassVolumeDB(),
            });

            var design = new ComponentDesign
            {
                UniqueID = "test-battery",
                Name = "Test Battery",
                ComponentMountType = ComponentMountType.PlanetInstallation,
            };
            design.AttributesByType[typeof(EnergyStoreAtb)] = new EnergyStoreAtb("electricity", 1_000_000);

            colony.AddComponent(design);

            Assert.That(colony.HasDataBlob<ColonyPowerDB>(), Is.True);
            Assert.That(colony.HasDataBlob<EnergyGenAbilityDB>(), Is.False);

            var power = colony.GetDataBlob<ColonyPowerDB>();
            Assert.That(power.StorageCapacityKJ, Is.EqualTo(1_000_000).Within(1));
            Assert.That(power.DockChargeRateKW, Is.GreaterThan(ColonyPowerDB.PortDockBonusKW - 1));
        }

        [Test]
        public void EnergyStoreAtb_on_ship_still_uses_EnergyGenAbilityDB()
        {
            var faction = _game.GameMasterFaction;
            var system = _game.Systems[0];

            var ship = Entity.Create(faction.Id);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("Ship", faction.Id, "Ship"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new ComponentInstancesDB(),
                new MassVolumeDB(),
            });

            var design = new ComponentDesign { UniqueID = "ship-bat", Name = "Ship Bat" };
            design.AttributesByType[typeof(EnergyStoreAtb)] = new EnergyStoreAtb("electricity", 5000);
            ship.AddComponent(design);

            Assert.That(ship.HasDataBlob<EnergyGenAbilityDB>(), Is.True);
            Assert.That(ship.HasDataBlob<ColonyPowerDB>(), Is.False);
            Assert.That(ship.GetDataBlob<EnergyGenAbilityDB>().EnergyStoreMax["electricity"], Is.EqualTo(5000));
        }

        [Test]
        public void Battery_charge_rate_targets_full_charge_hours()
        {
            var atb = new EnergyStoreAtb("electricity", 1_000_000);
            double expected = 1_000_000 / (EnergyStoreAtb.FullChargeHours * 3600.0);
            Assert.That(atb.MaxChargeRateKW, Is.EqualTo(expected).Within(1e-9));
            Assert.That(EnergyStoreAtb.FullChargeHours, Is.EqualTo(24.0));
        }

        [Test]
        public void Recharge_respects_colony_reserve_and_rate_cap()
        {
            var faction = _game.GameMasterFaction;
            var system = _game.Systems[0];

            var colony = Entity.Create(faction.Id);
            system.AddEntity(colony, new List<BaseDataBlob>
            {
                new NameDB("C", faction.Id, "C"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new ColonyPowerDB
                {
                    StorageCapacityKJ = 100_000,
                    EnergyStoredKJ = 100_000,
                    DockChargeRateKW = 10,
                    BatteryChargeRateKW = 5,
                },
            });

            var ship = Entity.Create(faction.Id);
            system.AddEntity(ship, new List<BaseDataBlob>
            {
                new NameDB("S", faction.Id, "S"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new EnergyGenAbilityDB(_game.TimePulse.GameGlobalDateTime)
                {
                    EnergyStored = new Dictionary<string, double> { ["electricity"] = 0 },
                    EnergyStoreMax = new Dictionary<string, double> { ["electricity"] = 50_000 },
                },
            });

            double transferred = EnergyRechargeHelper.TransferEnergy(colony, ship, rateKW: 1000, deltaSeconds: 10);
            Assert.That(transferred, Is.EqualTo(10_000).Within(0.1));

            var power = colony.GetDataBlob<ColonyPowerDB>();
            Assert.That(power.EnergyStoredKJ, Is.EqualTo(90_000).Within(0.1));

            power.EnergyStoredKJ = 20_000;
            transferred = EnergyRechargeHelper.TransferEnergy(colony, ship, 1000, 10);
            Assert.That(transferred, Is.EqualTo(0));
        }

        [Test]
        public void Solar_plant_without_star_outputs_zero()
        {
            var faction = _game.GameMasterFaction;
            // Use GlobalManager which has no stars.
            var manager = _game.GlobalManager;

            var planet = Entity.Create(faction.Id);
            manager.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("P", faction.Id, "P"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new SystemBodyInfoDB { LengthOfDay = TimeSpan.FromHours(24) },
            });

            var colony = Entity.Create(faction.Id);
            manager.AddEntity(colony, new List<BaseDataBlob>
            {
                new NameDB("C", faction.Id, "C"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                new ColonyInfoDB(new Dictionary<int, long>(), planet),
                new ComponentInstancesDB(),
                new MassVolumeDB(),
            });

            var design = new ComponentDesign
            {
                UniqueID = "solar-plant",
                Name = "Solar",
                ComponentMountType = ComponentMountType.PlanetInstallation,
            };
            design.AttributesByType[typeof(PowerGenerationAtb)] =
                new PowerGenerationAtb(1000, PowerGenProfile.SolarSine);

            colony.AddComponent(design);
            ColonyPowerProcessor.RecalcAbilities(colony);

            Assert.That(colony.TryGetDataBlob<ColonyPowerDB>(out var power) && power.GenerationKW == 0, Is.True);
        }

        [Test]
        public void Industry_purge_removes_electricity_jobs()
        {
            var faction = _game.GameMasterFaction;
            var factionInfo = faction.GetDataBlob<FactionInfoDB>();
            factionInfo.IndustryDesigns["electricity"] = new ProcessedMaterial
            {
                UniqueID = "electricity",
                IndustryTypeID = "",
                Name = "Electricity",
                ResourceCosts = new Dictionary<string, long>(),
            };

            var industry = new IndustryAbilityDB("line1", new IndustryAbilityDB.ProductionLine
            {
                Name = "Refinery",
                Jobs = new List<IndustryJob>
                {
                    new IndustryJob(factionInfo, "electricity")
                },
            });

            IndustryTools.PurgeUnconstructableElectricityJobs(industry, factionInfo);

            Assert.That(factionInfo.IndustryDesigns.ContainsKey("electricity"), Is.False);
            Assert.That(industry.ProductionLines["line1"].Jobs, Is.Empty);
        }

        [Test]
        public void Earth_colony_blueprint_creates_without_cargo_id_collision()
        {
            Assert.That(_game.StartingGameData.Colonies.ContainsKey("colony-earth"), Is.True);

            var faction = FactionFactory.CreateFaction(_game, "Quickstart Test Faction");
            var species = SpeciesFactory.CreateSpeciesHuman(faction, _game.GlobalManager);
            var system = _game.Systems[0];

            var planet = Entity.Create(faction.Id);
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("Earth", faction.Id, "Earth"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                MassVolumeDB.NewFromMassAndRadius_m(5.9726e24, 6378100),
                new SystemBodyInfoDB { LengthOfDay = TimeSpan.FromHours(24) },
            });

            Assert.DoesNotThrow(() =>
                ColonyFactory.CreateFromBlueprint(
                    _game,
                    faction,
                    species,
                    system,
                    planet,
                    _game.StartingGameData.Colonies["colony-earth"]));

            var colony = faction.GetDataBlob<FactionInfoDB>().Colonies[0];
            Assert.That(colony.TryGetDataBlob<ColonyPowerDB>(out var power), Is.True);
            Assert.That(power!.StorageCapacityKJ, Is.GreaterThan(0));
            Assert.That(power.GenerationKW, Is.GreaterThan(0));

            var instances = colony.GetDataBlob<ComponentInstancesDB>();
            Assert.That(instances.TryGetComponentsByAttribute<PowerDemandAtb>(out var loads), Is.True);
            Assert.That(loads.Any(i => i.Design.UniqueID == "default-design-factory"), Is.True,
                "Factory should draw colony power");
            Assert.That(loads.Any(i => i.Design.UniqueID == "default-design-refinery"), Is.True,
                "Refinery should draw colony power");

            double factoryNameplate = loads
                .Where(i => i.Design.UniqueID == "default-design-factory")
                .Sum(i => i.Design.GetAttribute<PowerDemandAtb>().DemandKW);
            double refineryNameplate = loads
                .Where(i => i.Design.UniqueID == "default-design-refinery")
                .Sum(i => i.Design.GetAttribute<PowerDemandAtb>().DemandKW);
            Assert.That(factoryNameplate, Is.GreaterThan(0));
            Assert.That(refineryNameplate, Is.GreaterThan(0));

            // Quickstart industry lines start idle → only idle fraction of factory/refinery draw.
            Assert.That(power.DemandKW, Is.LessThan(power.GenerationKW),
                $"Demand {power.DemandKW} kW should be below generation {power.GenerationKW} kW");
            Assert.That(power.DemandKW, Is.GreaterThan(0));
        }

        [Test]
        public void Industry_facility_draws_idle_fraction_until_it_has_work()
        {
            var faction = FactionFactory.CreateFaction(_game, "Idle Power Faction");
            var species = SpeciesFactory.CreateSpeciesHuman(faction, _game.GlobalManager);
            var system = _game.Systems[0];

            var planet = Entity.Create(faction.Id);
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("Body", faction.Id, "Body"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                MassVolumeDB.NewFromMassAndRadius_m(5.9726e24, 6378100),
                new SystemBodyInfoDB { LengthOfDay = TimeSpan.FromHours(24) },
            });

            ColonyFactory.CreateFromBlueprint(
                _game,
                faction,
                species,
                system,
                planet,
                _game.StartingGameData.Colonies["colony-earth"]);

            var colony = faction.GetDataBlob<FactionInfoDB>().Colonies[0];
            var instances = colony.GetDataBlob<ComponentInstancesDB>();
            Assert.That(instances.TryGetComponentsByAttribute<PowerDemandAtb>(out var loads), Is.True);

            var factory = loads.First(i => i.Design.UniqueID == "default-design-factory");
            double nameplate = factory.Design.GetAttribute<PowerDemandAtb>().DemandKW;

            Assert.That(ColonyPowerProcessor.IsIndustryLineWorking(colony, factory), Is.False);
            Assert.That(ColonyPowerProcessor.GetInstanceDemandFactor(colony, factory),
                Is.EqualTo(ColonyPowerProcessor.IdleDemandFactor));
            Assert.That(ColonyPowerProcessor.GetInstanceDemandKW(colony, factory),
                Is.EqualTo(nameplate * ColonyPowerProcessor.IdleDemandFactor).Within(1e-6));

            ColonyPowerProcessor.RecalcAbilities(colony);
            double idleColonyDemand = colony.GetDataBlob<ColonyPowerDB>().DemandKW;

            var factionInfo = faction.GetDataBlob<FactionInfoDB>();
            Assert.That(factionInfo.IndustryDesigns.ContainsKey("default-design-battery-25kg"), Is.True);
            var job = new IndustryJob(factionInfo, "default-design-battery-25kg");
            IndustryTools.AddJob(colony, factory.UniqueID, job);

            Assert.That(ColonyPowerProcessor.IsIndustryLineWorking(colony, factory), Is.True);
            Assert.That(ColonyPowerProcessor.GetInstanceDemandFactor(colony, factory), Is.EqualTo(1.0));
            Assert.That(ColonyPowerProcessor.GetInstanceDemandKW(colony, factory),
                Is.EqualTo(nameplate).Within(1e-6));

            ColonyPowerProcessor.RecalcAbilities(colony);
            double workingColonyDemand = colony.GetDataBlob<ColonyPowerDB>().DemandKW;
            Assert.That(workingColonyDemand, Is.GreaterThan(idleColonyDemand));
            Assert.That(workingColonyDemand - idleColonyDemand,
                Is.EqualTo(nameplate * (1.0 - ColonyPowerProcessor.IdleDemandFactor)).Within(1e-3));
        }

        [Test]
        public void Research_lab_draws_idle_fraction_until_tech_is_queued()
        {
            var faction = FactionFactory.CreateFaction(_game, "Lab Idle Power Faction");
            var species = SpeciesFactory.CreateSpeciesHuman(faction, _game.GlobalManager);
            var system = _game.Systems[0];

            var planet = Entity.Create(faction.Id);
            system.AddEntity(planet, new List<BaseDataBlob>
            {
                new NameDB("Body", faction.Id, "Body"),
                new PositionDB { AbsolutePosition = Vector3.Zero },
                MassVolumeDB.NewFromMassAndRadius_m(5.9726e24, 6378100),
                new SystemBodyInfoDB { LengthOfDay = TimeSpan.FromHours(24) },
            });

            ColonyFactory.CreateFromBlueprint(
                _game,
                faction,
                species,
                system,
                planet,
                _game.StartingGameData.Colonies["colony-earth"]);

            var colony = faction.GetDataBlob<FactionInfoDB>().Colonies[0];
            var instances = colony.GetDataBlob<ComponentInstancesDB>();
            Assert.That(instances.TryGetComponentsByAttribute<PowerDemandAtb>(out var loads), Is.True);

            var lab = loads.First(i => i.Design.UniqueID == "default-design-research-lab");
            double nameplate = lab.Design.GetAttribute<PowerDemandAtb>().DemandKW;

            Assert.That(ColonyPowerProcessor.IsResearchLabWorking(colony, lab), Is.False);
            Assert.That(ColonyPowerProcessor.GetInstanceDemandFactor(colony, lab),
                Is.EqualTo(ColonyPowerProcessor.IdleDemandFactor));
            Assert.That(ColonyPowerProcessor.GetInstanceDemandKW(colony, lab),
                Is.EqualTo(nameplate * ColonyPowerProcessor.IdleDemandFactor).Within(1e-6));

            Assert.That(system.TryGetEntityById(lab.SpawnedEntityId, out var labEntity), Is.True);
            var researcher = labEntity.GetDataBlob<ResearcherDB>();
            ResearchProcessor.AssignTech(researcher, "tech-panel-efficiency");

            Assert.That(ColonyPowerProcessor.IsResearchLabWorking(colony, lab), Is.True);
            Assert.That(ColonyPowerProcessor.GetInstanceDemandFactor(colony, lab), Is.EqualTo(1.0));
            Assert.That(ColonyPowerProcessor.GetInstanceDemandKW(colony, lab),
                Is.EqualTo(nameplate).Within(1e-6));
        }

        [Test]
        public void PowerGenerationAtb_and_PowerDemandAtb_roundtrip_via_Json()
        {
            var gen = new PowerGenerationAtb(2500, PowerGenProfile.SolarSine);
            var demand = new PowerDemandAtb(120);

            var genJson = Newtonsoft.Json.JsonConvert.SerializeObject(gen);
            var demandJson = Newtonsoft.Json.JsonConvert.SerializeObject(demand);

            var genLoaded = Newtonsoft.Json.JsonConvert.DeserializeObject<PowerGenerationAtb>(genJson);
            var demandLoaded = Newtonsoft.Json.JsonConvert.DeserializeObject<PowerDemandAtb>(demandJson);

            Assert.That(genLoaded, Is.Not.Null);
            Assert.That(genLoaded!.PowerOutputKW, Is.EqualTo(2500));
            Assert.That(genLoaded.Profile, Is.EqualTo(PowerGenProfile.SolarSine));
            Assert.That(demandLoaded, Is.Not.Null);
            Assert.That(demandLoaded!.DemandKW, Is.EqualTo(120));
        }
    }
}
