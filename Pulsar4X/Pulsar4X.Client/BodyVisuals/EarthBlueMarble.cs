using System;
using System.IO;
using Pulsar4X.Client.ShipVisuals;
using StbImageSharp;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>
/// NASA Visible Earth Blue Marble maps (public domain): land/shallow-water/shaded-topo
/// plus the combined cloud layer. Sampled as unlit equirectangular albedo.
/// </summary>
public static class EarthBlueMarble
{
    private static readonly object Gate = new();
    private static RgbaImage? _albedo;
    private static RgbaImage? _clouds;
    private static bool _tried;

    public static bool MapsLoaded
    {
        get
        {
            Ensure();
            return _albedo != null && _clouds != null;
        }
    }

    public static bool TrySampleAlbedo(double latDeg, double lonDeg, out BodyRgb rgb)
    {
        Ensure();
        rgb = default;
        if (_albedo is null)
            return false;
        SampleBilinear(_albedo, latDeg, lonDeg, out byte r, out byte g, out byte b, out _);
        rgb = GradeAlbedo(new BodyRgb(r, g, b));
        return true;
    }

    public static bool TrySampleCloud(double latDeg, double lonDeg, out double density)
    {
        Ensure();
        density = 0;
        if (_clouds is null)
            return false;
        SampleBilinear(_clouds, latDeg, lonDeg, out byte r, out byte g, out byte b, out _);
        double lum = (0.30 * r + 0.59 * g + 0.11 * b) / 255.0;
        density = Smoothstep(0.06, 0.88, lum);
        return true;
    }

    private static BodyRgb GradeAlbedo(BodyRgb c)
    {
        bool ocean = c.B > c.G + 4 && c.B > c.R + 4;
        if (ocean)
        {
            var deep = BodyRgb.FromHex("#041830");
            var teal = BodyRgb.FromHex("#2eb8c4");
            double shelf = Math.Clamp((c.G + 18 - c.R) / 90.0, 0, 1);
            return c.Mix(deep, 0.40).Mix(teal, shelf * 0.38);
        }

        return new BodyRgb(
            (byte)Math.Clamp(c.R * 1.04 + 4, 0, 255),
            (byte)Math.Clamp(c.G * 1.16 + 6, 0, 255),
            (byte)Math.Clamp(c.B * 0.90, 0, 255));
    }

    private static void SampleBilinear(
        RgbaImage img, double latDeg, double lonDeg,
        out byte r, out byte g, out byte b, out byte a)
    {
        double u = (lonDeg + 180.0) / 360.0;
        u -= Math.Floor(u);
        double v = Math.Clamp(0.5 - latDeg / 180.0, 0, 1 - 1e-6);
        double x = u * img.Width - 0.5;
        double y = v * img.Height - 0.5;
        int x0 = Mod((int)Math.Floor(x), img.Width);
        int y0 = Math.Clamp((int)Math.Floor(y), 0, img.Height - 1);
        int x1 = (x0 + 1) % img.Width;
        int y1 = Math.Min(y0 + 1, img.Height - 1);
        double fx = x - Math.Floor(x);
        double fy = y - Math.Floor(y);
        var p00 = img.GetPixel(x0, y0);
        var p10 = img.GetPixel(x1, y0);
        var p01 = img.GetPixel(x0, y1);
        var p11 = img.GetPixel(x1, y1);
        r = Lerp4(p00.r, p10.r, p01.r, p11.r, fx, fy);
        g = Lerp4(p00.g, p10.g, p01.g, p11.g, fx, fy);
        b = Lerp4(p00.b, p10.b, p01.b, p11.b, fx, fy);
        a = Lerp4(p00.a, p10.a, p01.a, p11.a, fx, fy);
    }

    private static byte Lerp4(byte a, byte b, byte c, byte d, double fx, double fy)
    {
        double ab = a + (b - a) * fx;
        double cd = c + (d - c) * fx;
        return (byte)Math.Clamp(Math.Round(ab + (cd - ab) * fy), 0, 255);
    }

    private static int Mod(int x, int m)
    {
        int r = x % m;
        return r < 0 ? r + m : r;
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / Math.Max(1e-6, edge1 - edge0), 0, 1);
        return t * t * (3.0 - 2.0 * t);
    }

    private static void Ensure()
    {
        if (_tried)
            return;
        lock (Gate)
        {
            if (_tried)
                return;
            _albedo = Load("Pulsar4X.Client.EarthAlbedo");
            _clouds = Load("Pulsar4X.Client.EarthClouds");
            _tried = true;
        }
    }

    private static RgbaImage? Load(string name)
    {
        var asm = typeof(EarthBlueMarble).Assembly;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var img = ImageResult.FromMemory(ms.ToArray(), ColorComponents.RedGreenBlueAlpha);
        return new RgbaImage(img.Width, img.Height, img.Data);
    }
}
