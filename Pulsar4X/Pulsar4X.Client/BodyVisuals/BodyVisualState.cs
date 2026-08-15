using System;

namespace Pulsar4X.Client.BodyVisuals;

public enum BodyVisualType : byte
{
    Terrestrial,
    Moon,
    Gas,
    Ice,
    Lava,
    Toxic,
    Asteroid,
    Comet,
    Star
}

public readonly record struct BodyRgb(byte R, byte G, byte B)
{
    public static BodyRgb FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return new BodyRgb(50, 100, 120);
        hex = hex.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length < 6)
            return new BodyRgb(50, 100, 120);
        int n = Convert.ToInt32(hex[..6], 16);
        return new BodyRgb((byte)(n >> 16), (byte)((n >> 8) & 255), (byte)(n & 255));
    }

    public BodyRgb Mix(BodyRgb other, double t)
    {
        t = System.Math.Clamp(t, 0, 1);
        return new BodyRgb(
            (byte)System.Math.Round(R + (other.R - R) * t),
            (byte)System.Math.Round(G + (other.G - G) * t),
            (byte)System.Math.Round(B + (other.B - B) * t));
    }

    public BodyRgb Shade(double f) => new(
        (byte)System.Math.Clamp((int)System.Math.Round(R * f), 0, 255),
        (byte)System.Math.Clamp((int)System.Math.Round(G * f), 0, 255),
        (byte)System.Math.Clamp((int)System.Math.Round(B * f), 0, 255));
}

public sealed class BodyVisualState
{
    public BodyVisualType Type { get; init; } = BodyVisualType.Terrestrial;
    public int Seed { get; init; }
    public int Size { get; init; } = 95;
    public int Rotation { get; init; }
    public int Light { get; init; } = -20;
    public int Water { get; init; } = 40;
    public int Clouds { get; init; } = 30;
    public int Atmo { get; init; } = 50;
    public int Craters { get; init; } = 20;
    public int Rings { get; init; }
    public int Anomalies { get; init; }
    public int Variance { get; init; } = 18;
    public BodyRgb Primary { get; init; } = BodyRgb.FromHex("#327da6");
    public BodyRgb Secondary { get; init; } = BodyRgb.FromHex("#74c6a4");
    public BodyRgb AtmoColor { get; init; } = BodyRgb.FromHex("#68d8ff");
    public BodyRgb GlowColor { get; init; } = BodyRgb.FromHex("#ffd54a");
    public bool Shadow { get; init; } = true;
    /// <summary>Legacy flag (stars/lava); surface heat/cold uses <see cref="ThermalGlow"/>.</summary>
    public bool Glow { get; init; } = true;

    /// <summary>
    /// Surface emission tint baked into the texture (not a halo).
    /// Positive = heat (orange→yellow), negative = cold (blue→white), magnitude 0–100.
    /// </summary>
    public int ThermalGlow { get; init; }

    /// <summary>
    /// Animated outer glow ring for extreme heat (stars / surface temp &gt; 2000 °C).
    /// Not used for normal hot rock like Mercury — that stays surface-only.
    /// </summary>
    public bool ExtremeHeatRing { get; init; }

    /// <summary>
    /// Polar flattening in percent (Saturn ~10). Squashes the live globe along screen Y.
    /// </summary>
    public int Flattening { get; init; }

    /// <summary>Cache key for shared unsurveyed defaults (type-only) or surveyed individuals.</summary>
    public string CacheKey { get; init; } = "default:terrestrial";
}
