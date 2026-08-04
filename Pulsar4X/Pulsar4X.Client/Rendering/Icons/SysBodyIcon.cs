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
        int _texW = 256;
        int _texH = 256;
        bool _extremeHeatRing;
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
            _glowR = visual.GlowColor.R;
            _glowG = visual.GlowColor.G;
            _glowB = visual.GlowColor.B;

            var (tex, w, h) = BodyMapTextureCache.GetOrCreate(visual);
            if (tex == IntPtr.Zero)
                return;
            _texture = tex;
            _texW = w;
            _texH = h;
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
            return System.Numerics.Vector2.Distance(v, point.ToVector2()) <= Scale * 100;
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            var entity = _state?.GameClient?.Galaxy.GetSystem(_sysId)?.GetEntity(_entityId);
            if (entity != null)
                RefreshStatusFlags(entity);

            // Texture rebinding happens in SystemMapRendering.AddIconable when the snapshot
            // changes after a survey completes — do not compose here (UI-thread race / crash risk).

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
            int bodyRadius = Math.Max(2, (int)(display * 0.38f));

            if (_extremeHeatRing)
                ExtremeHeatRingDrawer.Draw(rendererPtr, ViewScreenPos.X, ViewScreenPos.Y, bodyRadius, _glowR, _glowG, _glowB);

            if (_texture != IntPtr.Zero)
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

            DrawStatusRings(rendererPtr, Math.Max(2, (int)(Scale * 100)));
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
