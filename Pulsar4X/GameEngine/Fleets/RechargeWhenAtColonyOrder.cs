using System;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Waits until the fleet is at the colony, issues energy recharge blobs, then stays until done.
    /// </summary>
    public class RechargeWhenAtColonyOrder : EntityCommand
    {
        public override string Name => "Recharge at Colony";

        public override string Details =>
            _transfersIssued
                ? "Energy transfer in progress."
                : "Waiting to arrive at colony before transferring energy.";

        public override ActionLaneTypes ActionLanes { get; } =
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        private Entity _colony = Entity.InvalidEntity;
        private bool _transfersIssued;

        internal override Entity EntityCommanding => _entityCommanding;

        public Entity Colony => _colony;

        public static RechargeWhenAtColonyOrder CreateCommand(int factionId, Entity fleet, Entity colony)
        {
            return new RechargeWhenAtColonyOrder
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

            if (FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
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
                return;

            ColonyPowerProcessor.RecalcAbilities(_colony);
            if (!_colony.HasDataBlob<ColonyPowerDB>())
            {
                DebugTraceLog.Warn("Recharge",
                    $"fleet#{_entityCommanding.Id}: colony#{_colony.Id} has no power store",
                    atDateTime);
                _transfersIssued = true;
                return;
            }

            if (FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
            {
                _transfersIssued = true;
                return;
            }

            try
            {
                bool ok = EnergyRechargeHelper.CreateRechargeFleetCommand(_colony, _entityCommanding);
                DebugTraceLog.Info("Recharge",
                    $"fleet#{_entityCommanding.Id}: issued recharge from colony#{_colony.Id} success={ok}",
                    atDateTime);
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("Recharge",
                    $"fleet#{_entityCommanding.Id}: CreateRechargeFleetCommand failed: {ex.Message}",
                    atDateTime);
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
            return new RechargeWhenAtColonyOrder
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
