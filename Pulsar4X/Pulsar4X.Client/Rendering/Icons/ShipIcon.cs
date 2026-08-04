using System;
using System.IO;
using Pulsar4X.Api;
using Pulsar4X.Client.ShipVisuals;
using Pulsar4X.Orbital;
using SDL3;

namespace Pulsar4X.Client
{
    public class ShipIcon : Icon
    {
        // Legacy shared fallback texture (chevron / static PNG).
        private static IntPtr _fallbackTexture = IntPtr.Zero;
        private static int _fallbackWidth = 24;
        private static int _fallbackHeight = 12;
        private static bool _fallbackInitialized;

        private readonly GlobalUIState? _uiState;
        private readonly string? _systemId;
        private readonly int _entityId;
        private IntPtr _shipTexture = IntPtr.Zero;
        private int _textureWidth = 24;
        private int _textureHeight = 12;
        private bool _hasVelocityHeading;

        /// <summary>On-screen size of the generated ship sprite (pixels).</summary>
        public float DisplaySizePx { get; set; } = 42f;

        /// <summary>Initialize shared fallback texture + map visual cache. Call once at startup.</summary>
        public static void InitializeTexture(IntPtr renderer)
        {
            ShipMapTextureCache.Initialize(renderer);

            if (_fallbackInitialized) return;

            var path = Path.Combine(PulsarMainWindow.ResourcesPath, "ship-icons", "01.png");
            if (File.Exists(path))
            {
                _fallbackTexture = Image.LoadTexture(renderer, path);
                if (_fallbackTexture != IntPtr.Zero)
                {
                    SDL.GetTextureSize(_fallbackTexture, out float w, out float h);
                    _fallbackWidth = (int)w;
                    _fallbackHeight = (int)h;
                    _fallbackInitialized = true;
                }
            }
        }

        public ShipIcon(Vector3 position_m) : base(position_m)
        {
            BasicShape();
        }

        /// <summary>Snapshot constructor with entity context for generated visuals + heading.</summary>
        public ShipIcon(IPosition position, EntitySnapshot entity, GlobalUIState state, string systemId)
            : base(position)
        {
            _uiState = state;
            _systemId = systemId;
            _entityId = entity.Id;
            BasicShape();
            TryBindGeneratedTexture(entity);
        }

        /// <summary>Snapshot constructor without visuals (tests / legacy).</summary>
        public ShipIcon(IPosition position) : base(position)
        {
            BasicShape();
        }

        private void TryBindGeneratedTexture(EntitySnapshot entity)
        {
            var ship = entity.GetView<ShipView>();
            if (ship == null || _uiState == null)
                return;

            var mass = entity.GetView<MassVolumeView>();
            var thrust = entity.GetView<ThrustView>();
            var (tex, w, h) = ShipMapTextureCache.GetOrCreate(ship, mass, thrust);
            if (tex == IntPtr.Zero)
                return;

            _shipTexture = tex;
            _textureWidth = w;
            _textureHeight = h;
        }

        void BasicShape()
        {
            byte r = 50;
            byte g = 50;
            byte b = 200;
            byte a = 255;
            Orbital.Vector2[] points = {
            new Orbital.Vector2() { X = 0, Y = 5 },
            new Orbital.Vector2() { X = 5, Y = -5 },
            new Orbital.Vector2() { X = 0, Y = 0 },
            new Orbital.Vector2() { X = -5, Y = -5 },
            new Orbital.Vector2() { X = 0, Y = 5 }
            };

            SDL.Color colour = new SDL.Color() { R = r, G = g, B = b, A = a };
            Shapes.Add(new Shape() { Points = points, Color = colour });
        }

        public override void OnPhysicsUpdate()
        {
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            UpdateHeadingFromMotion();

            var mirrorMatrix = Matrix.IDMirror(true, false);
            var scaleMatrix = Matrix.IDScale(Scale, Scale);
            var rotateMatrix = Matrix.IDRotate(Heading - Math.PI * 0.5);//because the icons were done facing up, but angles are referenced from the right

            var shipMatrix = mirrorMatrix * scaleMatrix * rotateMatrix;

            ViewScreenPos = camera.ViewCoordinate_m(WorldPosition_m);

            DrawShapes = new Shape[this.Shapes.Count];
            for (int i = 0; i < Shapes.Count; i++)
            {
                var shape = Shapes[i];
                Vector2[] drawPoints = new Vector2[shape.Points.Length];
                for (int i2 = 0; i2 < shape.Points.Length; i2++)
                {
                    var tranlsatedPoint = shipMatrix.TransformD(shape.Points[i2].X, shape.Points[i2].Y);
                    int x = (int)(ViewScreenPos.X + tranlsatedPoint.X);
                    int y = (int)(ViewScreenPos.Y + tranlsatedPoint.Y);
                    drawPoints[i2] = new Vector2() { X = x, Y = y };
                }
                DrawShapes[i] = new Shape() { Points = drawPoints, Color = shape.Color };
            }
        }

        private void UpdateHeadingFromMotion()
        {
            if (_uiState == null || string.IsNullOrEmpty(_systemId))
                return;

            var galaxy = _uiState.GameClient?.Galaxy;
            var system = galaxy?.GetSystem(_systemId);
            var entity = system?.GetEntity(_entityId);
            if (galaxy == null || system == null || entity == null)
                return;

            DateTime now = galaxy.Time.GameDateTime;
            Vector3 vel = Vector3.Zero;

            if (entity.GetView<WarpMovingView>() is { } warp)
            {
                // Travel direction along the warp chord.
                vel = new Vector3(
                    warp.ExitPointAbsolute.X - warp.EntryPointAbsolute.X,
                    warp.ExitPointAbsolute.Y - warp.EntryPointAbsolute.Y,
                    0);
                if (vel.Length() < 1e-3)
                {
                    var pos = entity.GetView<PositionView>();
                    if (pos != null)
                    {
                        vel = new Vector3(
                            warp.ExitPointAbsolute.X - pos.AbsolutePosition.X,
                            warp.ExitPointAbsolute.Y - pos.AbsolutePosition.Y,
                            0);
                    }
                }
            }
            else if (entity.GetView<NewtonMoveView>() is { } newton)
            {
                vel = new Vector3(newton.CurrentVectorMps.X, newton.CurrentVectorMps.Y, newton.CurrentVectorMps.Z);
                if (vel.Length() < 1e-6)
                    vel = entity.GetRelativeState(now).vel;
            }
            else
            {
                vel = entity.GetRelativeState(now).vel;
            }

            double speed = vel.Length();
            if (speed > 1e-3)
            {
                Heading = (float)Math.Atan2(vel.Y, vel.X);
                _hasVelocityHeading = true;
            }
            // else keep last heading so parked ships don't spin / snap to 0
        }

        public override void Draw(IntPtr rendererPtr, Camera camera)
        {
            IntPtr texture = _shipTexture != IntPtr.Zero ? _shipTexture : _fallbackTexture;
            int texW = _shipTexture != IntPtr.Zero ? _textureWidth : _fallbackWidth;
            int texH = _shipTexture != IntPtr.Zero ? _textureHeight : _fallbackHeight;

            if (texture != IntPtr.Zero)
            {
                float display = DisplaySizePx * Scale;
                // Keep aspect ratio of the source sprite.
                float aspect = texH > 0 ? (float)texW / texH : 1f;
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

                // Generated / PNG sprites face "nose up". Camera: +worldY → screen up.
                // Math heading 0 = +X; SDL angle is clockwise degrees from the texture's up.
                // Rotate by (90° − heading°) so the nose tracks flight direction.
                double angleDegrees = _hasVelocityHeading || _shipTexture != IntPtr.Zero
                    ? Angle.ToDegrees(Math.PI * 0.5 - Heading)
                    : 0;

                SDL.RenderTextureRotated(
                    rendererPtr,
                    texture,
                    IntPtr.Zero,
                    in dstRect,
                    angleDegrees,
                    IntPtr.Zero,
                    SDL.FlipMode.None
                );
            }
            else
            {
                base.Draw(rendererPtr, camera);
            }
        }
    }

    public class ProjectileIcon : Icon
    {
        private Shape _flame;

        public ProjectileIcon(Vector3 position_m) : base(position_m)
        {
        }

        /// <summary>Snapshot constructor: rebuilt on snapshot change, so no engine subscriptions.</summary>
        public ProjectileIcon(IPosition position, bool underThrust) : base(position)
        {
            BasicShape();
            NewtonFlame();
            if (underThrust)
                Shapes.Add(_flame);
        }



        void BasicShape()
        {
            byte r = 150;
            byte g = 50;
            byte b = 200;
            byte a = 255;
            Vector2[] points = {
                new Vector2 { X = 0, Y = 4 },
                new Vector2 { X = 2, Y = -4 },
                new Vector2 { X = 0, Y = 0 },
                new Vector2 { X = -2, Y = -4 },
                new Vector2 { X = 0, Y = 4 }
            };

            SDL.Color colour = new SDL.Color() { R = r, G = g, B = b, A = a };
            Shapes.Add(new Shape() { Points = points, Color = colour });
        }

        void NewtonFlame()
        {
            byte r = 150;
            byte g = 50;
            byte b = 0;
            byte a = 200;
            Vector2[] points = {
                new Vector2 { X = 0, Y = 0 },
                new Vector2 { X = -2, Y = -2 },
                new Vector2 { X = 0, Y = -5 },
                new Vector2 { X = 2, Y = -2 },
                new Vector2 { X = 0, Y = 0 }
            };

            SDL.Color colour = new SDL.Color() { R = r, G = g, B = b, A = a };
            _flame = new Shape() { Points = points, Color = colour };
        }

        public override void OnPhysicsUpdate()
        {
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {

            var mirrorMatrix = Matrix.IDMirror(true, false);
            var scaleMatrix = Matrix.IDScale(Scale, Scale);
            var rotateMatrix = Matrix.IDRotate(Heading - Math.PI * 0.5);//because the icons were done facing up, but angles are referenced from the right

            var shipMatrix = mirrorMatrix * scaleMatrix * rotateMatrix;

            ViewScreenPos = camera.ViewCoordinate_m(WorldPosition_m);

            DrawShapes = new Shape[this.Shapes.Count];
            for (int i = 0; i < Shapes.Count; i++)
            {
                var shape = Shapes[i];
                Vector2[] drawPoints = new Vector2[shape.Points.Length];
                for (int i2 = 0; i2 < shape.Points.Length; i2++)
                {
                    var tranlsatedPoint = shipMatrix.TransformD(shape.Points[i2].X, shape.Points[i2].Y);
                    int x = (int)(ViewScreenPos.X + tranlsatedPoint.X);
                    int y = (int)(ViewScreenPos.Y + tranlsatedPoint.Y);
                    drawPoints[i2] = new Vector2() { X = x, Y = y };
                }
                DrawShapes[i] = new Shape() { Points = drawPoints, Color = shape.Color };
            }
        }

    }


    public class BeamIcon : Icon
    {
        Vector3 _start;
        Vector3 _end;
        bool _hasEndpoints;

        /// <summary>Snapshot constructor: the endpoints travel in the BeamView and the icon is
        /// rebuilt on each per-tick push.</summary>
        public BeamIcon(Pulsar4X.Api.BeamView beam, IPosition position) : base(position)
        {
            _start = new Vector3(beam.StartPosition.X, beam.StartPosition.Y, beam.StartPosition.Z);
            _end = new Vector3(beam.EndPosition.X, beam.EndPosition.Y, beam.EndPosition.Z);
            _hasEndpoints = true;
        }

        public BeamIcon(Vector3 position_m) : base(position_m)
        {
        }

        public override void OnPhysicsUpdate()
        {
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            if (!_hasEndpoints) return;

            var p0 = camera.ViewCoordinate_m(_start);
            var p1 = camera.ViewCoordinate_m(_end);

            DrawShapes = new Shape[1];
            var s1 = new Shape();
            s1.Points = new Vector2[2];
            s1.Points[0] = new Vector2() { X = p0.X, Y = p0.Y };
            s1.Points[1] = new Vector2() { X = p1.X, Y = p1.Y };
            var clr = new SDL.Color()
            {
                R = 200,
                G = 0,
                B = 0,
                A = 255
            };
            s1.Color = clr;
            DrawShapes[0] = s1;
        }
    }
}
