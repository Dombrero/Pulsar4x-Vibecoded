using System;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;

namespace Pulsar4X.Fleets;

/// <summary>
/// Standing-order / queue action placeholder for general resupply.
/// Completes immediately so it cannot block the fleet order queue (full resupply not implemented yet).
/// </summary>
public class ResupplyAction : EntityCommand
{
    public override string Name => "Resupply";
    public override string Details => "Resupply the fleet (not fully implemented — completes immediately).";
    public override ActionLaneTypes ActionLanes { get; } = ActionLaneTypes.InteractWithSelf | ActionLaneTypes.InteractWithEntitySameFleet;

    public override bool IsBlocking => true;

    private Entity _entityCommanding = Entity.InvalidEntity;
    internal override Entity EntityCommanding => _entityCommanding;

    public ResupplyAction() { }

    public ResupplyAction(Entity commandingEntity)
    {
        _entityCommanding = commandingEntity;
    }

    public static ResupplyAction CreateCommand(int factionId, Entity commandingEntity)
    {
        return new ResupplyAction(commandingEntity)
        {
            UseActionLanes = true,
            RequestingFactionGuid = factionId,
            EntityCommandingGuid = commandingEntity.Id,
        };
    }

    internal override bool IsFinished() => _isFinished;

    internal override void Execute(DateTime atDateTime)
    {
        if (_isFinished)
            return;

        IsRunning = true;
        // No dedicated resupply transfer path yet — finish so the queue is never stuck.
        _isFinished = true;
    }

        internal override bool IsValidCommand(Game game) => _entityCommanding.IsValid;

    internal override void BindCommandingEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entityCommanding = entity;
        base.BindCommandingEntity(entity);
    }

    public override EntityCommand Clone()
    {
        return new ResupplyAction(_entityCommanding)
        {
            UseActionLanes = UseActionLanes,
            RequestingFactionGuid = RequestingFactionGuid,
            EntityCommandingGuid = EntityCommandingGuid,
            CreatedDate = CreatedDate,
            ActionOnDate = ActionOnDate,
            ActionedOnDate = ActionedOnDate,
        };
    }
}
