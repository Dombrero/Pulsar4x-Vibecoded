using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Before logistics (Refuel/Recharge) preempts a lower standing commitment, check whether
    /// the commitment's next action is still affordable in tank fuel and warp energy.
    /// </summary>
    internal static class StandingActionAffordability
    {
        internal static bool ShouldDeferLogisticsPreempt(
            Entity fleet,
            ConditionalOrder activeOrder,
            ConditionalOrder enterOrder)
        {
            if (activeOrder == null || enterOrder == null)
                return false;

            bool logisticsEnter = LooksLikeRefuel(enterOrder) || LooksLikeRecharge(enterOrder);
            if (!logisticsEnter)
                return false;

            // Logistics vs logistics: list priority wins (no self-defer).
            if (LooksLikeRefuel(activeOrder) || LooksLikeRecharge(activeOrder))
                return false;

            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            // Empty tanks + warp blocked forever: never defer Refuel. Active local scan/transfer
            // may still finish first (0 fuel), but "parented near unfinished body" alone must not.
            if (LooksLikeRefuel(enterOrder)
                && AnyFuelCapableShipEmpty(fleet)
                && !HasActiveLocalScanOrTransfer(fleet, fleetDB))
                return false;

            // Active survey: opportunity Refuel never preempts while any hull is away.
            // Only real shortage (empty / below ENTER / cannot afford next hop or return) may interrupt.
            if (LooksLikeRefuel(enterOrder) && LooksLikeSurvey(activeOrder))
            {
                if (FleetFuel.HasFleetWideOpportunityTopOff(fleet)
                    && !HasOnSiteLocalWork(fleet, fleetDB, activeOrder))
                    return false; // whole fleet still docked — top off before the next hop

                float enterThreshold = 30f;
                if (TryGetRefuelEnterThreshold(enterOrder, out var threshold))
                    enterThreshold = threshold;

                // Even above ENTER: abroad fleets must keep a return-to-gate/colony reserve.
                // Otherwise survey burns tanks to 0% and JumpOrder cannot warp home.
                if (!CanAffordReturnPath(fleet, fleetDB))
                    return false;

                if (!FleetFuel.AnyBelow(fleet, enterThreshold))
                    return true; // opportunity / partial dock — keep surveying

                return CanAffordNextAction(fleet, activeOrder);
            }

            return CanAffordNextAction(fleet, activeOrder);
        }

        private static bool TryGetRefuelEnterThreshold(ConditionalOrder order, out float threshold)
        {
            threshold = 30f;
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

        internal static bool CanAffordNextAction(Entity fleet, ConditionalOrder activeOrder)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            // Local on-site work needs no warp fuel/energy — but only real scan/transfer work.
            // Parented-to-unfinished-body alone used to claim "affordable" at fuel=0% while
            // MoveToNearest kept failing warp-empty.
            if (HasActiveLocalScanOrTransfer(fleet, fleetDB))
                return true;

            if (HasOnSiteLocalWork(fleet, fleetDB, activeOrder)
                && !AnyFuelCapableShipEmpty(fleet))
                return true;

            var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList();
            if (ships.Count == 0)
                return false;

            // Empty cargo tanks: travel is never affordable.
            if (AnyFuelCapableShipEmpty(fleet))
                return false;

            bool anyTravelCheck = false;

            foreach (var ship in ships)
            {
                if (ship.TryGetDataBlob<WarpMovingDB>(out var moving))
                {
                    anyTravelCheck = true;
                    double dist = (moving.ExitPointAbsolute - moving.EntryPointAbsolute).Length();
                    // Prefer remaining distance from current absolute position when available.
                    if (ship.TryGetDataBlob<PositionDB>(out var pos))
                        dist = (moving.ExitPointAbsolute - pos.AbsolutePosition).Length();
                    if (!CanAffordStandingTravelHop(ship, dist))
                        return false;
                }
            }

            if (anyTravelCheck)
                return CanAffordReturnPath(fleet, fleetDB);

            // Not on-site and not mid-warp: estimate hop to the next mission target.
            if (!TryResolveTravelTarget(fleet, fleetDB, activeOrder, out var target)
                || target == null
                || !target.IsValid)
            {
                // Survey fleets abroad: lack of a next geo target must not look "unaffordable"
                // solely for that reason — still require a return-fuel reserve.
                return LooksLikeSurvey(activeOrder) && CanAffordReturnPath(fleet, fleetDB);
            }

            if (!target.TryGetDataBlob<PositionDB>(out var targetPos))
                return false;

            foreach (var ship in ships)
            {
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue;
                // Parent-only: IsShipAtBody uses SOI, and bodies without OrbitDB report infinite SOI.
                if (IsParentedToTarget(ship, target))
                    continue;
                if (!ship.TryGetDataBlob<PositionDB>(out var shipPos))
                    return false;

                double dist = (targetPos.AbsolutePosition - shipPos.AbsolutePosition).Length();
                if (!CanAffordStandingTravelHop(ship, dist))
                    return false;
            }

            return CanAffordReturnPath(fleet, fleetDB);
        }

        /// <summary>
        /// Fuel reserve to reach the nearest friendly colony in-system, or the jump gate
        /// toward the fleet's known refuel system. Uses the same ~2-hop standing reserve.
        /// </summary>
        private static bool CanAffordReturnPath(Entity fleet, FleetDB fleetDB)
        {
            var manager = fleet.AttachedManager;
            if (manager?.Game == null)
                return true;

            Entity? flagship = null;
            if (fleetDB.FlagShipID >= 0)
                manager.TryGetEntityById(fleetDB.FlagShipID, out flagship);
            if (flagship == null || !flagship.TryGetDataBlob<PositionDB>(out var flagshipPos))
                return true;

            int factionId = fleet.FactionOwnerID;
            Entity? returnTarget = RefuelColonySearch.FindNearestColonyInSystem(
                manager, factionId, flagshipPos);

            if (returnTarget == null
                && RefuelColonySearch.TryResolveRefuelSystemId(
                    manager.Game, fleet, factionId, fleetDB, out var targetSystemId)
                && RefuelColonySearch.TryFindJumpGateTowardSystem(
                    manager.Game, fleet, factionId, targetSystemId, flagshipPos, out var jumpGate)
                && jumpGate?.OwningEntity is { IsValid: true } gateEntity
                && gateEntity.AttachedManager == manager)
            {
                returnTarget = gateEntity;
            }

            if (returnTarget == null || !returnTarget.TryGetDataBlob<PositionDB>(out var returnPos))
                return true; // unknown path — don't invent a hard fail

            // Never compare AbsolutePosition across star systems.
            if (returnTarget.AttachedManager != manager)
                return true;

            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()))
            {
                if (!ship.HasDataBlob<WarpAbilityDB>())
                    continue;
                if (IsParentedToTarget(ship, returnTarget))
                    continue;
                if (!ship.TryGetDataBlob<PositionDB>(out var shipPos))
                    return false;

                double dist = (returnPos.AbsolutePosition - shipPos.AbsolutePosition).Length();
                if (dist < 1e6)
                    continue;
                if (!CanAffordStandingTravelHop(ship, dist))
                    return false;
            }

            return true;
        }

        private static bool AnyFuelCapableShipEmpty(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out _))
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<Factions.FactionInfoDB>().Data.CargoGoods;
            foreach (var ship in FleetFuel.FuelCapableShips(fleet, cargoLibrary))
            {
                if (!WarpMoveProcessor.HasWarpTankFuel(ship))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Standing logistics uses a travel reserve: bare next-hop affordability still leaves
        /// ships stranded after arrival. Require enough tank fuel for ~2 similar hops.
        /// </summary>
        private static bool CanAffordStandingTravelHop(Entity ship, double distance_m)
        {
            if (!WarpMoveProcessor.CanAffordWarpHop(ship, distance_m))
                return false;

            // Empty cargo tanks must never count as affordable travel.
            if (distance_m > 1e6 && !WarpMoveProcessor.HasWarpTankFuel(ship))
                return false;

            long need = WarpMoveProcessor.EstimateWarpTankFuelUnits(ship, distance_m);
            if (need <= 0)
                return true;

            try
            {
                if (!ship.TryGetDataBlob<CargoStorageDB>(out var storage))
                    return false;
                var cargoLib = ship.GetFactionOwner.GetDataBlob<Factions.FactionInfoDB>().Data.CargoGoods;
                var (fuel, _) = ship.GetFuelInfo(cargoLib);
                if (fuel == null)
                    return true;

                long stored = storage.GetUnitsStored(fuel, includeEscro: false);
                return stored >= need * 2;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsParentedToTarget(Entity ship, Entity target)
        {
            if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || shipPos.Parent == null)
                return false;
            if (shipPos.Parent.Id == target.Id)
                return true;
            if (target.TryGetDataBlob<Colonies.ColonyInfoDB>(out var colony)
                && colony.PlanetEntity != null
                && shipPos.Parent.Id == colony.PlanetEntity.Id)
                return true;
            return false;
        }

        /// <summary>
        /// True when a hull is actively scanning or transferring — work that needs no warp fuel.
        /// </summary>
        private static bool HasActiveLocalScanOrTransfer(Entity fleet, FleetDB fleetDB)
        {
            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()))
            {
                if (ship.HasDataBlob<CargoTransferDB>())
                    return true;
                if (ship.TryGetDataBlob<OrderableDB>(out var shipOrders)
                    && shipOrders.ActionList.OfType<CargoTransferOrder>().Any())
                    return true;

                // GeoSurveyingDB alone means a local scan is in progress (no warp fuel needed).
                if (ship.TryGetDataBlob<GeoSurveyingDB>(out var geo)
                    && TryGetSurveyTarget(fleet, geo.TargetId, out var geoTarget)
                    && geoTarget.TryGetDataBlob<GeoSurveyableDB>(out var geoDb)
                    && !geoDb.IsSurveyComplete(fleet.FactionOwnerID))
                    return true;

                // Grav/JP: only while hovering / at the anomaly (blob can linger after leaving).
                if (ship.TryGetDataBlob<JPSurveyDB>(out var jp)
                    && TryGetSurveyTarget(fleet, jp.TargetId, out var jpTarget)
                    && jpTarget.TryGetDataBlob<JPSurveyableDB>(out var jpDb)
                    && !jpDb.IsSurveyComplete(fleet.FactionOwnerID)
                    && IsAtGravSurveyTarget(ship, jpTarget))
                    return true;
            }

            return false;
        }

        private static bool TryGetSurveyTarget(Entity fleet, int targetId, out Entity target)
        {
            target = Entity.InvalidEntity;
            if (fleet.AttachedManager == null)
                return false;
            if (fleet.AttachedManager.TryGetEntityById(targetId, out target) && target.IsValid)
                return true;
            return fleet.AttachedManager.TryGetGlobalEntityById(targetId, out target) && target.IsValid;
        }

        private static bool IsAtGravSurveyTarget(Entity ship, Entity target)
        {
            if (IsParentedToTarget(ship, target))
                return true;
            // Grav hover stays in heliocentric frame with WarpMovingDB.IsAtTarget.
            if (ship.TryGetDataBlob<WarpMovingDB>(out var move)
                && move.IsAtTarget
                && move.TargetEntity != null
                && move.TargetEntity.Id == target.Id)
                return true;
            return false;
        }

        private static bool HasOnSiteLocalWork(Entity fleet, FleetDB fleetDB, ConditionalOrder activeOrder)
        {
            if (HasActiveLocalScanOrTransfer(fleet, fleetDB))
                return true;

            // Survey commitment: already parented to an unfinished geo body = local next action.
            // (Avoid SOI checks — bodies without OrbitDB report infinite SOI.)
            // Only when tanks still have fuel — empty tanks must go Refuel, not claim "affordable".
            if (LooksLikeSurvey(activeOrder) && !AnyFuelCapableShipEmpty(fleet))
            {
                foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()))
                {
                    if (!ship.TryGetDataBlob<PositionDB>(out var pos) || pos.Parent == null)
                        continue;
                    var parent = pos.Parent;
                    if (parent.TryGetDataBlob<GeoSurveyableDB>(out var geo)
                        && !geo.IsSurveyComplete(fleet.FactionOwnerID))
                        return true;
                }
            }

            return false;
        }

        private static bool TryResolveTravelTarget(
            Entity fleet,
            FleetDB fleetDB,
            ConditionalOrder activeOrder,
            out Entity? target)
        {
            target = null;

            if (fleet.TryGetDataBlob<OrderableDB>(out var orderable))
            {
                foreach (var cmd in orderable.ActionList)
                {
                    if (cmd is GeoSurveyOrder geo && geo.Target.IsValid)
                    {
                        target = geo.Target;
                        return true;
                    }
                    if (cmd is JPSurveyOrder jp && jp.Target.IsValid)
                    {
                        target = jp.Target;
                        return true;
                    }
                    if (cmd is WarpMoveCommand warp
                        && fleet.AttachedManager.TryGetGlobalEntityById(warp.TargetEntityGuid, out var warpTarget))
                    {
                        target = warpTarget;
                        return true;
                    }
                }
            }

            if (!LooksLikeSurvey(activeOrder))
                return false;

            // Nearest unfinished geo body in the flagship system (same idea as MoveToNearestGeoSurvey).
            if (!UnsurveyedGeoCondition.TryGetFlagshipSystem(fleet, out var manager))
                return false;

            Entity? flagship = null;
            if (fleetDB.FlagShipID >= 0)
                manager.TryGetEntityById(fleetDB.FlagShipID, out flagship);
            if (flagship == null || !flagship.TryGetDataBlob<PositionDB>(out var fromPos))
                return false;

            double best = double.MaxValue;
            foreach (var body in manager.GetAllEntitiesWithDataBlob<GeoSurveyableDB>())
            {
                if (!GeoSurveyTargets.IsEligible(body, fleet.FactionOwnerID))
                    continue;
                if (!body.TryGetDataBlob<PositionDB>(out var bodyPos))
                    continue;
                double dist = bodyPos.GetDistanceTo_m(fromPos);
                if (dist < best)
                {
                    best = dist;
                    target = body;
                }
            }

            return target != null;
        }

        private static bool LooksLikeRefuel(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a => a is RefuelAction);

        private static bool LooksLikeRecharge(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a => a is RechargeEnergyAction);

        private static bool LooksLikeSurvey(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a =>
                a is MoveToNearestGeoSurveyAction
                || a is MoveToNearestGravSurveyAction
                || a is GeoSurveyOrder
                || a is JPSurveyOrder);
    }
}
