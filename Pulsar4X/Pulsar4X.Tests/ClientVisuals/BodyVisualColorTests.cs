using System;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Client.BodyVisuals;

namespace Pulsar4X.Tests.ClientVisuals;

/// <summary>
/// Runtime checks for Sol body colors — catches albedo grey-wash regressions (Mars≠Luna).
/// </summary>
[TestFixture]
public class BodyVisualColorTests
{
    [Test]
    public void Mars_Preset_IsReddish_NotGrey()
    {
        Assert.That(SolBodyPresets.TryGet("Mars", out var mars), Is.True);
        Assert.That(mars.Primary.R, Is.GreaterThan(mars.Primary.G));
        Assert.That(mars.Primary.R, Is.GreaterThan(mars.Primary.B));
        Assert.That(IsMostlyGrey(mars.Primary), Is.False, "Mars must not be grey");
    }

    [Test]
    public void Luna_Preset_IsGrey()
    {
        Assert.That(SolBodyPresets.TryGet("Luna", out var luna), Is.True);
        Assert.That(IsMostlyGrey(luna.Primary), Is.True);
    }

    [Test]
    public void Earth_Preset_HasBlueOceanPrimary()
    {
        Assert.That(SolBodyPresets.TryGet("Earth", out var earth), Is.True);
        Assert.That(earth.Primary.B, Is.GreaterThan(earth.Primary.R));
        Assert.That(earth.Water, Is.GreaterThanOrEqualTo(60));
    }

    [Test]
    public void ApplyRockySurface_KeepsMarsRed_WhenSolHintPresent()
    {
        Assert.That(SolBodyPresets.TryGet("Mars", out var hint), Is.True);
        var primary = BodyRgb.FromHex("#ffffff");
        var secondary = BodyRgb.FromHex("#ffffff");

        // Albedo 0.25 would previously grey-wash Mars.
        BodyVisualStateFactory.ApplyRockySurfaceColors(
            hint,
            waterDominant: false,
            lavaDominant: false,
            iceDominant: false,
            BodyVisualType.Terrestrial,
            albedo: 0.25,
            dustySurface: false,
            ref primary,
            ref secondary);

        Assert.That(primary.R, Is.EqualTo(hint.Primary.R));
        Assert.That(primary.G, Is.EqualTo(hint.Primary.G));
        Assert.That(primary.B, Is.EqualTo(hint.Primary.B));
        Assert.That(IsMostlyGrey(primary), Is.False);
    }

    [Test]
    public void ApplyRockySurface_UnknownRock_UsesAlbedoGrey()
    {
        var primary = BodyRgb.FromHex("#ff0000");
        var secondary = BodyRgb.FromHex("#00ff00");
        BodyVisualStateFactory.ApplyRockySurfaceColors(
            solHint: null,
            waterDominant: false,
            lavaDominant: false,
            iceDominant: false,
            BodyVisualType.Moon,
            albedo: 0.12,
            dustySurface: false,
            ref primary,
            ref secondary);

        Assert.That(IsMostlyGrey(primary), Is.True);
    }

    [Test]
    public void ApplyRockySurface_Dusty_IsRustRed()
    {
        var primary = BodyRgb.FromHex("#808080");
        var secondary = BodyRgb.FromHex("#808080");
        BodyVisualStateFactory.ApplyRockySurfaceColors(
            solHint: null,
            waterDominant: false,
            lavaDominant: false,
            iceDominant: false,
            BodyVisualType.Terrestrial,
            albedo: 0.25,
            dustySurface: true,
            ref primary,
            ref secondary);

        Assert.That(primary.R, Is.GreaterThan(primary.B));
        Assert.That(IsMostlyGrey(primary), Is.False);
    }

    [Test]
    public void Venus_Io_Europa_Titan_Presets_Exist_AndDiffer()
    {
        Assert.That(SolBodyPresets.TryGet("Venus", out var venus), Is.True);
        Assert.That(SolBodyPresets.TryGet("Io", out var io), Is.True);
        Assert.That(SolBodyPresets.TryGet("Europa", out var europa), Is.True);
        Assert.That(SolBodyPresets.TryGet("Titan", out var titan), Is.True);

        Assert.That(venus.Type, Is.EqualTo(BodyVisualType.Toxic));
        Assert.That(io.Type, Is.EqualTo(BodyVisualType.Lava));
        Assert.That(europa.Type, Is.EqualTo(BodyVisualType.Ice));
        Assert.That(titan.Primary.R, Is.GreaterThan(titan.Primary.B));
    }

    [Test]
    public void Mercury_Preset_RequestsGlow()
    {
        Assert.That(SolBodyPresets.TryGet("Mercury", out var mercury), Is.True);
        Assert.That(mercury.Glow, Is.True);
        Assert.That(mercury.GlowColor.R, Is.GreaterThan(mercury.GlowColor.B));
    }

    [Test]
    public void ThermalSurfaceTint_HeatIsOrangeTowardYellow_ColdIsBlueTowardWhite()
    {
        var hot = BodyRgb.FromHex("#ff3c00").Mix(BodyRgb.FromHex("#ffe14a"), 0.7);
        Assert.That(hot.R, Is.GreaterThan(hot.B));
        Assert.That(hot.G, Is.GreaterThan(80));

        var cold = BodyRgb.FromHex("#5a9fff").Mix(BodyRgb.FromHex("#f4fbff"), 0.7);
        Assert.That(cold.B, Is.GreaterThan(cold.R));
    }

    [Test]
    public void TypeDefault_UsesSharedCacheKey_NotBodyName()
    {
        // Unsurveyed fog-of-war sprites must not encode a Sol name (else Earth/Mars leak identity).
        var terrestrial = BodyVisualStateFactory.TypeDefault(BodyKind.Planet, 0);
        var another = BodyVisualStateFactory.TypeDefault(BodyKind.Planet, 0);
        Assert.That(terrestrial.CacheKey, Does.StartWith("unsurveyed:"));
        Assert.That(terrestrial.CacheKey, Is.EqualTo(another.CacheKey));
        Assert.That(terrestrial.CacheKey, Does.Not.Contain("Mercury"));
        Assert.That(terrestrial.CacheKey, Does.Not.Contain("Earth"));
        Assert.That(terrestrial.Water, Is.EqualTo(0));
        Assert.That(terrestrial.Clouds, Is.EqualTo(0));
        Assert.That(terrestrial.Craters, Is.EqualTo(0));
        Assert.That(IsMostlyGrey(terrestrial.Primary), Is.True, "Unsurveyed default must look grey");
    }

    private static bool IsMostlyGrey(BodyRgb c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max - min <= 18;
    }
}
