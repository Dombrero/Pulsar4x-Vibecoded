using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Messaging;
using Pulsar4X.Movement;
using Pulsar4X.Names;

namespace Pulsar4X.Fleets;

/// <summary>
/// Standing orders (geo/grav) are scoped to the flagship's current star system.
/// After a jump, stale suppress windows and survey targets from the previous system
/// must not block work in the new system.
/// </summary>
internal static class FleetStandingSystemSync
{
    /// <summary>
    /// Call when the flagship's system may have changed (jump arrival, hourly standing tick).
    /// </summary>
    internal static void OnFlagshipSystemChanged(Entity fleet, FleetDB fleetDB)
    {
        if (!UnsurveyedGeoCondition.TryGetFlagshipSystem(fleet, out var manager))
            return;

        string systemId = manager.ManagerID;
        string? previous = fleetDB.StandingLastFlagshipSystemId;

        if (previous == systemId)
            return;

        fleetDB.StandingLastFlagshipSystemId = systemId;

        // First observation after load — don't wipe standing state.
        if (previous == null)
            return;

        fleetDB.StandingSuppressUntil = null;
        fleetDB.StandingStatusMessage = null;
        fleetDB.ActiveStandingOrderIndex = -1;

        if (fleet.TryGetDataBlob<OrderableDB>(out var orderable))
        {
            int removed = orderable.ActionList.RemoveAll(a =>
                a.Source == OrderSource.Standing && TargetsOtherSystem(a, systemId));

            if (removed > 0)
            {
                FleetOrderCleanup.AbortShipMovementOrders(fleet);
                PublishOrdersChanged(fleet);
            }
        }

        DebugTraceLog.Info("Standing",
            $"{FleetLabel(fleet)}: flagship system changed {previous} → {systemId} " +
            $"(cleared suppress + stale standing queue)",
            fleet.StarSysDateTime);
    }

    internal static bool TargetsOtherSystem(EntityCommand action, string currentSystemId)
    {
        if (action is GeoSurveyOrder geo && geo.Target.IsValid)
            return geo.Target.AttachedManager.ManagerID != currentSystemId;

        if (action is MoveToNearestGeoSurveyAction)
            return false;

        if (action is MoveToNearestGravSurveyAction or JPSurveyOrder or MoveToNearestAnomalyAction)
            return false;

        return false;
    }

    internal static bool EntityInSystem(Entity entity, string systemId)
        => entity.IsValid && entity.AttachedManager.ManagerID == systemId;

    private static void PublishOrdersChanged(Entity fleet)
    {
        _ = MessagePublisher.Instance.Publish(Message.Create(
            MessageTypes.OrdersChanged,
            entityId: fleet.Id,
            systemId: fleet.AttachedManager.ManagerID,
            factionId: fleet.FactionOwnerID));
    }

    private static string FleetLabel(Entity fleet)
    {
        string name = fleet.TryGetDataBlob<NameDB>(out var n)
            ? n.OwnersName
            : $"Fleet {fleet.Id}";
        return $"{name} id={fleet.Id}";
    }
}
