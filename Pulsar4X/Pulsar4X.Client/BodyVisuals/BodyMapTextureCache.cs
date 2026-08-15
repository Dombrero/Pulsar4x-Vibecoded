using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Pulsar4X.Api;
using Pulsar4X.Client.ShipVisuals;
using SDL3;

namespace Pulsar4X.Client.BodyVisuals;

public enum BodyTextureLayer : byte
{
    Disk = 0,
    Albedo = 1,
    Clouds = 2,
}

/// <summary>
/// Caches celestial body textures. Unsurveyed types share one sprite; surveyed/Sol bodies are keyed.
/// CPU compose runs on the thread pool; SDL upload stays on the UI thread.
/// </summary>
public static class BodyMapTextureCache
{
    private const int MaxInFlight = 2;
    private const int MaxUploadsPerPump = 4;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, (IntPtr texture, int width, int height)> _cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (BodyVisualState State, BodyTextureLayer Layer)> _queued = new(StringComparer.Ordinal);
    private static readonly Queue<string> _queueOrder = new();
    private static readonly Dictionary<string, RgbaImage> _ready = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _failed = new(StringComparer.Ordinal);
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
        => GetOrCreate(state, BodyTextureLayer.Disk);

    /// <summary>
    /// Returns a cached SDL texture, or Zero if still baking. Never composes on the caller thread.
    /// </summary>
    public static (IntPtr texture, int width, int height) GetOrCreate(BodyVisualState state, BodyTextureLayer layer)
    {
        string key = CacheKey(state, layer);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.texture != IntPtr.Zero)
                return cached;
            if (_failed.Contains(key) || _renderer == IntPtr.Zero)
                return (IntPtr.Zero, 0, 0);

            EnqueueUnlocked(key, state, layer);
            TryStartUnlocked();
            return (IntPtr.Zero, 0, 0);
        }
    }

    /// <summary>Upload finished CPU bitmaps on the UI/renderer thread. Call once per map frame.</summary>
    public static void PumpUploads()
    {
        List<(string key, RgbaImage rgba)> batch;
        lock (_lock)
        {
            if (_ready.Count == 0)
            {
                TryStartUnlocked();
                return;
            }

            batch = new List<(string, RgbaImage)>(Math.Min(MaxUploadsPerPump, _ready.Count));
            foreach (var kv in _ready)
            {
                batch.Add((kv.Key, kv.Value));
                if (batch.Count >= MaxUploadsPerPump)
                    break;
            }
            foreach (var (key, _) in batch)
                _ready.Remove(key);
        }

        foreach (var (key, rgba) in batch)
        {
            try
            {
                var uploaded = Upload(rgba);
                lock (_lock)
                {
                    if (uploaded.texture != IntPtr.Zero)
                        _cache[key] = uploaded;
                    else
                        _failed.Add(key);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BodyMapTextureCache: upload failed for '{key}': {ex.Message}");
                lock (_lock)
                    _failed.Add(key);
            }
        }

        lock (_lock)
            TryStartUnlocked();
    }

    private static string CacheKey(BodyVisualState state, BodyTextureLayer layer)
        => layer switch
        {
            BodyTextureLayer.Albedo => state.CacheKey + ":albedo",
            BodyTextureLayer.Clouds => state.CacheKey + ":clouds",
            _ => state.CacheKey + ":c" + BodyVisualComposer.CanvasSize,
        };

    private static void EnqueueUnlocked(string key, BodyVisualState state, BodyTextureLayer layer)
    {
        if (_inFlight.Contains(key) || _queued.ContainsKey(key) || _ready.ContainsKey(key) || _cache.ContainsKey(key))
            return;
        _queued[key] = (state, layer);
        _queueOrder.Enqueue(key);
    }

    private static void TryStartUnlocked()
    {
        while (_inFlight.Count < MaxInFlight && _queueOrder.Count > 0)
        {
            string key = _queueOrder.Dequeue();
            if (!_queued.Remove(key, out var item))
                continue;
            if (_cache.ContainsKey(key) || _ready.ContainsKey(key) || _failed.Contains(key) || _inFlight.Contains(key))
                continue;

            _inFlight.Add(key);
            var state = item.State;
            var layer = item.Layer;
            ThreadPool.QueueUserWorkItem(_ => ComposeWorker(key, state, layer));
        }
    }

    private static void ComposeWorker(string key, BodyVisualState state, BodyTextureLayer layer)
    {
        try
        {
            RgbaImage rgba = layer switch
            {
                BodyTextureLayer.Albedo => _composer.ComposeAlbedoMap(state),
                BodyTextureLayer.Clouds => _composer.ComposeCloudMap(state),
                _ => _composer.Compose(state),
            };
            lock (_lock)
            {
                _ready[key] = rgba;
                _inFlight.Remove(key);
                TryStartUnlocked();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BodyMapTextureCache: compose failed for '{key}': {ex.Message}");
            lock (_lock)
            {
                _failed.Add(key);
                _inFlight.Remove(key);
                TryStartUnlocked();
            }
        }
    }

    private static (IntPtr texture, int width, int height) Upload(RgbaImage rgba)
    {
        IntPtr renderer;
        lock (_lock)
            renderer = _renderer;
        if (renderer == IntPtr.Zero)
            return (IntPtr.Zero, 0, 0);

        IntPtr texture = IntPtr.Zero;
        GCHandle handle = GCHandle.Alloc(rgba.Pixels, GCHandleType.Pinned);
        try
        {
            Textures.CreateTexture(
                renderer,
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

        if (texture == IntPtr.Zero)
            return (IntPtr.Zero, 0, 0);

        SDL.SetTextureBlendMode(texture, SDL.BlendMode.Blend);
        SDL.SetTextureScaleMode(texture, SDL.ScaleMode.Linear);
        return (texture, rgba.Width, rgba.Height);
    }
}
