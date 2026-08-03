using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Pulsar4X.Api;
using SDL3;

namespace Pulsar4X.Client.ShipVisuals;

/// <summary>Caches generated ship textures keyed by design id/name for map icons.</summary>
public static class ShipMapTextureCache
{
    private static readonly Dictionary<string, (IntPtr texture, int width, int height)> _cache = new(StringComparer.Ordinal);
    private static readonly ShipVisualComposer _composer = new();
    private static IntPtr _renderer;

    public static void Initialize(IntPtr renderer)
    {
        _renderer = renderer;
    }

    public static (IntPtr texture, int width, int height) GetOrCreate(ShipView ship, MassVolumeView? mass, ThrustView? thrust)
    {
        string key = !string.IsNullOrEmpty(ship.DesignId) ? ship.DesignId! : ("name:" + ship.DesignName);
        if (_cache.TryGetValue(key, out var cached) && cached.texture != IntPtr.Zero)
            return cached;

        if (_renderer == IntPtr.Zero)
            return (IntPtr.Zero, 0, 0);

        var state = ShipVisualStateFactory.FromShipViews(ship, mass, thrust);
        var rgba = _composer.Compose(state);

        IntPtr texture = IntPtr.Zero;
        GCHandle handle = GCHandle.Alloc(rgba.Pixels, GCHandleType.Pinned);
        try
        {
                Textures.CreateTexture(
                _renderer,
                ref texture,
                rgba.Width,
                rgba.Height,
                32,
                rgba.Width * 4,
                handle.AddrOfPinnedObject(),
                Textures.RgbaByteOrder);
        }
        finally
        {
            handle.Free();
        }

        if (texture != IntPtr.Zero)
        {
            SDL.SetTextureBlendMode(texture, SDL.BlendMode.Blend);
            SDL.SetTextureScaleMode(texture, SDL.ScaleMode.Nearest);
            cached = (texture, rgba.Width, rgba.Height);
            _cache[key] = cached;
        }

        return cached;
    }
}
