using System;
using Pulsar4X.Api;
using Pulsar4X.Client.ShipVisuals;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>
/// Procedural body sprites. Planets bake an unlit equirectangular albedo (lighting/spin/rings
/// are applied live on the map). Stars/asteroids stay 2D disks.
/// </summary>
public sealed class BodyVisualComposer
{
    /// <summary>Map-icon resolution. 256 is sharp enough with linear filtering; 512 froze load/quickplay.</summary>
    public const int CanvasSize = 256;
    public const int EquirectWidth = 256;
    public const int EquirectHeight = 128;
    private const float RefCanvas = 256f;

    public static bool UsesLiveGlobe(BodyVisualState p)
        => p.Type is not BodyVisualType.Star
           and not BodyVisualType.Asteroid
           and not BodyVisualType.Comet;

    public static bool UsesLiveGlobe(BodyVisualState p, BodyKind kind)
    {
        if (kind is BodyKind.Asteroid or BodyKind.Comet)
            return false;
        if (kind == BodyKind.DwarfPlanet)
            return true;
        return UsesLiveGlobe(p);
    }

    public static bool UsesCloudLayer(BodyVisualState p)
        => UsesLiveGlobe(p)
           && p.Clouds > 8
           && p.Type is BodyVisualType.Terrestrial or BodyVisualType.Ice or BodyVisualType.Toxic;

    public RgbaImage Compose(BodyVisualState p)
    {
        var canvas = new RgbaImage(CanvasSize, CanvasSize);
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
                DrawPlanetSphere(canvas, p, rng);
                break;
        }

        return canvas;
    }

    /// <summary>Unlit lat/long albedo for the live globe (no terminator, rings, or atmosphere).</summary>
    public RgbaImage ComposeAlbedoMap(BodyVisualState p)
    {
        var canvas = new RgbaImage(EquirectWidth, EquirectHeight);
        canvas.Clear(0, 0, 0, 0);
        var rng = ShipVisualRng.Create(unchecked((int)ShipVisualRng.Hash(Canonical(p))));

        bool oceans = p.Water >= 35 && p.Type is BodyVisualType.Terrestrial or BodyVisualType.Ice;
        var oceanCol = p.Type == BodyVisualType.Ice
            ? new BodyRgb(160, 210, 230)
            : new BodyRgb(18, 75, 160);
        double oceanCut = oceans ? p.Water / 100.0 : 0;

        var craters = BuildSphereFeatures(rng, p.Craters / 7, minAng: 0.04, maxAng: 0.14);
        var anomalies = BuildSphereFeatures(rng, p.Anomalies / 20, minAng: 0.03, maxAng: 0.08);
        var storms = p.Type == BodyVisualType.Gas
            ? BuildSphereFeatures(rng, 3, minAng: 0.08, maxAng: 0.18)
            : Array.Empty<(double x, double y, double z, double cosR)>();

        for (int y = 0; y < EquirectHeight; y++)
        {
            double lat = (0.5 - (y + 0.5) / EquirectHeight) * Math.PI;
            double cl = Math.Cos(lat);
            double sl = Math.Sin(lat);
            for (int x = 0; x < EquirectWidth; x++)
            {
                double lon = ((x + 0.5) / EquirectWidth) * Math.PI * 2.0 - Math.PI;
                double wx = cl * Math.Cos(lon);
                double wy = sl;
                double wz = cl * Math.Sin(lon);
                var col = SampleSurface(p, oceans, oceanCol, oceanCut, wx, wy, wz, craters, storms);
                col = ApplyAnomalies(col, p, wx, wy, wz, anomalies);
                if (p.ThermalGlow != 0)
                    col = ApplyThermalAlbedo(col, p, wx, wy, wz);
                canvas.SetPixel(x, y, col.R, col.G, col.B, 255);
            }
        }

        return canvas;
    }

    /// <summary>Unlit cloud alpha map; empty (fully transparent) when the body has no cloud layer.</summary>
    public RgbaImage ComposeCloudMap(BodyVisualState p)
    {
        var canvas = new RgbaImage(EquirectWidth, EquirectHeight);
        canvas.Clear(0, 0, 0, 0);
        if (!UsesCloudLayer(p))
            return canvas;

        double cloudCut = 1.0 - Math.Clamp(p.Clouds / 100.0, 0, 1) * 0.82;
        for (int y = 0; y < EquirectHeight; y++)
        {
            double lat = (0.5 - (y + 0.5) / EquirectHeight) * Math.PI;
            double cl = Math.Cos(lat);
            double sl = Math.Sin(lat);
            for (int x = 0; x < EquirectWidth; x++)
            {
                double lon = ((x + 0.5) / EquirectWidth) * Math.PI * 2.0 - Math.PI;
                double wx = cl * Math.Cos(lon);
                double wy = sl;
                double wz = cl * Math.Sin(lon);
                double dens = CloudDensity(p, wx, wy, wz, cloudCut);
                if (dens < 0.04)
                    continue;
                byte a = (byte)Math.Clamp(dens * (IsSol(p, "earth") ? 235 : 200), 0, 230);
                var tint = p.Type == BodyVisualType.Toxic ? p.AtmoColor : new BodyRgb(236, 242, 252);
                canvas.SetPixel(x, y, tint.R, tint.G, tint.B, a);
            }
        }

        return canvas;
    }

    /// <summary>Visible sphere radius as a fraction of the icon box (hit-test / zoom framing).</summary>
    public static float TextureDiskFraction(BodyVisualState p)
        => UsesLiveGlobe(p) ? 0.48f : SphereRadiusPx(p) / CanvasSize;

    private static string Canonical(BodyVisualState p)
        => $"{p.Type}|{p.Seed}|{p.Size}|{p.Rotation}|{p.Light}|{p.Water}|{p.Clouds}|{p.Atmo}|{p.Craters}|{p.Rings}|{p.Anomalies}|{p.Variance}";

    private static float SphereRadiusPx(BodyVisualState p)
    {
        float scale = CanvasSize / RefCanvas;
        if (p.Rings > 5)
            return 72f * scale;
        if (p.Atmo > 5)
            return 106f * scale;
        return 114f * scale;
    }

    private static void DrawPlanetSphere(RgbaImage canvas, BodyVisualState p, Func<double> rng)
    {
        const double cx = CanvasSize * 0.5;
        const double cy = CanvasSize * 0.5;
        double rad = SphereRadiusPx(p);
        int irad = (int)Math.Ceiling(rad) + 8;

        bool oceans = p.Water >= 35 && p.Type is BodyVisualType.Terrestrial or BodyVisualType.Ice;
        var oceanCol = p.Type == BodyVisualType.Ice
            ? new BodyRgb(160, 210, 230)
            : new BodyRgb(18, 75, 160);

        double rot = p.Rotation * Math.PI / 180.0;
        double cr = Math.Cos(rot);
        double sr = Math.Sin(rot);

        // Light from the side (Light −100…100) and slightly above; viewer is +Z.
        double yaw = p.Light / 100.0 * 0.95;
        double lx = Math.Sin(yaw);
        double ly = -0.28;
        double lz = Math.Cos(yaw);
        double llen = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        lx /= llen; ly /= llen; lz /= llen;

        var craters = BuildSphereFeatures(rng, p.Craters / 7, minAng: 0.04, maxAng: 0.14);
        var anomalies = BuildSphereFeatures(rng, p.Anomalies / 20, minAng: 0.03, maxAng: 0.08);
        var storms = p.Type == BodyVisualType.Gas
            ? BuildSphereFeatures(rng, 3, minAng: 0.08, maxAng: 0.18)
            : Array.Empty<(double x, double y, double z, double cosR)>();

        if (p.Rings > 5)
            DrawPlanetRings(canvas, p, cx, cy, rad, back: true);

        double oceanCut = oceans ? p.Water / 100.0 : 0;
        double cloudCut = 1.0 - Math.Clamp(p.Clouds / 100.0, 0, 1) * 0.82;
        double atmoOuter = rad * (1.0 + Math.Clamp(p.Atmo, 0, 100) / 100.0 * 0.09);
        bool drawAtmo = p.Atmo > 5;

        for (int iy = -irad; iy <= irad; iy++)
        {
            for (int ix = -irad; ix <= irad; ix++)
            {
                int px = (int)cx + ix;
                int py = (int)cy + iy;
                double d2 = (ix * ix + iy * iy) / (rad * rad);
                if (d2 > 1.0)
                {
                    if (!drawAtmo)
                        continue;
                    double haloDist = Math.Sqrt(d2);
                    double outer = atmoOuter / rad;
                    if (haloDist >= outer)
                        continue;
                    double halo = 1.0 - (haloDist - 1.0) / (outer - 1.0);
                    // Light the halo from the same side as the globe.
                    double hx = ix / rad;
                    double hy = iy / rad;
                    double hlen = Math.Sqrt(hx * hx + hy * hy) + 1e-6;
                    double haloNdotl = Math.Clamp((hx / hlen) * lx + (hy / hlen) * ly + 0.35 * lz, 0.15, 1);
                    byte ha = (byte)Math.Clamp(halo * halo * (p.Atmo / 100.0) * 200 * haloNdotl, 0, 180);
                    if (ha < 4)
                        continue;
                    Blend(canvas, px, py, p.AtmoColor.R, p.AtmoColor.G, p.AtmoColor.B, ha);
                    continue;
                }

                double nx = ix / rad;
                double ny = iy / rad;
                double nz = Math.Sqrt(Math.Max(0, 1.0 - d2));

                // Edge AA
                double d = Math.Sqrt(d2);
                double coverage = d > 0.985 ? Math.Clamp((1.0 - d) / 0.015, 0, 1) : 1.0;

                // Spin around the polar (screen-Y) axis so the texture wraps like a globe.
                double wx = nx * cr + nz * sr;
                double wy = ny;
                double wz = -nx * sr + nz * cr;

                var albedo = SampleSurface(p, oceans, oceanCol, oceanCut, wx, wy, wz, craters, storms);
                albedo = ApplyAnomalies(albedo, p, wx, wy, wz, anomalies);

                if (p.ThermalGlow != 0)
                    albedo = ApplyThermalAlbedo(albedo, p, wx, wy, wz);

                double ndotl = nx * lx + ny * ly + nz * lz;
                const double wrap = 0.22;
                double lambert = p.Shadow
                    ? Math.Clamp((ndotl + wrap) / (1.0 + wrap), 0, 1)
                    : 0.85 + 0.15 * Math.Clamp(ndotl, 0, 1);
                double ambient = p.Shadow ? 0.10 : 0.55;
                double shade = ambient + (1.0 - ambient) * lambert;
                // Limb darkening sells the sphere even with frontal light.
                shade *= 0.62 + 0.38 * nz;

                var lit = albedo.Shade(shade);

                // Ocean specular (view = +Z).
                if (oceans && lambert > 0.35)
                {
                    double hx = lx;
                    double hy = ly;
                    double hz = lz + 1.0;
                    double hlen = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                    double spec = Math.Pow(Math.Max(0, (nx * hx + ny * hy + nz * hz) / hlen), 32);
                    if (spec > 0.02)
                        lit = lit.Mix(new BodyRgb(255, 255, 255), spec * 0.55);
                }

                // Lava stays emissive on the night side.
                if (p.Type == BodyVisualType.Lava)
                {
                    double emit = Math.Clamp(0.15 + Fbm(wx * 3.2, wy * 3.2, wz * 3.2, p.Seed + 7) * 0.35, 0, 0.7);
                    lit = lit.Mix(p.GlowColor, emit * (1.0 - lambert * 0.5));
                }

                if (p.Clouds > 4 && p.Type != BodyVisualType.Moon)
                {
                    double dens = CloudDensity(p, wx, wy, wz, cloudCut);
                    if (dens > 0)
                    {
                        var cloud = new BodyRgb(236, 242, 252).Shade(0.45 + 0.55 * lambert);
                        lit = lit.Mix(cloud, dens * 0.78);
                    }
                }

                if (drawAtmo)
                {
                    double fresnel = (1.0 - nz) * (1.0 - nz);
                    double at = Math.Clamp(p.Atmo / 100.0, 0, 1);
                    lit = lit.Mix(p.AtmoColor, fresnel * at * (0.35 + 0.45 * lambert));
                }

                byte alpha = (byte)Math.Clamp(coverage * 255, 0, 255);
                if (alpha < 255)
                    Blend(canvas, px, py, lit.R, lit.G, lit.B, alpha);
                else
                    canvas.SetPixel(px, py, lit.R, lit.G, lit.B, 255);
            }
        }

        if (p.Rings > 5)
            DrawPlanetRings(canvas, p, cx, cy, rad, back: false);
    }

    private static BodyRgb SampleSurface(
        BodyVisualState p,
        bool oceans,
        BodyRgb oceanCol,
        double oceanCut,
        double wx, double wy, double wz,
        (double x, double y, double z, double cosR)[] craters,
        (double x, double y, double z, double cosR)[] storms)
    {
        if (TrySampleSol(p, wx, wy, wz, out var solCol))
            return solCol;

        if (p.Type == BodyVisualType.Gas)
        {
            double lat = Math.Asin(Math.Clamp(wy, -1, 1));
            double turb = Fbm(wx * 3.4, wy * 0.55, wz * 3.4, p.Seed) * 0.28;
            double band = 0.5 + 0.5 * Math.Sin(lat * 14.0 + turb * 5.0 + p.Seed * 0.01);
            double fine = 0.5 + 0.5 * Fbm(wx * 9.0, wy * 1.2, wz * 9.0, p.Seed + 11);
            band = Math.Clamp(band * 0.82 + fine * 0.18, 0, 1);
            var gasCol = p.Primary.Mix(p.Secondary, band);
            for (int i = 0; i < storms.Length; i++)
            {
                var s = storms[i];
                if (wx * s.x + wy * s.y + wz * s.z > s.cosR)
                    gasCol = gasCol.Mix(p.Secondary, 0.45);
            }
            return gasCol;
        }

        // Continents + mid/high detail so close-ups are not a soft plaid blur.
        double n = 0.5 + 0.5 * Fbm(wx * 2.6, wy * 2.6, wz * 2.6, p.Seed);
        n += Fbm(wx * 9.5, wy * 9.5, wz * 9.5, p.Seed + 17) * 0.14;
        n = Math.Clamp(n + (p.Variance - 18) / 280.0, 0, 1);

        BodyRgb col;
        bool isOcean = oceans && n < oceanCut;
        if (isOcean)
        {
            col = oceanCol.Mix(p.Primary, n * 0.25);
        }
        else
        {
            col = oceans
                ? p.Secondary.Mix(new BodyRgb(90, 70, 40), (n - oceanCut) * 0.55)
                : p.Primary.Mix(p.Secondary, n);

            if (p.Type == BodyVisualType.Ice && !oceans)
                col = col.Mix(new BodyRgb(230, 250, 255), 0.42);
            if (p.Type == BodyVisualType.Moon)
                col = col.Mix(new BodyRgb(135, 140, 150), 0.55);
            if (p.Type == BodyVisualType.Toxic)
                col = col.Mix(new BodyRgb(90, 180, 70), 0.32);
            if (!oceans && p.Water > 20 && n < p.Water / 180.0
                && p.Type is BodyVisualType.Terrestrial or BodyVisualType.Ice)
                col = col.Mix(new BodyRgb(30, 90, 170), 0.6);
        }

        for (int i = 0; i < craters.Length; i++)
        {
            var c = craters[i];
            double dot = wx * c.x + wy * c.y + wz * c.z;
            if (dot <= c.cosR)
                continue;
            double t = (dot - c.cosR) / Math.Max(1e-4, 1.0 - c.cosR);
            if (t > 0.55)
                col = col.Mix(new BodyRgb(28, 28, 34), 0.55);
            else
                col = col.Mix(new BodyRgb(88, 88, 96), 0.28);
        }

        return col;
    }

    private static bool IsSol(BodyVisualState p, string name)
        => p.CacheKey.StartsWith("sol:" + name, StringComparison.OrdinalIgnoreCase);

    private static bool TrySampleSol(BodyVisualState p, double wx, double wy, double wz, out BodyRgb col)
    {
        col = default;
        if (!p.CacheKey.StartsWith("sol:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsSol(p, "earth")) { col = SampleEarth(p, wx, wy, wz); return true; }
        if (IsSol(p, "mars")) { col = SampleMars(p, wx, wy, wz); return true; }
        if (IsSol(p, "venus")) { col = SampleVenus(p, wx, wy, wz); return true; }
        if (IsSol(p, "mercury")) { col = SampleMercury(p, wx, wy, wz); return true; }
        if (IsSol(p, "jupiter")) { col = SampleJupiter(p, wx, wy, wz); return true; }
        if (IsSol(p, "saturn")) { col = SampleSaturn(p, wx, wy, wz); return true; }
        if (IsSol(p, "uranus")) { col = SampleUranus(p, wx, wy, wz); return true; }
        if (IsSol(p, "neptune")) { col = SampleNeptune(p, wx, wy, wz); return true; }
        return false;
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / Math.Max(1e-6, edge1 - edge0), 0, 1);
        return t * t * (3.0 - 2.0 * t);
    }

    private static BodyRgb SampleEarth(BodyVisualState p, double wx, double wy, double wz)
    {
        double lat = Math.Asin(Math.Clamp(wy, -1, 1)) * 180.0 / Math.PI;
        double lon = Math.Atan2(wz, wx) * 180.0 / Math.PI;
        if (EarthBlueMarble.TrySampleAlbedo(lat, lon, out var tex))
            return tex;

        double land = EarthLandField(wx, wy, wz);
        double coast = Fbm(wx * 14.0, wy * 14.0, wz * 14.0, p.Seed + 4) * 0.04;
        land += coast;

        if (lat < -62)
            return BodyRgb.FromHex("#eef4fa").Mix(BodyRgb.FromHex("#c8d8e8"), 0.5 + 0.5 * Fbm(wx * 6, wy * 6, wz * 6, p.Seed));

        if (land > 0)
        {
            double elev = Math.Clamp(land + 0.35 * (0.5 + 0.5 * Fbm(wx * 5.5, wy * 5.5, wz * 5.5, p.Seed + 8)), 0, 1);
            double mtn = EarthMountainField(lat, lon);
            var veg = BodyRgb.FromHex("#1e8a28").Mix(BodyRgb.FromHex("#3cbc44"), 0.45 + 0.4 * (0.5 + 0.5 * Fbm(wx * 7, wy * 7, wz * 7, p.Seed + 2)));
            var dirt = BodyRgb.FromHex("#c4a050");
            var col = veg.Mix(dirt, Math.Clamp(mtn * 0.85 + elev * 0.15, 0, 1));
            if (EarthBlob(lat, lon, 22, 12, 12, 22) > 0.05)
                col = col.Mix(BodyRgb.FromHex("#d2b46a"), 0.65); // Sahara
            if (lat > 70)
                col = col.Mix(BodyRgb.FromHex("#f4f8fc"), Smoothstep(70, 78, lat));
            return col;
        }

        double shelf = Smoothstep(-0.14, 0.0, land);
        var deep = BodyRgb.FromHex("#041830");
        var mid = BodyRgb.FromHex("#0a3a78");
        var teal = BodyRgb.FromHex("#2eb8c4");
        var ocean = deep.Mix(mid, Math.Clamp(0.35 + 0.4 * (0.5 + 0.5 * Fbm(wx * 2.2, wy * 2.2, wz * 2.2, p.Seed)), 0, 1));
        ocean = ocean.Mix(teal, shelf * 0.85);
        if (lat > 72)
            ocean = ocean.Mix(BodyRgb.FromHex("#e8f0f8"), Smoothstep(72, 82, lat));
        return ocean;
    }

    /// <summary>Signed land field: &gt;0 continent, ~0 coast, &lt;0 ocean. Stable Earth geography.</summary>
    public static double EarthLandField(double wx, double wy, double wz)
    {
        double lat = Math.Asin(Math.Clamp(wy, -1, 1)) * 180.0 / Math.PI;
        double lon = Math.Atan2(wz, wx) * 180.0 / Math.PI;
        double field = -1.0;
        field = Math.Max(field, EarthBlob(lat, lon, 46, -98, 24, 42));   // North America
        field = Math.Max(field, EarthBlob(lat, lon, 62, -140, 10, 24));  // Alaska
        field = Math.Max(field, EarthBlob(lat, lon, 15, -88, 11, 14));   // Central America
        field = Math.Max(field, EarthBlob(lat, lon, -10, -58, 30, 16));  // South America
        field = Math.Max(field, EarthBlob(lat, lon, -48, -70, 12, 8));   // Patagonia
        field = Math.Max(field, EarthBlob(lat, lon, 72, -42, 12, 16));   // Greenland
        field = Math.Max(field, EarthBlob(lat, lon, 8, 18, 32, 18));     // Africa
        field = Math.Max(field, EarthBlob(lat, lon, -24, 24, 12, 10));   // southern Africa
        field = Math.Max(field, EarthBlob(lat, lon, 52, 12, 14, 20));    // Europe
        field = Math.Max(field, EarthBlob(lat, lon, 40, 36, 10, 14));    // Anatolia
        field = Math.Max(field, EarthBlob(lat, lon, 58, 90, 18, 52));    // Siberia
        field = Math.Max(field, EarthBlob(lat, lon, 34, 108, 16, 26));   // China
        field = Math.Max(field, EarthBlob(lat, lon, 22, 78, 14, 12));    // India
        field = Math.Max(field, EarthBlob(lat, lon, 4, 114, 12, 22));    // SE Asia
        field = Math.Max(field, EarthBlob(lat, lon, -24, 134, 14, 20));  // Australia
        field = Math.Max(field, EarthBlob(lat, lon, -42, 172, 8, 8));    // New Zealand
        field = Math.Max(field, EarthBlob(lat, lon, 65, -18, 6, 8));     // Iceland
        if (lat < -60)
            field = Math.Max(field, (-60 - lat) / 16.0);
        field += Fbm(wx * 9.0, wy * 9.0, wz * 9.0, 2048) * 0.10;
        field -= Math.Max(0, EarthBlob(lat, lon, 25, -90, 8, 10)) * 0.55; // Gulf of Mexico
        field -= Math.Max(0, EarthBlob(lat, lon, 58, -86, 8, 10)) * 0.45; // Hudson Bay
        return field;
    }

    private static double EarthMountainField(double lat, double lon)
    {
        double m = Math.Max(0, EarthBlob(lat, lon, 42, -112, 18, 6));  // Rockies
        m = Math.Max(m, EarthBlob(lat, lon, -18, -70, 32, 4.5));       // Andes
        m = Math.Max(m, EarthBlob(lat, lon, 32, 82, 8, 16));           // Himalaya
        m = Math.Max(m, EarthBlob(lat, lon, 46, 10, 6, 8));            // Alps
        return Math.Clamp(m, 0, 1);
    }

    private static double EarthBlob(double lat, double lon, double lat0, double lon0, double rLat, double rLon)
    {
        double u = (lat - lat0) / rLat;
        double dLon = lon - lon0;
        if (dLon > 180) dLon -= 360;
        if (dLon < -180) dLon += 360;
        double v = dLon / rLon;
        return 1.0 - (u * u + v * v);
    }

    /// <summary>Night-side city lights, 0–1. Land in populated latitudes only.</summary>
    public static float EarthCityLights(double wx, double wy, double wz)
    {
        double lat = Math.Asin(Math.Clamp(wy, -1, 1)) * 180.0 / Math.PI;
        if (EarthLandField(wx, wy, wz) <= 0.05)
            return 0f;
        if (Math.Abs(lat) < 12 || lat > 60 || lat < -40)
            return 0f;
        float speckle = Hash01f(wx * 80, wy * 80, wz * 80, 77);
        if (speckle < 0.72f)
            return 0f;
        return Math.Clamp((speckle - 0.72f) * 3.2f, 0f, 1f);
    }

    private static float Hash01f(double x, double y, double z, int seed)
    {
        int n = unchecked(
            (int)(x * 374761393) + (int)(y * 668265263) + (int)(z * 1274126177) + seed * 1103515245);
        n = (n ^ (n >> 13)) * 1274126177;
        n ^= n >> 16;
        return (n & 0x7fffffff) / (float)0x7fffffff;
    }

    private static BodyRgb SampleMars(BodyVisualState p, double wx, double wy, double wz)
    {
        double n = 0.5 + 0.5 * Fbm(wx * 2.4, wy * 2.4, wz * 2.4, p.Seed);
        double dark = 0.5 + 0.5 * Fbm(wx * 1.5, wy * 0.9, wz * 1.5, p.Seed + 9);
        var rust = BodyRgb.FromHex("#c24a22").Mix(BodyRgb.FromHex("#e2a070"), n);
        if (dark > 0.62)
            rust = rust.Mix(BodyRgb.FromHex("#6a3824"), (dark - 0.62) * 2.0);
        double ice = Smoothstep(0.86, 0.96, Math.Abs(wy));
        return rust.Mix(new BodyRgb(236, 240, 245), ice);
    }

    private static BodyRgb SampleVenus(BodyVisualState p, double wx, double wy, double wz)
    {
        double n = 0.5 + 0.5 * Fbm(wx * 2.1, wy * 1.6, wz * 2.1, p.Seed);
        n += Fbm(wx * 7.0, wy * 5.0, wz * 7.0, p.Seed + 3) * 0.08;
        return BodyRgb.FromHex("#e4d09a").Mix(BodyRgb.FromHex("#f4ead4"), Math.Clamp(n, 0, 1));
    }

    private static BodyRgb SampleMercury(BodyVisualState p, double wx, double wy, double wz)
    {
        double n = 0.5 + 0.5 * Fbm(wx * 3.4, wy * 3.4, wz * 3.4, p.Seed);
        return BodyRgb.FromHex("#5a5652").Mix(BodyRgb.FromHex("#9a948c"), n);
    }

    private static BodyRgb SampleBandedGiant(
        BodyVisualState p, double wx, double wy, double wz,
        BodyRgb[] palette, double bandFreq, double turbAmt, double contrast)
    {
        double lat = Math.Asin(Math.Clamp(wy, -1, 1));
        double turb = Fbm(wx * 3.6, wy * 0.45, wz * 3.6, p.Seed) * turbAmt;
        double t = 0.5 + 0.5 * Math.Sin(lat * bandFreq + turb);
        t = Math.Clamp(0.5 + (t - 0.5) * contrast, 0, 0.999);
        double fine = 0.5 + 0.5 * Fbm(wx * 10.0, wy * 1.1, wz * 10.0, p.Seed + 11);
        t = Math.Clamp(t * 0.86 + fine * 0.14, 0, 0.999);
        double scaled = t * (palette.Length - 1);
        int i0 = (int)Math.Floor(scaled);
        int i1 = Math.Min(palette.Length - 1, i0 + 1);
        return palette[i0].Mix(palette[i1], scaled - i0);
    }

    private static BodyRgb SampleJupiter(BodyVisualState p, double wx, double wy, double wz)
    {
        var palette = new[]
        {
            BodyRgb.FromHex("#f0e2c4"),
            BodyRgb.FromHex("#d2a878"),
            BodyRgb.FromHex("#8a4a28"),
            BodyRgb.FromHex("#e8d0a0"),
            BodyRgb.FromHex("#b06a3a")
        };
        var col = SampleBandedGiant(p, wx, wy, wz, palette, bandFreq: 16.0, turbAmt: 0.42, contrast: 1.35);
        // Great Red Spot ~22°S
        double sx = 0.72, sy = -0.38, sz = 0.22;
        double sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        sx /= sl; sy /= sl; sz /= sl;
        double spot = wx * sx + wy * sy + wz * sz;
        if (spot > 0.93)
        {
            double k = Smoothstep(0.93, 0.985, spot);
            col = col.Mix(BodyRgb.FromHex("#c44a28"), k * 0.85);
        }
        return col;
    }

    private static BodyRgb SampleSaturn(BodyVisualState p, double wx, double wy, double wz)
    {
        var palette = new[]
        {
            BodyRgb.FromHex("#f2e6c4"),
            BodyRgb.FromHex("#e0cc98"),
            BodyRgb.FromHex("#c8b078"),
            BodyRgb.FromHex("#ead8a8")
        };
        return SampleBandedGiant(p, wx, wy, wz, palette, bandFreq: 11.0, turbAmt: 0.18, contrast: 0.85);
    }

    private static BodyRgb SampleUranus(BodyVisualState p, double wx, double wy, double wz)
    {
        double n = 0.5 + 0.5 * Fbm(wx * 1.8, wy * 1.2, wz * 1.8, p.Seed);
        return BodyRgb.FromHex("#9fd9d0").Mix(BodyRgb.FromHex("#c5ebe4"), n * 0.35);
    }

    private static BodyRgb SampleNeptune(BodyVisualState p, double wx, double wy, double wz)
    {
        var palette = new[]
        {
            BodyRgb.FromHex("#163a92"),
            BodyRgb.FromHex("#2a62c8"),
            BodyRgb.FromHex("#1a4aa8"),
            BodyRgb.FromHex("#3a7ad4")
        };
        var col = SampleBandedGiant(p, wx, wy, wz, palette, bandFreq: 8.0, turbAmt: 0.22, contrast: 0.9);
        double streak = 0.5 + 0.5 * Fbm(wx * 14.0, wy * 0.7, wz * 14.0, p.Seed + 19);
        if (streak > 0.72)
            col = col.Mix(BodyRgb.FromHex("#d8eefe"), (streak - 0.72) * 1.6);
        double sx = -0.55, sy = -0.42, sz = 0.72;
        double sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        double spot = wx * (sx / sl) + wy * (sy / sl) + wz * (sz / sl);
        if (spot > 0.94)
            col = col.Mix(BodyRgb.FromHex("#0c2870"), Smoothstep(0.94, 0.985, spot) * 0.8);
        return col;
    }

    private static double CloudDensity(BodyVisualState p, double wx, double wy, double wz, double cloudCut)
    {
        if (IsSol(p, "earth"))
            return EarthCloudDensity(p, wx, wy, wz);

        double cn = 0.5 + 0.5 * Fbm(wx * 3.6, wy * 4.4, wz * 3.6, p.Seed + 91);
        cn += Fbm(wx * 11.0, wy * 12.0, wz * 11.0, p.Seed + 97) * 0.12;
        if (cn <= cloudCut)
            return 0;
        return Math.Clamp((cn - cloudCut) / Math.Max(0.08, 1.0 - cloudCut), 0, 1);
    }

    private static double EarthCloudDensity(BodyVisualState p, double wx, double wy, double wz)
    {
        double lat = Math.Asin(Math.Clamp(wy, -1, 1)) * 180.0 / Math.PI;
        double lon = Math.Atan2(wz, wx) * 180.0 / Math.PI;
        double dens;
        if (EarthBlueMarble.TrySampleCloud(lat, lon, out var mapped))
            dens = mapped;
        else
        {
            double n = 0.5 + 0.5 * Fbm(wx * 2.4, wy * 3.6, wz * 2.4, p.Seed + 91);
            n += Fbm(wx * 9.0, wy * 11.0, wz * 9.0, p.Seed + 97) * 0.18;
            dens = Smoothstep(0.38, 0.72, n);
        }

        double dLat = lat - 26;
        double dLon = lon + 72;
        if (dLon > 180) dLon -= 360;
        if (dLon < -180) dLon += 360;
        double r = Math.Sqrt(dLat * dLat + dLon * dLon);
        if (r < 16)
        {
            double ang = Math.Atan2(dLat, dLon);
            double spiral = 0.5 + 0.5 * Math.Sin(ang * 3.2 + r * 0.85);
            double band = Smoothstep(16, 3.5, r) * (1.0 - Smoothstep(0.0, 1.6, r));
            dens = Math.Max(dens, spiral * band * 0.95);
            if (r < 1.35)
                dens *= 0.12; // eye
        }

        return Math.Clamp(dens, 0, 1);
    }

    private static BodyRgb ApplyAnomalies(
        BodyRgb col,
        BodyVisualState p,
        double wx, double wy, double wz,
        (double x, double y, double z, double cosR)[] anomalies)
    {
        for (int i = 0; i < anomalies.Length; i++)
        {
            var a = anomalies[i];
            if (wx * a.x + wy * a.y + wz * a.z > a.cosR)
                col = col.Mix(p.GlowColor, 0.55);
        }
        return col;
    }

    private static BodyRgb ApplyThermalAlbedo(BodyRgb col, BodyVisualState p, double wx, double wy, double wz)
    {
        int magnitude = Math.Clamp(Math.Abs(p.ThermalGlow), 0, 100);
        if (magnitude < 8)
            return col;
        // Heat is a live dayside emissive in BodyGlobeDrawer, not a baked orange wash
        // (that made Mercury look like lava). Cold still tints the albedo.
        if (p.ThermalGlow > 0)
            return col;
        double strength = magnitude / 100.0;
        var tint = BodyRgb.FromHex("#5a9fff").Mix(BodyRgb.FromHex("#f4fbff"), strength);
        double mottled = 0.55 + 0.45 * (0.5 + 0.5 * Fbm(wx * 2.8, wy * 2.8, wz * 2.8, p.Seed + 3));
        return col.Mix(tint, strength * mottled * 0.48);
    }

    private static void DrawPlanetRings(RgbaImage canvas, BodyVisualState p, double cx, double cy, double rad, bool back)
    {
        double rot = p.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rot);
        double sin = Math.Sin(rot);
        int ringCount = 2 + p.Rings / 35;
        int max = (int)Math.Ceiling(rad * (1.55 + ringCount * 0.18)) + 2;
        double rad2 = rad * rad;

        for (int iy = -max; iy <= max; iy++)
        {
            for (int ix = -max; ix <= max; ix++)
            {
                double lx = ix * cos + iy * sin;
                double ly = -ix * sin + iy * cos;
                bool isBack = ly < 0;
                if (isBack != back)
                    continue;

                bool insideDisk = ix * ix + iy * iy < rad2;
                if (back && insideDisk)
                    continue;

                for (int i = 0; i < ringCount; i++)
                {
                    double rx = rad * (1.32 + i * 0.16);
                    double ry = rad * (0.26 + i * 0.035);
                    double d = (lx * lx) / (rx * rx) + (ly * ly) / (ry * ry);
                    if (d < 0.93 || d > 1.07)
                        continue;
                    double dens = 1.0 - Math.Abs(d - 1.0) / 0.07;
                    byte a = (byte)Math.Clamp(dens * 165, 0, 180);
                    Blend(canvas, (int)cx + ix, (int)cy + iy, p.Secondary.R, p.Secondary.G, p.Secondary.B, a);
                    break;
                }
            }
        }
    }

    private static (double x, double y, double z, double cosR)[] BuildSphereFeatures(
        Func<double> rng, int count, double minAng, double maxAng)
    {
        if (count <= 0)
            return Array.Empty<(double, double, double, double)>();
        var list = new (double x, double y, double z, double cosR)[count];
        for (int i = 0; i < count; i++)
        {
            double z = rng() * 2 - 1;
            double t = rng() * Math.PI * 2;
            double rr = Math.Sqrt(Math.Max(0, 1 - z * z));
            double ang = minAng + rng() * (maxAng - minAng);
            list[i] = (rr * Math.Cos(t), rr * Math.Sin(t), z, Math.Cos(ang));
        }
        return list;
    }

    private static double Fbm(double x, double y, double z, int seed)
    {
        double n = 0;
        double a = 1;
        double f = 1.35;
        double s = 0;
        for (int o = 0; o < 4; o++)
        {
            n += a * ValueNoise3(x * f, y * f, z * f, seed + o * 19);
            s += a;
            a *= 0.5;
            f *= 2.05;
        }
        return n / s;
    }

    /// <summary>Trilinear value noise — much less "plaid" than sin/cos product noise when zoomed.</summary>
    private static double ValueNoise3(double x, double y, double z, int seed)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int z0 = (int)Math.Floor(z);
        double fx = x - x0;
        double fy = y - y0;
        double fz = z - z0;
        double ux = fx * fx * (3.0 - 2.0 * fx);
        double uy = fy * fy * (3.0 - 2.0 * fy);
        double uz = fz * fz * (3.0 - 2.0 * fz);

        double n000 = HashCorner(x0, y0, z0, seed);
        double n100 = HashCorner(x0 + 1, y0, z0, seed);
        double n010 = HashCorner(x0, y0 + 1, z0, seed);
        double n110 = HashCorner(x0 + 1, y0 + 1, z0, seed);
        double n001 = HashCorner(x0, y0, z0 + 1, seed);
        double n101 = HashCorner(x0 + 1, y0, z0 + 1, seed);
        double n011 = HashCorner(x0, y0 + 1, z0 + 1, seed);
        double n111 = HashCorner(x0 + 1, y0 + 1, z0 + 1, seed);

        double nx00 = n000 + (n100 - n000) * ux;
        double nx10 = n010 + (n110 - n010) * ux;
        double nx01 = n001 + (n101 - n001) * ux;
        double nx11 = n011 + (n111 - n011) * ux;
        double nxy0 = nx00 + (nx10 - nx00) * uy;
        double nxy1 = nx01 + (nx11 - nx01) * uy;
        return nxy0 + (nxy1 - nxy0) * uz;
    }

    private static double HashCorner(int x, int y, int z, int seed)
    {
        unchecked
        {
            int n = x * 374761393 + y * 668265263 + z * 1274126177 + seed * 1103515245;
            n = (n ^ (n >> 13)) * 1274126177;
            n ^= n >> 16;
            return (n & 0x7fffffff) / (double)0x7fffffff * 2.0 - 1.0;
        }
    }

    private static void DrawAsteroid(RgbaImage canvas, BodyVisualState p, Func<double> r, bool comet)
    {
        double cx = CanvasSize * 0.5;
        double cy = CanvasSize * 0.5;
        // Fill most of the icon box so rocks read at map-min size, unlike a tiny centered pebble.
        double rad = CanvasSize * 0.38;
        double rot = ((p.Seed * 0.013) % (Math.PI * 2));
        if (p.Rotation != 0)
            rot = p.Rotation * Math.PI / 180.0;
        double aspect = 0.38 + r() * 0.28;
        int n = 8 + (int)(r() * 5); // 8–12, lumpy not circular
        var pts = new (double x, double y)[n];
        for (int i = 0; i < n; i++)
        {
            double a = (i / (double)n) * Math.PI * 2 + (r() - 0.5) * 0.35;
            double rr = rad * (0.28 + r() * 0.82);
            if (r() < 0.22)
                rr *= 0.42; // concave bite
            else if (r() < 0.12)
                rr *= 1.22; // lobe
            double x = Math.Cos(a) * rr;
            double y = Math.Sin(a) * rr * aspect;
            pts[i] = (x * Math.Cos(rot) - y * Math.Sin(rot), x * Math.Sin(rot) + y * Math.Cos(rot));
        }

        // Optional second lobe (contact-binary look) for some seeds.
        (double x, double y)[]? lobe = null;
        if (!comet && r() < 0.35)
        {
            int n2 = 7 + (int)(r() * 3);
            lobe = new (double x, double y)[n2];
            double ox = rad * (0.35 + r() * 0.25) * (r() < 0.5 ? 1 : -1);
            double oy = rad * (r() - 0.5) * 0.35;
            double rad2 = rad * (0.32 + r() * 0.28);
            for (int i = 0; i < n2; i++)
            {
                double a = i / (double)n2 * Math.PI * 2 + (r() - 0.5) * 0.4;
                double rr = rad2 * (0.35 + r() * 0.75);
                lobe[i] = (ox + Math.Cos(a) * rr, oy + Math.Sin(a) * rr * (0.5 + r() * 0.3));
            }
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        void Bound((double x, double y)[] poly)
        {
            foreach (var (x, y) in poly)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        Bound(pts);
        if (lobe != null)
            Bound(lobe);

        // Linear (not radial) shade so it reads as a facet, not a globe.
        double lx = Math.Cos(p.Light / 100.0 * 1.2);
        double ly = Math.Sin(p.Light / 100.0 * 1.2);
        var dark = p.Primary.Mix(new BodyRgb(18, 18, 22), 0.55);
        var hi = p.Primary.Mix(p.Secondary, 0.45);

        for (int py = (int)Math.Floor(minY); py <= (int)Math.Ceiling(maxY); py++)
            for (int px = (int)Math.Floor(minX); px <= (int)Math.Ceiling(maxX); px++)
            {
                bool inside = PointInPoly(px + 0.5, py + 0.5, pts)
                              || (lobe != null && PointInPoly(px + 0.5, py + 0.5, lobe));
                if (!inside)
                    continue;
                double shade = 0.45 + 0.55 * Math.Clamp(0.5 + (px * lx + py * ly) / (rad * 1.6), 0, 1);
                var col = dark.Mix(hi, shade);
                canvas.SetPixel((int)cx + px, (int)cy + py, col.R, col.G, col.B, 255);
            }

        // Angular chips, not round crater ellipses.
        int chips = 4 + (int)(r() * 5);
        for (int i = 0; i < chips; i++)
        {
            double ox = (r() - 0.5) * rad * 1.1;
            double oy = (r() - 0.5) * rad * aspect * 1.1;
            int cn = 4 + (int)(r() * 3);
            var chip = new (double x, double y)[cn];
            double cr = 3 + r() * 11;
            for (int k = 0; k < cn; k++)
            {
                double a = k / (double)cn * Math.PI * 2 + r();
                double rr = cr * (0.4 + r() * 0.8);
                chip[k] = (ox + Math.Cos(a) * rr, oy + Math.Sin(a) * rr * 0.7);
            }
            var chipCol = p.Secondary.Mix(new BodyRgb(22, 22, 26), 0.4);
            for (int py = (int)(oy - cr - 2); py <= (int)(oy + cr + 2); py++)
                for (int px = (int)(ox - cr - 2); px <= (int)(ox + cr + 2); px++)
                {
                    if (!PointInPoly(px + 0.5, py + 0.5, chip))
                        continue;
                    if (!PointInPoly(px + 0.5, py + 0.5, pts)
                        && (lobe == null || !PointInPoly(px + 0.5, py + 0.5, lobe)))
                        continue;
                    canvas.SetPixel((int)cx + px, (int)cy + py, chipCol.R, chipCol.G, chipCol.B, 255);
                }
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
        double scale = CanvasSize / RefCanvas;
        double cx = CanvasSize * 0.5, cy = CanvasSize * 0.5 + 4 * scale, rad = p.Size * 0.42 * scale;
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
