using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using Pulsar4X.Api;
using Pulsar4X.Client.Interface.Widgets;
using SDL3;

namespace Pulsar4X.Client;

/// <summary>
/// Live ring-buffer viewer for <see cref="DebugTraceLog"/> — standing orders, warps, cargo,
/// production/launch, and errors. Filter categories: Standing, Warp, Fuel, Refuel, Production, Launch, UI.
/// Opened from the left toolbar.
/// </summary>
public class DebugLogWindow : UniquePulsarGuiWindow<DebugLogWindow>
{
    private bool _autoScroll = true;
    private bool _pauseCapture;
    private string _filterText = "";
    private int _minLevelIndex = 1; // Info+ (Trace is very chatty each standing tick)
    private readonly HashSet<string> _hiddenCategories = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSeqSeen;
    private bool _stickToBottom = true;

    private static readonly string[] LevelLabels = { "Trace+", "Info+", "Warn+", "Error" };

    private DebugLogWindow()
    {
        DebugTraceLog.Info("UI", "Debug Log window created");
    }

    internal static DebugLogWindow GetInstance()
    {
        if (_uiState.TryGetUniqueWindow<DebugLogWindow>(out var window))
            return window;

        return _uiState.AddUniqueWindow(new DebugLogWindow());
    }

    internal override void Display()
    {
        if (!IsActive)
            return;

        ImGui.SetNextWindowSize(new Vector2(920, 480), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(40, 80), ImGuiCond.FirstUseEver);

        if (!Window.Begin("Debug Log", ref IsActive))
        {
            Window.End();
            return;
        }

        DrawToolbar();
        ImGui.Separator();
        DrawEntries();

        Window.End();
    }

    private void DrawToolbar()
    {
        bool capture = DebugTraceLog.Enabled;
        if (ImGui.Checkbox("Capture", ref capture))
        {
            DebugTraceLog.Enabled = capture;
            // Re-enable briefly to log the toggle itself.
            bool was = DebugTraceLog.Enabled;
            DebugTraceLog.Enabled = true;
            DebugTraceLog.Info("UI", was ? "Capture enabled" : "Capture disabled");
            DebugTraceLog.Enabled = was;
        }

        ImGui.SameLine();
        bool storeTrace = DebugTraceLog.MinLevelToStore <= DebugTraceLevel.Trace;
        if (ImGui.Checkbox("Store Trace", ref storeTrace))
        {
            DebugTraceLog.MinLevelToStore = storeTrace ? DebugTraceLevel.Trace : DebugTraceLevel.Info;
            DebugTraceLog.Info("UI", storeTrace
                ? "Trace storage ON (can fill the buffer quickly)"
                : "Trace storage OFF — Info+ only");
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Pause view", ref _pauseCapture) && _pauseCapture)
            _frozenSnapshot = DebugTraceLog.Snapshot();

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            DebugTraceLog.Clear();
            _lastSeqSeen = 0;
            _frozenSnapshot = null;
            bool was = DebugTraceLog.Enabled;
            DebugTraceLog.Enabled = true;
            DebugTraceLog.Info("UI", "Log cleared");
            DebugTraceLog.Enabled = was;
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy all"))
            CopyToClipboard(GetVisibleEntries());

        ImGui.SameLine();
        if (ImGui.Button("Save…"))
            SaveToFile(GetVisibleEntries());

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.Combo("Level", ref _minLevelIndex, LevelLabels, LevelLabels.Length);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##filter", "Filter text…", ref _filterText, 128))
        { }

        var categories = DebugTraceLog.Snapshot()
            .Select(e => e.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        if (categories.Count > 0)
        {
            ImGui.TextUnformatted("Categories:");
            ImGui.SameLine();
            foreach (var cat in categories)
            {
                bool shown = !_hiddenCategories.Contains(cat);
                if (ImGui.Checkbox(cat, ref shown))
                {
                    if (shown)
                        _hiddenCategories.Remove(cat);
                    else
                        _hiddenCategories.Add(cat);
                }
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
    }

    private IReadOnlyList<DebugTraceEntry> GetVisibleEntries()
    {
        IReadOnlyList<DebugTraceEntry> entries;
        if (_pauseCapture)
        {
            _frozenSnapshot ??= DebugTraceLog.Snapshot();
            entries = _frozenSnapshot;
        }
        else
        {
            entries = DebugTraceLog.Snapshot();
        }

        var minLevel = (DebugTraceLevel)_minLevelIndex;
        return entries
            .Where(e => e.Level >= minLevel)
            .Where(e => !_hiddenCategories.Contains(e.Category))
            .Where(e => string.IsNullOrEmpty(_filterText)
                        || e.Message.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                        || e.Category.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void DrawEntries()
    {
        IReadOnlyList<DebugTraceEntry> entries;
        if (_pauseCapture)
        {
            _frozenSnapshot ??= DebugTraceLog.Snapshot();
            entries = _frozenSnapshot;
        }
        else
        {
            _frozenSnapshot = null;
            entries = DebugTraceLog.Snapshot();
        }

        var filtered = GetVisibleEntries();

        ImGui.Text($"Showing {filtered.Count} / {entries.Count}  (buffer {DebugTraceLog.Capacity})");

        if (ImGui.BeginChild("##debuglog_scroll", new Vector2(0, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2));

            foreach (var e in filtered)
            {
                var color = e.Level switch
                {
                    DebugTraceLevel.Error => new Vector4(1f, 0.35f, 0.35f, 1f),
                    DebugTraceLevel.Warn => new Vector4(1f, 0.85f, 0.3f, 1f),
                    DebugTraceLevel.Trace => new Vector4(0.55f, 0.55f, 0.6f, 1f),
                    _ => new Vector4(0.85f, 0.9f, 0.95f, 1f),
                };

                ImGui.PushStyleColor(ImGuiCol.Text, color);

                string game = e.GameTime.HasValue
                    ? e.GameTime.Value.ToString("dd.MM.yyyy HH:mm")
                    : "—";
                string line =
                    $"[{e.Sequence}] {e.WallClockUtc:HH:mm:ss.fff} | game {game} | {e.Level,-5} | {e.Category,-12} | {e.Message}";

                ImGui.TextUnformatted(line);
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    TrySetClipboard(line);

                ImGui.PopStyleColor();

                if (e.Sequence > _lastSeqSeen)
                    _lastSeqSeen = e.Sequence;
            }

            ImGui.PopStyleVar();

            if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 8)
                _stickToBottom = true;
            if (ImGui.GetScrollY() < ImGui.GetScrollMaxY() - 40)
                _stickToBottom = false;

            if (_autoScroll && _stickToBottom)
                ImGui.SetScrollHereY(1f);
        }

        ImGui.EndChild();
    }

    private IReadOnlyList<DebugTraceEntry>? _frozenSnapshot;

    private static string FormatEntries(IReadOnlyList<DebugTraceEntry> entries)
    {
        var sb = new StringBuilder(entries.Count * 120);
        foreach (var e in entries)
        {
            string game = e.GameTime.HasValue ? e.GameTime.Value.ToString("O") : "";
            sb.Append(e.Sequence).Append('\t')
                .Append(e.WallClockUtc.ToString("O")).Append('\t')
                .Append(game).Append('\t')
                .Append(e.Level).Append('\t')
                .Append(e.Category).Append('\t')
                .Append(e.Message).AppendLine();
        }
        return sb.ToString();
    }

    private static void CopyToClipboard(IReadOnlyList<DebugTraceEntry> entries)
    {
        string text = FormatEntries(entries);
        if (TrySetClipboard(text))
            DebugTraceLog.Info("UI", $"Copied {entries.Count} lines to clipboard ({text.Length} chars)");
        else
            DebugTraceLog.Error("UI", $"Clipboard copy failed ({entries.Count} lines). Use Save… instead. SDL: {SDL.GetError()}");
    }

    private static bool TrySetClipboard(string text)
    {
        // Prefer SDL directly — ImGui platform clipboard was wired with the wrong signature.
        try
        {
            if (SDL.SetClipboardText(text))
                return true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Warn("UI", $"SDL.SetClipboardText threw: {ex.Message}");
        }

        try
        {
            ImGui.SetClipboardText(text);
            return true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Warn("UI", $"ImGui.SetClipboardText threw: {ex.Message}");
            return false;
        }
    }

    private static void SaveToFile(IReadOnlyList<DebugTraceEntry> entries)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Pulsar4X-DebugLogs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"debug-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, FormatEntries(entries), Encoding.UTF8);
            DebugTraceLog.Info("UI", $"Saved {entries.Count} lines to {path}");
            TrySetClipboard(path);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Error("UI", $"Save failed: {ex.Message}");
        }
    }
}
