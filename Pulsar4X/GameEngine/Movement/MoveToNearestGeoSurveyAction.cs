using System;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.GeoSurveys;

namespace Pulsar4X.Movement
{
    /// <summary>
    /// Standing/issue action: find the nearest unsurveyed geo body (excluding owned colonies),
    /// warp there if needed, and survey until complete. Replaces the old move-only behaviour
    /// that finished instantly when already at a body and ping-ponged with Refuel.
    /// </summary>
    public class MoveToNearestGeoSurveyAction : EntityCommand
    {
        public override string Name => _survey?.Name ?? "Geo Survey Nearest";

        public override string Details => _survey?.Details
            ?? "Find nearest unsurveyed body (excluding colonies), move there, and geo-survey.";

        public override ActionLaneTypes ActionLanes =>
            ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

        public override bool IsBlocking => true;

        private Entity _entityCommanding = null!;
        private GeoSurveyOrder? _survey;
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

            if (_survey != null)
                return _isFinished = _survey.IsFinished();

            return _isFinished = false;
        }

        internal override void Execute(DateTime atDateTime)
        {
            if (_survey == null)
            {
                var target = FindNearestEligibleBody();
                if (target == null)
                {
                    _noTargets = true;
                    IsRunning = true;
                    return;
                }

                _survey = new GeoSurveyOrder(_entityCommanding, target)
                {
                    RequestingFactionGuid = RequestingFactionGuid,
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

        private Entity? FindNearestEligibleBody()
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

            Entity? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var body in _entityCommanding.Manager.GetAllEntitiesWithDataBlob<GeoSurveyableDB>())
            {
                if (!GeoSurveyTargets.IsEligible(body, RequestingFactionGuid))
                    continue;
                if (!body.TryGetDataBlob<PositionDB>(out var bodyPos))
                    continue;

                double distance = bodyPos.GetDistanceTo_m(flagshipPos);
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
