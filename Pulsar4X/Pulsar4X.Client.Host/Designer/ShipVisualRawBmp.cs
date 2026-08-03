using System;
using Pulsar4X.Client.ShipVisuals;
using Pulsar4X.DataStructures;

namespace Pulsar4X.Client.Host;

internal static class ShipVisualRawBmp
{
    /// <summary>Copy an RGBA composer buffer into the engine <see cref="RawBmp"/> layout (R,G,B,A).</summary>
    public static RawBmp ToRawBmp(RgbaImage image)
    {
        var bmp = new RawBmp(image.Width, image.Height, 4);
        Buffer.BlockCopy(image.Pixels, 0, bmp.ByteArray, 0, image.Pixels.Length);
        return bmp;
    }
}
