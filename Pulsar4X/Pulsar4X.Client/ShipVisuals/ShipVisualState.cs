using System;

namespace Pulsar4X.Client.ShipVisuals;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return new RgbColor(57, 120, 173);
        hex = hex.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        if (hex.Length < 6)
            return new RgbColor(57, 120, 173);
        int n = Convert.ToInt32(hex[..6], 16);
        return new RgbColor((byte)(n >> 16), (byte)((n >> 8) & 255), (byte)(n & 255));
    }

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}

public sealed class ShipVisualPalette
{
    public RgbColor Primary { get; init; } = RgbColor.FromHex("#3978ad");
    public RgbColor Accent { get; init; } = RgbColor.FromHex("#efb54a");
    public RgbColor Engine { get; init; } = RgbColor.FromHex("#62d9ff");
}

/// <summary>Parameters that drive Aurora-style ship composition.</summary>
public sealed class ShipVisualState
{
    public string Name { get; init; } = "Ship";
    public int Seed { get; init; }
    public int Size { get; init; } = 95;
    public int Spread { get; init; } = 52;
    public int Overlap { get; init; } = 38;
    public int Engines { get; init; } = 2;
    public int Weapons { get; init; }
    public int Sensors { get; init; }
    public int Cargo { get; init; }
    public int Armor { get; init; }
    public ShipVisualPalette Palette { get; init; } = new();
    public int AccentAmount { get; init; } = 28;
    public int ColorVariance { get; init; } = 12;
    public bool CoherentColors { get; init; } = true;

    /// <summary>Canonical string used for seeded RNG (mirrors JS JSON.stringify order of fields).</summary>
    public string CanonicalKey()
    {
        var p = Palette;
        return
            $"{{\"name\":\"{Name}\",\"seed\":{Seed},\"size\":{Size},\"spread\":{Spread},\"overlap\":{Overlap}," +
            $"\"engines\":{Engines},\"weapons\":{Weapons},\"sensors\":{Sensors},\"cargo\":{Cargo},\"armor\":{Armor}," +
            $"\"palette\":{{\"primary\":\"{p.Primary.ToHex()}\",\"accent\":\"{p.Accent.ToHex()}\",\"engine\":\"{p.Engine.ToHex()}\"}}," +
            $"\"accentAmount\":{AccentAmount},\"colorVariance\":{ColorVariance},\"coherentColors\":{(CoherentColors ? "true" : "false")}}}";
    }
}
