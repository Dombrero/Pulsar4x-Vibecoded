using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.GeoSurveys;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Movement
{
    /// <summary>
    /// Fleet standing coordinator: assigns each survey-capable ship its own nearest geo target
    /// by enqueueing a real <see cref="GeoSurveyOrder"/> on that ship (visible progress, busy queue).
    /// </summary>
    public class MoveToNearestGeoSurveyAction : EntityCommand
    {
        public override string Name
        {
            get
            {
                var active = ActiveShipSurveys().ToList();
                if (active.Count == 1)
                    return active[0].Name;
                if (active.Count > 1)
                    return $"Geo Survey Nearest ({active.Count} ships)";
                return "Geo Survey Nearest";
            }
        }

        public override string Details =>
            "Each ship surveys the nearest unsurveyed body (excluding colonies).";

        public override ActionLaneTypes ActionLanes =>
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        private readonly HashSet<int> _assignedShipIds = new();
        private bool _noTargets;

        internal override Entity EntityCommanding => _entityCommanding;

        public MoveToNearestGeoSurveyAction() { }

        public static MoveToNearestGeoSurveyAction CreateCommand(int factionId, Entity commandingEntity)
        {
            return new MoveToNearestGeoSurveyAction
            {
                _entityCommanding = commandingEntity,
                UseActionLanes = true,
                RequestingFactionGuid = factionId,
                EntityCommandingGuid = commandingEntity.Id,
            };
        }

        internal override bool IsFinished()
        {
            if (_noTargets)
                return _isFinished = true;

            SyncAssignedFromShips();
            if (_assignedShipIds.Count > 0)
                return _isFinished = false;

            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                return _isFinished = true;

            bool anyCapable = fleetDB.Children.Any(c =>
                c.HasDataBlob<ShipInfoDB>() && c.HasDataBlob<GeoSurveyAbilityDB>());
            if (!anyCapable)
                return _isFinished = true;

            foreach (var ship in fleetDB.Children.Where(c => c.HasDataBlob<GeoSurveyAbilityDB>()))
            {
                if (FindNearestEligibleBody(ship, CollectClaimedTargets(fleetDB, excludeShipId: -1)) != null)
                    return _isFinished = false;
            }

            _noTargets = true;
            return _isFinished = true;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
            {
                _noTargets = true;
                IsRunning = true;
                return;
            }

            var game = _entityCommanding.AttachedManager?.Game;
            if (game == null)
            {
                IsRunning = true;
                return;
            }

            SyncAssignedFromShips();
            var claimed = CollectClaimedTargets(fleetDB, excludeShipId: -1);

            bool assigned = _assignedShipIds.Count > 0;
            foreach (var ship in fleetDB.Children.Where(c =>
                         c.HasDataBlob<ShipInfoDB>() && c.HasDataBlob<GeoSurveyAbilityDB>()))
            {
                if (_assignedShipIds.Contains(ship.Id))
                {
                    assigned = true;
                    continue;
                }

                if (ship.TryGetDataBlob<OrderableDB>(out var shipQ))
                {
                    if (shipQ.ActionList.Any(a => a.Source == OrderSource.Issued))
                        continue;
                    // Busy with logistics — do not steal the hull for a new survey slot.
                    if (shipQ.ActionList.Any(a =>
                            a.Source == OrderSource.Standing
                            && (a is CargoTransferOrder || a is WarpMoveCommand)
                            && !a.IsFinished()))
                        continue;
                    // Already has a geo survey order (e.g. from ShipStandingDirector).
                    if (shipQ.ActionList.OfType<GeoSurveyOrder>().Any(g => !g.IsFinished()))
                    {
                        _assignedShipIds.Add(ship.Id);
                        if (shipQ.ActionList.OfType<GeoSurveyOrder>().FirstOrDefault(g => g.Target.IsValid) is { } existing)
                            claimed.Add(existing.Target.Id);
                        assigned = true;
                        continue;
                    }

                    if (shipQ.ActionList.Count > 0)
                        continue;
                }

                var target = FindNearestEligibleBody(ship, claimed);
                if (target == null)
                    continue;

                claimed.Add(target.Id);
                var survey = new GeoSurveyOrder(ship, target)
                {
                    RequestingFactionGuid = RequestingFactionGuid != 0
                        ? RequestingFactionGuid
                        : _entityCommanding.FactionOwnerID,
                    EntityCommandingGuid = ship.Id,
                    Source = Source,
                    UseActionLanes = UseActionLanes,
                    CreatedDate = CreatedDate,
                    ActionOnDate = ActionOnDate,
                };

                bool ok = Source == OrderSource.Standing
                    ? OrderEnqueue.Standing(game, survey)
                    : OrderEnqueue.Enqueue(game, survey);
                if (!ok)
                    continue;

                _assignedShipIds.Add(ship.Id);
                assigned = true;
            }

            IsRunning = true;
            if (!assigned && _assignedShipIds.Count == 0)
                _noTargets = true;
        }

        private void SyncAssignedFromShips()
        {
            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
            {
                _assignedShipIds.Clear();
                return;
            }

            foreach (var id in _assignedShipIds.ToList())
            {
                var ship = fleetDB.Children.FirstOrDefault(c => c.Id == id);
                if (ship == null || !ship.IsValid)
                {
                    _assignedShipIds.Remove(id);
                    continue;
                }

                if (!ship.TryGetDataBlob<OrderableDB>(out var q)
                    || !q.ActionList.OfType<GeoSurveyOrder>().Any(g => !g.IsFinished()))
                {
                    // Still warping under a finished-looking survey? Keep until queue clear of geo travel.
                    bool geoWarp = q != null && q.ActionList.OfType<WarpMoveCommand>()
                        .Any(w => !w.IsFinished() && IsGeoWarp(ship, w));
                    if (!geoWarp)
                        _assignedShipIds.Remove(id);
                }
            }
        }

        private IEnumerable<GeoSurveyOrder> ActiveShipSurveys()
        {
            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                yield break;

            foreach (var ship in fleetDB.Children)
            {
                if (!ship.TryGetDataBlob<OrderableDB>(out var q))
                    continue;
                foreach (var g in q.ActionList.OfType<GeoSurveyOrder>().Where(x => !x.IsFinished()))
                    yield return g;
            }
        }

        private static HashSet<int> CollectClaimedTargets(FleetDB fleetDB, int excludeShipId)
        {
            var claimed = new HashSet<int>();
            foreach (var child in fleetDB.Children)
            {
                if (child.Id == excludeShipId)
                    continue;
                if (child.TryGetDataBlob<GeoSurveyingDB>(out var surveying))
                    claimed.Add(surveying.TargetId);
                if (!child.TryGetDataBlob<OrderableDB>(out var q))
                    continue;
                foreach (var cmd in q.ActionList.OfType<GeoSurveyOrder>().Where(g => !g.IsFinished() && g.Target.IsValid))
                    claimed.Add(cmd.Target.Id);
                foreach (var warp in q.ActionList.OfType<WarpMoveCommand>().Where(w => !w.IsFinished()))
                {
                    if (IsGeoWarp(child, warp)
                        && child.AttachedManager != null
                        && child.AttachedManager.TryGetEntityById(warp.TargetEntityGuid, out var dest)
                        && dest.HasDataBlob<GeoSurveyableDB>())
                        claimed.Add(dest.Id);
                }
            }

            return claimed;
        }

        private static bool IsGeoWarp(Entity ship, WarpMoveCommand warp)
        {
            if (warp.TargetEntityGuid == 0 || ship.AttachedManager == null)
                return false;
            return ship.AttachedManager.TryGetEntityById(warp.TargetEntityGuid, out var dest)
                   && dest.HasDataBlob<GeoSurveyableDB>();
        }

        private Entity? FindNearestEligibleBody(Entity fromShip, HashSet<int> claimed)
        {
            if (!fromShip.TryGetDataBlob<PositionDB>(out var shipPos))
                return null;
            if (_entityCommanding.Manager == null)
                return null;

            int factionId = RequestingFactionGuid != 0
                ? RequestingFactionGuid
                : _entityCommanding.FactionOwnerID;

            Entity? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var body in _entityCommanding.AttachedManager.GetAllEntitiesWithDataBlob<GeoSurveyableDB>())
            {
                if (claimed.Contains(body.Id))
                    continue;
                if (!GeoSurveyTargets.IsEligible(body, factionId))
                    continue;
                if (!body.TryGetDataBlob<PositionDB>(out var bodyPos))
                    continue;

                double distance = bodyPos.GetDistanceTo_m(shipPos);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = body;
                }
            }

            return closest;
        }

        internal override bool IsValidCommand(Game game) => true;

        internal override void BindCommandingEntity(Entity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            _entityCommanding = entity;
            base.BindCommandingEntity(entity);
        }

        public override EntityCommand Clone()
        {
            return new MoveToNearestGeoSurveyAction
            {
                _entityCommanding = _entityCommanding,
                UseActionLanes = UseActionLanes,
                RequestingFactionGuid = RequestingFactionGuid,
                EntityCommandingGuid = EntityCommandingGuid,
                CreatedDate = CreatedDate,
                ActionOnDate = ActionOnDate,
                ActionedOnDate = ActionedOnDate,
                Source = Source,
            };
        }
    }
}
