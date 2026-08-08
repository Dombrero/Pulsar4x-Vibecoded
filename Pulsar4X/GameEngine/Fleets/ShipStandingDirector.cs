using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Fleets;

/// <summary>
/// Option B: fleet owns the standing mission; ships pick the next local step at action boundaries.
/// New mission types register an <see cref="IShipStandingStep"/>.
/// </summary>
internal interface IShipStandingStep
{
    bool TryEnqueue(Entity ship, Entity fleet, FleetDB fleetDB, ConditionalOrder mission);
}

internal static class ShipStandingDirector
{
    private static readonly IShipStandingStep[] Steps =
    {
        new LogisticsShipStep(),
        new GeoSurveyShipStep(),
        new GravSurveyShipStep(),
    };

    private static readonly HashSet<int> s_coalesce = new();

    internal static void TryStepNow(Entity ship)
    {
        if (ship is not { IsValid: true } || !ship.HasDataBlob<ShipInfoDB>())
            return;
        if (!s_coalesce.Add(ship.Id))
            return;

        try
        {
            StepCore(ship);
        }
        finally
        {
            s_coalesce.Remove(ship.Id);
        }
    }

    internal static void KickIdleChildren(Entity fleet)
    {
        if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            return;
        if (fleetDB.ActiveStandingOrderIndex < 0 || fleetDB.StandingOrders.Count == 0)
            return;

        foreach (var child in fleetDB.Children)
        {
            if (!child.HasDataBlob<ShipInfoDB>())
                continue;
            if (child.TryGetDataBlob<OrderableDB>(out var q) && q.ActionList.Count > 0)
                continue;
            TryStepNow(child);
        }
    }

    private static void StepCore(Entity ship)
    {
        if (ship.TryGetDataBlob<OrderableDB>(out var shipOrders))
        {
            if (shipOrders.ActionList.Any(a => a.Source == OrderSource.Issued))
                return;
            if (shipOrders.ActionList.Count > 0)
                return;
        }

        var fleet = FleetLookup.FindFleetContainingShip(ship);
        if (!fleet.IsValid || !fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            return;
        if (fleetDB.ActiveStandingOrderIndex < 0
            || fleetDB.ActiveStandingOrderIndex >= fleetDB.StandingOrders.Count)
            return;
        if (fleet.TryGetDataBlob<OrderableDB>(out var fleetOrders)
            && fleetOrders.ActionList.Any(a => a.Source == OrderSource.Issued))
            return;

        var mission = fleetDB.StandingOrders[fleetDB.ActiveStandingOrderIndex];
        foreach (var step in Steps)
        {
            if (step.TryEnqueue(ship, fleet, fleetDB, mission))
                return;
        }
    }

    private static bool LooksLikeGeo(ConditionalOrder order)
        => order.Actions.Any(a => a is MoveToNearestGeoSurveyAction || a is GeoSurveyOrder);

    private static bool LooksLikeGrav(ConditionalOrder order)
        => order.Actions.Any(a =>
            a is MoveToNearestGravSurveyAction || a is JPSurveyOrder || a is MoveToNearestAnomalyAction);

    /// <summary>
    /// Empty tanks or docked top-off during a non-logistics mission.
    /// Does not change fleet commitment; skips when fleet Refuel/Recharge already owns logistics.
    /// </summary>
    private sealed class LogisticsShipStep : IShipStandingStep
    {
        public bool TryEnqueue(Entity ship, Entity fleet, FleetDB fleetDB, ConditionalOrder mission)
        {
            // Fleet Refuel/Recharge actions already dispatch ship transfers — don't double-queue.
            if (mission.Actions.Any(a => a is RefuelAction || a is RechargeEnergyAction))
                return false;

            var game = ship.AttachedManager?.Game;
            if (game == null || !ship.HasDataBlob<WarpAbilityDB>())
                return false;

            var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            if (!FleetFuel.HasFuelCapacity(ship, cargoLibrary))
                return false;

            var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
            if (fuel == null || !ship.TryGetDataBlob<CargoStorageDB>(out var store))
                return false;

            long free = CargoMath.GetFreeUnitSpace(store, fuel, includeEscro: false);
            if (free <= 0)
                return false;

            long stored = store.GetUnitsStored(fuel, includeEscro: false);
            // Only empty tanks may divert a ship from an active survey/grav mission.
            // Opportunity top-off stays a fleet-level Standing decision.
            if (stored > 0)
                return false;

            Entity? colony = FindNearestColony(ship, fleet.FactionOwnerID);
            if (colony == null)
                return false;

            bool atColony = FleetOrderCleanup.IsShipAtColony(ship, colony);

            if (!atColony)
            {
                try
                {
                    FleetOrderCleanup.AbortCargoTransfersOnEntity(ship);
                    FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);
                    var warp = WarpMoveCommand.CreateCommandEZ(ship, colony, ship.StarSysDateTime);
                    warp.Source = OrderSource.Standing;
                    return OrderEnqueue.Standing(game, warp);
                }
                catch (Exception ex)
                {
                    DebugTraceLog.Warn("Standing",
                        $"ship#{ship.Id} logistics warp failed: {ex.Message}",
                        ship.StarSysDateTime);
                    return false;
                }
            }

            if (!colony.TryGetDataBlob<CargoStorageDB>(out var colonyStore)
                || !colonyStore.TypeStores.ContainsKey(fuel.CargoTypeID)
                || !store.TypeStores.ContainsKey(fuel.CargoTypeID))
                return false;

            CargoTransferOrder.CreateCommands(
                fleet.FactionOwnerID, ship, colony, fuel,
                CargoTransferOrder.Conditionals.WaitTillFull, OrderSource.Standing);
            return true;
        }

        private static Entity? FindNearestColony(Entity ship, int factionId)
        {
            if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || ship.Manager == null)
                return null;

            Entity? best = null;
            double bestDist = double.MaxValue;
            foreach (var colony in ship.AttachedManager.GetAllEntitiesWithDataBlob<ColonyInfoDB>())
            {
                if (colony.FactionOwnerID != factionId)
                    continue;
                if (!colony.TryGetDataBlob<PositionDB>(out var colonyPos))
                    continue;
                double d = colonyPos.GetDistanceTo_m(shipPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = colony;
                }
            }

            return best;
        }
    }

    private sealed class GeoSurveyShipStep : IShipStandingStep
    {
        public bool TryEnqueue(Entity ship, Entity fleet, FleetDB fleetDB, ConditionalOrder mission)
        {
            if (!LooksLikeGeo(mission) || !ship.HasDataBlob<GeoSurveyAbilityDB>())
                return false;

            var game = ship.AttachedManager?.Game;
            if (game == null)
                return false;

            // Fleet geo action already driving per-ship work — don't double-queue.
            if (fleet.TryGetDataBlob<OrderableDB>(out var fq)
                && fq.ActionList.OfType<MoveToNearestGeoSurveyAction>().Any())
                return false;

            var claimed = ClaimedGeoTargets(fleetDB, ship.Id);
            var target = FindNearestGeo(ship, fleet.FactionOwnerID, claimed);
            if (target == null)
                return false;

            var order = new GeoSurveyOrder(ship, target)
            {
                RequestingFactionGuid = fleet.FactionOwnerID,
                EntityCommandingGuid = ship.Id,
                Source = OrderSource.Standing,
                UseActionLanes = true,
            };
            return OrderEnqueue.Standing(game, order);
        }

        private static HashSet<int> ClaimedGeoTargets(FleetDB fleetDB, int selfId)
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
                        && child.AttachedManager.TryGetEntityById(warp.TargetEntityGuid, out var dest)
                        && dest.HasDataBlob<GeoSurveyableDB>())
                        claimed.Add(dest.Id);
                }
            }

            return claimed;
        }

        private static Entity? FindNearestGeo(Entity ship, int factionId, HashSet<int> claimed)
        {
            if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || ship.Manager == null)
                return null;

            Entity? best = null;
            double bestDist = double.MaxValue;
            foreach (var body in ship.AttachedManager.GetAllEntitiesWithDataBlob<GeoSurveyableDB>())
            {
                if (claimed.Contains(body.Id))
                    continue;
                if (!GeoSurveyTargets.IsEligible(body, factionId))
                    continue;
                if (!body.TryGetDataBlob<PositionDB>(out var bodyPos))
                    continue;
                double d = bodyPos.GetDistanceTo_m(shipPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = body;
                }
            }

            return best;
        }
    }

    private sealed class GravSurveyShipStep : IShipStandingStep
    {
        public bool TryEnqueue(Entity ship, Entity fleet, FleetDB fleetDB, ConditionalOrder mission)
        {
            if (!LooksLikeGrav(mission) || !ship.HasJPSurveyAbililty())
                return false;

            var game = ship.AttachedManager?.Game;
            if (game == null)
                return false;

            if (fleet.TryGetDataBlob<OrderableDB>(out var fq)
                && fq.ActionList.OfType<MoveToNearestGravSurveyAction>().Any())
                return false;

            var claimed = ClaimedGravTargets(fleetDB, ship.Id);
            var target = FindNearestAnomaly(ship, fleet.FactionOwnerID, claimed);
            if (target == null)
                return false;

            var order = new JPSurveyOrder(ship, target)
            {
                RequestingFactionGuid = fleet.FactionOwnerID,
                EntityCommandingGuid = ship.Id,
                Source = OrderSource.Standing,
                UseActionLanes = true,
            };
            return OrderEnqueue.Standing(game, order);
        }

        private static HashSet<int> ClaimedGravTargets(FleetDB fleetDB, int selfId)
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

        private static Entity? FindNearestAnomaly(Entity ship, int factionId, HashSet<int> claimed)
        {
            if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || ship.Manager == null)
                return null;

            Entity? best = null;
            double bestDist = double.MaxValue;
            foreach (var anomaly in ship.AttachedManager.GetAllEntitiesWithDataBlob<JPSurveyableDB>())
            {
                if (claimed.Contains(anomaly.Id))
                    continue;
                if (!anomaly.TryGetDataBlob<JPSurveyableDB>(out var db) || db.IsSurveyComplete(factionId))
                    continue;
                if (!anomaly.TryGetDataBlob<PositionDB>(out var pos))
                    continue;
                double d = pos.GetDistanceTo_m(shipPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = anomaly;
                }
            }

            return best;
        }
    }
}
