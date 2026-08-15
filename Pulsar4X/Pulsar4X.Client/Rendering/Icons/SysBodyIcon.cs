using System;
using System.Linq;
using Pulsar4X.Api;
using Pulsar4X.Client.BodyVisuals;
using Pulsar4X.Orbital;
using SDL3;
using Pulsar4X.Input;

namespace Pulsar4X.Client
{
    class SysBodyIcon : Icon, IPointerHandler, IShape, IInteractable
    {
        BodyKind _bodyType;
        double _bodyRadiusAU;
        float _viewRadius;
        float _iconMinSize = 8;
        int _entityId;
        string _sysId;
        bool _surveyComplete;
        bool _infrastructureComplete;
        IntPtr _texture = IntPtr.Zero;
        IntPtr _albedo = IntPtr.Zero;
        IntPtr _clouds = IntPtr.Zero;
        int _texW = 256;
        int _texH = 256;
        bool _extremeHeatRing;
        float _diskFrac = 0.42f;
        BodyVisualState? _pendingVisual;
        BodyVisualState? _visual;
        bool _liveGlobe;
        byte _glowR = 255;
        byte _glowG = 160;
        byte _glowB = 40;

        static readonly SDL.Color SurveyRingColor = new() { R = 64, G = 220, B = 80, A = 230 };
        static readonly SDL.Color InfrastructureRingColor = new() { R = 180, G = 90, B = 230, A = 230 };

        public byte Priority { get { return 100; } }

        public SysBodyIcon(EntitySnapshot entity, string systemId, IPosition position, double bodyRadiusAU)
            : base(position)
        {
            _bodyType = entity.Kind;
            _bodyRadiusAU = bodyRadiusAU;
            _entityId = entity.Id;
            _sysId = systemId;
            RefreshStatusFlags(entity);

            if (_bodyType == BodyKind.Moon)
                _iconMinSize = 4;
            else if (_bodyType == BodyKind.Asteroid || _bodyType == BodyKind.Comet)
                _iconMinSize = 5;

            // Shape fallback for hit-testing / if texture fails
            short segments = 24;
            var points = CreatePrimitiveShapes.Circle(0, 0, 100, segments);
            Shapes.Add(new Shape
            {
                Color = new SDL.Color { R = 100, G = 100, B = 100, A = 255 },
                Points = points
            });
        }

        public void BindTexture(EntitySnapshot entity, IClientSystem system)
        {
            var visual = BodyVisualStateFactory.FromEntity(entity, system);
            _extremeHeatRing = visual.ExtremeHeatRing;
            _diskFrac = BodyVisualComposer.TextureDiskFraction(visual);
            _glowR = visual.GlowColor.R;
            _glowG = visual.GlowColor.G;
            _glowB = visual.GlowColor.B;
            _visual = visual;
            _liveGlobe = BodyVisualComposer.UsesLiveGlobe(visual, _bodyType);
            _pendingVisual = visual;
            TryApplyTexture();
        }

        void TryApplyTexture()
        {
            var visual = _pendingVisual ?? _visual;
            if (visual == null)
                return;

            if (_liveGlobe)
            {
                var (albedo, aw, ah) = BodyMapTextureCache.GetOrCreate(visual, BodyTextureLayer.Albedo);
                if (albedo == IntPtr.Zero)
                    return;
                _albedo = albedo;
                _texW = aw;
                _texH = ah;

                if (BodyVisualComposer.UsesCloudLayer(visual))
                {
                    var (clouds, _, _) = BodyMapTextureCache.GetOrCreate(visual, BodyTextureLayer.Clouds);
                    _clouds = clouds;
                }
                else
                    _clouds = IntPtr.Zero;

                if (!BodyVisualComposer.UsesCloudLayer(visual) || _clouds != IntPtr.Zero)
                    _pendingVisual = null;
                return;
            }

            var (tex, w, h) = BodyMapTextureCache.GetOrCreate(visual, BodyTextureLayer.Disk);
            if (tex == IntPtr.Zero)
                return;
            _texture = tex;
            _texW = w;
            _texH = h;
            _pendingVisual = null;
        }

        public bool OnPointerUp(SDL.Event sevent)
        {
            if (_state == null)
                return false;
            var state = _state!;

            if (sevent.Button.Button == 1)
                state.EntityClicked(_entityId, _sysId, MouseButtons.Primary);
            else if (sevent.Button.Button == 3)
            {
                state.EntityClicked(_entityId, _sysId, MouseButtons.Alt);
                state.PendingContextMenuEntityId = _entityId;
            }
            return true;
        }

        public bool Contains(System.Drawing.PointF point)
        {
            System.Numerics.Vector2 v = new(ViewScreenPos.X, ViewScreenPos.Y);
            float hit = Math.Max(_iconMinSize, Scale * 100f * 2.2f * _diskFrac);
            return System.Numerics.Vector2.Distance(v, point.ToVector2()) <= hit;
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            var entity = _state?.GameClient?.Galaxy.GetSystem(_sysId)?.GetEntity(_entityId);
            if (entity != null)
                RefreshStatusFlags(entity);

            if (_pendingVisual != null
                || (_liveGlobe && _visual != null && _albedo != IntPtr.Zero
                    && BodyVisualComposer.UsesCloudLayer(_visual) && _clouds == IntPtr.Zero))
                TryApplyTexture();

            _viewRadius = camera.ViewDistance(_bodyRadiusAU);
            if (_viewRadius < _iconMinSize)
                Scale = _iconMinSize * 0.01f;
            else
                Scale = _viewRadius * 0.01f;
            base.OnFrameUpdate(matrix, camera);
        }

        public override void Draw(IntPtr rendererPtr, Camera camera)
        {
            float display = Math.Max(_iconMinSize * 2f, Scale * 100f * 2.2f);
            int bodyRadius = Math.Max(2, (int)(display * _diskFrac));

            if (_extremeHeatRing)
                ExtremeHeatRingDrawer.Draw(rendererPtr, ViewScreenPos.X, ViewScreenPos.Y, bodyRadius, _glowR, _glowG, _glowB);

            if (_liveGlobe && _visual != null && _albedo != IntPtr.Zero)
            {
                DrawLiveGlobe(rendererPtr, camera, bodyRadius);
            }
            else if (_texture != IntPtr.Zero)
            {
                float aspect = _texH > 0 ? (float)_texW / _texH : 1f;
                float drawW = display;
                float drawH = display;
                if (aspect >= 1f)
                    drawH = display / aspect;
                else
                    drawW = display * aspect;

                var dstRect = new SDL.FRect
                {
                    X = ViewScreenPos.X - drawW / 2f,
                    Y = ViewScreenPos.Y - drawH / 2f,
                    W = drawW,
                    H = drawH
                };
                SDL.RenderTexture(rendererPtr, _texture, IntPtr.Zero, in dstRect);
            }
            else if (DrawShapes != null && DrawShapes.Length > 0)
            {
                int cx = ViewScreenPos.X;
                int cy = ViewScreenPos.Y;
                int radius = Math.Max(2, (int)(Scale * 100));
                SDL.SetRenderDrawColor(rendererPtr, 80, 120, 140, 255);
                for (int y = -radius; y <= radius; y++)
                {
                    int xSpan = (int)Math.Sqrt(radius * radius - y * y);
                    SDL.RenderLine(rendererPtr, cx - xSpan, cy + y, cx + xSpan, cy + y);
                }
            }

            DrawStatusRings(rendererPtr, bodyRadius);
        }

        void DrawLiveGlobe(IntPtr rendererPtr, Camera camera, int bodyRadius)
        {
            var system = _state?.GameClient?.Galaxy.GetSystem(_sysId);
            var entity = system?.GetEntity(_entityId);
            var visual = _visual!;

            var light = BodyGlobeDrawer.LightDir.FromScreen(
                ViewScreenPos.X, ViewScreenPos.Y,
                ViewScreenPos.X + 40, ViewScreenPos.Y - 12);
            if (system != null && entity != null)
            {
                var star = BodyVisualStateFactory.FindPrimaryStarEntity(entity, system);
                if (star?.GetView<PositionView>() is { } starPos)
                {
                    var starScreen = camera.ViewCoordinate_m(new Vector3(starPos.AbsolutePosition.X, starPos.AbsolutePosition.Y, starPos.AbsolutePosition.Z));
                    light = BodyGlobeDrawer.LightDir.FromScreen(
                        ViewScreenPos.X, ViewScreenPos.Y, starScreen.X, starScreen.Y);
                }
            }

            bool fogOfWar = entity?.GetView<GeoSurveyView>() is { IsSurveyComplete: false };
            TimeSpan day = entity?.GetView<BodyView>()?.DayLength ?? TimeSpan.Zero;
            DateTime sim = _state != null ? _state.SimTimeForSystem(_sysId) : default;
            double spin = BodyGlobeDrawer.SurfaceSpinRadians(sim, day, visual.Seed, allowSpin: !fogOfWar);
            double cloudSpin = BodyGlobeDrawer.CloudSpinRadians(spin);

            BodyGlobeDrawer.Draw(
                rendererPtr,
                ViewScreenPos.X,
                ViewScreenPos.Y,
                bodyRadius,
                _albedo,
                _clouds,
                visual,
                light,
                spin,
                cloudSpin);
        }

        void RefreshStatusFlags(EntitySnapshot entity)
        {
            _surveyComplete = entity.GetView<GeoSurveyView>()?.IsSurveyComplete == true;
            _infrastructureComplete = false;

            var system = _state?.GameClient?.Galaxy.GetSystem(_sysId);
            if (system == null)
                return;

            var colony = system.Entities.FirstOrDefault(e =>
                e.Kind == BodyKind.Colony
                && e.Relation == OwnerRelation.Owned
                && e.GetView<ColonyView>()?.PlanetEntityId == entity.Id);

            _infrastructureComplete = colony?.GetView<InfrastructureView>()?.HasInstalledInfrastructure == true;
        }

        void DrawStatusRings(IntPtr rendererPtr, int bodyRadius)
        {
            if (!_surveyComplete && !_infrastructureComplete)
                return;

            int cx = ViewScreenPos.X;
            int cy = ViewScreenPos.Y;
            int surveyOffset = _extremeHeatRing ? 22 : 3;
            int infraOffset = _extremeHeatRing ? 28 : 7;

            if (_surveyComplete)
                DrawThickRing(rendererPtr, cx, cy, bodyRadius + surveyOffset, SurveyRingColor);

            if (_infrastructureComplete)
                DrawThickRing(rendererPtr, cx, cy, bodyRadius + infraOffset, InfrastructureRingColor);
        }

        static void DrawThickRing(IntPtr rendererPtr, int cx, int cy, int radius, SDL.Color color)
        {
            SDL.SetRenderDrawColor(rendererPtr, color.R, color.G, color.B, color.A);
            for (int offset = 0; offset < 2; offset++)
            {
                int r = radius + offset;
                DrawPrimitive.DrawEllipse(rendererPtr, cx, cy, r, r);
            }
        }
    }
}
