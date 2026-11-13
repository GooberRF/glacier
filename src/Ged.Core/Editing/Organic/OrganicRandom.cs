using System;

namespace Ged.Core.Editing.Organic;

/// <summary>
/// The organic generators' deterministic random stream: SplitMix64, seeded from a
/// <c>(seed, stream…)</c> tuple by mixing, so every brush, station or attempt draws from its own
/// reproducible stream. Never <see cref="System.Random"/> (its algorithm is not a stable contract)
/// and never the script's <c>rng</c> (generators must not consume or disturb it).
/// </summary>
public sealed class OrganicRandom
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    /// <summary>A stream for <paramref name="seed"/> and the stream identifiers <paramref name="streams"/> (in order).</summary>
    public OrganicRandom(long seed, params long[] streams)
    {
        ulong s = Mix(unchecked((ulong)seed) + Golden);
        foreach (long id in streams ?? Array.Empty<long>())
        {
            s = Mix(s ^ Mix(unchecked((ulong)id) + Golden));
        }

        _state = s;
    }

    /// <summary>The next 64 raw bits.</summary>
    public ulong NextUInt64()
    {
        _state = unchecked(_state + Golden);
        return Mix(_state);
    }

    /// <summary>A uniform value in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A uniform value in [<paramref name="a"/>, <paramref name="b"/>).</summary>
    public double Range(double a, double b) => a + ((b - a) * NextDouble());

    /// <summary>The SplitMix64 finaliser.</summary>
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
