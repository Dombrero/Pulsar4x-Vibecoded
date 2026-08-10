using System;
using Pulsar4X.Orbital;
using SDL3;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Draws the straight warp chord (entry → exit). Warp travel is non-newtonian and linear —
    /// bezier curves toward a moving relative endpoint falsely looked like paths through the sun.
    /// </summary>
    public class WarpMovingIcon : Icon
    {
        Vector3 _translateStartPoint = new Vector3();
        Vector3 _translateEndPoint = new Vector3();
        Vector3 _currentPosition = new Vector3();

        public byte Red = 255;
        public byte Grn = 255;
        public byte Blu = 0;
        byte alpha = 100;
        SDL.FPoint[] _drawPoints = new SDL.FPoint[2];

        public WarpMovingIcon(Pulsar4X.Api.WarpMovingView warp, IPosition position,
            IPosition? targetParentPosition) : base(new Vector3())
        {
            _translateStartPoint = new Vector3(warp.EntryPointAbsolute.X, warp.EntryPointAbsolute.Y, warp.EntryPointAbsolute.Z);
            _translateEndPoint = new Vector3(warp.ExitPointAbsolute.X, warp.ExitPointAbsolute.Y, warp.ExitPointAbsolute.Z);
            _positionDB = position;
            this.OnPhysicsUpdate();
        }

        public override void OnPhysicsUpdate()
        {
            _currentPosition = _positionDB.AbsolutePosition;
        }

        public override void OnFrameUpdate(Matrix matrix, Camera camera)
        {
            // Remaining chord: ship → planned exit (straight line).
            var spos = camera.ViewCoordinateV2_m(_currentPosition);
            var epos = camera.ViewCoordinateV2_m(_translateEndPoint);
            _drawPoints[0] = new SDL.FPoint() { X = (float)spos.X, Y = (float)spos.Y };
            _drawPoints[1] = new SDL.FPoint() { X = (float)epos.X, Y = (float)epos.Y };
        }

        public override void Draw(IntPtr rendererPtr, Camera camera)
        {
            SDL.SetRenderDrawColor(rendererPtr, Red, Grn, Blu, alpha);
            SDL.RenderLines(rendererPtr, _drawPoints, _drawPoints.Length);
        }
    }
}
