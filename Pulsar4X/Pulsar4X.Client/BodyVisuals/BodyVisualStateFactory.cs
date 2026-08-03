using System;
using System.Linq;
using System.Text;
using Pulsar4X.Api;
using Pulsar4X.Client.ShipVisuals;
using Pulsar4X.Orbital;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>
/// Maps snapshot science into visual state. Unsurveyed bodies share type defaults;
/// surveyed bodies get individual looks from temp, HZ distance, atmosphere and minerals.
/// </summary>
public static class BodyVisualStateFactory
{
    public static BodyVisualState FromEntity(EntitySnapshot entity, IClientSystem system)
    {
        if (entity.GetView<StarView>() is { } star)
        {
            string? starName = entity.GetView<NameView>()?.Name;
            if (SolBodyPresets.TryGet(starName, out var solStar))
                return solStar;
            return FromStar(entity, star);
        }

        var body = entity.GetView<BodyView>();
        byte typeId = body?.BodyTypeId ?? 0;
        var geo = entity.GetView<GeoSurveyView>();
        // Fog of war: surveyable bodies stay on a shared type default until geo survey completes.
        // Sol presets / individual looks only after survey — otherwise Earth/Mars/Mercury were
        // recognizable before any ship arrived.
        if (geo != null && !geo.IsSurveyComplete)
            return TypeDefault(entity.Kind, typeId);

        string? name = entity.GetView<NameView>()?.Name;
        SolBodyPresets.TryGet(name, out var solHint);

        // Surveyed (or not surveyable): measured values + optional Sol authored look.
        return FromBodyData(entity, system, body, typeId, solHint);
    }

    public static BodyVisualState TypeDefault(BodyKind kind, byte bodyTypeId)
    {
        BodyVisualType visual = MapType(kind, bodyTypeId, tempC: 15, forceLavaIce: false);
        // Fog-of-war: featureless grey disk — no oceans/clouds/craters that leak identity.
        int size = visual switch
        {
            BodyVisualType.Gas => 115,
            BodyVisualType.Moon or BodyVisualType.Asteroid => 62,
            BodyVisualType.Comet => 52,
            _ => 90
        };
        var grey = BodyRgb.FromHex("#6a6e74");
        var greyHi = BodyRgb.FromHex("#8a9098");
        return new BodyVisualState
        {
            Type = BodyVisualType.Moon, // flat rock path (no ocean/gas bands)
            Seed = unchecked((int)ShipVisualRng.Hash("unsurveyed:" + visual)),
            Size = size,
            Rotation = 0,
            Light = -15,
            Water = 0,
            Clouds = 0,
            Atmo = 0,
            Craters = 0,
            Rings = 0,
            Anomalies = 0,
            Variance = 4,
            Primary = grey,
            Secondary = greyHi,
            AtmoColor = grey,
            GlowColor = greyHi,
            Shadow = true,
            Glow = false,
            ThermalGlow = 0,
            ExtremeHeatRing = false,
            // Bump key so old colorful defaults are not reused from cache.
            CacheKey = "unsurveyed:grey:" + visual
        };
    }

    private static BodyVisualState FromStar(EntitySnapshot entity, StarView star)
    {
        var color = TemperatureToRgb(star.SurfaceTemperatureC);
        int size = 110;
        if (entity.GetView<MassVolumeView>() is { RadiusMetres: > 0 } mv)
        {
            double au = Distance.MToAU(mv.RadiusMetres);
            size = (int)Math.Clamp(70 + Math.Log10(Math.Max(1e-4, au)) * 40, 70, 160);
        }

        return new BodyVisualState
        {
            Type = BodyVisualType.Star,
            Seed = unchecked((int)ShipVisualRng.Hash("star:" + star.SpectralType + star.SpectralSubDivision)),
            Size = size,
            Primary = color,
            Secondary = color.Mix(new BodyRgb(255, 240, 200), 0.4),
            AtmoColor = color,
            GlowColor = color.Mix(new BodyRgb(255, 255, 255), 0.5),
            Atmo = 100,
            Glow = true,
            Shadow = false,
            ExtremeHeatRing = true,
            CacheKey = $"star:{star.SpectralType}:{star.SpectralSubDivision}:{star.SpectralTypeIndex}"
        };
    }

    private static BodyVisualState FromBodyData(
        EntitySnapshot entity,
        IClientSystem system,
        BodyView? body,
        byte typeId,
        BodyVisualState? solHint)
    {
        var atmo = entity.GetView<AtmosphereView>();
        double tempC = atmo?.SurfaceTemperatureC ?? body?.SurfaceTemperatureC ?? 0;

        var (star, heliocentricAu) = ResolvePrimaryStar(entity, system);
        // Legacy bug: moon→planet SMA used as solar distance → thousands of °C.
        // Recompute a rough equilibrium temp when stored value is nonsense.
        if (heliocentricAu > 0.05 && (tempC > 400 || tempC < -250))
        {
            double albedoFix = body?.Albedo ?? 0.2;
            // ~278 K at 1 AU for A=0, scale by 1/√au and (1−A)^¼
            double tK = 278.0 / Math.Sqrt(heliocentricAu) * Math.Pow(Math.Max(0.05, 1.0 - albedoFix), 0.25);
            tempC = tK - 273.15;
        }
        ApplyHabitableZone(ref tempC, star, heliocentricAu);

        // Measured scores 0–100. HydrosphereExtent may be percent (71) or fraction (0.71).
        int water = 0;
        if (atmo != null && atmo.Hydrosphere)
        {
            double he = atmo.HydrosphereExtentPercent;
            if (he > 0 && he <= 1.0)
                he *= 100.0;
            water = (int)Math.Clamp(he, 0, 100);
        }
        if (solHint != null)
            water = Math.Max(water, solHint.Water);

        double pressureAtm = atmo?.PressureAtm ?? 0;
        int atmoScore = (int)Math.Clamp(pressureAtm * 100.0, 0, 100); // 1 atm → 100
        int dustScore = body != null ? (int)Math.Clamp(body.AtmosphericDust * 100.0, 0, 100) : 0;
        // More atmosphere ⇒ more clouds
        int clouds = (int)Math.Clamp(atmoScore * 0.85 + dustScore * 0.35, 0, 100);
        int atmoRim = (int)Math.Clamp(atmoScore * 0.9, 0, 100);

        var visual = MapType(entity.Kind, typeId, tempC, forceLavaIce: true);
        // Sol authored type wins (Mars terrestrial, Io volcanic, Europa ice, Titan haze…).
        if (solHint != null && solHint.Type is not BodyVisualType.Star)
            visual = solHint.Type;

        int rockScore = Math.Max(0, 100 - water);
        bool waterDominant = water >= 40 && water >= rockScore * 0.55;
        // Sol presets lock type/look — don't let HZ heuristics recolor Mars as ice, etc.
        bool lavaDominant = visual == BodyVisualType.Lava
            || (solHint == null && tempC > 700);
        bool iceDominant = visual == BodyVisualType.Ice
            || (solHint == null && (tempC < -100
                || (star != null && heliocentricAu > star.MaxHabitableRadiusAu * 1.3)));

        if (solHint == null)
        {
            if (lavaDominant) visual = BodyVisualType.Lava;
            else if (iceDominant && visual is BodyVisualType.Terrestrial or BodyVisualType.Moon)
                visual = BodyVisualType.Ice;
            else if (waterDominant) visual = BodyVisualType.Terrestrial;
        }
        else if (waterDominant && solHint.Type == BodyVisualType.Terrestrial)
        {
            visual = BodyVisualType.Terrestrial;
        }

        var preset = solHint ?? Preset(visual);
        var primary = preset.Primary;
        var secondary = preset.Secondary;
        var atmoColor = preset.AtmoColor;
        var glow = preset.GlowColor;
        int craters = preset.Craters;
        int rings = typeId is 2 or 3 ? Math.Max(preset.Rings, 20) : preset.Rings;
        int anomalies = preset.Anomalies;

        if (waterDominant)
        {
            primary = BodyRgb.FromHex("#1560a8");
            secondary = solHint?.Secondary ?? BodyRgb.FromHex("#3d8c4a");
            atmoColor = BodyRgb.FromHex("#7ec8ff");
        }
        else if (lavaDominant || visual == BodyVisualType.Lava)
        {
            primary = solHint?.Primary ?? BodyRgb.FromHex("#651712");
            secondary = solHint?.Secondary ?? BodyRgb.FromHex("#d94b17");
            glow = solHint?.GlowColor ?? BodyRgb.FromHex("#ffd54a");
            water = 0;
        }
        else if (iceDominant || visual == BodyVisualType.Ice)
        {
            primary = solHint?.Primary ?? BodyRgb.FromHex("#4d8eae");
            secondary = solHint?.Secondary ?? BodyRgb.FromHex("#d8f3ff");
            atmoColor = solHint?.AtmoColor ?? BodyRgb.FromHex("#9eeaff");
        }
        else if (visual == BodyVisualType.Gas)
        {
            clouds = Math.Max(clouds, 25);
            atmoRim = Math.Max(atmoRim, 80);
        }
        else if (visual == BodyVisualType.Asteroid || entity.Kind == BodyKind.Asteroid)
        {
            primary = solHint?.Primary ?? BodyRgb.FromHex("#565c65");
            secondary = solHint?.Secondary ?? BodyRgb.FromHex("#9aa1aa");
            clouds = Math.Min(clouds, 5);
        }

        // Rocky surface tint. NEVER grey-wash Sol presets (that made Mars look like Luna).
        // - Sol hint → keep authored colors (Mars red, Luna grey, Venus ochre…)
        // - Dust → iron-oxide rust (procedural Mars-likes)
        // - Else albedo → neutral regolith grey for unknown rocks
        double albedo = body?.Albedo ?? 0;
        bool dustySurface = dustScore >= 25;
        ApplyRockySurfaceColors(solHint, waterDominant, lavaDominant, iceDominant || visual == BodyVisualType.Ice,
            visual, albedo, dustySurface, ref primary, ref secondary);

        // Ore deposits tint lightly; iron oxide rust only when the surface is dusty.
        if (!waterDominant)
            ApplyMinerals(entity.GetView<MineralDepositsView>(), ref primary, ref secondary, ref water, ref anomalies, ref glow, ref visual, dustySurface);
        else
        {
            var land = secondary;
            int wKeep = water;
            ApplyMinerals(entity.GetView<MineralDepositsView>(), ref land, ref secondary, ref wKeep, ref anomalies, ref glow, ref visual, dustySurface: false);
            secondary = land;
            anomalies = Math.Min(anomalies, 8);
            glow = solHint?.GlowColor ?? BodyRgb.FromHex("#ffd54a");
        }

        if (star != null && heliocentricAu > 0 && heliocentricAu < star.MinHabitableRadiusAu * 0.7 && !waterDominant)
        {
            water = Math.Min(water, 10);
            glow = BodyRgb.FromHex("#ff7b31");
        }

        int size = solHint?.Size ?? 95;
        if (entity.GetView<MassVolumeView>() is { RadiusMetres: > 0 } mv)
        {
            double logR = Math.Log10(Math.Max(1.0, mv.RadiusMetres));
            size = (int)Math.Clamp(45 + logR * 12, 40, 170);
        }

        var fingerprint = MineralFingerprint(entity.GetView<MineralDepositsView>());
        string nameKey = entity.GetView<NameView>()?.Name ?? entity.Id.ToString();
        int seed = solHint?.Seed
            ?? unchecked((int)ShipVisualRng.Hash($"{entity.Id}|{typeId}|{tempC:0}|{water}|{atmoScore}|{fingerprint}"));

        // Surface thermal glow (texture emission), not an outer halo.
        // Neutral band ~±50 °C; Mercury ~160 °C should read clearly orange.
        int thermalGlow = 0;
        if (visual == BodyVisualType.Lava)
            thermalGlow = 90;
        else if (visual == BodyVisualType.Ice || iceDominant)
            thermalGlow = Math.Min(-45, thermalGlow);
        else if (!waterDominant)
        {
            if (tempC >= 80)
                thermalGlow = (int)Math.Clamp(25 + (tempC - 80) * 0.65, 25, 100);
            else if (tempC <= -50)
                thermalGlow = -(int)Math.Clamp(25 + (-50 - tempC) * 0.45, 25, 100);
        }

        if (thermalGlow > 0)
        {
            double heat = thermalGlow / 100.0;
            glow = BodyRgb.FromHex("#ff4a08").Mix(BodyRgb.FromHex("#ffe566"), heat);
            if (visual is BodyVisualType.Moon or BodyVisualType.Terrestrial or BodyVisualType.Asteroid)
            {
                primary = primary.Mix(BodyRgb.FromHex("#c45a20"), heat * 0.55);
                secondary = secondary.Mix(BodyRgb.FromHex("#e8a040"), heat * 0.65);
            }
        }
        else if (thermalGlow < 0)
        {
            double cold = -thermalGlow / 100.0;
            glow = BodyRgb.FromHex("#6eb0ff").Mix(BodyRgb.FromHex("#f2f8ff"), cold);
            if (visual is BodyVisualType.Moon or BodyVisualType.Terrestrial or BodyVisualType.Ice or BodyVisualType.Asteroid)
            {
                primary = primary.Mix(BodyRgb.FromHex("#8ec8e8"), cold * 0.45);
                secondary = secondary.Mix(BodyRgb.FromHex("#e8f4ff"), cold * 0.55);
            }
        }

        bool doGlow = visual is BodyVisualType.Lava
            || anomalies > 20
            || pressureAtm > 5
            || thermalGlow != 0
            || (solHint?.Glow ?? false);

        return new BodyVisualState
        {
            Type = visual,
            Seed = seed < 0 ? seed & int.MaxValue : seed,
            Size = size,
            Rotation = solHint?.Rotation ?? ((seed % 360 + 360) % 360),
            Light = solHint?.Light ?? (-20 + (seed % 40) - 20),
            Water = water,
            Clouds = clouds,
            Atmo = atmoRim,
            Craters = waterDominant ? Math.Min(craters, 8) : craters,
            Rings = rings,
            Anomalies = anomalies,
            Variance = waterDominant ? 10 : 18,
            Primary = primary,
            Secondary = secondary,
            AtmoColor = atmoColor,
            GlowColor = glow,
            Shadow = true,
            Glow = doGlow,
            ThermalGlow = thermalGlow,
            ExtremeHeatRing = tempC > 2000,
            CacheKey = $"body:{nameKey}:{water}:{clouds}:{atmoScore}:{tempC:0}:{albedo:0.###}:{fingerprint}:tg{thermalGlow}:xh{(tempC > 2000 ? 1 : 0)}"
        };
    }

    /// <summary>Walk parent chain to the first star; estimate heliocentric AU from SMA sum or absolute positions.</summary>
    public static (StarView? star, double heliocentricAu) ResolvePrimaryStar(EntitySnapshot entity, IClientSystem system)
    {
        EntitySnapshot? current = entity;
        double smaSumM = 0;
        StarView? star = null;
        EntitySnapshot? starEntity = null;
        int guard = 0;

        while (current != null && guard++ < 12)
        {
            if (current.GetView<StarView>() is { } sv)
            {
                star = sv;
                starEntity = current;
                break;
            }

            var orbit = current.GetView<OrbitView>();
            if (orbit != null && orbit.SemiMajorAxisM > 0)
                smaSumM += orbit.SemiMajorAxisM;

            int? parentId = orbit?.ParentId ?? current.GetView<PositionView>()?.ParentId;
            current = parentId is int id ? system.GetEntity(id) : null;
        }

        double au = smaSumM > 0 ? Distance.MToAU(smaSumM) : 0;
        if (starEntity != null && entity.GetView<PositionView>() is { } pos && starEntity.GetView<PositionView>() is { } starPos)
        {
            double dx = pos.AbsolutePosition.X - starPos.AbsolutePosition.X;
            double dy = pos.AbsolutePosition.Y - starPos.AbsolutePosition.Y;
            double dz = pos.AbsolutePosition.Z - starPos.AbsolutePosition.Z;
            double distM = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distM > 0)
                au = Distance.MToAU(distM);
        }

        return (star, au);
    }

    private static void ApplyHabitableZone(ref double tempC, StarView? star, double heliocentricAu)
    {
        if (star == null || heliocentricAu <= 0)
            return;
        // Ignore bogus sub-0.05 AU readings (moon SMA mistaken for heliocentric).
        if (heliocentricAu < 0.05)
            return;
        if (heliocentricAu < star.MinHabitableRadiusAu * 0.5)
            tempC = Math.Max(tempC, 800);
        else if (heliocentricAu > star.MaxHabitableRadiusAu * 1.5)
            tempC = Math.Min(tempC, -120);
    }

    private static BodyVisualType MapType(BodyKind kind, byte bodyTypeId, double tempC, bool forceLavaIce)
    {
        if (kind == BodyKind.Asteroid) return BodyVisualType.Asteroid;
        if (kind == BodyKind.Comet) return BodyVisualType.Comet;
        if (kind == BodyKind.Moon) return BodyVisualType.Moon;

        // Engine BodyType enum ordinals
        BodyVisualType fromId = bodyTypeId switch
        {
            2 => BodyVisualType.Gas,      // GasGiant
            3 => BodyVisualType.Ice,      // IceGiant
            5 => BodyVisualType.Gas,      // GasDwarf
            6 => BodyVisualType.Moon,     // Moon
            7 => BodyVisualType.Asteroid,
            8 => BodyVisualType.Comet,
            _ => BodyVisualType.Terrestrial
        };

        if (forceLavaIce && fromId == BodyVisualType.Terrestrial)
        {
            if (tempC > 700) return BodyVisualType.Lava;
            if (tempC < -100) return BodyVisualType.Ice;
        }

        return fromId;
    }

    private static BodyVisualState Preset(BodyVisualType type) => type switch
    {
        BodyVisualType.Gas => new BodyVisualState
        {
            Type = type, Size = 120, Water = 0, Clouds = 25, Atmo = 80, Craters = 0, Rings = 68,
            Primary = BodyRgb.FromHex("#8066a2"), Secondary = BodyRgb.FromHex("#d4a879"),
            AtmoColor = BodyRgb.FromHex("#b9a4ff"), GlowColor = BodyRgb.FromHex("#ffd54a")
        },
        BodyVisualType.Ice => new BodyVisualState
        {
            Type = type, Size = 95, Water = 70, Clouds = 40, Atmo = 45, Craters = 26, Rings = 20,
            Primary = BodyRgb.FromHex("#4d8eae"), Secondary = BodyRgb.FromHex("#d8f3ff"),
            AtmoColor = BodyRgb.FromHex("#9eeaff"), GlowColor = BodyRgb.FromHex("#dff7ff")
        },
        BodyVisualType.Lava => new BodyVisualState
        {
            Type = type, Size = 95, Water = 0, Clouds = 5, Atmo = 24, Craters = 42, Rings = 0, Anomalies = 12,
            Primary = BodyRgb.FromHex("#651712"), Secondary = BodyRgb.FromHex("#d94b17"),
            AtmoColor = BodyRgb.FromHex("#ff7b31"), GlowColor = BodyRgb.FromHex("#ffd54a")
        },
        BodyVisualType.Moon => new BodyVisualState
        {
            Type = type, Size = 70, Water = 0, Clouds = 0, Atmo = 5, Craters = 72, Rings = 0,
            Primary = BodyRgb.FromHex("#8a8e96"), Secondary = BodyRgb.FromHex("#c5c8ce"),
            AtmoColor = BodyRgb.FromHex("#aab0b8"), GlowColor = BodyRgb.FromHex("#ddd")
        },
        BodyVisualType.Asteroid => new BodyVisualState
        {
            Type = type, Size = 55, Water = 0, Clouds = 0, Atmo = 0, Craters = 72, Rings = 0,
            Primary = BodyRgb.FromHex("#565c65"), Secondary = BodyRgb.FromHex("#9aa1aa"),
            AtmoColor = BodyRgb.FromHex("#8899aa"), GlowColor = BodyRgb.FromHex("#ccc")
        },
        BodyVisualType.Comet => new BodyVisualState
        {
            Type = type, Size = 50, Water = 30, Clouds = 0, Atmo = 20, Craters = 40, Rings = 0,
            Primary = BodyRgb.FromHex("#6a7380"), Secondary = BodyRgb.FromHex("#cfe6f5"),
            AtmoColor = BodyRgb.FromHex("#50b4ff"), GlowColor = BodyRgb.FromHex("#a0e0ff")
        },
        BodyVisualType.Toxic => new BodyVisualState
        {
            Type = type, Size = 95, Water = 10, Clouds = 60, Atmo = 90, Craters = 15, Rings = 0,
            Primary = BodyRgb.FromHex("#4a6b2f"), Secondary = BodyRgb.FromHex("#9acd32"),
            AtmoColor = BodyRgb.FromHex("#b4ff68"), GlowColor = BodyRgb.FromHex("#d4ff8a")
        },
        _ => new BodyVisualState // Terrestrial / Earth-like default
        {
            Type = BodyVisualType.Terrestrial, Size = 95, Water = 60, Clouds = 52, Atmo = 72, Craters = 8, Rings = 0,
            Primary = BodyRgb.FromHex("#327da6"), Secondary = BodyRgb.FromHex("#74c6a4"),
            AtmoColor = BodyRgb.FromHex("#68d8ff"), GlowColor = BodyRgb.FromHex("#ffd54a")
        }
    };

    /// <summary>
    /// Rocky / dusty surface colors. Sol presets win over generic albedo grey
    /// (otherwise Mars/Venus become grey crater balls).
    /// </summary>
    public static void ApplyRockySurfaceColors(
        BodyVisualState? solHint,
        bool waterDominant,
        bool lavaDominant,
        bool iceDominant,
        BodyVisualType visual,
        double albedo,
        bool dustySurface,
        ref BodyRgb primary,
        ref BodyRgb secondary)
    {
        if (waterDominant || lavaDominant || iceDominant
            || visual is BodyVisualType.Gas or BodyVisualType.Ice)
            return;

        // Known Sol look stays (Mars red, Luna grey, Venus ochre…).
        if (solHint != null)
        {
            primary = solHint.Primary;
            secondary = solHint.Secondary;
            return;
        }

        if (!dustySurface && albedo <= 0.001)
            return;

        if (dustySurface)
        {
            primary = BodyRgb.FromHex("#a84a2f").Mix(BodyRgb.FromHex("#d4a07a"), Math.Clamp(albedo, 0, 1));
            secondary = BodyRgb.FromHex("#c47a4a");
            return;
        }

        // Unknown rock: bond albedo → neutral grey
        double t = Math.Clamp(albedo, 0.02, 0.98);
        byte g = (byte)Math.Clamp(38 + t * 200, 40, 235);
        byte g2 = (byte)Math.Clamp(g + 28, 50, 245);
        primary = new BodyRgb(g, g, (byte)Math.Clamp(g - 2, 0, 255));
        secondary = new BodyRgb(g2, g2, (byte)Math.Clamp(g2 - 1, 0, 255));
    }

    private static void ApplyMinerals(
        MineralDepositsView? minerals,
        ref BodyRgb primary, ref BodyRgb secondary, ref int water, ref int anomalies, ref BodyRgb glow,
        ref BodyVisualType visual,
        bool dustySurface = false)
    {
        if (minerals?.Deposits == null || minerals.Deposits.Count == 0)
            return;

        foreach (var row in minerals.Deposits.Where(d => d.Access != DepositAccess.None))
        {
            string name = row.Name ?? "";
            double weight = Math.Log10(Math.Max(10, row.Amount));
            if (name.Contains("Iron", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Nickel", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Chromium", StringComparison.OrdinalIgnoreCase))
            {
                // Subsurface ore ≠ surface paint. Strong rust only with dusty atmosphere (Mars).
                double rust = dustySurface ? Math.Min(0.45, weight * 0.05) : Math.Min(0.08, weight * 0.01);
                primary = primary.Mix(BodyRgb.FromHex("#9a5524"), rust);
                if (dustySurface)
                    secondary = secondary.Mix(BodyRgb.FromHex("#b43c3c"), 0.25);
            }
            else if (name.Contains("Copper", StringComparison.OrdinalIgnoreCase))
            {
                secondary = secondary.Mix(BodyRgb.FromHex("#2e8b57"), dustySurface ? 0.3 : 0.08);
            }
            else if (name.Contains("Water", StringComparison.OrdinalIgnoreCase))
            {
                water = Math.Min(100, water + (int)(weight * 4));
            }
            else if (name.Contains("Hydrocarbon", StringComparison.OrdinalIgnoreCase))
            {
                primary = primary.Mix(BodyRgb.FromHex("#3a3020"), 0.35);
                visual = visual == BodyVisualType.Terrestrial ? BodyVisualType.Toxic : visual;
            }
            else if (name.Contains("Fissionable", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Rare Earth", StringComparison.OrdinalIgnoreCase))
            {
                anomalies = Math.Min(100, anomalies + (int)(weight * 6));
                glow = BodyRgb.FromHex("#a0ff80");
            }
            else if (name.Contains("Silicon", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Regolith", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Graphite", StringComparison.OrdinalIgnoreCase))
            {
                primary = primary.Mix(BodyRgb.FromHex("#6e6f73"), 0.2);
            }
            else if (name.Contains("Titanium", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Tungsten", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Aluminium", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Aluminum", StringComparison.OrdinalIgnoreCase))
            {
                secondary = secondary.Mix(BodyRgb.FromHex("#b0b8c0"), 0.2);
            }
        }
    }

    private static string MineralFingerprint(MineralDepositsView? minerals)
    {
        if (minerals?.Deposits == null || minerals.Deposits.Count == 0)
            return "none";
        var sb = new StringBuilder();
        foreach (var d in minerals.Deposits.Where(x => x.Access != DepositAccess.None).OrderBy(x => x.MineralId))
            sb.Append(d.MineralId).Append(':').Append((int)d.Access).Append(':').Append(d.Amount).Append(';');
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    /// <summary>Planck-ish star color from surface temperature °C (same idea as StarIcon).</summary>
    public static BodyRgb TemperatureToRgb(double temperatureC)
    {
        double tempK = Math.Clamp(temperatureC + 273.15, 1000, 40000) / 100.0;
        byte rr, gg, bb;
        if (tempK <= 66) rr = 255;
        else rr = (byte)Math.Clamp(329.698727446 * Math.Pow(tempK - 60, -0.1332047592), 0, 255);

        if (tempK <= 66)
            gg = (byte)Math.Clamp(99.4708025861 * Math.Log(Math.Max(1e-3, tempK)) - 161.1195681661, 0, 255);
        else
            gg = (byte)Math.Clamp(288.1221695283 * Math.Pow(tempK - 60, -0.0755148492), 0, 255);

        if (tempK >= 66) bb = 255;
        else if (tempK <= 19) bb = 0;
        else bb = (byte)Math.Clamp(138.5177312231 * Math.Log(Math.Max(1e-3, tempK - 10)) - 305.0447927307, 0, 255);

        return new BodyRgb(rr, gg, bb);
    }
}
