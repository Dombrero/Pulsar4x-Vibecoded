using Pulsar4X.Client.ShipVisuals;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>Hand-tuned looks for well-known Sol bodies (names from scenario JSON).</summary>
public static class SolBodyPresets
{
    public static bool TryGet(string? name, out BodyVisualState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string key = Normalize(name);
        if (!_presets.TryGetValue(key, out var preset))
            return false;

        state = CloneWithKey(preset, "sol:" + key);
        return true;
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        // "Sol A G0V" / "Sol A" → sol
        if (name.StartsWith("Sol", System.StringComparison.OrdinalIgnoreCase)
            && (name.Length == 3 || name[3] == ' ' || name[3] == 'A' || name[3] == 'a'))
            return "sol";
        if (name.Equals("Luna", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("Moon", System.StringComparison.OrdinalIgnoreCase))
            return "luna";
        return name.ToLowerInvariant();
    }

    private static BodyVisualState CloneWithKey(BodyVisualState s, string cacheKey) => new()
    {
        Type = s.Type,
        Seed = s.Seed,
        Size = s.Size,
        Rotation = s.Rotation,
        Light = s.Light,
        Water = s.Water,
        Clouds = s.Clouds,
        Atmo = s.Atmo,
        Craters = s.Craters,
        Rings = s.Rings,
        Anomalies = s.Anomalies,
        Variance = s.Variance,
        Primary = s.Primary,
        Secondary = s.Secondary,
        AtmoColor = s.AtmoColor,
        GlowColor = s.GlowColor,
        Shadow = s.Shadow,
        Glow = s.Glow,
        ThermalGlow = s.ThermalGlow,
        ExtremeHeatRing = s.ExtremeHeatRing || s.Type == BodyVisualType.Star,
        CacheKey = cacheKey
    };

    private static readonly System.Collections.Generic.Dictionary<string, BodyVisualState> _presets =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["earth"] = new BodyVisualState
            {
                Type = BodyVisualType.Terrestrial,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:earth")),
                Size = 100,
                Light = -25,
                Water = 71,
                Clouds = 55,
                Atmo = 80,
                Craters = 4,
                Variance = 12,
                Primary = BodyRgb.FromHex("#1a5f9e"),   // deep ocean
                Secondary = BodyRgb.FromHex("#3d8c4a"), // land / vegetation
                AtmoColor = BodyRgb.FromHex("#7ec8ff"),
                GlowColor = BodyRgb.FromHex("#cfefff"),
                Shadow = true,
                Glow = false
            },
            ["mars"] = new BodyVisualState
            {
                Type = BodyVisualType.Terrestrial,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:mars")),
                Size = 78,
                Light = -15,
                Water = 0,
                Clouds = 8,
                Atmo = 12,
                Craters = 48,
                Variance = 22,
                Primary = BodyRgb.FromHex("#a84a2f"),
                Secondary = BodyRgb.FromHex("#d4a07a"),
                AtmoColor = BodyRgb.FromHex("#e8c4a8"),
                GlowColor = BodyRgb.FromHex("#ffd0a0"),
                Shadow = true
            },
            ["venus"] = new BodyVisualState
            {
                Type = BodyVisualType.Toxic,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:venus")),
                Size = 96,
                Light = -10,
                Water = 0,
                Clouds = 92,
                Atmo = 95,
                Craters = 2,
                Variance = 10,
                Primary = BodyRgb.FromHex("#c4a35a"),
                Secondary = BodyRgb.FromHex("#e8d49a"),
                AtmoColor = BodyRgb.FromHex("#f0e0b0"),
                GlowColor = BodyRgb.FromHex("#ffcc66"),
                Shadow = true,
                Glow = true
            },
            ["mercury"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:mercury")),
                Size = 55,
                Light = -5,
                Water = 0,
                Clouds = 0,
                Atmo = 0,
                Craters = 85,
                Variance = 25,
                Primary = BodyRgb.FromHex("#6e6a66"),
                Secondary = BodyRgb.FromHex("#b0aaa4"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ff6a18"),
                Shadow = true,
                Glow = true
            },
            ["luna"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:luna")),
                Size = 48,
                Light = -30,
                Water = 0,
                Clouds = 0,
                Atmo = 0,
                Craters = 90,
                Variance = 18,
                Primary = BodyRgb.FromHex("#9a9a9e"),
                Secondary = BodyRgb.FromHex("#d0d0d4"),
                AtmoColor = BodyRgb.FromHex("#bbb"),
                GlowColor = BodyRgb.FromHex("#eee"),
                Shadow = true
            },
            ["io"] = new BodyVisualState
            {
                Type = BodyVisualType.Lava,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:io")),
                Size = 52,
                Water = 0,
                Clouds = 8,
                Atmo = 10,
                Craters = 20,
                Anomalies = 40,
                Variance = 20,
                Primary = BodyRgb.FromHex("#c4a030"),
                Secondary = BodyRgb.FromHex("#e8d060"),
                AtmoColor = BodyRgb.FromHex("#ffcc66"),
                GlowColor = BodyRgb.FromHex("#ff8844"),
                Shadow = true,
                Glow = true
            },
            ["europa"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:europa")),
                Size = 50,
                Water = 70,
                Clouds = 5,
                Atmo = 5,
                Craters = 25,
                Variance = 12,
                Primary = BodyRgb.FromHex("#c8d8e8"),
                Secondary = BodyRgb.FromHex("#f0f6ff"),
                AtmoColor = BodyRgb.FromHex("#d0e8f8"),
                GlowColor = BodyRgb.FromHex("#eef6ff"),
                Shadow = true
            },
            ["ganymede"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:ganymede")),
                Size = 62,
                Water = 45,
                Clouds = 5,
                Atmo = 5,
                Craters = 40,
                Variance = 16,
                Primary = BodyRgb.FromHex("#8a9098"),
                Secondary = BodyRgb.FromHex("#c4c8d0"),
                AtmoColor = BodyRgb.FromHex("#aab0b8"),
                GlowColor = BodyRgb.FromHex("#ddd"),
                Shadow = true
            },
            ["callisto"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:callisto")),
                Size = 58,
                Water = 20,
                Clouds = 0,
                Atmo = 2,
                Craters = 85,
                Variance = 18,
                Primary = BodyRgb.FromHex("#5a5854"),
                Secondary = BodyRgb.FromHex("#9a9488"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["titan"] = new BodyVisualState
            {
                Type = BodyVisualType.Toxic,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:titan")),
                Size = 64,
                Water = 15,
                Clouds = 85,
                Atmo = 90,
                Craters = 5,
                Variance = 10,
                Primary = BodyRgb.FromHex("#c48a40"),
                Secondary = BodyRgb.FromHex("#e0b060"),
                AtmoColor = BodyRgb.FromHex("#f0c878"),
                GlowColor = BodyRgb.FromHex("#ffd090"),
                Shadow = true,
                Glow = true
            },
            ["ceres"] = new BodyVisualState
            {
                Type = BodyVisualType.Asteroid,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:ceres")),
                Size = 42,
                Water = 25,
                Clouds = 0,
                Atmo = 0,
                Craters = 70,
                Variance = 20,
                Primary = BodyRgb.FromHex("#6a6864"),
                Secondary = BodyRgb.FromHex("#a8a49c"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["jupiter"] = new BodyVisualState
            {
                Type = BodyVisualType.Gas,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:jupiter")),
                Size = 145,
                Light = -20,
                Water = 0,
                Clouds = 20,
                Atmo = 90,
                Craters = 0,
                Rings = 8,
                Variance = 15,
                Primary = BodyRgb.FromHex("#c4a070"),
                Secondary = BodyRgb.FromHex("#8b5a3c"),
                AtmoColor = BodyRgb.FromHex("#e8c9a0"),
                GlowColor = BodyRgb.FromHex("#ffe0b0"),
                Shadow = true
            },
            ["saturn"] = new BodyVisualState
            {
                Type = BodyVisualType.Gas,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:saturn")),
                Size = 130,
                Light = -18,
                Water = 0,
                Clouds = 15,
                Atmo = 85,
                Craters = 0,
                Rings = 88,
                Rotation = 18,
                Variance = 12,
                Primary = BodyRgb.FromHex("#e6d5a8"),
                Secondary = BodyRgb.FromHex("#c4b080"),
                AtmoColor = BodyRgb.FromHex("#f5e8c8"),
                GlowColor = BodyRgb.FromHex("#fff5d0"),
                Shadow = true
            },
            ["uranus"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:uranus")),
                Size = 110,
                Light = -22,
                Water = 40,
                Clouds = 20,
                Atmo = 70,
                Craters = 0,
                Rings = 35,
                Rotation = 80,
                Variance = 8,
                Primary = BodyRgb.FromHex("#7ec8d4"),
                Secondary = BodyRgb.FromHex("#b8e8f0"),
                AtmoColor = BodyRgb.FromHex("#a0e0f0"),
                GlowColor = BodyRgb.FromHex("#d0f8ff"),
                Shadow = true
            },
            ["neptune"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:neptune")),
                Size = 108,
                Light = -22,
                Water = 50,
                Clouds = 25,
                Atmo = 75,
                Craters = 0,
                Rings = 15,
                Variance = 10,
                Primary = BodyRgb.FromHex("#2f5db0"),
                Secondary = BodyRgb.FromHex("#5a8fd4"),
                AtmoColor = BodyRgb.FromHex("#6aa0e8"),
                GlowColor = BodyRgb.FromHex("#90c0ff"),
                Shadow = true
            },
            ["sol"] = new BodyVisualState
            {
                Type = BodyVisualType.Star,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:star")),
                Size = 130,
                Primary = BodyRgb.FromHex("#fff2a0"),
                Secondary = BodyRgb.FromHex("#ffcc44"),
                AtmoColor = BodyRgb.FromHex("#ffb020"),
                GlowColor = BodyRgb.FromHex("#fff8d0"),
                Atmo = 100,
                Glow = true,
                Shadow = false,
                ExtremeHeatRing = true
            },
        };
}
