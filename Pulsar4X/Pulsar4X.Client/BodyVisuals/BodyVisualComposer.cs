using System;
using Pulsar4X.Client.ShipVisuals;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>HTML celestial_body_generator port: pixel planets/stars/asteroids on transparent canvas.</summary>
public sealed class BodyVisualComposer
{
    public RgbaImage Compose(BodyVisualState p)
    {
        var canvas = new RgbaImage(256, 256);
        canvas.Clear(0, 0, 0, 0);
        var rng = ShipVisualRng.Create(unchecked((int)ShipVisualRng.Hash(Canonical(p))));

        switch (p.Type)
        {
            case BodyVisualType.Star:
                DrawStar(canvas, p, rng);
                break;
            case BodyVisualType.Asteroid:
                DrawAsteroid(canvas, p, rng, comet: false);
                break;
            case BodyVisualType.Comet:
                DrawAsteroid(canvas, p, rng, comet: true);
                break;
            default:
                DrawPlanet(canvas, p, rng);
                break;
        }

        return canvas;
    }

    private static string Canonical(BodyVisualState p)
        => $"{p.Type}|{p.Seed}|{p.Size}|{p.Rotation}|{p.Light}|{p.Water}|{p.Clouds}|{p.Atmo}|{p.Craters}|{p.Rings}|{p.Anomalies}|{p.Variance}";

    private static void DrawPlanet(RgbaImage canvas, BodyVisualState p, Func<double> r)
    {
        double cx = 128, cy = 132, rad = p.Size * 0.44;
        int irad = Math.Max(4, (int)Math.Round(rad));

        // Soft limb / base — ocean blue when water dominates, else primary/secondary mix
        bool oceans = p.Water >= 35 && p.Type is BodyVisualType.Terrestrial or BodyVisualType.Ice;
        var baseCol = oceans
            ? (p.Type == BodyVisualType.Ice ? new BodyRgb(160, 210, 230) : new BodyRgb(18, 75, 160))
            : p.Primary.Mix(p.Secondary, 0.35);
        for (int y = -irad; y <= irad; y++)
            for (int x = -irad; x <= irad; x++)
            {
                double d2 = x * x + y * y;
                if (d2 > rad * rad) continue;
                double d = Math.Sqrt(d2) / rad;
                double shade = 1.0 - d * 0.55;
                var col = baseCol.Shade(shade);
                canvas.SetPixel((int)cx + x, (int)cy + y, col.R, col.G, col.B, 255);
            }

        if (p.Type == BodyVisualType.Gas)
        {
            for (int i = 0; i < 18; i++)
            {
                double yy = cy - rad + (i / 17.0) * rad * 2;
                var col = p.Primary.Mix(p.Secondary, (i % 2) * 0.55 + r() * 0.2);
                for (int x = -(int)rad; x <= (int)rad; x++)
                {
                    double dy = yy - cy;
                    if (x * x + dy * dy > rad * rad) continue;
                    int px = (int)cx + x, py = (int)Math.Round(yy);
                    Blend(canvas, px, py, col.R, col.G, col.B, 200);
                }
            }
            for (int i = 0; i < 3; i++)
            {
                double ex = cx + (r() - 0.5) * rad, ey = cy + (r() - 0.5) * rad * 0.6;
                double rw = 8 + r() * 18, rh = 4 + r() * 8;
                FillEllipse(canvas, ex, ey, rw, rh, p.Secondary.R, p.Secondary.G, p.Secondary.B, 160, cx, cy, rad);
            }
        }
        else
        {
            // Land coverage ≈ (100 − water)%. Ocean when noise t < water/100.
            int landBlobs = oceans ? (int)(40 + (100 - p.Water) * 0.9) : 150;
            double oceanCut = oceans ? p.Water / 100.0 : 0;
            for (int i = 0; i < landBlobs; i++)
            {
                double x = cx - rad + r() * rad * 2;
                double y = cy - rad + r() * rad * 2;
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > rad * rad) continue;
                double n = Math.Sin(x * 0.17 + y * 0.11 + p.Seed * 0.001) + Math.Sin(x * 0.07 - y * 0.13);
                double t = Math.Clamp(0.5 + n * 0.22 + (r() - 0.5) * p.Variance / 100.0, 0, 1);
                if (oceans && t < oceanCut)
                    continue;

                var col = oceans
                    ? p.Secondary.Mix(new BodyRgb(90, 70, 40), t * 0.35)
                    : p.Primary.Mix(p.Secondary, t);
                if (p.Type == BodyVisualType.Lava && r() < 0.22)
                    col = p.GlowColor;
                if (p.Type == BodyVisualType.Ice && !oceans)
                    col = col.Mix(new BodyRgb(230, 250, 255), 0.42);
                if (p.Type == BodyVisualType.Moon)
                    col = col.Mix(new BodyRgb(135, 140, 150), 0.62);
                if (p.Type == BodyVisualType.Toxic)
                    col = col.Mix(new BodyRgb(90, 180, 70), 0.35);
                if (!oceans && p.Water > 20 && t < p.Water / 180.0 && p.Type is BodyVisualType.Terrestrial or BodyVisualType.Ice)
                    col = col.Mix(new BodyRgb(30, 90, 170), 0.65);
                // Patch-level thermal: hot cracks / cold frost on the rock itself.
                if (p.ThermalGlow > 15 && !oceans && r() < 0.18 + p.ThermalGlow / 250.0)
                    col = col.Mix(p.GlowColor, 0.35 + p.ThermalGlow / 180.0);
                else if (p.ThermalGlow < -15 && r() < 0.16 + -p.ThermalGlow / 280.0)
                    col = col.Mix(p.GlowColor, 0.30 + -p.ThermalGlow / 200.0);
                double rr = 1 + r() * (oceans ? 5 : 7);
                byte alpha = oceans ? (byte)230 : (byte)165;
                FillEllipse(canvas, x, y, rr, rr * 0.45, col.R, col.G, col.B, alpha, cx, cy, rad);
            }
        }

        int craterN = p.Craters / 7;
        for (int i = 0; i < craterN; i++)
        {
            double x = cx + (r() - 0.5) * rad * 1.4;
            double y = cy + (r() - 0.5) * rad * 1.4;
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > rad * rad * 0.85) continue;
            double cr = 2 + r() * 6;
            FillEllipse(canvas, x, y, cr, cr * 0.85, 20, 20, 28, 180, cx, cy, rad);
            FillEllipse(canvas, x - 1, y - 1, cr * 0.55, cr * 0.45, 80, 80, 90, 120, cx, cy, rad);
        }

        int cloudN = p.Clouds / 3;
        for (int i = 0; i < cloudN; i++)
        {
            double x = cx + (r() - 0.5) * rad * 1.6;
            double y = cy + (r() - 0.5) * rad * 1.2;
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > rad * rad) continue;
            FillEllipse(canvas, x, y, 4 + r() * 10, 2 + r() * 5, 240, 245, 255, 90, cx, cy, rad);
        }

        if (p.Shadow)
        {
            double lightT = (p.Light + 100) / 200.0; // 0..1
            for (int y = -irad; y <= irad; y++)
                for (int x = -irad; x <= irad; x++)
                {
                    if (x * x + y * y > rad * rad) continue;
                    double nx = (x / rad + 1) * 0.5; // 0 left .. 1 right
                    double shade = Math.Clamp((nx - lightT) * 2.2, 0, 1);
                    if (shade <= 0) continue;
                    byte a = (byte)(shade * 160);
                    Blend(canvas, (int)cx + x, (int)cy + y, 5, 8, 18, a);
                }
        }

        // Atmosphere rim
        if (p.Atmo > 5)
        {
            byte aa = (byte)Math.Clamp(p.Atmo * 1.8, 0, 180);
            DrawRing(canvas, cx, cy, rad + 1, rad + 2 + p.Atmo * 0.04, p.AtmoColor.R, p.AtmoColor.G, p.AtmoColor.B, aa);
        }

        // Temperature reads on the surface itself (orange/yellow heat, blue-white cold) — no outer halo.
        if (p.ThermalGlow != 0)
            ApplyThermalSurfaceGlow(canvas, cx, cy, rad, p);

        if (p.Rings > 5)
        {
            double rot = p.Rotation * Math.PI / 180.0;
            int ringCount = 2 + p.Rings / 35;
            for (int i = 0; i < ringCount; i++)
            {
                double rx = rad * (1.35 + i * 0.18);
                double ry = rad * (0.28 + i * 0.04);
                DrawRotatedEllipse(canvas, cx, cy, rx, ry, rot, p.Secondary.R, p.Secondary.G, p.Secondary.B, 140);
            }
        }

        int anom = p.Anomalies / 20;
        for (int i = 0; i < anom; i++)
        {
            double x = cx + (r() - 0.5) * rad;
            double y = cy + (r() - 0.5) * rad;
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > rad * rad * 0.7) continue;
            DrawRing(canvas, x, y, 2, 5 + r() * 4, p.GlowColor.R, p.GlowColor.G, p.GlowColor.B, 160);
        }
    }

    private static void DrawAsteroid(RgbaImage canvas, BodyVisualState p, Func<double> r, bool comet)
    {
        double cx = 128, cy = 132, rad = p.Size * 0.42;
        double rot = p.Rotation * Math.PI / 180.0;
        var pts = new (double x, double y)[22];
        for (int i = 0; i < 22; i++)
        {
            double a = i / 22.0 * Math.PI * 2;
            double rr = rad * (0.68 + r() * 0.38);
            double x = Math.Cos(a) * rr;
            double y = Math.Sin(a) * rr * 0.78;
            pts[i] = (x * Math.Cos(rot) - y * Math.Sin(rot), x * Math.Sin(rot) + y * Math.Cos(rot));
        }

        // Fill polygon (scan crude bounding box)
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var (x, y) in pts)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        for (int py = (int)Math.Floor(minY); py <= (int)Math.Ceiling(maxY); py++)
            for (int px = (int)Math.Floor(minX); px <= (int)Math.Ceiling(maxX); px++)
            {
                if (PointInPoly(px + 0.5, py + 0.5, pts))
                    canvas.SetPixel((int)cx + px, (int)cy + py, p.Primary.R, p.Primary.G, p.Primary.B, 255);
            }

        for (int i = 0; i < 14; i++)
        {
            double ox = (r() - 0.5) * rad * 1.15;
            double oy = (r() - 0.5) * rad * 0.75;
            FillEllipse(canvas, cx + ox, cy + oy, 2 + r() * 8, 2 + r() * 6, p.Secondary.R, p.Secondary.G, p.Secondary.B, 200, cx, cy, rad * 2);
        }

        if (comet)
        {
            for (int i = 0; i < 40; i++)
            {
                double t = i / 40.0;
                double tx = cx - rad * 0.2 - t * rad * 1.8;
                double ty = cy + (r() - 0.5) * rad * 0.4 * (1 - t);
                byte a = (byte)((1 - t) * 140);
                Blend(canvas, (int)tx, (int)ty, p.AtmoColor.R, p.AtmoColor.G, p.AtmoColor.B, a);
            }
        }
    }

    private static void DrawStar(RgbaImage canvas, BodyVisualState p, Func<double> r)
    {
        double cx = 128, cy = 132, rad = p.Size * 0.42;
        int outer = (int)(rad * 1.55);
        for (int y = -outer; y <= outer; y++)
            for (int x = -outer; x <= outer; x++)
            {
                double d = Math.Sqrt(x * x + y * y);
                if (d > outer) continue;
                double t = d / outer;
                BodyRgb col;
                if (t < 0.15) col = new BodyRgb(255, 255, 255);
                else if (t < 0.35) col = p.GlowColor;
                else if (t < 0.7) col = p.Primary.Mix(p.GlowColor, 0.3);
                else col = p.Primary;
                byte a = (byte)Math.Clamp((1.0 - t) * 255, 0, 255);
                Blend(canvas, (int)cx + x, (int)cy + y, col.R, col.G, col.B, a);
            }
        FillEllipse(canvas, cx, cy, rad, rad, p.GlowColor.R, p.GlowColor.G, p.GlowColor.B, 255, cx, cy, rad * 2);
        for (int i = 0; i < 10; i++)
        {
            double rr = rad * (0.45 + r() * 0.5);
            DrawRing(canvas, cx, cy, rr, rr + 1, 255, 240, 170, 40);
        }
    }

    private static void ApplyThermalSurfaceGlow(RgbaImage canvas, double cx, double cy, double rad, BodyVisualState p)
    {
        int magnitude = Math.Clamp(Math.Abs(p.ThermalGlow), 0, 100);
        if (magnitude < 8)
            return;

        bool heat = p.ThermalGlow > 0;
        double strength = magnitude / 100.0;
        // Orange → yellow with heat; ice-blue → white with cold.
        var tint = heat
            ? BodyRgb.FromHex("#ff3c00").Mix(BodyRgb.FromHex("#ffe14a"), strength)
            : BodyRgb.FromHex("#5a9fff").Mix(BodyRgb.FromHex("#f4fbff"), strength);

        int irad = Math.Max(4, (int)Math.Round(rad));
        for (int y = -irad; y <= irad; y++)
            for (int x = -irad; x <= irad; x++)
            {
                double d2 = x * x + y * y;
                if (d2 > rad * rad) continue;

                // Soft falloff toward the limb so the disk stays a sphere, not a flat tint.
                double d = Math.Sqrt(d2) / rad;
                double n = Math.Sin((cx + x) * 0.11 + (cy + y) * 0.09 + p.Seed * 0.002)
                         + Math.Sin((cx + x) * 0.05 - (cy + y) * 0.14);
                double mottled = 0.55 + 0.45 * Math.Clamp(0.5 + n * 0.28, 0, 1);
                // Keep emission stronger across the face (not only limb), so map-scale icons read heat/cold.
                double face = 1.0 - d * 0.35;
                double amount = strength * mottled * face;
                byte a = (byte)Math.Clamp(amount * (heat ? 195 : 170), 0, 220);
                if (a < 6)
                    continue;
                Blend(canvas, (int)cx + x, (int)cy + y, tint.R, tint.G, tint.B, a);
            }
    }

    private static bool PointInPoly(double x, double y, (double x, double y)[] pts)
    {
        bool inside = false;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
        {
            double xi = pts[i].x, yi = pts[i].y;
            double xj = pts[j].x, yj = pts[j].y;
            if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi + 1e-9) + xi))
                inside = !inside;
        }
        return inside;
    }

    private static void FillEllipse(RgbaImage img, double cx, double cy, double rx, double ry, byte r, byte g, byte b, byte a, double clipCx, double clipCy, double clipRad)
    {
        int minX = (int)Math.Floor(cx - rx), maxX = (int)Math.Ceiling(cx + rx);
        int minY = (int)Math.Floor(cy - ry), maxY = (int)Math.Ceiling(cy + ry);
        double rx2 = rx * rx, ry2 = ry * ry;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - cx, dy = y - cy;
                if (dx * dx / rx2 + dy * dy / ry2 > 1) continue;
                if ((x - clipCx) * (x - clipCx) + (y - clipCy) * (y - clipCy) > clipRad * clipRad) continue;
                Blend(img, x, y, r, g, b, a);
            }
    }

    private static void DrawRing(RgbaImage img, double cx, double cy, double r0, double r1, byte r, byte g, byte b, byte a)
    {
        int max = (int)Math.Ceiling(r1);
        for (int y = -max; y <= max; y++)
            for (int x = -max; x <= max; x++)
            {
                double d = Math.Sqrt(x * x + y * y);
                if (d < r0 || d > r1) continue;
                Blend(img, (int)cx + x, (int)cy + y, r, g, b, a);
            }
    }

    private static void DrawRotatedEllipse(RgbaImage img, double cx, double cy, double rx, double ry, double rot, byte r, byte g, byte b, byte a)
    {
        int max = (int)Math.Ceiling(Math.Max(rx, ry) + 2);
        double cos = Math.Cos(rot), sin = Math.Sin(rot);
        for (int y = -max; y <= max; y++)
            for (int x = -max; x <= max; x++)
            {
                double lx = x * cos + y * sin;
                double ly = -x * sin + y * cos;
                double d = (lx * lx) / (rx * rx) + (ly * ly) / (ry * ry);
                if (d < 0.92 || d > 1.08) continue;
                Blend(img, (int)cx + x, (int)cy + y, r, g, b, a);
            }
    }

    private static void Blend(RgbaImage dst, int x, int y, byte r, byte g, byte b, byte a)
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
            (byte)Math.Min(255, da + a));
    }
}
