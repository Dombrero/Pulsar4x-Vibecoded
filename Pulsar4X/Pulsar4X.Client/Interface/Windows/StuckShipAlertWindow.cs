using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Pulsar4X.Api;
using Pulsar4X.Client.Interface;

namespace Pulsar4X.Client;

/// <summary>
/// Modal alert when MovementStuckWatchdog trips (fuel / energy / micro-hop).
/// Pin button mirrors EntityWindow header — focuses the camera on the stranded ship.
/// </summary>
public sealed class StuckShipAlertWindow : UniquePulsarGuiWindow<StuckShipAlertWindow>
{
    sealed class PendingAlert
    {
        public required LogEvent Event { get; init; }
        public required int EntityId { get; init; }
        public string? SystemId { get; init; }
    }

    readonly Queue<PendingAlert> _queue = new();
    PendingAlert? _current;
    bool _openPopup;
    const string PopupId = "Ship Stranded###stuck-ship-alert";

    StuckShipAlertWindow()
    {
        _flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.Modal | ImGuiWindowFlags.NoCollapse;
    }

    internal static StuckShipAlertWindow GetInstance()
    {
        if (_uiState.TryGetUniqueWindow<StuckShipAlertWindow>(out var window))
            return window;
        return _uiState.AddUniqueWindow(new StuckShipAlertWindow());
    }

    /// <summary>Queue a stuck-ship log entry for the next modal (skips duplicates for same hull).</summary>
    internal void Enqueue(LogEvent logEvent)
    {
        if (logEvent.EntityId is not int entityId || entityId == 0)
            return;
        if (string.IsNullOrEmpty(logEvent.Message)
            || !logEvent.Message.Contains("SHIP STUCK", StringComparison.Ordinal))
            return;

        // Fuel stranding is the primary case; other stuck kinds use the same popup.
        if (_current?.EntityId == entityId)
            return;
        foreach (var pending in _queue)
        {
            if (pending.EntityId == entityId)
                return;
        }

        _queue.Enqueue(new PendingAlert
        {
            Event = logEvent,
            EntityId = entityId,
            SystemId = logEvent.SystemId,
        });

        if (_current == null)
            PromoteNext();
    }

    void PromoteNext()
    {
        if (!_queue.TryDequeue(out _current))
        {
            IsActive = false;
            return;
        }

        IsActive = true;
        _openPopup = true;
    }

    internal override void Display()
    {
        if (_current == null && _queue.Count > 0)
            PromoteNext();

        if (_current == null)
            return;

        if (_openPopup)
        {
            ImGui.OpenPopup(PopupId);
            _openPopup = false;
        }

        bool open = IsActive;
        if (!ImGui.BeginPopupModal(PopupId, ref open, _flags))
        {
            if (!open)
                DismissCurrent();
            return;
        }

        IsActive = open;
        var alert = _current;
        string shipName = alert.Event.EntityName ?? $"Ship #{alert.EntityId}";
        string cause = ExtractCause(alert.Event.Message) ?? "Unable to continue travel";

        ImGui.PushStyleColor(ImGuiCol.Text, Styles.BadColor);
        ImGui.TextUnformatted("SHIP STUCK — time paused");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.TextUnformatted(shipName);
        ImGui.PushStyleColor(ImGuiCol.Text, Styles.OkColor);
        ImGui.TextWrapped(cause);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Pin (same icon / behaviour as EntityWindow header)
        float pinSize = 16f;
        ImGui.PushStyleColor(ImGuiCol.Button, Styles.InvisibleColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.4f, 0.4f, 0.4f, 0.7f));
        if (ImGui.ImageButton("##stuckpin", _uiState.Img_Pin().ToTextureRef(), new Vector2(pinSize, pinSize)))
            FocusShip(alert);
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pin camera to stranded ship");

        ImGui.SameLine();
        if (ImGui.Button("Go to ship"))
            FocusShip(alert);

        ImGui.SameLine();
        if (ImGui.Button("Dismiss"))
        {
            ImGui.CloseCurrentPopup();
            DismissCurrent();
            ImGui.EndPopup();
            return;
        }

        if (_queue.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(+{_queue.Count} more)");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Full diagnosis is in the Game Log.");

        if (!IsActive)
            DismissCurrent();

        ImGui.EndPopup();
    }

    void FocusShip(PendingAlert alert)
    {
        string? systemId = alert.SystemId;
        if (string.IsNullOrEmpty(systemId))
            systemId = _uiState.FindSystemContainingEntity(alert.EntityId);
        if (string.IsNullOrEmpty(systemId))
            return;

        if (systemId != _uiState.SelectedStarSystemId)
            _uiState.SetActiveSystem(systemId, keepSelection: true);

        _uiState.Camera.PinToEntity(alert.EntityId, systemId, _uiState);
        _uiState.EntitySelectedAsPrimary(alert.EntityId, systemId);
    }

    void DismissCurrent()
    {
        _current = null;
        if (_queue.Count > 0)
            PromoteNext();
        else
            IsActive = false;
    }

    static string? ExtractCause(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        const string prefix = "Cause: ";
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return trimmed[prefix.Length..].Trim();
        }

        return null;
    }
}
