using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Interfaces;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Ship-scoped ENTER/EXIT matching against the fleet standing template.
    /// </summary>
    internal static class ShipStandingEvaluator
    {
        internal static int FindFirstMatchingOrderIndex(Entity ship, FleetDB fleetDB, bool useExitThreshold)
        {
            for (int i = 0; i < fleetDB.StandingOrders.Count; i++)
            {
                var order = fleetDB.StandingOrders[i];
                if (!order.IsValid || order.Actions == null || order.Actions.Count == 0)
                    continue;
                if (!ShipCanRunOrder(ship, order))
                    continue;
                if (OrderMatches(ship, order, useExitThreshold))
                    return i;
            }

            return -1;
        }

        internal static bool OrderStillNeedsAction(Entity ship, ConditionalOrder order)
        {
            if (order == null)
                return false;

            bool noConditions = order.Condition?.ConditionItems == null
                                || order.Condition.ConditionItems.Count == 0;
            if (noConditions)
                return ActionStillHasWork(ship, order);

            if (TryGetFuelLessThanThreshold(order, out _))
                return ShipHasFreeTankSpace(ship);

            if (TryGetEnergyLessThanThreshold(order, out _))
                return FleetEnergy.NeedsColonyRecharge(ship);

            // Geo/Grav ENTER can match on system-wide unsurveyed counts while siblings already
            // claimed every target — require an assignable target or the director START/suppress-loops.
            if (LooksLikeGeo(order))
                return CountAssignableGeo(ship) > 0;
            if (LooksLikeGrav(order))
                return CountAssignableGrav(ship) > 0;

            return EvaluateConditionsForShip(ship, order);
        }

        internal static bool OrderMatches(Entity ship, ConditionalOrder order, bool useExitThreshold)
        {
            bool noConditions = order.Condition?.ConditionItems == null
                                || order.Condition.ConditionItems.Count == 0;

            if (noConditions)
            {
                if (useExitThreshold)
                    return OrderStillNeedsAction(ship, order);
                if (LooksLikeRefuel(order))
                    return ShipFuelBelow(ship, 30f) || ShipNeedsOpportunityTopOff(ship);
                if (LooksLikeRecharge(order))
                    return FleetEnergy.NeedsColonyRecharge(ship)
                           && EnergyRechargeHelper.GetShipEnergyPercent(ship) < 30f;
                return ActionStillHasWork(ship, order);
            }

            if (useExitThreshold)
                return OrderStillNeedsAction(ship, order);

            bool matches = EvaluateConditionsForShip(ship, order);
            if (!matches && LooksLikeRefuel(order) && ShipNeedsOpportunityTopOff(ship))
                matches = true;
            if (matches && LooksLikeGeo(order) && CountAssignableGeo(ship) == 0)
                matches = false;
            if (matches && LooksLikeGrav(order) && CountAssignableGrav(ship) == 0)
                matches = false;
            return matches;
        }

        internal static bool ShipCanRunOrder(Entity ship, ConditionalOrder order)
        {
            if (LooksLikeGeo(order) && !ship.HasDataBlob<GeoSurveyAbilityDB>())
                return false;
            if (LooksLikeGrav(order) && !ship.HasJPSurveyAbililty())
                return false;
            if (LooksLikeRefuel(order))
            {
                try
                {
                    var cargoLibrary = ship.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                    if (!FleetFuel.HasFuelCapacity(ship, cargoLibrary))
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            if (LooksLikeRecharge(order))
            {
                if (!FleetEnergy.HasEnergyCapacity(ship) || FleetEnergy.HasOnboardGeneration(ship))
                    return false;
            }

            return true;
        }

        private static bool ActionStillHasWork(Entity ship, ConditionalOrder order)
        {
            if (order.Actions == null || order.Actions.Count == 0)
                return false;

            if (LooksLikeRefuel(order))
                return ShipHasFreeTankSpace(ship);

            if (LooksLikeRecharge(order))
                return FleetEnergy.NeedsColonyRecharge(ship);

            if (LooksLikeGeo(order))
                return CountAssignableGeo(ship) > 0;

            if (LooksLikeGrav(order))
                return CountAssignableGrav(ship) > 0;

            return false;
        }

        private static bool EvaluateConditionsForShip(Entity ship, ConditionalOrder order)
        {
            if (order.Condition?.ConditionItems == null || order.Condition.ConditionItems.Count == 0)
                return true;

            // Mirror CompoundCondition.Evaluate with ship-scoped leaves.
            var items = order.Condition.ConditionItems;
            var orResults = new List<bool>();
            bool? andResult = null;

            for (int i = 0; i < items.Count; i++)
            {
                bool result = EvaluateLeaf(ship, items[i].Condition);

                if (items[i].LogicalOperation == LogicalOperation.And || i == items.Count - 1)
                {
                    andResult = andResult.HasValue ? andResult.Value && result : result;

                    if (i == items.Count - 1
                        || (i + 1 < items.Count && items[i + 1].LogicalOperation == LogicalOperation.Or))
                    {
                        orResults.Add(andResult.Value);
                        andResult = null;
                    }
                }
            }

            return orResults.Count == 0 || orResults.Any(r => r);
        }

        private static bool EvaluateLeaf(Entity ship, ICondition? condition)
        {
            if (condition == null)
                return false;

            if (condition is FuelCondition fuel)
                return EvaluateFuel(ship, fuel);
            if (condition is EnergyCondition energy)
                return EvaluateEnergy(ship, energy);
            if (condition is UnsurveyedGeoCondition geo)
                return geo.Compare(CountAssignableGeo(ship));
            if (condition is UnsurveyedAnomalyCondition anomaly)
                return anomaly.Compare(CountAssignableGrav(ship));

            // Unknown / fleet-only conditions: try fleet Evaluate if ship is in a fleet.
            var fleet = FleetLookup.FindFleetContainingShip(ship);
            return fleet.IsValid && condition.Evaluate(fleet);
        }

        private static bool EvaluateFuel(Entity ship, FuelCondition fuel)
        {
            try
            {
                var cargoLibrary = ship.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                if (!FleetFuel.HasFuelCapacity(ship, cargoLibrary))
                    return false;
                return fuel.Compare(Math.Round(ship.GetFuelPercent(cargoLibrary)));
            }
            catch
            {
                return false;
            }
        }

        private static bool EvaluateEnergy(Entity ship, EnergyCondition energy)
        {
            if (!FleetEnergy.HasEnergyCapacity(ship))
                return false;
            if (energy.ComparisionType is ComparisonType.LessThan or ComparisonType.LessThanOrEqual
                && FleetEnergy.HasOnboardGeneration(ship))
                return false;
            return energy.Compare(Math.Round(EnergyRechargeHelper.GetShipEnergyPercent(ship)));
        }

        internal static bool ShipFuelBelow(Entity ship, float thresholdPercent)
        {
            try
            {
                var cargoLibrary = ship.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                if (!FleetFuel.HasFuelCapacity(ship, cargoLibrary))
                    return false;
                return ship.GetFuelPercent(cargoLibrary) < thresholdPercent;
            }
            catch
            {
                return false;
            }
        }

        internal static bool ShipHasFreeTankSpace(Entity ship)
        {
            try
            {
                var cargoLibrary = ship.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                if (!FleetFuel.NeedsRefuel(ship, cargoLibrary))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Docked with free tank space — top off without yanking surveying siblings.</summary>
        internal static bool ShipNeedsOpportunityTopOff(Entity ship)
        {
            if (!ShipHasFreeTankSpace(ship))
                return false;
            return IsAtFriendlyColony(ship);
        }

        internal static bool IsAtFriendlyColony(Entity ship)
        {
            if (ship.AttachedManager == null || !ship.TryGetDataBlob<PositionDB>(out _))
                return false;

            foreach (var colony in ship.AttachedManager.GetAllEntitiesWithDataBlob<ColonyInfoDB>())
            {
                if (colony.FactionOwnerID != ship.FactionOwnerID)
                    continue;
                if (FleetOrderCleanup.IsShipAtColony(ship, colony))
                    return true;
            }

            return false;
        }

        internal static int CountEligibleGeo(Entity ship)
        {
            if (ship.AttachedManager == null)
                return 0;
            return GeoSurveyTargets.CountEligible(ship.AttachedManager, ship.FactionOwnerID);
        }

        internal static int CountUnsurveyedAnomalies(Entity ship)
        {
            if (ship.AttachedManager == null)
                return 0;
            return UnsurveyedAnomalyCondition.CountUnsurveyed(ship.AttachedManager, ship.FactionOwnerID);
        }

        /// <summary>Eligible geo bodies not already claimed by sibling standing work.</summary>
        internal static int CountAssignableGeo(Entity ship)
        {
            if (ship.AttachedManager == null)
                return 0;

            HashSet<int>? claimed = null;
            var fleet = FleetLookup.FindFleetContainingShip(ship);
            if (fleet.IsValid && fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                claimed = ClaimedGeoTargets(fleetDB, ship.Id);

            int count = 0;
            foreach (var body in ship.AttachedManager.GetAllEntitiesWithDataBlob<GeoSurveyableDB>())
            {
                if (claimed != null && claimed.Contains(body.Id))
                    continue;
                if (GeoSurveyTargets.IsEligible(body, ship.FactionOwnerID))
                    count++;
            }

            return count;
        }

        /// <summary>Unsurveyed anomalies not already claimed by sibling standing work.</summary>
        internal static int CountAssignableGrav(Entity ship)
        {
            if (ship.AttachedManager == null)
                return 0;

            HashSet<int>? claimed = null;
            var fleet = FleetLookup.FindFleetContainingShip(ship);
            if (fleet.IsValid && fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                claimed = ClaimedGravTargets(fleetDB, ship.Id);

            int count = 0;
            foreach (var anomaly in ship.AttachedManager.GetAllEntitiesWithDataBlob<JPSurveyableDB>())
            {
                if (claimed != null && claimed.Contains(anomaly.Id))
                    continue;
                if (!anomaly.TryGetDataBlob<JPSurveyableDB>(out var db) || db.IsSurveyComplete(ship.FactionOwnerID))
                    continue;
                count++;
            }

            return count;
        }

        internal static HashSet<int> ClaimedGeoTargets(FleetDB fleetDB, int selfId)
        {
            var claimed = new HashSet<int>();
            foreach (var child in fleetDB.Children)
            {
                if (child.Id == selfId)
                    continue;
                if (child.TryGetDataBlob<GeoSurveyingDB>(out var surveying))
                    claimed.Add(surveying.TargetId);
                if (!child.TryGetDataBlob<OrderableDB>(out var q))
                    continue;
                foreach (var cmd in q.ActionList.OfType<GeoSurveyOrder>())
                {
                    if (cmd.Target.IsValid)
                        claimed.Add(cmd.Target.Id);
                }
                foreach (var warp in q.ActionList.OfType<WarpMoveCommand>())
                {
                    if (warp.TargetEntityGuid != 0
                        && child.AttachedManager != null
                        && child.AttachedManager.TryGetEntityById(warp.TargetEntityGuid, out var dest)
                        && dest.HasDataBlob<GeoSurveyableDB>())
                        claimed.Add(dest.Id);
                }
            }

            return claimed;
        }

        internal static HashSet<int> ClaimedGravTargets(FleetDB fleetDB, int selfId)
        {
            var claimed = new HashSet<int>();
            foreach (var child in fleetDB.Children)
            {
                if (child.Id == selfId)
                    continue;
                if (child.TryGetDataBlob<JPSurveyDB>(out var surveying))
                    claimed.Add(surveying.TargetId);
                if (!child.TryGetDataBlob<OrderableDB>(out var q))
                    continue;
                foreach (var cmd in q.ActionList.OfType<JPSurveyOrder>())
                {
                    if (cmd.Target.IsValid)
                        claimed.Add(cmd.Target.Id);
                }
            }

            return claimed;
        }

        internal static bool LooksLikeRefuel(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a => a is RefuelAction);

        internal static bool LooksLikeRecharge(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a => a is RechargeEnergyAction);

        internal static bool LooksLikeGeo(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a =>
                a is MoveToNearestGeoSurveyAction || a is GeoSurveyOrder);

        internal static bool LooksLikeGrav(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a =>
                a is MoveToNearestGravSurveyAction || a is JPSurveyOrder || a is MoveToNearestAnomalyAction);

        internal static bool LooksLikeSurvey(ConditionalOrder order)
            => LooksLikeGeo(order) || LooksLikeGrav(order);

        internal static bool TryGetFuelLessThanThreshold(ConditionalOrder order, out float threshold)
        {
            threshold = 0;
            if (order.Condition?.ConditionItems == null)
                return false;
            foreach (var item in order.Condition.ConditionItems)
            {
                if (item.Condition is FuelCondition fuel
                    && (fuel.ComparisionType == ComparisonType.LessThan
                        || fuel.ComparisionType == ComparisonType.LessThanOrEqual))
                {
                    threshold = fuel.Threshold;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetEnergyLessThanThreshold(ConditionalOrder order, out float threshold)
        {
            threshold = 0;
            if (order.Condition?.ConditionItems == null)
                return false;
            foreach (var item in order.Condition.ConditionItems)
            {
                if (item.Condition is EnergyCondition energy
                    && (energy.ComparisionType == ComparisonType.LessThan
                        || energy.ComparisionType == ComparisonType.LessThanOrEqual))
                {
                    threshold = energy.Threshold;
                    return true;
                }
            }

            return false;
        }
    }
}
