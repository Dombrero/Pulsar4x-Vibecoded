using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.JumpPoints;
using Pulsar4X.Ships;
using Pulsar4X.Storage;
using WarpMoveCommand = Pulsar4X.Movement.WarpMoveCommand;

namespace Pulsar4X.Movement
{
    /// <summary>
    /// Fleet standing coordinator: assigns each JP-survey ship its own nearest anomaly
    /// by enqueueing a real <see cref="JPSurveyOrder"/> on that ship.
    /// </summary>
    public class MoveToNearestGravSurveyAction : EntityCommand
    {
        public override string Name
        {
            get
            {
                var active = ActiveShipSurveys().ToList();
                if (active.Count == 1)
                    return active[0].Name;
                if (active.Count > 1)
                    return $"Grav Survey Nearest ({active.Count} ships)";
                return "Grav Survey Nearest";
            }
        }

        public override string Details =>
            "Each ship surveys the nearest unsurveyed grav anomaly.";

        public override ActionLaneTypes ActionLanes =>
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = Entity.InvalidEntity;
        private readonly HashSet<int> _assignedShipIds = new();
        private bool _noTargets;

        internal override Entity EntityCommanding => _entityCommanding;

        public MoveToNearestGravSurveyAction() { }

        public static MoveToNearestGravSurveyAction CreateCommand(int factionId, Entity commandingEntity)
        {
            return new MoveToNearestGravSurveyAction
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

            foreach (var ship in fleetDB.Children.Where(c => c.HasJPSurveyAbililty()))
            {
                if (FindNearestEligibleAnomaly(ship, CollectClaimedTargets(fleetDB)) != null)
                    return _isFinished = false;
            }

            _noTargets = true;
            if (_entityCommanding.TryGetDataBlob<FleetDB>(out fleetDB))
                fleetDB.StandingStatusMessage = "Can't find more anomalies";
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
            var claimed = CollectClaimedTargets(fleetDB);

            bool assigned = _assignedShipIds.Count > 0;
            foreach (var ship in fleetDB.Children.Where(c =>
                         c.HasDataBlob<ShipInfoDB>() && c.HasJPSurveyAbililty()))
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
                    if (shipQ.ActionList.Any(a =>
                            a.Source == OrderSource.Standing
                            && (a is CargoTransferOrder || a is WarpMoveCommand)
                            && !a.IsFinished()))
                        continue;
                    if (shipQ.ActionList.OfType<JPSurveyOrder>().Any(g => !g.IsFinished()))
                    {
                        _assignedShipIds.Add(ship.Id);
                        if (shipQ.ActionList.OfType<JPSurveyOrder>().FirstOrDefault(g => g.Target.IsValid) is { } existing)
                            claimed.Add(existing.Target.Id);
                        assigned = true;
                        continue;
                    }

                    if (shipQ.ActionList.Count > 0)
                        continue;
                }

                var target = FindNearestEligibleAnomaly(ship, claimed);
                if (target == null)
                    continue;

                claimed.Add(target.Id);
                var survey = new JPSurveyOrder(ship, target)
                {
                    RequestingFactionGuid = FactionIdForSurvey(),
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
            {
                _noTargets = true;
                fleetDB.StandingStatusMessage = "Can't find more anomalies";
                // Release + suppress before OrderableProcessor calls TryEvaluateNow on finish —
                // otherwise restart→enqueue→empty loops on the same stack (StackOverflow).
                fleetDB.ActiveStandingOrderIndex = -1;
                fleetDB.StandingSuppressUntil = atDateTime + TimeSpan.FromDays(1);
                DebugTraceLog.Warn("Standing",
                    $"fleet id={_entityCommanding.Id}: Grav Survey Nearest — no eligible anomaly " +
                    $"(faction={FactionIdForSurvey()}; standing suppressed until {fleetDB.StandingSuppressUntil.Value:yyyy-MM-dd HH:mm})",
                    atDateTime);
            }
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
                    || !q.ActionList.OfType<JPSurveyOrder>().Any(g => !g.IsFinished()))
                {
                    bool gravWarp = q != null && q.ActionList.OfType<WarpMoveCommand>()
                        .Any(w => !w.IsFinished() && IsGravWarp(ship, w));
                    if (!gravWarp)
                        _assignedShipIds.Remove(id);
                }
            }
        }

        private IEnumerable<JPSurveyOrder> ActiveShipSurveys()
        {
            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                yield break;

            foreach (var ship in fleetDB.Children)
            {
                if (!ship.TryGetDataBlob<OrderableDB>(out var q))
                    continue;
                foreach (var g in q.ActionList.OfType<JPSurveyOrder>().Where(x => !x.IsFinished()))
                    yield return g;
            }
        }

        private static HashSet<int> CollectClaimedTargets(FleetDB fleetDB)
        {
            var claimed = new HashSet<int>();
            foreach (var child in fleetDB.Children)
            {
                if (child.TryGetDataBlob<JPSurveyDB>(out var surveying))
                    claimed.Add(surveying.TargetId);
                if (!child.TryGetDataBlob<OrderableDB>(out var q))
                    continue;
                foreach (var cmd in q.ActionList.OfType<JPSurveyOrder>().Where(g => !g.IsFinished() && g.Target.IsValid))
                    claimed.Add(cmd.Target.Id);
                foreach (var warp in q.ActionList.OfType<WarpMoveCommand>().Where(w => !w.IsFinished()))
                {
                    if (IsGravWarp(child, warp)
                        && child.AttachedManager != null
                        && child.AttachedManager.TryGetEntityById(warp.TargetEntityGuid, out var dest)
                        && dest.HasDataBlob<JPSurveyableDB>())
                        claimed.Add(dest.Id);
                }
            }

            return claimed;
        }

        private static bool IsGravWarp(Entity ship, WarpMoveCommand warp)
        {
            if (warp.TargetEntityGuid == 0 || ship.AttachedManager == null)
                return false;
            return ship.AttachedManager.TryGetEntityById(warp.TargetEntityGuid, out var dest)
                   && dest.HasDataBlob<JPSurveyableDB>();
        }

        private int FactionIdForSurvey()
            => RequestingFactionGuid != 0 ? RequestingFactionGuid : _entityCommanding.FactionOwnerID;

        private Entity? FindNearestEligibleAnomaly(Entity fromShip, HashSet<int> claimed)
        {
            if (!fromShip.TryGetDataBlob<PositionDB>(out var shipPos))
                return null;
            if (_entityCommanding.Manager == null)
                return null;

            int factionId = FactionIdForSurvey();
            Entity? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var anomaly in _entityCommanding.AttachedManager.GetAllEntitiesWithDataBlob<JPSurveyableDB>())
            {
                if (claimed.Contains(anomaly.Id))
                    continue;
                if (!anomaly.TryGetDataBlob<JPSurveyableDB>(out var surveyDB) || surveyDB == null)
                    continue;
                if (surveyDB.IsSurveyComplete(factionId))
                    continue;
                if (!anomaly.TryGetDataBlob<PositionDB>(out var anomalyPos))
                    continue;

                double distance = anomalyPos.GetDistanceTo_m(shipPos);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = anomaly;
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
            return new MoveToNearestGravSurveyAction
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
