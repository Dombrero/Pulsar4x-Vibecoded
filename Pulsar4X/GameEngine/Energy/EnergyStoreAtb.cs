using System;
using Pulsar4X.Colonies;
using Pulsar4X.Components;
using Pulsar4X.Interfaces;
using Pulsar4X.Engine;

namespace Pulsar4X.Energy
{
    public class EnergyStoreAtb : IComponentDesignAttribute
    {
        public string EnergyTypeID;
        /// <summary>Capacity in kJ.</summary>
        public double MaxStore;

        /// <summary>
        /// Hours of continuous dock charge to fill this battery from empty.
        /// Ship accept / colony battery charge rates derive from this.
        /// </summary>
        public const double FullChargeHours = 24.0;

        /// <summary>Dock/charge accept rate in kW (= kJ/s). Full charge from empty in <see cref="FullChargeHours"/>.</summary>
        public double MaxChargeRateKW => MaxStore / (FullChargeHours * 3600.0);

        public EnergyStoreAtb(string energyTypeID, double maxStore)
        {
            EnergyTypeID = energyTypeID;
            MaxStore = maxStore;
        }

        public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            if (parentEntity.HasDataBlob<ColonyInfoDB>())
            {
                PowerGenerationAtb.EnsureColonyPower(parentEntity);
                ColonyPowerProcessor.RecalcAbilities(parentEntity);
                return;
            }

            EnergyGenAbilityDB genDB;

            if (!parentEntity.HasDataBlob<EnergyGenAbilityDB>())
            {
                genDB = new EnergyGenAbilityDB(parentEntity.StarSysDateTime);
                parentEntity.SetDataBlob(genDB);
            }
            else
            {
                genDB = parentEntity.GetDataBlob<EnergyGenAbilityDB>();
            }
            if (genDB.EnergyStoreMax.ContainsKey(EnergyTypeID))
            {
                genDB.EnergyStoreMax[EnergyTypeID] += MaxStore;
            }
            else
            {
                genDB.EnergyStored[EnergyTypeID] = 0;
                genDB.EnergyStoreMax[EnergyTypeID] = MaxStore;
            }
        }

        public void OnComponentUninstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            if (parentEntity.HasDataBlob<ColonyInfoDB>())
            {
                ColonyPowerProcessor.RecalcAbilities(parentEntity);
                return;
            }

            if (!parentEntity.TryGetDataBlob<EnergyGenAbilityDB>(out var genDB))
                return;

            if (genDB.EnergyStoreMax.ContainsKey(EnergyTypeID))
            {
                genDB.EnergyStoreMax[EnergyTypeID] = Math.Max(0, genDB.EnergyStoreMax[EnergyTypeID] - MaxStore);
                if (genDB.EnergyStored.ContainsKey(EnergyTypeID))
                {
                    genDB.EnergyStored[EnergyTypeID] = Math.Min(
                        genDB.EnergyStored[EnergyTypeID],
                        genDB.EnergyStoreMax[EnergyTypeID]);
                }
            }
        }

        public string AtbName()
        {
            return "Energy Storage";
        }

        public string AtbDescription()
        {
            return "Adds " + MaxStore + " Energy Storage to parent";
        }
    }
}
