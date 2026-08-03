using System;
using System.Collections.Generic;
using System.IO;
using StbImageSharp;

namespace Pulsar4X.Client.ShipVisuals;

public static class ShipPartCategories
{
    public static readonly string[] All =
    {
        "cores", "cockpits", "wings", "engines", "weapons",
        "sensors", "pods", "cargo_hangars", "armor"
    };
}

/// <summary>Loads and caches ship-part PNG sprites from Resources/ship-parts.</summary>
public sealed class PartLibrary
{
    private readonly Dictionary<string, List<RgbaImage>> _parts = new(StringComparer.Ordinal);
    private readonly string _root;

    public PartLibrary(string? resourcesRoot = null)
    {
        _root = resourcesRoot
            ?? Path.Combine(PulsarMainWindow.ResourcesPath, "ship-parts");
    }

    public bool IsLoaded => _parts.Count > 0;

    public void EnsureLoaded()
    {
        if (IsLoaded)
            return;

        foreach (string cat in ShipPartCategories.All)
        {
            var list = new List<RgbaImage>();
            string dir = Path.Combine(_root, cat);
            if (!Directory.Exists(dir))
                continue;

            foreach (string file in Directory.GetFiles(dir, "*.png"))
            {
                byte[] bytes = File.ReadAllBytes(file);
                ImageResult img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
                list.Add(new RgbaImage(img.Width, img.Height, img.Data));
            }

            _parts[cat] = list;
        }
    }

    public IReadOnlyList<RgbaImage> GetCategory(string category)
    {
        EnsureLoaded();
        return _parts.TryGetValue(category, out var list) ? list : Array.Empty<RgbaImage>();
    }

    public RgbaImage Pick(string category, Func<double> rng)
    {
        var list = GetCategory(category);
        if (list.Count == 0)
            return new RgbaImage(1, 1);
        int index = (int)System.Math.Floor(rng() * list.Count);
        if (index >= list.Count)
            index = list.Count - 1;
        return list[index];
    }
}
