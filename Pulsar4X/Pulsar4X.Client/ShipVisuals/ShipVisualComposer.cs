using System;
using System.Collections.Generic;

namespace Pulsar4X.Client.ShipVisuals;

/// <summary>
/// Aurora-style ship compositor: places tinted part sprites onto a 256×256 canvas.
/// </summary>
public sealed class ShipVisualComposer
{
    private readonly PartLibrary _library;
    private readonly PaletteTint _tint = new();
    private readonly Dictionary<RgbaImage, string> _partKeys = new();

    public ShipVisualComposer(PartLibrary? library = null)
    {
        _library = library ?? new PartLibrary();
    }

    public RgbaImage Compose(ShipVisualState state)
    {
        _library.EnsureLoaded();
        _tint.Clear();
        _partKeys.Clear();

        // Assign stable keys for tint cache (category:index).
        foreach (string cat in ShipPartCategories.All)
        {
            var list = _library.GetCategory(cat);
            for (int i = 0; i < list.Count; i++)
                _partKeys[list[i]] = $"{cat}:{i}";
        }

        string selJson = "{}"; // no manual part overrides in-game
        var rng = ShipVisualRng.Create(unchecked((int)ShipVisualRng.Hash(state.CanonicalKey() + selJson)));
        var canvas = new RgbaImage(256, 256);
        // Transparent backdrop so ImGui shows the designer panel behind the ship.
        canvas.Clear(0, 0, 0, 0);

        double cx = 128, cy = 124, scale = state.Size / 100.0;

        RgbaImage core = Pick("cores", rng);
        RgbaImage cockpit = Pick("cockpits", rng);
        RgbaImage wing = Pick("wings", rng);

        double coreW = 78 * scale, coreH = 104 * scale;
        double wingW = 56 * scale, wingH = 68 * scale;
        double wingRoot = 22 * scale + state.Spread * 0.18;
        double overlap = 7 + state.Overlap * 0.17;

        Bridge(canvas, state, cx - 12 * scale, cy - 52 * scale, 24 * scale, 101 * scale);
        Bridge(canvas, state, cx - wingRoot - 9 * scale, cy - 18 * scale, (wingRoot + 18 * scale) * 2, 20 * scale);

        Draw(canvas, state, wing, "wings", cx - wingRoot - wingW + overlap, cy - 38 * scale, wingW, wingH, flip: false, state.Seed + 1);
        Draw(canvas, state, wing, "wings", cx + wingRoot - overlap, cy - 38 * scale, wingW, wingH, flip: true, state.Seed + 1);

        Draw(canvas, state, core, "cores", cx - coreW / 2, cy - coreH / 2, coreW, coreH, false, state.Seed + 2);
        Draw(canvas, state, cockpit, "cockpits", cx - 24 * scale, cy - 59 * scale, 48 * scale, 46 * scale, false, state.Seed + 3);

        int armorN = System.Math.Min(3, (int)System.Math.Ceiling(state.Armor / 4.0));
        for (int i = 0; i < armorN; i++)
        {
            var im = Pick("armor", rng);
            double w = (50 - i * 5) * scale, h = 22 * scale, y = cy - 14 * scale + i * 20 * scale;
            Draw(canvas, state, im, "armor", cx - w / 2, y - h / 2, w, h, false, state.Seed + 10 + i);
        }

        int podN = System.Math.Min(4, (int)System.Math.Ceiling(state.Cargo / 3.0));
        if (podN > 0)
        {
            double railY = cy - 9 * scale, railH = (18 + podN * 13) * scale, railX = 34 * scale;
            Bridge(canvas, state, cx - railX - 5 * scale, railY, 5 * scale, railH);
            Bridge(canvas, state, cx + railX, railY, 5 * scale, railH);
        }
        for (int i = 0; i < podN; i++)
        {
            string cat = i % 2 == 0 ? "pods" : "cargo_hangars";
            var im = Pick(cat, rng);
            double y = cy - 5 * scale + i * 13 * scale, w = 30 * scale, h = 27 * scale, x = 34 * scale;
            Draw(canvas, state, im, cat, cx - x - w + 5 * scale, y - h / 2, w, h, false, state.Seed + 20 + i);
            Draw(canvas, state, im, cat, cx + x - 5 * scale, y - h / 2, w, h, true, state.Seed + 20 + i);
        }

        int weaponPairs = System.Math.Min(4, (int)System.Math.Ceiling(state.Weapons / 2.0));
        for (int i = 0; i < weaponPairs; i++)
        {
            var im = Pick("weapons", rng);
            double y = cy - 31 * scale + i * 17 * scale;
            double x = (36 + i % 2 * 7) * scale;
            double w = 27 * scale, h = 20 * scale;
            Bridge(canvas, state, cx - x - 7 * scale, y + 6 * scale, 10 * scale, 4 * scale);
            Bridge(canvas, state, cx + x - 3 * scale, y + 6 * scale, 10 * scale, 4 * scale);
            Draw(canvas, state, im, "weapons", cx - x - w + 4 * scale, y, w, h, false, state.Seed + 30 + i);
            Draw(canvas, state, im, "weapons", cx + x - 4 * scale, y, w, h, true, state.Seed + 30 + i);
        }

        int sensorGroups = System.Math.Min(2, (int)System.Math.Ceiling(state.Sensors / 4.0));
        if (sensorGroups > 0)
            Bridge(canvas, state, cx - 3 * scale, cy - 71 * scale, 6 * scale, 18 * scale);
        for (int i = 0; i < sensorGroups; i++)
        {
            var im = Pick("sensors", rng);
            double w = 28 * scale, h = 26 * scale;
            Draw(canvas, state, im, "sensors", cx - w / 2, cy - (83 + i * 18) * scale, w, h, false, state.Seed + 40 + i);
        }

        var engine = Pick("engines", rng);
        double engineW = System.Math.Min(92, 42 + state.Engines * 7) * scale;
        double engineH = 45 * scale;
        Bridge(canvas, state, cx - engineW * 0.34, cy + 34 * scale, engineW * 0.68, 15 * scale);
        Draw(canvas, state, engine, "engines", cx - engineW / 2, cy + 29 * scale, engineW, engineH, false, state.Seed + 50);

        return canvas;
    }

    private RgbaImage Pick(string category, Func<double> rng) => _library.Pick(category, rng);

    private void Draw(
        RgbaImage canvas,
        ShipVisualState state,
        RgbaImage source,
        string category,
        double x,
        double y,
        double w,
        double h,
        bool flip,
        int variantSeed)
    {
        string partKey = _partKeys.TryGetValue(source, out var k) ? k : category;
        RgbaImage art = _tint.Apply(source, category, partKey, state, variantSeed);

        int dstX = (int)System.Math.Round(x);
        int dstY = (int)System.Math.Round(y);
        int dstW = System.Math.Max(1, (int)System.Math.Round(w));
        int dstH = System.Math.Max(1, (int)System.Math.Round(h));

        // Soft drop shadow (no Gaussian blur — offset dark copy).
        BlitScaled(canvas, art, dstX + 2, dstY + 2, dstW, dstH, flip, shadow: true);
        BlitScaled(canvas, art, dstX, dstY, dstW, dstH, flip, shadow: false);
    }

    private static void Bridge(RgbaImage canvas, ShipVisualState state, double x, double y, double w, double h)
    {
        var dark = Shade(state.Palette.Primary, 0.34);
        var mid = Shade(state.Palette.Primary, 0.62);
        FillRect(canvas, (int)System.Math.Round(x), (int)System.Math.Round(y),
            System.Math.Max(1, (int)System.Math.Round(w)), System.Math.Max(1, (int)System.Math.Round(h)),
            dark.R, dark.G, dark.B, 255);
        FillRect(canvas,
            (int)System.Math.Round(x) + 2, (int)System.Math.Round(y) + 2,
            System.Math.Max(1, (int)System.Math.Round(w) - 4), System.Math.Max(1, (int)System.Math.Round(h) - 4),
            mid.R, mid.G, mid.B, 255);
    }

    private static void BlitScaled(RgbaImage dst, RgbaImage src, int dx, int dy, int dw, int dh, bool flip, bool shadow)
    {
        for (int y = 0; y < dh; y++)
        {
            int sy = y * src.Height / dh;
            for (int x = 0; x < dw; x++)
            {
                int sx = flip
                    ? (dw - 1 - x) * src.Width / dw
                    : x * src.Width / dw;
                var (r, g, b, a) = src.GetPixel(sx, sy);
                if (a < 8)
                    continue;
                if (shadow)
                {
                    // Dark translucent shadow
                    BlendPixel(dst, dx + x, dy + y, 0, 0, 0, (byte)(a * 0.45));
                }
                else
                {
                    BlendPixel(dst, dx + x, dy + y, r, g, b, a);
                }
            }
        }
    }

    private static void BlendPixel(RgbaImage dst, int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)dst.Width || (uint)y >= (uint)dst.Height || a == 0)
            return;
        if (a == 255)
        {
            dst.SetPixel(x, y, r, g, b, 255);
            return;
        }
        var (dr, dg, db, da) = dst.GetPixel(x, y);
        float af = a / 255f;
        float inv = 1f - af;
        dst.SetPixel(x, y,
            (byte)(r * af + dr * inv),
            (byte)(g * af + dg * inv),
            (byte)(b * af + db * inv),
            (byte)System.Math.Min(255, da + a));
    }

    private static void FillRect(RgbaImage img, int x, int y, int w, int h, byte r, byte g, byte b, byte a)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                BlendPixel(img, xx, yy, r, g, b, a);
    }

    private static RgbColor Shade(RgbColor rgb, double f) => new(
        (byte)System.Math.Clamp((int)System.Math.Round(rgb.R * f), 0, 255),
        (byte)System.Math.Clamp((int)System.Math.Round(rgb.G * f), 0, 255),
        (byte)System.Math.Clamp((int)System.Math.Round(rgb.B * f), 0, 255));
}
