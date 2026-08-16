using NUnit.Framework;
using Pulsar4X.Client.Rendering;

namespace Pulsar4X.Tests.ClientVisuals;

[TestFixture]
public class SystemStarfieldTests
{
    [Test]
    public void SameSystemId_ProducesSameSky()
    {
        var a = new SystemStarfield();
        var b = new SystemStarfield();
        a.Rebuild("sol-test-system");
        b.Rebuild("sol-test-system");

        Assert.That(a.Seed, Is.EqualTo(b.Seed));
        Assert.That(a.StarCount, Is.EqualTo(b.StarCount));
        Assert.That(a.NebulaCount, Is.EqualTo(b.NebulaCount));
        Assert.That(a.StarCount, Is.InRange(180, 250));
        Assert.That(a.NebulaCount, Is.InRange(3, 6));
        Assert.That(a.GetStarUv(0), Is.EqualTo(b.GetStarUv(0)));
        Assert.That(a.GetStarUv(a.StarCount / 2), Is.EqualTo(b.GetStarUv(b.StarCount / 2)));
    }

    [Test]
    public void DifferentSystemId_ProducesDifferentSky()
    {
        var a = new SystemStarfield();
        var b = new SystemStarfield();
        a.Rebuild("alpha-centauri");
        b.Rebuild("barnards-star");

        Assert.That(a.Seed, Is.Not.EqualTo(b.Seed));
        Assert.That(
            a.StarCount != b.StarCount
            || a.GetStarUv(0) != b.GetStarUv(0)
            || a.NebulaCount != b.NebulaCount,
            Is.True,
            "Different systems should not share an identical starfield");
    }

    [Test]
    public void Rebuild_BakesVisibleNebulaCoverage()
    {
        var sky = new SystemStarfield();
        sky.Rebuild("visibility-check");
        int total = 320 * 180;
        Assert.That(sky.NebulaOpaquePixelCount, Is.GreaterThan(total / 20),
            "Nebula bake must cover a visible fraction of the sky");
    }

    [Test]
    public void Rebuild_ReplacesPreviousSky()
    {
        var sky = new SystemStarfield();
        sky.Rebuild("first");
        var firstSeed = sky.Seed;
        var firstStar = sky.GetStarUv(0);
        sky.Rebuild("second");
        Assert.That(sky.SystemId, Is.EqualTo("second"));
        Assert.That(sky.Seed, Is.Not.EqualTo(firstSeed));
        Assert.That(sky.StarCount, Is.GreaterThan(0));
        // May coincidentally match UV; seed change is the hard guarantee.
        Assert.That(sky.GetStarUv(0).u, Is.InRange(0f, 1f));
        Assert.That(firstStar.u, Is.InRange(0f, 1f));
    }
}
