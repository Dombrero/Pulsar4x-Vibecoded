using System;
using SDL3;

namespace Pulsar4X.Client.BodyVisuals;

/// <summary>
/// Runtime animated corona for extreme heat (stars / T &gt; 2000 °C).
/// Ring radii are fractions of <paramref name="bodyRadius"/> so they shrink with zoom.
/// </summary>
public static class ExtremeHeatRingDrawer
{
    public static void Draw(IntPtr rendererPtr, int cx, int cy, int bodyRadius, byte r, byte g, byte b)
    {
        if (bodyRadius < 2)
            return;

        // ~1.1s pulse
        double phase = Environment.TickCount64 / 1000.0 * Math.PI * 1.8;
        float pulse = 0.55f + 0.45f * (float)((Math.Sin(phase) + 1.0) * 0.5); // 0.55..1

        // Fewer rings when tiny so galmap / zoomed-out views stay clean.
        int rings = bodyRadius < 10 ? 4 : bodyRadius < 20 ? 6 : 8;

        for (int i = 0; i < rings; i++)
        {
            float t = i / (float)Math.Max(1, rings - 1);
            // Outer extent ~12%→~50% of body radius (scales with zoom), slight pulse.
            float extent = 0.12f + t * (0.38f + pulse * 0.12f);
            int rad = Math.Max(bodyRadius + 1, (int)Math.Round(bodyRadius * (1f + extent)));
            byte a = (byte)Math.Clamp((200 * pulse) * (1f - t * 0.85f), 0, 220);
            if (a < 10)
                continue;

            SDL.SetRenderDrawColor(rendererPtr, r, g, b, a);
            int thick = bodyRadius < 12 ? 1 : 2;
            for (int k = 0; k < thick; k++)
                global::Pulsar4X.Client.DrawPrimitive.DrawEllipse(rendererPtr, cx, cy, rad + k, rad + k);
        }

        // Bright inner limb — also proportional (1–2 px beyond core).
        byte limbA = (byte)Math.Clamp(130 + 80 * pulse, 0, 255);
        SDL.SetRenderDrawColor(rendererPtr, r, g, b, limbA);
        int limbPad = Math.Max(1, bodyRadius / 20);
        for (int k = 0; k < Math.Min(2, limbPad + 1); k++)
            global::Pulsar4X.Client.DrawPrimitive.DrawEllipse(rendererPtr, cx, cy, bodyRadius + k, bodyRadius + k);
    }
}
