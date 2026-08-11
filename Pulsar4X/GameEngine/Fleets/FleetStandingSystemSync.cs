using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Names;

namespace Pulsar4X.Fleets;

/// <summary>
/// Tracks flagship system for UI/legacy. Per-ship standing sync lives in
/// <see cref="ShipStandingDirector"/> (each hull clears its own stale survey after a jump).
/// </summary>
internal static class FleetStandingSystemSync
{
    /// <summary>
    /// Record flagship system id. Does not abort sibling ships or clear per-ship commitments.
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
        // ActiveStandingOrderIndex is a UI aggregate rebuilt by FleetOrderProcessor.

        DebugTraceLog.Info("Standing",
            $"{FleetLabel(fleet)}: flagship system changed {previous} → {systemId} " +
            "(per-ship standing sync; no fleet-wide abort)",
            fleet.StarSysDateTime);
    }

    internal static bool TargetsOtherSystem(EntityCommand action, string currentSystemId)
    {
        if (action is GeoSurveyOrder geo && geo.Target.IsValid)
            return geo.Target.AttachedManager.ManagerID != currentSystemId;

        return false;
    }

    internal static bool EntityInSystem(Entity entity, string systemId)
        => entity.IsValid && entity.AttachedManager.ManagerID == systemId;

    private static string FleetLabel(Entity fleet)
    {
        string name = fleet.TryGetDataBlob<NameDB>(out var n)
            ? n.OwnersName
            : $"Fleet {fleet.Id}";
        return $"{name} id={fleet.Id}";
    }
}
