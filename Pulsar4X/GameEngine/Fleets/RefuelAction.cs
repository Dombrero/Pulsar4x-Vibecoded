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
using Pulsar4X.JumpPoints;
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

                Entity? nearestColony = RefuelColonySearch.FindNearestColonyInSystem(
                    _entityCommanding.AttachedManager,
                    RequestingFactionGuid,
                    flagshipPos);

                _entityCommanding.TryGetDataBlob<OrderableDB>(out var orderable);

                if (nearestColony == null)
                {
                    if (orderable != null && orderable.ActionList.Any(a => a is JumpOrder))
                        return;

                    var game = _entityCommanding.AttachedManager.Game;
                    if (RefuelColonySearch.TryResolveRefuelSystemId(
                            game,
                            _entityCommanding,
                            RequestingFactionGuid,
                            fleetDB,
                            out var targetSystemId)
                        && RefuelColonySearch.TryFindJumpGateTowardSystem(
                            game,
                            _entityCommanding,
                            RequestingFactionGuid,
                            targetSystemId,
                            flagshipPos,
                            out var jumpGate)
                        && jumpGate != null)
                    {
                        DebugTraceLog.Info("Refuel",
                            $"fleet#{_entityCommanding.Id}: no local colony — jump toward refuel system {targetSystemId}",
                            atDateTime);

                        InsertFollowUpsAfterSelf(
                            CreateJumpOrder(jumpGate, atDateTime),
                            CreateCommand(RequestingFactionGuid, _entityCommanding));
                        return;
                    }

                    DebugTraceLog.Warn("Refuel",
                        $"fleet#{_entityCommanding.Id}: no colony with fuel stores found",
                        atDateTime);
                    return;
                }

                if (orderable != null
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

                bool alreadyAtColony = FleetFuel.AreNeedyShipsAtColony(_entityCommanding, nearestColony);

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

                // Only abort stuck transfers on ships that will leave — leave full siblings alone.
                foreach (var ship in FleetFuel.ShipsNeedingRefuel(_entityCommanding)
                             .Where(s => !FleetOrderCleanup.IsShipAtColony(s, nearestColony)))
                {
                    FleetOrderCleanup.AbortCargoTransfersOnEntity(ship);
                }

                // If Move-to-Colony (or another warp) is already queued, only wait-then-refuel —
                // a second warp here raced Move and crashed on velocity/orbit edge cases.
                bool travelAlreadyQueued = orderable != null && orderable.ActionList.Any(a =>
                    a is MoveToNearestColonyAction
                    || a is WarpFleetTowardsTargetOrder
                    || a is MoveToSystemBodyOrder
                    || a is JumpOrder);

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
                        $"fleet#{_entityCommanding.Id}: warp fuel-needy ships to colony#{nearestColony.Id} then refuel",
                        atDateTime);
                    InsertFollowUpsAfterSelf(
                        WarpFleetTowardsTargetOrder.CreateCommand(
                            _entityCommanding, nearestColony, onlyShipsNeedingFuel: true),
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

        private JumpOrder CreateJumpOrder(JumpPointDB jumpGate, DateTime atDateTime)
        {
            return new JumpOrder
            {
                UseActionLanes = true,
                RequestingFactionGuid = RequestingFactionGuid,
                EntityCommandingGuid = _entityCommanding.Id,
                CreatedDate = atDateTime,
                JumpGate = jumpGate,
                Source = Source,
            };
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
                    OrderEnqueue.Enqueue(_entityCommanding.AttachedManager.Game, cmd);
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
                    OrderEnqueue.Enqueue(_entityCommanding.AttachedManager.Game, cmd);
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
