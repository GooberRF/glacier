namespace Ged.Core.Effects;

/// <summary>
/// A tiny stateless hash-based pseudo-random source. Every draw is a pure
/// function of (seed, index, salt), so a simulated particle stream is fully
/// deterministic and reproducible — the property the emitter-sim unit tests rely
/// on (same emitter + time ⇒ same particles).
/// </summary>
internal static class SimRandom
{
    /// <summary>A reproducible value in [0, 1) from the three keys.</summary>
    public static float Value(int seed, int index, int salt)
    {
        unchecked
        {
            uint h = (uint)seed * 2654435761u;
            h ^= ((uint)index * 40503u) + ((uint)salt * 2246822519u);
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>A reproducible value in [-1, 1).</summary>
    public static float Signed(int seed, int index, int salt) => (Value(seed, index, salt) * 2f) - 1f;
}
