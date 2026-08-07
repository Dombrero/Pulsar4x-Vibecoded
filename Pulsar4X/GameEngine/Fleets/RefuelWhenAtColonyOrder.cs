using System;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Fleet order that waits until ships are at the colony, then issues refuel cargo transfers
    /// and stays in the queue until those transfers finish (so standing orders do not re-fire).
    /// Intended to run behind <see cref="Movement.WarpFleetTowardsTargetOrder"/> on the Movement lane.
    /// </summary>
    public class RefuelWhenAtColonyOrder : EntityCommand
    {
        public override string Name => "Refuel at Colony";

        public override string Details =>
            _transfersIssued
                ? "Fuel transfer in progress."
                : "Waiting to arrive at colony before transferring fuel.";

        public override ActionLaneTypes ActionLanes { get; } =
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        private Entity _colony = Entity.InvalidEntity;
        private bool _transfersIssued;

        internal override Entity EntityCommanding => _entityCommanding;

        public Entity Colony => _colony;

        public static RefuelWhenAtColonyOrder CreateCommand(int factionId, Entity fleet, Entity colony)
        {
            return new RefuelWhenAtColonyOrder
            {
                UseActionLanes = true,
                RequestingFactionGuid = factionId,
                EntityCommandingGuid = fleet.Id,
                _entityCommanding = fleet,
                _colony = colony,
                CreatedDate = fleet.StarSysDateTime,
            };
        }

        internal override bool IsFinished()
        {
            if (!_transfersIssued)
                return _isFinished = false;

            // Stay until ship-level fuel transfers complete — finishing early emptied the fleet
            // queue and made standing Refuel flicker / re-abort transfers every tick.
            if (FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding))
                return _isFinished = false;

            return _isFinished = true;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (_isFinished)
                return;

            IsRunning = true;

            if (_transfersIssued)
                return;

            if (!FleetOrderCleanup.IsFleetAtColony(_entityCommanding, _colony))
            {
                // Waiting to arrive.
                if (!_transfersIssued)
                    return;

                // Left the colony with a hanging transfer (e.g. warped away) — clear instead of stalling forever.
                if (FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding))
                    FleetOrderCleanup.AbortCargoTransfersOnFleetShips(_entityCommanding);
                _isFinished = true;
                return;
            }

            if (!_colony.HasDataBlob<CargoStorageDB>())
            {
                DebugTraceLog.Warn("Refuel",
                    $"fleet#{_entityCommanding.Id}: colony#{_colony.Id} has no cargo storage",
                    atDateTime);
                _transfersIssued = true; // nothing to do — IsFinished will clear us
                return;
            }

            // Already transferring — just wait (do not issue a second transfer / abort).
            if (FleetOrderProcessor.FleetShipsHaveRefuelWork(_entityCommanding))
            {
                _transfersIssued = true;
                return;
            }

            try
            {
                bool ok = CargoTransferOrder.CreateRefuelFleetCommand(_colony, _entityCommanding, Source);
                if (ok && _entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                    RefuelColonySearch.RememberRefuelSite(fleetDB, _colony);

                DebugTraceLog.Info("Refuel",
                    $"fleet#{_entityCommanding.Id}: issued refuel transfers from colony#{_colony.Id} success={ok}",
                    atDateTime);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("Refuel",
                    $"fleet#{_entityCommanding.Id}: CreateRefuelFleetCommand failed: {ex.Message}",
                    atDateTime);
                System.Diagnostics.Debug.WriteLine($"RefuelWhenAtColony failed: {ex}");
            }

            _transfersIssued = true;
        }

        internal override bool IsValidCommand(Game game)
        {
            if (!CommandHelpers.IsCommandValid(
                    game.GlobalManager,
                    RequestingFactionGuid,
                    EntityCommandingGuid,
                    out _,
                    out _entityCommanding))
                return false;

            return _colony != null && _colony.HasDataBlob<ColonyInfoDB>();
        }

        public override EntityCommand Clone()
        {
            return new RefuelWhenAtColonyOrder
            {
                UseActionLanes = UseActionLanes,
                RequestingFactionGuid = RequestingFactionGuid,
                EntityCommandingGuid = EntityCommandingGuid,
                CreatedDate = CreatedDate,
                ActionOnDate = ActionOnDate,
                ActionedOnDate = ActionedOnDate,
                _entityCommanding = _entityCommanding,
                _colony = _colony,
            };
        }

        internal override void BindCommandingEntity(Entity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            _entityCommanding = entity;
            base.BindCommandingEntity(entity);
        }
    }
}
