using System;
using Pulsar4X.Api;

namespace Pulsar4X.Client.ShipVisuals;

/// <summary>Builds a <see cref="ShipVisualState"/> from replicated ship snapshot data (map icons).</summary>
public static class ShipVisualStateFactory
{
    public static ShipVisualState FromShipViews(ShipView ship, MassVolumeView? massVolume = null, ThrustView? thrust = null)
    {
        string key = ship.DesignId ?? ship.DesignName ?? "Ship";
        int seed = unchecked((int)ShipVisualRng.Hash(key));
        if (seed < 0)
            seed &= int.MaxValue;

        int size = 85;
        if (massVolume is { MassKg: > 0 })
        {
            double logMass = Math.Log10(Math.Max(1.0, massVolume.MassKg));
            size = (int)Math.Clamp(55 + logMass * 12, 60, 140);
        }

        int engines = 2;
        if (thrust is { ThrustNewtons: > 0 })
            engines = (int)Math.Clamp(1 + Math.Log10(Math.Max(1.0, thrust.ThrustNewtons)), 1, 8);

        int weapons = ship.IsMilitaryHint() ? Math.Clamp(ship.TotalComponents / 4, 1, 8) : 0;
        int sensors = ship.CanGeoSurvey || ship.CanGravSurvey ? 4 : Math.Clamp(ship.TotalComponents / 8, 0, 4);
        int cargo = Math.Clamp(ship.TotalComponents / 5, 0, 8);
        int armor = (int)Math.Clamp(Math.Round(ship.ArmorThicknessMm / 3.0), 0, 12);

        int spread = (int)Math.Clamp(40 + (size - 80) * 0.4, 15, 95);
        int overlap = (int)Math.Clamp(32 + (size - 80) * 0.2, 0, 70);

        return new ShipVisualState
        {
            Name = ship.DesignName,
            Seed = seed,
            Size = size,
            Spread = spread,
            Overlap = overlap,
            Engines = engines,
            Weapons = weapons,
            Sensors = sensors,
            Cargo = cargo,
            Armor = armor,
            Palette = new ShipVisualPalette(),
            AccentAmount = 28,
            ColorVariance = 12,
            CoherentColors = true
        };
    }

    private static bool IsMilitaryHint(this ShipView ship)
        => ship.CanGeoSurvey == false && ship.CanGravSurvey == false && ship.TotalComponents >= 6;
}
