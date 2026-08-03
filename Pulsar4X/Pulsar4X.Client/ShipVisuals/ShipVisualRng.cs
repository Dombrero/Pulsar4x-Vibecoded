using System;

namespace Pulsar4X.Client.ShipVisuals;

/// <summary>Mulberry32 + FNV-1a, matching aurora_module_database_generator_v5.html.</summary>
public static class ShipVisualRng
{
    public static Func<double> Create(int seed)
    {
        int s = seed;
        return () =>
        {
            unchecked
            {
                s |= 0;
                s += (int)0x6D2B79F5;
                int t = s;
                t = Imul(t ^ (t >>> 15), t | 1);
                t ^= t + Imul(t ^ (t >>> 7), t | 61);
                return ((uint)(t ^ (t >>> 14))) / 4294967296.0;
            }
        };
    }

    public static uint Hash(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s)
        {
            h ^= c;
            h = unchecked(h * 16777619u);
        }
        return h;
    }

    private static int Imul(int a, int b) => unchecked((int)((uint)a * (uint)b));
}
