using System;
using System.Collections.Generic;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.Movement;

namespace Pulsar4X.GeoSurveys;

/// <summary>Ship-level geo survey goal → move (if needed) + <see cref="GeoSurveyOrder"/>.</summary>
public class ScanBodyPlan : IGoalToActionsPlanner
{
    public GoalType Type => GoalType.ServeyBodies;

    public IEnumerable<EntityCommand> Plan(Goal goal, Entity ship)
    {
        if (!ship.HasOrChildHasAbility<GeoSurveyAbilityDB>())
            return Fail(goal, "no geo-survey capability");

        // Our GeoSurveyOrder already warps then surveys — emit it as the single action.
        if (ship.Manager == null
            || !ship.Manager.TryGetGlobalEntityById(goal.TargetEntityID, out var target))
            return Fail(goal, "Target not found");

        if (!MovePlanner.CanMove(ship, out var immobile) && !IsAlreadyNear(ship, target))
            return Fail(goal, immobile);

        return new EntityCommand[]
        {
            GeoSurveyOrder.CreateCommand(ship.FactionOwnerID, ship, target)
        };
    }

    static bool IsAlreadyNear(Entity ship, Entity target)
    {
        if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || !target.TryGetDataBlob<PositionDB>(out var tgtPos))
            return false;
        return (shipPos.AbsolutePosition - tgtPos.AbsolutePosition).Length() < 50_000;
    }

    static IEnumerable<EntityCommand> Fail(Goal goal, string message)
    {
        goal.Status = GoalStatus.Failed;
        goal.Message = message;
        return Array.Empty<EntityCommand>();
    }
}

/// <summary>Fleet-level geo survey goal → hand the same target to capable ships.</summary>
public class ScanSystemBodiesPlan : IGoalToGoalsPlanner
{
    public GoalType Type => GoalType.ServeyBodies;

    public IEnumerable<(Entity subordinate, Goal goal)> Plan(Goal goal, Entity fleet)
    {
        if (!fleet.TryGetDataBlob<FleetDB>(out var db))
        {
            goal.Status = GoalStatus.Failed;
            goal.Message = "We have no subordinates to manage";
            yield break;
        }

        bool any = false;
        foreach (var subunit in db.Children)
        {
            if (!subunit.HasOrChildHasAbility<GeoSurveyAbilityDB>())
                continue;
            if (!MovePlanner.CanMove(subunit, out _))
                continue;

            any = true;
            yield return (subunit, new Goal
            {
                Type = GoalType.ServeyBodies,
                TargetEntityID = goal.TargetEntityID,
            });
        }

        if (!any)
        {
            goal.Status = GoalStatus.Failed;
            goal.Message = "no capable subordinates can reach or survey the target";
        }
    }
}
