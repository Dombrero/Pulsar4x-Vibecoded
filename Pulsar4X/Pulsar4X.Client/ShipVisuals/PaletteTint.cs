using System;
using System.Collections.Generic;

namespace Pulsar4X.Client.ShipVisuals;

/// <summary>HSV coherent palette remapping, ported from aurora tintImage().</summary>
public sealed class PaletteTint
{
    private readonly Dictionary<string, RgbaImage> _cache = new(StringComparer.Ordinal);

    public void Clear() => _cache.Clear();

    public RgbaImage Apply(RgbaImage source, string category, string partKey, ShipVisualState state, int variantSeed)
    {
        if (!state.CoherentColors)
            return source;

        string key =
            $"{partKey}|{category}|{state.Palette.Primary.ToHex()}|{state.Palette.Accent.ToHex()}|{state.Palette.Engine.ToHex()}|" +
            $"{state.AccentAmount}|{state.ColorVariance}|{variantSeed}";

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = source.Clone();
        var d = result.Pixels;
        var primary = state.Palette.Primary;
        var accent = state.Palette.Accent;
        var engine = state.Palette.Engine;
        var rnd = ShipVisualRng.Create(unchecked((int)ShipVisualRng.Hash(key)));
        double localShift = (rnd() - 0.5) * state.ColorVariance / 100.0;
        double accentThreshold = 1.0 - System.Math.Max(0.04, state.AccentAmount / 100.0);
        int width = result.Width;
        int height = result.Height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (d[i + 3] < 8)
                    continue;

                byte rr = d[i], gg = d[i + 1], bb = d[i + 2];
                double lum = (rr * 0.2126 + gg * 0.7152 + bb * 0.0722) / 255.0;
                var (h, sat, val) = RgbToHsv(rr, gg, bb);

                bool edgeDark = lum < 0.18;
                double bottomRatio = y / (double)System.Math.Max(1, height - 1);

                bool sourceWarm = (h < 75 || h > 330) && sat > 0.30;
                bool sourceCyan = h > 155 && h < 230 && sat > 0.28;
                bool sourceHighlight = val > 0.78 && sat > 0.22;

                double pattern = ((x * 13 + y * 7 + variantSeed * 3) % 100) / 100.0;
                bool accentEligible = sourceWarm || sourceHighlight || (category != "engines" && sat > 0.48);
                bool useAccent = accentEligible && pattern > accentThreshold;

                bool useEngine = category == "engines" &&
                    ((bottomRatio > 0.52 && (sourceWarm || sourceCyan || val > 0.52)) || bottomRatio > 0.76);

                RgbColor baseColor = primary;
                if (useEngine)
                    baseColor = engine;
                else if (useAccent)
                    baseColor = accent;

                double factor;
                if (edgeDark) factor = 0.19;
                else if (lum < 0.32) factor = 0.44;
                else if (lum < 0.50) factor = 0.68;
                else if (lum < 0.70) factor = 0.94;
                else if (lum < 0.86) factor = 1.18;
                else factor = 1.42;

                factor *= 1.0 + localShift;
                var outbound = Shade(baseColor, factor);
                d[i] = outbound.R;
                d[i + 1] = outbound.G;
                d[i + 2] = outbound.B;
            }
        }

        _cache[key] = result;
        return result;
    }

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = System.Math.Max(rf, System.Math.Max(gf, bf));
        double min = System.Math.Min(rf, System.Math.Min(gf, bf));
        double delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
            else h = 60 * ((rf - gf) / delta + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : delta / max, max);
    }

    private static RgbColor Shade(RgbColor rgb, double f)
    {
        return new RgbColor(
            (byte)System.Math.Clamp((int)System.Math.Round(rgb.R * f), 0, 255),
            (byte)System.Math.Clamp((int)System.Math.Round(rgb.G * f), 0, 255),
            (byte)System.Math.Clamp((int)System.Math.Round(rgb.B * f), 0, 255));
    }
}
