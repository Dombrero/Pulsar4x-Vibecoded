using Pulsar4X.Api;
using Pulsar4X.Client.Interface;
using Pulsar4X.Input;
using Pulsar4X.Orbital;
using SDL3;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace Pulsar4X.Client
{
    public class EntityLabel : IPointerHandler, IShape, IInteractable
    {
        public int EntityId { get; }
        public string SystemId { get; }
        internal UserOrbitSettings.OrbitBodyType BodyType { get; }
        public string Name => _name;

        public byte Priority { get { return 120; } }

        protected readonly GlobalUIState _state;
        private readonly IPosition _position;
        private readonly double _radiusAU;
        private readonly bool _showOrders;

        protected virtual void DrawExt(IntPtr rendererPtr, Camera camera) {}
        protected virtual void OnFrameUpdateExt(Matrix matrix, Camera camera) {}

        private SDL.Color _color;
        private SDL.Color _orderColor;
        protected string _name = "??";

        private IntPtr _nameTexture = IntPtr.Zero;
        protected SDL.FRect _nameRect = new ();

        private string _orderText = "";
        private IntPtr _orderTexture = IntPtr.Zero;
        private SDL.FRect _orderRect = new ();
        private float _shipScreenX;
        private float _shipScreenY;

        public RectangleF Rect = new ();

        // Start of the diagonal leader (just outside the body) and the elbow
        // where the 45° leader meets the horizontal underline beneath the label.
        private float _lineStartX;
        private float _lineStartY;
        private float _elbowX;
        private float _elbowY;

        // Minimum perpendicular offset (px) of the elbow from the body center,
        // extra gap added on top of the on-screen body radius so the leader
        // clears the body when zoomed in, and the small visible gap between
        // the body edge and the start of the leader.
        private const float MinLeaderOffset = 12f;
        private const float BodyEdgeGap = 18f;
        private const float BodyLineGap = 8f;
        private const float OrderAboveShipGap = 14f;

        private uint _padding = 0;
        public uint Padding {
            set {
                _padding = value;
                OnPaddingUpdate();
            }
            get {
                return _padding;
            }
        }

        private void OnPaddingUpdate()
        {
            Rect.Width = _nameRect.W + _padding * 2;
            Rect.Height = _nameRect.H + _padding * 2;
        }

        public EntityLabel(GlobalUIState state, EntitySnapshot entity, string systemId)
        {
            _state = state;
            EntityId = entity.Id;
            SystemId = systemId;
            BodyType = UserOrbitSettings.FromBodyKind(entity.Kind);
            _name = entity.GetView<NameView>()?.Name ?? "Unknown";
            _position = new SnapshotPosition(state, systemId, entity.Id);
            _radiusAU = entity.GetView<MassVolumeView>() is { } massVolume
                ? Distance.MToAU(massVolume.RadiusMetres)
                : 0;
            _showOrders = entity.HasView<ShipView>();

            _color = entity.Relation switch
            {
                OwnerRelation.Neutral => Styles.NeutralColor.ToSDLColor(),
                OwnerRelation.Owned or OwnerRelation.Friendly => Styles.Theme.Text.ToSDLColor(),
                _ => Styles.BadColor.ToSDLColor(),
            };
            _orderColor = new SDL.Color { R = _color.R, G = _color.G, B = _color.B, A = 200 };

            if (Styles.SDLDefaultFont != IntPtr.Zero)
            {
                _nameRect.H = SDL3.TTF.GetFontHeight(Styles.SDLDefaultFont);
                if (!string.IsNullOrEmpty(_name))
                {
                    SDL3.TTF.GetStringSize(Styles.SDLDefaultFont, _name, 0, out int w, out _);
                    _nameRect.W = w;
                }
            }

            OnPaddingUpdate();
        }

        /// <summary>
        /// Releases SDL textures on the UI thread. Do not destroy textures from a finalizer —
        /// GC runs off-thread and <see cref="SDL.DestroyTexture"/> then crashes (seen after
        /// geo-survey icon rebuilds).
        /// </summary>
        public void DisposeTextures()
        {
            DestroyName();
            DestroyOrder();
        }

        private void DestroyName()
        {
            if (_nameTexture == IntPtr.Zero)
                return;

            var p = _nameTexture;
            _nameTexture = IntPtr.Zero;
            SDL.DestroyTexture(p);
        }

        private void DestroyOrder()
        {
            if (_orderTexture == IntPtr.Zero)
                return;

            var p = _orderTexture;
            _orderTexture = IntPtr.Zero;
            SDL.DestroyTexture(p);
        }

        private bool _hovered = false;
        public virtual bool OnPointerEnter(SDL.Event sevent)
        {
            _hovered = true;
            return true;
        }
        public virtual bool OnPointerExit(SDL.Event sevent)
        {
            /* If pointer moves moves out of a label and then comes back while
             * the button is still pressed, then OnPointerUp does still fire
             * even though _pressed is false. It's kinda difficult to do that,
             * unless you're doing it on purpose. It doesn't break anything,
             * but the label doesn't change to the correct color.
             */
            _pressed = false;

            _hovered = false;
            return true;
        }

        private bool _pressed = false;
        public virtual bool OnPointerDown(SDL.Event sevent)
        {
            _pressed = true;
            return true;
        }
        public virtual bool OnPointerUp(SDL.Event sevent)
        {
            _pressed = false;

            if (sevent.Button.Button == 1)
                _state.EntityClicked(EntityId, SystemId, MouseButtons.Primary);
            else if (sevent.Button.Button == 3)
                _state.EntityClicked(EntityId, SystemId, MouseButtons.Alt);
            return true;
        }

        public virtual bool Contains(System.Drawing.PointF point)
        {
            return Rect.Contains(point);
        }

        public void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            var point = camera.ViewCoordinate_m(_position.AbsolutePosition);

            float anchorX = (float)point.X;
            float anchorY = (float)point.Y;
            _shipScreenX = anchorX;
            _shipScreenY = anchorY;

            // Diagonal distance from body center to the elbow must clear the
            // body's on-screen radius. The leader rises by `offset` in both X
            // and Y, so its length along the diagonal is offset*sqrt(2).
            float viewRadius = camera.ViewDistance(_radiusAU);
            const float invSqrt2 = 0.70710678f;
            float offset = MathF.Max(MinLeaderOffset, (viewRadius + BodyEdgeGap) * invSqrt2);

            // Start the leader a few pixels past the body edge so it doesn't touch.
            float startOffset = (viewRadius + BodyLineGap) * invSqrt2;
            _lineStartX = anchorX + startOffset;
            _lineStartY = anchorY + startOffset;

            // 45° leader down-right from the body, then horizontal under the label.
            _elbowX = anchorX + offset;
            _elbowY = anchorY + offset;

            _nameRect.X = (int)_elbowX;
            _nameRect.Y = (int)(_elbowY - _nameRect.H);

            Rect.Location = new (_nameRect.X - Padding, _nameRect.Y - Padding);

            if (_showOrders)
                UpdateOrderLayout();

            OnFrameUpdateExt(matrix, camera);
        }

        private void UpdateOrderLayout()
        {
            string? next = ResolveCurrentOrderText();
            if (!string.Equals(_orderText, next, StringComparison.Ordinal))
            {
                _orderText = next ?? "";
                DestroyOrder();
            }

            if (string.IsNullOrEmpty(_orderText) || Styles.SDLDefaultFont == IntPtr.Zero)
            {
                _orderRect = default;
                return;
            }

            float h = SDL3.TTF.GetFontHeight(Styles.SDLDefaultFont);
            SDL3.TTF.GetStringSize(Styles.SDLDefaultFont, _orderText, 0, out int w, out _);
            _orderRect.W = w;
            _orderRect.H = h;
            _orderRect.X = _shipScreenX - w * 0.5f;
            _orderRect.Y = _shipScreenY - OrderAboveShipGap - h;
        }

        private string? ResolveCurrentOrderText()
        {
            var entity = _state.GameClient?.Galaxy.GetSystem(SystemId)?.GetEntity(EntityId);
            var own = entity?.GetView<OrdersView>();
            var shipActivity = OrderDisplayHelpers.GetShipActivityOrders(
                own, entity?.GetView<ActivityView>());
            if (shipActivity.Count > 0 && !string.IsNullOrWhiteSpace(shipActivity[0].Name)
                && !shipActivity[0].Name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return shipActivity[0].Name;

            var fleet = OrderDisplayHelpers.GetFleetOrders(_state.GameClient, EntityId);
            if (fleet.Count > 0 && !string.IsNullOrWhiteSpace(fleet[0].Name)
                && !fleet[0].Name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return "Fleet: " + fleet[0].Name;

            return null;
        }

        private bool RenderName(IntPtr rendererPtr)
        {
            IntPtr textSurface = SDL3.TTF.RenderTextSolid(
                    Styles.SDLDefaultFont,
                    _name,
                    0,
                    _color);

            if (textSurface == IntPtr.Zero) {
                Trace.WriteLine("EntityLabel: failed to create surface");
                return false;
            }

            _nameTexture = SDL.CreateTextureFromSurface(rendererPtr, textSurface);

            if (_nameTexture == IntPtr.Zero) {
                SDL.DestroySurface(textSurface);

                Trace.WriteLine("EntityLabel: failed to create texture from surface");
                return false;
            }

            SDL.DestroySurface(textSurface);

            return true;
        }

        private bool RenderOrder(IntPtr rendererPtr)
        {
            if (string.IsNullOrEmpty(_orderText))
                return false;

            IntPtr textSurface = SDL3.TTF.RenderTextSolid(
                    Styles.SDLDefaultFont,
                    _orderText,
                    0,
                    _orderColor);

            if (textSurface == IntPtr.Zero) {
                Trace.WriteLine("EntityLabel: failed to create order surface");
                return false;
            }

            _orderTexture = SDL.CreateTextureFromSurface(rendererPtr, textSurface);
            SDL.DestroySurface(textSurface);

            if (_orderTexture == IntPtr.Zero) {
                Trace.WriteLine("EntityLabel: failed to create order texture");
                return false;
            }

            return true;
        }

        public void Draw(IntPtr rendererPtr, Camera camera)
        {
            if (rendererPtr == IntPtr.Zero)
                return;

            bool nameOnScreen = camera.IsOnScreen(Rect.X, Rect.Y, Rect.Width, Rect.Height);
            bool orderOnScreen = _showOrders
                && !string.IsNullOrEmpty(_orderText)
                && camera.IsOnScreen(_orderRect.X, _orderRect.Y, _orderRect.W, _orderRect.H);
            if (!nameOnScreen && !orderOnScreen)
                return;

            if (nameOnScreen && (_pressed || _hovered))
            {
                byte r, g, b, a;
                SDL.GetRenderDrawColor(rendererPtr, out r, out g, out b, out a);

                if (_pressed)
                {
                    var c = Styles.Theme.ButtonActive;
                    SDL.SetRenderDrawColor(rendererPtr, c.R, c.G, c.B, c.A);
                }
                else if (_hovered)
                {
                    var c = Styles.Theme.ButtonHovered;
                    SDL.SetRenderDrawColor(rendererPtr, c.R, c.G, c.B, c.A);
                }

                SDL.FRect frect = new () {
                    X = Rect.X,
                    Y = Rect.Y,
                    W = Rect.Width,
                    H = Rect.Height
                };

                SDL.RenderFillRect(rendererPtr, frect);

                SDL.SetRenderDrawColor(rendererPtr, r, g, b ,a);
            }

            if (nameOnScreen && (_nameTexture != IntPtr.Zero || RenderName(rendererPtr)))
            {
                // Leader line: diagonal from body at 45° down-right, then horizontal under the label.
                byte lr, lg, lb, la;
                SDL.GetRenderDrawColor(rendererPtr, out lr, out lg, out lb, out la);
                SDL.SetRenderDrawColor(rendererPtr, _color.R, _color.G, _color.B, _color.A);
                SDL.RenderLine(rendererPtr, _lineStartX, _lineStartY, _elbowX, _elbowY);
                SDL.RenderLine(rendererPtr, _elbowX, _elbowY, _elbowX + _nameRect.W, _elbowY);
                SDL.SetRenderDrawColor(rendererPtr, lr, lg, lb, la);

                SDL.RenderTexture(rendererPtr, _nameTexture, IntPtr.Zero, in _nameRect);
            }

            if (orderOnScreen && (_orderTexture != IntPtr.Zero || RenderOrder(rendererPtr)))
                SDL.RenderTexture(rendererPtr, _orderTexture, IntPtr.Zero, in _orderRect);

            DrawExt(rendererPtr, camera);
        }

        protected bool IsHovered => _hovered;
    }
}
