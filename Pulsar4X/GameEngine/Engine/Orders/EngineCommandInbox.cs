using Pulsar4X.Engine;
using System.Collections.Concurrent;
using System.Threading;

namespace Pulsar4X.Engine.Orders;

/// <summary>
/// Thread-safe queue of immediate engine work (orders, agent wake-ups).
/// Drained on command submit, between time sub-pulses, the continuous engine pump, and the in-process UI pump.
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
    private readonly object _drainLock = new();

    /// <summary>Reentrant drain depth on the current thread (nested Enqueue while Process runs).</summary>
    private readonly ThreadLocal<int> _drainDepth = new(() => 0);

    /// <summary>Result of the most recent <see cref="Kind.HandleOrder"/> processed during the last drain on this call stack.</summary>
    public bool LastHandleOrderAccepted { get; private set; }

    public void EnqueueHandleOrder(EntityCommand command)
        => _queue.Enqueue(new Item(Kind.HandleOrder, command, null));

    public void EnqueueWakeAgent(Entity entity)
        => _queue.Enqueue(new Item(Kind.WakeAgent, null, entity));

    /// <summary>
    /// Processes all pending items. Safe across the background pump and UI/sim threads;
    /// reentrant on the same thread when <see cref="OrderEnqueue"/> nests during Process.
    /// </summary>
    public void Drain(Game game)
    {
        if (_drainDepth.Value > 0)
        {
            DrainCore(game);
            return;
        }

        lock (_drainLock)
        {
            _drainDepth.Value++;
            try
            {
                DrainCore(game);
            }
            finally
            {
                _drainDepth.Value--;
            }
        }
    }

    private void DrainCore(Game game)
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
