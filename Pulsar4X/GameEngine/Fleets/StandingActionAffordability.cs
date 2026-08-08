using System;
using System.Linq;
using Pulsar4X.Datablobs;
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

            return CanAffordNextAction(fleet, activeOrder);
        }

        internal static bool CanAffordNextAction(Entity fleet, ConditionalOrder activeOrder)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            // Local on-site work needs no warp fuel/energy.
            if (HasOnSiteLocalWork(fleet, fleetDB, activeOrder))
                return true;

            var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList();
            if (ships.Count == 0)
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
                    if (!WarpMoveProcessor.CanAffordWarpHop(ship, dist))
                        return false;
                }
            }

            if (anyTravelCheck)
                return true;

            // Not on-site and not mid-warp: estimate hop to the next mission target.
            if (!TryResolveTravelTarget(fleet, fleetDB, activeOrder, out var target)
                || target == null
                || !target.IsValid)
                return false;

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
                if (!WarpMoveProcessor.CanAffordWarpHop(ship, dist))
                    return false;
            }

            return true;
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

        private static bool HasOnSiteLocalWork(Entity fleet, FleetDB fleetDB, ConditionalOrder activeOrder)
        {
            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()))
            {
                if (ship.HasDataBlob<CargoTransferDB>())
                    return true;
                if (ship.TryGetDataBlob<OrderableDB>(out var shipOrders)
                    && shipOrders.ActionList.OfType<CargoTransferOrder>().Any())
                    return true;

                if (ship.TryGetDataBlob<GeoSurveyingDB>(out var geo)
                    && fleet.AttachedManager.TryGetGlobalEntityById(geo.TargetId, out var geoTarget)
                    && geoTarget.TryGetDataBlob<GeoSurveyableDB>(out var geoDb)
                    && !geoDb.IsSurveyComplete(fleet.FactionOwnerID))
                    return true;

                if (ship.HasDataBlob<JPSurveyDB>())
                    return true;
            }

            // Survey commitment: already parented to an unfinished geo body = local next action.
            // (Avoid SOI checks — bodies without OrbitDB report infinite SOI.)
            if (LooksLikeSurvey(activeOrder))
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
