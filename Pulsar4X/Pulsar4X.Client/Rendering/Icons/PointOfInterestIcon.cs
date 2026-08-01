using System;
using Pulsar4X.Orbital;
using SDL3;

namespace Pulsar4X.Client;
public class PointOfInterestIcon : Icon
{
    private readonly SDL.Color _colour;

    public PointOfInterestIcon(IPosition positionDB) : this(positionDB, new SDL.Color { R = 115, G = 115, B = 115, A = 165 })
    {
    }

    /// <summary>Jump points use a brighter colour so they stand out from grey survey anomalies.</summary>
    public static PointOfInterestIcon ForJumpPoint(IPosition positionDB)
        => new(positionDB, new SDL.Color { R = 80, G = 200, B = 255, A = 220 });

    public PointOfInterestIcon(IPosition positionDB, SDL.Color colour) : base(positionDB)
    {
        _colour = colour;
        BasicShape();
        OnPhysicsUpdate();
    }

    void BasicShape()
    {
        // Diamond marker for grav anomalies / jump points.
        Vector2[] points = {
            new Vector2() { X = 0, Y = 5 },
            new Vector2() { X = 5, Y = 0 },
            new Vector2() { X = 0, Y = -5 },
            new Vector2() { X = -5, Y = 0 },
            new Vector2() { X = 0, Y = 5 }
        };

        Shapes.Add(new Shape() { Points = points, Color = _colour });
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
                int x = (int)(ViewScreenPos.X + tranlsatedPoint.X );
                int y = (int)(ViewScreenPos.Y + tranlsatedPoint.Y );
                drawPoints[i2] = new Vector2() { X = x, Y = y };
            }
            DrawShapes[i] = new Shape() { Points = drawPoints, Color = shape.Color };
        }
    }
}