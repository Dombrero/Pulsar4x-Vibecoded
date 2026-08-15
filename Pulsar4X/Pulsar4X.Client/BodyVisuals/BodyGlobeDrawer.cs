using System;
using Pulsar4X.Api;
using SDL3;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>
/// Top-down fake-3D globe: unlit albedo/clouds as equirectangular textures, Lambert from the
/// real star, sidereal spin, filled isometric ring disk, fresnel atmosphere. UI thread only.
/// </summary>
public static class BodyGlobeDrawer
{
    private const float LightZ = 0.38f;
    private const float Ambient = 0.10f;
    private const float Wrap = 0.20f;
    private const float CloudSpinMul = 1.65f;
    private const float HighLodRadiusPx = 36f;

    private static readonly GlobeMesh LowLod = BuildMesh(rings: 10, sectors: 20);
    private static readonly GlobeMesh HighLod = BuildMesh(rings: 32, sectors: 56);
    private static readonly SDL.Vertex[] DrawVerts = new SDL.Vertex[HighLod.Tris.Length];
    private static readonly int[] DrawIndices = BuildIdentityIndices(HighLod.Tris.Length);

    private static GlobeMesh _active = HighLod;

    private static int[] BuildIdentityIndices(int count)
    {
        var idx = new int[count];
        for (int i = 0; i < count; i++)
            idx[i] = i;
        return idx;
    }

    private static GlobeMesh BuildMesh(int rings, int sectors)
    {
        var grid = new MeshVert[1 + rings * sectors];
        grid[0] = new MeshVert(0, 0, 1);
        int v = 1;
        for (int r = 1; r <= rings; r++)
        {
            float t = r / (float)rings;
            for (int s = 0; s < sectors; s++)
            {
                double ang = s / (double)sectors * Math.PI * 2.0;
                float nx = t * (float)Math.Cos(ang);
                float ny = t * (float)Math.Sin(ang);
                float nz = (float)Math.Sqrt(Math.Max(0, 1.0 - (double)t * t));
                grid[v++] = new MeshVert(nx, ny, nz);
            }
        }

        var tris = new int[sectors * 3 + (rings - 1) * sectors * 6];
        int tix = 0;
        for (int s = 0; s < sectors; s++)
        {
            tris[tix++] = 0;
            tris[tix++] = 1 + s;
            tris[tix++] = 1 + (s + 1) % sectors;
        }
        for (int r = 0; r < rings - 1; r++)
        {
            int row = 1 + r * sectors;
            int next = 1 + (r + 1) * sectors;
            for (int s = 0; s < sectors; s++)
            {
                int s1 = (s + 1) % sectors;
                tris[tix++] = row + s;
                tris[tix++] = next + s;
                tris[tix++] = next + s1;
                tris[tix++] = row + s;
                tris[tix++] = next + s1;
                tris[tix++] = row + s1;
            }
        }

        return new GlobeMesh(grid, tris);
    }

    private readonly record struct GlobeMesh(MeshVert[] Grid, int[] Tris);

    public readonly record struct LightDir(float X, float Y)
    {
        public static LightDir FromScreen(float bodyX, float bodyY, float starX, float starY)
        {
            float dx = starX - bodyX;
            float dy = starY - bodyY;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3f)
                return new LightDir(0.72f, -0.22f);
            return new LightDir(dx / len, dy / len);
        }
    }

    /// <summary>
    /// Sidereal spin from <paramref name="dayLength"/>. Unsurveyed / missing day stay still.
    /// Phase from seed so bodies are not all aligned at epoch.
    /// </summary>
    public static double SurfaceSpinRadians(DateTime simTime, TimeSpan dayLength, int seed, bool allowSpin)
    {
        double phase = (seed & 1023) / 1024.0 * Math.PI * 2.0;
        if (!allowSpin || dayLength.TotalSeconds < 60)
            return phase;
        return phase + Math.PI * 2.0 * (simTime.Ticks / (double)dayLength.Ticks);
    }

    public static double CloudSpinRadians(double surfaceSpin)
        => surfaceSpin * CloudSpinMul;

    public static void Draw(
        IntPtr renderer,
        float cx,
        float cy,
        float radiusPx,
        IntPtr albedo,
        IntPtr clouds,
        BodyVisualState visual,
        LightDir light,
        double surfaceSpin,
        double cloudSpin)
    {
        if (radiusPx < 2f || albedo == IntPtr.Zero)
            return;

        SDL.SetRenderDrawBlendMode(renderer, SDL.BlendMode.Blend);
        _active = radiusPx >= HighLodRadiusPx ? HighLod : LowLod;
        int n = _active.Tris.Length;

        bool rings = visual.Rings > 5;
        if (rings)
            DrawRings(renderer, cx, cy, radiusPx, visual, light, back: true);

        FillGlobe(cx, cy, radiusPx, visual, light, surfaceSpin, clouds: false);
        SDL.RenderGeometry(renderer, albedo, DrawVerts, n, DrawIndices, n);

        if (clouds != IntPtr.Zero)
        {
            FillGlobe(cx, cy, radiusPx, visual, light, cloudSpin, clouds: true);
            SDL.RenderGeometry(renderer, clouds, DrawVerts, n, DrawIndices, n);
        }

        if (visual.Atmo > 5)
        {
            FillAtmosphere(cx, cy, radiusPx, visual, light);
            SDL.RenderGeometry(renderer, IntPtr.Zero, DrawVerts, n, DrawIndices, n);
        }

        if (visual.Atmo > 40)
            DrawAtmosphereBloom(renderer, cx, cy, radiusPx, visual);

        if (rings)
            DrawRings(renderer, cx, cy, radiusPx, visual, light, back: false);
    }

    /// <summary>
    /// Far half of the isometric ring (local ellipse Y &lt; 0) sits behind the globe.
    /// </summary>
    public static bool RingSegmentIsBehind(double angle0, double angle1)
        => Math.Sin(angle0) + Math.Sin(angle1) < 0;

    /// <summary>Live plasma disk for stars. Granules boil with wall-clock time; no night side.</summary>
    public static void DrawStar(IntPtr renderer, float cx, float cy, float radiusPx, BodyVisualState visual)
    {
        if (radiusPx < 2f)
            return;

        SDL.SetRenderDrawBlendMode(renderer, SDL.BlendMode.Blend);
        _active = radiusPx >= HighLodRadiusPx ? HighLod : LowLod;
        int n = _active.Tris.Length;
        double t = Environment.TickCount64 / 1000.0;
        double spin = t * 0.28 + (visual.Seed & 1023) / 1024.0 * Math.PI * 2.0;
        FillStarGlobe(cx, cy, radiusPx, visual, t, spin);
        SDL.RenderGeometry(renderer, IntPtr.Zero, DrawVerts, n, DrawIndices, n);
    }

    /// <summary>
    /// Fake-3D ring ellipse for band 0. Minor/major stays ~0.20 so Sol's 0° orbits
    /// still read as a disk, not a face-on circle.
    /// </summary>
    public static (double Rx, double Ry) RingBandRadii(double planetRadius, int band)
    {
        double rx = planetRadius * (1.32 + band * 0.16);
        double ry = planetRadius * (0.26 + band * 0.035);
        return (rx, ry);
    }

    public static float PolarSquash(BodyVisualState visual)
        => 1f - Math.Clamp(visual.Flattening, 0, 20) / 100f;

    /// <summary>
    /// Warm emissive add on the sunlit face of hot airless rock (Mercury). Night stays dark;
    /// thick-atmosphere worlds skip this so Venus clouds do not look molten.
    /// </summary>
    public static (float r, float g, float b) DaysideHeatTint(BodyVisualState visual, float lambert)
    {
        if (visual.ThermalGlow <= 0 || visual.Type == BodyVisualType.Toxic || visual.Atmo >= 40)
            return (0f, 0f, 0f);
        if (visual.Type is BodyVisualType.Gas or BodyVisualType.Ice or BodyVisualType.Star)
            return (0f, 0f, 0f);

        float heat = Math.Clamp(visual.ThermalGlow / 100f, 0f, 1f);
        float day = lambert * lambert;
        float amt = heat * day * 0.48f;
        float gr = visual.GlowColor.R / 255f;
        float gg = visual.GlowColor.G / 255f;
        float gb = visual.GlowColor.B / 255f;
        return (amt * (0.55f + 0.65f * gr), amt * (0.25f + 0.50f * gg), amt * (0.04f + 0.18f * gb));
    }

    private static void FillStarGlobe(
        float cx, float cy, float radius,
        BodyVisualState visual,
        double time,
        double spin)
    {
        var mesh = _active;
        float cr = (float)Math.Cos(spin);
        float sr = (float)Math.Sin(spin);
        float pr = visual.Primary.R / 255f;
        float pg = visual.Primary.G / 255f;
        float pb = visual.Primary.B / 255f;
        float srR = visual.Secondary.R / 255f;
        float srG = visual.Secondary.G / 255f;
        float srB = visual.Secondary.B / 255f;
        float gr = visual.GlowColor.R / 255f;
        float gg = visual.GlowColor.G / 255f;
        float gb = visual.GlowColor.B / 255f;
        int seed = visual.Seed;
        var grid = mesh.Grid;
        var tris = mesh.Tris;
        float t = (float)time;

        for (int i = 0; i < tris.Length; i++)
        {
            var g = grid[tris[i]];
            float wx = g.Nx * cr + g.Nz * sr;
            float wy = g.Ny;
            float wz = -g.Nx * sr + g.Nz * cr;

            float n1 = Hash01(wx * 3.1f + t * 0.55f, wy * 3.1f, wz * 3.1f - t * 0.20f, seed);
            float n2 = Hash01(wx * 8.4f - t * 1.15f, wy * 8.4f + t * 0.35f, wz * 8.4f, seed + 17);
            float n3 = Hash01(wx * 18f + t * 2.1f, wy * 18f, wz * 18f, seed + 41);
            float boil = n1 * n1;
            float flare = Math.Clamp((n2 - 0.62f) * 2.8f, 0f, 1f);
            float spark = Math.Clamp((n3 - 0.78f) * 4.5f, 0f, 1f);
            float mix = Math.Clamp(0.25f + 0.55f * boil + 0.35f * flare, 0f, 1f);
            float r = pr + (srR - pr) * mix;
            float gch = pg + (srG - pg) * mix;
            float b = pb + (srB - pb) * mix;
            r = r + (gr - r) * flare * 0.65f;
            gch = gch + (gg - gch) * flare * 0.65f;
            b = b + (gb - b) * flare * 0.45f;
            r = Math.Clamp(r + spark * 0.35f, 0f, 1f);
            gch = Math.Clamp(gch + spark * 0.28f, 0f, 1f);
            b = Math.Clamp(b + spark * 0.12f, 0f, 1f);
            float limb = 0.72f + 0.28f * g.Nz;

            DrawVerts[i] = new SDL.Vertex
            {
                Position = new SDL.FPoint { X = cx + g.Nx * radius, Y = cy + g.Ny * radius },
                Color = new SDL.FColor { R = r * limb, G = gch * limb, B = b * limb, A = 1f },
                TexCoord = new SDL.FPoint { X = 0, Y = 0 }
            };
        }
    }

    private static float Hash01(float x, float y, float z, int seed)
    {
        int n = unchecked(
            (int)(x * 374761393f) + (int)(y * 668265263f) + (int)(z * 1274126177f) + seed * 1103515245);
        n = (n ^ (n >> 13)) * 1274126177;
        n ^= n >> 16;
        return (n & 0x7fffffff) / (float)0x7fffffff;
    }

    private static void FillGlobe(
        float cx, float cy, float radius,
        BodyVisualState visual,
        LightDir light,
        double spin,
        bool clouds)
    {
        var mesh = _active;
        float cSpin = (float)Math.Cos(spin);
        float sSpin = (float)Math.Sin(spin);
        float lx = light.X;
        float ly = light.Y;
        bool lava = visual.Type == BodyVisualType.Lava;
        bool earth = visual.CacheKey.StartsWith("sol:earth", StringComparison.OrdinalIgnoreCase);
        var grid = mesh.Grid;
        var tris = mesh.Tris;

        for (int i = 0; i < tris.Length; i++)
        {
            var g = grid[tris[i]];
            float wx = g.Nx * cSpin + g.Nz * sSpin;
            float wy = g.Ny;
            float wz = -g.Nx * sSpin + g.Nz * cSpin;

            float lon = MathF.Atan2(wz, wx);
            float lat = MathF.Asin(Math.Clamp(wy, -1f, 1f));
            float u = (lon + MathF.PI) / (MathF.PI * 2f);
            float v = 0.5f - lat / MathF.PI;

            float ndotl = g.Nx * lx + g.Ny * ly + g.Nz * LightZ;
            float lambert = Math.Clamp((ndotl + Wrap) / (1f + Wrap), 0f, 1f);
            float shade = Ambient + (1f - Ambient) * lambert;
            shade *= 0.62f + 0.38f * g.Nz;
            if (lava)
                shade = Math.Max(shade, 0.35f + 0.25f * (1f - lambert));

            float cr = shade, cg = shade, cb = shade;
            if (!clouds && earth && lambert < 0.42f)
            {
                float night = Math.Clamp((0.42f - lambert) / 0.42f, 0f, 1f);
                float lights = BodyVisualComposer.EarthCityLights(wx, wy, wz) * night;
                cr = Math.Clamp(shade + lights * 1.15f, 0f, 1f);
                cg = Math.Clamp(shade + lights * 0.72f, 0f, 1f);
                cb = Math.Clamp(shade + lights * 0.22f, 0f, 1f);
            }
            else if (!clouds)
            {
                var heat = DaysideHeatTint(visual, lambert);
                cr = Math.Clamp(cr + heat.r, 0f, 1f);
                cg = Math.Clamp(cg + heat.g, 0f, 1f);
                cb = Math.Clamp(cb + heat.b, 0f, 1f);
            }

            // Rings leak through a faded limb; keep the sphere opaque when a ring disk is present.
            float a = clouds || visual.Rings > 5
                ? 1f
                : (g.Nz < 0.12f ? Math.Clamp(g.Nz / 0.12f, 0f, 1f) : 1f);
            if (clouds)
            {
                shade = 0.45f + 0.55f * lambert;
                cr = cg = cb = shade;
            }

            DrawVerts[i] = new SDL.Vertex
            {
                Position = new SDL.FPoint { X = cx + g.Nx * radius, Y = cy + g.Ny * radius * PolarSquash(visual) },
                Color = new SDL.FColor { R = cr, G = cg, B = cb, A = a },
                TexCoord = new SDL.FPoint { X = u, Y = v }
            };
        }

        FixUvSeams(tris.Length);
    }

    private static void FillAtmosphere(float cx, float cy, float radius, BodyVisualState visual, LightDir light)
    {
        var mesh = _active;
        float at = Math.Clamp(visual.Atmo / 100f, 0f, 1f);
        float ar = visual.AtmoColor.R / 255f;
        float ag = visual.AtmoColor.G / 255f;
        float ab = visual.AtmoColor.B / 255f;
        float lx = light.X;
        float ly = light.Y;
        var grid = mesh.Grid;
        var tris = mesh.Tris;

        for (int i = 0; i < tris.Length; i++)
        {
            var g = grid[tris[i]];
            float ndotl = g.Nx * lx + g.Ny * ly + g.Nz * LightZ;
            float lambert = Math.Clamp((ndotl + Wrap) / (1f + Wrap), 0f, 1f);
            float fresnel = (1f - g.Nz) * (1f - g.Nz);
            bool earth = visual.CacheKey.StartsWith("sol:earth", StringComparison.OrdinalIgnoreCase);
            float rim = earth ? 0.48f + 0.52f * lambert : 0.35f + 0.50f * lambert;
            float alpha = Math.Clamp(fresnel * at * rim * (earth ? 1.25f : 1f), 0f, earth ? 0.92f : 0.85f);
            DrawVerts[i] = new SDL.Vertex
            {
                Position = new SDL.FPoint { X = cx + g.Nx * radius, Y = cy + g.Ny * radius * PolarSquash(visual) },
                Color = new SDL.FColor { R = ar, G = ag, B = ab, A = alpha },
                TexCoord = new SDL.FPoint { X = 0, Y = 0 }
            };
        }
    }

    private static void FixUvSeams(int vertCount)
    {
        for (int i = 0; i < vertCount; i += 3)
        {
            float u0 = DrawVerts[i].TexCoord.X;
            float u1 = DrawVerts[i + 1].TexCoord.X;
            float u2 = DrawVerts[i + 2].TexCoord.X;
            float min = Math.Min(u0, Math.Min(u1, u2));
            float max = Math.Max(u0, Math.Max(u1, u2));
            if (max - min <= 0.5f)
                continue;

            if (u0 < 0.5f)
            {
                var v0 = DrawVerts[i];
                v0.TexCoord.X = u0 + 1f;
                DrawVerts[i] = v0;
            }
            if (u1 < 0.5f)
            {
                var v1 = DrawVerts[i + 1];
                v1.TexCoord.X = u1 + 1f;
                DrawVerts[i + 1] = v1;
            }
            if (u2 < 0.5f)
            {
                var v2 = DrawVerts[i + 2];
                v2.TexCoord.X = u2 + 1f;
                DrawVerts[i + 2] = v2;
            }
        }
    }

    private static void DrawAtmosphereBloom(IntPtr renderer, float cx, float cy, float radius, BodyVisualState visual)
    {
        bool earth = visual.CacheKey.StartsWith("sol:earth", StringComparison.OrdinalIgnoreCase);
        float at = Math.Clamp(visual.Atmo / 100f, 0f, 1f);
        int icx = (int)MathF.Round(cx);
        int icy = (int)MathF.Round(cy);
        int rings = earth ? 5 : 3;
        for (int i = 0; i < rings; i++)
        {
            float t = (i + 1) / (float)rings;
            float spread = earth ? 0.22f : 0.16f;
            int rad = Math.Max(2, (int)MathF.Round(radius * (1.04f + t * spread * at)));
            byte a = (byte)Math.Clamp((earth ? 80 : 55) * (1f - t) * at, 0, earth ? 120 : 90);
            if (a < 8)
                continue;
            SDL.SetRenderDrawColor(renderer, visual.AtmoColor.R, visual.AtmoColor.G, visual.AtmoColor.B, a);
            int ry = Math.Max(2, (int)MathF.Round(rad * PolarSquash(visual)));
            DrawPrimitive.DrawEllipse(renderer, icx, icy, rad, ry);
        }
    }

    private static void DrawRings(
        IntPtr renderer,
        float cx, float cy, float radius,
        BodyVisualState visual,
        LightDir light,
        bool back)
    {
        int ringCount = 2 + visual.Rings / 35;
        double rot = visual.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rot);
        double sin = Math.Sin(rot);
        float cr = visual.Secondary.R / 255f;
        float cg = visual.Secondary.G / 255f;
        float cb = visual.Secondary.B / 255f;
        float pr = visual.Primary.R / 255f;
        float pg = visual.Primary.G / 255f;
        float pb = visual.Primary.B / 255f;

        SDL.SetRenderDrawBlendMode(renderer, SDL.BlendMode.Blend);
        const int segs = 80;
        int vi = 0;

        for (int band = 0; band < ringCount; band++)
        {
            if (visual.Rings > 50 && band == 1)
                continue;

            var (rx, ry) = RingBandRadii(radius, band);
            double inner = 0.90;
            double outer = 1.08;
            float bandAlpha = Math.Clamp(0.62f - band * 0.08f, 0.28f, 0.68f);
            float tint = band % 2 == 0 ? 0.18f : 0.42f;

            for (int i = 0; i < segs; i++)
            {
                double a0 = i / (double)segs * Math.PI * 2.0;
                double a1 = (i + 1) / (double)segs * Math.PI * 2.0;
                var i0 = RingPoint(rx, ry, inner, a0, cos, sin);
                var o0 = RingPoint(rx, ry, outer, a0, cos, sin);
                var i1 = RingPoint(rx, ry, inner, a1, cos, sin);
                var o1 = RingPoint(rx, ry, outer, a1, cos, sin);
                if (RingSegmentIsBehind(a0, a1) != back)
                    continue;
                // Far half that sits on the globe disk is occluded — don't leak through the limb.
                if (back)
                {
                    double mx = (i0.X + o0.X + i1.X + o1.X) * 0.25;
                    double my = (i0.Y + o0.Y + i1.Y + o1.Y) * 0.25;
                    if (mx * mx + my * my < radius * radius * 0.96)
                        continue;
                }
                if (vi + 6 > DrawVerts.Length)
                    break;

                PushRingVert(ref vi, cx, cy, i0, cr, cg, cb, pr, pg, pb, tint, bandAlpha, light, inner);
                PushRingVert(ref vi, cx, cy, o0, cr, cg, cb, pr, pg, pb, tint, bandAlpha * 0.78f, light, outer);
                PushRingVert(ref vi, cx, cy, o1, cr, cg, cb, pr, pg, pb, tint, bandAlpha * 0.78f, light, outer);
                PushRingVert(ref vi, cx, cy, i0, cr, cg, cb, pr, pg, pb, tint, bandAlpha, light, inner);
                PushRingVert(ref vi, cx, cy, o1, cr, cg, cb, pr, pg, pb, tint, bandAlpha * 0.78f, light, outer);
                PushRingVert(ref vi, cx, cy, i1, cr, cg, cb, pr, pg, pb, tint, bandAlpha, light, inner);
            }
        }

        if (vi > 0)
            SDL.RenderGeometry(renderer, IntPtr.Zero, DrawVerts, vi, DrawIndices, vi);
    }

    private static (double X, double Y) RingPoint(
        double rx, double ry, double scale, double ang, double cos, double sin)
    {
        double lx = rx * scale * Math.Cos(ang);
        double ly = ry * scale * Math.Sin(ang);
        return (lx * cos - ly * sin, lx * sin + ly * cos);
    }

    private static void PushRingVert(
        ref int vi,
        float cx, float cy,
        (double X, double Y) p,
        float cr, float cg, float cb,
        float pr, float pg, float pb,
        float tint, float alpha,
        LightDir light,
        double radial)
    {
        float nx = (float)p.X;
        float ny = (float)p.Y;
        float len = MathF.Sqrt(nx * nx + ny * ny);
        float lx = len > 1e-3f ? nx / len : 0f;
        float ly = len > 1e-3f ? ny / len : 0f;
        float lambert = Math.Clamp(0.42f + 0.58f * (0.5f + 0.5f * (lx * light.X + ly * light.Y)), 0.22f, 1f);
        float mix = tint;
        DrawVerts[vi++] = new SDL.Vertex
        {
            Position = new SDL.FPoint { X = cx + (float)p.X, Y = cy + (float)p.Y },
            Color = new SDL.FColor
            {
                R = (cr + (pr - cr) * mix) * lambert,
                G = (cg + (pg - cg) * mix) * lambert,
                B = (cb + (pb - cb) * mix) * lambert,
                A = alpha * (float)(0.75 + 0.25 * (1.08 - radial) / 0.18)
            },
            TexCoord = new SDL.FPoint { X = 0, Y = 0 }
        };
    }

    private readonly record struct MeshVert(float Nx, float Ny, float Nz);
}
