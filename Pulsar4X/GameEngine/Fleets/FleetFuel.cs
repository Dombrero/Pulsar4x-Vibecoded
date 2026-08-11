using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Components;
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
        /// True when every local (same-system) ship that still needs fuel is on-station
        /// (or cannot warp). Hulls still jumping elsewhere must not block sibling refuel.
        /// </summary>
        internal static bool AreNeedyShipsAtColony(Entity fleet, Entity colony)
        {
            var needy = ShipsNeedingRefuel(fleet).ToList();
            if (needy.Count == 0)
                return true;

            var colonySys = colony.AttachedManager;
            foreach (var ship in needy)
            {
                // Remote jumpers move independently — ignore until they share this system.
                if (colonySys != null && ship.AttachedManager != colonySys)
                    continue;
                if (FleetOrderCleanup.IsShipAtColony(ship, colony))
                    continue;
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue; // cannot travel — do not block others
                return false;
            }

            return true;
        }

        /// <summary>
        /// Free tank space only among fuel-capable hulls already in <paramref name="system"/>.
        /// </summary>
        internal static bool AnyHasFreeTankSpaceInSystem(Entity fleet, EntityManager? system)
        {
            if (system == null || !fleet.TryGetDataBlob<FleetDB>(out _))
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            foreach (var ship in FuelCapableShips(fleet, cargoLibrary))
            {
                if (ship.AttachedManager != system)
                    continue;
                var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
                if (fuel == null)
                    continue;
                if (CargoMath.GetFreeUnitSpace(ship.GetDataBlob<CargoStorageDB>(), fuel, includeEscro: false) > 0)
                    return true;
            }

            return false;
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

        /// <summary>
        /// True when the hull has at least one working <see cref="CargoTransferAtb"/> module.
        /// Fallback TransferRate on storage alone does NOT count — that is only for colonies /
        /// broken installs, not free ship-to-ship tanking.
        /// </summary>
        internal static bool HasWorkingCargoTransferModule(Entity ship)
        {
            if (!ship.TryGetDataBlob<ComponentInstancesDB>(out var instances))
                return false;
            if (!instances.TryGetComponentsByAttribute<CargoTransferAtb>(out List<ComponentInstance> list)
                || list == null
                || list.Count == 0)
                return false;

            foreach (var instance in list)
            {
                if (!instance.Design.HasAttribute<CargoTransferAtb>())
                    continue;
                if (instance.HealthPercent > instance.StopWorkingAtPercent)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Both ships must have a working transfer module and be within cargo Δv range
        /// (same rules as <see cref="CargoTransferProcessor.CalcTransferRate"/>).
        /// </summary>
        internal static bool CanShipToShipTransfer(Entity a, Entity b)
        {
            if (a == null || b == null || !a.IsValid || !b.IsValid || a.Id == b.Id)
                return false;
            if (!HasWorkingCargoTransferModule(a) || !HasWorkingCargoTransferModule(b))
                return false;
            if (!a.TryGetDataBlob<CargoStorageDB>(out var storeA)
                || !b.TryGetDataBlob<CargoStorageDB>(out var storeB))
                return false;

            double dv = CargoTransferProcessor.CalcDVDifference_m(a, b);
            return CargoTransferProcessor.CalcTransferRate(dv, storeA, storeB) > 0;
        }

        /// <summary>
        /// Move tank fuel between fleet ships that can legally transfer (module + range)
        /// so as many as possible can afford a warp of <paramref name="distance_m"/>.
        /// Instant mass move for JumpOrder recovery — not a substitute for CargoTransferOrder
        /// over time, but respects the same eligibility gates.
        /// </summary>
        internal static int TryRedistributeFuelForWarpHop(IReadOnlyList<Entity> ships, double distance_m)
        {
            if (ships == null || ships.Count < 2 || distance_m <= 1e6)
                return 0;

            int transfers = 0;
            var slots = new List<FuelShareSlot>();

            foreach (var ship in ships)
            {
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue;
                if (!HasWorkingCargoTransferModule(ship))
                    continue;
                if (!ship.TryGetDataBlob<CargoStorageDB>(out var store))
                    continue;
                try
                {
                    var cargoLib = ship.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                    var (fuel, _) = ship.GetFuelInfo(cargoLib);
                    if (fuel == null)
                        continue;
                    long need = WarpMoveProcessor.EstimateWarpTankFuelUnits(ship, distance_m);
                    if (need <= 0)
                        continue;
                    long have = store.GetUnitsStored(fuel, includeEscro: false);
                    slots.Add(new FuelShareSlot(ship, fuel, store, need, have));
                }
                catch
                {
                    // ignore malformed hull
                }
            }

            if (slots.Count < 2)
                return 0;

            // Pair-wise: only donor→needy when same fuel type AND CanShipToShipTransfer.
            while (true)
            {
                FuelShareSlot? needy = null;
                FuelShareSlot? donor = null;
                long bestSurplus = 0;

                foreach (var recv in slots.Where(g => g.Have < g.Need).OrderBy(g => g.Have))
                {
                    foreach (var cand in slots.Where(g =>
                                 g.Have > g.Need
                                 && g.Fuel.UniqueID == recv.Fuel.UniqueID
                                 && g.Ship.Id != recv.Ship.Id))
                    {
                        if (!CanShipToShipTransfer(cand.Ship, recv.Ship))
                            continue;
                        long surplus = cand.Have - cand.Need;
                        if (surplus > bestSurplus)
                        {
                            bestSurplus = surplus;
                            needy = recv;
                            donor = cand;
                        }
                    }

                    if (needy != null)
                        break;
                }

                if (needy == null || donor == null || bestSurplus <= 0)
                    break;

                long want = needy.Need - needy.Have;
                long give = Math.Min(want, bestSurplus);
                if (give <= 0)
                    break;

                long removed = donor.Store.RemoveCargoByUnit(donor.Fuel, give);
                if (removed <= 0)
                    break;

                long added = needy.Store.AddCargoByUnit(needy.Fuel, removed);
                if (added < removed)
                    donor.Store.AddCargoByUnit(donor.Fuel, removed - added);

                long transferred = Math.Min(removed, added);
                if (transferred <= 0)
                    break;

                donor.Have -= transferred;
                needy.Have += transferred;
                transfers++;
            }

            return transfers;
        }

        private sealed class FuelShareSlot
        {
            public Entity Ship;
            public ICargoable Fuel;
            public CargoStorageDB Store;
            public long Need;
            public long Have;

            public FuelShareSlot(Entity ship, ICargoable fuel, CargoStorageDB store, long need, long have)
            {
                Ship = ship;
                Fuel = fuel;
                Store = store;
                Need = need;
                Have = have;
            }
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
