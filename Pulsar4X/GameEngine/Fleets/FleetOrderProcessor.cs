using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
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
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Names;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Standing-order evaluator with commitment (no flicker / ping-pong).
    ///
    /// Model:
    /// 1. Player Issue Orders always win — standing never touches an Issued queue.
    /// 2. List order = priority (index 0 highest). First matching ENTER condition wins.
    /// 3. Once a standing order is committed, it stays active until its work is done AND
    ///    its EXIT condition is met (fuel ENTER &lt;30% / EXIT = tanks full — WaitTillFull).
    /// 4. Higher priority may preempt lower priority only when the higher order's ENTER
    ///    condition is true and we are not already committed to that same higher order.
    /// 5. Ship-level work (cargo transfer, warp) counts as in-progress — empty fleet queue
    ///    alone must not re-fire standing every tick.
    /// </summary>
    public class FleetOrderProcessor : IHotloopProcessor
    {
        /// <summary>Legacy hysteresis; colony recharge now exits when battery-only hulls are full.</summary>
        public const float EnergyExitHysteresisPercent = 40f;

        public TimeSpan RunFrequency => TimeSpan.FromHours(1);

        public TimeSpan FirstRunOffset => TimeSpan.FromHours(1);

        public Type GetParameterType => typeof(FleetDB);

        public void Init(Game game)
        {
        }

        public void ProcessEntity(Entity entity, int deltaSeconds)
        {
            if (entity.TryGetDataBlob<FleetDB>(out var fleetDB))
                Process(fleetDB);
        }

        public int ProcessManager(EntityManager manager, int deltaSeconds)
        {
            var entities = manager.GetAllDataBlobsOfType<FleetDB>();
            foreach (var db in entities)
                Process(db);
            return Math.Max(entities.Count, 1);
        }

        private static void Process(FleetDB fleetDB)
        {
            try
            {
                ProcessCore(fleetDB);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("Standing",
                    $"FleetOrderProcessor crashed fleet={fleetDB.OwningEntity?.Id}: {ex.Message}",
                    fleetDB.OwningEntity?.StarSysDateTime);
                System.Diagnostics.Debug.WriteLine(
                    $"FleetOrderProcessor failed on fleet {fleetDB.OwningEntity?.Id}: {ex}");
            }
        }

        private static void ProcessCore(FleetDB fleetDB)
        {
            if (fleetDB.StandingOrders.Count == 0)
            {
                fleetDB.ActiveStandingOrderIndex = -1;
                return;
            }

            if (fleetDB.FlagShipID == -1)
                return;

            var fleet = fleetDB.OwningEntity;
            if (fleet == null || !fleet.TryGetDataBlob<OrderableDB>(out var orderableDB))
                return;

            FleetFlagshipSync.TryResolveFlagship(fleet, fleetDB, out _);

            DateTime gameTime = fleet.StarSysDateTime;
            string fleetName = FleetLabel(fleet);

            FleetStandingSystemSync.OnFlagshipSystemChanged(fleet, fleetDB);

            // Issue Orders outrank Standing.
            if (orderableDB.ActionList.Any(a => a.Source == OrderSource.Issued))
            {
                DebugTraceLog.Trace("Standing",
                    $"{fleetName}: idle — Issue Orders still queued [{QueueSummary(orderableDB)}]",
                    gameTime);
                return;
            }

            // Goals (MoveTo / GeoSurvey Issue path) sit on GoalsDB, not the fleet ActionList —
            // treat an active top-level goal like Issued so Standing does not fight ship warps.
            if (fleet.TryGetDataBlob<GoalsDB>(out var goalsDB)
                && goalsDB.GivenGoal != null
                && string.IsNullOrEmpty(goalsDB.GivenGoal.ParentGoalId)
                && goalsDB.GivenGoal.Status is GoalStatus.Pending or GoalStatus.Active)
            {
                DebugTraceLog.Trace("Standing",
                    $"{fleetName}: idle — Goal {goalsDB.GivenGoal.Type} still {goalsDB.GivenGoal.Status}",
                    gameTime);
                return;
            }

            // Back off after an empty standing run (no targets / instant finish).
            if (fleetDB.StandingSuppressUntil.HasValue)
            {
                if (gameTime < fleetDB.StandingSuppressUntil.Value)
                {
                    // Once per suppress window is enough — do not spam every hotloop hour.
                    DebugTraceLog.Trace("Standing",
                        $"{fleetName}: idle — standing suppressed until {fleetDB.StandingSuppressUntil.Value:yyyy-MM-dd HH:mm} " +
                        $"(empty run / no travel target)",
                        gameTime);
                    return;
                }
                fleetDB.StandingSuppressUntil = null;
            }

            ClampActiveIndex(fleetDB);

            int enterMatch = FindFirstMatchingOrderIndex(fleetDB, fleet, useExitThreshold: false);
            double fuelPct = GetFleetAverageFuelPercent(fleet);

            // Recover commitment from queue after load / older saves.
            if (fleetDB.ActiveStandingOrderIndex < 0)
            {
                int inferred = FindRunningStandingOrderIndex(fleetDB, orderableDB);
                if (inferred >= 0)
                {
                    fleetDB.ActiveStandingOrderIndex = inferred;
                    DebugTraceLog.Info("Standing",
                        $"{fleetName}: recovered commitment → [{inferred}] {OrderName(fleetDB, inferred)} (fuel={fuelPct:0.#}%)",
                        gameTime);
                }
            }

            bool busy = HasStandingWorkInProgress(fleet, orderableDB, fleetDB.ActiveStandingOrderIndex);

            // --- Committed mission ---
            if (fleetDB.ActiveStandingOrderIndex >= 0)
            {
                int active = fleetDB.ActiveStandingOrderIndex;
                var activeOrder = fleetDB.StandingOrders[active];

                // Higher-priority ENTER may preempt a lower-priority commitment —
                // unless the active mission's next action is still affordable (fuel+energy).
                if (enterMatch >= 0 && enterMatch < active)
                {
                    var enterOrder = fleetDB.StandingOrders[enterMatch];
                    if (StandingActionAffordability.ShouldDeferLogisticsPreempt(fleet, activeOrder, enterOrder))
                    {
                        DebugTraceLog.Info("Standing",
                            $"{fleetName}: defer logistics preempt [{active}] {OrderName(fleetDB, active)} " +
                            $"(next action still affordable; fuel={fuelPct:0.#}%)",
                            gameTime);
                    }
                    else
                    {
                        DebugTraceLog.Warn("Standing",
                            $"{fleetName}: PREEMPT [{active}] {OrderName(fleetDB, active)} → [{enterMatch}] {OrderName(fleetDB, enterMatch)} " +
                            $"(fuel={fuelPct:0.#}%, queue=[{QueueSummary(orderableDB)}])",
                            gameTime);
                        AbortStandingFleetWork(fleet, orderableDB);
                        fleetDB.ActiveStandingOrderIndex = enterMatch;
                        EnqueueStandingActions(fleet, orderableDB, enterOrder);
                        return;
                    }
                }

                if (busy)
                {
                    // Near-full exit can land while the last cargo ticks finish. Clear standing fleet
                    // work so we don't thrash; ship transfers keep running. New Refuel ENTRY is
                    // blocked while they linger.
                    if (!OrderStillNeedsAction(fleet, activeOrder))
                    {
                        DebugTraceLog.Info("Standing",
                            $"{fleetName}: release commitment [{active}] {OrderName(fleetDB, active)} " +
                            $"(fuel={fuelPct:0.#}% — exit met, clearing standing queue; ship work may linger)",
                            gameTime);
                        orderableDB.ActionList.RemoveAll(a => a.Source == OrderSource.Standing);
                        PublishOrdersChanged(fleet);
                        fleetDB.ActiveStandingOrderIndex = -1;
                        // Fall through to pick a new match.
                    }
                    else
                    {
                        return;
                    }
                }
                else if (OrderStillNeedsAction(fleet, activeOrder))
                {
                    // Work finished. Leave only when EXIT condition says we are done.
                    DebugTraceLog.Info("Standing",
                        $"{fleetName}: restart [{active}] {OrderName(fleetDB, active)} — still needs action " +
                        $"(fuel={fuelPct:0.#}%)",
                        gameTime);
                    EnqueueStandingActions(fleet, orderableDB, activeOrder);
                    return;
                }
                else
                {
                    DebugTraceLog.Info("Standing",
                        $"{fleetName}: release commitment [{active}] {OrderName(fleetDB, active)} " +
                        $"(fuel={fuelPct:0.#}% — exit condition met)",
                        gameTime);
                    fleetDB.ActiveStandingOrderIndex = -1;
                    // Fall through to pick a new match.
                }
            }

            // --- Idle: pick a new standing mission ---
            // Standing items already in the queue without a commitment: adopt or preempt.
            if (fleetDB.ActiveStandingOrderIndex < 0
                && orderableDB.ActionList.Any(a => a.Source == OrderSource.Standing))
            {
                int running = FindRunningStandingOrderIndex(fleetDB, orderableDB);
                if (enterMatch >= 0 && (running < 0 || enterMatch < running))
                {
                    ConditionalOrder? runningOrder = running >= 0 ? fleetDB.StandingOrders[running] : null;
                    var enterOrder = fleetDB.StandingOrders[enterMatch];
                    if (runningOrder != null
                        && StandingActionAffordability.ShouldDeferLogisticsPreempt(fleet, runningOrder, enterOrder))
                    {
                        DebugTraceLog.Info("Standing",
                            $"{fleetName}: defer orphan logistics preempt running={running} " +
                            $"(next action still affordable)",
                            gameTime);
                        fleetDB.ActiveStandingOrderIndex = running;
                        return;
                    }

                    DebugTraceLog.Warn("Standing",
                        $"{fleetName}: adopt/preempt orphan queue running={running} → enter={enterMatch} {OrderName(fleetDB, enterMatch)}",
                        gameTime);
                    AbortStandingFleetWork(fleet, orderableDB);
                    fleetDB.ActiveStandingOrderIndex = enterMatch;
                    EnqueueStandingActions(fleet, orderableDB, enterOrder);
                    return;
                }

                if (running >= 0)
                {
                    fleetDB.ActiveStandingOrderIndex = running;
                    DebugTraceLog.Info("Standing",
                        $"{fleetName}: adopt orphan as commitment [{running}] {OrderName(fleetDB, running)}",
                        gameTime);
                    return;
                }

                DebugTraceLog.Warn("Standing",
                    $"{fleetName}: clearing unrecognized standing queue [{QueueSummary(orderableDB)}]",
                    gameTime);
                AbortStandingFleetWork(fleet, orderableDB);
                return;
            }

            // Only block starting a NEW Refuel/Recharge while transfers are already filling —
            // never block Grav/Geo survey re-entry after an Issue Order.
            if (enterMatch >= 0
                && OrderLooksLikeRefuel(fleetDB.StandingOrders[enterMatch])
                && FleetShipsHaveRefuelWork(fleet))
                return;

            if (enterMatch >= 0
                && OrderLooksLikeRecharge(fleetDB.StandingOrders[enterMatch])
                && FleetShipsHaveRechargeWork(fleet))
                return;

            if (enterMatch < 0)
            {
                // Explain Idle: usually no unsurveyed anomalies / geo targets, or conditions not met.
                int anomalies = CountUnsurveyedAnomalies(fleet);
                int geo = CountUnsurveyedGeo(fleet);
                UpdateStandingStatusWhenIdle(fleetDB, anomalies, geo);

                // No targets left: only re-check once per day (not every standing hour).
                if (!string.IsNullOrEmpty(fleetDB.StandingStatusMessage)
                    && (anomalies == 0 || geo == 0))
                {
                    fleetDB.StandingSuppressUntil = gameTime + TimeSpan.FromDays(1);
                }

                bool staleCounts = anomalies < 0 || geo < 0;
                if (staleCounts)
                {
                    fleetDB.StandingSuppressUntil = gameTime + TimeSpan.FromDays(1);
                    DebugTraceLog.Warn("Standing",
                        $"{fleetName}: idle — flagship system unresolved (anomalies={anomalies}, geo={geo}); " +
                        "standing suppressed 1 day — check FlagShipID after jump",
                        gameTime);
                    return;
                }

                DebugTraceLog.Trace("Standing",
                    $"{fleetName}: idle — no ENTER match (fuel={fuelPct:0.#}%, " +
                    $"unsurveyed anomalies={anomalies}, geo={geo}, orders={fleetDB.StandingOrders.Count}" +
                    (string.IsNullOrEmpty(fleetDB.StandingStatusMessage)
                        ? ""
                        : $", status='{fleetDB.StandingStatusMessage}'") +
                    (fleetDB.StandingSuppressUntil.HasValue
                        ? $", next check {fleetDB.StandingSuppressUntil.Value:yyyy-MM-dd HH:mm}"
                        : "") + ")",
                    gameTime);
                return;
            }

            fleetDB.StandingStatusMessage = null;
            DebugTraceLog.Info("Standing",
                $"{fleetName}: START [{enterMatch}] {OrderName(fleetDB, enterMatch)} " +
                $"(fuel={fuelPct:0.#}%)",
                gameTime);
            fleetDB.ActiveStandingOrderIndex = enterMatch;
            EnqueueStandingActions(fleet, orderableDB, fleetDB.StandingOrders[enterMatch]);
        }

        private static void UpdateStandingStatusWhenIdle(FleetDB fleetDB, int anomalies, int geo)
        {
            bool hasGravStanding = false;
            bool hasGeoStanding = false;
            foreach (var order in fleetDB.StandingOrders)
            {
                if (order?.Actions == null)
                    continue;
                if (order.Actions.Any(a =>
                        a is MoveToNearestGravSurveyAction || a is JPSurveyOrder || a is MoveToNearestAnomalyAction))
                    hasGravStanding = true;
                if (order.Actions.Any(a =>
                        a is MoveToNearestGeoSurveyAction || a is GeoSurveyOrder))
                    hasGeoStanding = true;
            }

            if (hasGravStanding && anomalies == 0)
                fleetDB.StandingStatusMessage = "Can't find more anomalies";
            else if (hasGeoStanding && geo == 0 && !hasGravStanding)
                fleetDB.StandingStatusMessage = "Can't find more survey targets";
            else if (!hasGravStanding && !hasGeoStanding)
                fleetDB.StandingStatusMessage = null;
        }

        private static string FleetLabel(Entity fleet)
        {
            // Avoid "Name#Id" — '#' truncates when logs are pasted into Markdown/chat.
            string name = fleet.TryGetDataBlob<NameDB>(out var n)
                ? n.OwnersName
                : $"Fleet {fleet.Id}";
            return $"{name} id={fleet.Id}";
        }

        private static int CountUnsurveyedAnomalies(Entity fleet)
        {
            if (!UnsurveyedGeoCondition.TryGetFlagshipSystem(fleet, out var manager))
                return -1;
            return UnsurveyedAnomalyCondition.CountUnsurveyed(manager, fleet.FactionOwnerID);
        }

        private static int CountUnsurveyedGeo(Entity fleet)
        {
            if (!UnsurveyedGeoCondition.TryGetFlagshipSystem(fleet, out var manager))
                return -1;
            return GeoSurveyTargets.CountEligible(manager, fleet.FactionOwnerID);
        }

        private static string OrderName(FleetDB fleetDB, int index)
        {
            if (index < 0 || index >= fleetDB.StandingOrders.Count)
                return "?";
            var o = fleetDB.StandingOrders[index];
            return string.IsNullOrEmpty(o.Name) ? $"order[{index}]" : o.Name;
        }

        private static string QueueSummary(OrderableDB orderable)
        {
            if (orderable.ActionList.Count == 0)
                return "empty";
            return string.Join(", ",
                orderable.ActionList.Select(a => $"{a.GetType().Name}({a.Source})"));
        }

        private static int FindRunningStandingOrderIndex(FleetDB fleetDB, OrderableDB orderable)
        {
            int best = -1;
            for (int i = 0; i < fleetDB.StandingOrders.Count; i++)
            {
                if (IsStandingResponseInProgress(orderable, fleetDB.StandingOrders[i]))
                {
                    if (best < 0 || i < best)
                        best = i;
                }
            }

            return best;
        }

        private static void ClampActiveIndex(FleetDB fleetDB)
        {
            if (fleetDB.ActiveStandingOrderIndex >= fleetDB.StandingOrders.Count)
                fleetDB.ActiveStandingOrderIndex = -1;
        }

        /// <summary>
        /// True when the standing order should keep running (ENTER, or EXIT for fuel hysteresis).
        /// Empty conditions mean "always enter"; exit then follows the action's remaining work
        /// (survey targets / fuel), so a Grav_Survey with only an action still runs.
        /// </summary>
        internal static bool OrderStillNeedsAction(Entity fleet, ConditionalOrder order)
        {
            if (order == null)
                return false;

            bool noConditions = order.Condition?.ConditionItems == null
                                || order.Condition.ConditionItems.Count == 0;
            if (noConditions)
                return ActionStillHasWork(fleet, order);

            // Fuel: ENTER uses the condition threshold; EXIT stays until every tank is full.
            if (TryGetFuelLessThanThreshold(order, out _))
                return FleetFuel.AnyHasFreeTankSpace(fleet);

            // Energy colony recharge: EXIT when every battery-only (no generator) hull is full.
            if (TryGetEnergyLessThanThreshold(order, out _))
                return FleetEnergy.AnyHasFreeBatteryForColonyRecharge(fleet);

            return order.Condition?.Evaluate(fleet) ?? false;
        }

        /// <summary>
        /// When the player saved an action without a condition, infer whether work remains.
        /// </summary>
        private static bool ActionStillHasWork(Entity fleet, ConditionalOrder order)
        {
            if (order.Actions == null || order.Actions.Count == 0)
                return false;

            if (OrderLooksLikeRefuel(order))
                return FleetFuel.AnyHasFreeTankSpace(fleet);

            if (OrderLooksLikeRecharge(order))
                return FleetEnergy.AnyHasFreeBatteryForColonyRecharge(fleet);

            if (OrderLooksLikeSurvey(order))
            {
                bool wantsGrav = order.Actions.Any(a =>
                    a is MoveToNearestGravSurveyAction || a is JPSurveyOrder || a is MoveToNearestAnomalyAction);
                bool wantsGeo = order.Actions.Any(a =>
                    a is MoveToNearestGeoSurveyAction || a is GeoSurveyOrder);

                if (wantsGrav
                    && new UnsurveyedAnomalyCondition(0f, ComparisonType.GreaterThan).Evaluate(fleet))
                    return true;
                if (wantsGeo
                    && new UnsurveyedGeoCondition(0f, ComparisonType.GreaterThan).Evaluate(fleet))
                    return true;
                return false;
            }

            // Unknown action with no conditions: one-shot (do not loop forever).
            return false;
        }

        private static int FindFirstMatchingOrderIndex(FleetDB fleetDB, Entity fleet, bool useExitThreshold)
        {
            for (int i = 0; i < fleetDB.StandingOrders.Count; i++)
            {
                var order = fleetDB.StandingOrders[i];
                if (!order.IsValid || order.Actions == null || order.Actions.Count == 0)
                    continue;

                bool noConditions = order.Condition?.ConditionItems == null
                                    || order.Condition.ConditionItems.Count == 0;

                bool matches;
                if (noConditions)
                {
                    // Empty conditions used to be skipped → Grav_Survey with only an action
                    // never ENTERed. Treat as the action's natural trigger:
                    // survey → unsurveyed targets remain; refuel → fuel below 30%.
                    if (useExitThreshold)
                        matches = OrderStillNeedsAction(fleet, order);
                    else if (OrderLooksLikeRefuel(order))
                        matches = FleetFuel.AnyBelow(fleet, 30f)
                                  || FleetFuel.HasOpportunityTopOff(fleet);
                    else if (OrderLooksLikeRecharge(order))
                        matches = FleetEnergy.AnyColonyRechargeBelow(fleet, 30f);
                    else
                        matches = ActionStillHasWork(fleet, order);
                }
                else if (useExitThreshold)
                {
                    matches = OrderStillNeedsAction(fleet, order);
                }
                else
                {
                    matches = order.Condition?.Evaluate(fleet) ?? false;
                    // Docked with free tanks: ENTER Refuel even above the fuel threshold so
                    // fleets top off before departing for the next survey hop.
                    if (!matches
                        && OrderLooksLikeRefuel(order)
                        && FleetFuel.HasOpportunityTopOff(fleet))
                        matches = true;
                }

                if (matches)
                    return i;
            }

            return -1;
        }

        private static bool TryGetFuelLessThanThreshold(ConditionalOrder order, out float threshold)
        {
            threshold = 0;
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

        internal static double GetFleetAverageEnergyPercent(Entity fleet)
            => FleetEnergy.AveragePercent(fleet);

        internal static double GetFleetAverageFuelPercent(Entity fleet)
            => FleetFuel.AveragePercent(fleet);

        private static void AbortStandingFleetWork(Entity fleet, OrderableDB orderableDB)
        {
            orderableDB.ActionList.RemoveAll(a => a.Source == OrderSource.Standing);
            // Clear destination warps when switching missions — never kill cargo here.
            // Preempting survey→refuel must not wipe an in-progress tank fill.
            FleetOrderCleanup.AbortShipMovementOrders(fleet);

            PublishOrdersChanged(fleet);
        }

        private static void EnqueueStandingActions(Entity fleet, OrderableDB orderableDB, ConditionalOrder order)
        {
            // Never stack on top of existing standing queue items.
            if (orderableDB.ActionList.Any(a => a.Source == OrderSource.Standing))
            {
                DebugTraceLog.Warn("Standing",
                    $"{FleetLabel(fleet)}: enqueue skipped — standing already in queue [{QueueSummary(orderableDB)}]",
                    fleet.StarSysDateTime);
                return;
            }

            var game = fleet.AttachedManager?.Game;
            if (game == null) return;

            var actions = NormalizeStandingActions(order.Actions);
            if (actions.Count == 0)
            {
                DebugTraceLog.Warn("Standing",
                    $"{FleetLabel(fleet)}: enqueue skipped — normalized actions empty for '{order.Name}'",
                    fleet.StarSysDateTime);
                return;
            }

            foreach (var action in actions)
            {
                action.BindCommandingEntity(fleet);
                var clone = action.Clone();
                clone.BindCommandingEntity(fleet);
                // Standing path goes through HandleOrder (ActionList + ProcessEntity + OrdersChanged).
                OrderEnqueue.Standing(game, clone);
            }

            DebugTraceLog.Info("Standing",
                $"{FleetLabel(fleet)}: enqueued '{order.Name}' → [{QueueSummary(orderableDB)}]",
                fleet.StarSysDateTime);

            // Action evaporated in the same tick (no targets / instant finish). Clear commitment
            // and suppress re-ENTRY so we do not restart→enqueue→vanish every hotloop hour.
            if (fleet.TryGetDataBlob<FleetDB>(out var fleetDB)
                && !orderableDB.ActionList.Any(a => a.Source == OrderSource.Standing))
            {
                bool stillWants = OrderStillNeedsAction(fleet, order);
                fleetDB.ActiveStandingOrderIndex = -1;

                bool gravNoTargets = OrderLooksLikeSurvey(order)
                    && order.Actions.Any(a =>
                        a is MoveToNearestGravSurveyAction || a is JPSurveyOrder || a is MoveToNearestAnomalyAction)
                    && CountUnsurveyedAnomalies(fleet) == 0;

                if (gravNoTargets)
                {
                    fleetDB.StandingStatusMessage = "Can't find more anomalies";
                    // Re-scan at most once per day — avoids START→empty→START every hour.
                    fleetDB.StandingSuppressUntil = fleet.StarSysDateTime + TimeSpan.FromDays(1);
                    DebugTraceLog.Info("Standing",
                        $"{FleetLabel(fleet)}: release — Can't find more anomalies " +
                        $"(next check {fleetDB.StandingSuppressUntil.Value:yyyy-MM-dd HH:mm})",
                        fleet.StarSysDateTime);
                }
                else if (stillWants)
                {
                    fleetDB.StandingSuppressUntil = fleet.StarSysDateTime + TimeSpan.FromDays(1);
                    DebugTraceLog.Warn("Standing",
                        $"{FleetLabel(fleet)}: '{order.Name}' finished immediately while work remains " +
                        $"— suppressing standing for 1d (no target or travel failed)",
                        fleet.StarSysDateTime);
                }
                else
                {
                    DebugTraceLog.Info("Standing",
                        $"{FleetLabel(fleet)}: release commitment after empty run '{order.Name}'",
                        fleet.StarSysDateTime);
                }
            }
        }

        internal static List<EntityCommand> NormalizeStandingActions(IEnumerable<EntityCommand> actions)
        {
            var list = actions?.Where(a => a != null && a is not ResupplyAction).ToList()
                       ?? new List<EntityCommand>();

            if (list.Any(a => a is RefuelAction))
                list = list.Where(a => a is not MoveToNearestColonyAction).ToList();

            if (list.Any(a => a is RechargeEnergyAction))
                list = list.Where(a => a is not MoveToNearestColonyAction).ToList();

            return list;
        }

        /// <summary>
        /// Fleet queue OR ship-level work that belongs to the active / given standing mission.
        /// </summary>
        internal static bool HasStandingWorkInProgress(Entity fleet, OrderableDB orderable, int activeIndex)
        {
            ConditionalOrder? active = null;
            if (activeIndex >= 0
                && fleet.TryGetDataBlob<FleetDB>(out var fleetDB)
                && activeIndex < fleetDB.StandingOrders.Count)
            {
                active = fleetDB.StandingOrders[activeIndex];
            }

            if (active != null && IsStandingResponseInProgress(orderable, active))
                return true;

            // Any standing-sourced fleet orders count as busy.
            if (orderable.ActionList.Any(a => a.Source == OrderSource.Standing))
                return true;

            // Ship cargo / warp while we have a refuel commitment (or any refuel-shaped queue history).
            bool treatAsRefuel = active != null && OrderLooksLikeRefuel(active);
            if (treatAsRefuel && FleetShipsHaveRefuelWork(fleet))
                return true;

            bool treatAsRecharge = active != null && OrderLooksLikeRecharge(active);
            if (treatAsRecharge && FleetShipsHaveRechargeWork(fleet))
                return true;

            // Survey commitment: geo survey order or travel warps spawned by it.
            bool treatAsSurvey = active != null && OrderLooksLikeSurvey(active);
            if (treatAsSurvey && FleetShipsHaveSurveyWork(fleet, orderable))
                return true;

            // No commitment yet — still treat active ship fuel/energy transfers as busy so we don't
            // stamp a new Refuel/Recharge on top of an in-flight fill (empty fleet queue flicker).
            if (activeIndex < 0 && FleetShipsHaveRefuelWork(fleet))
                return true;
            if (activeIndex < 0 && FleetShipsHaveRechargeWork(fleet))
                return true;

            return false;
        }

        internal static bool IsStandingResponseInProgress(OrderableDB orderable, ConditionalOrder order)
        {
            if (order.Actions == null || order.Actions.Count == 0)
                return false;

            bool orderHasRefuel = OrderLooksLikeRefuel(order);
            bool orderHasRecharge = OrderLooksLikeRecharge(order);
            bool orderHasSurvey = OrderLooksLikeSurvey(order);

            var effectiveTypes = new HashSet<Type>(
                NormalizeStandingActions(order.Actions).Select(a => a.GetType()));

            foreach (var queued in orderable.ActionList)
            {
                if (orderHasRefuel && IsRefuelFleetOrder(queued))
                    return true;

                if (orderHasRecharge && IsRechargeFleetOrder(queued))
                    return true;

                if (orderHasSurvey && IsSurveyFleetOrder(queued))
                    return true;

                if (effectiveTypes.Contains(queued.GetType()))
                    return true;
            }

            var fleet = orderable.OwningEntity;
            if (fleet == null)
                return false;

            if (orderHasRefuel && FleetShipsHaveRefuelWork(fleet))
                return true;

            if (orderHasRecharge && FleetShipsHaveRechargeWork(fleet))
                return true;

            if (orderHasSurvey && FleetShipsHaveSurveyWork(fleet, orderable))
                return true;

            return false;
        }

        private static bool OrderLooksLikeRefuel(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a => a is RefuelAction);

        private static bool OrderLooksLikeRecharge(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a => a is RechargeEnergyAction);

        private static bool OrderLooksLikeSurvey(ConditionalOrder order)
            => order.Actions != null && order.Actions.Any(a =>
                a is MoveToNearestGeoSurveyAction
                || a is MoveToNearestGravSurveyAction
                || a is GeoSurveyOrder
                || a is JPSurveyOrder);

        private static bool IsRefuelFleetOrder(EntityCommand cmd)
            => cmd is RefuelAction
               || cmd is RefuelWhenAtColonyOrder
               || cmd is WarpFleetTowardsTargetOrder
               || cmd is MoveToNearestColonyAction
               || cmd is JumpOrder;

        private static bool IsRechargeFleetOrder(EntityCommand cmd)
            => cmd is RechargeEnergyAction
               || cmd is RechargeWhenAtColonyOrder
               || cmd is WarpFleetTowardsTargetOrder
               || cmd is MoveToNearestColonyAction;

        private static bool IsSurveyFleetOrder(EntityCommand cmd)
            => cmd is MoveToNearestGeoSurveyAction
               || cmd is MoveToNearestGravSurveyAction
               || cmd is GeoSurveyOrder
               || cmd is JPSurveyOrder;

        internal static bool FleetShipsHaveRefuelWork(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            foreach (var ship in fleetDB.Children.Where(c => !c.HasDataBlob<FleetDB>()))
            {
                if (ship.HasDataBlob<CargoTransferDB>())
                    return true;
                if (ship.TryGetDataBlob<OrderableDB>(out var shipOrders)
                    && shipOrders.ActionList.OfType<CargoTransferOrder>().Any())
                    return true;
            }

            return false;
        }

        internal static bool FleetShipsHaveRechargeWork(Entity fleet)
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            foreach (var ship in fleetDB.Children.Where(c => !c.HasDataBlob<FleetDB>()))
            {
                if (ship.HasDataBlob<EnergyRechargeDB>())
                    return true;
            }

            return false;
        }

        private static bool FleetShipsHaveSurveyWork(Entity fleet, OrderableDB fleetOrders)
        {
            if (fleetOrders.ActionList.Any(IsSurveyFleetOrder))
                return true;

            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
                return false;

            // Only count ship warps as survey work when a survey order is (or was) driving them —
            // checked via fleet queue survey orders above. Bare WarpMovingDB alone is NOT enough
            // (that false-positive blocked refuel preemption while surveying).
            return false;
        }

        private static void PublishOrdersChanged(Entity fleet)
        {
            if (fleet.Manager == null)
                return;

            _ = MessagePublisher.Instance.Publish(Message.Create(
                MessageTypes.OrdersChanged,
                entityId: fleet.Id,
                systemId: fleet.AttachedManager.ManagerID,
                factionId: fleet.FactionOwnerID));
        }
    }
}
