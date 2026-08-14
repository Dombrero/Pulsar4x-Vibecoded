using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Pulsar4X.Api;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Snapshot-based production-lines UI (the API-layer port of the old engine-backed IndustryDisplay):
    /// renders an entity's <see cref="IndustryView"/> and submits industry commands.
    /// </summary>
    public sealed class ColonyProductionDisplay
    {
        private string? _selectedProdLine;
        private int _newJobDesignIndex = 0;
        private int _newJobBatchCount = 1;
        private bool _newJobRepeat = false;
        private bool _newJobAutoInstall = true;
        /// <summary>0 = Components, 1 = Colony Installations (Factory new-job panel).</summary>
        private int _newJobCategoryTab = 0;

        internal ColonyProductionDisplay() { }

        public void Display(int entityId, IndustryView? industry, GlobalUIState uiState)
        {
            if (industry == null)
            {
                Vector2 topSize = ImGui.GetContentRegionAvail();
                if (ImGui.BeginChild("NoProductionAvailable", new Vector2(topSize.X, 56f), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Styles.OkColor);
                    ImGui.Text("You need an installation capable of production. Consider importing one.\n\nExamples: Factory, Shipyard or Refinery");
                    ImGui.PopStyleColor();
                }
                ImGui.EndChild();
                return;
            }

            Vector2 windowContentSize = ImGui.GetContentRegionAvail();
            ProductionLineDisplay(entityId, industry, uiState);
            TutorialHighlight.ReportItem(TutorialHighlightRegion.ProductionLines);
            ImGui.SameLine();

            var selectedLine = industry.ProductionLines.FirstOrDefault(l => l.Id == _selectedProdLine);
            if (selectedLine == null)
                return;

            if (ImGui.BeginChild("JobDescriptionPane", new Vector2(windowContentSize.X * 0.5f - 8f, windowContentSize.Y), ImGuiChildFlags.Borders))
            {
                DisplayHelpers.Header("Create a new job for: " + selectedLine.Name);
                NewJobDisplay(entityId, selectedLine, uiState);
            }
            ImGui.EndChild();
            TutorialHighlight.ReportItem(TutorialHighlightRegion.ProductionNewJob);
        }

        private void ProductionLineDisplay(int entityId, IndustryView industry, GlobalUIState uiState)
        {
            if (industry.ProductionLines.Count == 0)
            {
                ImGui.Text("No capacity for construction at this colony.");
                return;
            }

            Vector2 windowContentSize = ImGui.GetContentRegionAvail();
            if (ImGui.BeginChild("ColonyProductionLines", new Vector2(windowContentSize.X * 0.5f, windowContentSize.Y), ImGuiChildFlags.Borders))
            {
                DisplayHelpers.Header("Production Lines");

                foreach (var line in industry.ProductionLines)
                {
                    string headerTitle = line.Name;
                    if (line.Jobs.Count == 0)
                        headerTitle += " (Idle)";
                    ImGui.PushID(line.Id);

                    var pop = false;
                    if (_selectedProdLine == line.Id)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Header, Styles.DescriptiveColor);
                        pop = true;
                    }
                    bool lineOpen = ImGui.CollapsingHeader(headerTitle, ImGuiTreeNodeFlags.DefaultOpen);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Production line: " + line.Name + (line.Jobs.Count == 0
                            ? "\nCurrently idle — queue a job from the panel on the right."
                            : "\n" + line.Jobs.Count + " job(s) in queue."));
                    if (lineOpen)
                    {
                        if (ImGui.Button("+ New Job"))
                        {
                            _selectedProdLine = line.Id;
                            _newJobDesignIndex = 0;
                            _newJobBatchCount = 1;
                            _newJobCategoryTab = 0;
                        }

                        ImGui.SameLine();

                        ImGui.BeginDisabled();
                        if (ImGui.Button("Upgrade " + line.Name))
                        {
                            // TODO: add upgrade functionality
                        }
                        ImGui.EndDisabled();

                        if (line.Jobs.Count > 0)
                        {
                            ImGui.SameLine();
                            ImGui.Text("Progress per day:");
                            ImGui.SameLine();
                            ImGui.PushStyleColor(ImGuiCol.Text, Styles.HighlightColor);
                            ImGui.Text(line.CurrentRatePerDay.ToString());
                            ImGui.PopStyleColor();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Assuming all resources needed are available.");

                            JobsTable(entityId, line, uiState);
                        }
                    }
                    if (pop)
                    {
                        ImGui.PopStyleColor();
                    }
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }

        private void JobsTable(int entityId, ProductionLineView line, GlobalUIState uiState)
        {
            if (ImGui.BeginTable(line.Name, 4, Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.None, 0.3f);
                ImGui.TableSetupColumn("Batch", ImGuiTableColumnFlags.None, 0.1f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.None, 0.3f);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 0.3f);
                ImGui.TableHeadersRow();

                for (int jobIndex = 0; jobIndex < line.Jobs.Count; jobIndex++)
                {
                    var job = line.Jobs[jobIndex];

                    ImGui.TableNextColumn();
                    ImGui.Text(job.Name);
                    if (ImGui.IsItemHovered())
                    {
                        string statusLine = job.MissingResources
                            ? job.Status + " — waiting on resources"
                            : job.Status;
                        DisplayHelpers.DescriptiveTooltip(job.Name, "Production Job",
                            statusLine + "\nBatch: " + job.NumberCompleted + "/" + job.NumberOrdered
                            + (job.Repeat ? " (repeating)" : ""));
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(job.NumberCompleted + "/" + job.NumberOrdered);

                    if (job.Repeat)
                    {
                        ImGui.SameLine();
                        ImGui.Image(uiState.Img_Repeat().ToTextureRef(), new Vector2(16, 16));
                    }

                    ImGui.TableNextColumn();
                    var color = job.MissingResources ? Styles.BadColor : Styles.GoodColor;

                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    if (job.Status == "Processing")
                        ImGui.Text("Processing (" + job.PercentComplete.ToString("0.#") + "%%)");
                    else
                        ImGui.Text(job.Status);
                    ImGui.PopStyleColor();

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
                        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
                        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.1f, 0.1f, 0.1f, 1f));
                        ImGui.BeginTooltip();

                        ImGui.Text("Still needed");
                        if (ImGui.BeginTable(job.JobId + "-need", 2, ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn("Input");
                            ImGui.TableSetupColumn("Amount");
                            ImGui.TableHeadersRow();

                            ImGui.TableNextColumn();
                            ImGui.Text("Industry Points");
                            ImGui.TableNextColumn();
                            ImGui.Text(job.ProductionPointsLeft.ToString());

                            if (job.RemainingRequirements.Count == 0)
                            {
                                ImGui.TableNextColumn();
                                ImGui.TextDisabled("(materials gathered)");
                                ImGui.TableNextColumn();
                                ImGui.TextDisabled("-");
                            }
                            else
                            {
                                foreach (var requirement in job.RemainingRequirements)
                                {
                                    ImGui.TableNextColumn();
                                    ImGui.Text(requirement.Name);
                                    ImGui.TableNextColumn();
                                    ImGui.Text(requirement.Amount.ToString());
                                }
                            }
                            ImGui.EndTable();
                        }

                        ImGui.Spacing();
                        ImGui.Text("Recipe (per unit)");
                        if (ImGui.BeginTable(job.JobId + "-recipe", 2, ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn("Input");
                            ImGui.TableSetupColumn("Amount");
                            ImGui.TableHeadersRow();

                            ImGui.TableNextColumn();
                            ImGui.Text("Industry Points");
                            ImGui.TableNextColumn();
                            ImGui.Text(job.ProductionPointsCost.ToString());

                            foreach (var requirement in job.RecipeRequirements)
                            {
                                ImGui.TableNextColumn();
                                ImGui.Text(requirement.Name);
                                ImGui.TableNextColumn();
                                ImGui.Text(requirement.Amount.ToString());
                            }
                            ImGui.EndTable();
                        }

                        ImGui.EndTooltip();
                        ImGui.PopStyleColor();
                        ImGui.PopStyleVar(2);
                    }
                    ImGui.TableNextColumn();
                    ActionButtons(entityId, line, job.JobId, jobIndex, uiState);
                    ImGui.TableNextRow();
                }
                ImGui.EndTable();
            }
        }

        private void ActionButtons(int entityId, ProductionLineView line, string jobId, int jobIndex, GlobalUIState uiState)
        {
            var invisButtonSize = new Vector2(15, 15);
            ImGui.PushID(jobId);
            if (jobIndex > 0)
            {
                if (ImGui.SmallButton("^"))
                {
                    uiState.GameClient?.SubmitCommandAsync(
                        new ChangeIndustryJobPriorityCommand(entityId, line.Id, jobId, -1));
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Move up in the produciton queue.");
            }
            else
            {
                ImGui.InvisibleButton("invis1", invisButtonSize);
            }
            ImGui.SameLine();

            if (jobIndex < line.Jobs.Count - 1)
            {
                if (ImGui.SmallButton("v"))
                {
                    uiState.GameClient?.SubmitCommandAsync(
                        new ChangeIndustryJobPriorityCommand(entityId, line.Id, jobId, 1));
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Move down in the produciton queue.");
            }
            else
            {
                ImGui.InvisibleButton("invis2", invisButtonSize);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                uiState.GameClient?.SubmitCommandAsync(
                    new CancelIndustryJobCommand(entityId, line.Id, jobId));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancel the job.");
            ImGui.PopID();
        }

        private void NewJobDisplay(int entityId, ProductionLineView line, GlobalUIState uiState)
        {
            if (line.Constructibles.Count == 0)
            {
                ImGui.Text("This production line can't build anything the faction knows how to make.");
                return;
            }

            var components = line.Constructibles.Where(c => !c.IsColonyInstallation).ToList();
            var installations = line.Constructibles.Where(c => c.IsColonyInstallation).ToList();
            bool splitTabs = components.Count > 0 && installations.Count > 0;

            if (splitTabs)
            {
                ImGui.NewLine();
                if (ImGui.BeginTabBar("##factory-job-categories"))
                {
                    if (ImGui.BeginTabItem($"Components ({components.Count})###components"))
                    {
                        if (_newJobCategoryTab != 0)
                        {
                            _newJobCategoryTab = 0;
                            _newJobDesignIndex = 0;
                        }
                        NewJobForm(entityId, line, components, uiState);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem($"Colony Installations ({installations.Count})###installations"))
                    {
                        if (_newJobCategoryTab != 1)
                        {
                            _newJobCategoryTab = 1;
                            _newJobDesignIndex = 0;
                        }
                        NewJobForm(entityId, line, installations, uiState);
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
                return;
            }

            NewJobForm(entityId, line, line.Constructibles, uiState);
        }

        private void NewJobForm(
            int entityId,
            ProductionLineView line,
            IReadOnlyList<ConstructibleItemView> filtered,
            GlobalUIState uiState)
        {
            if (filtered.Count == 0)
            {
                ImGui.Text("Nothing available in this category.");
                return;
            }

            if (_newJobDesignIndex >= filtered.Count)
                _newJobDesignIndex = 0;

            var constructableNames = filtered.Select(c => c.Name).ToArray();

            ImGui.NewLine();
            ImGui.Text("Select a design:");
            int curItemIndex = _newJobDesignIndex;
            if (ImGui.Combo("###newjobselection", ref curItemIndex, constructableNames, constructableNames.Length))
            {
                _newJobDesignIndex = curItemIndex;
            }
            if (ImGui.IsItemHovered())
            {
                var hovered = filtered[_newJobDesignIndex];
                ImGui.SetNextWindowSize(Styles.ToolTipsize);
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(hovered.Name);
                if (!string.IsNullOrEmpty(hovered.ProductionHint))
                {
                    ImGui.Separator();
                    ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                    ImGui.TextWrapped(hovered.ProductionHint);
                    ImGui.PopStyleColor();
                }
                ImGui.EndTooltip();
            }

            var selectedDesign = filtered[_newJobDesignIndex];

            ImGui.NewLine();
            ImGui.Text("Enter the quantity:");
            if (ImGui.InputInt("##batchcount", ref _newJobBatchCount))
            {
                if (_newJobBatchCount < 1)
                    _newJobBatchCount = 1;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The production line will move to the next job in the queue\nafter finishing the number of items requested.");

            CostsDisplay(selectedDesign);

            ImGui.Columns(1);
            ImGui.NewLine();
            ImGui.Checkbox("##repeat", ref _newJobRepeat);
            ImGui.SameLine();
            ImGui.Text("Repeat this job?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A repeat job will run until cancelled.");

            // Only colony buildings — ship components always go to cargo.
            if (selectedDesign.CanAutoInstall)
            {
                ImGui.Checkbox("##autoinstall", ref _newJobAutoInstall);
                ImGui.SameLine();
                ImGui.Text("Auto-install on colony?");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Install this building on the colony when finished.\nUncheck to leave it in cargo instead.");
            }

            ImGui.NewLine();

            if (ImGui.Button("Queue the job to " + line.Name))
            {
                uiState.GameClient?.SubmitCommandAsync(new QueueIndustryJobCommand(
                    entityId, line.Id, selectedDesign.DesignId, _newJobBatchCount,
                    _newJobRepeat, selectedDesign.CanAutoInstall && _newJobAutoInstall));
            }
        }

        private void CostsDisplay(ConstructibleItemView design)
        {
            int quantity = _newJobBatchCount;

            ImGui.NewLine();
            ImGui.Text("Inputs Needed:");
            if (ImGui.BeginTable("JobCostsTables", 4, Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None, 0.4f);
                ImGui.TableSetupColumn("Cost Per Quantity", ImGuiTableColumnFlags.None, 0.2f);
                ImGui.TableSetupColumn("Total Cost", ImGuiTableColumnFlags.None, 0.2f);
                ImGui.TableSetupColumn("Available", ImGuiTableColumnFlags.None, 0.2f);
                ImGui.TableHeadersRow();

                ImGui.TableNextColumn();
                ImGui.Text("");
                ImGui.SameLine();
                ImGui.Text("Industry Points");
                ImGui.TableNextColumn();
                ImGui.Text(design.IndustryPointsPerUnit.ToString());
                ImGui.TableNextColumn();
                ImGui.Text((design.IndustryPointsPerUnit * quantity).ToString());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Total Cost = Cost Per Quantity * Quantity Ordered");
                ImGui.TableNextColumn();
                // Industry points are daily capacity on this line, not a cargo stockpile.
                var pointsPerDay = design.IndustryPointsPerDay;
                ImGui.Text(pointsPerDay > 0
                    ? Stringify.Quantity((long)Math.Round(pointsPerDay)) + "/day"
                    : "0/day");
                if (ImGui.IsItemHovered())
                {
                    double days = pointsPerDay > 0
                        ? (design.IndustryPointsPerUnit * quantity) / pointsPerDay
                        : 0;
                    ImGui.SetTooltip(pointsPerDay > 0
                        ? $"This production line can spend {pointsPerDay:0.##} industry points per day on this job type.\nEstimated time for this batch: ~{days:0.#} day(s)."
                        : "This production line has no industry points for this job type.");
                }
                ImGui.TableNextRow();

                foreach (var cost in design.Costs)
                {
                    var totalCost = quantity * cost.PerUnit;

                    ImGui.TableNextColumn();
                    ImGui.Text("");
                    ImGui.SameLine();
                    ImGui.Text(cost.Name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(BuildCostItemTooltip(cost));
                    ImGui.TableNextColumn();
                    ImGui.Text(cost.PerUnit.ToString());
                    ImGui.TableNextColumn();
                    ImGui.Text(totalCost.ToString());
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Total Cost = Cost Per Output * Quantity Ordered\n" + totalCost + " = " + cost.PerUnit + " * " + quantity);
                    ImGui.TableNextColumn();

                    bool short_ = cost.Available < totalCost;
                    if (short_)
                        ImGui.PushStyleColor(ImGuiCol.Text, cost.CanProduce ? Styles.BadColor : Styles.TerribleColor);

                    ImGui.Text(Stringify.Quantity(cost.Available));

                    if (short_)
                    {
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(BuildShortageTooltip(cost));

                        ImGui.PopStyleColor();
                    }
                    else if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(cost.ProductionHint))
                    {
                        ImGui.SetTooltip(cost.ProductionHint);
                    }
                    ImGui.TableNextRow();
                }

                ImGui.EndTable();
            }

            ImGui.NewLine();
            ImGui.Text("Outputs:");

            if (ImGui.BeginTable("JobOutputsTables", 3, Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None, 0.4f);
                ImGui.TableSetupColumn("Amount Per Quantity", ImGuiTableColumnFlags.None, 0.3f);
                ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.None, 0.3f);
                ImGui.TableHeadersRow();

                ImGui.TableNextColumn();
                ImGui.Text("");
                ImGui.SameLine();
                ImGui.Text(design.Name);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetNextWindowSize(Styles.ToolTipsize);
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(design.Name);
                    ImGui.TextUnformatted(design.IsColonyInstallation
                        ? "Colony installation — can be auto-installed when finished."
                        : "Component or product output for this job.");
                    if (!string.IsNullOrEmpty(design.ProductionHint))
                    {
                        ImGui.Separator();
                        ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                        ImGui.TextWrapped(design.ProductionHint);
                        ImGui.PopStyleColor();
                    }
                    ImGui.EndTooltip();
                }
                ImGui.TableNextColumn();
                ImGui.Text(design.OutputAmount.ToString());
                ImGui.TableNextColumn();
                ImGui.Text((design.OutputAmount * quantity).ToString());

                ImGui.EndTable();
            }
        }

        private static string BuildCostItemTooltip(IndustryCostItem cost)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(cost.Description))
                parts.Add(cost.Description!);
            if (!string.IsNullOrWhiteSpace(cost.ProductionHint))
                parts.Add(cost.ProductionHint!);
            else if (!cost.CanProduce)
                parts.Add("Cannot be produced — import or salvage it.");
            return parts.Count > 0 ? string.Join("\n\n", parts) : cost.Name;
        }

        private static string BuildShortageTooltip(IndustryCostItem cost)
        {
            string shortage = cost.CanProduce
                ? "Not enough " + cost.Name + " available on this colony."
                : "Not enough " + cost.Name + " available on this colony.\nAnd we can't produce this item!";
            if (!string.IsNullOrWhiteSpace(cost.ProductionHint))
                return shortage + "\n\n" + cost.ProductionHint;
            return shortage;
        }
    }
}
