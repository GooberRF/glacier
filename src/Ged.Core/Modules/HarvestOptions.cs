using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>Every threshold of the module harvest (general rules; defaults documented per field).</summary>
public sealed record HarvestOptions
{
    /// <summary>A solid non-detail brush is a candidate when its bounding-box volume is at most this (m³).</summary>
    public double MaxSolidVolume { get; init; } = 80;

    /// <summary>… and its largest extent at most this (m).</summary>
    public double MaxSolidExtent { get; init; } = 20;

    /// <summary>An air brush is a niche candidate when its bounding-box volume is at most this (m³) …</summary>
    public double MaxNicheVolume { get; init; } = 30;

    /// <summary>… its smallest extent at most this (m) …</summary>
    public double MaxNicheThickness { get; init; } = 1.25;

    /// <summary>… and at least this fraction of its volume is solid without it.</summary>
    public double NicheSolidFraction { get; init; } = 0.7;

    /// <summary>Candidate bounds are grown by this before the adjacency test (m).</summary>
    public double AdjacencyGrow { get; init; } = 0.06;

    /// <summary>Candidates are adjacent when their real geometry is within this (m).</summary>
    public double AdjacencyDistance { get; init; } = 0.1;

    /// <summary>Components above these limits are split by detaching their largest members.</summary>
    public int MaxComponentMembers { get; init; } = 48;

    public double MaxComponentHorizontal { get; init; } = 20;

    public double MaxComponentHeight { get; init; } = 16;

    /// <summary>Contact probes sit this far outside a side of the module bounds (m).</summary>
    public double ContactOffset { get; init; } = 0.08;

    /// <summary>A side is in contact when at least this fraction of its 5×5 probes is solid.</summary>
    public double ContactFraction { get; init; } = 0.5;

    /// <summary>A module "touches" the floor when its bottom is within this of the floor plane (m).</summary>
    public double FloorTouch { get; init; } = 0.05;

    /// <summary>Floor-below / ceiling-above marches stop after this distance (m).</summary>
    public double MaxProbeDistance { get; init; } = 40;

    /// <summary>A drop beside an edge module is at least this deep (m).</summary>
    public double EdgeDrop { get; init; } = 0.75;

    /// <summary>The smallest clear opening a frame must surround (width, height in m).</summary>
    public double MinOpeningWidth { get; init; } = 1.2;

    public double MinOpeningHeight { get; init; } = 1.8;

    /// <summary>A stretch slice must be at least this long (m) …</summary>
    public double MinSliceLength { get; init; } = 0.25;

    /// <summary>… and at least this fraction of the module extent on that axis.</summary>
    public double MinSliceFraction { get; init; } = 0.35;

    /// <summary>A tile family needs at least this many congruent units.</summary>
    public int MinTileUnits { get; init; } = 3;

    /// <summary>Tile pitch and collinearity tolerance (m).</summary>
    public double TileTolerance { get; init; } = 0.03;

    /// <summary>Quality gate: smallest allowed largest extent (m).</summary>
    public double MinModuleExtent { get; init; } = 0.15;

    /// <summary>Quality gate: the second-largest extent must reach this (m), else the module is a stick or sliver.</summary>
    public double MinModuleThickness { get; init; } = 0.1;

    /// <summary>Quality gate: largest allowed extent (m).</summary>
    public double MaxModuleExtent { get; init; } = 24;

    public int MaxModuleBrushes { get; init; } = 80;

    public int MaxModuleFaces { get; init; } = 800;

    /// <summary>The provenance list is capped at this many entries (the total is kept in occurrences).</summary>
    public int MaxProvenance { get; init; } = 12;

    /// <summary>Weight of the level's dominant style families in a module's style blend.</summary>
    public double LevelStyleWeight { get; init; } = 0.35;

    /// <summary>Weight of a manifest style hint in a module's style blend.</summary>
    public double HintStyleWeight { get; init; } = 0.25;

    /// <summary>A style is listed when its share of the blend is at least this.</summary>
    public double MinStyleShare { get; init; } = 0.15;
}

/// <summary>What the harvester needs to know about one level besides its geometry.</summary>
public sealed class HarvestLevelContext
{
    /// <summary>The level's provenance path (relative to the manifest root; <c>pack.vpp|level.rfl</c> inside a packfile).</summary>
    public required string Level { get; init; }

    /// <summary>The packfile holding the level (relative to the manifest root), or null.</summary>
    public string? Packfile { get; init; }

    /// <summary>True when the texture is a stock (retail packfile) texture; null = unknown (no RF install).</summary>
    public Func<string, bool>? IsStockTexture { get; init; }

    /// <summary>True when the texture resolves in the level's asset library (install + its packfiles); null = unknown.</summary>
    public Func<string, bool>? TextureResolves { get; init; }

    /// <summary>The packfile (relative path) that supplies a custom texture, or null.</summary>
    public Func<string, string?>? CustomTextureSource { get; init; }

    /// <summary>The texture's stock browser category (e.g. "Wall - Rock"), or null.</summary>
    public Func<string, string?>? TextureCategory { get; init; }

    /// <summary>A manifest style hint for every module of this level, or null.</summary>
    public string? StyleHint { get; init; }
}

/// <summary>One harvested module occurrence before de-duplication.</summary>
public sealed class HarvestedModule
{
    /// <summary>The module's description (id, score and occurrences are filled in when the library is built).</summary>
    public required ModuleInfo Info { get; set; }

    /// <summary>The module's brushes in its local frame (module order = source time order).</summary>
    public required IReadOnlyList<Brush> LocalBrushes { get; init; }

    /// <summary>Exact identity: category + attach + quantised local geometry (mirror-folded) + texture names.</summary>
    public required string ExactKey { get; init; }

    /// <summary>Near-duplicate identity (size and member-size classes, primary style and texture family).</summary>
    public required string NearKey { get; init; }

    /// <summary>The single-occurrence score (contact, member count, textures, classification; occurrences = 1).</summary>
    public double BaseScore { get; init; }

    public double ContactConfidence { get; init; }

    public double ClassConfidence { get; init; }

    public double TextureValidity { get; init; }

    /// <summary>Where it was found.</summary>
    public required ModuleProvenance Provenance { get; init; }

    /// <summary>The manifest order of its source (ties in representative choice).</summary>
    public int SourceOrder { get; set; }
}

/// <summary>The per-level harvest outcome.</summary>
public sealed class LevelHarvestResult
{
    public required string Level { get; init; }

    public List<HarvestedModule> Modules { get; } = new();

    public int Brushes { get; set; }

    public int Candidates { get; set; }

    public int Components { get; set; }

    /// <summary>Rejected components by reason.</summary>
    public SortedDictionary<string, int> Rejected { get; } = new(StringComparer.Ordinal);

    public void Reject(string reason) => Rejected[reason] = Rejected.GetValueOrDefault(reason) + 1;
}
