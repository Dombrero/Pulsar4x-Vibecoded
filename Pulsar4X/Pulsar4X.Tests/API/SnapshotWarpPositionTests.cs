using System;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Client;
using Pulsar4X.Orbital;

namespace Pulsar4X.Tests;

/// <summary>Live map positions for warping ships must follow pushed PositionView (sim truth),
/// not client chord interpolation — Aurora-style display clock unity.</summary>
[TestFixture]
public class SnapshotWarpPositionTests
{
    [Test]
    public void AbsolutePositionM_for_warp_uses_PositionView_not_chord()
    {
        var t0 = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(2);
        var midTime = t0.AddHours(1);

        var entry = new Vec3(0, 0, 0);
        var exit = new Vec3(1_000_000, 0, 0);
        var pushed = new Vec3(123_000, 456_000, 0);

        var warp = new WarpMovingView(100)
        {
            EntryPointAbsolute = entry,
            ExitPointAbsolute = exit,
            ExitPointRelative = exit,
            EntryDateTime = t0,
            PredictedExitTime = t1,
        };

        var entity = new EntitySnapshot
        {
            Id = 1,
            Views = new IComponentView[]
            {
                new PositionView(pushed, pushed, ParentId: null),
                warp,
            },
        };

        // IClientSystem is unused on the warp path.
        var abs = entity.AbsolutePositionM(system: null!, midTime);
        Assert.That(abs.X, Is.EqualTo(pushed.X).Within(1e-6));
        Assert.That(abs.Y, Is.EqualTo(pushed.Y).Within(1e-6));

        // Preview helper still interpolates the chord for order UI.
        var chord = SnapshotOrbits.WarpPositionAlongChord(warp, midTime);
        Assert.That(chord.X, Is.EqualTo(500_000).Within(1.0));
        Assert.That(chord.X, Is.Not.EqualTo(abs.X).Within(1.0));
    }
}
