using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Pulsar4X.Orbital;
using Pulsar4X.DataStructures;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.Orbits;
using Pulsar4X.Ships;
using Pulsar4X.Galaxy;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Engine;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Movement
{
    public class MoveToNearestAction : EntityCommand
    {
        public override string Name => "Move to Nearest";
        public override string Details => "Moves the fleet to the nearest X by filter.";

        public override ActionLaneTypes ActionLanes { get; } = ActionLaneTypes.InteractWithSelf | ActionLaneTypes.InteractWithEntitySameFleet | ActionLaneTypes.Movement;

        public override bool IsBlocking => true;

        protected Entity _entityCommanding = Entity.InvalidEntity;
        internal override Entity EntityCommanding
        {
            get { return _entityCommanding; }
        }

        [JsonIgnore]
        public EntityManager.FilterEntities? Filter { get; protected set; }

        public delegate Entity EntitySelector(Entity entity);

        [JsonIgnore]
        public EntitySelector? TargetSelector { get; protected set; }

        public EntityFilter EntityFactionFilter { get; protected set; } = EntityFilter.Friendly | EntityFilter.Neutral | EntityFilter.Hostile;

        private List<EntityCommand> _shipCommands = new List<EntityCommand>();

        protected virtual void EnsureFiltersConfigured() { }

        internal override bool IsFinished()
        {
            if (!IsRunning)
                return _isFinished = false;

            // Preempted warps — stay in the queue so Execute can re-dispatch.
            if (_shipCommands.Any(c => c is WarpMoveCommand { WasCancelled: true }))
                return _isFinished = false;

            // After Execute, an empty list means every ship was already at the target
            // (no warps needed). Returning false here used to block Refuel forever.
            if (_shipCommands.Count == 0)
                return _isFinished = true;

            return _isFinished = ShipsFinishedWarping();
        }

        internal override void Execute(DateTime atDateTime)
        {
            EnsureFiltersConfigured();

            bool needsRedispatch = !IsRunning
                || _shipCommands.Count == 0
                || _shipCommands.Any(c => c is WarpMoveCommand { WasCancelled: true });

            if (needsRedispatch)
                FindNearestAndSetupWarpCommands();
        }

        private void FindNearestAndSetupWarpCommands()
        {
            if (Filter == null) return;
            if (!EntityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB)) return;
            if (fleetDB.FlagShipID == -1) return;
            if (!EntityCommanding.AttachedManager.TryGetEntityById(fleetDB.FlagShipID, out var flagship)) return;
            if (!flagship.TryGetDataBlob<PositionDB>(out var flagshipPositionDB)) return;

            List<Entity> filteredEntities = EntityCommanding.AttachedManager.GetFilteredEntities(
                EntityFactionFilter,
                RequestingFactionGuid,
                Filter);

            Entity? closestValidEntity = null;
            double closestDistance = double.MaxValue;

            foreach (var entity in filteredEntities)
            {
                if (!entity.TryGetDataBlob<PositionDB>(out var positionDB))
                    continue;

                var distance = positionDB.GetDistanceTo_m(flagshipPositionDB);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestValidEntity = entity;
                }
            }

            if (closestValidEntity == null) return;

            var targetEntity = TargetSelector == null ? closestValidEntity : TargetSelector(closestValidEntity);

            if (!targetEntity.TryGetDataBlob<PositionDB>(out var targetEntityPositionDB))
                return;

            if (targetEntityPositionDB.Parent == null) return;

            var ships = fleetDB.Children.Where(c => c.HasDataBlob<ShipInfoDB>()).ToList();
            bool anyNeedsWarp = ships.Any(ship =>
                ship.HasDataBlob<WarpAbilityDB>()
                && ship.TryGetDataBlob<PositionDB>(out var shipPos)
                && shipPos.Parent != targetEntityPositionDB.OwningEntity);

            // Only clear the Movement lane when we are actually leaving. Aborting cargo while
            // already at the colony kills an in-progress standing Refuel transfer → loop.
            if (anyNeedsWarp)
                FleetOrderCleanup.AbortShipOrdersBlockingMovement(EntityCommanding);
            else
                FleetOrderCleanup.AbortShipMovementOrders(EntityCommanding);

            _shipCommands.Clear();

            foreach (var ship in ships)
            {
                if (!ship.HasDataBlob<WarpAbilityDB>()) continue;
                if (!ship.TryGetDataBlob<PositionDB>(out var shipPositionDB)) continue;
                if (shipPositionDB.Parent == targetEntityPositionDB.OwningEntity) continue;

                try
                {
                    if (!targetEntity.TryGetDataBlob<OrbitDB>(out _))
                        continue;

                    var cmd = WarpMoveCommand.CreateCommandEZ(
                        ship,
                        targetEntity,
                        EntityCommanding.StarSysDateTime);
                    _shipCommands.Add(cmd);
                    OrderEnqueue.Enqueue(ship.AttachedManager.Game, cmd);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"MoveToNearest warp failed for ship {ship.Id}: {ex.Message}");
                }
            }

            IsRunning = true;
        }

        private bool ShipsFinishedWarping()
        {
            if (!IsRunning) return false;

            foreach (var command in _shipCommands)
            {
                if (!command.IsFinished())
                    return false;
            }
            return true;
        }

        internal override bool IsValidCommand(Game game)
        {
            return true;
        }

        protected MoveToNearestAction() { }

        protected static T CreateCommand<T>(int factionId, Entity commandingEntity) where T : MoveToNearestAction, new()
        {
            ArgumentNullException.ThrowIfNull(commandingEntity);

            var command = new T()
            {
                _entityCommanding = commandingEntity,
                UseActionLanes = true,
                RequestingFactionGuid = factionId,
                EntityCommandingGuid = commandingEntity.Id,
            };

            return command;
        }

        internal override void BindCommandingEntity(Entity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            _entityCommanding = entity;
            base.BindCommandingEntity(entity);
        }

        public override EntityCommand Clone()
        {
            var command = new MoveToNearestAction()
            {
                _entityCommanding = this._entityCommanding,
                Filter = this.Filter,
                UseActionLanes = this.UseActionLanes,
                RequestingFactionGuid = this.RequestingFactionGuid,
                EntityCommandingGuid = this.EntityCommandingGuid,
                CreatedDate = this.CreatedDate,
                ActionOnDate = this.ActionOnDate,
                ActionedOnDate = this.ActionedOnDate,
                IsRunning = this.IsRunning,
                TargetSelector = this.TargetSelector,
                EntityFactionFilter = this.EntityFactionFilter,
            };

            return command;
        }
    }
}
