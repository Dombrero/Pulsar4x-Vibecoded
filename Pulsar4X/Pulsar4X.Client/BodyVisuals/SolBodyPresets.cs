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

        string cacheKey = key == "earth" ? "sol:earth:marble" : "sol:" + key;
        state = CloneWithKey(preset, cacheKey);
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
        Flattening = s.Flattening,
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
                Clouds = 72,
                Atmo = 96,
                Craters = 0,
                Variance = 14,
                Primary = BodyRgb.FromHex("#062a5c"),
                Secondary = BodyRgb.FromHex("#2aa336"),
                AtmoColor = BodyRgb.FromHex("#7ad4ff"),
                GlowColor = BodyRgb.FromHex("#ffe8a0"),
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
                Clouds = 6,
                Atmo = 10,
                Craters = 42,
                Variance = 22,
                Primary = BodyRgb.FromHex("#c24a22"),
                Secondary = BodyRgb.FromHex("#e2a070"),
                AtmoColor = BodyRgb.FromHex("#e8c4a8"),
                GlowColor = BodyRgb.FromHex("#fff2dc"),
                Shadow = true
            },
            ["venus"] = new BodyVisualState
            {
                Type = BodyVisualType.Toxic,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:venus")),
                Size = 96,
                Light = -10,
                Water = 0,
                Clouds = 96,
                Atmo = 95,
                Craters = 0,
                Variance = 8,
                Primary = BodyRgb.FromHex("#e4d09a"),
                Secondary = BodyRgb.FromHex("#f3ead0"),
                AtmoColor = BodyRgb.FromHex("#f0e0b8"),
                GlowColor = BodyRgb.FromHex("#fff3c8"),
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
                Craters = 88,
                Variance = 22,
                Primary = BodyRgb.FromHex("#5a5652"),
                Secondary = BodyRgb.FromHex("#9a948c"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ffb060"),
                Shadow = true,
                Glow = false,
                ThermalGlow = 52
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
            ["mimas"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:mimas")),
                Size = 36,
                Craters = 92,
                Variance = 16,
                Primary = BodyRgb.FromHex("#c8c4bc"),
                Secondary = BodyRgb.FromHex("#ece8e0"),
                AtmoColor = BodyRgb.FromHex("#bbb"),
                GlowColor = BodyRgb.FromHex("#eee"),
                Shadow = true
            },
            ["enceladus"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:enceladus")),
                Size = 38,
                Water = 80,
                Craters = 20,
                Variance = 8,
                Primary = BodyRgb.FromHex("#e8f2f6"),
                Secondary = BodyRgb.FromHex("#ffffff"),
                AtmoColor = BodyRgb.FromHex("#d8eef8"),
                GlowColor = BodyRgb.FromHex("#f4fcff"),
                Shadow = true
            },
            ["tethys"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:tethys")),
                Size = 42,
                Water = 55,
                Craters = 55,
                Variance = 12,
                Primary = BodyRgb.FromHex("#d4d8dc"),
                Secondary = BodyRgb.FromHex("#f2f4f6"),
                AtmoColor = BodyRgb.FromHex("#c8d0d6"),
                GlowColor = BodyRgb.FromHex("#eee"),
                Shadow = true
            },
            ["dione"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:dione")),
                Size = 44,
                Water = 40,
                Craters = 62,
                Variance = 14,
                Primary = BodyRgb.FromHex("#c0c4c8"),
                Secondary = BodyRgb.FromHex("#e8eaee"),
                AtmoColor = BodyRgb.FromHex("#b8c0c8"),
                GlowColor = BodyRgb.FromHex("#eee"),
                Shadow = true
            },
            ["rhea"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:rhea")),
                Size = 48,
                Water = 20,
                Craters = 80,
                Variance = 14,
                Primary = BodyRgb.FromHex("#b0aaa4"),
                Secondary = BodyRgb.FromHex("#dcd6ce"),
                AtmoColor = BodyRgb.FromHex("#bbb"),
                GlowColor = BodyRgb.FromHex("#eee"),
                Shadow = true
            },
            ["amalthea"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:amalthea")),
                Size = 32,
                Craters = 70,
                Variance = 22,
                Primary = BodyRgb.FromHex("#8a4030"),
                Secondary = BodyRgb.FromHex("#c07050"),
                AtmoColor = BodyRgb.FromHex("#a06040"),
                GlowColor = BodyRgb.FromHex("#e09060"),
                Shadow = true
            },
            ["himalia"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:himalia")),
                Size = 34,
                Craters = 60,
                Variance = 18,
                Primary = BodyRgb.FromHex("#6a5a48"),
                Secondary = BodyRgb.FromHex("#a09078"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["elara"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:elara")),
                Size = 30,
                Craters = 58,
                Variance = 16,
                Primary = BodyRgb.FromHex("#7a7068"),
                Secondary = BodyRgb.FromHex("#b0a8a0"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["lysithea"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:lysithea")),
                Size = 28,
                Craters = 55,
                Variance = 16,
                Primary = BodyRgb.FromHex("#6e6458"),
                Secondary = BodyRgb.FromHex("#a89c8c"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["pasiphae"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:pasiphae")),
                Size = 28,
                Craters = 52,
                Variance = 15,
                Primary = BodyRgb.FromHex("#5c5854"),
                Secondary = BodyRgb.FromHex("#948e86"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["sinope"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:sinope")),
                Size = 26,
                Craters = 50,
                Variance = 14,
                Primary = BodyRgb.FromHex("#585450"),
                Secondary = BodyRgb.FromHex("#8c8680"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#ccc"),
                Shadow = true
            },
            ["pluto"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:pluto")),
                Size = 58,
                Water = 35,
                Craters = 40,
                Anomalies = 24,
                Variance = 20,
                Primary = BodyRgb.FromHex("#c4a888"),
                Secondary = BodyRgb.FromHex("#f0e0c8"),
                AtmoColor = BodyRgb.FromHex("#d8c8b0"),
                GlowColor = BodyRgb.FromHex("#fff5e0"),
                Shadow = true
            },
            ["eris"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:eris")),
                Size = 56,
                Water = 50,
                Craters = 30,
                Variance = 10,
                Primary = BodyRgb.FromHex("#d8e0e8"),
                Secondary = BodyRgb.FromHex("#f6f8fc"),
                AtmoColor = BodyRgb.FromHex("#c8d4e0"),
                GlowColor = BodyRgb.FromHex("#fff"),
                Shadow = true
            },
            ["haumea"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:haumea")),
                Size = 52,
                Water = 45,
                Craters = 22,
                Variance = 12,
                Primary = BodyRgb.FromHex("#e0d8d0"),
                Secondary = BodyRgb.FromHex("#f8f4ee"),
                AtmoColor = BodyRgb.FromHex("#ddd"),
                GlowColor = BodyRgb.FromHex("#fff"),
                Shadow = true
            },
            ["makemake"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:makemake")),
                Size = 50,
                Craters = 35,
                Variance = 16,
                Primary = BodyRgb.FromHex("#c07040"),
                Secondary = BodyRgb.FromHex("#e0a070"),
                AtmoColor = BodyRgb.FromHex("#c88858"),
                GlowColor = BodyRgb.FromHex("#f0c090"),
                Shadow = true
            },
            ["ceres"] = new BodyVisualState
            {
                Type = BodyVisualType.Moon,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:ceres")),
                Size = 56,
                Water = 8,
                Clouds = 0,
                Atmo = 0,
                Craters = 78,
                Anomalies = 40,
                Variance = 16,
                Primary = BodyRgb.FromHex("#6a6864"),
                Secondary = BodyRgb.FromHex("#b8b0a4"),
                AtmoColor = BodyRgb.FromHex("#888"),
                GlowColor = BodyRgb.FromHex("#f4eee4"),
                Shadow = true
            },
            ["jupiter"] = new BodyVisualState
            {
                Type = BodyVisualType.Gas,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:jupiter")),
                Size = 145,
                Light = -20,
                Water = 0,
                Clouds = 0,
                Atmo = 88,
                Craters = 0,
                Rings = 0,
                Flattening = 7,
                Variance = 18,
                Primary = BodyRgb.FromHex("#e6d2a8"),
                Secondary = BodyRgb.FromHex("#6a3a22"),
                AtmoColor = BodyRgb.FromHex("#e8c9a0"),
                GlowColor = BodyRgb.FromHex("#c45a32"),
                Shadow = true
            },
            ["saturn"] = new BodyVisualState
            {
                Type = BodyVisualType.Gas,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:saturn")),
                Size = 130,
                Light = -18,
                Water = 0,
                Clouds = 0,
                Atmo = 82,
                Craters = 0,
                Rings = 88,
                Rotation = 27,
                Flattening = 10,
                Variance = 10,
                Primary = BodyRgb.FromHex("#ead9a4"),
                Secondary = BodyRgb.FromHex("#c4ae78"),
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
                Water = 0,
                Clouds = 0,
                Atmo = 70,
                Craters = 0,
                Rings = 35,
                Rotation = 82,
                Flattening = 2,
                Variance = 4,
                Primary = BodyRgb.FromHex("#9fd9d0"),
                Secondary = BodyRgb.FromHex("#c8ebe4"),
                AtmoColor = BodyRgb.FromHex("#b4ece4"),
                GlowColor = BodyRgb.FromHex("#d0f8ff"),
                Shadow = true
            },
            ["neptune"] = new BodyVisualState
            {
                Type = BodyVisualType.Ice,
                Seed = unchecked((int)ShipVisualRng.Hash("sol:neptune")),
                Size = 108,
                Light = -22,
                Water = 0,
                Clouds = 0,
                Atmo = 75,
                Craters = 0,
                Rings = 15,
                Rotation = 28,
                Flattening = 2,
                Variance = 10,
                Primary = BodyRgb.FromHex("#1a4aa8"),
                Secondary = BodyRgb.FromHex("#5a9ae0"),
                AtmoColor = BodyRgb.FromHex("#6aa0e8"),
                GlowColor = BodyRgb.FromHex("#d8eefe"),
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
