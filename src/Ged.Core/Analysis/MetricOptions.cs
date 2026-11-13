using System;
using System.Collections.Generic;
using Ged.Core.Tables;

namespace Ged.Core.Analysis;

/// <summary>Inputs and limits for <see cref="LevelMetrics.Compute"/>. Everything is optional.</summary>
public sealed class MetricOptions
{
    /// <summary>The level's file (or packfile entry) name — gametype prefix and the local-only identity field.</summary>
    public string? FileName { get; init; }

    /// <summary><c>retail</c> or <c>community</c> (the corpus sweep decides; null = not recorded).</summary>
    public string? Origin { get; init; }

    /// <summary>Normalised author string → canonical author (the local alias table). Null = no aliases.</summary>
    public IReadOnlyDictionary<string, string>? AuthorAliases { get; init; }

    /// <summary>The mounted item table (item categories); null = categories are not recorded.</summary>
    public ItemCatalog? Items { get; init; }

    /// <summary>Texture name → true when a retail packfile holds it; null (the delegate) = no install, fraction is null.</summary>
    public Func<string, bool>? IsStockTexture { get; init; }

    /// <summary>The floor-lattice cap: the pitch doubles until a level yields at most this many samples (spec: 20,000).</summary>
    public int MaxFloorSamples { get; init; } = 20_000;

    /// <summary>The starting floor-lattice pitch in metres (spec: 0.5).</summary>
    public double LatticePitch { get; init; } = 0.5;

    /// <summary>Cap on link-graph paths enumerated for motif mining.</summary>
    public int MaxMotifPaths { get; init; } = 200_000;

    /// <summary>
    /// Test hook: invoked once per floor sample the sampler measures (lets a test force a slow sampler to exercise
    /// the cooperative timeout). Never set in production.
    /// </summary>
    public Action? SamplerProbe { get; init; }

    /// <summary>
    /// Detect the level class (standard / open_air) with the design grader's class detector (DESIGN-QUALITY-SPEC §(h),
    /// E12): a spatial walk-graph pass on the compiled geometry, a few seconds per level. The corpus sweep turns it on;
    /// off, <c>identity.class</c> is recorded only when the gametype decides it (payload).
    /// </summary>
    public bool LevelClass { get; init; }

    /// <summary>Floor-sample cap for the class detector's walk graph (its pitch doubles above it).</summary>
    public int LevelClassMaxSamples { get; init; } = 250_000;
}
