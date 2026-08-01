using System;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.JumpPoints;

namespace Pulsar4X.Movement
{
    /// <summary>
    /// Standing action: find the nearest unsurveyed grav anomaly, warp there if needed,
    /// and JP/grav-survey until complete.
    /// </summary>
    public class MoveToNearestGravSurveyAction : EntityCommand
    {
        public override string Name => _survey?.Name ?? "Grav Survey Nearest";

        public override string Details => _survey?.Details
            ?? "Find nearest unsurveyed grav anomaly, move there, and survey.";

        public override ActionLaneTypes ActionLanes =>
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = null!;
        private JPSurveyOrder? _survey;
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

            if (_survey != null)
                return _isFinished = _survey.IsFinished();

            return _isFinished = false;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (_survey == null)
            {
                var target = FindNearestEligibleAnomaly();
                if (target == null)
                {
                    _noTargets = true;
                    IsRunning = true;
                    if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                        fleetDB.StandingStatusMessage = "Can't find more anomalies";
                    DebugTraceLog.Warn("Standing",
                        $"fleet id={_entityCommanding.Id}: Grav Survey Nearest — no eligible anomaly " +
                        $"(faction={FactionIdForSurvey()})",
                        atDateTime);
                    return;
                }

                _survey = new JPSurveyOrder(_entityCommanding, target)
                {
                    RequestingFactionGuid = FactionIdForSurvey(),
                    EntityCommandingGuid = EntityCommandingGuid,
                    Source = Source,
                    UseActionLanes = UseActionLanes,
                    CreatedDate = CreatedDate,
                    ActionOnDate = ActionOnDate,
                };
            }

            IsRunning = true;
            _survey.Execute(atDateTime);
        }

        private int FactionIdForSurvey()
            => RequestingFactionGuid != 0 ? RequestingFactionGuid : _entityCommanding.FactionOwnerID;

        private Entity? FindNearestEligibleAnomaly()
        {
            if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
                return null;
            if (fleetDB.FlagShipID == -1)
                return null;
            if (_entityCommanding.Manager == null)
                return null;
            if (!_entityCommanding.Manager.TryGetEntityById(fleetDB.FlagShipID, out var flagship))
                return null;
            if (!flagship.TryGetDataBlob<PositionDB>(out var flagshipPos))
                return null;

            int factionId = FactionIdForSurvey();
            Entity? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var anomaly in _entityCommanding.Manager.GetAllEntitiesWithDataBlob<JPSurveyableDB>())
            {
                if (!anomaly.TryGetDataBlob<JPSurveyableDB>(out var surveyDB) || surveyDB == null)
                    continue;
                if (surveyDB.IsSurveyComplete(factionId))
                    continue;
                if (!anomaly.TryGetDataBlob<PositionDB>(out var anomalyPos))
                    continue;

                double distance = anomalyPos.GetDistanceTo_m(flagshipPos);
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
