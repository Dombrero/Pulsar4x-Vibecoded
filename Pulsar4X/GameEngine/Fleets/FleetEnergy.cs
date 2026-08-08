using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Movement;
using Pulsar4X.Ships;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Fleet energy checks ignore hulls without a usable battery store.
    /// Colony recharge is only for ships that cannot regenerate onboard;
    /// ships with a reactor/solar wait for charge instead of docking.
    /// </summary>
    internal static class FleetEnergy
    {
        private const double GenerationEpsilonKW = 1e-9;

        internal static IEnumerable<Entity> EnergyCapableShips(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                yield break;

            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()))
            {
                if (HasEnergyCapacity(ship))
                    yield return ship;
            }
        }

        internal static bool HasEnergyCapacity(Entity ship)
        {
            if (!ship.TryGetDataBlob<EnergyGenAbilityDB>(out var gen))
                return false;
            return gen.EnergyStoreMax.TryGetValue(EnergyRechargeHelper.EnergyTypeId, out var max)
                   && max > 0;
        }

        internal static bool HasOnboardGeneration(Entity ship)
        {
            if (!ship.TryGetDataBlob<EnergyGenAbilityDB>(out var gen))
                return false;
            return gen.TotalOutputMax > GenerationEpsilonKW;
        }

        /// <summary>True when the ship has free battery space and cannot fill it itself.</summary>
        internal static bool NeedsColonyRecharge(Entity ship)
        {
            if (!HasEnergyCapacity(ship) || HasOnboardGeneration(ship))
                return false;
            return EnergyRechargeHelper.ShipNeedsEnergy(ship);
        }

        internal static IEnumerable<Entity> ShipsNeedingColonyRecharge(Entity fleet)
        {
            foreach (var ship in EnergyCapableShips(fleet))
            {
                if (NeedsColonyRecharge(ship))
                    yield return ship;
            }
        }

        /// <summary>
        /// True when every ship that still needs colony recharge is already on-station
        /// (or cannot warp). Generator hulls are ignored — they must not block siblings.
        /// </summary>
        internal static bool AreNeedyShipsAtColony(Entity fleet, Entity colony)
        {
            var needy = ShipsNeedingColonyRecharge(fleet).ToList();
            if (needy.Count == 0)
                return true;

            foreach (var ship in needy)
            {
                if (FleetOrderCleanup.IsShipAtColony(ship, colony))
                    continue;
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue;
                return false;
            }

            return true;
        }

        /// <summary>Average fill of energy-capable ships only. None → 100.</summary>
        internal static double AveragePercent(Entity fleet)
        {
            double total = 0;
            int count = 0;
            foreach (var ship in EnergyCapableShips(fleet))
            {
                total += EnergyRechargeHelper.GetShipEnergyPercent(ship);
                count++;
            }

            return count == 0 ? 100 : total / count;
        }

        /// <summary>
        /// ENTER for standing colony recharge: any battery-only (no generator) hull below threshold.
        /// </summary>
        internal static bool AnyColonyRechargeBelow(Entity fleet, float thresholdPercent)
        {
            foreach (var ship in EnergyCapableShips(fleet))
            {
                if (HasOnboardGeneration(ship))
                    continue;
                if (EnergyRechargeHelper.GetShipEnergyPercent(ship) < thresholdPercent)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// EXIT for standing colony recharge: any battery-only hull still has free capacity.
        /// </summary>
        internal static bool AnyHasFreeBatteryForColonyRecharge(Entity fleet)
        {
            foreach (var ship in EnergyCapableShips(fleet))
            {
                if (HasOnboardGeneration(ship))
                    continue;
                if (EnergyRechargeHelper.ShipNeedsEnergy(ship))
                    return true;
            }

            return false;
        }
    }
}
