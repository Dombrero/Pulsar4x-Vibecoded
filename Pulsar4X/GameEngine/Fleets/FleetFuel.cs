using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Movement;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Fleet fuel checks ignore hulls without a usable fuel tank (e.g. SensorSats).
    /// Averaging those as 0% made standing Refuel loop at "fuel=50%" while CreateRefuel saw full=1.
    /// </summary>
    internal static class FleetFuel
    {
        internal static IEnumerable<Entity> FuelCapableShips(Entity fleet, CargoDefinitionsLibrary cargoLibrary)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                yield break;

            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()))
            {
                if (HasFuelCapacity(ship, cargoLibrary))
                    yield return ship;
            }
        }

        internal static bool HasFuelCapacity(Entity ship, CargoDefinitionsLibrary cargoLibrary)
        {
            var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
            if (fuel == null)
                return false;
            if (!ship.TryGetDataBlob<CargoStorageDB>(out var store))
                return false;
            if (!store.TypeStores.ContainsKey(fuel.CargoTypeID))
                return false;

            long stored = store.GetUnitsStored(fuel, includeEscro: false);
            long free = store.GetFreeUnitSpace(fuel, includeEscro: false);
            return stored + free > 0;
        }

        internal static bool NeedsRefuel(Entity ship, CargoDefinitionsLibrary cargoLibrary)
        {
            if (!HasFuelCapacity(ship, cargoLibrary))
                return false;
            var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
            if (fuel == null || !ship.TryGetDataBlob<CargoStorageDB>(out var store))
                return false;
            return CargoMath.GetFreeUnitSpace(store, fuel, includeEscro: false) > 0;
        }

        internal static IEnumerable<Entity> ShipsNeedingRefuel(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                yield break;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            foreach (var ship in FuelCapableShips(fleet, cargoLibrary))
            {
                if (NeedsRefuel(ship, cargoLibrary))
                    yield return ship;
            }
        }

        /// <summary>
        /// True when every ship that still needs fuel is already on-station (or cannot warp).
        /// Full / non-fuel hulls are ignored — they must not block sibling refuel.
        /// </summary>
        internal static bool AreNeedyShipsAtColony(Entity fleet, Entity colony)
        {
            var needy = ShipsNeedingRefuel(fleet).ToList();
            if (needy.Count == 0)
                return true;

            foreach (var ship in needy)
            {
                if (FleetOrderCleanup.IsShipAtColony(ship, colony))
                    continue;
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue; // cannot travel — do not block others
                return false;
            }

            return true;
        }

        /// <summary>Average fill of fuel-capable ships only. No capable hull → 100 (nothing to refuel).</summary>
        internal static double AveragePercent(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return 100;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            double total = 0;
            int count = 0;
            foreach (var ship in FuelCapableShips(fleet, cargoLibrary))
            {
                total += ship.GetFuelPercent(cargoLibrary);
                count++;
            }

            return count == 0 ? 100 : total / count;
        }

        /// <summary>True when any fuel-capable ship is strictly below <paramref name="thresholdPercent"/>.</summary>
        internal static bool AnyBelow(Entity fleet, float thresholdPercent)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            foreach (var ship in FuelCapableShips(fleet, cargoLibrary))
            {
                if (ship.GetFuelPercent(cargoLibrary) < thresholdPercent)
                    return true;
            }

            return false;
        }

        /// <summary>True when at least one fuel-capable hull still has free tank space.</summary>
        internal static bool AnyHasFreeTankSpace(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            foreach (var ship in FuelCapableShips(fleet, cargoLibrary))
            {
                var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
                if (fuel == null)
                    continue;
                if (CargoMath.GetFreeUnitSpace(ship.GetDataBlob<CargoStorageDB>(), fuel, includeEscro: false) > 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when a fuel-capable ship with free tank space is already at a friendly colony.
        /// Standing orders use this to top off before leaving, even when fuel is above the
        /// normal ENTER threshold (e.g. enough for one more hop).
        /// </summary>
        internal static bool HasOpportunityTopOff(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;
            if (fleet.AttachedManager == null)
                return false;

            return ShipsNeedingRefuel(fleet)
                .Any(s => IsAtFriendlyColony(fleet, s));
        }

        /// <summary>
        /// Opportunity ENTER only when the whole warp-capable fuel fleet is on-station
        /// and at least one tank still has free space.
        /// Needy-only checks were wrong: full surveyors already away do not count as needy,
        /// so docked siblings with a few free litres yanked the fleet into Refuel mid-survey.
        /// </summary>
        internal static bool HasFleetWideOpportunityTopOff(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;
            if (fleet.AttachedManager == null)
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            var warpFuelShips = FuelCapableShips(fleet, cargoLibrary)
                .Where(s => s.HasDataBlob<WarpAbilityDB>())
                .ToList();
            if (warpFuelShips.Count == 0)
                return false;

            // Any surveyor (even full) still out → not a fleet-wide top-off window.
            if (warpFuelShips.Any(s => !IsAtFriendlyColony(fleet, s)))
                return false;

            return warpFuelShips.Any(s => NeedsRefuel(s, cargoLibrary));
        }

        private static bool IsAtFriendlyColony(Entity fleet, Entity ship)
        {
            if (fleet.AttachedManager == null)
                return false;

            foreach (var colony in fleet.AttachedManager.GetFilteredEntities(
                         EntityFilter.Friendly,
                         fleet.FactionOwnerID,
                         e => e.HasDataBlob<Colonies.ColonyInfoDB>()
                              && e.HasDataBlob<CargoStorageDB>()))
            {
                if (FleetOrderCleanup.IsShipAtColony(ship, colony))
                    return true;
            }

            return false;
        }
    }
}
