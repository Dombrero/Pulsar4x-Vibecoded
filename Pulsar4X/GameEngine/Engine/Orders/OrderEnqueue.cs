using System;
using Pulsar4X.Engine;

namespace Pulsar4X.Engine.Orders;

/// <summary>
/// Single entry points for queuing work on <see cref="OrderableDB"/> / agents.
/// Callers outside this type should prefer these over raw <see cref="IOrderHandler.HandleOrder"/>.
/// </summary>
public static class OrderEnqueue
{
    /// <summary>Queue one command without changing <see cref="EntityCommand.Source"/>.</summary>
    public static bool Enqueue(Game game, EntityCommand command)
    {
        game.CommandInbox.EnqueueHandleOrder(command);
        game.CommandInbox.Drain(game);
        return game.CommandInbox.LastHandleOrderAccepted;
    }

    /// <summary>Player/API issue order — pauses standing via <see cref="StandAloneOrderHandler"/>.</summary>
    public static bool Issued(Game game, EntityCommand command)
    {
        command.Source = OrderSource.Issued;
        game.CommandInbox.EnqueueHandleOrder(command);
        game.CommandInbox.Drain(game);
        return game.CommandInbox.LastHandleOrderAccepted;
    }

    /// <summary>Standing mission actions (already cloned/bound by fleet processor).</summary>
    public static bool Standing(Game game, EntityCommand command)
    {
        command.Source = OrderSource.Standing;
        game.CommandInbox.EnqueueHandleOrder(command);
        game.CommandInbox.Drain(game);
        return game.CommandInbox.LastHandleOrderAccepted;
    }

    /// <summary>Goal planner output — <see cref="EntityCommand.ParentGoalId"/> must already be set.</summary>
    public static bool FromGoal(Game game, EntityCommand command)
    {
        game.CommandInbox.EnqueueHandleOrder(command);
        game.CommandInbox.Drain(game);
        return game.CommandInbox.LastHandleOrderAccepted;
    }

    /// <summary>Immediate agent tick for one unit (commander host or hull).</summary>
    public static void WakeAgent(Game game, Entity unit)
    {
        game.CommandInbox.EnqueueWakeAgent(unit);
        game.CommandInbox.Drain(game);
    }
}
