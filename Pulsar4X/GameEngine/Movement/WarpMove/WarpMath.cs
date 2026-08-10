using System;
using System.Collections.Generic;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Galaxy;
using Pulsar4X.Orbital;
using Pulsar4X.Orbits;

namespace Pulsar4X.Movement;

public static class WarpMath
{
    /// <summary>
    /// recalculates a shipsMaxSpeed.
    /// </summary>
    /// <param name="ship"></param>
    public static void CalcMaxWarpAndEnergyUsage(Entity ship)
    {
        Dictionary<string, double> totalFuelUsage = new Dictionary<string, double>();
        var instancesDB = ship.GetDataBlob<ComponentInstancesDB>();
        int totalEnginePower = instancesDB.GetTotalEnginePower(out totalFuelUsage);

        //Note: TN aurora uses the TCS for max speed calcs.
        WarpAbilityDB warpDB = ship.GetDataBlob<WarpAbilityDB>();
        warpDB.TotalWarpPower = totalEnginePower;
        //propulsionDB.FuelUsePerKM = totalFuelUsage;

        var mass = ship.GetDataBlob<MassVolumeDB>().MassTotal;
        var maxSpeed = MaxSpeedCalc(totalEnginePower, mass);
        warpDB.MaxSpeed = maxSpeed;

    }

    /// <summary>
    /// Calculates max ship speed based on engine power and ship mass
    /// </summary>
    /// <param name="power">TotalEnginePower</param>
    /// <param name="tonage">HullSize</param>
    /// <returns>Max speed in km/s</returns>
    public static int MaxSpeedCalc(double power, double tonage)
    {
        // From Aurora4x wiki:  Speed = (Total Engine Power / Total Class Size in HS) * 1000 km/s
        return (int)((power / tonage) * 1000);
    }

    struct Orbit
    {
        public Vector3 position;
        public double T;

        public Orbit(Vector3 position, double t)
        {
            this.position = position;
            T = t;
        }
    }

    public static (Vector3 position, DateTime etiDateTime) GetInterceptPosition(Entity mover, Entity target, DateTime atDateTime, Vector3 offsetPosition = new Vector3())
    {
        var moverPos = (Vector3)MoveMath.GetAbsoluteFuturePosition(mover, atDateTime);
        var tgtPos = (Vector3)MoveMath.GetAbsoluteFuturePosition(target, atDateTime);
        var exitPos = tgtPos + offsetPosition;
        double spd_m = mover.GetDataBlob<WarpAbilityDB>().MaxSpeed;

        var tgtMoveType = target.GetDataBlob<PositionDB>().MoveType;
        switch (tgtMoveType)
        {
            case PositionDB.MoveTypes.None:
                {
                    var distance = (exitPos - moverPos).Length();
                    var intercept = ((Vector3)exitPos, atDateTime + TimeSpan.FromSeconds(distance / spd_m));
                    return intercept;
                }
            case PositionDB.MoveTypes.Orbit:
                {
                    var intercept = WarpMath.GetInterceptPosition_m(moverPos, spd_m, target.GetDataBlob<OrbitDB>(), atDateTime, offsetPosition);
                    return intercept;
                }
            //For the following cases, we need to know if the target is an object which is owned by the same empire and we know what it's doing,
            //or if that info is unknown and how do we try predict?
            case PositionDB.MoveTypes.NewtonSimple:
                throw new NotImplementedException("not implemented");
            case PositionDB.MoveTypes.NewtonComplex:
                throw new NotImplementedException("not implemented");
            case PositionDB.MoveTypes.Warp:
                throw new NotImplementedException("not implemented");
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// Calculates a cartisian position for an intercept for a ship and an target's orbit using warp.
    /// </summary>
    public static (Vector3 position, DateTime etiDateTime) GetInterceptPosition(Entity mover, OrbitDB targetOrbit, DateTime atDateTime, Vector3 offsetPosition = new Vector3())
    {
        var moverPos = (Vector3)MoveMath.GetAbsoluteFuturePosition(mover, atDateTime);
        double spd_m = mover.GetDataBlob<WarpAbilityDB>().MaxSpeed;
        return WarpMath.GetInterceptPosition_m(moverPos, spd_m, targetOrbit, atDateTime, offsetPosition);
    }

    /// <summary>
    /// Calculates a cartesian position for an intercept for a ship and a target's orbit using warp.
    /// High-eccentricity / long-period / nested orbits use an iterative solver — the classic
    /// period-sweep produces false positives (exits tens of thousands of AU away, paths through
    /// the sun) for Halley-class comets and moons.
    /// </summary>
    public static (Vector3 position, DateTime etiDateTime) GetInterceptPosition_m(
        Vector3 moverAbsolutePos,
        double speed,
        OrbitDB targetOrbit,
        DateTime atDateTime,
        Vector3 offsetPosition = new Vector3())
    {
        if (speed < 1e-9)
        {
            var now = OrbitMath.GetAbsolutePosition(targetOrbit, atDateTime) + offsetPosition;
            return (now, atDateTime);
        }

        if (IsNestedOrbit(targetOrbit))
            return GetNestedBodyIntercept(moverAbsolutePos, speed, targetOrbit, atDateTime, offsetPosition);

        // Nearly circular / short-period: classic period-sweep (OrbitTests.TestIntercept).
        // Halley-class false positives fail IsPlausible and fall back to iterative.
        var sweep = GetPeriodSweepIntercept(moverAbsolutePos, speed, targetOrbit, atDateTime, offsetPosition);
        if (!IsPlausibleIntercept(moverAbsolutePos, sweep.position, sweep.etiDateTime, targetOrbit, offsetPosition))
            return GetIterativeOrbitIntercept(moverAbsolutePos, speed, targetOrbit, atDateTime, offsetPosition);

        return sweep;
    }

    /// <summary>
    /// True when this orbit is around a body that itself orbits something (moon, etc.).
    /// Requires the parent's orbit to have a real SMA — empty OrbitDB shells on stars must not count.
    /// </summary>
    static bool IsNestedOrbit(OrbitDB targetOrbit)
        => targetOrbit.Parent is { IsValid: true } parent
           && parent.TryGetDataBlob<OrbitDB>(out var parentOrbit)
           && parentOrbit.Parent is { IsValid: true } grandParent
           && grandParent.Id != parent.Id
           && parentOrbit.SemiMajorAxis > 1;

    static Vector3 GetParentAbsolute(OrbitDB targetOrbit, DateTime when)
    {
        var parent = targetOrbit.Parent;
        if (parent is null || !parent.IsValid)
            return Vector3.Zero;

        if (parent.TryGetDataBlob<OrbitDB>(out var parentOrbit))
            return OrbitMath.GetAbsolutePosition(parentOrbit, when);

        if (parent.TryGetDataBlob<PositionDB>(out var parentPos))
            return parentPos.AbsolutePosition;

        return Vector3.Zero;
    }

    /// <summary>
    /// Short iterative intercept in the parent frame — keeps moons on a local solution.
    /// </summary>
    static (Vector3 position, DateTime etiDateTime) GetNestedBodyIntercept(
        Vector3 moverAbsolutePos,
        double speed,
        OrbitDB targetOrbit,
        DateTime atDateTime,
        Vector3 offsetPosition)
    {
        Vector3 parentNow = GetParentAbsolute(targetOrbit, atDateTime);
        Vector3 shipRel = moverAbsolutePos - parentNow;

        double t = (OrbitMath.GetPosition(targetOrbit, atDateTime) + offsetPosition - shipRel).Length() / speed;
        Vector3 exit = Vector3.Zero;
        for (int i = 0; i < 12; i++)
        {
            DateTime when = atDateTime + TimeSpan.FromSeconds(t);
            exit = GetParentAbsolute(targetOrbit, when)
                   + OrbitMath.GetPosition(targetOrbit, when)
                   + offsetPosition;
            double tNew = (exit - moverAbsolutePos).Length() / speed;
            if (Math.Abs(tNew - t) < 0.5)
            {
                t = tNew;
                break;
            }
            t = tNew;
        }

        DateTime meet = atDateTime + TimeSpan.FromSeconds(t);
        return (exit, meet);
    }

    /// <summary>
    /// Short iterative intercept — seed with current range / speed, converge meeting time.
    /// Works for planets and highly eccentric comets without period-sweep traps.
    /// </summary>
    static (Vector3 position, DateTime etiDateTime) GetIterativeOrbitIntercept(
        Vector3 moverAbsolutePos,
        double speed,
        OrbitDB targetOrbit,
        DateTime atDateTime,
        Vector3 offsetPosition)
    {
        Vector3 seed = OrbitMath.GetAbsolutePosition(targetOrbit, atDateTime) + offsetPosition;
        double t = (seed - moverAbsolutePos).Length() / speed;
        Vector3 exit = seed;

        for (int i = 0; i < 16; i++)
        {
            DateTime when = atDateTime + TimeSpan.FromSeconds(t);
            exit = OrbitMath.GetAbsolutePosition(targetOrbit, when) + offsetPosition;
            double tNew = (exit - moverAbsolutePos).Length() / speed;
            if (Math.Abs(tNew - t) < 0.5)
            {
                t = tNew;
                break;
            }
            t = tNew;
        }

        double hopSeconds = (exit - moverAbsolutePos).Length() / speed;
        // Keep exit and ETI on the same sample — recomputing position at hopSeconds desyncs
        // slightly from the converged exit and breaks OrbitTests.TestIntercept equality.
        DateTime meet = atDateTime + TimeSpan.FromSeconds(t);
        exit = OrbitMath.GetAbsolutePosition(targetOrbit, meet) + offsetPosition;
        return (exit, meet);
    }

    static bool IsPlausibleIntercept(
        Vector3 moverAbsolutePos,
        Vector3 exit,
        DateTime eti,
        OrbitDB targetOrbit,
        Vector3 offsetPosition)
    {
        if (double.IsNaN(exit.X) || double.IsInfinity(exit.X))
            return false;

        Vector3 bodyAtEti = OrbitMath.GetAbsolutePosition(targetOrbit, eti) + offsetPosition;
        double miss = (exit - bodyAtEti).Length();
        // Exit must land near the body at the meeting time (Halley false positives miss by AU).
        const double MaxMissM = 7.5e10; // 0.5 AU
        if (miss > MaxMissM)
            return false;

        double hop = (exit - moverAbsolutePos).Length();
        // Absurd hops (Halley false positive was ~28,000 AU).
        const double MaxHopM = 1.5e14; // ~1000 AU
        if (hop > MaxHopM)
            return false;

        return true;
    }

    static (Vector3 position, DateTime etiDateTime) GetPeriodSweepIntercept(
        Vector3 moverAbsolutePos,
        double speed,
        OrbitDB targetOrbit,
        DateTime atDateTime,
        Vector3 offsetPosition)
    {
        var pos = moverAbsolutePos;
        double tim = 0;

        var pl = new Orbit()
        {
            position = moverAbsolutePos,
            T = targetOrbit.OrbitalPeriod.TotalSeconds,
        };

        Vector3 p;
        int i;
        double tt, t, dt, a0, a1, T;
        // find orbital position with min error (coarse)
        a1 = -1.0;
        dt = 0.01 * pl.T;


        for (t = 0; t < pl.T; t += dt)
        {
            p = OrbitMath.GetAbsolutePosition(targetOrbit, atDateTime + TimeSpan.FromSeconds(t));
            p += offsetPosition;
            tt = (p - pos).Length() / speed;
            a0 = tt - t; if (a0 < 0.0) continue;
            a0 /= pl.T;
            a0 -= Math.Floor(a0);
            a0 *= pl.T;
            if ((a0 < a1) || (a1 < 0.0))
            {
                a1 = a0;
                tim = tt;
            }
        }
        // find orbital position with min error (fine)
        for (i = 0; i < 10; i++)
            for (a1 = -1.0, t = tim - dt, T = tim + dt, dt *= 0.1; t < T; t += dt)
            {
                p = OrbitMath.GetAbsolutePosition(targetOrbit, atDateTime + TimeSpan.FromSeconds(t));
                p += offsetPosition;
                tt = (p - pos).Length() / speed;
                a0 = tt - t; if (a0 < 0.0) continue;
                a0 /= pl.T;
                a0 -= Math.Floor(a0);
                a0 *= pl.T;
                if ((a0 < a1) || (a1 < 0.0))
                {
                    a1 = a0;
                    tim = tt;
                }
            }

        p = OrbitMath.GetAbsolutePosition(targetOrbit, atDateTime + TimeSpan.FromSeconds(tim));
        p += offsetPosition;
        return (p, atDateTime + TimeSpan.FromSeconds(tim));
    }

}
