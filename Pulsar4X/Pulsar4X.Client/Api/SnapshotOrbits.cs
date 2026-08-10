using System;
using Pulsar4X.Api;
using Pulsar4X.Orbital;

namespace Pulsar4X.Client;

/// <summary>
/// Client-side orbit propagation at a given simulation time (Aurora-style): Kepler bodies use
/// shared orbital math; warping ships use the last pushed <see cref="PositionView"/>. Optional
/// <see cref="WarpPositionAlongChord"/> remains for order-preview sketches only — live map icons
/// must not use it.
/// </summary>
public static class SnapshotOrbits
{
    public static KeplerElements ToKeplerElements(this OrbitView orbit)
    {
        double a = orbit.SemiMajorAxisM;
        double e = orbit.Eccentricity;
        return new KeplerElements
        {
            StandardGravParameter = orbit.StandardGravParameter,
            SemiMajorAxis = a,
            SemiMinorAxis = a * Math.Sqrt(1 - e * e),
            Eccentricity = e,
            LinearEccentricity = e * a,
            Periapsis = (1 - e) * a,
            Apoapsis = (1 + e) * a,
            LoAN = orbit.LongitudeOfAscendingNodeRad,
            AoP = orbit.ArgumentOfPeriapsisRad,
            Inclination = orbit.InclinationRad,
            MeanMotion = orbit.MeanMotionRadPerSec,
            Period = orbit.OrbitalPeriodSeconds,
            MeanAnomalyAtEpoch = orbit.MeanAnomalyAtEpochRad,
            Epoch = orbit.Epoch,
        };
    }

    /// <summary>The position relative to the orbit parent at the given time, in metres.</summary>
    public static Vector3 RelativePositionM(this OrbitView orbit, DateTime atTime)
        => OrbitalMath.GetPosition(orbit.ToKeplerElements(), atTime);

    /// <summary>Absolute position at <paramref name="atTime"/>: Kepler up the parent chain;
    /// mid-warp uses pushed <see cref="PositionView"/> (ignores <paramref name="atTime"/>).</summary>
    public static Vector3 AbsolutePositionM(this EntitySnapshot entity, IClientSystem system, DateTime atTime)
    {
        if (entity.HasView<WarpMovingView>())
        {
            var position = entity.GetView<PositionView>();
            return position != null
                ? new Vector3(position.AbsolutePosition.X, position.AbsolutePosition.Y, position.AbsolutePosition.Z)
                : Vector3.Zero;
        }

        var orbit = entity.GetView<OrbitView>();
        if (orbit != null && orbit.StandardGravParameter > 0)
        {
            var relative = orbit.RelativePositionM(atTime);
            if (orbit.ParentId is int parentId && system.GetEntity(parentId) is { } parent)
                return parent.AbsolutePositionM(system, atTime) + relative;
            return relative;
        }

        var pos = entity.GetView<PositionView>();
        return pos != null
            ? new Vector3(pos.AbsolutePosition.X, pos.AbsolutePosition.Y, pos.AbsolutePosition.Z)
            : Vector3.Zero;
    }

    /// <summary>
    /// Linear position along Entry→Exit for order-preview UI only. Live map positions must use
    /// <see cref="PositionView"/> via <see cref="AbsolutePositionM"/> / <see cref="SnapshotPosition"/>.
    /// </summary>
    public static Vector3 WarpPositionAlongChord(WarpMovingView warp, DateTime atTime)
    {
        var entry = new Vector3(warp.EntryPointAbsolute.X, warp.EntryPointAbsolute.Y, warp.EntryPointAbsolute.Z);
        var exit = new Vector3(warp.ExitPointAbsolute.X, warp.ExitPointAbsolute.Y, warp.ExitPointAbsolute.Z);

        if (warp.PredictedExitTime <= warp.EntryDateTime)
            return exit;

        if (atTime <= warp.EntryDateTime)
            return entry;
        if (atTime >= warp.PredictedExitTime)
            return exit;

        double total = (warp.PredictedExitTime - warp.EntryDateTime).TotalSeconds;
        if (total <= 1e-9)
            return exit;

        double u = (atTime - warp.EntryDateTime).TotalSeconds / total;
        if (u < 0) u = 0;
        if (u > 1) u = 1;
        return entry + (exit - entry) * u;
    }
}
