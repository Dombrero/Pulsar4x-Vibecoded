using System.Linq;
using ImGuiNET;

namespace Pulsar4X.Client
{
    public static class ComponentInstancesDBDisplay
    {
        /// <summary>Snapshot-based installations display for UI ported to the API galaxy model.
        /// <paramref name="holderId"/> is the colony/ship the installations belong to (the command target).</summary>
        public static void Display(this Pulsar4X.Api.InstallationsView view, int holderId, GlobalUIState uiState)
        {
            if (ImGui.BeginTable("InstallationTable", 3, Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.None, 0.45f);
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.None, 0.1f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.None, 0.45f);
                ImGui.TableHeadersRow();

                foreach (var group in view.Installations)
                {
                    ImGui.TableNextColumn();
                    ImGui.Text(group.Name);
                    AddContextMenu(group, holderId, uiState);
                    DisplayHelpers.DescriptiveTooltip(group.Name, group.TemplateName,
                        string.IsNullOrEmpty(group.ProductionHint)
                            ? group.Description
                            : (string.IsNullOrEmpty(group.Description)
                                ? group.ProductionHint
                                : group.Description + "\n\n" + group.ProductionHint),
                        null, true);
                    ImGui.TableNextColumn();
                    ImGui.Text(group.Count.ToString());
                    ImGui.TableNextColumn();

                    if (group.OperationalCount > 0 && group.OperationalCount < group.Count)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Styles.OkColor);
                        ImGui.Text("Degraded");
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"{group.OperationalCount} of {group.Count} instances are operational.\nSome units are disabled or offline.");
                    }
                    else if (group.OperationalCount == 0)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Styles.BadColor);
                        ImGui.Text("Disabled");
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("No instances are operational — this installation is fully offline.");
                    }
                    else
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Styles.HighlightColor);
                        ImGui.Text("Operational");
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("All instances are online and contributing.");
                    }
                }
                ImGui.EndTable();
            }
        }

        private static void AddContextMenu(Pulsar4X.Api.InstallationGroup group, int holderId, GlobalUIState uiState)
        {
            ImGui.PushID(group.DesignId);
            if (ImGui.BeginPopupContextItem("###" + group.DesignId))
            {
                ImGui.Text(group.Name);
                ImGui.Separator();
                if (group.CanStore && ImGui.MenuItem("Move to Storage"))
                {
                    uiState.GameClient?.SubmitCommandAsync(
                        new Pulsar4X.Api.UninstallComponentCommand(holderId, group.DesignId));
                }
                ImGui.PushStyleColor(ImGuiCol.Text, Styles.TerribleColor);
                if (ImGui.MenuItem("Destroy"))
                {

                }
                ImGui.PopStyleColor();
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }

    }
}
