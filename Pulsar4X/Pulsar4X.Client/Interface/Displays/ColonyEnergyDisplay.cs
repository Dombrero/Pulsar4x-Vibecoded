using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Pulsar4X.Api;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Colony power dashboard modelled on an EMS overview:
    /// sparkline trends → pie + device cards → selectable history charts.
    /// </summary>
    public sealed class ColonyEnergyDisplay
    {
        private enum Focus { Consumption, Generation }

        private static readonly (string Label, int Hours)[] HistoryRanges =
        {
            ("24 h", 24),
            ("48 h", 48),
            ("7 d", 168),
            ("14 d", 336),
            ("30 d", 720),
            ("90 d", 2160),
            ("1 y", 8760),
            ("5 y", 43800),
            ("10 y", 87600),
        };

        /// <summary>Max points drawn in history charts — longer windows are averaged down.</summary>
        private const int MaxChartPoints = 480;

        private Focus _focus = Focus.Consumption;
        private bool _activeOnly = true;
        private int _historyRangeIndex = 0;

        private static readonly Vector4 Gen = new(0.35f, 0.78f, 0.72f, 1f);
        private static readonly Vector4 Load = new(0.95f, 0.58f, 0.32f, 1f);
        private static readonly Vector4 Dock = new(0.55f, 0.62f, 0.95f, 1f);
        private static readonly Vector4 Soc = new(0.45f, 0.82f, 0.58f, 1f);
        private static readonly Vector4 CardBg = new(0.07f, 0.09f, 0.12f, 0.92f);
        private static readonly Vector4 Track = new(0.14f, 0.16f, 0.20f, 1f);

        private static readonly Vector4[] Palette =
        {
            new(0.55f, 0.45f, 0.92f, 1f),
            new(0.95f, 0.58f, 0.32f, 1f),
            new(0.35f, 0.78f, 0.72f, 1f),
            new(0.55f, 0.62f, 0.95f, 1f),
            new(0.92f, 0.72f, 0.35f, 1f),
            new(0.88f, 0.42f, 0.48f, 1f),
        };

        public void Display(ColonyPowerView power)
        {
            ImGui.PushFont(Styles.MediumFont, 16f);
            ImGui.TextUnformatted("Power management");
            ImGui.PopFont();
            ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
            ImGui.TextWrapped("Generation, load and storage for this colony — live mix and hourly history.");
            ImGui.PopStyleColor();

            ImGui.Spacing();
            DrawToolbar(power);
            ImGui.Spacing();

            // --- Top: three trend cards ---
            float gap = ImGui.GetStyle().ItemSpacing.X;
            float trendW = (ImGui.GetContentRegionAvail().X - gap * 2) / 3f;
            float trendH = 88f;

            string historyLabel = HistoryRanges[_historyRangeIndex].Label;
            int historyHours = HistoryRanges[_historyRangeIndex].Hours;
            var histFull = power.Histogram;
            var hist = DownsampleForChart(WindowHistogram(histFull, historyHours));
            TrendCard("Generation", $"{power.GenerationKW:N0} kW", Gen,
                hist.Select(h => (float)h.GenerationKW).ToArray(), trendW, trendH,
                "Total electrical power currently produced by colony generators.");
            ImGui.SameLine();
            TrendCard("Demand", $"{power.DemandKW + DockTotal(power):N0} kW", Load,
                hist.Select(h => (float)(h.DemandKW + h.DockKW)).ToArray(), trendW, trendH,
                "Total power demand from installations and docked ships.");
            ImGui.SameLine();
            float[] socSeries = hist.Select(h =>
                power.CapacityKJ > 0 ? (float)(100.0 * h.StoredKJ / power.CapacityKJ) : 0f).ToArray();
            TrendCard("Battery SOC", $"{power.StoredPercent:0.#}%", Soc, socSeries, trendW, trendH,
                "State of charge — stored energy as a percentage of battery capacity.");

            ImGui.Spacing();

            // --- Middle: donut + device cards ---
            var devices = BuildDevices(power);
            float midH = 210f;
            float donutW = ImGui.GetContentRegionAvail().X * 0.38f;

            if (ImGui.BeginChild("##power-donut", new Vector2(donutW, midH), ImGuiChildFlags.Borders))
            {
                ImGui.TextDisabled(_focus == Focus.Consumption ? "Average power by load" : "Average power by plant");
                DrawDonut(devices, new Vector2(ImGui.GetContentRegionAvail().X, midH - 48f));
            }
            ImGui.EndChild();

            ImGui.SameLine();

            if (ImGui.BeginChild("##power-devices", new Vector2(0, midH), ImGuiChildFlags.None))
            {
                float cardW = (ImGui.GetContentRegionAvail().X - gap * 2) / 3f;
                var top = devices.Take(3).ToList();
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    if (i < top.Count)
                        DeviceCard(top[i], devices.Sum(d => d.KW), cardW, midH - 4f);
                    else
                        EmptyDeviceCard(cardW, midH - 4f);
                }
            }
            ImGui.EndChild();

            ImGui.Spacing();
            DrawHistoryRangeSelector(histFull.Count);

            // --- Bottom: history charts for the selected window ---
            float bottomH = 120f;
            float bottomW = (ImGui.GetContentRegionAvail().X - gap * 2) / 3f;

            if (ImGui.BeginChild("##hist-energy", new Vector2(bottomW, bottomH), ImGuiChildFlags.Borders))
            {
                ImGui.TextDisabled($"Energy · {historyLabel}");
                DrawStackedHistory(hist, power.CapacityKJ, new Vector2(ImGui.GetContentRegionAvail().X, bottomH - 36f));
            }
            ImGui.EndChild();

            ImGui.SameLine();
            if (ImGui.BeginChild("##hist-draw", new Vector2(bottomW, bottomH), ImGuiChildFlags.Borders))
            {
                ImGui.TextDisabled($"Load draw · {historyLabel}");
                DrawAreaHistory(hist, new Vector2(ImGui.GetContentRegionAvail().X, bottomH - 36f));
            }
            ImGui.EndChild();

            ImGui.SameLine();
            if (ImGui.BeginChild("##hist-balance", new Vector2(0, bottomH), ImGuiChildFlags.Borders))
            {
                ImGui.TextDisabled($"Gen vs demand · {historyLabel}");
                DrawLineHistory(hist, new Vector2(ImGui.GetContentRegionAvail().X, bottomH - 36f));
            }
            ImGui.EndChild();

            if (power.IsUndersupplied)
            {
                ImGui.Spacing();
                double runPct = power.PowerEfficiency * 100.0;
                double cutPct = Math.Max(0, 100.0 - runPct);
                ImGui.TextColored(Styles.BadColor,
                    $"Undersupplied — power-gated production at {runPct:0.#}% (−{cutPct:0.#}%).");
            }
            if (power.CapacityKJ <= 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped("No colony batteries — generation cannot be stored and ships cannot dock-recharge.");
            }

            if (histFull.Count < 2)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("History fills in as the colony power tick runs (hourly).");
            }
            else if (histFull.Count < historyHours)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Showing {histFull.Count} h of history — fills up to {historyLabel} as time advances.");
            }

            ImGui.Spacing();
            DrawDetailLists(power);
        }

        private void DrawDetailLists(ColonyPowerView power)
        {
            var generators = power.Generators
                .Where(g => !_activeOnly || (g.IsEnabled && g.GenerationKW > 0))
                .OrderByDescending(g => g.GenerationKW)
                .ToList();

            var loads = power.Consumers
                .Where(c => !_activeOnly || (c.IsEnabled && c.DemandKW > 0))
                .OrderByDescending(c => c.DemandKW)
                .ToList();

            var docks = power.DockConsumers
                .Where(c => !_activeOnly || c.DemandKW > 0)
                .OrderByDescending(c => c.DemandKW)
                .ToList();

            DisplayHelpers.Header($"Generators ({generators.Count})");
            if (generators.Count == 0)
            {
                ImGui.TextDisabled(_activeOnly
                    ? "No active generators."
                    : "No power plants installed.");
            }
            else if (ImGui.BeginTable("ColonyEnergyGenerators", 4,
                         Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Generator", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupColumn("Output", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                ImGui.TableSetupColumn("Share", ImGuiTableColumnFlags.WidthStretch, 0.18f);
                ImGui.TableHeadersRow();

                double genTotal = Math.Max(1e-9, power.GenerationKW);
                foreach (var plant in generators)
                {
                    ImGui.TableNextColumn();
                    if (!plant.IsEnabled)
                        ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                    ImGui.TextUnformatted(plant.Name);
                    DisplayHelpers.DescriptiveTooltip(plant.Name, "Generator",
                        "Installed power plant. Output is its current contribution to colony generation.");
                    if (!plant.IsEnabled)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled("(off)");
                        ImGui.PopStyleColor();
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(plant.Count.ToString());
                    ImGui.TableNextColumn();
                    ImGui.Text($"{plant.GenerationKW:N0} kW");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{100.0 * plant.GenerationKW / genTotal:0.#}%");
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();
            int consumerCount = loads.Count + docks.Count;
            DisplayHelpers.Header($"Consumers ({consumerCount})");
            if (consumerCount == 0)
            {
                ImGui.TextDisabled(_activeOnly
                    ? "No active power demand."
                    : "No consumers installed.");
            }
            else if (ImGui.BeginTable("ColonyEnergyConsumers", 4,
                         Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Consumer", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupColumn("Demand", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                ImGui.TableSetupColumn("Share", ImGuiTableColumnFlags.WidthStretch, 0.18f);
                ImGui.TableHeadersRow();

                double loadTotal = Math.Max(1e-9, power.DemandKW + DockTotal(power));
                foreach (var load in loads)
                    DrawConsumerRow(load, loadTotal, dock: false);
                foreach (var load in docks)
                    DrawConsumerRow(load, loadTotal, dock: true);

                ImGui.EndTable();
            }
        }

        private static void DrawConsumerRow(ColonyPowerConsumer load, double groupTotal, bool dock)
        {
            ImGui.TableNextColumn();
            if (!load.IsEnabled)
                ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
            ImGui.TextUnformatted(load.Name);
            DisplayHelpers.DescriptiveTooltip(load.Name, dock ? "Dock consumer" : "Consumer",
                dock
                    ? "Ship drawing dock recharge from this colony's batteries."
                    : "Installation drawing power from the colony grid.");
            if (dock)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(dock)");
            }
            if (!load.IsEnabled)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(off)");
                ImGui.PopStyleColor();
            }

            ImGui.TableNextColumn();
            ImGui.Text(load.Count.ToString());
            ImGui.TableNextColumn();
            ImGui.Text($"{load.DemandKW:N0} kW");
            ImGui.TableNextColumn();
            ImGui.Text($"{100.0 * load.DemandKW / groupTotal:0.#}%");
        }

        private void DrawToolbar(ColonyPowerView power)
        {
            if (ImGui.RadioButton("Consumption", _focus == Focus.Consumption))
                _focus = Focus.Consumption;
            ImGui.SameLine();
            if (ImGui.RadioButton("Generation", _focus == Focus.Generation))
                _focus = Focus.Generation;
            ImGui.SameLine();
            ImGui.Checkbox("Active only", ref _activeOnly);
            ImGui.SameLine();
            double net = power.GenerationKW - power.DemandKW - DockTotal(power);
            ImGui.TextDisabled($"Net {(net >= 0 ? "+" : "")}{net:N0} kW");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Net power balance: generation minus facility demand and dock draw.\nPositive means surplus that can charge batteries.");
            ImGui.SameLine();
            ImGui.TextDisabled("·");
            ImGui.SameLine();
            ImGui.TextDisabled($"Dock {DockTotal(power):N0} kW");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Power currently drawn by docked ships recharging from this colony.");
            ImGui.SameLine();
            ImGui.TextDisabled("·");
            ImGui.SameLine();
            ImGui.TextDisabled($"Eff {power.PowerEfficiency * 100:0.#}%");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Fraction of power-gated production that can run given current supply.\nBelow 100% when the colony is undersupplied.");
        }

        private void DrawHistoryRangeSelector(int samplesAvailable)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("History window");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            string preview = HistoryRanges[_historyRangeIndex].Label;
            if (ImGui.BeginCombo("##history-range", preview))
            {
                for (int i = 0; i < HistoryRanges.Length; i++)
                {
                    bool selected = i == _historyRangeIndex;
                    if (ImGui.Selectable(HistoryRanges[i].Label, selected))
                        _historyRangeIndex = i;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            int capped = Math.Min(samplesAvailable, HistoryRanges[_historyRangeIndex].Hours);
            ImGui.TextDisabled($"({FormatSampleCount(capped)} / {HistoryRanges[_historyRangeIndex].Label} stored hourly)");
        }

        private static string FormatSampleCount(int hours)
        {
            if (hours >= 8760)
                return $"{hours / 8760.0:0.#} y";
            if (hours >= 168)
                return $"{hours / 24} d";
            return $"{hours} h";
        }

        private static IReadOnlyList<ColonyPowerHistogramPoint> WindowHistogram(
            IReadOnlyList<ColonyPowerHistogramPoint> hist, int hours)
        {
            if (hist.Count == 0 || hist.Count <= hours)
                return hist;

            var windowed = new List<ColonyPowerHistogramPoint>(hours);
            for (int i = hist.Count - hours; i < hist.Count; i++)
                windowed.Add(hist[i]);
            return windowed;
        }

        private static IReadOnlyList<ColonyPowerHistogramPoint> DownsampleForChart(
            IReadOnlyList<ColonyPowerHistogramPoint> hist)
        {
            if (hist.Count <= MaxChartPoints)
                return hist;

            var result = new List<ColonyPowerHistogramPoint>(MaxChartPoints);
            double bucket = (double)hist.Count / MaxChartPoints;
            for (int i = 0; i < MaxChartPoints; i++)
            {
                int start = (int)(i * bucket);
                int end = Math.Min(hist.Count, (int)((i + 1) * bucket));
                if (end <= start)
                    end = Math.Min(hist.Count, start + 1);

                double gen = 0, dem = 0, dock = 0, stored = 0;
                int n = end - start;
                for (int j = start; j < end; j++)
                {
                    gen += hist[j].GenerationKW;
                    dem += hist[j].DemandKW;
                    dock += hist[j].DockKW;
                    stored += hist[j].StoredKJ;
                }

                result.Add(new ColonyPowerHistogramPoint(gen / n, dem / n, dock / n, stored / n));
            }

            return result;
        }

        private List<DeviceSlice> BuildDevices(ColonyPowerView power)
        {
            var list = new List<DeviceSlice>();
            if (_focus == Focus.Generation)
            {
                foreach (var g in power.Generators)
                {
                    if (_activeOnly && (!g.IsEnabled || g.GenerationKW <= 0))
                        continue;
                    list.Add(new DeviceSlice(g.Name, g.GenerationKW, g.Count, "plant"));
                }
            }
            else
            {
                foreach (var c in power.Consumers)
                {
                    if (_activeOnly && (!c.IsEnabled || c.DemandKW <= 0))
                        continue;
                    list.Add(new DeviceSlice(c.Name, c.DemandKW, c.Count, "load"));
                }
                foreach (var c in power.DockConsumers)
                {
                    if (_activeOnly && c.DemandKW <= 0)
                        continue;
                    list.Add(new DeviceSlice(c.Name, c.DemandKW, c.Count, "dock"));
                }
            }

            return list.OrderByDescending(d => d.KW).ToList();
        }

        private static void TrendCard(string title, string value, Vector4 color, float[] series, float width, float height, string? tooltip = null)
        {
            var draw = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##trend" + title, new Vector2(width, height));
            if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            var p1 = new Vector2(p0.X + width, p0.Y + height);

            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(CardBg), 4f);
            draw.AddText(new Vector2(p0.X + 10f, p0.Y + 8f),
                ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor), title);
            draw.AddText(new Vector2(p0.X + 10f, p0.Y + 26f),
                ImGui.ColorConvertFloat4ToU32(color), value);

            var plot0 = new Vector2(p0.X + 8f, p0.Y + 48f);
            var plot1 = new Vector2(p1.X - 8f, p1.Y - 8f);
            DrawSparkline(draw, plot0, plot1, series, color);
        }

        private static void DrawSparkline(ImDrawListPtr draw, Vector2 p0, Vector2 p1, float[] series, Vector4 color)
        {
            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(Track), 2f);
            if (series.Length < 2)
                return;

            float min = series.Min();
            float max = series.Max();
            if (Math.Abs(max - min) < 1e-6f)
            {
                max = min + 1f;
                min = Math.Max(0, min - 0.1f);
            }

            float w = p1.X - p0.X;
            float h = p1.Y - p0.Y;
            uint col = ImGui.ColorConvertFloat4ToU32(color);
            for (int i = 1; i < series.Length; i++)
            {
                float x0 = p0.X + w * (i - 1) / (series.Length - 1);
                float x1 = p0.X + w * i / (series.Length - 1);
                float y0 = p1.Y - h * (series[i - 1] - min) / (max - min);
                float y1 = p1.Y - h * (series[i] - min) / (max - min);
                draw.AddLine(new Vector2(x0, y0), new Vector2(x1, y1), col, 1.8f);
            }
        }

        private void DrawDonut(List<DeviceSlice> devices, Vector2 size)
        {
            var draw = ImGui.GetWindowDrawList();
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##donut", size);

            float radius = Math.Min(size.X, size.Y) * 0.38f;
            var center = new Vector2(cursor.X + size.X * 0.35f, cursor.Y + size.Y * 0.5f);
            double total = devices.Sum(d => d.KW);
            if (total <= 0 || devices.Count == 0)
            {
                draw.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(Track), 48, 2f);
                var na = ImGui.CalcTextSize("No load");
                draw.AddText(new Vector2(center.X - na.X * 0.5f, center.Y - na.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor), "No load");
                return;
            }

            float angle = -MathF.PI / 2f;
            for (int i = 0; i < devices.Count; i++)
            {
                float sweep = (float)(devices[i].KW / total * Math.PI * 2);
                if (sweep <= 0f)
                    continue;

                uint col = ImGui.ColorConvertFloat4ToU32(Palette[i % Palette.Length]);
                // Pie slice (center → outer arc) is convex, so PathFillConvex works correctly.
                int segments = Math.Max(3, (int)(sweep / (MathF.PI * 2f) * 64f));
                draw.PathClear();
                draw.PathLineTo(center);
                draw.PathArcTo(center, radius, angle, angle + sweep, segments);
                draw.PathFillConvex(col);
                angle += sweep;
            }

            string centerText = $"{total:N0}\nkW";
            var lines = centerText.Split('\n');
            float y = center.Y - ImGui.GetTextLineHeight();
            uint labelBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.07f, 0.1f, 0.75f));
            float labelPad = 4f;
            float labelW = 0f;
            float labelH = 0f;
            foreach (var line in lines)
            {
                var s = ImGui.CalcTextSize(line);
                labelW = Math.Max(labelW, s.X);
                labelH += s.Y;
            }
            draw.AddRectFilled(
                new Vector2(center.X - labelW * 0.5f - labelPad, center.Y - labelH * 0.5f - labelPad),
                new Vector2(center.X + labelW * 0.5f + labelPad, center.Y + labelH * 0.5f + labelPad),
                labelBg, 3f);
            y = center.Y - labelH * 0.5f;
            foreach (var line in lines)
            {
                var s = ImGui.CalcTextSize(line);
                draw.AddText(new Vector2(center.X - s.X * 0.5f, y),
                    ImGui.ColorConvertFloat4ToU32(Styles.NeutralColor), line);
                y += s.Y;
            }

            // Legend on the right of the pie
            float lx = cursor.X + size.X * 0.62f;
            float ly = cursor.Y + 12f;
            for (int i = 0; i < Math.Min(devices.Count, 6); i++)
            {
                var d = devices[i];
                double pct = 100.0 * d.KW / total;
                var sw = new Vector2(lx, ly);
                draw.AddRectFilled(sw, new Vector2(sw.X + 8f, sw.Y + 8f),
                    ImGui.ColorConvertFloat4ToU32(Palette[i % Palette.Length]), 1f);
                draw.AddText(new Vector2(lx + 12f, ly - 2f),
                    ImGui.ColorConvertFloat4ToU32(Styles.NeutralColor),
                    $"{d.Name}  {pct:0.#}%");
                ly += ImGui.GetTextLineHeight() + 4f;
            }
        }

        private void DeviceCard(DeviceSlice device, double groupTotal, float width, float height)
        {
            var draw = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##dev" + device.Name, new Vector2(width, height));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(device.Name);
            var p1 = new Vector2(p0.X + width, p0.Y + height);
            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(CardBg), 4f);

            draw.AddText(new Vector2(p0.X + 10f, p0.Y + 8f),
                ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor), device.Kind.ToUpperInvariant());
            draw.AddText(new Vector2(p0.X + 10f, p0.Y + 26f),
                ImGui.ColorConvertFloat4ToU32(Styles.NeutralColor), Truncate(device.Name, 16));

            float share = groupTotal > 0 ? (float)(device.KW / groupTotal) : 0f;
            var gaugeCenter = new Vector2(p0.X + width * 0.5f, p0.Y + height * 0.58f);
            DrawRingGauge(draw, gaugeCenter, 34f, share, Palette[Math.Abs(device.Name.GetHashCode()) % Palette.Length],
                $"{device.KW:N0}", "kW");

            draw.AddText(new Vector2(p0.X + 10f, p1.Y - 22f),
                ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor),
                $"Share {share * 100f:0.#}%  ·  ×{device.Count}");
        }

        private static void EmptyDeviceCard(float width, float height)
        {
            var draw = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##devempty", new Vector2(width, height));
            draw.AddRectFilled(p0, new Vector2(p0.X + width, p0.Y + height),
                ImGui.ColorConvertFloat4ToU32(CardBg), 4f);
            draw.AddText(new Vector2(p0.X + 10f, p0.Y + height * 0.45f),
                ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor), "—");
        }

        private static void DrawRingGauge(
            ImDrawListPtr draw, Vector2 center, float radius, float value, Vector4 color, string centerText, string label)
        {
            value = Math.Clamp(value, 0f, 1f);
            uint dim = ImGui.ColorConvertFloat4ToU32(Track);
            uint col = ImGui.ColorConvertFloat4ToU32(color);
            draw.AddCircle(center, radius, dim, 48, 5f);
            if (value > 0)
            {
                float start = -MathF.PI / 2f;
                draw.PathArcTo(center, radius, start, start + value * MathF.PI * 2f, 48);
                draw.PathStroke(col, ImDrawFlags.None, 5f);
            }

            var ts = ImGui.CalcTextSize(centerText);
            draw.AddText(new Vector2(center.X - ts.X * 0.5f, center.Y - ts.Y * 0.65f), col, centerText);
            var ls = ImGui.CalcTextSize(label);
            draw.AddText(new Vector2(center.X - ls.X * 0.5f, center.Y + 4f),
                ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor), label);
        }

        private static void DrawStackedHistory(
            IReadOnlyList<ColonyPowerHistogramPoint> hist, double capacityKJ, Vector2 size)
        {
            var draw = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##stackhist", size);
            var p1 = new Vector2(p0.X + size.X, p0.Y + size.Y);
            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(Track), 2f);

            if (hist.Count == 0)
                return;

            double maxY = hist.Max(h => h.DemandKW + h.DockKW + h.GenerationKW * 0.01);
            maxY = Math.Max(maxY, 1);

            float barW = size.X / Math.Max(1, hist.Count);
            for (int i = 0; i < hist.Count; i++)
            {
                var h = hist[i];
                float x = p0.X + i * barW + 1f;
                float y = p1.Y;
                float demandH = (float)(size.Y * h.DemandKW / maxY);
                float dockH = (float)(size.Y * h.DockKW / maxY);

                if (demandH > 0)
                {
                    draw.AddRectFilled(new Vector2(x, y - demandH), new Vector2(x + barW - 2f, y),
                        ImGui.ColorConvertFloat4ToU32(Load));
                    y -= demandH;
                }
                if (dockH > 0)
                {
                    draw.AddRectFilled(new Vector2(x, y - dockH), new Vector2(x + barW - 2f, y),
                        ImGui.ColorConvertFloat4ToU32(Dock));
                }
            }
        }

        private static void DrawAreaHistory(IReadOnlyList<ColonyPowerHistogramPoint> hist, Vector2 size)
        {
            var draw = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##areahist", size);
            var p1 = new Vector2(p0.X + size.X, p0.Y + size.Y);
            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(Track), 2f);
            if (hist.Count < 2)
                return;

            double maxY = Math.Max(1, hist.Max(h => h.DemandKW + h.DockKW));
            uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(Load.X, Load.Y, Load.Z, 0.35f));
            uint line = ImGui.ColorConvertFloat4ToU32(Load);

            draw.PathClear();
            for (int i = 0; i < hist.Count; i++)
            {
                float x = p0.X + size.X * i / (hist.Count - 1);
                float y = p1.Y - size.Y * (float)((hist[i].DemandKW + hist[i].DockKW) / maxY);
                draw.PathLineTo(new Vector2(x, y));
            }
            draw.PathLineTo(p1);
            draw.PathLineTo(new Vector2(p0.X, p1.Y));
            draw.PathFillConvex(fill);

            for (int i = 1; i < hist.Count; i++)
            {
                float x0 = p0.X + size.X * (i - 1) / (hist.Count - 1);
                float x1 = p0.X + size.X * i / (hist.Count - 1);
                float y0 = p1.Y - size.Y * (float)((hist[i - 1].DemandKW + hist[i - 1].DockKW) / maxY);
                float y1 = p1.Y - size.Y * (float)((hist[i].DemandKW + hist[i].DockKW) / maxY);
                draw.AddLine(new Vector2(x0, y0), new Vector2(x1, y1), line, 1.5f);
            }
        }

        private static void DrawLineHistory(IReadOnlyList<ColonyPowerHistogramPoint> hist, Vector2 size)
        {
            var draw = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##linehist", size);
            var p1 = new Vector2(p0.X + size.X, p0.Y + size.Y);
            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(Track), 2f);
            if (hist.Count < 2)
                return;

            double maxY = Math.Max(1, hist.Max(h => Math.Max(h.GenerationKW, h.DemandKW + h.DockKW)));
            DrawSeries(draw, p0, p1, hist.Select(h => h.GenerationKW).ToArray(), maxY, Gen);
            DrawSeries(draw, p0, p1, hist.Select(h => h.DemandKW + h.DockKW).ToArray(), maxY, Load);
        }

        private static void DrawSeries(
            ImDrawListPtr draw, Vector2 p0, Vector2 p1, double[] values, double maxY, Vector4 color)
        {
            uint col = ImGui.ColorConvertFloat4ToU32(color);
            float w = p1.X - p0.X;
            float h = p1.Y - p0.Y;
            for (int i = 1; i < values.Length; i++)
            {
                float x0 = p0.X + w * (i - 1) / (values.Length - 1);
                float x1 = p0.X + w * i / (values.Length - 1);
                float y0 = p1.Y - h * (float)(values[i - 1] / maxY);
                float y1 = p1.Y - h * (float)(values[i] / maxY);
                draw.AddLine(new Vector2(x0, y0), new Vector2(x1, y1), col, 1.6f);
            }
        }

        private static double DockTotal(ColonyPowerView power)
            => power.DockConsumers.Sum(c => c.DemandKW);

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private readonly record struct DeviceSlice(string Name, double KW, int Count, string Kind);
    }
}
