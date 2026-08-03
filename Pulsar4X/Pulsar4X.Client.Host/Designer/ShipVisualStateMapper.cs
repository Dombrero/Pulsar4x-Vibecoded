using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pulsar4X.Blueprints;
using Pulsar4X.Client.ShipVisuals;
using Pulsar4X.Components;
using Pulsar4X.Movement;
using Pulsar4X.Sensors;
using Pulsar4X.Storage;
using Pulsar4X.Weapons;

namespace Pulsar4X.Client.Host;

/// <summary>Maps a ship design's components/armor into Aurora-style visual parameters.</summary>
public static class ShipVisualStateMapper
{
    public static ShipVisualState FromDesign(
        string designName,
        IReadOnlyList<(ComponentDesign design, int count)> components,
        ArmorBlueprint? armor,
        float armorThickness,
        double volumeM3,
        RgbColor? primaryOverride = null)
    {
        int engines = 0;
        int weapons = 0;
        int sensors = 0;
        int cargoUnits = 0;

        var seedBuilder = new StringBuilder(designName ?? "Ship");
        foreach (var (design, count) in components.OrderBy(c => c.design.UniqueID, StringComparer.Ordinal))
        {
            seedBuilder.Append('|').Append(design.UniqueID).Append('x').Append(count);

            if (design.HasAttribute<NewtonionThrustAtb>() || design.HasAttribute<WarpDriveAtb>())
                engines += count;

            if (design.HasAttribute<GenericBeamWeaponAtb>()
                || design.HasAttribute<MissileLauncherAtb>()
                || design.HasAttribute<GenericWeaponAtb>())
                weapons += count;

            if (design.HasAttribute<SensorReceiverAtb>())
                sensors += count;

            if (design.HasAttribute<CargoStorageAtb>())
                cargoUnits += count;
        }

        seedBuilder.Append("|armor=").Append(armor?.UniqueID ?? "none").Append(':').Append(armorThickness.ToString("0.##"));

        int seed = unchecked((int)ShipVisualRng.Hash(seedBuilder.ToString()));

        // Size 60–140 from volume (empty → mid-small).
        int size = 75;
        if (volumeM3 > 0)
        {
            double logVol = Math.Log10(Math.Max(1.0, volumeM3));
            size = (int)Math.Clamp(60 + logVol * 18, 60, 140);
        }

        int armorParam = (int)Math.Clamp(Math.Round(armorThickness / 3.0), 0, 12);
        int cargoParam = (int)Math.Clamp(cargoUnits, 0, 12);
        int engineParam = (int)Math.Clamp(Math.Max(1, engines), 1, 8);
        int weaponParam = (int)Math.Clamp(weapons, 0, 12);
        int sensorParam = (int)Math.Clamp(sensors, 0, 8);

        // Spread/overlap nudges with size so larger hulls look wider.
        int spread = (int)Math.Clamp(40 + (size - 80) * 0.4, 15, 95);
        int overlap = (int)Math.Clamp(32 + (size - 80) * 0.2, 0, 70);

        var palette = primaryOverride is { } primary
            ? DerivePalette(primary)
            : new ShipVisualPalette(); // navy default

        return new ShipVisualState
        {
            Name = string.IsNullOrWhiteSpace(designName) ? "Ship" : designName.Trim(),
            Seed = seed < 0 ? seed & int.MaxValue : seed,
            Size = size,
            Spread = spread,
            Overlap = overlap,
            Engines = engineParam,
            Weapons = weaponParam,
            Sensors = sensorParam,
            Cargo = cargoParam,
            Armor = armorParam,
            Palette = palette,
            AccentAmount = 28,
            ColorVariance = 12,
            CoherentColors = true
        };
    }

    private static ShipVisualPalette DerivePalette(RgbColor primary)
    {
        // Warm accent + cyan engine from primary luminance.
        var accent = new RgbColor(
            (byte)Math.Clamp(primary.R + 80, 0, 255),
            (byte)Math.Clamp(primary.G + 40, 0, 255),
            (byte)Math.Clamp(primary.B - 40, 0, 255));
        var engine = new RgbColor(
            (byte)Math.Clamp(40 + primary.B / 2, 0, 255),
            (byte)Math.Clamp(180 + primary.G / 8, 0, 255),
            255);
        return new ShipVisualPalette { Primary = primary, Accent = accent, Engine = engine };
    }
}
