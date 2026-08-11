using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Pulsar4X.Api;
using Pulsar4X.Client.Interface;
using Pulsar4X.Client.Interface.Widgets;
// Engine usings: Stringify formatting, plus the deferred camera-pin / maneuver-panel bridges below.

namespace Pulsar4X.Client
{
    public class EntityWindow : NamedPulsarGuiWindow
    {
        public int EntityId { get; }
        public string SystemId { get; private set; }
        public string Title { get; private set; } = "Unknown";

        // Re-resolved each frame: system snapshots are replaced wholesale by server pushes.
        private EntitySnapshot? _entity;
        private IClientSystem? _system;
        private UserOrbitSettings.OrbitBodyType _bodyType;

        private Vector4 _accentColor;

        // Animation constants — ships need more height for gauges + orders + cargo without scrolling.
        private const float DefaultWindowWidth = 624f;
        private const float DefaultWindowHeight = 420f;
        private const float ShipWindowWidth = 720f;
        private const float ShipWindowHeight = 620f;
        private const float AnimationDuration = 0.2f; // seconds
        private const float BottomMargin = 4f;
        private const float RightMargin = 4f;

        // Animation state
        private enum AnimationState { Closed, Opening, Open, Closing }
        private AnimationState _animationState = AnimationState.Closed;
        private float _animationProgress = 0f;
        private DateTime _animationStartTime;

        // After the open animation finishes, stop forcing pos/size so the user can drag/resize.
        private bool _lockLayoutToAnimation = true;

        public EntityWindow(int entityId, string systemId) : base("EntityWindow|" + entityId)
        {
            EntityId = entityId;
            SystemId = systemId;
            _flags = ImGuiWindowFlags.NoTitleBar; // custom header; move + resize like other panels
        }

        /// <summary>Ship jumped — rebind the panel to the system that now owns the snapshot.</summary>
        internal void RelocateToSystem(string systemId)
        {
            if (!string.IsNullOrEmpty(systemId))
                SystemId = systemId;
        }

        public new void SetActive(bool activeVal = true)
        {
            if (activeVal && !IsActive)
            {
                // Starting to open
                _animationState = AnimationState.Opening;
                _animationStartTime = DateTime.Now;
                _animationProgress = 0f;
                _lockLayoutToAnimation = true;
                IsActive = true;
            }
            else if (!activeVal && IsActive)
            {
                // Starting to close
                _animationState = AnimationState.Closing;
                _animationStartTime = DateTime.Now;
                _animationProgress = 1f;
                _lockLayoutToAnimation = true;
            }
        }

        public new void ToggleActive()
        {
            SetActive(!IsActive);
        }

        private float EaseOutCubic(float t)
        {
            return 1f - MathF.Pow(1f - t, 3f);
        }

        private float EaseInCubic(float t)
        {
            return t * t * t;
        }

        private Vector4 GetAccentColor()
        {
            return _bodyType switch
            {
                UserOrbitSettings.OrbitBodyType.Star => new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                UserOrbitSettings.OrbitBodyType.Planet => new Vector4(0.3f, 0.6f, 1.0f, 1.0f),
                UserOrbitSettings.OrbitBodyType.DwarfPlanet => new Vector4(0.65f, 0.5f, 0.8f, 1.0f),
                UserOrbitSettings.OrbitBodyType.Moon => new Vector4(0.7f, 0.75f, 0.85f, 1.0f),
                UserOrbitSettings.OrbitBodyType.Asteroid => new Vector4(0.75f, 0.55f, 0.3f, 1.0f),
                UserOrbitSettings.OrbitBodyType.Comet => new Vector4(0.4f, 0.85f, 0.95f, 1.0f),
                UserOrbitSettings.OrbitBodyType.Colony => new Vector4(0.3f, 0.85f, 0.4f, 1.0f),
                UserOrbitSettings.OrbitBodyType.Ship => new Vector4(0.4f, 0.6f, 0.9f, 1.0f),
                _ => new Vector4(0.5f, 0.5f, 0.55f, 1.0f),
            };
        }

        private void UpdateAnimation()
        {
            if (_animationState == AnimationState.Open || _animationState == AnimationState.Closed)
                return;

            float elapsed = (float)(DateTime.Now - _animationStartTime).TotalSeconds;
            float t = Math.Clamp(elapsed / AnimationDuration, 0f, 1f);

            if (_animationState == AnimationState.Opening)
            {
                _animationProgress = EaseOutCubic(t);
                if (t >= 1f)
                {
                    _animationState = AnimationState.Open;
                    _animationProgress = 1f;
                    _lockLayoutToAnimation = false;
                }
            }
            else if (_animationState == AnimationState.Closing)
            {
                _animationProgress = 1f - EaseInCubic(t);
                if (t >= 1f)
                {
                    _animationState = AnimationState.Closed;
                    _animationProgress = 0f;
                    IsActive = false;
                }
            }
        }

        private Vector2 GetWindowSize()
        {
            if (_bodyType == UserOrbitSettings.OrbitBodyType.Ship)
                return new Vector2(ShipWindowWidth, ShipWindowHeight);
            return new Vector2(DefaultWindowWidth, DefaultWindowHeight);
        }

        private Vector2 CalculateWindowPosition()
        {
            var viewportSize = _uiState.ViewPort.Size;
            var size = GetWindowSize();

            // Final position: bottom right corner
            float finalX = viewportSize.Width - size.X - RightMargin;
            float finalY = viewportSize.Height - size.Y - BottomMargin;

            // Animate from right (offscreen beyond right edge) into final position
            // When progress is 0, window is offscreen to the right
            // When progress is 1, window is at its final position
            float startX = viewportSize.Width; // Start completely off-screen to the right
            float currentX = startX + (finalX - startX) * _animationProgress;

            return new Vector2(currentX, finalY);
        }

        internal override void Display()
        {
            if (!IsActive && _animationState == AnimationState.Closed) return;

            UpdateAnimation();

            // Don't render if fully closed
            if (_animationState == AnimationState.Closed) return;

            _system = _uiState.GameClient?.Galaxy.GetSystem(SystemId);
            _entity = _system?.GetEntity(EntityId);

            // After a JP jump the window may still hold the departure SystemId.
            if (_entity == null)
            {
                var resolved = _uiState.FindSystemContainingEntity(EntityId);
                if (resolved != null)
                {
                    RelocateToSystem(resolved);
                    _system = _uiState.GameClient?.Galaxy.GetSystem(SystemId);
                    _entity = _system?.GetEntity(EntityId);
                }
            }

            // The entity left the faction's view (destroyed/hidden) — drop the window.
            if (_entity == null)
            {
                IsActive = false;
                _animationState = AnimationState.Closed;
                return;
            }

            _bodyType = UserOrbitSettings.FromBodyKind(_entity.Kind);
            Title = _entity.GetView<NameView>()?.Name ?? "Unknown";

            var windowPos = CalculateWindowPosition();
            var windowSize = GetWindowSize();
            if (_lockLayoutToAnimation)
            {
                // Slide-in / slide-out owns layout; once open the user may drag and resize freely.
                ImGui.SetNextWindowPos(windowPos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
            }
            else
            {
                ImGui.SetNextWindowSize(windowSize, ImGuiCond.FirstUseEver);
            }
            // Ships pack dense order text; keep the panel more opaque so list UI behind it does not bleed through.
            ImGui.SetNextWindowBgAlpha(_bodyType == UserOrbitSettings.OrbitBodyType.Ship ? 0.94f : 0.85f);

            var accentColor = GetAccentColor();

            // Remove window border
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            // Accent-colored collapsing headers
            ImGui.PushStyleColor(ImGuiCol.Header,
                new Vector4(accentColor.X * 0.15f, accentColor.Y * 0.15f, accentColor.Z * 0.15f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered,
                new Vector4(accentColor.X * 0.25f, accentColor.Y * 0.25f, accentColor.Z * 0.25f, 0.7f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,
                new Vector4(accentColor.X * 0.2f, accentColor.Y * 0.2f, accentColor.Z * 0.2f, 0.8f));

            // Track if window is closed via the X button
            bool windowOpen = true;
            if (Window.Begin(Title + " (" + _bodyType.ToDescription() + ")" + "###" + EntityId, ref windowOpen, _flags))
            {
                _accentColor = accentColor;
                DrawWindowAccents(accentColor);
                DisplayHeader(accentColor);
                DisplayContent();
            }
            Window.End();

            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();

            // Handle close button click
            if (!windowOpen && _animationState != AnimationState.Closing)
            {
                SetActive(false);
            }
        }

        private void DrawWindowAccents(Vector4 accentColor)
        {
            var drawList = ImGui.GetWindowDrawList();
            var winPos = ImGui.GetWindowPos();
            var winSize = ImGui.GetWindowSize();

            // Top accent strip
            drawList.AddRectFilled(
                winPos,
                new Vector2(winPos.X + winSize.X, winPos.Y + 3f),
                ImGui.ColorConvertFloat4ToU32(accentColor));
        }

        private void DisplayHeader(Vector4 accentColor)
        {
            var drawList = ImGui.GetWindowDrawList();
            var winPos = ImGui.GetWindowPos();
            var winSize = ImGui.GetWindowSize();
            var contentStart = ImGui.GetCursorScreenPos();
            float startLocalY = ImGui.GetCursorPosY();
            float pinBtnSize = 16f;

            // Measure title line height with the header font
            ImGui.PushFont(Styles.MediumFont, 16f);
            float titleLineHeight = ImGui.GetTextLineHeight();
            ImGui.PopFont();

            // Entity subtitle (ship class, spectral type, body sub-type)
            string subtitle = GetEntitySubtitle();
            bool hasSubtitle = subtitle.Length > 0;

            // Second row always reserved for subtitle text + action buttons
            float textLineHeight = ImGui.GetTextLineHeight();
            float btnTotalHeight = pinBtnSize + ImGui.GetStyle().FramePadding.Y * 2;
            float secondRowHeight = Math.Max(textLineHeight, btnTotalHeight) + 4f;

            // Header dimensions
            float headerPad = 8f;
            float headerContentHeight = titleLineHeight + secondRowHeight;
            float headerTop = contentStart.Y - headerPad;
            float headerBottom = contentStart.Y + headerContentHeight + headerPad;

            // Header background (dark tinted with entity accent color)
            drawList.AddRectFilled(
                new Vector2(winPos.X, headerTop),
                new Vector2(winPos.X + winSize.X, headerBottom),
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(accentColor.X * 0.12f, accentColor.Y * 0.12f, accentColor.Z * 0.12f, 0.6f)));

            // Left accent bar
            drawList.AddRectFilled(
                new Vector2(winPos.X, headerTop),
                new Vector2(winPos.X + 3f, headerBottom),
                ImGui.ColorConvertFloat4ToU32(accentColor));

            // Bottom accent line
            drawList.AddLine(
                new Vector2(winPos.X, headerBottom),
                new Vector2(winPos.X + winSize.X, headerBottom),
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(accentColor.X, accentColor.Y, accentColor.Z, 0.4f)),
                1f);

            // Row 1: Title (left) + action buttons (right)
            float framePadX = ImGui.GetStyle().FramePadding.X * 2;
            float btnSpacing = 4f;
            float closeBtnWidth = pinBtnSize + framePadX;
            float pinBtnWidth = pinBtnSize + framePadX;
            float totalBtnsWidth = pinBtnWidth + btnSpacing + closeBtnWidth;
            float btnX = winSize.X - ImGui.GetStyle().WindowPadding.X - totalBtnsWidth;
            float btnY = startLocalY + (titleLineHeight - btnTotalHeight) * 0.5f;

            // NoTitleBar: allow dragging from the custom header (leave pin/close clickable).
            if (!_lockLayoutToAnimation)
            {
                ImGui.SetCursorScreenPos(new Vector2(winPos.X, headerTop));
                ImGui.InvisibleButton("##headerdrag", new Vector2(Math.Max(1f, btnX), Math.Max(1f, headerBottom - headerTop)));
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    Vector2 delta = ImGui.GetIO().MouseDelta;
                    ImGui.SetWindowPos(ImGui.GetWindowPos() + delta);
                }
            }

            ImGui.SetCursorPos(new Vector2(ImGui.GetStyle().WindowPadding.X, startLocalY));
            ImGui.PushFont(Styles.MediumFont, 16f);
            ImGui.Text(Title.ToUpper());
            ImGui.PopFont();

            // Pin button (right-aligned on row 1)
            ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGui.PushID(EntityId);
            ImGui.PushStyleColor(ImGuiCol.Button, Styles.InvisibleColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                new Vector4(accentColor.X * 0.3f, accentColor.Y * 0.3f, accentColor.Z * 0.3f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                new Vector4(accentColor.X * 0.4f, accentColor.Y * 0.4f, accentColor.Z * 0.4f, 0.7f));
            if (ImGui.ImageButton("##headerpin", _uiState.Img_Pin().ToTextureRef(), new Vector2(pinBtnSize, pinBtnSize)))
            {
                _uiState.Camera.PinToEntity(EntityId, SystemId, _uiState);
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(GlobalUIState.NamesForMenus[typeof(PinCameraBlankMenuHelper)]);

            // Close button
            ImGui.SameLine(0, btnSpacing);
            ImGui.PushStyleColor(ImGuiCol.Button, Styles.InvisibleColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                new Vector4(0.8f, 0.2f, 0.2f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                new Vector4(0.9f, 0.1f, 0.1f, 0.7f));
            if (ImGui.Button("X##headerclose", new Vector2(pinBtnSize + framePadX, pinBtnSize + ImGui.GetStyle().FramePadding.Y * 2)))
            {
                SetActive(false);
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Close");
            ImGui.PopID();

            // Row 2: Subtitle (left) + type label (right)
            float secondRowY = startLocalY + titleLineHeight;
            ImGui.SetCursorPosY(secondRowY);

            if (hasSubtitle)
            {
                ImGui.PushStyleColor(ImGuiCol.Text,
                    new Vector4(accentColor.X * 0.8f, accentColor.Y * 0.8f, accentColor.Z * 0.8f, 0.6f));
                ImGui.Text(subtitle);
                ImGui.PopStyleColor();
            }


            // Ensure cursor is past the header background
            float headerLocalBottom = startLocalY + headerContentHeight + headerPad * 2;
            if (ImGui.GetCursorPosY() < headerLocalBottom)
                ImGui.SetCursorPosY(headerLocalBottom);
        }

        private string GetEntitySubtitle()
        {
            if (_entity == null) return "";

            if (_entity.GetView<ShipView>() is { } ship)
                return ship.DesignName;
            if (_entity.GetView<StarView>() is { } star)
                return star.SpectralClass;
            if (_entity.GetView<BodyView>() is { } body)
            {
                if (GetParent() is { } parent)
                    return "Orbiting: " + (parent.GetView<NameView>()?.Name ?? "Unknown");
                return body.BodyType;
            }
            return "";
        }

        private EntitySnapshot? GetParent()
        {
            return _entity?.GetView<PositionView>()?.ParentId is { } parentId
                ? _system?.GetEntity(parentId)
                : null;
        }

        /// <summary>
        /// Resolves the colony associated with this body — either this entity itself, or the
        /// owned colony sitting on it.
        /// </summary>
        private EntitySnapshot? GetColony()
        {
            if (_entity == null || _system == null) return null;
            if (_entity.Kind == BodyKind.Colony) return _entity;

            return _system.Entities.FirstOrDefault(e => e.Kind == BodyKind.Colony
                && e.Relation == OwnerRelation.Owned
                && e.GetView<ColonyView>()?.PlanetEntityId == _entity.Id);
        }

        private void DisplayContent()
        {
            if (_entity == null) return;

            switch (_bodyType)
            {
                case UserOrbitSettings.OrbitBodyType.Ship:
                    DisplayShipContent();
                    break;
                case UserOrbitSettings.OrbitBodyType.Star:
                    DisplayStarContent();
                    break;
                case UserOrbitSettings.OrbitBodyType.Planet:
                case UserOrbitSettings.OrbitBodyType.DwarfPlanet:
                case UserOrbitSettings.OrbitBodyType.Moon:
                    DisplaySystemBodyContent();
                    break;
                case UserOrbitSettings.OrbitBodyType.Asteroid:
                case UserOrbitSettings.OrbitBodyType.Comet:
                    DisplaySmallBodyContent();
                    break;
                case UserOrbitSettings.OrbitBodyType.Colony:
                    DisplayColonyContent();
                    break;
                default:
                    DisplayGenericContent();
                    break;
            }
        }

        // --- Shared Helpers ---

        private void DisplayOrbitInfo()
        {
            var parent = GetParent();
            if (parent == null) return;

            ImGui.Columns(2, "##orbit-info", true);
            if (_entity!.GetView<WarpMovingView>() is { } warping)
            {
                DisplayHelpers.PrintRow("Warping", Stringify.Velocity(warping.SpeedMps));
            }
            else
            {
                DisplayHelpers.PrintFormattedCell("Orbiting");
                if (ImGui.SmallButton(parent.GetView<NameView>()?.Name ?? "Unknown"))
                {
                    _uiState.EntityClicked(parent.Id, _uiState.SelectedStarSystemId, MouseButtons.Primary);
                }
                ImGui.NextColumn();
                ImGui.Separator();
            }
            ImGui.Columns(1);
        }

        private void DisplayOrders()
        {
            var own = _entity?.GetView<OrdersView>();
            var shipOrders = OrderDisplayHelpers.GetShipActivityOrders(
                own, _entity?.GetView<ActivityView>());
            var isMember = OrderDisplayHelpers.IsFleetMember(_uiState.GameClient, EntityId);
            var fleetOrders = (isMember || OrderDisplayHelpers.FindFleetById(
                    _uiState.GameClient?.Galaxy.Fleets ?? Array.Empty<FleetSnapshot>(), EntityId) != null)
                ? OrderDisplayHelpers.GetFleetOrders(_uiState.GameClient, EntityId)
                : Array.Empty<OrderSnapshot>();

            if (shipOrders.Count == 0 && fleetOrders.Count == 0)
                return;

            if (ImGui.CollapsingHeader("Orders", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (shipOrders.Count > 0)
                    DrawOrdersCollapseTable("Ship", "OrdersTableShip", shipOrders);
                if (fleetOrders.Count > 0)
                    DrawOrdersCollapseTable(isMember ? "Fleet" : "Fleet queue", "OrdersTableFleet", fleetOrders);
            }
        }

        private void DrawOrdersCollapseTable(string heading, string tableId, System.Collections.Generic.IReadOnlyList<OrderSnapshot> orders)
        {
            ImGui.TextDisabled(heading);
            if (!ImGui.BeginTable(tableId, 3, Styles.TableFlags))
                return;

            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthStretch, 0.1f);
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthStretch, 0.2f);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < orders.Count; i++)
            {
                ImGui.TableNextColumn();
                ImGui.Text((i + 1).ToString());
                ImGui.TableNextColumn();
                ImGui.Text(orders[i].Name);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("IsRunning: " + orders[i].IsRunning);
                    ImGui.Text("IsFinished: " + orders[i].IsFinished);
                    ImGui.EndTooltip();
                }
                ImGui.TableNextColumn();
                ImGui.Text(orders[i].Details);
            }

            ImGui.EndTable();
        }

        private void DisplaySurveyInfo()
        {
            if (_entity?.GetView<GravSurveyView>() is { } gravSurvey)
            {
                Displays.GravitationalAnomlay(gravSurvey);
            }
        }

        private void DisplayProgressIndicator()
        {
            if (_entity == null) return;

            var geoSurvey = _entity.GetView<GeoSurveyView>();
            bool isColonizeable = _entity.HasView<ColonizableView>();
            var colony = GetColony();

            // Once a colony on this body has infrastructure installed it's an established,
            // working colony — the survey/colonize progress is behind us, so show a live
            // infrastructure overview in place of the progress bar.
            var infrastructure = colony?.GetView<InfrastructureView>();
            if (colony != null && infrastructure is { HasInstalledInfrastructure: true })
            {
                DisplayInfrastructureOverview(colony, infrastructure);
                return;
            }

            if (geoSurvey == null && !isColonizeable && colony == null)
                return;

            var stages = new System.Collections.Generic.List<SurveyProgressBar.Stage>(3);

            stages.Add(new SurveyProgressBar.Stage(
                "Discovered",
                1f,
                "Discovered\nThis body has been detected and is visible on the system map."));

            if (geoSurvey != null)
            {
                const string rewardSummary =
                    "Reveals:\n"
                    + "  • Atmospheric composition\n"
                    + "  • Mineral deposits and their accessibility\n"
                    + "  • Surface conditions used to assess colonization";
                float fill = 0f;
                string tooltip;
                if (geoSurvey.HasSurveyStarted && geoSurvey.PointsRequired > 0)
                {
                    fill = (float)(geoSurvey.PercentComplete / 100.0);
                    if (geoSurvey.IsSurveyComplete)
                    {
                        tooltip = "Geological Survey\nComplete. Mineral and atmospheric data are available below.";
                    }
                    else
                    {
                        tooltip = "Geological Survey\nIn progress: " + geoSurvey.PercentComplete.ToString("0") + "%"
                            + " (" + geoSurvey.PointsCompleted + " / " + geoSurvey.PointsRequired + " survey points)\n\n"
                            + rewardSummary;
                    }
                }
                else
                {
                    tooltip = "Geological Survey\nNot started. Send a ship with geo-survey ability to scan this body.\n\n"
                        + rewardSummary;
                }
                stages.Add(new SurveyProgressBar.Stage("Geo Survey", fill, tooltip));
            }

            // A colony only counts as established once infrastructure is delivered, so the
            // final stage tracks infrastructure rather than mere presence of a colony.
            if (isColonizeable || colony != null)
            {
                string infraTooltip = colony != null
                    ? "Infrastructure\nThis body has a colony, but no infrastructure is installed yet. "
                      + "Deliver infrastructure here — build it elsewhere and ship it in, or construct it "
                      + "locally — to bring the colony online. Infrastructure provides the support capacity "
                      + "every other installation on the body draws on."
                    : "Infrastructure\nNo colony yet. Establish one by delivering infrastructure to this body: "
                      + "load it onto a freighter and unload it here. Infrastructure provides the support "
                      + "capacity every other installation draws on, so it must come first.";
                stages.Add(new SurveyProgressBar.Stage("Infrastructure", 0f, infraTooltip));
            }

            if (stages.Count == 0) return;

            SectionLabel("PROGRESS");
            ImGui.Indent();
            SurveyProgressBar.Draw("##entity-progress", stages, _accentColor);
            ImGui.Unindent();
        }

        private void DisplayInfrastructureOverview(EntitySnapshot colony, InfrastructureView infrastructure)
        {
            bool overCapacity = infrastructure.CapacityAvailable < 0;

            string colonyName = colony.GetView<NameView>()?.Name ?? "";
            SectionLabel(string.IsNullOrWhiteSpace(colonyName) ? "COLONY" : colonyName.ToUpperInvariant());

            // Single-line overview: capacity used vs provided, and the resulting output.
            // TextUnformatted: the literal '%' would be read as a printf specifier by ImGui.Text.
            // One decimal so near-full over-capacity (e.g. 99.7%) is not rounded to "100%".
            string summary = $"{infrastructure.CapacityRequired:N0} / {infrastructure.CapacityProvided:N0} capacity"
                + $" · {(infrastructure.Efficiency * 100):0.0}% output";
            if (overCapacity)
                summary += $" · short {(-infrastructure.CapacityAvailable):N0}";

            const float cardPadding = 8f;
            float cardHeight = cardPadding * 2f + ImGui.GetTextLineHeightWithSpacing() * 2f;

            ImGui.PushStyleColor(ImGuiCol.ChildBg,
                new Vector4(_accentColor.X, _accentColor.Y, _accentColor.Z, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.Border,
                new Vector4(_accentColor.X, _accentColor.Y, _accentColor.Z, 0.35f));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(cardPadding, cardPadding));

            if (ImGui.BeginChild("##infra-card", new Vector2(0f, cardHeight),
                ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                ImGui.TextUnformatted("Infrastructure");
                ImGui.PopStyleColor();

                ImGui.PushStyleColor(ImGuiCol.Text, overCapacity ? Styles.BadColor : Styles.DescriptiveColor);
                ImGui.TextUnformatted(summary);
                ImGui.PopStyleColor();
            }
            ImGui.EndChild();

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        // --- Layout Helpers ---

        private void SectionLabel(string label)
        {
            ImGui.Spacing();
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float availWidth = ImGui.GetContentRegionAvail().X;
            var labelSize = ImGui.CalcTextSize(label);
            float lineY = pos.Y + labelSize.Y * 0.5f;

            ImGui.PushStyleColor(ImGuiCol.Text,
                new Vector4(_accentColor.X * 0.7f, _accentColor.Y * 0.7f, _accentColor.Z * 0.7f, 0.8f));
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();

            drawList.AddLine(
                new Vector2(pos.X + labelSize.X + 8f, lineY),
                new Vector2(pos.X + availWidth, lineY),
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(_accentColor.X, _accentColor.Y, _accentColor.Z, 0.15f)));
        }

        private void StatBlock(string label, string value, Vector4? valueColor = null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            if (valueColor.HasValue)
                ImGui.PushStyleColor(ImGuiCol.Text, valueColor.Value);
            ImGui.TextUnformatted(value);
            if (valueColor.HasValue)
                ImGui.PopStyleColor();
        }

        /// <summary>
        /// Renders label/value stats as accent-tinted cards laid out three per row.
        /// Each card matches the infrastructure overview card's styling.
        /// </summary>
        private void DisplayStatCards(string idPrefix, System.Collections.Generic.List<(string Label, string Value)> stats)
        {
            if (stats.Count == 0) return;

            const int columns = 3;
            const float cardPadding = 6f;
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float avail = ImGui.GetContentRegionAvail().X;
            float cardWidth = MathF.Floor((avail - spacing * (columns - 1)) / columns);
            float cardHeight = cardPadding * 2f + ImGui.GetTextLineHeightWithSpacing() * 2f;

            ImGui.PushStyleColor(ImGuiCol.ChildBg,
                new Vector4(_accentColor.X, _accentColor.Y, _accentColor.Z, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.Border,
                new Vector4(_accentColor.X, _accentColor.Y, _accentColor.Z, 0.35f));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(cardPadding, cardPadding));

            for (int i = 0; i < stats.Count; i++)
            {
                if (i % columns != 0)
                    ImGui.SameLine();

                if (ImGui.BeginChild(idPrefix + i, new Vector2(cardWidth, cardHeight),
                    ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                    ImGui.TextUnformatted(stats[i].Label);
                    ImGui.PopStyleColor();
                    ImGui.TextUnformatted(stats[i].Value);
                }
                ImGui.EndChild();
            }

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        private Vector4 GetHealthColor(float value)
        {
            if (value >= 0.75f) return Styles.GoodColor;
            if (value >= 0.50f) return Styles.OkColor;
            if (value >= 0.25f) return Styles.MediocreColor;
            return Styles.BadColor;
        }

        private void DrawRadialIndicator(
            ImDrawListPtr drawList, Vector2 center, float radius, float ringThickness,
            float value, string label, string centerText, bool isPlaceholder,
            string? extraTooltip = null)
        {
            var dimColor = new Vector4(
                _accentColor.X * 0.3f, _accentColor.Y * 0.3f, _accentColor.Z * 0.3f, 0.4f);
            var healthColor = isPlaceholder ? Styles.DescriptiveColor : GetHealthColor(value);
            var textColor = isPlaceholder ? Styles.DescriptiveColor : healthColor;

            // Background ring (full circle, dim)
            drawList.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(dimColor), 32, ringThickness);

            // Foreground arc (starts at 12 o'clock, sweeps clockwise)
            if (!isPlaceholder && value > 0f)
            {
                float startAngle = -MathF.PI / 2f;
                float endAngle = startAngle + value * 2f * MathF.PI;
                drawList.PathArcTo(center, radius, startAngle, endAngle, 32);
                drawList.PathStroke(ImGui.ColorConvertFloat4ToU32(healthColor), ImDrawFlags.None, ringThickness);
            }

            // Center text (centered in the ring)
            var centerTextSize = ImGui.CalcTextSize(centerText);
            drawList.AddText(
                new Vector2(center.X - centerTextSize.X * 0.5f, center.Y - centerTextSize.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(textColor),
                centerText);

            // Label below the ring
            var labelSize = ImGui.CalcTextSize(label);
            float labelY = center.Y + radius + ringThickness * 0.5f + 2f;
            drawList.AddText(
                new Vector2(center.X - labelSize.X * 0.5f, labelY),
                ImGui.ColorConvertFloat4ToU32(Styles.DescriptiveColor),
                label);

            // Tooltip on hover (ring + label)
            var min = new Vector2(center.X - radius - ringThickness, center.Y - radius - ringThickness);
            var max = new Vector2(
                center.X + radius + ringThickness,
                labelY + labelSize.Y);
            if (ImGui.IsMouseHoveringRect(min, max))
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(label + ": " + (isPlaceholder ? "N/A" : (value * 100f).ToString("0") + "%"));
                if (extraTooltip != null)
                    ImGui.TextUnformatted(extraTooltip);
                ImGui.EndTooltip();
            }
        }

        private void DisplayShipStatusRow()
        {
            float radius = 22f;
            float ringThickness = 4f;
            float indicatorSpacing = 16f;
            float indicatorWidth = radius * 2f + indicatorSpacing;
            float availWidth = ImGui.GetContentRegionAvail().X;

            var cursorPos = ImGui.GetCursorScreenPos();
            float centerY = cursorPos.Y + radius + 4f;

            var drawList = ImGui.GetWindowDrawList();

            var ship = _entity!.GetView<ShipView>();
            var thrust = _entity.GetView<ThrustView>();

            // Compute values
            float htkValue = 0f;
            string htkText = "-";
            float compValue = 0f;
            string compText = "-";
            float armorValue = 0f;
            string armorText = "N/A";
            bool armorPlaceholder = true;

            if (ship != null && ship.TotalComponents > 0)
            {
                htkValue = (float)ship.AverageComponentHealth;
                htkText = (htkValue * 100f).ToString("0") + "%";
                compValue = (float)ship.OperationalComponents / ship.TotalComponents;
                compText = ship.OperationalComponents + "/" + ship.TotalComponents;
            }

            if (ship != null && ship.ArmorThicknessMm > 0)
            {
                armorPlaceholder = false;
                armorValue = 1.0f;
                armorText = ship.ArmorThicknessMm.ToString("0.#") + "mm";
            }

            // Compute delta V values
            float dvValue = 0f;
            string dvText = "N/A";
            string? dvTooltip = null;
            bool dvPlaceholder = true;

            if (thrust != null && thrust.ExhaustVelocityMps > 0)
            {
                dvPlaceholder = false;
                double dv = thrust.DeltaVMps;
                dvTooltip = Stringify.Velocity(dv);

                // Compact center text
                if (dv >= 1e6)
                    dvText = (dv / 1e6).ToString("0.#") + "M";
                else if (dv >= 1e3)
                    dvText = (dv / 1e3).ToString("0.#") + "k";
                else
                    dvText = dv.ToString("0");

                // Percentage: current DV / max DV at full fuel (pre-computed server-side)
                if (dv > 0 && thrust.MaxDeltaVMps > 0)
                    dvValue = Math.Clamp((float)(dv / thrust.MaxDeltaVMps), 0f, 1f);
            }

            // Battery / power store
            float energyValue = 0f;
            string energyText = "N/A";
            string? energyTooltip = null;
            bool energyPlaceholder = true;
            var energy = _entity.GetView<EnergyView>();
            if (energy != null && energy.StoreMax > 0)
            {
                energyPlaceholder = false;
                energyValue = Math.Clamp((float)(energy.Stored / energy.StoreMax), 0f, 1f);
                energyText = energy.StoredPercent.ToString("0") + "%";
                energyTooltip = Stringify.Energy(energy.Stored) + " / " + Stringify.Energy(energy.StoreMax);
                if (energy.IsRecharging)
                    energyTooltip += "\nRecharging";
                if (energy.AcceptRateKW > 0)
                    energyTooltip += "\nAccept " + Stringify.Power(energy.AcceptRateKW);
                if (energy.ActionBlock != null)
                {
                    energyText = "!";
                    energyTooltip += "\nBLOCKED: " + energy.ActionBlock.ActionName
                        + "\n" + energy.ActionBlock.Reason;
                }
            }

            // Drive / warp fuel from cargo tanks (same source as standing Refuel / GetFuelPercent).
            // Ring label stays "FUEL"; specific propellant type is shown in the hover tooltip.
            float fuelValue = 0f;
            string fuelText = "N/A";
            const string fuelLabel = "FUEL";
            string? fuelTooltip = null;
            bool fuelPlaceholder = true;
            if (thrust != null && !string.IsNullOrEmpty(thrust.FuelName))
                fuelTooltip = "Fuel type: " + thrust.FuelName;

            if (thrust != null && thrust.MaxFuelKg > 0)
            {
                fuelPlaceholder = false;
                fuelValue = Math.Clamp((float)(thrust.TotalFuelKg / thrust.MaxFuelKg), 0f, 1f);
                fuelText = (fuelValue * 100f).ToString("0") + "%";
                string massLine = Stringify.Mass(thrust.TotalFuelKg) + " / " + Stringify.Mass(thrust.MaxFuelKg);
                fuelTooltip = fuelTooltip == null ? massLine : fuelTooltip + "\n" + massLine;
            }
            else if (thrust != null && thrust.TotalFuelKg > 0)
            {
                fuelPlaceholder = false;
                fuelValue = 1f;
                fuelText = CompactMass(thrust.TotalFuelKg);
                string massLine = Stringify.Mass(thrust.TotalFuelKg);
                fuelTooltip = fuelTooltip == null ? massLine : fuelTooltip + "\n" + massLine;
            }

            // Six indicators: propulsion / hull / stores
            float x0 = cursorPos.X + radius;

            DrawRadialIndicator(drawList, new Vector2(x0, centerY),
                radius, ringThickness, dvValue, "Δv", dvText, dvPlaceholder, dvTooltip);
            DrawRadialIndicator(drawList, new Vector2(x0 + indicatorWidth, centerY),
                radius, ringThickness, fuelValue, fuelLabel, fuelText, fuelPlaceholder, fuelTooltip);
            DrawRadialIndicator(drawList, new Vector2(x0 + indicatorWidth * 2f, centerY),
                radius, ringThickness, htkValue, "HTK", htkText, false);
            DrawRadialIndicator(drawList, new Vector2(x0 + indicatorWidth * 3f, centerY),
                radius, ringThickness, compValue, "COMP", compText, false);
            DrawRadialIndicator(drawList, new Vector2(x0 + indicatorWidth * 4f, centerY),
                radius, ringThickness, armorValue, "ARMOR", armorText, armorPlaceholder);
            DrawRadialIndicator(drawList, new Vector2(x0 + indicatorWidth * 5f, centerY),
                radius, ringThickness, energyValue, "ENERGY", energyText, energyPlaceholder, energyTooltip);

            // Gauges only here — ship/fleet orders live in their own sections below so long
            // names/details cannot paint over the rings.
            float labelPad = ImGui.GetTextLineHeight() + 6f;
            ImGui.InvisibleButton("##statusRow", new Vector2(availWidth, radius * 2f + ringThickness + labelPad));
        }

        private static string CompactMass(double kg)
        {
            if (kg >= 1e6)
                return (kg / 1e6).ToString("0.#") + "M";
            if (kg >= 1e3)
                return (kg / 1e3).ToString("0.#") + "k";
            return kg.ToString("0");
        }

        private void DrawOrdersSection(
            string sectionTitle,
            string tableId,
            System.Collections.Generic.IReadOnlyList<OrderSnapshot> orders,
            bool allowManeuverEdit,
            bool showIdleIfEmpty = false)
        {
            if (orders.Count == 0)
            {
                if (showIdleIfEmpty)
                {
                    SectionLabel(sectionTitle);
                    ImGui.Indent();
                    ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                    ImGui.TextUnformatted("Idle");
                    ImGui.PopStyleColor();
                    ImGui.Unindent();
                }
                return;
            }

            SectionLabel(sectionTitle + " (" + orders.Count + ")");
            ImGui.Indent();

            // Stack name then details (wrapped) — a fixed-width name column was clipping into details.
            for (int i = 0; i < orders.Count; i++)
            {
                var order = orders[i];
                ImGui.PushID(tableId + i);

                ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                ImGui.TextUnformatted((i + 1).ToString() + ".");
                ImGui.PopStyleColor();
                ImGui.SameLine();

                if (allowManeuverEdit && order.IsEditableManeuver)
                {
                    ImGui.PushStyleColor(ImGuiCol.Header, Styles.InvisibleColor);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered,
                        new Vector4(_accentColor.X * 0.2f, _accentColor.Y * 0.2f, _accentColor.Z * 0.2f, 0.5f));
                    ImGui.PushStyleColor(ImGuiCol.HeaderActive,
                        new Vector4(_accentColor.X * 0.3f, _accentColor.Y * 0.3f, _accentColor.Z * 0.3f, 0.7f));
                    if (ImGui.Selectable(order.Name + "##sel", false))
                        _uiState.OpenManeuverPanelForOrder(EntityId, SystemId, order);
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Click to edit or delete this order");
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, _accentColor);
                    ImGui.TextWrapped(order.Name);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Running: " + order.IsRunning);
                        ImGui.Text("Finished: " + order.IsFinished);
                        ImGui.EndTooltip();
                    }
                }

                if (!string.IsNullOrEmpty(order.Details))
                {
                    ImGui.Indent();
                    ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                    ImGui.TextWrapped(order.Details);
                    ImGui.PopStyleColor();
                    ImGui.Unindent();
                }

                ImGui.PopID();
            }

            ImGui.Unindent();
        }

        // --- Type-Specific Content ---

        private void DisplayShipContent()
        {
            var ship = _entity!.GetView<ShipView>();

            DisplayShipStatusRow();

            // Crew row
            SectionLabel("CREW");

            string captainName = ship?.CommanderName ?? "Unassigned";

            ImGui.Indent();
            int crewCols = ship != null ? 2 : 1;
            if (ImGui.BeginTable("##crew", crewCols, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                StatBlock("COMMANDER", captainName);

                if (ship != null)
                {
                    ImGui.TableNextColumn();
                    StatBlock("CREW", ship.CrewRequired.ToString());
                }

                ImGui.EndTable();
            }
            ImGui.Unindent();

            // Propulsion stat grid
            var thrust = _entity.GetView<ThrustView>();
            var warp = _entity.GetView<WarpAbilityView>();

            if (thrust != null || warp != null)
            {
                SectionLabel("PROPULSION");

                ImGui.Indent();
                int propCols = (thrust != null ? 3 : 0) + (warp != null ? 1 : 0);
                if (ImGui.BeginTable("##propulsion", propCols, ImGuiTableFlags.SizingStretchSame))
                {
                    if (thrust != null)
                    {
                        ImGui.TableNextColumn();
                        StatBlock("THRUST", Stringify.Thrust(thrust.ThrustNewtons));

                        ImGui.TableNextColumn();
                        StatBlock("BURN", Stringify.Mass(thrust.FuelBurnRateKgPerSec) + "/s");

                        ImGui.TableNextColumn();
                        StatBlock("EXHAUST", Stringify.Velocity(thrust.ExhaustVelocityMps));
                    }
                    if (warp != null)
                    {
                        ImGui.TableNextColumn();
                        StatBlock("WARP", Stringify.Velocity(warp.MaxSpeedMps));
                    }
                    ImGui.EndTable();
                }

                if (warp != null && (warp.BubbleCreationCostKJ > 0 || warp.BubbleSustainCostKW > 0))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Styles.DescriptiveColor);
                    ImGui.TextWrapped(
                        "Bubble " + Stringify.Energy(warp.BubbleCreationCostKJ)
                        + "  ·  Sustain " + Stringify.Power(warp.BubbleSustainCostKW));
                    ImGui.PopStyleColor();
                }
                ImGui.Unindent();
            }

            // Location
            var parent = GetParent();
            if (parent != null)
            {
                SectionLabel("LOCATION");
                ImGui.Indent();
                ImGui.PushStyleColor(ImGuiCol.Text, _accentColor);
                if (_entity.GetView<WarpMovingView>() is { } warping)
                {
                    ImGui.TextUnformatted("Warping");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(Stringify.Velocity(warping.SpeedMps));
                }
                else
                {
                    ImGui.TextUnformatted("Orbiting");
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    if (ImGui.SmallButton(parent.GetView<NameView>()?.Name ?? "Unknown"))
                    {
                        _uiState.EntityClicked(parent.Id, _uiState.SelectedStarSystemId, MouseButtons.Primary);
                    }
                }
                ImGui.Unindent();
            }

            // Ship activity | fleet mission side by side.
            var ownOrdersView = _entity.GetView<OrdersView>();
            var shipOrders = OrderDisplayHelpers.GetShipActivityOrders(
                ownOrdersView, _entity.GetView<ActivityView>());
            bool isFleetMember = OrderDisplayHelpers.IsFleetMember(_uiState.GameClient, EntityId);
            var fleetOrders = isFleetMember
                ? OrderDisplayHelpers.GetFleetOrders(_uiState.GameClient, EntityId)
                : System.Array.Empty<OrderSnapshot>();

            bool shipHasQueuedOrders = OrderDisplayHelpers.GetShipOrders(ownOrdersView).Count > 0;
            int orderCols = isFleetMember ? 2 : 1;
            if (ImGui.BeginTable("##ship-fleet-orders", orderCols,
                    ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX))
            {
                ImGui.TableNextColumn();
                DrawOrdersSection("SHIP ORDERS", "##ship-orders", shipOrders,
                    allowManeuverEdit: shipHasQueuedOrders, showIdleIfEmpty: true);

                if (isFleetMember)
                {
                    ImGui.TableNextColumn();
                    DrawOrdersSection("FLEET ORDERS", "##fleet-orders", fleetOrders,
                        allowManeuverEdit: false, showIdleIfEmpty: true);
                }

                ImGui.EndTable();
            }

            // Cargo / fuel / energy fill is shown only in the status rings above — no duplicate bars.
        }

        private void DisplayStarContent()
        {
            var star = _entity!.GetView<StarView>();
            var massVolume = _entity.GetView<MassVolumeView>();

            ImGui.Columns(2, "##star-info", true);

            if (star != null)
            {
                DisplayHelpers.PrintRow("Spectral Type", star.SpectralType + star.SpectralSubDivision);
                DisplayHelpers.PrintRow("Class", star.SpectralClass);
                DisplayHelpers.PrintRow("Temperature", star.SurfaceTemperatureC.ToString("#,##0") + " °C");
                DisplayHelpers.PrintRow("Luminosity", star.Luminosity + " " + star.LuminosityClass + " (" + star.LuminosityClassDescription + ")");
                DisplayHelpers.PrintRow("Age", Stringify.Quantity(star.AgeYears));
            }

            if (massVolume != null)
            {
                DisplayHelpers.PrintRow("Mass", Stringify.CelestialMass(massVolume.MassKg));
                DisplayHelpers.PrintRow("Radius", Stringify.Distance(massVolume.RadiusMetres));
                DisplayHelpers.PrintRow("Density", massVolume.DensityGramsPerCm3.ToString("##0.000") + " g/cm³");
            }

            if (star != null)
            {
                DisplayHelpers.PrintRow("Habitable Zone", star.MinHabitableRadiusAu.ToString("0.##") + " - " + star.MaxHabitableRadiusAu.ToString("0.##") + " AU");
            }

            ImGui.Columns(1);

            DisplayOrbitInfo();
            DisplaySurveyInfo();
        }

        private void DisplaySystemBodyContent()
        {
            bool isGeoSurveyed = _entity!.GetView<GeoSurveyView>()?.IsSurveyComplete ?? false;

            DisplayProgressIndicator();

            var bodyStats = new System.Collections.Generic.List<(string Label, string Value)>(10);
            var body = _entity.GetView<BodyView>();
            if (body != null)
            {
                bodyStats.Add(("Gravity",
                    body.GravityMetresPerSec2.ToString("0.##") + " m/s² · "
                    + (body.GravityMetresPerSec2 / 9.80665).ToString("0.###") + " G"));
                bodyStats.Add(("Temperature", body.SurfaceTemperatureC.ToString("##0.#") + " °C"));
                bodyStats.Add(("Day Length", body.DayLength.TotalDays.ToString("0.#") + " days"));
                bodyStats.Add(("Axial Tilt", body.AxialTiltDegrees.ToString("0.#") + "°"));
                bodyStats.Add(("Tectonics", body.Tectonics));
                bodyStats.Add(("Magnetic Field", body.MagneticFieldMicroTesla.ToString("0.##") + " μT"));
                // Every colony needs infrastructure now, so show what a body's infrastructure
                // must be rated for. Earth-like worlds take the default Earth-Standard design;
                // hostile worlds need one tuned to their gravity and atmospheric pressure.
                string infraReq;
                if (!_entity.HasView<ColonizableView>())
                {
                    infraReq = "Not colonizable";
                }
                else if (body.SupportsPopulations)
                {
                    infraReq = "Earth-Standard";
                }
                else
                {
                    string grav = body.GravityMetresPerSec2.ToString("0.##") + " m/s²";
                    string pressure;
                    var atmosphere = _entity.GetView<AtmosphereView>();
                    if (!isGeoSurveyed)
                        pressure = "? atm"; // atmospheric pressure isn't known until surveyed
                    else if (atmosphere != null && atmosphere.PressureAtm > 0)
                        pressure = atmosphere.PressureAtm.ToString("0.##") + " atm";
                    else
                        pressure = "vacuum";
                    infraReq = grav + " · " + pressure;
                }
                bodyStats.Add(("Infrastructure", infraReq));
            }

            if (_entity.GetView<MassVolumeView>() is { } massVolume)
            {
                bodyStats.Add(("Mass", Stringify.CelestialMass(massVolume.MassKg)));
                bodyStats.Add(("Radius", Stringify.Distance(massVolume.RadiusMetres)));
            }

            SectionLabel(body != null ? body.BodyType.ToUpperInvariant() : "CELESTIAL BODY");
            DisplayStatCards("##body-stat", bodyStats);

            if (isGeoSurveyed && _entity.GetView<AtmosphereView>() is { } atmosphereView)
            {
                atmosphereView.Display();
            }

            if (_entity.GetView<ColonyView>() is { } colonyView)
            {
                colonyView.Display(_entity.Id);
            }

            if (isGeoSurveyed && _entity.GetView<MineralDepositsView>() is { } deposits
                && ImGui.CollapsingHeader("Minerals", ImGuiTreeNodeFlags.DefaultOpen))
            {
                deposits.Display(_entity.Id);
            }

            DisplaySurveyInfo();

            var colony = GetColony();
            if (colony?.GetView<ColonyMiningView>() is { Minerals.Count: > 0 } mining
                && ImGui.CollapsingHeader("Mining", ImGuiTreeNodeFlags.DefaultOpen))
            {
                mining.Display();
            }

            // Installations (collapsed by default)
            if (_entity.GetView<InstallationsView>() is { } installations
                && ImGui.CollapsingHeader("Installations"))
            {
                installations.Display(_entity.Id, _uiState);
            }

            // Cargo (collapsed by default)
            if (_entity.GetView<CargoStorageView>() is { } cargo)
            {
                cargo.Display(_entity.Id, _uiState, ImGuiTreeNodeFlags.None);
            }
        }

        private void DisplaySmallBodyContent()
        {
            bool isGeoSurveyed = _entity!.GetView<GeoSurveyView>()?.IsSurveyComplete ?? false;

            DisplayProgressIndicator();

            ImGui.Columns(2, "##small-body-info", true);

            if (_entity.GetView<BodyView>() is { } body)
            {
                DisplayHelpers.PrintRow("Body Type", body.BodyType);
            }

            if (_entity.GetView<MassVolumeView>() is { } massVolume)
            {
                DisplayHelpers.PrintRow("Mass", Stringify.CelestialMass(massVolume.MassKg));
                DisplayHelpers.PrintRow("Radius", Stringify.Distance(massVolume.RadiusMetres));
            }

            ImGui.Columns(1);

            if (isGeoSurveyed && _entity.GetView<MineralDepositsView>() is { } deposits
                && ImGui.CollapsingHeader("Minerals", ImGuiTreeNodeFlags.DefaultOpen))
            {
                deposits.Display(_entity.Id);
            }

            DisplaySurveyInfo();
        }

        private void DisplayColonyContent()
        {
            bool isGeoSurveyed = _entity!.GetView<GeoSurveyView>()?.IsSurveyComplete ?? false;

            DisplayProgressIndicator();

            // Population (prominent at top)
            if (_entity.GetView<ColonyView>() is { } colonyView)
            {
                colonyView.Display(_entity.Id);
            }

            // Environment section
            if (ImGui.CollapsingHeader("Environment"))
            {
                ImGui.Columns(2, "##environment-info", true);
                if (_entity.GetView<BodyView>() is { } body)
                {
                    DisplayHelpers.PrintRow("Body Type", body.BodyType);
                    DisplayHelpers.PrintRow("Gravity", body.GravityMetresPerSec2.ToString("0.##") + " m/s²",
                        null, (body.GravityMetresPerSec2 / 9.80665).ToString("0.###") + " G");
                    DisplayHelpers.PrintRow("Temperature", body.SurfaceTemperatureC.ToString("##0.#") + " °C");
                }
                if (_entity.GetView<MassVolumeView>() is { } massVolume)
                {
                    DisplayHelpers.PrintRow("Radius", Stringify.Distance(massVolume.RadiusMetres));
                }
                ImGui.Columns(1);
            }

            // Atmosphere
            if (isGeoSurveyed && _entity.GetView<AtmosphereView>() is { } atmosphere)
            {
                atmosphere.Display();
            }

            // Minerals (collapsed by default)
            if (isGeoSurveyed && _entity.GetView<MineralDepositsView>() is { } deposits
                && ImGui.CollapsingHeader("Minerals"))
            {
                deposits.Display(_entity.Id);
            }

            // Mining
            if (_entity.GetView<ColonyMiningView>() is { Minerals.Count: > 0 } mining
                && ImGui.CollapsingHeader("Mining", ImGuiTreeNodeFlags.DefaultOpen))
            {
                mining.Display();
            }

            // Installations
            if (_entity.GetView<InstallationsView>() is { } installations
                && ImGui.CollapsingHeader("Installations", ImGuiTreeNodeFlags.DefaultOpen))
            {
                installations.Display(_entity.Id, _uiState);
            }

            // Cargo
            if (_entity.GetView<CargoStorageView>() is { } cargo)
            {
                cargo.Display(_entity.Id, _uiState);
            }

            DisplayOrbitInfo();
        }

        private void DisplayGenericContent()
        {
            ImGui.Columns(2, "##generic-info", true);
            if (_entity!.GetView<MassVolumeView>() is { } massVolume)
            {
                DisplayHelpers.PrintRow("Mass", Stringify.Mass(massVolume.MassKg));
                DisplayHelpers.PrintRow("Radius", Stringify.Distance(massVolume.RadiusMetres));
            }
            ImGui.Columns(1);

            DisplayOrbitInfo();
            DisplayOrders();

            if (_entity.GetView<InstallationsView>() is { } installations
                && ImGui.CollapsingHeader("Components", ImGuiTreeNodeFlags.DefaultOpen))
            {
                installations.Display(_entity.Id, _uiState);
            }

            if (_entity.GetView<CargoStorageView>() is { } cargo)
            {
                cargo.Display(_entity.Id, _uiState);
            }

            bool isGeoSurveyed = _entity.GetView<GeoSurveyView>()?.IsSurveyComplete ?? false;

            if (isGeoSurveyed && _entity.GetView<MineralDepositsView>() is { } deposits
                && ImGui.CollapsingHeader("Minerals", ImGuiTreeNodeFlags.DefaultOpen))
            {
                deposits.Display(_entity.Id);
            }

            DisplaySurveyInfo();
        }
    }
}
