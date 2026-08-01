using System;
using System.Collections.Generic;

namespace Pulsar4X.Api;

public enum DebugTraceLevel
{
    Trace = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public readonly record struct DebugTraceEntry(
    long Sequence,
    DateTime WallClockUtc,
    DateTime? GameTime,
    string Category,
    DebugTraceLevel Level,
    string Message);

/// <summary>
/// Process-wide ring buffer for order / standing / movement diagnostics.
/// Written by the engine, shown in the client Debug Log window.
/// Trace is off by default so per-tick spam cannot wipe Info/Warn history.
/// </summary>
public static class DebugTraceLog
{
    public const int DefaultCapacity = 8000;

    private static readonly object Gate = new();
    private static DebugTraceEntry[] _buffer = new DebugTraceEntry[DefaultCapacity];
    private static int _count;
    private static int _next;
    private static long _sequence;

    /// <summary>When false, Write is a no-op (UI can toggle).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Entries below this level are discarded (default Info).
    /// Enable Trace from the Debug Log UI only when you need per-tick noise.
    /// </summary>
    public static DebugTraceLevel MinLevelToStore { get; set; } = DebugTraceLevel.Info;

    public static int Capacity
    {
        get { lock (Gate) return _buffer.Length; }
        set
        {
            if (value < 100)
                value = 100;
            lock (Gate)
            {
                var snapshot = SnapshotUnlocked();
                _buffer = new DebugTraceEntry[value];
                _count = 0;
                _next = 0;
                foreach (var e in snapshot)
                    AppendUnlocked(e.WallClockUtc, e.GameTime, e.Category, e.Level, e.Message);
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _count = 0;
            _next = 0;
            Array.Clear(_buffer);
        }
    }

    public static void Trace(string category, string message, DateTime? gameTime = null)
        => Write(category, message, DebugTraceLevel.Trace, gameTime);

    public static void Info(string category, string message, DateTime? gameTime = null)
        => Write(category, message, DebugTraceLevel.Info, gameTime);

    public static void Warn(string category, string message, DateTime? gameTime = null)
        => Write(category, message, DebugTraceLevel.Warn, gameTime);

    public static void Error(string category, string message, DateTime? gameTime = null)
        => Write(category, message, DebugTraceLevel.Error, gameTime);

    public static void Write(string category, string message, DebugTraceLevel level = DebugTraceLevel.Info, DateTime? gameTime = null)
    {
        if (!Enabled)
            return;
        if (level < MinLevelToStore)
            return;

        category ??= "";
        message ??= "";

        lock (Gate)
            AppendUnlocked(DateTime.UtcNow, gameTime, category, level, message);
    }

    /// <summary>Oldest → newest copy of the ring buffer.</summary>
    public static IReadOnlyList<DebugTraceEntry> Snapshot()
    {
        lock (Gate)
            return SnapshotUnlocked();
    }

    private static void AppendUnlocked(DateTime wall, DateTime? gameTime, string category, DebugTraceLevel level, string message)
    {
        _sequence++;
        _buffer[_next] = new DebugTraceEntry(_sequence, wall, gameTime, category, level, message);
        _next = (_next + 1) % _buffer.Length;
        if (_count < _buffer.Length)
            _count++;
    }

    private static List<DebugTraceEntry> SnapshotUnlocked()
    {
        var list = new List<DebugTraceEntry>(_count);
        if (_count == 0)
            return list;

        int start = _count < _buffer.Length ? 0 : _next;
        for (int i = 0; i < _count; i++)
            list.Add(_buffer[(start + i) % _buffer.Length]);
        return list;
    }
}
