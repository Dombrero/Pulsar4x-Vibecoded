using System;
using System.Linq;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.JumpPoints;
using Pulsar4X.Movement;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Fleets
{
    /// <summary>
    /// Shared tank-fuel checks for jump / standing travel: outbound hop plus a return reserve
    /// (~2 similar hops), matching <see cref="StandingActionAffordability"/>.
    /// </summary>
    internal static class MissionFuelEstimator
    {
        private const double MetersPerAu = 149597870700.0;
        /// <summary>Min-billable hop distance used when already at a gate (outbound need is 0).</summary>
        private const double MinReserveHop_m = 0.01 * MetersPerAu;
        private const double AtGateDistance_m = 1e6;

        internal readonly record struct Assessment(
            bool CanAffordOutbound,
            bool CanAffordRoundTrip,
            long StoredUnits,
            long OutboundNeedUnits,
            long RoundTripNeedUnits,
            string Reason)
        {
            public static Assessment Ok(long stored) =>
                new(true, true, stored, 0, 0, "");

            public static Assessment NoDrive(string reason) =>
                new(false, false, 0, 0, 0, reason);
        }

        /// <summary>
        /// Standing / survey travel: one hop of <paramref name="distance_m"/> plus ~2× tank reserve.
        /// </summary>
        internal static Assessment EvaluateTravelHop(Entity ship, double distance_m)
        {
            if (!ship.HasDataBlob<WarpAbilityDB>())
                return Assessment.NoDrive("Ship has no warp drive.");

            if (!TryGetTankState(ship, out long stored, out var fuel))
            {
                // No tank / no fuel type — treat as free (same as CanAffordWarpHop).
                return Assessment.Ok(0);
            }

            if (fuel != null && distance_m > AtGateDistance_m && stored <= 0)
            {
                return new Assessment(false, false, 0, 0, 0,
                    "Cargo fuel tank empty — cannot warp.");
            }

            bool outboundOk = distance_m <= AtGateDistance_m
                || WarpMoveProcessor.CanAffordWarpHop(ship, distance_m);

            long outboundNeed = WarpMoveProcessor.EstimateWarpTankFuelUnits(ship, distance_m);
            long roundTripNeed = outboundNeed <= 0 ? 0 : outboundNeed * 2;

            if (outboundNeed <= 0)
            {
                return new Assessment(outboundOk, outboundOk, stored, 0, 0,
                    outboundOk ? "" : "Not enough warp energy for this hop.");
            }

            bool roundTripOk = outboundOk && stored >= roundTripNeed;
            string reason = "";
            if (!outboundOk)
            {
                reason = stored < outboundNeed
                    ? $"Need {outboundNeed} fuel units for outbound hop, have {stored}."
                    : "Not enough warp energy for outbound hop.";
            }
            else if (!roundTripOk)
            {
                reason =
                    $"Need {roundTripNeed} fuel units for outbound + return reserve " +
                    $"(2× hop of {outboundNeed}), have {stored}.";
            }

            return new Assessment(outboundOk, roundTripOk, stored, outboundNeed, roundTripNeed, reason);
        }

        /// <summary>
        /// Jump via gate: warp to gate (if needed) plus optional return-hop reserve.
        /// Jump itself does not burn tank fuel. Standing Refuel jumps home with empty tanks
        /// must use <paramref name="requireReturnReserve"/> = false (the destination is fuel).
        /// </summary>
        internal static Assessment EvaluateJumpViaGate(
            Entity ship, Entity gateEntity, bool requireReturnReserve = true)
        {
            if (!ship.HasDataBlob<WarpAbilityDB>())
                return Assessment.NoDrive("Ship has no warp drive.");

            if (!gateEntity.IsValid
                || ship.AttachedManager == null
                || gateEntity.AttachedManager != ship.AttachedManager)
            {
                return Assessment.NoDrive("Jump gate is not in this system.");
            }

            if (!TryGetTankState(ship, out long stored, out var fuel))
                return Assessment.Ok(0);

            double dist = DistanceMeters(ship, gateEntity);
            bool atGate = dist <= AtGateDistance_m
                || (ship.TryGetDataBlob<PositionDB>(out var pos) && pos.Parent == gateEntity);

            long outboundNeed = atGate
                ? 0
                : WarpMoveProcessor.EstimateWarpTankFuelUnits(ship, dist);

            bool outboundOk = atGate || WarpMoveProcessor.CanAffordWarpHop(ship, dist);

            if (!requireReturnReserve)
            {
                string outboundReason = "";
                if (!outboundOk)
                {
                    outboundReason = stored < outboundNeed
                        ? $"Need {outboundNeed} fuel units to reach the jump gate, have {stored}."
                        : "Not enough warp energy to reach the jump gate.";
                }
                return new Assessment(outboundOk, outboundOk, stored, outboundNeed, outboundNeed, outboundReason);
            }

            // Return reserve: 2× outbound hop, or 2× min-billable when already at the gate.
            long unitHop = outboundNeed > 0
                ? outboundNeed
                : WarpMoveProcessor.EstimateWarpTankFuelUnits(ship, MinReserveHop_m);
            long roundTripNeed = unitHop <= 0 ? 0 : unitHop * 2;

            if (fuel != null && roundTripNeed > 0 && stored <= 0)
            {
                return new Assessment(false, false, 0, outboundNeed, roundTripNeed,
                    "Cargo fuel tank empty — cannot reach gate / return after jump.");
            }

            bool roundTripOk = outboundOk && (roundTripNeed <= 0 || stored >= roundTripNeed);

            string reason = "";
            if (!outboundOk)
            {
                reason = stored < outboundNeed
                    ? $"Need {outboundNeed} fuel units to reach the jump gate, have {stored}."
                    : "Not enough warp energy to reach the jump gate.";
            }
            else if (!roundTripOk)
            {
                reason =
                    $"Need {roundTripNeed} fuel units for gate hop + return reserve, have {stored}. " +
                    "Refuel before jumping to another system.";
            }

            return new Assessment(outboundOk, roundTripOk, stored, outboundNeed, roundTripNeed, reason);
        }

        /// <summary>
        /// True when at least one local fleet hull can afford a round-trip jump via the gate.
        /// </summary>
        internal static bool AnyShipCanAffordFleetJump(
            Entity fleet, Entity gateEntity, out string rejectReason)
        {
            rejectReason = "No ships can afford the jump (fuel for gate + return reserve).";
            if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            {
                rejectReason = "Fleet data missing.";
                return false;
            }

            var ships = fleetDB.Children
                .Where(c => c.HasDataBlob<ShipInfoDB>()
                            && c.HasDataBlob<WarpAbilityDB>()
                            && c.AttachedManager == gateEntity.AttachedManager)
                .ToList();

            if (ships.Count == 0)
            {
                rejectReason = "No warp-capable ships in this system to jump.";
                return false;
            }

            Assessment? firstFail = null;
            foreach (var ship in ships)
            {
                var a = EvaluateJumpViaGate(ship, gateEntity);
                if (a.CanAffordRoundTrip)
                    return true;
                firstFail ??= a;
            }

            if (firstFail is { } fail && !string.IsNullOrEmpty(fail.Reason))
                rejectReason = fail.Reason;
            return false;
        }

        /// <summary>
        /// Human-readable warning for the ship panel (null when tanks look fine for mission hops).
        /// </summary>
        internal static string? TryBuildMissionFuelWarning(Entity ship)
        {
            if (!ship.HasDataBlob<WarpAbilityDB>() || !ship.HasDataBlob<ShipInfoDB>())
                return null;

            if (ship.TryGetDataBlob<ShipStandingStateDB>(out var standing)
                && !string.IsNullOrWhiteSpace(standing.StatusMessage)
                && LooksLikeFuelStatus(standing.StatusMessage))
            {
                return standing.StatusMessage;
            }

            // Prefer a concrete pending gate / jump target.
            if (TryGetPendingJumpGate(ship, out var gate))
            {
                var jump = EvaluateJumpViaGate(ship, gate);
                return jump.CanAffordRoundTrip ? null : jump.Reason;
            }

            if (TryGetPendingWarpTarget(ship, out var warpTarget, out double warpDist))
            {
                var hop = EvaluateTravelHop(ship, warpDist);
                if (!hop.CanAffordRoundTrip)
                    return hop.Reason;

                // Warping to a jump point: also apply jump reserve wording.
                if (warpTarget.HasDataBlob<JumpPointDB>())
                {
                    var jump = EvaluateJumpViaGate(ship, warpTarget);
                    return jump.CanAffordRoundTrip ? null : jump.Reason;
                }

                return null;
            }

            // Standing return path (colony / last refuel gate) unaffordable.
            var fleet = FleetLookup.FindFleetContainingShip(ship);
            if (fleet.IsValid && fleet.TryGetDataBlob<FleetDB>(out var fleetDB)
                && !StandingActionAffordability.CanAffordReturnPathForShip(ship, fleet, fleetDB))
            {
                return "Need fuel reserve to return to last refuel site / colony.";
            }

            // Proactive: nearest jump gate in-system looks unaffordable for round-trip.
            if (TryFindNearestJumpGate(ship, out var nearestGate))
            {
                var jump = EvaluateJumpViaGate(ship, nearestGate);
                if (!jump.CanAffordRoundTrip && jump.RoundTripNeedUnits > 0)
                    return jump.Reason;
            }

            return null;
        }

        private static bool LooksLikeFuelStatus(string message)
        {
            return message.Contains("fuel", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("refuel", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("gate", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetPendingJumpGate(Entity ship, out Entity gate)
        {
            gate = Entity.InvalidEntity;
            if (!ship.TryGetDataBlob<OrderableDB>(out var orderable))
                return false;

            foreach (var action in orderable.ActionList)
            {
                if (action is ShipJumpCommand jump
                    && !jump.GetIsFinished
                    && jump.JumpGate?.OwningEntity is { IsValid: true } g)
                {
                    gate = g;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetPendingWarpTarget(Entity ship, out Entity target, out double distance_m)
        {
            target = Entity.InvalidEntity;
            distance_m = 0;
            if (!ship.TryGetDataBlob<OrderableDB>(out var orderable))
                return false;

            foreach (var action in orderable.ActionList)
            {
                if (action is not WarpMoveCommand warpCmd
                    || warpCmd.WasCancelled
                    || warpCmd.GetIsFinished)
                    continue;

                if (!ship.AttachedManager.TryGetEntityById(warpCmd.TargetEntityGuid, out target)
                    && !(ship.AttachedManager.Game?.GlobalManager
                        .TryGetGlobalEntityById(warpCmd.TargetEntityGuid, out target) ?? false))
                    return false;

                distance_m = DistanceMeters(ship, target);
                return true;
            }

            return false;
        }

        private static bool TryFindNearestJumpGate(Entity ship, out Entity gate)
        {
            gate = Entity.InvalidEntity;
            var manager = ship.AttachedManager;
            if (manager == null || !ship.TryGetDataBlob<PositionDB>(out var shipPos))
                return false;

            double best = double.MaxValue;
            foreach (var jp in manager.GetAllEntitiesWithDataBlob<JumpPointDB>())
            {
                var db = jp.GetDataBlob<JumpPointDB>();
                if (!db.IsDiscovered.Contains(ship.FactionOwnerID))
                    continue;
                if (!jp.TryGetDataBlob<PositionDB>(out var jpPos))
                    continue;
                double d = (jpPos.AbsolutePosition - shipPos.AbsolutePosition).Length();
                if (d < best)
                {
                    best = d;
                    gate = jp;
                }
            }

            return gate.IsValid;
        }

        private static double DistanceMeters(Entity ship, Entity target)
        {
            if (!ship.TryGetDataBlob<PositionDB>(out var shipPos)
                || !target.TryGetDataBlob<PositionDB>(out var tgtPos))
                return double.MaxValue;
            return (tgtPos.AbsolutePosition - shipPos.AbsolutePosition).Length();
        }

        private static bool TryGetTankState(Entity ship, out long stored, out ICargoable? fuel)
        {
            stored = 0;
            fuel = null;
            try
            {
                if (!ship.TryGetDataBlob<CargoStorageDB>(out var storage))
                    return false;

                var cargoLib = ship.GetFactionOwner.GetDataBlob<Factions.FactionInfoDB>().Data.CargoGoods;
                (fuel, _) = ship.GetFuelInfo(cargoLib);
                if (fuel == null)
                    return false;

                stored = storage.GetUnitsStored(fuel, includeEscro: false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
