using System;
using Newtonsoft.Json;
using Pulsar4X.Colonies;
using Pulsar4X.Components;
using Pulsar4X.Engine;
using Pulsar4X.Interfaces;

namespace Pulsar4X.Energy
{
    /// <summary>Colony power plant attribute. Installations with this atb feed <see cref="ColonyPowerDB"/>.</summary>
    public class PowerGenerationAtb : IComponentDesignAttribute
    {
        [JsonProperty]
        public double PowerOutputKW { get; private set; }

        [JsonProperty]
        public PowerGenProfile Profile { get; private set; }

        [JsonConstructor]
        private PowerGenerationAtb() { }

        public PowerGenerationAtb(double powerOutputKW, string profile)
        {
            PowerOutputKW = powerOutputKW;
            Profile = Enum.TryParse(profile, ignoreCase: true, out PowerGenProfile parsed)
                ? parsed
                : PowerGenProfile.Constant;
        }

        public PowerGenerationAtb(double powerOutputKW, PowerGenProfile profile)
        {
            PowerOutputKW = powerOutputKW;
            Profile = profile;
        }

        public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            if (!parentEntity.HasDataBlob<ColonyInfoDB>())
                return;

            EnsureColonyPower(parentEntity);
            ColonyPowerProcessor.RecalcAbilities(parentEntity);
        }

        public void OnComponentUninstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            if (!parentEntity.HasDataBlob<ColonyInfoDB>())
                return;

            ColonyPowerProcessor.RecalcAbilities(parentEntity);
        }

        internal static void EnsureColonyPower(Entity colony)
        {
            if (!colony.HasDataBlob<ColonyPowerDB>())
            {
                colony.SetDataBlob(new ColonyPowerDB
                {
                    LastProcessTime = colony.StarSysDateTime,
                });
            }
        }

        public string AtbName() => "Power Generation";

        public string AtbDescription() =>
            $"Generates {PowerOutputKW:0.##} kW ({Profile})";
    }
}
