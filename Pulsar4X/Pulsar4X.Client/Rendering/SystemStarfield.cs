using System;
using System.Runtime.InteropServices;
using Pulsar4X.Client.ShipVisuals;
using SDL3;

namespace Pulsar4X.Client.Rendering;

/// <summary>
/// Screen-space procedural sky behind the system map: stars, a soft baked nebula haze,
/// and occasional meteors. Seeded from system id; light parallax on the star layer.
/// </summary>
public sealed class SystemStarfield
{
    private const float Parallax = 0.045f;
    private const int MaxMeteors = 2;
    private const double MeteorIntervalMs = 4200;
    private const double MeteorLifeMs = 1100;
    private const int NebulaW = 320;
    private const int NebulaH = 180;

    private readonly record struct Star(float U, float V, byte Size, byte R, byte G, byte B, byte A);

    private struct Meteor
    {
        public float U0, V0, U1, V1;
        public long StartMs;
        public bool Active;
    }

    private Star[] _stars = Array.Empty<Star>();
    private readonly Meteor[] _meteors = new Meteor[MaxMeteors];
    private Func<double> _rng = () => 0;
    private uint _seed;
    private string? _systemId;
    private long _nextMeteorAtMs;
    private int _meteorCursor;
    private int _nebulaCount;
    private byte[]? _nebulaPixels;
    private IntPtr _nebulaTex = IntPtr.Zero;
    private bool _nebulaNeedsUpload;

    public string? SystemId => _systemId;
    public uint Seed => _seed;
    public int StarCount => _stars.Length;
    public int NebulaCount => _nebulaCount;

    /// <summary>Non-transparent pixels in the baked nebula (debug / tests).</summary>
    public int NebulaOpaquePixelCount
    {
        get
        {
            if (_nebulaPixels is null)
                return 0;
            int n = 0;
            for (int i = 3; i < _nebulaPixels.Length; i += 4)
            {
                if (_nebulaPixels[i] > 8)
                    n++;
            }
            return n;
        }
    }

    /// <summary>Normalized UV of star at index (for seed-stability tests).</summary>
    public (float u, float v) GetStarUv(int index)
    {
        var s = _stars[index];
        return (s.U, s.V);
    }

    public void Rebuild(string systemId)
    {
        _systemId = systemId ?? "";
        _seed = ShipVisualRng.Hash("starfield:" + _systemId);
        _rng = ShipVisualRng.Create(unchecked((int)_seed));

        int starCount = 180 + (int)(_rng() * 71); // 180–250
        _stars = new Star[starCount];
        for (int i = 0; i < starCount; i++)
        {
            float u = (float)_rng();
            float v = (float)_rng();
            byte size = (byte)(1 + (int)(_rng() * 3)); // 1–3
            float cool = (float)_rng();
            byte r, g, b;
            if (cool < 0.35f)
            {
                r = 180; g = 200; b = 255;
            }
            else if (cool < 0.7f)
            {
                r = 235; g = 235; b = 240;
            }
            else
            {
                r = 255; g = 220; b = 180;
            }

            byte a = (byte)(90 + (int)(_rng() * 150));
            if (size >= 3)
                a = (byte)Math.Min(255, a + 40);
            _stars[i] = new Star(u, v, size, r, g, b, a);
        }

        _nebulaCount = 3 + (int)(_rng() * 4); // 3–6 soft lobes in the bake
        BakeNebulaLayer();

        for (int i = 0; i < _meteors.Length; i++)
            _meteors[i].Active = false;
        _nextMeteorAtMs = Environment.TickCount64 + 1500 + (long)(_rng() * 2500);
        _meteorCursor = 0;
    }

    public void Draw(IntPtr renderer, Camera camera, int viewportW, int viewportH)
    {
        if (_stars.Length == 0 || viewportW < 2 || viewportH < 2)
            return;

        SDL.SetRenderDrawBlendMode(renderer, SDL.BlendMode.Blend);

        float ox = (float)(camera.CameraWorldPosition_AU.X * camera.ZoomLevel * Parallax);
        float oy = (float)(-camera.CameraWorldPosition_AU.Y * camera.ZoomLevel * Parallax);

        EnsureNebulaTexture(renderer);
        DrawNebulaTexture(renderer, viewportW, viewportH, ox, oy);
        DrawStars(renderer, viewportW, viewportH, ox, oy);
        UpdateAndDrawMeteors(renderer, viewportW, viewportH, ox, oy);
    }

    private void BakeNebulaLayer()
    {
        DestroyNebulaTexture();
        _nebulaPixels = new byte[NebulaW * NebulaH * 4];

        var palettes = new (byte r, byte g, byte b)[]
        {
            (160, 70, 140),
            (70, 110, 170),
            (140, 60, 100),
            (60, 90, 140),
            (120, 80, 150),
        };
        int pal = (int)(_seed % (uint)palettes.Length);
        var (cr, cg, cb) = palettes[pal];
        var (cr2, cg2, cb2) = palettes[(pal + 2) % palettes.Length];
        int seed = unchecked((int)_seed);

        // Soft lobe centers (keep NebulaCount meaningful for tests).
        var lobes = new (float u, float v, float rx, float ry, float strength)[_nebulaCount];
        for (int i = 0; i < _nebulaCount; i++)
        {
            lobes[i] = (
                0.18f + (float)_rng() * 0.64f,
                0.2f + (float)_rng() * 0.6f,
                0.32f + (float)_rng() * 0.4f,
                0.24f + (float)_rng() * 0.34f,
                0.65f + (float)_rng() * 0.4f);
        }

        for (int y = 0; y < NebulaH; y++)
        {
            float v = (y + 0.5f) / NebulaH;
            for (int x = 0; x < NebulaW; x++)
            {
                float u = (x + 0.5f) / NebulaW;
                float n = 0.5f + 0.5f * Fbm2(u * 2.2f, v * 2.2f, seed);
                n += Fbm2(u * 5.5f + 3.1f, v * 5.5f - 1.7f, seed + 17) * 0.28f;

                // Gaussian lobes — no hard ellipse rim (exp never cuts off abruptly).
                float lobe = 0f;
                for (int i = 0; i < lobes.Length; i++)
                {
                    var L = lobes[i];
                    float du = (u - L.u) / L.rx;
                    float dv = (v - L.v) / L.ry;
                    float d2 = du * du + dv * dv;
                    float fall = MathF.Exp(-d2 * 2.4f);
                    lobe = Math.Max(lobe, fall * L.strength);
                }

                float dens = lobe * (0.4f + 0.6f * Smoothstep(0.22f, 0.7f, n));
                dens = MathF.Pow(Math.Clamp(dens, 0f, 1f), 1.05f);

                // Fade the whole layer at the texture border so the stretch has no hard frame.
                float edge = Smoothstep(0f, 0.14f, u) * Smoothstep(0f, 0.14f, 1f - u)
                           * Smoothstep(0f, 0.14f, v) * Smoothstep(0f, 0.14f, 1f - v);
                dens *= edge;

                if (dens < 0.015f)
                {
                    SetPx(_nebulaPixels, x, y, 0, 0, 0, 0);
                    continue;
                }

                float tint = Smoothstep(0.25f, 0.85f, n);
                byte r = (byte)Math.Clamp(cr + (cr2 - cr) * tint, 0, 255);
                byte g = (byte)Math.Clamp(cg + (cg2 - cg) * tint, 0, 255);
                byte b = (byte)Math.Clamp(cb + (cb2 - cb) * tint, 0, 255);
                byte a = (byte)Math.Clamp(dens * 100f, 0, 105);
                SetPx(_nebulaPixels, x, y, r, g, b, a);
            }
        }

        _nebulaNeedsUpload = true;
    }

    private void EnsureNebulaTexture(IntPtr renderer)
    {
        if (!_nebulaNeedsUpload || _nebulaPixels is null || renderer == IntPtr.Zero)
            return;

        DestroyNebulaTexture();
        GCHandle handle = GCHandle.Alloc(_nebulaPixels, GCHandleType.Pinned);
        try
        {
            // Streaming texture + UpdateTexture is more reliable than CreateSurfaceFrom for CPU buffers.
            _nebulaTex = SDL.CreateTexture(
                renderer,
                Textures.RgbaByteOrder,
                SDL.TextureAccess.Static,
                NebulaW,
                NebulaH);
            if (_nebulaTex != IntPtr.Zero)
            {
                SDL.UpdateTexture(_nebulaTex, IntPtr.Zero, handle.AddrOfPinnedObject(), NebulaW * 4);
                SDL.SetTextureBlendMode(_nebulaTex, SDL.BlendMode.Blend);
                SDL.SetTextureScaleMode(_nebulaTex, SDL.ScaleMode.Linear);
            }
        }
        finally
        {
            handle.Free();
        }

        _nebulaNeedsUpload = false;
    }

    private void DestroyNebulaTexture()
    {
        if (_nebulaTex != IntPtr.Zero)
        {
            SDL.DestroyTexture(_nebulaTex);
            _nebulaTex = IntPtr.Zero;
        }
    }

    private void DrawNebulaTexture(IntPtr renderer, int vw, int vh, float ox, float oy)
    {
        if (_nebulaTex == IntPtr.Zero)
            return;

        // Full-bleed, no pan offset — shifting the rect left a hard clear seam at the edge.
        var dst = new SDL.FRect { X = 0, Y = 0, W = vw, H = vh };
        SDL.RenderTexture(renderer, _nebulaTex, IntPtr.Zero, in dst);
    }

    private void DrawStars(IntPtr renderer, int vw, int vh, float ox, float oy)
    {
        for (int i = 0; i < _stars.Length; i++)
        {
            var s = _stars[i];
            float x = Wrap(s.U * vw + ox, vw);
            float y = Wrap(s.V * vh + oy, vh);
            int ix = (int)MathF.Round(x);
            int iy = (int)MathF.Round(y);
            SDL.SetRenderDrawColor(renderer, s.R, s.G, s.B, s.A);
            SDL.RenderPoint(renderer, ix, iy);
            if (s.Size >= 2)
            {
                SDL.RenderPoint(renderer, ix + 1, iy);
                SDL.RenderPoint(renderer, ix, iy + 1);
            }
            if (s.Size >= 3)
            {
                SDL.SetRenderDrawColor(renderer, s.R, s.G, s.B, (byte)Math.Min(255, s.A + 30));
                SDL.RenderPoint(renderer, ix - 1, iy);
                SDL.RenderPoint(renderer, ix, iy - 1);
            }
        }
    }

    private void UpdateAndDrawMeteors(IntPtr renderer, int vw, int vh, float ox, float oy)
    {
        long now = Environment.TickCount64;
        if (now >= _nextMeteorAtMs)
        {
            SpawnMeteor(now);
            _nextMeteorAtMs = now + (long)(MeteorIntervalMs * (0.55 + _rng() * 1.1));
        }

        for (int i = 0; i < _meteors.Length; i++)
        {
            ref var m = ref _meteors[i];
            if (!m.Active)
                continue;
            double age = now - m.StartMs;
            if (age >= MeteorLifeMs)
            {
                m.Active = false;
                continue;
            }

            float t = (float)(age / MeteorLifeMs);
            float headU = m.U0 + (m.U1 - m.U0) * t;
            float headV = m.V0 + (m.V1 - m.V0) * t;
            float tailT = Math.Max(0f, t - 0.12f);
            float tailU = m.U0 + (m.U1 - m.U0) * tailT;
            float tailV = m.V0 + (m.V1 - m.V0) * tailT;

            float hx = Wrap(headU * vw + ox * 0.3f, vw);
            float hy = Wrap(headV * vh + oy * 0.3f, vh);
            float tx = Wrap(tailU * vw + ox * 0.3f, vw);
            float ty = Wrap(tailV * vh + oy * 0.3f, vh);

            if (MathF.Abs(hx - tx) > vw * 0.35f || MathF.Abs(hy - ty) > vh * 0.35f)
                continue;

            byte a = (byte)Math.Clamp(200 * (1f - t) * (1f - t), 0, 220);
            SDL.SetRenderDrawColor(renderer, 230, 235, 255, a);
            SDL.RenderLine(renderer, tx, ty, hx, hy);
            SDL.SetRenderDrawColor(renderer, 255, 255, 255, (byte)Math.Min(255, a + 40));
            SDL.RenderPoint(renderer, (int)MathF.Round(hx), (int)MathF.Round(hy));
        }
    }

    private void SpawnMeteor(long nowMs)
    {
        int slot = -1;
        for (int i = 0; i < _meteors.Length; i++)
        {
            if (!_meteors[i].Active)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
            return;

        float u0 = (float)_rng();
        float v0 = (float)_rng() * 0.55f;
        float ang = (float)(_rng() * Math.PI * 0.35 + Math.PI * 0.15);
        if (_rng() < 0.5)
            ang = -ang;
        float len = 0.18f + (float)_rng() * 0.22f;
        float u1 = u0 + MathF.Cos(ang) * len;
        float v1 = v0 + MathF.Abs(MathF.Sin(ang)) * len;

        _meteors[slot] = new Meteor
        {
            U0 = u0,
            V0 = v0,
            U1 = u1,
            V1 = v1,
            StartMs = nowMs,
            Active = true
        };
        _meteorCursor++;
    }

    private static void SetPx(byte[] px, int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * NebulaW + x) * 4;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
        px[i + 3] = a;
    }

    private static float Wrap(float x, float span)
    {
        if (span <= 0)
            return 0;
        x %= span;
        if (x < 0)
            x += span;
        return x;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / Math.Max(1e-6f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Hash21(float x, float y, int seed)
    {
        int n = unchecked(
            (int)(x * 374761393f) + (int)(y * 668265263f) + seed * 1103515245);
        n = (n ^ (n >> 13)) * 1274126177;
        n ^= n >> 16;
        return (n & 0x7fffffff) / (float)0x7fffffff;
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        float ux = fx * fx * (3f - 2f * fx);
        float uy = fy * fy * (3f - 2f * fy);
        float a = Hash21(x0, y0, seed);
        float b = Hash21(x0 + 1, y0, seed);
        float c = Hash21(x0, y0 + 1, seed);
        float d = Hash21(x0 + 1, y0 + 1, seed);
        float ab = a + (b - a) * ux;
        float cd = c + (d - c) * ux;
        return ab + (cd - ab) * uy;
    }

    private static float Fbm2(float x, float y, int seed)
    {
        float sum = 0, amp = 0.5f, freq = 1f;
        for (int i = 0; i < 5; i++)
        {
            sum += (ValueNoise(x * freq, y * freq, seed + i * 31) * 2f - 1f) * amp;
            amp *= 0.5f;
            freq *= 2.05f;
        }
        return Math.Clamp(sum, -1f, 1f);
    }
}
