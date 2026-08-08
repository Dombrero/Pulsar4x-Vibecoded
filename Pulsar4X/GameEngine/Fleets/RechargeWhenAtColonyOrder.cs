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
    /// Fleet order: issue colony recharge for battery-only ships already on-station,
    /// wait for stragglers still warping in, and stay queued until those batteries are full.
    /// </summary>
    public class RechargeWhenAtColonyOrder : EntityCommand
    {
        public override string Name => "Recharge at Colony";

        public override string Details
        {
            get
            {
                if (!_entityCommanding.IsValid)
                    return "Waiting for battery-only ships at colony.";
                if (FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
                    return "Energy transfer in progress.";
                if (FleetEnergy.AnyHasFreeBatteryForColonyRecharge(_entityCommanding))
                    return "Waiting for battery-only ships at colony.";
                return "Recharge complete.";
            }
        }

        public override ActionLaneTypes ActionLanes { get; } =
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        private Entity _colony = Entity.InvalidEntity;
        private bool _gaveUp;

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
            if (_gaveUp)
                return _isFinished = true;

            if (FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
                return _isFinished = false;

            if (FleetEnergy.AnyHasFreeBatteryForColonyRecharge(_entityCommanding))
                return _isFinished = false;

            return _isFinished = true;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (_isFinished || _gaveUp)
                return;

            IsRunning = true;

            if (!FleetEnergy.AnyHasFreeBatteryForColonyRecharge(_entityCommanding)
                && !FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
                return;

            ColonyPowerProcessor.RecalcAbilities(_colony);
            if (!_colony.HasDataBlob<ColonyPowerDB>())
            {
                DebugTraceLog.Warn("Recharge",
                    $"fleet#{_entityCommanding.Id}: colony#{_colony.Id} has no power store",
                    atDateTime);
                _gaveUp = true;
                return;
            }

            if (FleetOrderProcessor.FleetShipsHaveRechargeWork(_entityCommanding))
                return;

            try
            {
                bool ok = EnergyRechargeHelper.CreateRechargeFleetCommand(_colony, _entityCommanding);

                if (ok)
                {
                    DebugTraceLog.Info("Recharge",
                        $"fleet#{_entityCommanding.Id}: issued recharge from colony#{_colony.Id} success=True",
                        atDateTime);
                    return;
                }

                if (!FleetEnergy.AnyHasFreeBatteryForColonyRecharge(_entityCommanding))
                {
                    DebugTraceLog.Info("Recharge",
                        $"fleet#{_entityCommanding.Id}: at colony#{_colony.Id} — batteries full, nothing to issue",
                        atDateTime);
                    return;
                }

                if (!FleetEnergy.AreNeedyShipsAtColony(_entityCommanding, _colony))
                {
                    DebugTraceLog.Info("Recharge",
                        $"fleet#{_entityCommanding.Id}: waiting for battery-only ships to reach colony#{_colony.Id}",
                        atDateTime);
                    return;
                }

                if (_entityCommanding.TryGetDataBlob<FleetDB>(out var suppressDb))
                {
                    suppressDb.StandingSuppressUntil = atDateTime + TimeSpan.FromHours(6);
                    DebugTraceLog.Warn("Recharge",
                        $"fleet#{_entityCommanding.Id}: recharge issue failed at colony#{_colony.Id} — " +
                        $"suppress standing until {suppressDb.StandingSuppressUntil.Value:yyyy-MM-dd HH:mm}",
                        atDateTime);
                }

                DebugTraceLog.Info("Recharge",
                    $"fleet#{_entityCommanding.Id}: issued recharge from colony#{_colony.Id} success=False",
                    atDateTime);
                _gaveUp = true;
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("Recharge",
                    $"fleet#{_entityCommanding.Id}: CreateRechargeFleetCommand failed: {ex.Message}",
                    atDateTime);
                if (_entityCommanding.TryGetDataBlob<FleetDB>(out var suppressDb))
                    suppressDb.StandingSuppressUntil = atDateTime + TimeSpan.FromHours(6);
                _gaveUp = true;
            }
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
