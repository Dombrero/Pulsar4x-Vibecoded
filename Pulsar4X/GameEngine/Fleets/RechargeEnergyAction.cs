using System;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Movement;
using Pulsar4X.Ships;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Standing-order / queue action: find nearest friendly colony with power stores and recharge the fleet.
    /// Mirrors <see cref="RefuelAction"/>.
    /// </summary>
    public class RechargeEnergyAction : EntityCommand
    {
        public override string Name => "Recharge";
        public override string Details => "Recharge fleet batteries at the nearest colony with power stores.";
        public override ActionLaneTypes ActionLanes { get; } = ActionLaneTypes.InteractWithSelf | ActionLaneTypes.InteractWithEntitySameFleet;
        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        internal override Entity EntityCommanding => _entityCommanding;

        public RechargeEnergyAction() { }

        public RechargeEnergyAction(Entity commandingEntity)
        {
            _entityCommanding = commandingEntity;
        }

        public static RechargeEnergyAction CreateCommand(int factionId, Entity commandingEntity)
        {
            return new RechargeEnergyAction(commandingEntity)
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
                             e => e.HasDataBlob<ColonyInfoDB>() && e.HasDataBlob<ColonyPowerDB>()))
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

                // Colonies without ColonyPowerDB yet may still have batteries/plants — recalc and retry.
                if (nearestColony == null)
                {
                    foreach (var entity in _entityCommanding.AttachedManager.GetFilteredEntities(
                                 EntityFilter.Friendly,
                                 RequestingFactionGuid,
                                 e => e.HasDataBlob<ColonyInfoDB>()))
                    {
                        ColonyPowerProcessor.RecalcAbilities(entity);
                        if (!entity.HasDataBlob<ColonyPowerDB>())
                            continue;
                        if (!entity.TryGetDataBlob<PositionDB>(out var colonyPos))
                            continue;
                        double dist = colonyPos.GetDistanceTo_m(flagshipPos);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestColony = entity;
                        }
                    }
                }

                if (nearestColony == null)
                {
                    DebugTraceLog.Warn("Recharge",
                        $"fleet#{_entityCommanding.Id}: no colony with power stores found",
                        atDateTime);
                    return;
                }

                if (_entityCommanding.TryGetDataBlob<OrderableDB>(out var orderable)
                    && orderable.ActionList.Any(a => a is RechargeWhenAtColonyOrder))
                    return;

                if (FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
                {
                    if (orderable == null || !orderable.ActionList.Any(a => a is RechargeWhenAtColonyOrder))
                    {
                        InsertFollowUpsAfterSelf(
                            RechargeWhenAtColonyOrder.CreateCommand(
                                RequestingFactionGuid, _entityCommanding, nearestColony));
                    }
                    return;
                }

                bool alreadyAtColony = FleetOrderCleanup.IsFleetAtColony(_entityCommanding, nearestColony);

                if (alreadyAtColony)
                {
                    InsertFollowUpsAfterSelf(
                        RechargeWhenAtColonyOrder.CreateCommand(
                            RequestingFactionGuid, _entityCommanding, nearestColony));
                    return;
                }

                FleetOrderCleanup.AbortCargoTransfersOnFleetShips(_entityCommanding);

                bool travelAlreadyQueued = orderable != null && orderable.ActionList.Any(a =>
                    a is MoveToNearestColonyAction
                    || a is WarpFleetTowardsTargetOrder
                    || a is MoveToSystemBodyOrder);

                if (travelAlreadyQueued)
                {
                    InsertFollowUpsAfterSelf(
                        RechargeWhenAtColonyOrder.CreateCommand(
                            RequestingFactionGuid, _entityCommanding, nearestColony));
                }
                else
                {
                    InsertFollowUpsAfterSelf(
                        WarpFleetTowardsTargetOrder.CreateCommand(_entityCommanding, nearestColony),
                        RechargeWhenAtColonyOrder.CreateCommand(
                            RequestingFactionGuid, _entityCommanding, nearestColony));
                }
            }
            finally
            {
                _isFinished = true;
            }
        }

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
                cmd.Source = Source;
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
            return new RechargeEnergyAction(_entityCommanding)
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
