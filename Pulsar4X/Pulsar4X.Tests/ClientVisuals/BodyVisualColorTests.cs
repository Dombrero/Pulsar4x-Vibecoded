using System;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Client.BodyVisuals;
using Pulsar4X.Client.ShipVisuals;

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
    public void Mercury_Preset_IsGreyRock_NotLavaGlow()
    {
        Assert.That(SolBodyPresets.TryGet("Mercury", out var mercury), Is.True);
        Assert.That(IsMostlyGrey(mercury.Primary), Is.True);
        Assert.That(mercury.Type, Is.Not.EqualTo(BodyVisualType.Lava));
        Assert.That(mercury.Glow, Is.False, "no outer halo — heat is dayside-only");
        Assert.That(mercury.ThermalGlow, Is.GreaterThan(0), "sunlit face should warm-glow");
        Assert.That(mercury.GlowColor.R, Is.GreaterThan(mercury.GlowColor.B));
    }

    [Test]
    public void EarthAlbedo_IsMostlyBlueOcean()
    {
        Assert.That(SolBodyPresets.TryGet("Earth", out var earth), Is.True);
        var img = new BodyVisualComposer().ComposeAlbedoMap(earth!);
        int ocean = 0, other = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                var px = img.GetPixel(x, y);
                if (px.b > px.g && px.b > px.r)
                    ocean++;
                else
                    other++;
            }
        Assert.That(ocean, Is.GreaterThan(other), "Earth must read as an ocean world");
    }

    [Test]
    public void EarthContinents_AmericasAreLand_AtlanticIsOcean()
    {
        var kansas = FromLatLon(40, -100);
        var atlantic = FromLatLon(0, -30);
        var andes = FromLatLon(-20, -70);
        Assert.That(BodyVisualComposer.EarthLandField(kansas.x, kansas.y, kansas.z), Is.GreaterThan(0.05));
        Assert.That(BodyVisualComposer.EarthLandField(andes.x, andes.y, andes.z), Is.GreaterThan(0.0));
        Assert.That(BodyVisualComposer.EarthLandField(atlantic.x, atlantic.y, atlantic.z), Is.LessThan(0));

        Assert.That(SolBodyPresets.TryGet("Earth", out var earth), Is.True);
        var img = new BodyVisualComposer().ComposeAlbedoMap(earth!);
        var landPx = SampleEquirect(img, 40, -100);
        var seaPx = SampleEquirect(img, 0, -30);
        Assert.That(landPx.g, Is.GreaterThan(landPx.b), "Great Plains should be vegetated land");
        Assert.That(seaPx.b, Is.GreaterThan(seaPx.r), "mid-Atlantic should be ocean");
        Assert.That(EarthBlueMarble.MapsLoaded, Is.True, "NASA Blue Marble maps must be embedded");
    }

    [Test]
    public void JupiterAlbedo_HasBands_UranusIsQuiet()
    {
        Assert.That(SolBodyPresets.TryGet("Jupiter", out var jupiter), Is.True);
        Assert.That(SolBodyPresets.TryGet("Uranus", out var uranus), Is.True);
        var composer = new BodyVisualComposer();
        var jImg = composer.ComposeAlbedoMap(jupiter!);
        var uImg = composer.ComposeAlbedoMap(uranus!);
        Assert.That(ColumnVariance(jImg, jImg.Width / 2), Is.GreaterThan(ColumnVariance(uImg, uImg.Width / 2) * 2));
        Assert.That(jupiter.Flattening, Is.GreaterThanOrEqualTo(6));
        Assert.That(uranus.Water, Is.EqualTo(0));
    }

    [Test]
    public void VenusAlbedo_IsCream_NotGreen()
    {
        Assert.That(SolBodyPresets.TryGet("Venus", out var venus), Is.True);
        var img = new BodyVisualComposer().ComposeAlbedoMap(venus!);
        var px = img.GetPixel(img.Width / 2, img.Height / 2);
        Assert.That(px.g, Is.LessThan(px.r + 12), "Venus clouds are cream, not toxic green");
        Assert.That(px.b, Is.LessThan(px.r));
    }

    [Test]
    public void Saturn_IsFlattened_AndHasRings()
    {
        Assert.That(SolBodyPresets.TryGet("Saturn", out var saturn), Is.True);
        Assert.That(saturn!.Flattening, Is.GreaterThanOrEqualTo(8));
        Assert.That(BodyGlobeDrawer.PolarSquash(saturn), Is.LessThan(0.95f));
        Assert.That(saturn.Rings, Is.GreaterThan(50));
        Assert.That(saturn.Rotation, Is.EqualTo(27));
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

    private static double ColumnVariance(RgbaImage img, int x)
    {
        double sum = 0, sum2 = 0;
        int n = img.Height;
        for (int y = 0; y < n; y++)
        {
            var p = img.GetPixel(x, y);
            double v = p.r * 0.3 + p.g * 0.5 + p.b * 0.2;
            sum += v;
            sum2 += v * v;
        }
        double mean = sum / n;
        return sum2 / n - mean * mean;
    }

    private static (double x, double y, double z) FromLatLon(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180.0;
        double lon = lonDeg * Math.PI / 180.0;
        double cl = Math.Cos(lat);
        return (cl * Math.Cos(lon), Math.Sin(lat), cl * Math.Sin(lon));
    }

    private static (byte r, byte g, byte b, byte a) SampleEquirect(RgbaImage img, double latDeg, double lonDeg)
    {
        double u = (lonDeg + 180.0) / 360.0;
        double v = 0.5 - latDeg / 180.0;
        int x = Math.Clamp((int)(u * img.Width), 0, img.Width - 1);
        int y = Math.Clamp((int)(v * img.Height), 0, img.Height - 1);
        return img.GetPixel(x, y);
    }
}
