using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pulsar4X.Api;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Events;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.GeoSurveys;
using Pulsar4X.JumpPoints;
using Pulsar4X.Ships;

namespace Pulsar4X.Movement;

/// <summary>
/// Detects ships stuck in warp micro-hop / blocked-retry loops, pauses sim time,
/// publishes a player-visible event with diagnosis, and breaks the loop.
/// </summary>
public static class MovementStuckWatchdog
{
    const double MicroHopAu = 0.001;
    const int MicroHopTripCount = 10;
    const int BlockedTripCount = 8;
    static readonly TimeSpan AlertCooldown = TimeSpan.FromDays(1);
    const double MetersPerAu = 149597870700.0;

    sealed class Track
    {
        public int TargetId;
        public int MicroHopCount;
        public int BlockedFuelCount;
        public int BlockedEnergyCount;
        public DateTime WindowStart;
        public DateTime LastAlert = DateTime.MinValue;
        public double LastHopAu;
    }

    static readonly Dictionary<int, Track> s_tracks = new();

    /// <summary>Call after a warp hop completes and burns tank fuel.</summary>
    public static void NoteWarpHopCompleted(Entity ship, Entity? target, double distance_m, DateTime atDateTime)
    {
        if (!ship.IsValid || ship.Id == 0)
            return;

        double au = Math.Max(0, distance_m / MetersPerAu);
        int targetId = target is { IsValid: true } ? target.Id : 0;
        var track = GetOrResetTrack(ship.Id, targetId, atDateTime);
        track.LastHopAu = au;

        if (au > MicroHopAu)
        {
            track.MicroHopCount = 0;
            return;
        }

        // Already parented to the target — tiny hops while surveying/orbiting are fine.
        // Do NOT use IsShipAtBody here: bodies without OrbitDB report infinite SOI and would
        // always look "at body", masking micro-hop loops toward anomalies/static targets.
        if (target is { IsValid: true }
            && ship.TryGetDataBlob<PositionDB>(out var shipPos)
            && shipPos.Parent is { IsValid: true } parent
            && parent.Id == target.Id)
        {
            track.MicroHopCount = 0;
            return;
        }

        track.MicroHopCount++;
        if (track.MicroHopCount < MicroHopTripCount)
            return;

        Trip(ship, target, atDateTime, track, StuckKind.MicroHopLoop);
    }

    /// <summary>Call when a warp order cannot start (fuel / energy / invalid hop).</summary>
    public static void NoteWarpBlocked(Entity ship, Entity? target, StuckKind kind, DateTime atDateTime)
    {
        if (!ship.IsValid || ship.Id == 0)
            return;
        if (kind is not (StuckKind.InsufficientFuel or StuckKind.InsufficientEnergy or StuckKind.InvalidHop))
            return;

        int targetId = target is { IsValid: true } ? target.Id : 0;
        var track = GetOrResetTrack(ship.Id, targetId, atDateTime);

        if (kind == StuckKind.InsufficientFuel)
            track.BlockedFuelCount++;
        else if (kind == StuckKind.InsufficientEnergy)
            track.BlockedEnergyCount++;
        else
            track.MicroHopCount++; // reuse counter for invalid hop spam

        int count = kind switch
        {
            StuckKind.InsufficientFuel => track.BlockedFuelCount,
            StuckKind.InsufficientEnergy => track.BlockedEnergyCount,
            _ => track.MicroHopCount,
        };

        if (count < BlockedTripCount)
            return;

        Trip(ship, target, atDateTime, track, kind);
    }

    public enum StuckKind
    {
        MicroHopLoop,
        InsufficientFuel,
        InsufficientEnergy,
        InvalidHop,
    }

    static Track GetOrResetTrack(int shipId, int targetId, DateTime at)
    {
        if (!s_tracks.TryGetValue(shipId, out var track))
        {
            track = new Track { TargetId = targetId, WindowStart = at };
            s_tracks[shipId] = track;
            return track;
        }

        if (track.TargetId != targetId
            || (at - track.WindowStart) > TimeSpan.FromDays(2))
        {
            track.TargetId = targetId;
            track.WindowStart = at;
            track.MicroHopCount = 0;
            track.BlockedFuelCount = 0;
            track.BlockedEnergyCount = 0;
            track.LastHopAu = 0;
        }

        return track;
    }

    static void Trip(Entity ship, Entity? target, DateTime at, Track track, StuckKind kind)
    {
        if (at - track.LastAlert < AlertCooldown)
            return;
        track.LastAlert = at;
        track.MicroHopCount = 0;
        track.BlockedFuelCount = 0;
        track.BlockedEnergyCount = 0;

        string message = BuildDiagnosis(ship, target, at, kind, track.LastHopAu);
        DebugTraceLog.Warn("Stuck", message, at);

        try
        {
            ship.AttachedManager?.Game?.TimePulse?.PauseTime();
        }
        catch
        {
            /* pause is best-effort */
        }

        try
        {
            EventManager.Instance.Publish(
                Event.Create(
                    EventType.OrdersNotPossible,
                    at,
                    message,
                    ship.FactionOwnerID,
                    ship.AttachedManager?.ManagerID,
                    ship.Id));
        }
        catch
        {
            /* event is best-effort */
        }

        // Ensure the faction event log pauses on this type (many saves never ToggleHaltsOn).
        try
        {
            var faction = ship.GetFactionOwner;
            if (faction.TryGetDataBlob<Factions.FactionInfoDB>(out var info)
                && info.EventLog is Factions.FactionEventLog fel
                && !fel.HaltsOn(EventType.OrdersNotPossible))
            {
                fel.ToggleHaltsOn(EventType.OrdersNotPossible);
            }
        }
        catch
        {
            /* ignore */
        }

        BreakLoop(ship, target, at);
    }

    static string BuildDiagnosis(Entity ship, Entity? target, DateTime at, StuckKind kind, double lastHopAu)
    {
        var sb = new StringBuilder();
        string shipName = ship.GetOwnersName();
        string targetName = target is { IsValid: true } ? target.GetOwnersName() : "(none)";

        sb.AppendLine($"SHIP STUCK — time paused");
        sb.AppendLine($"Ship: {shipName} (id={ship.Id})");
        sb.AppendLine($"Target: {targetName} (id={target?.Id ?? 0})");
        sb.AppendLine($"Game time: {at:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Cause: {KindLabel(kind)}");

        if (ship.TryGetDataBlob<PositionDB>(out var shipPos))
        {
            string parent = shipPos.Parent is { IsValid: true } p ? p.GetOwnersName() : "-";
            sb.AppendLine($"Ship parent: {parent}");
            if (target is { IsValid: true } && target.TryGetDataBlob<PositionDB>(out var tgtPos))
            {
                double dist = tgtPos.GetDistanceTo_m(shipPos);
                double soi = target.GetSOI_m();
                bool atBody = FleetOrderCleanup.IsShipAtBody(ship, target);
                sb.AppendLine($"Distance to target: {dist / MetersPerAu:0.####} AU ({dist:0} m)");
                sb.AppendLine($"Target SOI: {(double.IsInfinity(soi) ? "∞" : $"{soi / MetersPerAu:0.####} AU")} | IsShipAtBody={atBody}");
            }
        }

        if (lastHopAu > 0 || kind == StuckKind.MicroHopLoop)
            sb.AppendLine($"Last hop length: {lastHopAu:0.####} AU");

        if (ship.TryGetDataBlob<WarpAbilityDB>(out var warp))
            sb.AppendLine($"Warp max speed: {warp.MaxSpeed} m/s");

        AppendFuelEnergy(sb, ship);
        AppendOrders(sb, ship);

        sb.AppendLine();
        sb.AppendLine("Suggested checks:");
        sb.AppendLine(kind switch
        {
            StuckKind.InsufficientFuel => "  • Refuel the ship / raise standing Refuel threshold",
            StuckKind.InsufficientEnergy => "  • Recharge at a colony or fit more capacitors / reactor",
            StuckKind.InvalidHop => "  • Warp speed may be 0 or target position is invalid",
            _ => "  • Nested body / SOI miss — ship keeps micro-warping without counting as 'at target'",
        });

        return sb.ToString().TrimEnd();
    }

    static string KindLabel(StuckKind kind) => kind switch
    {
        StuckKind.MicroHopLoop => "Micro-hop loop (repeated near-zero warps without arriving)",
        StuckKind.InsufficientFuel => "Insufficient cargo fuel for warp",
        StuckKind.InsufficientEnergy => "Insufficient warp energy / cannot charge for hop",
        StuckKind.InvalidHop => "Invalid warp hop (speed/distance)",
        _ => kind.ToString(),
    };

    static void AppendFuelEnergy(StringBuilder sb, Entity ship)
    {
        try
        {
            var cargoLib = ship.GetFactionOwner.GetDataBlob<Factions.FactionInfoDB>().Data.CargoGoods;
            double fuelPct = ship.GetFuelPercent(cargoLib);
            sb.AppendLine($"Fuel tank: {fuelPct:0.#}% | HasWarpTankFuel={WarpMoveProcessor.HasWarpTankFuel(ship)}");
        }
        catch
        {
            sb.AppendLine("Fuel tank: (unavailable)");
        }

        if (ship.TryGetDataBlob<WarpAbilityDB>(out var warp)
            && ship.TryGetDataBlob<EnergyGenAbilityDB>(out var power)
            && !string.IsNullOrEmpty(warp.EnergyType))
        {
            power.EnergyStored.TryGetValue(warp.EnergyType, out double stored);
            power.EnergyStoreMax.TryGetValue(warp.EnergyType, out double max);
            sb.AppendLine($"Energy ({warp.EnergyType}): {stored:0}/{max:0} kJ | gen={power.TotalOutputMax:0} kW");
        }
    }

    static void AppendOrders(StringBuilder sb, Entity ship)
    {
        if (!ship.TryGetDataBlob<OrderableDB>(out var orders))
        {
            sb.AppendLine("Ship orders: (none)");
            return;
        }

        var names = orders.ActionList.Select(o => o.Name).Take(8).ToList();
        sb.AppendLine(names.Count == 0
            ? "Ship orders: (empty)"
            : "Ship orders: " + string.Join(" | ", names));

        // Fleet standing context if this hull is assigned.
        foreach (var fleet in ship.Manager?.GetAllEntitiesWithDataBlob<FleetDB>() ?? Enumerable.Empty<Entity>())
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fdb))
                continue;
            if (!fdb.Children.Any(c => c.Id == ship.Id))
                continue;
            sb.AppendLine($"Fleet: {fleet.GetOwnersName()} (id={fleet.Id}) standingIdx={fdb.ActiveStandingOrderIndex}");
            if (!string.IsNullOrEmpty(fdb.StandingStatusMessage))
                sb.AppendLine($"Standing status: {fdb.StandingStatusMessage}");
            if (fleet.TryGetDataBlob<OrderableDB>(out var fleetOrders))
            {
                var fo = fleetOrders.ActionList.Select(o => o.Name).Take(6).ToList();
                if (fo.Count > 0)
                    sb.AppendLine("Fleet orders: " + string.Join(" | ", fo));
            }
            break;
        }
    }

    static void BreakLoop(Entity ship, Entity? target, DateTime at)
    {
        try
        {
            FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);
        }
        catch { /* ignore */ }

        if (ship.TryGetDataBlob<OrderableDB>(out var orders))
        {
            foreach (var cmd in orders.ActionList
                         .Where(a => a is GeoSurveyOrder or JPSurveyOrder or WarpMoveCommand)
                         .ToList())
            {
                if (cmd is WarpMoveCommand warp)
                    warp.CancelInPlace();
                cmd.Status = ActionStatus.Failed;
                orders.ActionList.Remove(cmd);
            }
        }

        if (ship.HasDataBlob<GeoSurveyingDB>())
            ship.RemoveDataBlob<GeoSurveyingDB>();
        if (ship.HasDataBlob<JPSurveyDB>())
            ship.RemoveDataBlob<JPSurveyDB>();

        // Suppress fleet standing so it does not immediately re-enqueue the same target.
        foreach (var fleet in ship.Manager?.GetAllEntitiesWithDataBlob<FleetDB>() ?? Enumerable.Empty<Entity>())
        {
            if (!fleet.TryGetDataBlob<FleetDB>(out var fdb))
                continue;
            if (!fdb.Children.Any(c => c.Id == ship.Id))
                continue;

            fdb.ActiveStandingOrderIndex = -1;
            fdb.StandingSuppressUntil = at + TimeSpan.FromDays(1);
            fdb.StandingStatusMessage = "Stuck travel — paused (see Game Log)";

            if (fleet.TryGetDataBlob<OrderableDB>(out var fleetOrders))
            {
                foreach (var action in fleetOrders.ActionList
                             .Where(a => a is MoveToNearestGeoSurveyAction
                                      or MoveToNearestGravSurveyAction
                                      or GeoSurveyOrder
                                      or JPSurveyOrder)
                             .ToList())
                {
                    action.Status = ActionStatus.Failed;
                    fleetOrders.ActionList.Remove(action);
                }
            }
            break;
        }
    }

    /// <summary>Test helper — clear static state between tests.</summary>
    public static void ResetForTests() => s_tracks.Clear();
}
