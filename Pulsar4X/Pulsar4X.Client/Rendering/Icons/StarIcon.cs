using System;
using Pulsar4X.Api;
using Pulsar4X.Client.BodyVisuals;
using Pulsar4X.Orbital;
using SDL3;

namespace Pulsar4X.Client
{
    class StarIcon : Icon
    {
        float _iconMinSize = 16;
        double _bodyRadiusAU;
        IntPtr _texture = IntPtr.Zero;
        int _texW = 256;
        int _texH = 256;
        bool _extremeHeatRing = true;
        byte _glowR = 255;
        byte _glowG = 240;
        byte _glowB = 180;
        BodyVisualState? _pendingVisual;
        BodyVisualState? _visual;

        public StarIcon(StarView star, MassVolumeView massVolume, IPosition position) : base(position)
        {
            _bodyRadiusAU = Distance.MToAU(massVolume.RadiusMetres);
            // Minimal shape for legacy path
            Shapes.Add(new Shape
            {
                Color = new SDL.Color { R = 255, G = 200, B = 80, A = 255 },
                Points = CreatePrimitiveShapes.Circle(0, 0, 100, 16)
            });
        }

        public void BindTexture(EntitySnapshot entity, IClientSystem system)
        {
            var visual = BodyVisualStateFactory.FromEntity(entity, system);
            _visual = visual;
            _extremeHeatRing = visual.ExtremeHeatRing || visual.Type == BodyVisualType.Star;
            _glowR = visual.GlowColor.R;
            _glowG = visual.GlowColor.G;
            _glowB = visual.GlowColor.B;
            _pendingVisual = visual.Type == BodyVisualType.Star ? null : visual;
            if (_pendingVisual != null)
                TryApplyTexture();
        }

        void TryApplyTexture()
        {
            if (_pendingVisual == null)
                return;
            var (tex, w, h) = BodyMapTextureCache.GetOrCreate(_pendingVisual);
            if (tex == IntPtr.Zero)
                return;
            _texture = tex;
            _texW = w;
            _texH = h;
            _pendingVisual = null;
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            if (_pendingVisual != null)
                TryApplyTexture();

            var viewRadius = camera.ViewDistance(_bodyRadiusAU);
            if (viewRadius < _iconMinSize)
                Scale = _iconMinSize * 0.01f;
            else
                Scale = viewRadius * 0.01f;
            base.OnFrameUpdate(matrix, camera);
        }

        public override void Draw(IntPtr rendererPtr, Camera camera)
        {
            float display = Math.Max(_iconMinSize * 2.5f, Scale * 100f * 2.8f);
            // Match the drawn star disk (~42% of texture half-size), not fixed screen px.
            int bodyRadius = Math.Max(2, (int)(display * 0.38f));

            if (_extremeHeatRing)
                ExtremeHeatRingDrawer.Draw(rendererPtr, ViewScreenPos.X, ViewScreenPos.Y, bodyRadius, _glowR, _glowG, _glowB);

            if (_visual is { Type: BodyVisualType.Star })
            {
                BodyGlobeDrawer.DrawStar(rendererPtr, ViewScreenPos.X, ViewScreenPos.Y, bodyRadius, _visual);
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
            else
            {
                base.Draw(rendererPtr, camera);
            }
        }
    }
}
