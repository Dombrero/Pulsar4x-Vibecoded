using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Pulsar4X.Api;
using Pulsar4X.Client.ShipVisuals;
using SDL3;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>Caches celestial body textures. Unsurveyed types share one sprite; surveyed/Sol bodies are keyed.</summary>
public static class BodyMapTextureCache
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (IntPtr texture, int width, int height)> _cache = new(StringComparer.Ordinal);
    private static readonly BodyVisualComposer _composer = new();
    private static IntPtr _renderer;

    public static void Initialize(IntPtr renderer)
    {
        lock (_lock)
        {
            _renderer = renderer;
        }
    }

    public static (IntPtr texture, int width, int height) GetOrCreate(EntitySnapshot entity, IClientSystem system)
    {
        try
        {
            var state = BodyVisualStateFactory.FromEntity(entity, system);
            return GetOrCreate(state);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BodyMapTextureCache: failed for entity {entity.Id}: {ex.Message}");
            return (IntPtr.Zero, 0, 0);
        }
    }

    public static (IntPtr texture, int width, int height) GetOrCreate(BodyVisualState state)
    {
        string key = state.CacheKey;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.texture != IntPtr.Zero)
                return cached;

            if (_renderer == IntPtr.Zero)
                return (IntPtr.Zero, 0, 0);

            try
            {
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
            catch (Exception ex)
            {
                Console.WriteLine($"BodyMapTextureCache: compose/upload failed for '{key}': {ex.Message}");
                return (IntPtr.Zero, 0, 0);
            }
        }
    }
}
