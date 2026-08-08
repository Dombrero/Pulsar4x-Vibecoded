using System;
using System.Linq;
using Pulsar4X.Colonies;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Interfaces;
using Pulsar4X.Ships;

namespace Pulsar4X.Energy
{
    /// <summary>Shared charge-rate and transfer helpers for colony↔ship energy docking.</summary>
    public static class EnergyRechargeHelper
    {
        public const string EnergyTypeId = ColonyPowerDB.EnergyTypeId;

        /// <summary>Ship accept rate = sum of battery MaxChargeRate (kW).</summary>
        public static double GetShipAcceptRateKW(Entity ship)
        {
            if (!ship.TryGetDataBlob<ComponentInstancesDB>(out var instances))
                return 0;

            double rate = 0;
            if (instances.TryGetComponentsByAttribute<EnergyStoreAtb>(out var batteries))
            {
                foreach (var instance in batteries)
                {
                    if (!instance.IsEnabled)
                        continue;
                    rate += instance.Design.GetAttribute<EnergyStoreAtb>().MaxChargeRateKW * instance.HealthPercent;
                }
            }
            return rate;
        }

        public static double GetColonyDockRateKW(Entity colony)
        {
            if (!colony.TryGetDataBlob<ColonyPowerDB>(out var power))
            {
                ColonyPowerProcessor.RecalcAbilities(colony);
                if (!colony.TryGetDataBlob<ColonyPowerDB>(out power))
                    return ColonyPowerDB.PortDockBonusKW;
            }
            return Math.Max(ColonyPowerDB.PortDockBonusKW, power.DockChargeRateKW);
        }

        public static double GetEffectiveRechargeRateKW(Entity colony, Entity ship)
            => Math.Min(GetColonyDockRateKW(colony), GetShipAcceptRateKW(ship));

        public static bool ShipNeedsEnergy(Entity ship)
        {
            if (!ship.TryGetDataBlob<EnergyGenAbilityDB>(out var gen))
                return false;
            if (!gen.EnergyStoreMax.TryGetValue(EnergyTypeId, out var max) || max <= 0)
                return false;
            gen.EnergyStored.TryGetValue(EnergyTypeId, out var stored);
            return stored < max - 1e-6;
        }

        public static double GetShipEnergyPercent(Entity ship)
        {
            if (!ship.TryGetDataBlob<EnergyGenAbilityDB>(out var gen))
                return 100;
            if (!gen.EnergyStoreMax.TryGetValue(EnergyTypeId, out var max) || max <= 0)
                return 100;
            gen.EnergyStored.TryGetValue(EnergyTypeId, out var stored);
            return 100.0 * stored / max;
        }

        /// <summary>Issue EnergyRechargeDB on fleet ships that need charge and can accept.</summary>
        public static bool CreateRechargeFleetCommand(Entity colony, Entity fleet)
        {
            if (!colony.HasDataBlob<ColonyInfoDB>() || !colony.HasDataBlob<ColonyPowerDB>())
            {
                ColonyPowerProcessor.RecalcAbilities(colony);
                if (!colony.HasDataBlob<ColonyPowerDB>())
                    return false;
            }

            if (!fleet.TryGetDataBlob<Fleets.FleetDB>(out var fleetDB))
                return false;

            bool any = false;
            foreach (var ship in fleetDB.Children.Where(c => !c.HasDataBlob<Fleets.FleetDB>() && c.HasDataBlob<ShipInfoDB>()))
            {
                if (ship.HasDataBlob<EnergyRechargeDB>())
                {
                    any = true;
                    continue;
                }

                // Generator / solar ships recharge themselves — do not dock-steal colony power.
                if (!Fleets.FleetEnergy.NeedsColonyRecharge(ship))
                    continue;

                // Only issue for hulls already on-station (stragglers keep warping).
                if (!Fleets.FleetOrderCleanup.IsShipAtColony(ship, colony))
                    continue;

                double rate = GetEffectiveRechargeRateKW(colony, ship);
                if (rate <= 0)
                    continue;

                ship.SetDataBlob(new EnergyRechargeDB(colony, rate));
                any = true;
            }

            return any;
        }

        /// <summary>Transfer up to rate*dt kJ from colony spendable into ship batteries.</summary>
        public static double TransferEnergy(Entity colony, Entity ship, double rateKW, double deltaSeconds)
        {
            if (!colony.TryGetDataBlob<ColonyPowerDB>(out var colonyPower)
                || !ship.TryGetDataBlob<EnergyGenAbilityDB>(out var shipGen))
                return 0;

            if (!shipGen.EnergyStoreMax.TryGetValue(EnergyTypeId, out var max) || max <= 0)
                return 0;

            shipGen.EnergyStored.TryGetValue(EnergyTypeId, out var stored);
            double free = Math.Max(0, max - stored);
            double spendable = colonyPower.SpendableKJ;
            double want = rateKW * Math.Max(0, deltaSeconds);
            double transfer = Math.Min(want, Math.Min(free, spendable));
            if (transfer <= 0)
                return 0;

            colonyPower.EnergyStoredKJ -= transfer;
            shipGen.EnergyStored[EnergyTypeId] = stored + transfer;
            return transfer;
        }
    }
}
