using System;
using System.Collections.Generic;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>rng</c> global: a seeded, deterministic random source (plan §5.7). Because the sandbox
/// withholds wall-clock nondeterminism, a procedural script that only draws from here is fully
/// reproducible and diffable in tests. Re-seed with <c>rng.seed(n)</c> for a fixed stream.
/// </summary>
public sealed class ScriptRng
{
    private Random _random;

    public ScriptRng(int seed = 0)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    /// <summary>The seed currently driving the stream.</summary>
    public int Seed { get; private set; }

    /// <summary>Lua: <c>rng.seed(n)</c> — restarts the deterministic stream at seed <paramref name="seed"/>.</summary>
    public void SetSeed(int seed)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    /// <summary>Lua: <c>rng.float()</c> — a double in [0, 1).</summary>
    public double Float() => _random.NextDouble();

    /// <summary>Lua: <c>rng.range(min, max)</c> — a double in [min, max).</summary>
    public double Range(double min, double max) => min + (_random.NextDouble() * (max - min));

    /// <summary>Lua: <c>rng.int(min, max)</c> — an integer in [min, max] inclusive.</summary>
    public int Int(int min, int max)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return _random.Next(min, max == int.MaxValue ? max : max + 1);
    }

    /// <summary>Lua: <c>rng.bool()</c> — a fair coin.</summary>
    public bool Bool() => _random.Next(2) == 0;

    /// <summary>Lua: <c>rng.pick(array)</c> — a uniformly-chosen element, or nil when empty.</summary>
    public object? Pick(IList<object>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        return items[_random.Next(items.Count)];
    }
}
