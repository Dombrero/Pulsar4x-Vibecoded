using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Pulsar4X.Api;
using Pulsar4X.Client.Interface.Widgets;

namespace Pulsar4X.Client
{
    public class FleetWindow : UniquePulsarGuiWindow<FleetWindow>
    {
        private enum IssueOrderType
        {
            MoveTo,
            GeoSurvey,
            GravSurvey,
            Jump,
            RefuelAt,
        }

        private IssueOrderType selectedIssueOrderType = IssueOrderType.MoveTo;

        private int? selectedFleetId = null;
        // Re-selects the first root fleet after connect/faction change, mirroring the old default selection.
        private bool autoSelectFirstFleet = true;
        private int dragFleetId = -1;
        private Dictionary<int, bool> selectedShips = new ();
        private Dictionary<int, bool> selectedUnattachedShips = new ();

        /// <summary>The id of the fleet this window is managing, or null when none is selected.</summary>
        public int? SelectedFleetId => selectedFleetId;

        // The snapshot of the selected fleet, re-resolved each frame from the galaxy model (fleet
        // pushes replace the whole tree, so cached FleetSnapshot references go stale).
        private FleetSnapshot? selectedFleet = null;

        // ----- Standing Orders editor -----
        // The editor works on a local copy of the fleet's StandingOrders snapshot; Save replaces
        // the fleet's whole list with one SetStandingOrdersCommand.

        private sealed class StandingOrderEdit
        {
            public byte[] NameBuffer = new byte[32];
            public List<StandingOrderConditionEdit> Conditions = new();
            public List<string> Actions = new();
        }

        private sealed class StandingOrderConditionEdit
        {
            public string ConditionType = "";
            public StandingOrderComparison Comparison;
            public float Threshold;
            /// <summary>How this condition combines with the next one.</summary>
            public StandingOrderLogic Logic = StandingOrderLogic.And;
        }

        // Display registry for the contract's StandingOrderTypes ids.
        // Resupply is intentionally omitted: the action is a no-op stub that caused standing loops.
        // Refuel already handles move-to-colony + fuel transfer.
        private static readonly (string Id, string Label)[] StandingOrderActionTypes =
        {
            (StandingOrderTypes.MoveToNearestColony, "Move to Nearest Colony"),
            (StandingOrderTypes.MoveToNearestGeoSurvey, "Geo Survey Nearest Body"),
            (StandingOrderTypes.MoveToNearestGravSurvey, "Grav Survey Nearest Anomaly"),
            (StandingOrderTypes.MoveToNearestAnomaly, "Move to Nearest Anomaly"),
            (StandingOrderTypes.Refuel, "Refuel"),
        };

        private static readonly (string Id, string Label, string Description, float Min, float Max)[] StandingOrderConditionTypes =
        {
            (StandingOrderTypes.FuelCondition, "Fuel (Fleet Avg)", "percent", 0, 100),
            (StandingOrderTypes.HealthCondition, "Health (Fleet Avg)", "percent", 0, 100),
            (StandingOrderTypes.CargoFillCondition, "Cargo Fill (Fleet Avg)", "percent", 0, 100),
            (StandingOrderTypes.UnsurveyedGeoCondition, "Unsurveyed Geo Targets", "targets", 0, 50),
            (StandingOrderTypes.UnsurveyedAnomalyCondition, "Unsurveyed Anomalies", "targets", 0, 50),
        };

        private static readonly string[] orderComparisons = { "<", "<=", "=", ">", ">=" };

        /// <summary>
        /// Sensible defaults when adding a condition — Fuel/Health/Cargo are "below X",
        /// unsurveyed counts are "above 0" (LessThan 0 can never fire).
        /// </summary>
        private static (StandingOrderComparison Comparison, float Threshold) DefaultForCondition(string conditionTypeId)
            => conditionTypeId switch
            {
                StandingOrderTypes.FuelCondition => (StandingOrderComparison.LessThan, 30f),
                StandingOrderTypes.HealthCondition => (StandingOrderComparison.LessThan, 50f),
                StandingOrderTypes.CargoFillCondition => (StandingOrderComparison.GreaterThanOrEqual, 90f),
                StandingOrderTypes.UnsurveyedGeoCondition => (StandingOrderComparison.GreaterThan, 0f),
                StandingOrderTypes.UnsurveyedAnomalyCondition => (StandingOrderComparison.GreaterThan, 0f),
                _ => (StandingOrderComparison.LessThan, 30f),
            };

        private List<StandingOrderEdit>? editedOrders;
        private IReadOnlyList<StandingOrder>? editedOrdersSource;
        private bool standingOrdersDirty;
        private int selectedOrderIndex = -1;
        private int orderActionsIndex = 0;
        private int orderConditionsIndex = 0;

        private FleetWindow()
        {
            _uiState.OnFactionChanged += FactionChanged;
        }
        internal static FleetWindow GetInstance()
        {
            if(_uiState.TryGetUniqueWindow<FleetWindow>(out var window))
            {
                return window;
            }

            return _uiState.AddUniqueWindow(new FleetWindow());
        }

        private void FactionChanged(GlobalUIState uiState)
        {
            SelectFleet(null);
            autoSelectFirstFleet = true;
        }

        public void SelectFleet(int? fleetId)
        {
            selectedFleetId = fleetId;
            selectedShips = new ();
            autoSelectFirstFleet = false;
            editedOrders = null;
            editedOrdersSource = null;
            standingOrdersDirty = false;
            selectedOrderIndex = -1;
        }

        private static FleetSnapshot? FindFleet(IReadOnlyList<FleetSnapshot> fleets, int fleetId)
        {
            foreach(var fleet in fleets)
            {
                if(fleet.Id == fleetId) return fleet;
                if(FindFleet(fleet.SubFleets, fleetId) is { } nested) return nested;
            }
            return null;
        }

        internal override void Display()
        {
            if(!IsActive) return;

            var galaxy = _uiState.GameClient?.Galaxy;
            if(galaxy == null) return;

            if(autoSelectFirstFleet && galaxy.Fleets.Count > 0)
            {
                SelectFleet(galaxy.Fleets[0].Id);
            }

            // Resolve the selection against the current push; a disbanded fleet drops the selection.
            selectedFleet = selectedFleetId is { } id ? FindFleet(galaxy.Fleets, id) : null;

            if(Window.Begin("Fleet Management", ref IsActive, _flags))
            {
                DisplayFleetList(galaxy);

                if(selectedFleet != null)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosY(27f);
                    var ysize = ImGui.GetContentRegionAvail().Y;
                    DisplayShips();
                    ImGui.SetCursorPosY(ysize * 0.5f);
                    DisplayOrders();

                    ImGui.SameLine();
                    ImGui.SetCursorPosY(27f);

                    DisplayTabs(galaxy);
                }
            }

            TutorialHighlight.ReportCurrentWindow(TutorialHighlightRegion.WindowFleetManagement);
            Window.End();
        }

        private void DisplayTabs(IClientGalaxy galaxy)
        {
            if(selectedFleet == null) return;

            if(ImGui.BeginChild("FleetTabs"))
            {
                ImGui.BeginTabBar("FleetTabBar", ImGuiTabBarFlags.None);

                if(ImGui.BeginTabItem("Summary"))
                {
                    Vector2 windowContentSize = ImGui.GetContentRegionAvail();
                    var firstChildSize = new Vector2(windowContentSize.X * 0.99f, windowContentSize.Y);
                    if (ImGui.BeginChild("FleetSummary1", firstChildSize, ImGuiChildFlags.Borders))
                    {
                        if (ImGui.CollapsingHeader("Fleet Information", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            ImGui.Columns(2);
                            ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                            ImGui.Text("Name");
                            ImGui.PopStyleColor();
                            ImGui.NextColumn();
                            ImGui.Text(selectedFleet.Name);
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Rename###fleet-rename"))
                                OpenFleetRename(selectedFleet);
                            ImGui.NextColumn();
                            ImGui.Separator();
                            DisplayHelpers.PrintRow("Flagship", selectedFleet.FlagshipName ?? "-");
                            DisplayHelpers.PrintRow("Commander", selectedFleet.FlagshipName == null ? "-" : selectedFleet.CommanderName ?? "None");

                            // Current system
                            ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                            ImGui.Text("Current System");
                            ImGui.PopStyleColor();
                            ImGui.NextColumn();
                            if (ImGui.SmallButton(selectedFleet.SystemName ?? "Unknown"))
                            {
                                if(selectedFleet.SystemId != null)
                                    _uiState.SetActiveSystem(selectedFleet.SystemId);
                            }
                            ImGui.NextColumn();
                            ImGui.Separator();

                            ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                            ImGui.Text("Orbiting");
                            ImGui.PopStyleColor();
                            ImGui.NextColumn();
                            // The server already resolved this to the nearest faction-visible ancestor
                            // (hidden entities like un-surveyed anomalies are skipped).
                            if (ImGui.SmallButton(selectedFleet.OrbitingName ?? "Unknown"))
                            {
                                if(selectedFleet.OrbitingEntityId is { } orbitingId && selectedFleet.SystemId != null)
                                    _uiState.EntityClicked(orbitingId, selectedFleet.SystemId, MouseButtons.Primary);
                            }
                            ImGui.NextColumn();
                            ImGui.Separator();
                            DisplayHelpers.PrintRow("Ships", selectedFleet.Ships.Count.ToString());
                        }
                        ImGui.Columns(1);
                    }
                    ImGui.EndChild();
                    ImGui.SameLine();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Issue Orders"))
                {
                    TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetTabIssueOrders);
                    var size = ImGui.GetContentRegionAvail();
                    var firstChildSize = new Vector2(size.X * 0.27f, size.Y);
                    var secondChildSize = new Vector2(size.X * 0.73f - (size.X * 0.01f), size.Y);
                    if(ImGui.BeginChild("IssueOrders-List", firstChildSize, ImGuiChildFlags.Borders))
                    {
                        DisplayHelpers.Header("Available Orders");

                        if(ImGui.Selectable("Move to ...", selectedIssueOrderType == IssueOrderType.MoveTo))
                        {
                            selectedIssueOrderType = IssueOrderType.MoveTo;
                        }
                        if(ImGui.Selectable("Refuel at ...", selectedIssueOrderType == IssueOrderType.RefuelAt))
                        {
                            selectedIssueOrderType = IssueOrderType.RefuelAt;
                        }
                        if(selectedFleet.CanGeoSurvey && ImGui.Selectable("Geo Survey ...", selectedIssueOrderType == IssueOrderType.GeoSurvey))
                        {
                            selectedIssueOrderType = IssueOrderType.GeoSurvey;
                        }
                        if (selectedFleet.CanGeoSurvey)
                            TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetOrderGeoSurvey);
                        if(selectedFleet.CanGravSurvey && ImGui.Selectable("Grav Survey ...", selectedIssueOrderType == IssueOrderType.GravSurvey))
                        {
                            selectedIssueOrderType = IssueOrderType.GravSurvey;
                        }
                        if(ImGui.Selectable("Jump...", selectedIssueOrderType == IssueOrderType.Jump))
                        {
                            selectedIssueOrderType = IssueOrderType.Jump;
                        }
                    }
                    ImGui.EndChild();
                    ImGui.SameLine();
                    IssueOrdersDisplay(galaxy, secondChildSize);
                    TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetOrderTargets);
                    ImGui.EndTabItem();
                }

                DisplayStandingOrdersTab();

                ImGui.EndTabBar();
            }
            ImGui.EndChild();
        }

        private void IssueOrdersDisplay(IClientGalaxy galaxy, Vector2 size)
        {
            if(ImGui.BeginChild("IssueOrders", size, ImGuiChildFlags.Borders))
            {
                var system = selectedFleet?.SystemId == null ? null : galaxy.GetSystem(selectedFleet.SystemId);
                if(selectedFleet == null || system == null || _uiState.GameClient == null)
                {
                    ImGui.EndChild();
                    return;
                }

                // Mirror the old EntityFilter.Friendly | EntityFilter.Neutral read: hostiles aren't targets.
                var candidates = system.Entities.Where(e => e.Relation != OwnerRelation.Hostile);

                switch(selectedIssueOrderType)
                {
                    case IssueOrderType.MoveTo:
                        foreach(var body in candidates.Where(e => e.HasView<BodyView>() && e.HasView<PositionView>()))
                        {
                            var name = NameOf(body);
                            if(ImGui.Button($"{name}###movement-button-{body.Id}"))
                            {
                                SubmitFleetCommand(new MoveToBodyCommand(selectedFleet.Id, body.Id));
                            }
                        }
                        break;
                    case IssueOrderType.GeoSurvey:
                        foreach(var body in candidates.Where(e => e.GetView<GeoSurveyView>() is { IsSurveyComplete: false }))
                        {
                            var name = NameOf(body);
                            if(ImGui.Button($"{name}###geosurvey-button-{body.Id}"))
                            {
                                SubmitFleetCommand(new GeoSurveyCommand(selectedFleet.Id, body.Id));
                            }
                        }
                        break;
                    case IssueOrderType.GravSurvey:
                        foreach(var location in candidates.Where(e => e.GetView<GravSurveyView>() is { IsSurveyComplete: false }))
                        {
                            var name = NameOf(location);
                            if(ImGui.Button($"{name}###gravsurvey-button-{location.Id}"))
                            {
                                SubmitFleetCommand(new GravSurveyCommand(selectedFleet.Id, location.Id));
                            }
                        }
                        break;
                    case IssueOrderType.Jump:
                        // The server only projects a JumpPointView once this faction has discovered it.
                        foreach(var jumpPoint in candidates.Where(e => e.HasView<JumpPointView>()))
                        {
                            var name = NameOf(jumpPoint);
                            if(ImGui.Button($"{name}###jump-gate-button-{jumpPoint.Id}"))
                            {
                                SubmitFleetCommand(new JumpCommand(selectedFleet.Id, jumpPoint.Id));
                            }
                        }
                        break;
                    case IssueOrderType.RefuelAt:
                        foreach(var colony in candidates.Where(e => e.Kind == BodyKind.Colony && e.HasView<CargoStorageView>()))
                        {
                            var name = NameOf(colony);
                            if(ImGui.Button($"{name}###refuelAt-button-{colony.Id}"))
                            {
                                SubmitFleetCommand(new RefuelAtCommand(selectedFleet.Id, colony.Id));
                            }
                        }
                        break;
                }
            }
            ImGui.EndChild();
        }

        private static string NameOf(EntitySnapshot entity) => entity.GetView<NameView>()?.Name ?? "";

        private void SubmitFleetCommand(GameCommand command) => _uiState.GameClient?.SubmitCommandAsync(command);

        private void DisplayOrders()
        {
            if(selectedFleet == null)
                return;

            var xPosition = ImGui.GetCursorPosX();
            Vector2 windowContentSize = ImGui.GetContentRegionAvail();

            if (ImGui.BeginChild("Fleet Orders", new Vector2(Styles.LeftColumnWidthLg, windowContentSize.Y), ImGuiChildFlags.Borders))
            {
                DisplayHelpers.Header("Fleet Orders");
                if (selectedFleet.Orders.Count == 0)
                {
                    ImGui.Text(string.IsNullOrWhiteSpace(selectedFleet.StatusMessage)
                        ? "None"
                        : selectedFleet.StatusMessage);
                }
                else
                {
                    if (ImGui.BeginTable("FleetOrdersTable", 3, Styles.TableFlags | ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.None, 0.1f);
                        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.None, 0.7f);
                        ImGui.TableSetupColumn("##cancel", ImGuiTableColumnFlags.None, 0.2f);
                        ImGui.TableHeadersRow();

                        for (int i = 0; i < selectedFleet.Orders.Count; i++)
                        {
                            var order = selectedFleet.Orders[i];
                            ImGui.PushID(order.OrderId);
                            ImGui.TableNextColumn();
                            ImGui.Text((i + 1).ToString());
                            ImGui.TableNextColumn();
                            ImGui.Text(order.Name);
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.BeginTooltip();
                                ImGui.Text("IsRunning: " + order.IsRunning);
                                ImGui.Text("IsFinished: " + order.IsFinished);
                                if (!string.IsNullOrEmpty(order.Details))
                                    ImGui.TextWrapped(order.Details);
                                ImGui.EndTooltip();
                            }
                            ImGui.TableNextColumn();
                            ImGui.PushStyleColor(ImGuiCol.Button, Styles.BadColor);
                            if (ImGui.SmallButton("X") && !string.IsNullOrEmpty(order.OrderId))
                            {
                                _uiState.GameClient?.SubmitCommandAsync(
                                    new CancelOrderCommand(selectedFleet.Id, order.OrderId));
                            }
                            ImGui.PopStyleColor();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Cancel / remove this order");
                            ImGui.PopID();
                        }

                        ImGui.EndTable();
                    }

                    ImGui.Spacing();
                    ImGui.PushStyleColor(ImGuiCol.Button, Styles.BadColor);
                    if (ImGui.Button("Clear All Orders", new Vector2(-1, 0)))
                    {
                        // Clears fleet + ship queues (stuck cargo transfers block Movement / Move).
                        _uiState.GameClient?.SubmitCommandAsync(
                            new ClearFleetOrdersCommand(selectedFleet.Id));
                    }
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Remove every order from this fleet and its ships");
                }
            }
            ImGui.EndChild();
            TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetOrdersQueue);
            ImGui.SetCursorPosX(xPosition);
        }

        private void DisplayShips()
        {
            if(selectedFleet == null) return;

            var xPosition = ImGui.GetCursorPosX();
            Vector2 windowContentSize = ImGui.GetContentRegionAvail();
            if (ImGui.BeginChild("FleetSummary2", new Vector2(Styles.LeftColumnWidthLg, windowContentSize.Y * 0.5f - 24f), ImGuiChildFlags.Borders))
            {
                DisplayHelpers.Header("Assigned Ships", "Select ships, then Unassign — or right-click for more options.");

                ImGui.PushStyleColor(ImGuiCol.FrameBg, Styles.InvisibleColor);
                var contentSizeAvail = ImGui.GetContentRegionAvail();
                // Two action buttons under the list.
                float buttonReserve = Styles.ButtonVerticalOffset * 2f + 8f;
                if (ImGui.BeginListBox("###assigned-ships", new Vector2(contentSizeAvail.X, Math.Max(40f, contentSizeAvail.Y - buttonReserve))))
                {
                    if (selectedFleet.Ships.Count == 0)
                    {
                        ImGui.TextDisabled("No ships assigned.");
                        ImGui.TextDisabled("Select unattached ships and Assign.");
                    }
                    foreach (var ship in selectedFleet.Ships)
                    {
                        if (!selectedShips.ContainsKey(ship.Id))
                        {
                            selectedShips.Add(ship.Id, false);
                        }

                        string name = ship.Name;
                        if (selectedFleet.FlagshipId == ship.Id)
                        {
                            name = "(F) " + name;
                        }
                        if (ImGui.Selectable($"{name}###ship-{ship.Id}", selectedShips[ship.Id], ImGuiSelectableFlags.SpanAllColumns))
                        {
                            selectedShips[ship.Id] = !selectedShips[ship.Id];
                        }
                        DisplayHelpers.ShipTooltip(ship);
                        DisplayShipContextMenu(selectedShips, ship);
                    }
                    ImGui.EndListBox();
                }
                ImGui.PopStyleColor();

                bool anySelected = selectedShips.Any(kv => kv.Value);
                if(ImGui.Button("Select All/None", new Vector2(contentSizeAvail.X * 0.48f, 0)))
                {
                    bool selectAll = !anySelected;
                    foreach(var shipId in selectedShips.Keys.ToArray())
                    {
                        selectedShips[shipId] = selectAll;
                    }
                }
                ImGui.SameLine();
                if (!anySelected) ImGui.BeginDisabled();
                if (ImGui.Button("Unassign", new Vector2(contentSizeAvail.X * 0.48f, 0))
                    && _uiState.GameClient != null)
                {
                    int factionId = _uiState.GameClient.Session.FactionId;
                    foreach (var (shipId, isSelected) in selectedShips)
                    {
                        if (!isSelected) continue;
                        SubmitFleetCommand(new ReassignShipCommand(shipId, factionId));
                    }
                    selectedShips.Clear();
                }
                if (!anySelected) ImGui.EndDisabled();
            }
            ImGui.EndChild();
            ImGui.SetCursorPosX(xPosition);
        }

        private void DisplayFleetList(IClientGalaxy galaxy)
        {
            Vector2 windowContentSize = ImGui.GetContentRegionAvail();
            if(ImGui.BeginChild("FleetListSelection", new Vector2(Styles.LeftColumnWidthLg, windowContentSize.Y - 24f), ImGuiChildFlags.Borders))
            {
                DisplayHelpers.Header("Fleets", "Select a fleet to manage it.");

                // We need a drop target here so nested items can be un-nested to the root of the tree
                DisplayEmptyDropTarget();

                foreach(var fleet in galaxy.Fleets)
                {
                    DisplayFleetItem(fleet);
                }

                var sizeLeft = ImGui.GetContentRegionAvail();
                ImGui.InvisibleButton("invis-droptarget", new Vector2(sizeLeft.X, 32f));
                DisplayEmptyDropTarget();

                if(galaxy.UnattachedShips.Count > 0)
                {
                    DisplayHelpers.Header("Unattached Ships", "Select ships, then Assign to the selected fleet.");

                    foreach(var ship in galaxy.UnattachedShips)
                    {
                        if(!selectedUnattachedShips.ContainsKey(ship.Id))
                        {
                            selectedUnattachedShips.Add(ship.Id, false);
                        }

                        if(ImGui.Selectable($"{ship.Name}###unattached-{ship.Id}", selectedUnattachedShips[ship.Id]))
                        {
                            selectedUnattachedShips[ship.Id] = !selectedUnattachedShips[ship.Id];
                        }
                        DisplayHelpers.ShipTooltip(ship);
                        DisplayShipContextMenu(selectedUnattachedShips, ship, isUnattached: true);
                    }

                    bool anyUnattachedSelected = selectedUnattachedShips.Any(kv => kv.Value);
                    bool canAssign = anyUnattachedSelected && selectedFleetId != null;
                    if (!canAssign) ImGui.BeginDisabled();
                    string assignLabel = selectedFleet != null
                        ? $"Assign to {selectedFleet.Name}"
                        : "Assign to Selected Fleet";
                    if (ImGui.Button(assignLabel + "###assign-unattached", new Vector2(-1, 0)))
                    {
                        AssignSelectedUnattachedShips();
                    }
                    if (!canAssign) ImGui.EndDisabled();
                }
            }
            ImGui.EndChild();
            TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetList);

            if(ImGui.Button("Create New Fleet", new Vector2(Styles.LeftColumnWidthLg, 0f)))
            {
                if(_uiState.GameClient != null && !string.IsNullOrEmpty(_uiState.SelectedStarSystemId))
                {
                    // The fleet is created (and named) server-side; the FleetsChanged push adds it here.
                    SubmitFleetCommand(new CreateFleetCommand(_uiState.GameClient.Session.FactionId, _uiState.SelectedStarSystemId));
                }
            }
            if (ImGui.IsItemHovered() && string.IsNullOrEmpty(_uiState.SelectedStarSystemId))
                ImGui.SetTooltip("Select a star system first (system map / fleet in a system).");
        }

        private void AssignSelectedUnattachedShips()
        {
            if (selectedFleetId is not { } fleetId) return;
            foreach (var (shipId, isSelected) in selectedUnattachedShips)
            {
                if (!isSelected) continue;
                SubmitFleetCommand(new ReassignShipCommand(shipId, fleetId));
            }
            selectedUnattachedShips.Clear();
        }

        private static void OpenFleetRename(FleetSnapshot fleet)
        {
            RenameWindow.GetInstance().SetTarget(fleet.Id, fleet.Name);
            RenameWindow.GetInstance().SetActive(true);
        }

        private void DisplayFleetItem(FleetSnapshot fleet)
        {
            ImGui.PushID(fleet.Id.ToString());
            string name = fleet.Name;
            var flags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;

            if(fleet.SubFleets.Count == 0)
            {
                flags |= ImGuiTreeNodeFlags.Leaf;
            }

            if(selectedFleetId == fleet.Id)
            {
                flags |= ImGuiTreeNodeFlags.Selected;
            }

            string description = "Left-click: select. Double-click or right-click: rename.\n";

            if(fleet.Orders.Count == 0)
            {
                description += string.IsNullOrWhiteSpace(fleet.StatusMessage)
                    ? "No Orders"
                    : fleet.StatusMessage;
            }
            else
            {
                foreach(var order in fleet.Orders)
                {
                    description += order.Name + "\n";
                }
            }

            bool isTreeOpen = ImGui.TreeNodeEx(name, flags);
            // Always attach to the tree-node header (works when collapsed too).
            if (ImGui.IsItemClicked())
                SelectFleet(fleet.Id);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                OpenFleetRename(fleet);
            if (ImGui.IsItemHovered())
                DisplayHelpers.DescriptiveTooltip(name, "Fleet", description);

            DisplayContextMenu(fleet);
            DisplayDropSource(fleet.Id, name);
            DisplayDropTarget(fleet.Id);

            if(isTreeOpen)
            {
                foreach(var subFleet in fleet.SubFleets)
                {
                    DisplayFleetItem(subFleet);
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        private void DisplayContextMenu(FleetSnapshot fleet)
        {
            // Explicit id + mouse button: BeginPopupContextItem() without args is unreliable on TreeNodeEx.
            if (ImGui.BeginPopupContextItem("fleet-ctx", ImGuiPopupFlags.MouseButtonRight))
            {
                if(ImGui.MenuItem("Rename"))
                    OpenFleetRename(fleet);
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, Styles.TerribleColor);
                if(ImGui.MenuItem("Disband###delete-" + fleet.Id))
                {
                    SubmitFleetCommand(new DisbandFleetCommand(fleet.Id));
                    SelectFleet(null);
                }
                ImGui.PopStyleColor();
                ImGui.EndPopup();
            }
        }

        private void DisplayShipContextMenu(Dictionary<int, bool> selected, ShipSnapshot ship, bool isUnattached = false)
        {
            var galaxy = _uiState.GameClient?.Galaxy;
            if(galaxy == null) return;

            if(ImGui.BeginPopupContextItem("ship-ctx-" + ship.Id, ImGuiPopupFlags.MouseButtonRight))
            {
                if(ImGui.MenuItem("View Ship"))
                {
                    var systemId = string.IsNullOrEmpty(ship.SystemId) ? _uiState.SelectedStarSystemId : ship.SystemId;
                    _uiState.EntityClicked(ship.Id, systemId, MouseButtons.Primary);
                }
                if(!isUnattached && selectedFleet != null)
                {
                    bool isFlagship = ship.Id == selectedFleet.FlagshipId;
                    if(isFlagship)
                    {
                        ImGui.BeginDisabled();
                    }
                    if(ImGui.MenuItem("Promote to Flagship"))
                    {
                        SubmitFleetCommand(new SetFlagshipCommand(selectedFleet.Id, ship.Id));
                    }
                    if(isFlagship)
                    {
                        ImGui.EndDisabled();
                    }

                    if (_uiState.GameClient != null && ImGui.MenuItem("Unassign from Fleet"))
                    {
                        SubmitFleetCommand(new ReassignShipCommand(ship.Id, _uiState.GameClient.Session.FactionId));
                    }
                }
                ImGui.Separator();

                if(isUnattached && selectedFleetId is { } targetFleetId && ImGui.MenuItem("Assign to Selected Fleet"))
                {
                    SubmitFleetCommand(new ReassignShipCommand(ship.Id, targetFleetId));
                }

                if(ImGui.BeginMenu("Re-assign ships"))
                {
                    ImGui.Text("Re-assign ships to:");
                    ImGui.Separator();
                    foreach(var fleet in galaxy.Fleets)
                    {
                        DisplayShipAssignmentOption(selected, ship, fleet, isUnattached: isUnattached);
                    }
                    ImGui.EndMenu();
                }
                ImGui.EndPopup();
            }
        }

        private void DisplayShipAssignmentOption(Dictionary<int, bool> selected, ShipSnapshot ship, FleetSnapshot fleet, int depth = 0, bool isUnattached = false)
        {
            for(int i = 0; i < depth; i++)
            {
                ImGui.InvisibleButton("invis", new Vector2(8, 8));
                ImGui.SameLine();
            }

            if(fleet.Id == selectedFleetId && !isUnattached)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                ImGui.Text(fleet.Name);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.PushID(fleet.Id.ToString());
                if(ImGui.MenuItem(fleet.Name))
                {
                    // The server detaches each ship from whichever fleet (or the faction root)
                    // currently holds it, so no unassign bookkeeping is needed here.
                    if(!selected.Any(x => x.Value))
                    {
                        SubmitFleetCommand(new ReassignShipCommand(ship.Id, fleet.Id));
                    }
                    else
                    {
                        foreach(var (selectedShipId, isSelected) in selected)
                        {
                            if(!isSelected) continue;
                            SubmitFleetCommand(new ReassignShipCommand(selectedShipId, fleet.Id));
                        }
                        // Clean up the selections
                        selected.Clear();
                    }
                }
                ImGui.PopID();
            }

            foreach(var subFleet in fleet.SubFleets)
            {
                DisplayShipAssignmentOption(selected, ship, subFleet, depth + 1, isUnattached);
            }
        }

        private void DisplayEmptyDropTarget()
        {
            if(ImGui.BeginDragDropTarget())
            {
                ImGui.AcceptDragDropPayload("FLEET", ImGuiDragDropFlags.None);
                if(ImGui.IsMouseReleased(ImGuiMouseButton.Left) && dragFleetId != -1)
                {
                    if(_uiState.GameClient != null)
                    {
                        // Dropping on empty space re-parents to the faction root.
                        SubmitFleetCommand(new ChangeFleetParentCommand(dragFleetId, _uiState.GameClient.Session.FactionId));
                        dragFleetId = -1;
                    }
                }
                ImGui.EndDragDropTarget();
            }
        }

        private void DisplayDropTarget(int fleetId)
        {
            // Begin Drag Target
            if (ImGui.BeginDragDropTarget())
            {
                ImGui.AcceptDragDropPayload("FLEET", ImGuiDragDropFlags.None);
                if(ImGui.IsMouseReleased(ImGuiMouseButton.Left) && dragFleetId != -1)
                {
                    SubmitFleetCommand(new ChangeFleetParentCommand(dragFleetId, fleetId));
                    dragFleetId = -1;
                }
                ImGui.EndDragDropTarget();
            }
        }

        private void DisplayDropSource(int fleetId, string name)
        {
            // Begin drag source
            if(ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
            {
                dragFleetId = fleetId;

                ImGui.SetDragDropPayload("FLEET", IntPtr.Zero, 0);
                ImGui.Text(name);
                ImGui.EndDragDropSource();
            }
        }

        #region Standing Orders editor

        /// <summary>The local working copy, (re)loaded from the snapshot when nothing is being
        /// edited; player edits are kept until saved or the fleet selection changes.</summary>
        private List<StandingOrderEdit> EditedOrders(FleetSnapshot fleet)
        {
            if (editedOrders != null
                && (standingOrdersDirty || ReferenceEquals(editedOrdersSource, fleet.StandingOrders)))
                return editedOrders;

            editedOrders = new List<StandingOrderEdit>(fleet.StandingOrders.Count);
            foreach (var order in fleet.StandingOrders)
            {
                var edit = new StandingOrderEdit
                {
                    NameBuffer = string.IsNullOrEmpty(order.Name) ? new byte[32] : Utils.BytesFromString(order.Name, 32),
                    Actions = NormalizeStandingActionIds(order.Actions),
                };
                foreach (var condition in order.Conditions)
                {
                    edit.Conditions.Add(new StandingOrderConditionEdit
                    {
                        ConditionType = condition.ConditionType,
                        Comparison = condition.Comparison,
                        Threshold = condition.Threshold,
                        Logic = condition.Logic ?? StandingOrderLogic.And,
                    });
                }
                editedOrders.Add(edit);
            }

            editedOrdersSource = fleet.StandingOrders;
            standingOrdersDirty = false;
            if (selectedOrderIndex >= editedOrders.Count)
                selectedOrderIndex = -1;
            return editedOrders;
        }

        private void SaveStandingOrders(int fleetId, List<StandingOrderEdit> orders)
        {
            var payload = new List<StandingOrder>(orders.Count);
            foreach (var edit in orders)
            {
                var conditions = new List<StandingOrderCondition>(edit.Conditions.Count);
                for (int i = 0; i < edit.Conditions.Count; i++)
                {
                    var condition = edit.Conditions[i];
                    conditions.Add(new StandingOrderCondition(
                        condition.ConditionType,
                        condition.Comparison,
                        condition.Threshold,
                        i < edit.Conditions.Count - 1 ? condition.Logic : null));
                }
                payload.Add(new StandingOrder(
                    Utils.StringFromBytes(edit.NameBuffer),
                    conditions,
                    NormalizeStandingActionIds(edit.Actions)));
            }

            _uiState.GameClient?.SubmitCommandAsync(new SetStandingOrdersCommand(fleetId, payload));
            // Keep the local copy on screen until the refreshed fleet snapshot is pushed back.
            standingOrdersDirty = false;
            editedOrdersSource = null;
        }

        /// <summary>
        /// Drop Resupply (no-op stub) and redundant Move-to-Colony when Refuel is present.
        /// </summary>
        private static List<string> NormalizeStandingActionIds(IEnumerable<string> actions)
        {
            var list = actions?
                .Where(a => !string.IsNullOrEmpty(a) && a != StandingOrderTypes.Resupply)
                .ToList()
                ?? new List<string>();

            if (list.Contains(StandingOrderTypes.Refuel))
                list.RemoveAll(a => a == StandingOrderTypes.MoveToNearestColony);

            return list;
        }

        private void DisplayStandingOrdersTab()
        {
            if(selectedFleetId is not { } fleetId || selectedFleet == null)
                return;

            if(ImGui.BeginTabItem("Standing Orders"))
            {
                TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetTabStandingOrders);
                var orders = EditedOrders(selectedFleet);

                var size = ImGui.GetContentRegionAvail();
                var firstChildSize = new Vector2(size.X * 0.33f, size.Y);
                var secondChildSize = new Vector2(size.X * 0.67f - (size.X * 0.01f), size.Y);
                if(ImGui.BeginChild("StandingOrders-List", firstChildSize, ImGuiChildFlags.Borders))
                {
                    var sizeAvailable = ImGui.GetContentRegionAvail();
                    DisplayHelpers.Header("Order List", "Top = highest priority. The first matching order runs; it can interrupt lower-priority standing work.");
                    if(orders.Count > 0)
                    {
                        for(int i = 0; i < orders.Count; i++)
                        {
                            ImGui.PushID("###" + i);
                            bool isSelected = selectedOrderIndex == i;
                            string name = Utils.StringFromBytes(orders[i].NameBuffer);
                            if(string.IsNullOrEmpty(name)) name = "<un-named>";
                            if(ImGui.Selectable((i + 1) + ". " + name, ref isSelected))
                            {
                                selectedOrderIndex = i;
                            }
                            if(ImGui.BeginPopupContextItem())
                            {
                                if(i > 0 && ImGui.MenuItem("Move Up"))
                                {
                                    (orders[i - 1], orders[i]) = (orders[i], orders[i - 1]);
                                    if(selectedOrderIndex == i) selectedOrderIndex = i - 1;
                                    else if(selectedOrderIndex == i - 1) selectedOrderIndex = i;
                                    // Persist immediately — same as Delete; Save lives only in the
                                    // detail pane and is easy to miss when reordering from the list.
                                    SaveStandingOrders(fleetId, orders);
                                }
                                if(i < orders.Count - 1 && ImGui.MenuItem("Move Down"))
                                {
                                    (orders[i + 1], orders[i]) = (orders[i], orders[i + 1]);
                                    if(selectedOrderIndex == i) selectedOrderIndex = i + 1;
                                    else if(selectedOrderIndex == i + 1) selectedOrderIndex = i;
                                    SaveStandingOrders(fleetId, orders);
                                }
                                if(ImGui.MenuItem("Delete Order"))
                                {
                                    orders.RemoveAt(i);
                                    if(selectedOrderIndex == i) selectedOrderIndex = -1;
                                    else if(selectedOrderIndex > i) selectedOrderIndex--;
                                    // Persist immediately — players expect delete to stick without a separate Save.
                                    SaveStandingOrders(fleetId, orders);
                                }
                                ImGui.EndPopup();
                            }
                            ImGui.PopID();
                        }
                    }
                    else
                    {
                        ImGui.Text("No orders");
                    }

                    ImGui.SetCursorPosY(sizeAvailable.Y - 12f);
                    if(ImGui.Button("Create New Order", new Vector2(sizeAvailable.X, 0)))
                    {
                        orders.Add(new StandingOrderEdit());
                        standingOrdersDirty = true;

                        // if this is the first order, select it
                        if(orders.Count == 1)
                            selectedOrderIndex = 0;
                    }
                }
                ImGui.EndChild();
                ImGui.SameLine();
                if(ImGui.BeginChild("StandingOrders-edit", secondChildSize, ImGuiChildFlags.Borders)
                    && selectedOrderIndex >= 0 && selectedOrderIndex < orders.Count)
                {
                    var selectedOrder = orders[selectedOrderIndex];
                    var sizeAvailable = ImGui.GetContentRegionAvail();
                    DisplayHelpers.Header("Order Name");
                    if(ImGui.InputText("###order-name-input", selectedOrder.NameBuffer, 32))
                    {
                        standingOrdersDirty = true;
                    }
                    ImGui.NewLine();
                    DisplayHelpers.Header("Conditions", "If the conditions listed are true, the actions will execute.");

                    var conditions = selectedOrder.Conditions;
                    if (conditions.Count == 0 && selectedOrder.Actions.Count > 0)
                    {
                        ImGui.TextColored(Styles.OkColor,
                            "No conditions — order will use the action's default trigger after Save.");
                    }
                    for(int i = 0; i < conditions.Count; i++)
                    {
                        var condition = conditions[i];
                        var conditionType = StandingOrderConditionTypes.FirstOrDefault(t => t.Id == condition.ConditionType);
                        ImGui.PushID(i);
                        ImGui.Button(conditionType.Label ?? condition.ConditionType, new Vector2(Math.Max(sizeAvailable.X * 0.4f, 128f), 0f));

                        int value = (int)condition.Threshold;
                        int comparisonIndex = (int)condition.Comparison;
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(Math.Max(sizeAvailable.X * 0.075f, 16f));
                        if(ImGui.Combo("###orderComparison", ref comparisonIndex, orderComparisons, orderComparisons.Length))
                        {
                            condition.Comparison = (StandingOrderComparison)comparisonIndex;
                            standingOrdersDirty = true;
                        }
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(Math.Max(sizeAvailable.X * 0.15f, 32f));
                        if(ImGui.InputInt(conditionType.Description + "###orderValue", ref value, 1, 5))
                        {
                            if(value < conditionType.Min) value = (int)conditionType.Min;
                            if(value > conditionType.Max) value = (int)conditionType.Max;

                            condition.Threshold = value;
                            standingOrdersDirty = true;
                        }

                        // Show the logical operators UI on all but the last item
                        ImGui.SameLine();
                        var position = ImGui.GetCursorPos();
                        if(i < conditions.Count - 1)
                        {
                            ImGui.SetCursorPosY(position.Y + 12f);
                            if(condition.Logic == StandingOrderLogic.And)
                            {
                                ImGui.SetCursorPosX(sizeAvailable.X - 82f);
                                if(ImGui.Button("AND"))
                                {
                                    condition.Logic = StandingOrderLogic.Or;
                                    standingOrdersDirty = true;
                                }
                            }
                            else
                            {
                                ImGui.SetCursorPosX(sizeAvailable.X - 48f);
                                if(ImGui.Button("OR"))
                                {
                                    condition.Logic = StandingOrderLogic.And;
                                    standingOrdersDirty = true;
                                }
                            }
                        }
                        ImGui.SameLine();
                        ImGui.SetCursorPos(position);
                        ImGui.SetCursorPosX(sizeAvailable.X - 12f);
                        if(ImGui.Button("x"))
                        {
                            conditions.RemoveAt(i);
                            standingOrdersDirty = true;
                            ImGui.PopID();
                            break;
                        }
                        ImGui.PopID();
                    }

                    if(ImGui.Button("Add Condition"))
                    {
                        if(orderConditionsIndex >= 0 && orderConditionsIndex < StandingOrderConditionTypes.Length)
                        {
                            var conditionType = StandingOrderConditionTypes[orderConditionsIndex];
                            var (comparison, threshold) = DefaultForCondition(conditionType.Id);
                            conditions.Add(new StandingOrderConditionEdit
                            {
                                ConditionType = conditionType.Id,
                                Comparison = comparison,
                                Threshold = threshold,
                            });
                            standingOrdersDirty = true;
                        }
                    }
                    ImGui.SameLine();
                    var conditionLabels = StandingOrderConditionTypes.Select(t => t.Label).ToArray();
                    if(ImGui.Combo("###order-add-condition-list", ref orderConditionsIndex, conditionLabels, conditionLabels.Length))
                    {
                    }

                    ImGui.NewLine();
                    DisplayHelpers.Header("Actions", "The actions listed will execute in the order in which they are listed.");

                    for(int i = 0; i < selectedOrder.Actions.Count; i++)
                    {
                        ImGui.PushID("action" + i);
                        var actionSize = ImGui.GetContentRegionAvail();
                        var actionLabel = StandingOrderActionTypes.FirstOrDefault(t => t.Id == selectedOrder.Actions[i]).Label;
                        ImGui.Text(actionLabel ?? selectedOrder.Actions[i]);
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(actionSize.X - 12f);
                        if(ImGui.Button("x"))
                        {
                            selectedOrder.Actions.RemoveAt(i);
                            standingOrdersDirty = true;
                            ImGui.PopID();
                            break;
                        }
                        ImGui.PopID();
                    }

                    if(ImGui.Button("Add Action"))
                    {
                        if(orderActionsIndex >= 0 && orderActionsIndex < StandingOrderActionTypes.Length)
                        {
                            var actionId = StandingOrderActionTypes[orderActionsIndex].Id;
                            selectedOrder.Actions.Add(actionId);
                            // Suggest the matching condition if the order has none yet.
                            if (selectedOrder.Conditions.Count == 0)
                            {
                                string? condId = actionId switch
                                {
                                    StandingOrderTypes.MoveToNearestGravSurvey => StandingOrderTypes.UnsurveyedAnomalyCondition,
                                    StandingOrderTypes.MoveToNearestAnomaly => StandingOrderTypes.UnsurveyedAnomalyCondition,
                                    StandingOrderTypes.MoveToNearestGeoSurvey => StandingOrderTypes.UnsurveyedGeoCondition,
                                    StandingOrderTypes.Refuel => StandingOrderTypes.FuelCondition,
                                    _ => null,
                                };
                                if (condId != null)
                                {
                                    var (comparison, threshold) = DefaultForCondition(condId);
                                    selectedOrder.Conditions.Add(new StandingOrderConditionEdit
                                    {
                                        ConditionType = condId,
                                        Comparison = comparison,
                                        Threshold = threshold,
                                    });
                                }
                            }
                            standingOrdersDirty = true;
                        }
                    }
                    ImGui.SameLine();
                    var actionLabels = StandingOrderActionTypes.Select(t => t.Label).ToArray();
                    if(ImGui.Combo("###order-add-action-list", ref orderActionsIndex, actionLabels, actionLabels.Length))
                    {
                    }

                    ImGui.SetCursorPosY(sizeAvailable.Y - 12f);
                    if(ImGui.Button(standingOrdersDirty ? "Save*" : "Save", new Vector2(sizeAvailable.X, 0)))
                    {
                        SaveStandingOrders(fleetId, orders);
                    }
                    TutorialHighlight.ReportItem(TutorialHighlightRegion.FleetStandingSave);
                }
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
        }

        #endregion
    }
}
