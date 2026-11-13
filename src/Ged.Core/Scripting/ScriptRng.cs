using System;
using System.Collections.Generic;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>rng</c> global: a seeded, deterministic random source. Because the sandbox
/// withholds wall-clock nondeterminism, a procedural script that only draws from here is fully
/// reproducible and diffable in tests. Re-seed with <c>rng.set_seed(n)</c> for a fixed stream
/// (<c>rng.seed</c> reads the current seed).
/// </summary>
public sealed class ScriptRng
{
    private Random _random;

    public ScriptRng(int seed = 0)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    /// <summary>Lua: <c>rng.seed</c> (read-only) — the seed currently driving the stream.</summary>
    [ScriptDoc("The seed the deterministic stream started from (the run's seed, or the last rng.set_seed).", Example = "log.info(\"seed \" .. rng.seed)")]
    public int Seed { get; private set; }

    /// <summary>Lua: <c>rng.set_seed(n)</c> — restarts the deterministic stream at seed <paramref name="seed"/>.</summary>
    [ScriptDoc("Restarts the deterministic random stream at seed n: the same seed gives the same sequence.", Example = "rng.set_seed(1234)")]
    public void SetSeed(int seed)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    /// <summary>Lua: <c>rng.float()</c> — a double in [0, 1).</summary>
    [ScriptDoc("A random number in [0, 1).", Example = "if rng.float() < 0.25 then log.info(\"rare\") end")]
    public double Float() => _random.NextDouble();

    /// <summary>Lua: <c>rng.range(min, max)</c> — a double in [min, max).</summary>
    [ScriptDoc("A random number in [min, max).", Example = "local h = rng.range(2, 6)")]
    public double Range(double min, double max) => min + (_random.NextDouble() * (max - min));

    /// <summary>Lua: <c>rng.int(min, max)</c> — an integer in [min, max] inclusive.</summary>
    [ScriptDoc("A random integer in [min, max], both inclusive.", Example = "local n = rng.int(1, 6)")]
    public int Int(int min, int max)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return _random.Next(min, max == int.MaxValue ? max : max + 1);
    }

    /// <summary>Lua: <c>rng.bool()</c> — a fair coin.</summary>
    [ScriptDoc("A random boolean (50/50).", Example = "if rng.bool() then log.info(\"heads\") end")]
    public bool Bool() => _random.Next(2) == 0;

    /// <summary>Lua: <c>rng.pick(array)</c> — a uniformly-chosen element, or nil when empty.</summary>
    [ScriptDoc("A uniformly chosen element of the list, or nil when it is empty.", Example = "local tex = rng.pick({\"rock01.tga\", \"rock02.tga\"})")]
    public object? Pick(IList<object>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        return items[_random.Next(items.Count)];
    }
}
