using System;
using System.Collections.Generic;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.Movement;
using Pulsar4X.Ships;

namespace Pulsar4X.GeoSurveys;

/// <summary>
/// Single self-contained order: travel to the body (if needed), then geo-survey.
/// Progress only counts ships that are actually at the target — no remote surveying.
/// </summary>
public class GeoSurveyOrder : EntityCommand
{
    /// <summary>
    /// While en route, do not occupy the Movement lane — ship-level goals queue
    /// <see cref="WarpMoveCommand"/> on the same hull; holding Movement forever blocked warp.
    /// Fleet-level orders avoid this because warps are issued onto member ships.
    /// </summary>
    public override ActionLaneTypes ActionLanes =>
        Target.IsValid && IsAtTarget()
            ? ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity
            : ActionLaneTypes.InteractWithExternalEntity;

    public override bool IsBlocking => true;

    public override string Name
    {
        get
        {
            string targetName = Target?.GetOwnersName() ?? "?";
            if (!IsAtTarget())
                return $"Geo Survey {targetName} (en route)";
            return $"Geo Survey {targetName} ({GetProgressPercent():0.#}%)";
        }
    }

    public override string Details => IsAtTarget()
        ? "Surveying at target."
        : "Moving to survey target before scanning.";

    public Entity Target { get; set; } = Entity.InvalidEntity;
    public GeoSurveyableDB? TargetGeoSurveyDB { get; private set; } = null;
    public DateTime? PreviousUpdate { get; private set; } = null;
    public GeoSurveyProcessor? Processor { get; private set; } = null;

    private Entity _entityCommanding = Entity.InvalidEntity;
    private readonly List<WarpMoveCommand> _travelCommands = new();
    private bool _surveyStarted;

    internal override Entity EntityCommanding => _entityCommanding;

    public GeoSurveyOrder() { }

    public GeoSurveyOrder(Entity commandingEntity, Entity target)
    {
        _entityCommanding = commandingEntity;
        Target = target;
        if (Target.TryGetDataBlob<GeoSurveyableDB>(out var geoSurveyableDB))
            TargetGeoSurveyDB = geoSurveyableDB;
    }

    public override EntityCommand Clone()
    {
        return new GeoSurveyOrder(EntityCommanding, Target)
        {
            UseActionLanes = UseActionLanes,
            RequestingFactionGuid = RequestingFactionGuid,
            EntityCommandingGuid = EntityCommandingGuid,
            CreatedDate = CreatedDate,
            ActionOnDate = ActionOnDate,
            ActionedOnDate = ActionedOnDate,
            IsRunning = IsRunning,
            Source = Source,
        };
    }

    internal override bool IsFinished()
    {
        bool finished = TargetGeoSurveyDB == null
            || TargetGeoSurveyDB.IsSurveyComplete(EntityCommanding.FactionOwnerID);
        if (finished)
            ClearSurveyingBlobs();
        return _isFinished = finished;
    }

    internal override void Execute(DateTime atDateTime)
    {
        if (TargetGeoSurveyDB == null || IsFinished())
            return;

        if (Target.IsValid
            && UnsurveyedGeoCondition.TryGetFlagshipSystem(_entityCommanding, out var flagshipSystem)
            && Target.AttachedManager.ManagerID != flagshipSystem.ManagerID)
        {
            ClearSurveyingBlobs();
            IsRunning = true;
            _isFinished = true;
            return;
        }

        IsRunning = true;

        if (!IsAtTarget())
        {
            ClearSurveyingBlobs();
            EnsureTravel(atDateTime);
            return;
        }

        // Arrived — survey only from ships on station.
        if (!_surveyStarted)
        {
            _surveyStarted = true;
            PreviousUpdate = atDateTime;
            Processor = new GeoSurveyProcessor(EntityCommanding, Target);
            SyncSurveyingBlobs();
            return;
        }

        SyncSurveyingBlobs();

        if (PreviousUpdate != null && atDateTime - PreviousUpdate >= TimeSpan.FromDays(1))
        {
            Processor?.ProcessEntity(EntityCommanding, atDateTime);
            PreviousUpdate = atDateTime;
        }
    }

    private bool IsAtTarget()
    {
        if (Target == null)
            return false;
        if (_entityCommanding.HasDataBlob<FleetDB>())
            return FleetOrderCleanup.IsFleetAtBody(_entityCommanding, Target);
        return FleetOrderCleanup.IsShipAtBody(_entityCommanding, Target);
    }

    /// <summary>
    /// Put <see cref="GeoSurveyingDB"/> on hulls that can and are surveying; clear it elsewhere.
    /// </summary>
    private void SyncSurveyingBlobs()
    {
        if (Target == null)
        {
            ClearSurveyingBlobs();
            return;
        }

        if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            foreach (var child in fleetDB.Children)
            {
                if (child.HasDataBlob<FleetDB>())
                    continue;
                if (!child.HasDataBlob<ShipInfoDB>())
                    continue;

                bool surveying = child.HasDataBlob<GeoSurveyAbilityDB>()
                    && FleetOrderCleanup.IsShipAtBody(child, Target);
                if (surveying)
                    child.SetDataBlob(new GeoSurveyingDB { TargetId = Target.Id });
                else if (child.HasDataBlob<GeoSurveyingDB>())
                    child.RemoveDataBlob<GeoSurveyingDB>();
            }
            return;
        }

        if (_entityCommanding.HasDataBlob<ShipInfoDB>()
            && _entityCommanding.HasDataBlob<GeoSurveyAbilityDB>()
            && FleetOrderCleanup.IsShipAtBody(_entityCommanding, Target))
        {
            _entityCommanding.SetDataBlob(new GeoSurveyingDB { TargetId = Target.Id });
        }
        else if (_entityCommanding.HasDataBlob<GeoSurveyingDB>())
        {
            _entityCommanding.RemoveDataBlob<GeoSurveyingDB>();
        }
    }

    private void ClearSurveyingBlobs()
    {
        if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            foreach (var child in fleetDB.Children)
            {
                if (child.HasDataBlob<GeoSurveyingDB>())
                    child.RemoveDataBlob<GeoSurveyingDB>();
            }
        }

        if (_entityCommanding.HasDataBlob<GeoSurveyingDB>())
            _entityCommanding.RemoveDataBlob<GeoSurveyingDB>();
    }

    private void EnsureTravel(DateTime atDateTime)
    {
        if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            // Goal planners assign GeoSurveyOrder to a ship — warp that hull directly.
            if (_entityCommanding.HasDataBlob<WarpAbilityDB>()
                && _entityCommanding.HasDataBlob<ShipInfoDB>()
                && Target.IsValid
                && !FleetOrderCleanup.IsShipAtBody(_entityCommanding, Target))
            {
                if (_travelCommands.Any(c => !c.WasCancelled && !c.IsFinished()))
                    return;

                FleetOrderCleanup.AbortShipOrdersBlockingMovement(_entityCommanding);
                _travelCommands.Clear();
                try
                {
                    var cmd = WarpMoveCommand.CreateCommandEZ(_entityCommanding, Target, atDateTime);
                    _travelCommands.Add(cmd);
                    OrderEnqueue.Enqueue(_entityCommanding.AttachedManager.Game, cmd);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"GeoSurvey travel failed for ship {_entityCommanding.Id}: {ex}");
                }
            }
            return;
        }

        // Still warping toward this target — wait; do not stack another hop.
        if (_travelCommands.Any(c => !c.WasCancelled && !c.IsFinished()))
            return;

        bool needsRedispatch = _travelCommands.Count == 0
            || _travelCommands.Any(c => c.WasCancelled)
            || _travelCommands.All(c =>
                !c.EntityCommanding.TryGetDataBlob<OrderableDB>(out var shipOrders)
                || !shipOrders.ActionList.Contains(c));

        if (!needsRedispatch)
            return;

        var shipsNeedingTravel = fleetDB.Children
            .Where(c => c.HasDataBlob<ShipInfoDB>()
                        && c.HasDataBlob<WarpAbilityDB>()
                        && !FleetOrderCleanup.IsShipAtBody(c, Target))
            .ToList();

        if (shipsNeedingTravel.Count == 0)
            return;

        // Leaving the current body for survey — clear cargo so Movement is free for warp.
        FleetOrderCleanup.AbortShipOrdersBlockingMovement(_entityCommanding);

        _travelCommands.Clear();

        if (!Target.IsValid)
            return;

        Entity surveyTarget = Target;

        foreach (var ship in shipsNeedingTravel)
        {
            try
            {
                var cmd = WarpMoveCommand.CreateCommandEZ(ship, surveyTarget, atDateTime);
                _travelCommands.Add(cmd);
                if (!OrderEnqueue.Enqueue(ship.AttachedManager.Game, cmd))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"GeoSurvey travel HandleOrder rejected for ship {ship.Id} → {Target?.Id}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GeoSurvey travel failed for ship {ship.Id}: {ex}");
            }
        }
    }

    internal override bool IsValidCommand(Game game)
        => TargetGeoSurveyDB != null;

    public static GeoSurveyOrder CreateCommand(int requestingFactionId, Entity fleet, Entity target)
    {
        return new GeoSurveyOrder(fleet, target)
        {
            RequestingFactionGuid = requestingFactionId,
            EntityCommandingGuid = fleet.Id,
            Source = OrderSource.Issued,
        };
    }

    private float GetProgressPercent()
    {
        if (TargetGeoSurveyDB == null) return 0f;
        if (!TargetGeoSurveyDB.HasSurveyStarted(RequestingFactionGuid)) return 0f;

        uint pointsRequired = TargetGeoSurveyDB.PointsRequired;
        uint currentValue = TargetGeoSurveyDB.GeoSurveyStatus[RequestingFactionGuid];
        if (pointsRequired == 0) return 100f;

        return (1f - ((float)currentValue / (float)pointsRequired)) * 100f;
    }
}
