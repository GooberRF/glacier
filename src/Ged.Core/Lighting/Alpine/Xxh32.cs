using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// XXH32 (xxHash, 32-bit), implemented from the published algorithm description (xxHash specification,
/// "XXH32 algorithm description", Yann Collet). Alpine Faction fingerprints the surface records an Alpine lightmap
/// section was baked for with XXH32 at seed 0 (game_patch/graphics/af_lightmap.cpp:36-41,
/// editor_patch/alpine_lightmaps.cpp:1946-1988), so Glacier computes the same digest over the bytes it writes.
/// </summary>
public static class Xxh32
{
    private const uint Prime1 = 2654435761u;
    private const uint Prime2 = 2246822519u;
    private const uint Prime3 = 3266489917u;
    private const uint Prime4 = 668265263u;
    private const uint Prime5 = 374761393u;

    /// <summary>The XXH32 digest of <paramref name="data"/> with <paramref name="seed"/>.</summary>
    public static uint Hash(ReadOnlySpan<byte> data, uint seed = 0)
    {
        int len = data.Length;
        int p = 0;
        uint h;
        if (len >= 16)
        {
            uint v1 = seed + Prime1 + Prime2;
            uint v2 = seed + Prime2;
            uint v3 = seed;
            uint v4 = seed - Prime1;
            int limit = len - 16;
            do
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(data[p..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 8)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 12)..]));
                p += 16;
            }
            while (p <= limit);

            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
        }
        else
        {
            h = seed + Prime5;
        }

        h += (uint)len;
        while (p + 4 <= len)
        {
            h += BinaryPrimitives.ReadUInt32LittleEndian(data[p..]) * Prime3;
            h = BitOperations.RotateLeft(h, 17) * Prime4;
            p += 4;
        }

        while (p < len)
        {
            h += data[p] * Prime5;
            h = BitOperations.RotateLeft(h, 11) * Prime1;
            p++;
        }

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    private static uint Round(uint acc, uint lane)
    {
        acc += lane * Prime2;
        acc = BitOperations.RotateLeft(acc, 13);
        return acc * Prime1;
    }
}
