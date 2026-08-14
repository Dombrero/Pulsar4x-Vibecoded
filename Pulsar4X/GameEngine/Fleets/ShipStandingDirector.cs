using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
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
/// Fleet <see cref="FleetDB.StandingOrders"/> is a shared behavior template.
/// Each ship evaluates ENTER/EXIT and enqueues its own work independently.
/// </summary>
internal static class ShipStandingDirector
{
    private static readonly HashSet<int> s_coalesce = new();

    internal static ShipStandingStateDB GetOrAddState(Entity ship)
    {
        if (ship.TryGetDataBlob<ShipStandingStateDB>(out var state))
            return state;
        state = new ShipStandingStateDB();
        ship.SetDataBlob(state);
        return state;
    }

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

    /// <summary>Wake every child — idle ships pick work; busy ships may logistics-preempt.</summary>
    internal static void KickIdleChildren(Entity fleet)
    {
        if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB) || fleetDB.StandingOrders.Count == 0)
            return;

        foreach (var child in fleetDB.Children)
        {
            if (!child.HasDataBlob<ShipInfoDB>())
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
            {
                TryPreemptWhileBusy(ship, shipOrders);
                return;
            }
        }

        var fleet = FleetLookup.FindFleetContainingShip(ship);
        if (!fleet.IsValid || !fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            return;
        if (fleetDB.StandingOrders.Count == 0)
            return;

        // Issued fleet orders still pause standing for all children.
        if (fleet.TryGetDataBlob<OrderableDB>(out var fleetOrders)
            && fleetOrders.ActionList.Any(a => a.Source == OrderSource.Issued))
            return;

        if (fleet.TryGetDataBlob<GoalsDB>(out var goalsDB)
            && goalsDB.GivenGoal != null
            && string.IsNullOrEmpty(goalsDB.GivenGoal.ParentGoalId)
            && goalsDB.GivenGoal.Status is GoalStatus.Pending or GoalStatus.Active)
            return;

        var state = GetOrAddState(ship);
        OnShipSystemChanged(ship, state);

        DateTime gameTime = ship.StarSysDateTime;
        if (state.SuppressUntil.HasValue)
        {
            if (gameTime < state.SuppressUntil.Value)
                return;
            state.SuppressUntil = null;
        }

        if (state.ActiveStandingOrderIndex >= fleetDB.StandingOrders.Count)
            state.ActiveStandingOrderIndex = -1;

        int enterMatch = ShipStandingEvaluator.FindFirstMatchingOrderIndex(
            ship, fleetDB, useExitThreshold: false);

        // --- Committed mission ---
        if (state.ActiveStandingOrderIndex >= 0)
        {
            int active = state.ActiveStandingOrderIndex;
            var activeOrder = fleetDB.StandingOrders[active];

            // Higher-priority ENTER may preempt this hull only.
            if (enterMatch >= 0 && enterMatch < active)
            {
                var enterOrder = fleetDB.StandingOrders[enterMatch];
                if (!ShouldDeferLogisticsPreempt(ship, fleet, activeOrder, enterOrder))
                {
                    DebugTraceLog.Info("Standing",
                        $"ship#{ship.Id}: PREEMPT [{active}] → [{enterMatch}] {OrderName(fleetDB, enterMatch)}",
                        gameTime);
                    AbortShipStandingWork(ship);
                    state.ActiveStandingOrderIndex = enterMatch;
                    if (!TryEnqueueMission(ship, fleet, fleetDB, enterOrder)
                        && !ShipStandingEvaluator.LooksLikeRefuel(enterOrder)
                        && !ShipStandingEvaluator.LooksLikeRecharge(enterOrder))
                        SuppressEmpty(ship, state, enterOrder);
                    return;
                }
            }

            if (ShipStandingEvaluator.OrderStillNeedsAction(ship, activeOrder))
            {
                if (!TryEnqueueMission(ship, fleet, fleetDB, activeOrder))
                {
                    // Stuck opportunity / no local target — release and re-pick.
                    if (ShipStandingEvaluator.LooksLikeRefuel(activeOrder)
                        && !ShipStandingEvaluator.ShipFuelBelow(ship, 30f)
                        && !ShipStandingEvaluator.ShipNeedsOpportunityTopOff(ship))
                    {
                        state.ActiveStandingOrderIndex = -1;
                    }
                    else if (ShipStandingEvaluator.LooksLikeRefuel(activeOrder)
                             || ShipStandingEvaluator.LooksLikeRecharge(activeOrder))
                    {
                        // Keep commitment — cargo may need another tick to attach.
                        return;
                    }
                    else
                    {
                        SuppressEmpty(ship, state, activeOrder);
                        return;
                    }
                }
                else
                {
                    return;
                }
            }
            else
            {
                state.ActiveStandingOrderIndex = -1;
                state.StatusMessage = null;
            }
        }

        // --- Idle: pick new mission ---
        if (enterMatch < 0)
        {
            UpdateIdleStatus(ship, state, fleetDB);
            return;
        }

        state.StatusMessage = null;
        state.ActiveStandingOrderIndex = enterMatch;
        var order = fleetDB.StandingOrders[enterMatch];
        DebugTraceLog.Info("Standing",
            $"ship#{ship.Id}: START [{enterMatch}] {OrderName(fleetDB, enterMatch)}",
            gameTime);
        if (!TryEnqueueMission(ship, fleet, fleetDB, order))
        {
            if (ShipStandingEvaluator.LooksLikeRefuel(order)
                || ShipStandingEvaluator.LooksLikeRecharge(order))
                return;
            SuppressEmpty(ship, state, order);
        }
    }

    private static void TryPreemptWhileBusy(Entity ship, OrderableDB shipOrders)
    {
        var fleet = FleetLookup.FindFleetContainingShip(ship);
        if (!fleet.IsValid || !fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            return;
        if (fleetDB.StandingOrders.Count == 0)
            return;
        if (fleet.TryGetDataBlob<OrderableDB>(out var fleetOrders)
            && fleetOrders.ActionList.Any(a => a.Source == OrderSource.Issued))
            return;

        var state = GetOrAddState(ship);
        if (state.ActiveStandingOrderIndex < 0
            || state.ActiveStandingOrderIndex >= fleetDB.StandingOrders.Count)
            return;

        int enterMatch = ShipStandingEvaluator.FindFirstMatchingOrderIndex(
            ship, fleetDB, useExitThreshold: false);
        int active = state.ActiveStandingOrderIndex;
        if (enterMatch < 0 || enterMatch >= active)
            return;

        var activeOrder = fleetDB.StandingOrders[active];
        var enterOrder = fleetDB.StandingOrders[enterMatch];
        if (ShouldDeferLogisticsPreempt(ship, fleet, activeOrder, enterOrder))
            return;

        DebugTraceLog.Info("Standing",
            $"ship#{ship.Id}: PREEMPT busy [{active}] → [{enterMatch}] {OrderName(fleetDB, enterMatch)}",
            ship.StarSysDateTime);
        AbortShipStandingWork(ship);
        state.ActiveStandingOrderIndex = enterMatch;
        if (!TryEnqueueMission(ship, fleet, fleetDB, enterOrder)
            && !ShipStandingEvaluator.LooksLikeRefuel(enterOrder)
            && !ShipStandingEvaluator.LooksLikeRecharge(enterOrder))
            SuppressEmpty(ship, state, enterOrder);
    }

    private static bool ShouldDeferLogisticsPreempt(
        Entity ship, Entity fleet, ConditionalOrder activeOrder, ConditionalOrder enterOrder)
    {
        if (!ShipStandingEvaluator.LooksLikeRefuel(enterOrder)
            && !ShipStandingEvaluator.LooksLikeRecharge(enterOrder))
            return false;
        if (ShipStandingEvaluator.LooksLikeRefuel(activeOrder)
            || ShipStandingEvaluator.LooksLikeRecharge(activeOrder))
            return false;

        // Finish an in-progress local scan before yanking to the colony.
        if (ship.HasDataBlob<GeoSurveyingDB>() || ship.HasDataBlob<JPSurveyDB>())
            return true;

        // Empty tanks never defer (next hop cannot warp).
        if (ShipStandingEvaluator.LooksLikeRefuel(enterOrder))
        {
            try
            {
                var cargoLibrary = ship.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
                var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
                if (fuel != null
                    && ship.TryGetDataBlob<CargoStorageDB>(out var store)
                    && store.GetUnitsStored(fuel, includeEscro: false) <= 0)
                    return false;
            }
            catch { /* fall through */ }
        }

        if (ShipStandingEvaluator.LooksLikeSurvey(activeOrder)
            && ShipStandingEvaluator.LooksLikeRefuel(enterOrder))
        {
            float threshold = 30f;
            ShipStandingEvaluator.TryGetFuelLessThanThreshold(enterOrder, out threshold);
            if (!ShipStandingEvaluator.ShipFuelBelow(ship, threshold))
            {
                // Mid-survey opportunity: only top off when already docked.
                // Away from colony → keep surveying (siblings may tank independently).
                if (!ShipStandingEvaluator.ShipNeedsOpportunityTopOff(ship))
                    return true;
                return false;
            }
        }

        return StandingActionAffordability.CanAffordNextAction(fleet, activeOrder);
    }

    private static bool TryEnqueueMission(
        Entity ship, Entity fleet, FleetDB fleetDB, ConditionalOrder mission)
    {
        if (ShipStandingEvaluator.LooksLikeRefuel(mission))
            return TryEnqueueRefuel(ship, fleet, fleetDB);
        if (ShipStandingEvaluator.LooksLikeRecharge(mission))
            return TryEnqueueRecharge(ship, fleet);
        if (ShipStandingEvaluator.LooksLikeGeo(mission))
            return TryEnqueueGeo(ship, fleet, fleetDB);
        if (ShipStandingEvaluator.LooksLikeGrav(mission))
            return TryEnqueueGrav(ship, fleet, fleetDB);
        return false;
    }

    private static bool TryEnqueueRefuel(Entity ship, Entity fleet, FleetDB fleetDB)
    {
        var game = ship.AttachedManager?.Game;
        if (game == null || !ship.HasDataBlob<WarpAbilityDB>())
            return false;
        if (!ship.TryGetDataBlob<PositionDB>(out var shipPos))
            return false;

        var cargoLibrary = fleet.GetFactionOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
        if (!FleetFuel.HasFuelCapacity(ship, cargoLibrary))
            return false;
        var (fuel, _) = ship.GetFuelInfo(cargoLibrary);
        if (fuel == null || !ship.TryGetDataBlob<CargoStorageDB>(out var store))
            return false;
        if (CargoMath.GetFreeUnitSpace(store, fuel, includeEscro: false) <= 0)
            return false;

        Entity? colony = RefuelColonySearch.FindNearestColonyInSystem(
            ship.AttachedManager, fleet.FactionOwnerID, shipPos, fleetDB.LastRefuelColonyId);

        if (colony == null)
        {
            // Cross-system return — this hull jumps home alone.
            if (RefuelColonySearch.TryResolveRefuelSystemId(
                    game, fleet, fleet.FactionOwnerID, fleetDB, out var targetSystemId)
                && RefuelColonySearch.TryFindJumpGateTowardSystem(
                    game, fleet, ship.AttachedManager, fleet.FactionOwnerID, targetSystemId, shipPos, out var jumpGate)
                && jumpGate != null
                && jumpGate.OwningEntity.IsValid)
            {
                RefuelColonySearch.RememberLocalColonyAsRefuelSite(fleet, fleetDB, fleet.FactionOwnerID);
                return EnqueueShipJumpHome(ship, jumpGate);
            }

            return false;
        }

        if (FleetOrderCleanup.IsShipAtColony(ship, colony))
        {
            if (!colony.TryGetDataBlob<CargoStorageDB>(out var colonyStore)
                || !colonyStore.TypeStores.ContainsKey(fuel.CargoTypeID)
                || !store.TypeStores.ContainsKey(fuel.CargoTypeID))
                return false;

            long colonyUnits = CargoMath.GetUnitsStored(colonyStore, fuel, includeEscro: false);
            if (colonyUnits <= 0)
                return false;

            CargoTransferOrder.EnsureMinimumTransferCapability(colonyStore);
            CargoTransferOrder.EnsureMinimumTransferCapability(store);
            CargoTransferOrder.ReleaseOrphanEscrow(colonyStore);
            CargoTransferOrder.ReleaseOrphanEscrow(store);

            FleetOrderCleanup.AbortShipWarpsOnlyOnEntity(ship);
            CargoTransferOrder.CreateCommands(
                fleet.FactionOwnerID, ship, colony, fuel,
                CargoTransferOrder.Conditionals.WaitTillFull, OrderSource.Standing);
            RefuelColonySearch.RememberRefuelSite(fleetDB, colony);
            bool lasting = ship.HasDataBlob<CargoTransferDB>()
                || (ship.TryGetDataBlob<OrderableDB>(out var after)
                    && after.ActionList.OfType<CargoTransferOrder>().Any());
            return lasting;
        }

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
                $"ship#{ship.Id} refuel warp failed: {ex.Message}",
                ship.StarSysDateTime);
            return false;
        }
    }

    private static bool EnqueueShipJumpHome(Entity ship, JumpPointDB jumpGate)
    {
        var game = ship.AttachedManager?.Game;
        if (game == null)
            return false;

        var gateEntity = jumpGate.OwningEntity;
        FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);

        bool ok = false;
        var shipParent = ship.TryGetDataBlob<PositionDB>(out var pos) ? pos.Parent : null;
        if (shipParent != gateEntity && ship.HasDataBlob<WarpAbilityDB>())
        {
            try
            {
                var warp = WarpMoveCommand.CreateCommandEZ(ship, gateEntity, ship.StarSysDateTime);
                warp.Source = OrderSource.Standing;
                ok = OrderEnqueue.Standing(game, warp);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Warn("Standing",
                    $"ship#{ship.Id} jump-home warp failed: {ex.Message}",
                    ship.StarSysDateTime);
                return false;
            }
        }

        var jumpCmd = ShipJumpCommand.Create(ship, jumpGate);
        jumpCmd.Source = OrderSource.Standing;
        bool jumpOk = OrderEnqueue.Standing(game, jumpCmd);
        return ok || jumpOk;
    }

    private static bool TryEnqueueRecharge(Entity ship, Entity fleet)
    {
        var game = ship.AttachedManager?.Game;
        if (game == null || !ship.HasDataBlob<WarpAbilityDB>())
            return false;
        if (!FleetEnergy.NeedsColonyRecharge(ship))
            return false;
        if (!ship.TryGetDataBlob<PositionDB>(out var shipPos))
            return false;

        Entity? colony = null;
        double best = double.MaxValue;
        foreach (var c in ship.AttachedManager.GetAllEntitiesWithDataBlob<ColonyInfoDB>())
        {
            if (c.FactionOwnerID != fleet.FactionOwnerID)
                continue;
            if (!c.TryGetDataBlob<PositionDB>(out var cp))
                continue;
            double d = cp.GetDistanceTo_m(shipPos);
            if (d < best)
            {
                best = d;
                colony = c;
            }
        }

        if (colony == null)
            return false;

        if (FleetOrderCleanup.IsShipAtColony(ship, colony))
        {
            if (ship.HasDataBlob<EnergyRechargeDB>())
                return true;
            double rate = EnergyRechargeHelper.GetEffectiveRechargeRateKW(colony, ship);
            if (rate <= 0)
                return false;
            ship.SetDataBlob(new EnergyRechargeDB(colony, rate));
            return true;
        }

        try
        {
            FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);
            var warp = WarpMoveCommand.CreateCommandEZ(ship, colony, ship.StarSysDateTime);
            warp.Source = OrderSource.Standing;
            return OrderEnqueue.Standing(game, warp);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Warn("Standing",
                $"ship#{ship.Id} recharge warp failed: {ex.Message}",
                ship.StarSysDateTime);
            return false;
        }
    }

    private static bool TryEnqueueGeo(Entity ship, Entity fleet, FleetDB fleetDB)
    {
        if (!ship.HasDataBlob<GeoSurveyAbilityDB>())
            return false;
        var game = ship.AttachedManager?.Game;
        if (game == null)
            return false;

        var claimed = ShipStandingEvaluator.ClaimedGeoTargets(fleetDB, ship.Id);
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

    private static bool TryEnqueueGrav(Entity ship, Entity fleet, FleetDB fleetDB)
    {
        if (!ship.HasJPSurveyAbililty())
            return false;
        var game = ship.AttachedManager?.Game;
        if (game == null)
            return false;

        var claimed = ShipStandingEvaluator.ClaimedGravTargets(fleetDB, ship.Id);
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

    private static void AbortShipStandingWork(Entity ship)
    {
        if (ship.TryGetDataBlob<OrderableDB>(out var orders))
            orders.ActionList.RemoveAll(a => a.Source == OrderSource.Standing);
        FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);
    }

    private static void SuppressEmpty(Entity ship, ShipStandingStateDB state, ConditionalOrder order)
    {
        state.ActiveStandingOrderIndex = -1;
        state.SuppressUntil = ship.StarSysDateTime + TimeSpan.FromDays(1);
        if (ShipStandingEvaluator.LooksLikeGrav(order)
            && ShipStandingEvaluator.CountAssignableGrav(ship) == 0)
            state.StatusMessage = "Can't find more anomalies";
        else if (ShipStandingEvaluator.LooksLikeGeo(order)
                 && ShipStandingEvaluator.CountAssignableGeo(ship) == 0)
            state.StatusMessage = "Can't find more survey targets";
        DebugTraceLog.Info("Standing",
            $"ship#{ship.Id}: empty run '{order.Name}' — suppress 1d",
            ship.StarSysDateTime);
    }

    private static void UpdateIdleStatus(Entity ship, ShipStandingStateDB state, FleetDB fleetDB)
    {
        bool hasGrav = fleetDB.StandingOrders.Any(ShipStandingEvaluator.LooksLikeGrav);
        bool hasGeo = fleetDB.StandingOrders.Any(ShipStandingEvaluator.LooksLikeGeo);
        int anomalies = ShipStandingEvaluator.CountAssignableGrav(ship);
        int geo = ShipStandingEvaluator.CountAssignableGeo(ship);

        if (hasGrav && anomalies == 0)
            state.StatusMessage = "Can't find more anomalies";
        else if (hasGeo && geo == 0 && !hasGrav)
            state.StatusMessage = "Can't find more survey targets";
        else
            state.StatusMessage = null;

        if (!string.IsNullOrEmpty(state.StatusMessage))
            state.SuppressUntil = ship.StarSysDateTime + TimeSpan.FromDays(1);
    }

    private static void OnShipSystemChanged(Entity ship, ShipStandingStateDB state)
    {
        string? systemId = ship.AttachedManager?.ManagerID;
        if (string.IsNullOrEmpty(systemId))
            return;

        string? previous = state.LastSystemId;
        if (previous == systemId)
            return;

        state.LastSystemId = systemId;
        if (previous == null)
            return;

        // Local sync only — do not abort sibling ships.
        state.SuppressUntil = null;
        if (ship.TryGetDataBlob<OrderableDB>(out var orders))
        {
            orders.ActionList.RemoveAll(a =>
                a.Source == OrderSource.Standing
                && a is GeoSurveyOrder geo
                && geo.Target.IsValid
                && geo.Target.AttachedManager?.ManagerID != systemId);
        }

        // Keep logistics (warp-to-gate / jump / cargo) across the hop; drop stale survey warps.
        if (ship.TryGetDataBlob<OrderableDB>(out orders)
            && !orders.ActionList.Any(a =>
                a.Source == OrderSource.Standing
                && (a is ShipJumpCommand or CargoTransferOrder or WarpMoveCommand)))
        {
            FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);
            // Survey commitment may be invalid in the new system — re-evaluate next tick.
            if (state.ActiveStandingOrderIndex >= 0)
            {
                // Keep active index; StepCore will restart or release based on local conditions.
            }
        }
    }

    private static string OrderName(FleetDB fleetDB, int index)
    {
        if (index < 0 || index >= fleetDB.StandingOrders.Count)
            return "?";
        var o = fleetDB.StandingOrders[index];
        return string.IsNullOrEmpty(o.Name) ? $"order[{index}]" : o.Name;
    }

    private static Entity? FindNearestGeo(Entity ship, int factionId, HashSet<int> claimed)
    {
        if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || ship.AttachedManager == null)
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

    private static Entity? FindNearestAnomaly(Entity ship, int factionId, HashSet<int> claimed)
    {
        if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || ship.AttachedManager == null)
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
