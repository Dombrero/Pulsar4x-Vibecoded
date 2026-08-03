using System;
using System.Linq;
using Pulsar4X.Colonies;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Galaxy;
using Pulsar4X.Industry;
using Pulsar4X.Interfaces;
using Pulsar4X.Movement;
using Pulsar4X.Orbital;
using Pulsar4X.Sensors;
using Pulsar4X.Technology;

namespace Pulsar4X.Energy
{
    /// <summary>
    /// Hourly colony power tick: plants charge batteries, demand drains them, efficiency is updated.
    /// Runs with an earlier offset than mining so AutoMines see fresh efficiency.
    /// </summary>
    public class ColonyPowerProcessor : IHotloopProcessor
    {
        /// <summary>
        /// Industry lines and research labs draw this fraction of nameplate demand while idle
        /// (no production jobs / empty tech queue).
        /// </summary>
        public const double IdleDemandFactor = 0.1;

        public TimeSpan RunFrequency => TimeSpan.FromHours(1);
        public TimeSpan FirstRunOffset => TimeSpan.FromMinutes(30);
        public Type GetParameterType => typeof(ColonyPowerDB);

        public void Init(Game game) { }

        public int ProcessManager(EntityManager manager, int deltaSeconds)
        {
            var entities = manager.GetAllEntitiesWithDataBlob<ColonyPowerDB>();
            foreach (var entity in entities)
                ProcessEntity(entity, deltaSeconds);
            return entities.Count;
        }

        public void ProcessEntity(Entity entity, int deltaSeconds)
        {
            if (!entity.TryGetDataBlob<ColonyPowerDB>(out var power)
                || !entity.HasDataBlob<ColonyInfoDB>())
                return;

            RecalcAbilities(entity);
            power = entity.GetDataBlob<ColonyPowerDB>();

            double seconds = Math.Max(1, deltaSeconds);

            if (power.DemandKW <= 0)
            {
                power.PowerEfficiency = 1.0;
                double gainKJ = power.GenerationKW * seconds;
                power.EnergyStoredKJ = Math.Clamp(
                    power.EnergyStoredKJ + gainKJ,
                    0,
                    Math.Max(0, power.StorageCapacityKJ));
            }
            else
            {
                double neededKJ = power.DemandKW * seconds;
                double availableKJ = power.EnergyStoredKJ + power.GenerationKW * seconds;
                power.PowerEfficiency = Math.Clamp(availableKJ / neededKJ, 0, 1);

                double consumedKJ = neededKJ * power.PowerEfficiency;
                double producedKJ = power.GenerationKW * seconds;
                power.EnergyStoredKJ = Math.Clamp(
                    power.EnergyStoredKJ + producedKJ - consumedKJ,
                    0,
                    Math.Max(0, power.StorageCapacityKJ));
            }

            power.LastProcessTime = entity.StarSysDateTime;

            double dockKW = 0;
            if (entity.Manager != null)
            {
                foreach (var ship in entity.Manager.GetAllEntitiesWithDataBlob<EnergyRechargeDB>())
                {
                    if (ship.TryGetDataBlob<EnergyRechargeDB>(out var recharge)
                        && recharge.ColonyEntityId == entity.Id)
                        dockKW += recharge.RateKW;
                }
            }

            power.RecordHistogramSample(
                power.GenerationKW, power.DemandKW, dockKW, power.EnergyStoredKJ);
        }

        /// <summary>Re-sums battery capacity, plant output and demand from installed components.</summary>
        public static void RecalcAbilities(Entity colony)
        {
            if (!colony.HasDataBlob<ColonyInfoDB>())
                return;

            if (!colony.TryGetDataBlob<ComponentInstancesDB>(out var instances))
                return;

            PowerGenerationAtb.EnsureColonyPower(colony);
            var power = colony.GetDataBlob<ColonyPowerDB>();

            double capacity = 0;
            double batteryChargeRate = 0;
            double generation = 0;
            double demand = 0;

            DateTime at = colony.StarSysDateTime;

            if (instances.TryGetComponentsByAttribute<EnergyStoreAtb>(out var batteries))
            {
                foreach (var instance in batteries)
                {
                    if (!instance.IsEnabled)
                        continue;
                    var atb = instance.Design.GetAttribute<EnergyStoreAtb>();
                    double health = instance.HealthPercent;
                    capacity += atb.MaxStore * health;
                    batteryChargeRate += atb.MaxChargeRateKW * health;
                }
            }

            if (instances.TryGetComponentsByAttribute<PowerGenerationAtb>(out var plants))
            {
                foreach (var instance in plants)
                {
                    if (!instance.IsEnabled)
                        continue;
                    var atb = instance.Design.GetAttribute<PowerGenerationAtb>();
                    generation += ComputePlantOutputKW(colony, atb, at) * instance.HealthPercent;
                }
            }

            if (instances.TryGetComponentsByAttribute<PowerDemandAtb>(out var loads))
            {
                foreach (var instance in loads)
                    demand += GetInstanceDemandKW(colony, instance);
            }

            power.StorageCapacityKJ = capacity;
            power.BatteryChargeRateKW = batteryChargeRate;
            power.DockChargeRateKW = batteryChargeRate + ColonyPowerDB.PortDockBonusKW;
            power.GenerationKW = generation;
            power.DemandKW = demand;

            if (power.EnergyStoredKJ > power.StorageCapacityKJ)
                power.EnergyStoredKJ = power.StorageCapacityKJ;

            if (power.DemandKW <= 0)
                power.PowerEfficiency = 1.0;
        }

        internal static double ComputePlantOutputKW(Entity colony, PowerGenerationAtb atb, DateTime at)
        {
            return atb.Profile switch
            {
                PowerGenProfile.SolarSine => atb.PowerOutputKW * SolarFactor(colony),
                PowerGenProfile.WindRng => atb.PowerOutputKW * WindFactor(colony, at),
                _ => atb.PowerOutputKW,
            };
        }

        /// <summary>
        /// Colony solar irradiance relative to a 1 AU reference, using the same
        /// attenuated star-emission model as ship solar panels.
        /// Nameplate plant kW is defined at 1.0 (Earth-equivalent).
        /// </summary>
        private static double SolarFactor(Entity colony)
        {
            if (!colony.TryGetDataBlob<ColonyInfoDB>(out var info))
                return 0;
            if (!info.PlanetEntity.TryGetDataBlob<PositionDB>(out var planetPos))
                return 0;

            double colonyFlux = 0;
            double referenceFlux = 0;
            const double oneAU_m = 149597870700.0; // Distance.AuToMt(1)

            foreach (var star in colony.Manager.GetAllEntitiesWithDataBlob<StarInfoDB>())
            {
                if (!star.TryGetDataBlob<SensorProfileDB>(out var profile))
                    continue;
                if (!star.TryGetDataBlob<PositionDB>(out var starPos))
                    continue;

                double distance = Vector3.Distance(planetPos.AbsolutePosition, starPos.AbsolutePosition);
                if (distance <= 0)
                    continue;

                colonyFlux += SumFlux(SensorTools.AttenuatedForDistanceList(profile, distance, 0.1));
                referenceFlux += SumFlux(SensorTools.AttenuatedForDistanceList(profile, oneAU_m, 0.1));
            }

            if (referenceFlux <= 0 || colonyFlux <= 0)
                return 0;

            // Cap boost for very close orbits so plants stay balanceable.
            return Math.Clamp(colonyFlux / referenceFlux, 0, 10);
        }

        private static double SumFlux(System.Collections.Generic.List<EMData> emissions)
        {
            double total = 0;
            foreach (var em in emissions)
                total += em.Magnitude;
            return total;
        }

        private static double WindFactor(Entity colony, DateTime at)
        {
            int day = at.DayOfYear + at.Year * 366;
            int seed = unchecked(colony.Id * 397) ^ day;
            var rng = new Random(seed);
            return 0.2 + rng.NextDouble() * 0.8;
        }

        /// <summary>Power efficiency for mining / other consumers (1.0 if no power-gated demand).</summary>
        public static double GetPowerEfficiency(Entity colony)
        {
            if (colony.TryGetDataBlob<ColonyPowerDB>(out var power) && power.DemandKW > 0)
                return power.PowerEfficiency;
            return 1.0;
        }

        /// <summary>
        /// Effective kW draw for one installed consumer, including idle scaling for
        /// industry lines and research labs.
        /// </summary>
        public static double GetInstanceDemandKW(Entity colony, ComponentInstance instance)
        {
            if (!instance.IsEnabled || !instance.Design.HasAttribute<PowerDemandAtb>())
                return 0;

            double nameplate = instance.Design.GetAttribute<PowerDemandAtb>().DemandKW * instance.HealthPercent;
            return nameplate * GetInstanceDemandFactor(colony, instance);
        }

        /// <summary>
        /// 1.0 when the facility is working (or is not activity-gated);
        /// <see cref="IdleDemandFactor"/> when an industry line or research lab is idle.
        /// </summary>
        public static double GetInstanceDemandFactor(Entity colony, ComponentInstance instance)
        {
            if (instance.Design.HasAttribute<IndustryAtb>())
                return IsIndustryLineWorking(colony, instance) ? 1.0 : IdleDemandFactor;

            if (instance.Design.HasAttribute<ResearchPointsAtbDB>())
                return IsResearchLabWorking(colony, instance) ? 1.0 : IdleDemandFactor;

            return 1.0;
        }

        /// <summary>
        /// True when this component's production line has incomplete jobs
        /// (queued, processing, or waiting on resources).
        /// </summary>
        public static bool IsIndustryLineWorking(Entity colony, ComponentInstance instance)
        {
            if (!colony.TryGetDataBlob<IndustryAbilityDB>(out var industry))
                return false;
            if (!industry.ProductionLines.TryGetValue(instance.UniqueID, out var line))
                return false;

            return line.Jobs.Any(j => j.Status != IndustryJobStatus.Completed);
        }

        /// <summary>
        /// True when the lab's spawned researcher entity has a non-empty tech queue.
        /// </summary>
        public static bool IsResearchLabWorking(Entity colony, ComponentInstance instance)
        {
            if (!instance.Design.HasAttribute<ResearchPointsAtbDB>())
                return false;
            if (instance.SpawnedEntityId < 0 || colony.Manager == null)
                return false;
            if (!colony.Manager.TryGetEntityById(instance.SpawnedEntityId, out var labEntity))
                return false;
            if (!labEntity.TryGetDataBlob<ResearcherDB>(out var researcher))
                return false;

            return researcher.TechQueue.TryPeek(out _);
        }
    }
}
