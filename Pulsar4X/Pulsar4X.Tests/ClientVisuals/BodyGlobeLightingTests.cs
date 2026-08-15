using System;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Client.BodyVisuals;

namespace Pulsar4X.Tests.ClientVisuals;

[TestFixture]
public class BodyGlobeLightingTests
{
    [Test]
    public void SunDirection_IsTowardTheStar()
    {
        var dir = BodyGlobeDrawer.LightDir.FromScreen(bodyX: 100, bodyY: 100, starX: 200, starY: 100);
        Assert.That(dir.X, Is.GreaterThan(0.9f));
        Assert.That(Math.Abs(dir.Y), Is.LessThan(0.1f));
    }

    [Test]
    public void SunDirection_OppositeStar_IsNightward()
    {
        var dir = BodyGlobeDrawer.LightDir.FromScreen(bodyX: 100, bodyY: 100, starX: 0, starY: 100);
        Assert.That(dir.X, Is.LessThan(-0.9f));
    }

    [Test]
    public void DaysideHeat_GlowsOnSunlitFace_NotNight()
    {
        Assert.That(SolBodyPresets.TryGet("Mercury", out var mercury), Is.True);
        var noon = BodyGlobeDrawer.DaysideHeatTint(mercury!, lambert: 1f);
        var night = BodyGlobeDrawer.DaysideHeatTint(mercury!, lambert: 0f);
        var terminator = BodyGlobeDrawer.DaysideHeatTint(mercury!, lambert: 0.25f);
        Assert.That(noon.r, Is.GreaterThan(0.08f));
        Assert.That(noon.r, Is.GreaterThan(noon.b));
        Assert.That(night.r, Is.EqualTo(0f).Within(0.001f));
        Assert.That(terminator.r, Is.LessThan(noon.r * 0.2f));

        Assert.That(SolBodyPresets.TryGet("Venus", out var venus), Is.True);
        var venusDay = BodyGlobeDrawer.DaysideHeatTint(venus!, lambert: 1f);
        Assert.That(venusDay.r, Is.EqualTo(0f), "thick clouds should not look molten");

        Assert.That(SolBodyPresets.TryGet("Earth", out var earth), Is.True);
        var earthDay = BodyGlobeDrawer.DaysideHeatTint(earth!, lambert: 1f);
        Assert.That(earthDay.r, Is.EqualTo(0f));
    }

    [Test]
    public void Unsurveyed_DoesNotSpin()
    {
        var t0 = new DateTime(2050, 1, 1);
        var t1 = t0.AddHours(12);
        var day = TimeSpan.FromHours(24);
        double a = BodyGlobeDrawer.SurfaceSpinRadians(t0, day, seed: 7, allowSpin: false);
        double b = BodyGlobeDrawer.SurfaceSpinRadians(t1, day, seed: 7, allowSpin: false);
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void EarthDay_TwelveHours_IsHalfTurn()
    {
        var t0 = new DateTime(2050, 1, 1);
        var t1 = t0.AddHours(12);
        var day = TimeSpan.FromHours(24);
        double a = BodyGlobeDrawer.SurfaceSpinRadians(t0, day, seed: 0, allowSpin: true);
        double b = BodyGlobeDrawer.SurfaceSpinRadians(t1, day, seed: 0, allowSpin: true);
        Assert.That(b - a, Is.EqualTo(Math.PI).Within(1e-6));
    }

    [Test]
    public void Clouds_SpinFasterThanSurface()
    {
        double surface = 1.0;
        Assert.That(BodyGlobeDrawer.CloudSpinRadians(surface), Is.GreaterThan(surface));
    }

    [Test]
    public void LiveGlobe_PlanetsYes_StarsAsteroidsNo()
    {
        Assert.That(BodyVisualComposer.UsesLiveGlobe(new BodyVisualState { Type = BodyVisualType.Terrestrial }), Is.True);
        Assert.That(BodyVisualComposer.UsesLiveGlobe(new BodyVisualState { Type = BodyVisualType.Gas }), Is.True);
        Assert.That(BodyVisualComposer.UsesLiveGlobe(new BodyVisualState { Type = BodyVisualType.Star }), Is.False);
        Assert.That(BodyVisualComposer.UsesLiveGlobe(new BodyVisualState { Type = BodyVisualType.Asteroid }), Is.False);
        Assert.That(BodyVisualComposer.UsesLiveGlobe(new BodyVisualState { Type = BodyVisualType.Comet }), Is.False);
    }

    [Test]
    public void AlbedoMap_IsOpaqueEquirect()
    {
        var composer = new BodyVisualComposer();
        var img = composer.ComposeAlbedoMap(new BodyVisualState
        {
            Type = BodyVisualType.Moon,
            Seed = 1,
            Primary = new BodyRgb(120, 120, 125),
            Secondary = new BodyRgb(90, 90, 95),
            CacheKey = "test:moon"
        });
        Assert.That(img.Width, Is.EqualTo(BodyVisualComposer.EquirectWidth));
        Assert.That(img.Height, Is.EqualTo(BodyVisualComposer.EquirectHeight));
        var px = img.GetPixel(img.Width / 2, img.Height / 2);
        Assert.That(px.a, Is.EqualTo(255));
    }

    [Test]
    public void TypeDefault_IsLiveGlobe_WithoutClouds()
    {
        var grey = BodyVisualStateFactory.TypeDefault(BodyKind.Planet, 0);
        Assert.That(BodyVisualComposer.UsesLiveGlobe(grey), Is.True);
        Assert.That(BodyVisualComposer.UsesCloudLayer(grey), Is.False);
    }

    [Test]
    public void RingBand_IsFlattenedDisk_NotFaceOnCircle()
    {
        var (rx, ry) = BodyGlobeDrawer.RingBandRadii(planetRadius: 100, band: 0);
        Assert.That(ry / rx, Is.LessThan(0.30), "rings must stay isometric, not orbital face-on circles");
        Assert.That(rx, Is.GreaterThan(100), "rings sit outside the planet disk");
    }

    [Test]
    public void TypeDefault_Asteroid_IsNotAGlobe()
    {
        var rock = BodyVisualStateFactory.TypeDefault(BodyKind.Asteroid, 7);
        Assert.That(rock.Type, Is.EqualTo(BodyVisualType.Asteroid));
        Assert.That(BodyVisualComposer.UsesLiveGlobe(rock), Is.False);
        Assert.That(rock.CacheKey, Does.Contain("rock"));
    }

    [Test]
    public void TypeDefault_DwarfPlanet_IsAGlobe()
    {
        var dwarf = BodyVisualStateFactory.TypeDefault(BodyKind.DwarfPlanet, 4);
        Assert.That(dwarf.Type, Is.Not.EqualTo(BodyVisualType.Asteroid));
        Assert.That(BodyVisualComposer.UsesLiveGlobe(dwarf, BodyKind.DwarfPlanet), Is.True);
    }

    [Test]
    public void Ceres_Preset_IsRoundDwarfPlanet_NotAsteroid()
    {
        Assert.That(SolBodyPresets.TryGet("Ceres", out var ceres), Is.True);
        Assert.That(ceres!.Type, Is.EqualTo(BodyVisualType.Moon));
        Assert.That(BodyVisualComposer.UsesLiveGlobe(ceres, BodyKind.DwarfPlanet), Is.True);
        Assert.That(ceres.Craters, Is.GreaterThan(40));
    }

    [Test]
    public void Moons_NeverGetRings()
    {
        Assert.That(BodyVisualStateFactory.AssignRings(BodyKind.Moon, 6, solHintRings: 88, seedHint: 1), Is.EqualTo(0));
        Assert.That(BodyVisualStateFactory.AssignRings(BodyKind.DwarfPlanet, 4, null, 1), Is.EqualTo(0));
        Assert.That(SolBodyPresets.TryGet("Titan", out var titan), Is.True);
        Assert.That(titan!.Rings, Is.EqualTo(0));
        Assert.That(SolBodyPresets.TryGet("Enceladus", out var enc), Is.True);
        Assert.That(enc!.Rings, Is.EqualTo(0));
        Assert.That(enc.Type, Is.EqualTo(BodyVisualType.Ice));
    }

    [Test]
    public void Saturn_HasRings_JupiterDoesNot()
    {
        Assert.That(SolBodyPresets.TryGet("Saturn", out var saturn), Is.True);
        Assert.That(saturn!.Rings, Is.GreaterThan(50));
        Assert.That(SolBodyPresets.TryGet("Jupiter", out var jupiter), Is.True);
        Assert.That(jupiter!.Rings, Is.EqualTo(0));
        Assert.That(BodyVisualStateFactory.AssignRings(BodyKind.Planet, 2, jupiter.Rings, 9), Is.EqualTo(0));
        Assert.That(BodyVisualStateFactory.AssignRings(BodyKind.Planet, 2, saturn.Rings, 9), Is.EqualTo(saturn.Rings));
    }

    [Test]
    public void RingFarHalf_IsLocalEllipseY_NotScreenY()
    {
        Assert.That(BodyGlobeDrawer.RingSegmentIsBehind(Math.PI * 1.25, Math.PI * 1.5), Is.True);
        Assert.That(BodyGlobeDrawer.RingSegmentIsBehind(Math.PI * 0.25, Math.PI * 0.5), Is.False);
    }

    [Test]
    public void SolMoonsAndDwarfs_HaveAuthoredLooks()
    {
        Assert.That(SolBodyPresets.TryGet("Mimas", out _), Is.True);
        Assert.That(SolBodyPresets.TryGet("Pluto", out var pluto), Is.True);
        Assert.That(pluto!.Type, Is.EqualTo(BodyVisualType.Ice));
        Assert.That(BodyVisualComposer.UsesLiveGlobe(pluto, BodyKind.DwarfPlanet), Is.True);
    }

    [Test]
    public void AsteroidSprite_IsNotAFilledCircle()
    {
        var composer = new BodyVisualComposer();
        var img = composer.Compose(new BodyVisualState
        {
            Type = BodyVisualType.Asteroid,
            Seed = 42,
            Size = 80,
            Primary = new BodyRgb(110, 100, 90),
            Secondary = new BodyRgb(70, 65, 60),
            CacheKey = "test:asteroid"
        });

        int opaque = 0;
        int cx = img.Width / 2;
        int cy = img.Height / 2;
        int rad = img.Width / 5;
        int insideCircle = 0;
        int circleOpaque = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                if (img.GetPixel(x, y).a < 128)
                    continue;
                opaque++;
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy <= rad * rad)
                {
                    insideCircle++;
                    circleOpaque++;
                }
            }

        Assert.That(opaque, Is.GreaterThan(200), "asteroid should have a visible body");
        // A round mini-planet would fill the test circle almost solidly; a potato leaves gaps.
        double fill = circleOpaque / (Math.PI * rad * rad);
        Assert.That(fill, Is.LessThan(0.92), "asteroid must not be a solid disk");
    }
}
