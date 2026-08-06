using System;
using System.Collections.Generic;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Engine.Orders;

/// <summary>
/// Holds goals for an entity (ship, commander, fleet, etc.).
/// Ported from OrdersAndAI; works alongside <see cref="OrderableDB"/>.
/// </summary>
public class GoalsDB : BaseDataBlob
{
    public static readonly IReadOnlyDictionary<GoalType, float> BaseWeights = new Dictionary<GoalType, float>
    {
        [GoalType.StayAlive] = 1.0f,
        [GoalType.DontRunOutOfFuel] = 0.9f,
        [GoalType.MakeProfit] = 0.7f,
        [GoalType.HelpOwn] = 0.8f,
        [GoalType.HelpAllied] = 0.75f,
        [GoalType.HelpFrendly] = 0.74f,
        [GoalType.HelpNeutral] = 0.73f,
        [GoalType.ExploreJP] = 0.5f,
        [GoalType.SurveySystem] = 0.5f,
        [GoalType.ServeyBodies] = 0.5f,
        [GoalType.ScanAnomalies] = 0.5f,
        [GoalType.Mine] = 0.5f,
        [GoalType.Colonise] = 0.5f,
        [GoalType.ListeningPost] = 0.5f,
        [GoalType.Freighter] = 0.5f,
        [GoalType.Trade] = 0.5f,
        [GoalType.Scout] = 0.5f,
        [GoalType.Patrol] = 0.5f,
        [GoalType.Attack] = 0.5f,
        [GoalType.Defend] = 0.5f,
        [GoalType.Intercept] = 0.5f,
        [GoalType.Blockade] = 0.5f,
        [GoalType.Stealth] = 0.0f,
    };

    public Goal? GivenGoal { get; set; }

    public Dictionary<GoalType, float> PersonalityModifiers { get; } = new();
    public Dictionary<GoalType, float> CapabilityModifiers { get; } = new();
    public Dictionary<GoalType, float> OrdersModifiers { get; } = new();
    public Dictionary<GoalType, Goal> EffectiveGoals { get; } = new();

    public override object Clone()
    {
        return new GoalsDB
        {
            GivenGoal = GivenGoal,
        };
    }
}

public enum GoalType
{
    StayAlive,
    DontRunOutOfFuel,
    MakeProfit,
    HelpOwn,
    HelpAllied,
    HelpFrendly,
    HelpNeutral,
    ExploreJP,
    SurveySystem,
    ServeyBodies,
    ScanAnomalies,
    Mine,
    Colonise,
    ListeningPost,
    Freighter,
    Trade,
    Scout,
    Patrol,
    Attack,
    Defend,
    Intercept,
    Blockade,
    Stealth,
    MoveTo,
    RefuelAt,
    RearmAt,
    RepairAt,
}

public class Goal
{
    public string Id = Guid.NewGuid().ToString();
    public string ParentGoalId = "";
    public GoalType Type;
    public int TargetEntityID = -1;
    public float Weight = 0.5f;
    public GoalStatus Status = GoalStatus.Pending;
    public string Message = "";
}

public enum GoalStatus
{
    Pending,
    Active,
    Completed,
    Holding,
    Failing,
    Failed,
}
