using System;

namespace Pulsar4X.Client.ShipVisuals;

/// <summary>CPU-side RGBA8 bitmap used by the ship visual composer.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match dimensions.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public void Clear(byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
            Pixels[i + 3] = a;
        }
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        int i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    public (byte r, byte g, byte b, byte a) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return (0, 0, 0, 0);
        int i = (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public RgbaImage Clone()
    {
        var copy = new byte[Pixels.Length];
        Buffer.BlockCopy(Pixels, 0, copy, 0, Pixels.Length);
        return new RgbaImage(Width, Height, copy);
    }
}
