using Pulsar4X.Engine;
using System.Collections.Concurrent;

namespace Pulsar4X.Engine.Orders;

/// <summary>
/// Thread-safe queue of immediate engine work (orders, agent wake-ups).
/// Drained on command submit, between time sub-pulses, and from the in-process client pump while paused.
/// </summary>
public sealed class EngineCommandInbox
{
    private enum Kind : byte
    {
        HandleOrder,
        WakeAgent,
    }

    private readonly struct Item
    {
        public readonly Kind Kind;
        public readonly EntityCommand? Command;
        public readonly Entity? Entity;

        public Item(Kind kind, EntityCommand? command, Entity? entity)
        {
            Kind = kind;
            Command = command;
            Entity = entity;
        }
    }

    private readonly ConcurrentQueue<Item> _queue = new();

    /// <summary>Result of the most recent <see cref="Kind.HandleOrder"/> processed during the last drain.</summary>
    public bool LastHandleOrderAccepted { get; private set; }

    public void EnqueueHandleOrder(EntityCommand command)
        => _queue.Enqueue(new Item(Kind.HandleOrder, command, null));

    public void EnqueueWakeAgent(Entity entity)
        => _queue.Enqueue(new Item(Kind.WakeAgent, null, entity));

    /// <summary>Processes all pending items. Safe to call reentrantly from Process (nested follow-up orders).</summary>
    public void Drain(Game game)
    {
        while (_queue.TryDequeue(out var item))
            Process(game, item);
    }

    private void Process(Game game, Item item)
    {
        switch (item.Kind)
        {
            case Kind.HandleOrder when item.Command != null:
                LastHandleOrderAccepted = game.OrderHandler?.HandleOrder(item.Command) ?? false;
                break;
            case Kind.WakeAgent when item.Entity != null:
                AgentProcessor.ProcessEntityStatic(item.Entity, item.Entity.StarSysDateTime);
                break;
        }
    }
}
