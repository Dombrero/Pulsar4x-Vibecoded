using System;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Movement;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Standing-order / queue action: find nearest friendly colony with cargo and refuel the fleet.
    /// Warps first when away; fuel transfers are deferred until ships are at the colony.
    /// </summary>
    public class RefuelAction : EntityCommand
    {
        public override string Name => "Refuel";
        public override string Details => "Refuel the fleet at the nearest colony with fuel stores.";
        public override ActionLaneTypes ActionLanes { get; } = ActionLaneTypes.InteractWithSelf | ActionLaneTypes.InteractWithEntitySameFleet;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        internal override Entity EntityCommanding => _entityCommanding;

        public RefuelAction() { }

        public RefuelAction(Entity commandingEntity)
        {
            _entityCommanding = commandingEntity;
        }

        public static RefuelAction CreateCommand(int factionId, Entity commandingEntity)
        {
            return new RefuelAction(commandingEntity)
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

            try
            {
                if (!_entityCommanding.IsValid || !_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                    return;

                if (fleetDB.FlagShipID == -1
                    || !_entityCommanding.AttachedManager.TryGetEntityById(fleetDB.FlagShipID, out var flagship)
                    || !flagship.TryGetDataBlob<PositionDB>(out var flagshipPos))
                    return;

                Entity? nearestColony = null;
                double nearestDist = double.MaxValue;

                foreach (var entity in _entityCommanding.AttachedManager.GetFilteredEntities(
                             EntityFilter.Friendly,
                             RequestingFactionGuid,
                             e => e.HasDataBlob<ColonyInfoDB>() && e.HasDataBlob<CargoStorageDB>()))
                {
                    if (!entity.TryGetDataBlob<PositionDB>(out var colonyPos))
                        continue;

                    double dist = colonyPos.GetDistanceTo_m(flagshipPos);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestColony = entity;
                    }
                }

                if (nearestColony == null)
                {
                    DebugTraceLog.Warn("Refuel",
                        $"fleet#{_entityCommanding.Id}: no colony with fuel stores found",
                        atDateTime);
                    return;
                }

                if (_entityCommanding.TryGetDataBlob<OrderableDB>(out var orderable)
                    && orderable.ActionList.Any(a => a is RefuelWhenAtColonyOrder))
                    return;

                // Do not abort an in-progress fuel transfer — keep a fleet waiter instead of
                // finishing with an empty queue (that caused standing Refuel flicker).
                if (FleetShipsHaveActiveFuelTransfer(_entityCommanding))
                {
                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: transfer already active — ensure waiter",
                        atDateTime);
                    if (orderable == null || !orderable.ActionList.Any(a => a is RefuelWhenAtColonyOrder))
                    {
                        InsertFollowUpsAfterSelf(
                            RefuelWhenAtColonyOrder.CreateCommand(
                                RequestingFactionGuid, _entityCommanding, nearestColony));
                    }
                    return;
                }

                bool alreadyAtColony = FleetOrderCleanup.IsFleetAtColony(_entityCommanding, nearestColony);

                // Always hand off to RefuelWhenAtColonyOrder so the fleet queue stays occupied
                // until transfers finish (prevents standing-order flicker with an empty queue).
                if (alreadyAtColony)
                {
                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: already at colony#{nearestColony.Id} — issue RefuelWhenAtColony",
                        atDateTime);
                    InsertFollowUpsAfterSelf(
                        RefuelWhenAtColonyOrder.CreateCommand(
                            RequestingFactionGuid, _entityCommanding, nearestColony));
                    return;
                }

                // Leaving for the colony — free Movement for warp (stuck out-of-range transfers only).
                FleetOrderCleanup.AbortCargoTransfersOnFleetShips(_entityCommanding);

                // If Move-to-Colony (or another warp) is already queued, only wait-then-refuel —
                // a second warp here raced Move and crashed on velocity/orbit edge cases.
                bool travelAlreadyQueued = orderable != null && orderable.ActionList.Any(a =>
                    a is MoveToNearestColonyAction
                    || a is WarpFleetTowardsTargetOrder
                    || a is MoveToSystemBodyOrder);

                if (travelAlreadyQueued)
                {
                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: travel already queued → wait then refuel at colony#{nearestColony.Id}",
                        atDateTime);
                    InsertFollowUpsAfterSelf(
                        RefuelWhenAtColonyOrder.CreateCommand(
                            RequestingFactionGuid, _entityCommanding, nearestColony));
                }
                else
                {
                    DebugTraceLog.Info("Refuel",
                        $"fleet#{_entityCommanding.Id}: warp to colony#{nearestColony.Id} then refuel",
                        atDateTime);
                    InsertFollowUpsAfterSelf(
                        WarpFleetTowardsTargetOrder.CreateCommand(_entityCommanding, nearestColony),
                        RefuelWhenAtColonyOrder.CreateCommand(
                            RequestingFactionGuid, _entityCommanding, nearestColony));
                }
            }
            finally
            {
                // Always complete so a missing colony cannot block the queue forever.
                // Warp + RefuelWhenAtColony continue on the fleet queue.
                _isFinished = true;
            }
        }

        private static bool FleetShipsHaveActiveFuelTransfer(Entity fleet)
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

        /// <summary>
        /// Insert follow-up orders immediately after this action so they stay ahead of any
        /// remaining fleet work (HandleOrder would append at the end).
        /// </summary>
        private void InsertFollowUpsAfterSelf(params EntityCommand[] followUps)
        {
            if (!_entityCommanding.TryGetDataBlob<OrderableDB>(out var orderable))
            {
                foreach (var cmd in followUps)
                    _entityCommanding.AttachedManager.Game.OrderHandler.HandleOrder(cmd);
                return;
            }

            int selfIndex = -1;
            for (int i = 0; i < orderable.ActionList.Count; i++)
            {
                if (ReferenceEquals(orderable.ActionList[i], this))
                {
                    selfIndex = i;
                    break;
                }
            }

            if (selfIndex < 0)
            {
                foreach (var cmd in followUps)
                    _entityCommanding.AttachedManager.Game.OrderHandler.HandleOrder(cmd);
                return;
            }

            for (int i = 0; i < followUps.Length; i++)
            {
                var cmd = followUps[i];
                cmd.UseActionLanes = true;
                cmd.Source = Source; // keep Standing vs Issued with the parent Refuel action
                orderable.ActionList.Insert(selfIndex + 1 + i, cmd);
            }
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
            return new RefuelAction(_entityCommanding)
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
}
