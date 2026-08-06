using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Industry;
using Pulsar4X.Interfaces;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.People;
using Pulsar4X.Sensors;
using Pulsar4X.Ships;

namespace Pulsar4X.Engine.Orders;

public interface IGoalToActionsPlanner
{
    GoalType Type { get; }
    IEnumerable<EntityCommand> Plan(Goal goal, Entity ship);
}

public interface IGoalToGoalsPlanner
{
    GoalType Type { get; }
    IEnumerable<(Entity subordinate, Goal goal)> Plan(Goal goal, Entity fleet);
}

/// <summary>
/// Decomposes goals into fleet sub-goals or ship actions queued on <see cref="OrderableDB"/>.
/// Ported from OrdersAndAI without renaming the existing order stack.
/// </summary>
public class AgentProcessor : IInstanceProcessor
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RelayDelay = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<GoalType, IGoalToActionsPlanner> ActionPlanners;
    private static readonly Dictionary<GoalType, IGoalToGoalsPlanner> GoalPlanners;

    static AgentProcessor()
    {
        ActionPlanners = new Dictionary<GoalType, IGoalToActionsPlanner>();
        foreach (var planner in new IGoalToActionsPlanner[]
                 {
                     new MoveToPlan(),
                     new ScanBodyPlan(),
                 })
        {
            ActionPlanners[planner.Type] = planner;
        }

        GoalPlanners = new Dictionary<GoalType, IGoalToGoalsPlanner>();
        foreach (var planner in new IGoalToGoalsPlanner[]
                 {
                     new MoveSubordinatesTo(),
                     new ScanSystemBodiesPlan(),
                 })
        {
            GoalPlanners[planner.Type] = planner;
        }
    }

    internal override void ProcessEntity(Entity entity, DateTime atDateTime)
        => ProcessEntityStatic(entity, atDateTime);

    public static void ProcessEntityStatic(Entity entity, DateTime atDateTime)
    {
        Entity managedEntity = entity;
        if (entity.TryGetDataBlob<CommanderDB>(out var commanderDB) && commanderDB.AssignedTo != -1)
        {
            if (entity.Manager == null || !entity.Manager.TryGetGlobalEntityById(commanderDB.AssignedTo, out managedEntity))
                return;
        }

        if (!managedEntity.TryGetDataBlob<GoalsDB>(out var goalsDB))
            return;

        if (managedEntity.HasDataBlob<FleetDB>())
            GoalsProcessor(entity, goalsDB, managedEntity, atDateTime);
        else if (managedEntity.HasDataBlob<ShipInfoDB>())
            ActionsProcessor(entity, goalsDB, managedEntity, atDateTime);
    }

    static void GoalsProcessor(Entity agentHost, GoalsDB goalsDB, Entity fleet, DateTime atDateTime)
    {
        var goal = goalsDB.GivenGoal;
        if (goal == null) return;
        if (goal.Status is GoalStatus.Completed or GoalStatus.Failed) return;

        switch (goal.Status)
        {
            case GoalStatus.Pending:
            {
                if (!GoalPlanners.TryGetValue(goal.Type, out var planner))
                {
                    Fail(goal, $"no planner for {goal.Type}");
                    return;
                }

                try
                {
                    foreach (var (subordinate, subGoal) in planner.Plan(goal, fleet))
                    {
                        subGoal.ParentGoalId = goal.Id;
                        AssignGoal(subordinate, subGoal, atDateTime + RelayDelay);
                    }
                }
                catch (Exception e)
                {
                    Fail(goal, e.Message);
                    return;
                }

                if (goal.Status != GoalStatus.Pending)
                    break;

                goal.Status = GoalStatus.Active;
                ScheduleAgent(agentHost, atDateTime + RecheckInterval);
                break;
            }
            case GoalStatus.Active:
            {
                var mine = SubGoalsOf(fleet, goal);
                if (mine.Count > 0 && mine.All(g => g.Status == GoalStatus.Completed))
                    goal.Status = GoalStatus.Completed;
                else if (mine.Any(g => g.Status == GoalStatus.Failed))
                    Fail(goal, "a subordinate's goal failed");
                else
                    ScheduleAgent(agentHost, atDateTime + RecheckInterval);
                break;
            }
        }
    }

    static void ActionsProcessor(Entity agentHost, GoalsDB goalsDB, Entity ship, DateTime atDateTime)
    {
        var goal = goalsDB.GivenGoal;
        if (goal == null) return;
        if (goal.Status is GoalStatus.Completed or GoalStatus.Failed) return;
        if (!ship.TryGetDataBlob<OrderableDB>(out var queue)) return;
        if (ship.AttachedManager?.Game?.OrderHandler == null) return;

        switch (goal.Status)
        {
            case GoalStatus.Pending:
            {
                if (!ActionPlanners.TryGetValue(goal.Type, out var planner))
                {
                    Fail(goal, $"no planner for {goal.Type}");
                    return;
                }

                foreach (var action in planner.Plan(goal, ship))
                {
                    action.ParentGoalId = goal.Id;
                    ship.AttachedManager.Game.OrderHandler.HandleOrder(action);
                }

                if (goal.Status == GoalStatus.Pending)
                    goal.Status = GoalStatus.Active;
                else
                    break;

                ScheduleAgent(agentHost, atDateTime + RecheckInterval);
                break;
            }
            case GoalStatus.Active:
            {
                var actions = queue.ActionsFor(goal);

                if (actions.Any(a => a.Status == ActionStatus.Failed))
                {
                    Fail(goal, "an action failed");
                    queue.ClearFor(goal);
                }
                else
                {
                    queue.ActionList.RemoveAll(a =>
                        a.ParentGoalId == goal.Id && a.Status == ActionStatus.Succeeded);

                    if (!queue.ActionsFor(goal).Any())
                        goal.Status = GoalStatus.Completed;
                    else
                        ScheduleAgent(agentHost, atDateTime + RecheckInterval);
                }
                break;
            }
        }
    }

    internal static GoalsDB GetOrCreateGoals(Entity entity)
    {
        if (!entity.TryGetDataBlob<GoalsDB>(out var goals))
        {
            goals = new GoalsDB();
            entity.SetDataBlob(goals);
        }
        return goals;
    }

    /// <summary>Record a goal and wake the unit's agent (now or after relay delay).</summary>
    public static void AssignGoal(Entity unit, Goal goal, DateTime? when = null)
    {
        GetOrCreateGoals(unit).GivenGoal = goal;

        if (when == null)
            RunAgentNow(unit);
        else
            ScheduleAgent(unit, when.Value);
    }

    static List<Goal> SubGoalsOf(Entity fleet, Goal goal)
    {
        var subGoals = new List<Goal>();
        if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB)) return subGoals;

        foreach (var child in fleetDB.Children)
        {
            if (child.TryGetDataBlob<GoalsDB>(out var childGoals)
                && childGoals.GivenGoal != null
                && childGoals.GivenGoal.ParentGoalId == goal.Id)
            {
                subGoals.Add(childGoals.GivenGoal);
            }
        }

        return subGoals;
    }

    static void Fail(Goal goal, string message)
    {
        goal.Status = GoalStatus.Failed;
        goal.Message = message;
    }

    internal static void ScheduleAgent(Entity unit, DateTime when)
    {
        unit.AttachedManager?.ManagerSubpulses.AddEntityInterupt(when, nameof(AgentProcessor), unit);
    }

    internal static void RunAgentNow(Entity unit)
    {
        ProcessEntityStatic(unit, unit.StarSysDateTime);
    }

    public static void PruneImpossibleGoals(GoalsDB goalsDB, Entity entity)
    {
        goalsDB.CapabilityModifiers.Clear();

        foreach (var type in GoalsDB.BaseWeights.Keys)
        {
            bool canDo = type switch
            {
                GoalType.StayAlive or GoalType.DontRunOutOfFuel => true,
                GoalType.ExploreJP => entity.HasOrChildHasAbility<SensorAbilityDB>(),
                GoalType.SurveySystem or GoalType.ServeyBodies => entity.HasOrChildHasAbility<GeoSurveyAbilityDB>(),
                GoalType.ScanAnomalies => entity.HasOrChildHasAbility<JPSurveyAbilityDB>(),
                GoalType.Mine => entity.HasOrChildHasAbility<MiningDB>(),
                GoalType.ListeningPost => entity.HasOrChildHasAbility<SensorAbilityDB>(),
                GoalType.Scout => entity.HasOrChildHasAbility<SensorAbilityDB>(),
                GoalType.MakeProfit or GoalType.Freighter or GoalType.Trade => false,
                GoalType.Colonise => false,
                _ => false
            };

            if (!canDo)
                goalsDB.CapabilityModifiers[type] = -1f;
        }
    }

    public static void RecalculateEffectiveGoals(GoalsDB goalsDB, AgentDB? agentDB)
    {
        goalsDB.EffectiveGoals.Clear();

        foreach (var kvp in GoalsDB.BaseWeights)
        {
            GoalType type = kvp.Key;
            float multiplier = 1.0f;
            multiplier *= GetModifier(goalsDB.CapabilityModifiers, type);
            if (agentDB != null)
                multiplier *= GetPersonalityMultiplier(agentDB, type);
            multiplier *= GetModifier(goalsDB.OrdersModifiers, type);

            goalsDB.EffectiveGoals[type] = new Goal
            {
                Type = type,
                Weight = kvp.Value * multiplier
            };
        }
    }

    private static float GetModifier(Dictionary<GoalType, float> modifiers, GoalType type)
        => modifiers.TryGetValue(type, out var value) ? value : 1.0f;

    private static float GetPersonalityMultiplier(AgentDB agent, GoalType type)
        => type switch
        {
            GoalType.StayAlive or GoalType.DontRunOutOfFuel => agent.Caution,
            GoalType.ExploreJP or GoalType.SurveySystem or GoalType.ScanAnomalies => agent.Curiosity,
            GoalType.MakeProfit or GoalType.Trade or GoalType.Freighter => agent.Greed,
            GoalType.Attack or GoalType.Patrol or GoalType.Scout => agent.Aggression,
            _ => 1.0f
        };
}
