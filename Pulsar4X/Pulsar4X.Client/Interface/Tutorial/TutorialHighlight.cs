using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace Pulsar4X.Client
{
    /// <summary>Feine UI-Ziele fuer Tutorial-Hervorhebung (ein Ziel nach dem anderen).</summary>
    public enum TutorialHighlightRegion
    {
        None,

        TimeControl,
        TimeControlInterval,
        TimeControlPlayPause,

        LeftToolbarColony,
        LeftToolbarResearch,
        LeftToolbarFleet,
        LeftToolbarSystemTree,
        LeftToolbarGalaxy,

        RightSelector,
        RightSelectorColonies,
        RightSelectorFleets,
        RightSelectorFunds,

        WindowColonyManagement,
        ColonyList,
        ColonyTabSummary,
        ColonyTabProduction,
        ColonyTabConstruction,
        ColonyTabMining,
        ColonyTabEnergy,
        ColonyStockpile,
        ColonyTransferButton,
        ProductionLines,
        ProductionNewJob,
        ConstructionQueue,
        ConstructionDesigns,

        WindowResearch,
        ResearchLabList,
        ResearchAvailableTechs,
        ResearchTechQueue,

        WindowFleetManagement,
        FleetList,
        FleetOrdersQueue,
        FleetTabIssueOrders,
        FleetOrderGeoSurvey,
        FleetOrderTargets,
        FleetTabStandingOrders,
        FleetStandingSave,
    }

    /// <summary>
    /// Sammelt Rects pro Frame; zeigt nur das aktuelle Klickziel.
    /// Klick in die Markierung → naechstes Ziel.
    /// </summary>
    public static class TutorialHighlight
    {
        private static readonly Dictionary<TutorialHighlightRegion, (Vector2 Min, Vector2 Max)> Regions = new();

        private static readonly Dictionary<TutorialHighlightRegion, TutorialHighlightRegion> Fallbacks = new()
        {
            { TutorialHighlightRegion.ColonyStockpile, TutorialHighlightRegion.ColonyTabSummary },
            { TutorialHighlightRegion.ColonyTransferButton, TutorialHighlightRegion.ColonyTabSummary },
            { TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ColonyTabProduction },
            { TutorialHighlightRegion.ProductionNewJob, TutorialHighlightRegion.ColonyTabProduction },
            { TutorialHighlightRegion.ConstructionQueue, TutorialHighlightRegion.ColonyTabConstruction },
            { TutorialHighlightRegion.ConstructionDesigns, TutorialHighlightRegion.ColonyTabConstruction },
            { TutorialHighlightRegion.ResearchAvailableTechs, TutorialHighlightRegion.ResearchLabList },
            { TutorialHighlightRegion.ResearchTechQueue, TutorialHighlightRegion.ResearchLabList },
            { TutorialHighlightRegion.FleetOrderGeoSurvey, TutorialHighlightRegion.FleetTabIssueOrders },
            { TutorialHighlightRegion.FleetOrderTargets, TutorialHighlightRegion.FleetTabIssueOrders },
            { TutorialHighlightRegion.FleetStandingSave, TutorialHighlightRegion.FleetTabStandingOrders },
            { TutorialHighlightRegion.FleetOrdersQueue, TutorialHighlightRegion.WindowFleetManagement },
            { TutorialHighlightRegion.FleetList, TutorialHighlightRegion.LeftToolbarFleet },
            { TutorialHighlightRegion.FleetTabIssueOrders, TutorialHighlightRegion.LeftToolbarFleet },
            { TutorialHighlightRegion.FleetTabStandingOrders, TutorialHighlightRegion.LeftToolbarFleet },
            { TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.ColonyTabEnergy, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.ColonyList, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.WindowColonyManagement, TutorialHighlightRegion.LeftToolbarColony },
            { TutorialHighlightRegion.WindowResearch, TutorialHighlightRegion.LeftToolbarResearch },
            { TutorialHighlightRegion.ResearchLabList, TutorialHighlightRegion.LeftToolbarResearch },
            { TutorialHighlightRegion.WindowFleetManagement, TutorialHighlightRegion.LeftToolbarFleet },
            { TutorialHighlightRegion.TimeControlInterval, TutorialHighlightRegion.TimeControl },
            { TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.TimeControl },
            { TutorialHighlightRegion.RightSelectorColonies, TutorialHighlightRegion.RightSelector },
            { TutorialHighlightRegion.RightSelectorFleets, TutorialHighlightRegion.RightSelector },
            { TutorialHighlightRegion.RightSelectorFunds, TutorialHighlightRegion.RightSelector },
        };

        private static TutorialHighlightRegion[] _path = Array.Empty<TutorialHighlightRegion>();
        private static int _pathIndex;
        private static bool _mouseWasDown;

        public static bool Enabled { get; set; }

        public static int PathIndex => _pathIndex;

        public static int PathLength => _path.Length;

        public static TutorialHighlightRegion CurrentRegion =>
            _path.Length > 0 && _pathIndex >= 0 && _pathIndex < _path.Length
                ? _path[_pathIndex]
                : TutorialHighlightRegion.None;

        public static bool PathComplete => _path.Length == 0 || _pathIndex >= _path.Length;

        public static void BeginFrame() => Regions.Clear();

        /// <summary>Neuen Klickpfad setzen (setzt Index auf 0).</summary>
        public static void SetPath(TutorialHighlightRegion[] path)
        {
            _path = path ?? Array.Empty<TutorialHighlightRegion>();
            _pathIndex = 0;
            _mouseWasDown = false;
        }

        public static void ClearPath()
        {
            _path = Array.Empty<TutorialHighlightRegion>();
            _pathIndex = 0;
        }

        public static void Advance()
        {
            if (_pathIndex < _path.Length)
                _pathIndex++;
        }

        public static void Retreat()
        {
            if (_pathIndex > 0)
                _pathIndex--;
        }

        public static void Report(TutorialHighlightRegion region, Vector2 min, Vector2 max)
        {
            if (region == TutorialHighlightRegion.None)
                return;
            if (max.X <= min.X || max.Y <= min.Y)
                return;

            const float pad = 3f;
            Regions[region] = (min - new Vector2(pad, pad), max + new Vector2(pad, pad));
        }

        public static void ReportItem(TutorialHighlightRegion region)
        {
            if (region == TutorialHighlightRegion.None)
                return;
            Report(region, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }

        public static void ReportCurrentWindow(TutorialHighlightRegion region)
        {
            if (region == TutorialHighlightRegion.None)
                return;
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            if (size.X < 1f || size.Y < 1f)
                return;
            Report(region, pos, pos + size);
        }

        public static void ReportToolbarTooltip(string? tooltip)
        {
            if (string.IsNullOrEmpty(tooltip))
                return;

            TutorialHighlightRegion region = tooltip switch
            {
                "Colony Management" => TutorialHighlightRegion.LeftToolbarColony,
                "Research" => TutorialHighlightRegion.LeftToolbarResearch,
                "Fleet Management" => TutorialHighlightRegion.LeftToolbarFleet,
                "View objects in the system" => TutorialHighlightRegion.LeftToolbarSystemTree,
                "Galaxy Browser" => TutorialHighlightRegion.LeftToolbarGalaxy,
                _ => TutorialHighlightRegion.None,
            };

            if (region != TutorialHighlightRegion.None)
                ReportItem(region);
        }

        public static void DrawOverlay()
        {
            if (!Enabled || PathComplete)
                return;

            var region = CurrentRegion;
            if (region == TutorialHighlightRegion.None)
                return;

            if (!TryResolveRect(region, out var rect))
                return;

            float phase = (Environment.TickCount64 % 1800) / 1800f;
            float pulse = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(phase * MathF.PI * 2f));
            float thickness = 2.5f + pulse * 2.5f;

            var draw = ImGui.GetForegroundDrawList();
            uint border = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.15f, 0.9f + pulse * 0.1f));
            uint glow = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.15f, 0.14f * pulse));

            draw.AddRectFilled(rect.Min, rect.Max, glow, 4f);
            draw.AddRect(rect.Min, rect.Max, border, 4f, ImDrawFlags.RoundCornersAll, thickness);

            int number = _pathIndex + 1;
            DrawNumberBadge(draw, rect.Min, number);

            // Klick in die aktuelle Markierung → naechstes Ziel (einmal pro Mouse-Down).
            bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            if (mouseDown && !_mouseWasDown)
            {
                var mouse = ImGui.GetMousePos();
                if (mouse.X >= rect.Min.X && mouse.X <= rect.Max.X
                    && mouse.Y >= rect.Min.Y && mouse.Y <= rect.Max.Y)
                {
                    Advance();
                }
            }
            _mouseWasDown = mouseDown;
        }

        private static void DrawNumberBadge(ImDrawListPtr draw, Vector2 anchor, int number)
        {
            const float r = 11f;
            var center = anchor + new Vector2(r + 2f, r + 2f);
            uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.55f, 0.05f, 0.95f));
            uint textCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
            draw.AddCircleFilled(center, r, fill, 16);
            draw.AddCircle(center, r, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f)), 16, 1.5f);

            string label = number.ToString();
            var textSize = ImGui.CalcTextSize(label);
            draw.AddText(center - textSize * 0.5f, textCol, label);
        }

        private static bool TryResolveRect(TutorialHighlightRegion region, out (Vector2 Min, Vector2 Max) rect)
        {
            var seen = new HashSet<TutorialHighlightRegion>();
            var current = region;
            while (true)
            {
                if (!seen.Add(current))
                    break;

                if (Regions.TryGetValue(current, out rect))
                    return true;

                if (!Fallbacks.TryGetValue(current, out current))
                    break;
            }

            rect = default;
            return false;
        }
    }
}
