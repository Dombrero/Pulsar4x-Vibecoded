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

namespace Pulsar4X.JumpPoints;

/// <summary>
/// Travel to the grav anomaly (if needed), then JP/grav-survey.
/// Progress only counts when ships are within survey range — matches <see cref="JPSurveyProcessor"/>.
/// </summary>
public class JPSurveyOrder : EntityCommand
{
    /// <summary>Must match the range check in <see cref="JPSurveyProcessor"/>.</summary>
    internal const double SurveyRange_m = 100_000;

    public override ActionLaneTypes ActionLanes => ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

    public override bool IsBlocking => true;

    public override string Name
    {
        get
        {
            string targetName = Target?.GetOwnersName() ?? "?";
            if (!IsAtTarget())
                return $"Jump Point Survey {targetName} (en route)";
            return $"Jump Point Survey {targetName} ({GetProgressPercent():0.#}%)";
        }
    }

    public override string Details => IsAtTarget()
        ? "Surveying at target."
        : "Moving to grav anomaly before scanning.";

    public Entity Target { get; set; } = Entity.InvalidEntity;
    public JPSurveyableDB? TargetSurveyDB { get; private set; } = null;

    private Entity _entityCommanding = Entity.InvalidEntity;
    private readonly List<WarpMoveCommand> _travelCommands = new();
    private bool _surveyStarted;

    internal override Entity EntityCommanding => _entityCommanding;

    public JPSurveyOrder() { }

    public JPSurveyOrder(Entity commandingEntity, Entity target)
    {
        _entityCommanding = commandingEntity;
        Target = target;
        if (Target.TryGetDataBlob<JPSurveyableDB>(out var jpSurveyableDB))
            TargetSurveyDB = jpSurveyableDB;
    }

    public override EntityCommand Clone()
    {
        return new JPSurveyOrder(EntityCommanding, Target)
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
        return _isFinished = TargetSurveyDB == null
            || TargetSurveyDB.IsSurveyComplete(EntityCommanding.FactionOwnerID);
    }

    internal override void Execute(DateTime atDateTime)
    {
        if (TargetSurveyDB == null || IsFinished())
            return;

        IsRunning = true;

        if (!IsAtTarget())
        {
            EnsureTravel(atDateTime);
            return;
        }

        if (!_surveyStarted)
            StartSurvey();
    }

    private void StartSurvey()
    {
        _surveyStarted = true;

        if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            foreach (var child in fleetDB.Children)
            {
                if (!child.HasJPSurveyAbililty())
                    continue;
                if (child.HasDataBlob<FleetDB>())
                    continue;

                child.SetDataBlob(new JPSurveyDB { TargetId = Target.Id });
            }
        }
        else if (_entityCommanding.HasDataBlob<ShipInfoDB>() && _entityCommanding.HasJPSurveyAbililty())
        {
            _entityCommanding.SetDataBlob(new JPSurveyDB { TargetId = Target.Id });
        }
    }

    private bool IsAtTarget()
    {
        if (Target == null)
            return false;

        if (_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            return fleetDB.Children
                .Where(c => c.HasDataBlob<ShipInfoDB>())
                .Any(IsShipNearTarget);
        }

        return IsShipNearTarget(_entityCommanding);
    }

    private bool IsShipNearTarget(Entity ship)
    {
        if (!ship.TryGetDataBlob<PositionDB>(out var shipPos) || !Target.TryGetDataBlob<PositionDB>(out var targetPos))
            return false;

        return targetPos.GetDistanceTo_m(shipPos) < SurveyRange_m;
    }

    private void EnsureTravel(DateTime atDateTime)
    {
        if (!_entityCommanding.TryGetDataBlob<FleetDB>(out var fleetDB))
        {
            // Single ship: warp itself if capable.
            if (_entityCommanding.HasDataBlob<WarpAbilityDB>()
                && _entityCommanding.HasDataBlob<ShipInfoDB>()
                && !IsShipNearTarget(_entityCommanding))
            {
                if (_travelCommands.Any(c => !c.WasCancelled && !c.IsFinished()))
                    return;

                FleetOrderCleanup.AbortShipOrdersBlockingMovement(_entityCommanding);
                _travelCommands.Clear();
                try
                {
                    var cmd = WarpMoveCommand.CreateCommandEZ(_entityCommanding, Target, atDateTime);
                    _travelCommands.Add(cmd);
                    _entityCommanding.AttachedManager.Game.OrderHandler.HandleOrder(cmd);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"JPSurvey travel failed for ship {_entityCommanding.Id}: {ex}");
                }
            }
            return;
        }

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
                        && !IsShipNearTarget(c))
            .ToList();

        if (shipsNeedingTravel.Count == 0)
            return;

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
                if (!ship.AttachedManager.Game.OrderHandler.HandleOrder(cmd))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"JPSurvey travel HandleOrder rejected for ship {ship.Id} → {Target?.Id}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JPSurvey travel failed for ship {ship.Id}: {ex}");
            }
        }
    }

    internal override bool IsValidCommand(Game game)
        => TargetSurveyDB != null;

    public static JPSurveyOrder CreateCommand(int requestingFactionId, Entity fleet, Entity target)
    {
        return new JPSurveyOrder(fleet, target)
        {
            RequestingFactionGuid = requestingFactionId,
            EntityCommandingGuid = fleet.Id,
            Source = OrderSource.Issued,
        };
    }

    private float GetProgressPercent()
    {
        if (TargetSurveyDB == null) return 0f;
        if (!TargetSurveyDB.HasSurveyStarted(RequestingFactionGuid)) return 0f;

        uint pointsRequired = TargetSurveyDB.PointsRequired;
        uint currentValue = TargetSurveyDB.SurveyPointsRemaining[RequestingFactionGuid];
        if (pointsRequired == 0) return 100f;

        return (1f - ((float)currentValue / (float)pointsRequired)) * 100f;
    }
}
