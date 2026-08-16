using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using Pulsar4X.Api;
using Pulsar4X.Client.BodyVisuals;
using Pulsar4X.Input;
using Pulsar4X.Orbital;
using SDL3;

namespace Pulsar4X.Client.Rendering
{
    internal class SystemMapRendering : UpdateWindowState
    {
        GlobalUIState _state;
        string? _systemId;
        Camera _camera;
        SDL3Window _window;
        SystemLabelDistributor _distributor;
        readonly SystemStarfield _starfield = new();

        internal Dictionary<string, IDrawData> UIWidgets = new();

        ConcurrentDictionary<int, Icon> _testIcons = new();
        ConcurrentDictionary<int, Icon> _orbitRings = new();
        ConcurrentDictionary<int, Icon> _moveIcons = new();
        ConcurrentDictionary<int, Icon> _entityIcons = new();
        ConcurrentDictionary<int, Icon> _bodyIcons = new();

        HashSet<EntityLabel> _allLabels = new();
        HashSet<EntityLabel> _visibleLabels = new();
        readonly HashSet<int> _syncSeen = new();
        readonly List<int> _syncGone = new();

        // Last snapshot reference used to build (or keep) icons. Positions are read live via
        // SnapshotPosition; a new snapshot only rebuilds icons when the visual shell or move overlay changes.
        Dictionary<int, EntitySnapshot> _iconedSnapshots = new();

        DateTime _lastPhysicsTime;

        // Per-body-type minimum camera zoom for the label to render. Lower-tier
        // bodies (moons, ships, asteroids, comets) only show labels once you've
        // zoomed in enough that they aren't just visual clutter. Stars, planets,
        // dwarf planets and colonies are always shown (subject to view prefs).
        static readonly Dictionary<UserOrbitSettings.OrbitBodyType, float> _minZoomForLabel = new()
        {
            { UserOrbitSettings.OrbitBodyType.Star,         0f },
            { UserOrbitSettings.OrbitBodyType.Planet,       0f },
            { UserOrbitSettings.OrbitBodyType.DwarfPlanet,  0f },
            { UserOrbitSettings.OrbitBodyType.Colony,       0f },
            { UserOrbitSettings.OrbitBodyType.Moon,        1e4f },
            { UserOrbitSettings.OrbitBodyType.Ship,        2e4f },
            { UserOrbitSettings.OrbitBodyType.Asteroid,    5e4f },
            { UserOrbitSettings.OrbitBodyType.Comet,       2e4f },
            { UserOrbitSettings.OrbitBodyType.Unknown,      0f },
        };

        ConcurrentDictionary<int, InteractableState[]> _interactable = new();
        IOrderedEnumerable<IGrouping<byte, InteractableState>> _interactableGrouped =
            Array.Empty<InteractableState>().GroupBy(_ => (byte)0).OrderBy(g => g.Key);

        internal List<IDrawData> SelectedEntityExtras = new List<IDrawData>();
        internal Vector2 GalacticMapPosition = new Vector2();

        bool _updateLabels = false;

        internal SystemMapRendering(SDL3Window window, GlobalUIState state)
        {
            _state = state;

            _distributor = EntityLabelDistributor.Group;

            _camera = _state.Camera;
            _window = window;

            // Initialize ship + celestial body icon texture caches
            ShipIcon.InitializeTexture(window.Renderer);
            BodyMapTextureCache.Initialize(window.Renderer);

            foreach (var item in TestDrawIconData.GetTestIcons())
            {
                _testIcons.TryAdd(-1, item);
            }

            var mainWin = (PulsarMainWindow)window;
            mainWin.MouseButtonDownOccured += (object? sender, SDL.Event e) =>
            {
                if (mainWin.PlatformBackend.WantsMouseCapture())
                    return;

                foreach (var i in _interactableGrouped)
                {
                    var key = i.Key;

                    foreach (var j in i)
                    {
                        if (j.IsDisabled)
                            continue;

                        var item = j.Item;

                        var c = item.Contains(new(e.Motion.X, e.Motion.Y));

                        if (c)
                        {
                            j.IsPressed = true;
                            if (item.OnPointerDown(e))
                                return;
                        }
                    }
                }
            };
            mainWin.MouseButtonUpOccured += (object? sender, SDL.Event e) =>
            {
                if (mainWin.PlatformBackend.WantsMouseCapture())
                    return;

                foreach (var i in _interactableGrouped)
                {
                    var key = i.Key;

                    foreach (var j in i)
                    {
                        if (j.IsDisabled)
                            continue;

                        var item = j.Item;

                        var c = item.Contains(new(e.Motion.X, e.Motion.Y));

                        if (c)
                        {
                            j.IsPressed = false;
                            if (item.OnPointerUp(e))
                                return;
                        }
                    }
                }
            };
            mainWin.MouseMoveOccured += (object? sender, SDL.Event e) =>
            {
                foreach (var i in _interactableGrouped)
                {
                    var key = i.Key;

                    foreach (var j in i)
                    {
                        if (j.IsDisabled)
                            continue;

                        var item = j.Item;

                        if (mainWin.PlatformBackend.WantsMouseCapture())
                        {
                            if (j.IsHovered)
                            {
                                j.IsHovered = false;
                                if (item.OnPointerExit(e))
                                    return;
                            }
                            continue;
                        }

                        var c = item.Contains(new(e.Motion.X, e.Motion.Y));

                        if (j.IsHovered)
                        {
                            if (c)
                            {
                                if (item.OnPointerMove(e))
                                    return;
                            }
                            else
                            {
                                j.IsHovered = false;
                                if (item.OnPointerExit(e))
                                    return;
                            }
                        }
                        else if (c)
                        {
                            j.IsHovered = true;
                            if (item.OnPointerEnter(e))
                                return;
                        }
                    }
                }
            };

            _camera.PanOccured +=
                (object? sender, Orbital.Vector3 pos) => _updateLabels = true;

            _camera.ZoomOccured +=
                (object? sender, float zoom) => _updateLabels = true;

            SystemViewPreferences.GetInstance().ViewUpdateOccured +=
                (object? sender, SystemViewPreferences.View view) => _updateLabels = true;

            // should be empty
            _interactableGrouped = _interactable
                .Values
                .SelectMany(x => x)
                .GroupBy(x => x.Item.Priority)
                .OrderByDescending(x => x.Key);
        }

        internal void Initialize(string systemId)
        {
            _systemId = systemId;
            _starfield.Rebuild(systemId);
            SyncIcons();
            _updateLabels = true; // update labels on first frame
        }

        void AddEntityIcon(EntitySnapshot entity, Icon icon)
        {
            var l = new EntityLabelExtCombo(_state, entity, _systemId!);
            l.Padding = 3;

            var interactables = new List<InteractableState> { new(l) };
            if (icon is IInteractable interactable)
                interactables.Add(new InteractableState(interactable));

            _interactable.TryAdd(entity.Id, interactables.ToArray());
            _entityIcons.TryAdd(entity.Id, icon);
            _allLabels.Add(l);
        }

        void AddIconable(EntitySnapshot entity)
        {
            if (_systemId == null)
                return;

            var position = new SnapshotPosition(_state, _systemId, entity.Id);
            var bodyType = UserOrbitSettings.FromBodyKind(entity.Kind);
            var massVolume = entity.GetView<MassVolumeView>();

            AddMoveOverlays(entity, position, bodyType);

            if (entity.GetView<StarView>() is { } star && massVolume != null)
            {
                var starIcon = new StarIcon(star, massVolume, position);
                var system = _state.GameClient?.Galaxy.GetSystem(_systemId!);
                if (system != null)
                    starIcon.BindTexture(entity, system);
                AddEntityIcon(entity, starIcon);
            }

            if (entity.HasView<BodyView>() && entity.Kind != BodyKind.Star && massVolume != null)
            {
                var i = new SysBodyIcon(entity, _systemId, position, Distance.MToAU(massVolume.RadiusMetres));
                i.AttachState(_state);
                var system = _state.GameClient?.Galaxy.GetSystem(_systemId!);
                if (system != null)
                    i.BindTexture(entity, system);

                var l = new EntityLabelExtCombo(_state, entity, _systemId);
                l.Padding = 3;

                _interactable.TryAdd(
                        entity.Id,
                        new[] { new InteractableState(i), new InteractableState(l) });
                _bodyIcons.TryAdd(entity.Id, i);
                _allLabels.Add(l);
            }

            if (entity.HasView<ShipView>() && entity.HasView<PositionView>())
            {
                AddEntityIcon(entity, new ShipIcon(position, entity, _state, _systemId!));
            }

            if (entity.HasView<ProjectileView>() && entity.HasView<PositionView>())
            {
                AddEntityIcon(entity, new ProjectileIcon(position, underThrust: entity.HasView<NewtonMoveView>()));
            }

            if (entity.GetView<BeamView>() is { } beam)
            {
                _entityIcons.TryAdd(entity.Id, new BeamIcon(beam, position));
            }

            if (entity.HasView<JumpPointView>() && entity.HasView<PositionView>())
            {
                AddEntityIcon(entity, PointOfInterestIcon.ForJumpPoint(position));
            }
            else if (entity.HasView<GravSurveyView>() && entity.HasView<PositionView>())
            {
                AddEntityIcon(entity, new PointOfInterestIcon(position));
            }
        }

        void RemoveIconable(int entityGuid)
        {
            _testIcons.TryRemove(entityGuid, out _);
            _entityIcons.TryRemove(entityGuid, out _);
            _orbitRings.TryRemove(entityGuid, out _);
            _moveIcons.TryRemove(entityGuid, out _);
            _interactable.TryRemove(entityGuid, out _);
            _bodyIcons.TryRemove(entityGuid, out _);

            // Dispose label textures on the UI thread before dropping references.
            foreach (var label in _allLabels)
            {
                if (label.EntityId == entityGuid)
                    label.DisposeTextures();
            }
            _allLabels.RemoveWhere(x => x.EntityId == entityGuid);
        }

        void AddMoveOverlays(EntitySnapshot entity, IPosition position, UserOrbitSettings.OrbitBodyType bodyType)
        {
            if (_systemId == null)
                return;

            var orbit = entity.GetView<OrbitView>();
            if (orbit != null && orbit.SemiMajorAxisM > 0 && orbit.StandardGravParameter > 0)
            {
                IPosition parentPosition = orbit.ParentId is int parentId
                    ? new SnapshotPosition(_state, _systemId, parentId)
                    : position;
                if (orbit.Eccentricity < 1)
                    _orbitRings.TryAdd(entity.Id,
                        new OrbitEllipseIcon(orbit, position, parentPosition, bodyType, _state.UserOrbitSettingsMtx));
                else if (orbit.ParentSoiRadiusM > 0)
                    _orbitRings.TryAdd(entity.Id,
                        new OrbitHyperbolicIcon2(orbit, position, parentPosition, bodyType, _state.UserOrbitSettingsMtx));
            }

            if (entity.GetView<NewtonMoveView>() is { } newton && newton.SoiParentId is int newtonParentId)
            {
                _orbitRings.TryAdd(entity.Id, new NewtonMoveIcon(
                    newton, position, new SnapshotPosition(_state, _systemId, newtonParentId),
                    bodyType, _state.UserOrbitSettingsMtx));
            }

            if (entity.GetView<NewtonSimpleMoveView>() is { } newtonSimple && newtonSimple.SoiParentId is int simpleParentId)
            {
                var time = _state.SimTimeForSystem(_systemId);
                _orbitRings.TryAdd(entity.Id, new NewtonSimpleIcon(
                    newtonSimple, position, new SnapshotPosition(_state, _systemId, simpleParentId),
                    bodyType, _state.UserOrbitSettingsMtx, time));
            }

            if (entity.GetView<WarpMovingView>() is { } warp)
            {
                IPosition? targetPosition = warp.TargetEntityId is int targetId
                    ? new SnapshotPosition(_state, _systemId, targetId)
                    : null;
                _orbitRings.TryAdd(entity.Id, new WarpMovingIcon(warp, position, targetPosition));
            }
        }

        void ReplaceMoveOverlays(EntitySnapshot entity)
        {
            if (_systemId == null)
                return;

            _orbitRings.TryRemove(entity.Id, out _);
            _moveIcons.TryRemove(entity.Id, out _);

            var position = new SnapshotPosition(_state, _systemId, entity.Id);
            var bodyType = UserOrbitSettings.FromBodyKind(entity.Kind);
            AddMoveOverlays(entity, position, bodyType);

            if (entity.GetView<BeamView>() is { } beam)
                _entityIcons[entity.Id] = new BeamIcon(beam, position);
        }

        static bool SameVisualShell(EntitySnapshot a, EntitySnapshot b)
        {
            if (a.Kind != b.Kind)
                return false;
            if (a.HasView<ShipView>() != b.HasView<ShipView>())
                return false;
            if (a.HasView<BodyView>() != b.HasView<BodyView>())
                return false;
            if (a.HasView<StarView>() != b.HasView<StarView>())
                return false;
            if (a.HasView<BeamView>() != b.HasView<BeamView>())
                return false;
            if (a.HasView<JumpPointView>() != b.HasView<JumpPointView>())
                return false;
            if (a.HasView<ProjectileView>() != b.HasView<ProjectileView>())
                return false;
            return true;
        }

        static int MoveOverlayKey(EntitySnapshot e)
        {
            if (e.GetView<WarpMovingView>() is { } w)
                return HashCode.Combine(1, w.TargetEntityId, w.EntryPointAbsolute, w.ExitPointAbsolute);
            if (e.GetView<NewtonSimpleMoveView>() is { CurrentTrajectory: { } nsTraj } ns)
                return HashCode.Combine(2, ns.SoiParentId, nsTraj.SemiMajorAxisM);
            if (e.GetView<NewtonMoveView>() is { Trajectory: { } nTraj } n)
                return HashCode.Combine(3, n.SoiParentId, nTraj.SemiMajorAxisM);
            if (e.GetView<OrbitView>() is { } o)
                return HashCode.Combine(4, o.ParentId, o.Eccentricity < 1);
            if (e.GetView<BeamView>() is { } b)
                return HashCode.Combine(5, b.StartPosition, b.EndPosition);
            return 0;
        }

        /// <summary>The entity's orbit-ring icon, for screen-space hit testing (maneuver-node
        /// placement); null when the entity has no orbit ring.</summary>
        internal OrbitIconBase? GetOrbitIcon(int entityId)
            => _orbitRings.TryGetValue(entityId, out var icon) ? icon as OrbitIconBase : null;

        public void UpdateUserOrbitSettings()
        {
            foreach (var item in _orbitRings.Values)
            {
                if (item is IUpdateUserSettings foo)
                {
                    foo.UpdateUserSettings();
                }
            }
        }

        /// <summary>Reconciles icons with current snapshots. Unchanged visual shells keep their
        /// sprites/labels; only movement overlays rebuild when warp/newton/orbit identity changes.</summary>
        void SyncIcons()
        {
            try
            {
                SyncIconsCore();
            }
            catch (Exception ex)
            {
                DebugTraceLog.Error("UI", "SyncIcons: " + ex);
            }
        }

        void SyncIconsCore()
        {
            var system = _systemId != null ? _state.GameClient?.Galaxy.GetSystem(_systemId) : null;
            if (system == null)
                return;

            bool changed = false;
            _syncSeen.Clear();
            foreach (var entity in system.Entities)
            {
                _syncSeen.Add(entity.Id);
                if (_iconedSnapshots.TryGetValue(entity.Id, out var iconed))
                {
                    if (ReferenceEquals(iconed, entity))
                        continue;

                    if (SameVisualShell(iconed, entity))
                    {
                        if (MoveOverlayKey(iconed) != MoveOverlayKey(entity))
                            ReplaceMoveOverlays(entity);

                        if (_bodyIcons.TryGetValue(entity.Id, out var bodyIcon)
                            && bodyIcon is SysBodyIcon sysBody
                            && entity.HasView<BodyView>()
                            && entity.Kind != BodyKind.Star)
                        {
                            sysBody.BindTexture(entity, system);
                        }

                        _iconedSnapshots[entity.Id] = entity;
                        continue;
                    }

                    RemoveIconable(entity.Id);
                }

                _iconedSnapshots[entity.Id] = entity;
                AddIconable(entity);
                changed = true;
            }

            _syncGone.Clear();
            foreach (var entityId in _iconedSnapshots.Keys)
            {
                if (!_syncSeen.Contains(entityId))
                    _syncGone.Add(entityId);
            }
            foreach (var entityId in _syncGone)
            {
                RemoveIconable(entityId);
                _iconedSnapshots.Remove(entityId);
                changed = true;
            }

            if (changed)
                _updateLabels = true;
        }

        internal void Update()
        {
            if (_systemId == null) return;

            BodyMapTextureCache.PumpUploads();
            SyncIcons();

            // Advance icon physics when the global tick clock moves (Aurora increments).
            var systemTime = _state.SimTimeForSystem(_systemId);
            if (systemTime != default && systemTime != _lastPhysicsTime)
            {
                _lastPhysicsTime = systemTime;
                RunPhysicsUpdate();
            }

            var matrix = _camera.GetZoomMatrix();
            foreach (var (_, item) in UIWidgets)
                item.OnFrameUpdate(matrix, _camera);

            foreach (var (_, item) in _orbitRings)
                item.OnFrameUpdate(matrix, _camera);

            foreach (var (_, item) in _moveIcons)
                item.OnFrameUpdate(matrix, _camera);

            foreach (var (_, item) in _entityIcons)
                item.OnFrameUpdate(matrix, _camera);

            foreach (var (_, item) in _bodyIcons)
                item.OnFrameUpdate(matrix, _camera);

            foreach (var item in SelectedEntityExtras)
                item.OnFrameUpdate(matrix, _camera);

            foreach (var item in _allLabels)
                item.OnFrameUpdate(matrix, _camera);

            if (_updateLabels)
            {
                _updateLabels = false;

                var prefs = SystemViewPreferences.GetInstance();

                foreach (var item in _interactable.Values)
                {
                    foreach (var i in item)
                        i.IsDisabled = true;
                }

                var zoom = _camera.ZoomLevel;
                var lbl = _allLabels
                    .Where(x => prefs.ShouldDisplay("map", x.BodyType)
                        && zoom >= _minZoomForLabel[x.BodyType]);

                _visibleLabels.Clear();
                foreach (var i in _distributor(lbl))
                {
                    if (!_interactable.TryGetValue(i.EntityId, out var states))
                        continue;
                    foreach (var j in states)
                        j.IsDisabled = false;
                    _visibleLabels.Add(i);
                }

                // Ship sprites stay clickable even when name labels are culled by zoom / prefs.
                foreach (var (entityId, icon) in _entityIcons)
                {
                    if (icon is not ShipIcon)
                        continue;
                    if (!_interactable.TryGetValue(entityId, out var states))
                        continue;
                    foreach (var j in states)
                    {
                        if (j.Item is ShipIcon)
                            j.IsDisabled = false;
                    }
                }

                _interactableGrouped = _interactable
                    .Values
                    .SelectMany(x => x)
                    .GroupBy(x => x.Item.Priority)
                    .OrderByDescending(x => x.Key);
            }
        }

        void RunPhysicsUpdate()
        {
            foreach (var icon in UIWidgets.Values)
            {
                icon.OnPhysicsUpdate();
            }
            foreach (var icon in _orbitRings.Values)
            {
                icon.OnPhysicsUpdate();
            }
            foreach (var icon in _entityIcons.Values)
            {
                icon.OnPhysicsUpdate();
            }
            foreach (var icon in _moveIcons.Values.ToArray())
            {
                icon.OnPhysicsUpdate();
            }
            foreach (var icon in SelectedEntityExtras)
            {
                icon.OnPhysicsUpdate();
            }
        }

        internal void Draw()
        {
            var vp = _camera.ViewPortSize;
            _starfield.Draw(_window.Renderer, _camera, (int)vp.X, (int)vp.Y);

            DrawIcons(UIWidgets.Values);
            DrawIcons(_orbitRings.Values);
            DrawIcons(_moveIcons.Values);
            DrawIcons(_entityIcons.Values);
            DrawIcons(_bodyIcons.Values);
            DrawIcons(SelectedEntityExtras);

            foreach (var i in _visibleLabels)
                i.Draw(_window.Renderer, _camera);
        }

        void DrawIcons(IEnumerable<IDrawData> icons)
        {
            foreach (var item in icons)
                item.Draw(_window.Renderer, _camera);
        }

        public override bool GetActive()
        {
            return true;
        }

        public override void OnSystemTickChange(DateTime newDate)
        {
            // Keep order-preview clock aligned with the live system sim clock.
            _state.PrimarySystemDateTime = newDate;
            if (newDate != default && newDate != _lastPhysicsTime)
            {
                _lastPhysicsTime = newDate;
                RunPhysicsUpdate();
            }
        }
    }
}
