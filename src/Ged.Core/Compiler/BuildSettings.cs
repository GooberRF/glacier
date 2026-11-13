using System;
using System.Threading;
using Ged.Core.Lighting;

namespace Ged.Core.Compiler;

/// <summary>
/// The user's build choices that are NOT derivable from the document itself — the inputs the
/// editor reads from its settings (Geometry ▸ Build method, Level ▸ Lightmap Method). Every build
/// host (the editor, script runs, a headless/automation host) hands one of these to
/// <see cref="CompileOptionsFactory"/>, so the same choices always produce the same compile.
/// <see cref="EditorDefaults"/> is exactly what a fresh install's settings produce.
/// </summary>
public sealed record BuildSettings
{
    /// <summary>
    /// Geometry build method: true = "RED-authentic Shared BSP" (the default), false = GED's
    /// "Incremental" construction. Maps to <see cref="CompileOptions.SharedBsp"/>.
    /// </summary>
    public bool UseSharedBsp { get; init; } = true;

    /// <summary>
    /// "Repair holes when building" — Alpine RED's build repair (its <c>[Alpine] RepairBuildHoles</c>, on by
    /// default). Maps to <see cref="CompileOptions.AlpineRepair"/>. Check for Holes forces a sealed build and so
    /// counts what remains after this repair; it never repairs on its own.
    /// </summary>
    public bool RepairBuildHoles { get; init; } = true;

    /// <summary>
    /// The lightmap method (base + modifiers) a real bake applies, and the source of the
    /// High-Resolution Lightmaps surface density. Null means stock RED Classic with no modifiers
    /// (the raw-options state the parity gates pin). The default is a fresh
    /// <see cref="Lighting.LightingMethod"/> — RED Classic with the editor's default modifiers
    /// (movers cast shadows), matching a fresh install.
    /// </summary>
    public LightingMethod? LightingMethod { get; init; } = new();

    /// <summary>The settings a fresh editor install builds with.</summary>
    public static BuildSettings EditorDefaults => new();
}

/// <summary>
/// The per-build request: what kind of build to run (sealed vs preview, with or without a
/// lighting bake) plus the run's progress/cancellation plumbing. Paired with
/// <see cref="BuildSettings"/> by <see cref="CompileOptionsFactory"/>.
/// </summary>
public sealed record CompileRequest
{
    /// <summary>
    /// True (default) = a full, SEALED build: the surface/atlas stage and the t-joint seal run.
    /// False = the fast live-preview build, which skips both — its geometry is unsealed and must
    /// never be used to measure holes or be saved as authoritative.
    /// </summary>
    public bool Sealed { get; init; } = true;

    /// <summary>Bake real lighting into the atlas (Calculate Lighting).</summary>
    public bool BakeLighting { get; init; }

    /// <summary>Raycast occluder shadows during the bake (Calculate Lighting vs. … w/o Shadows).</summary>
    public bool CastShadows { get; init; } = true;

    /// <summary>
    /// When true (default) a bake applies <see cref="BuildSettings.LightingMethod"/>; false keeps
    /// the bake on stock RED Classic (the editor's Preview Lighting gate for slow levels). The
    /// High-Resolution Lightmaps surface density follows the method either way.
    /// </summary>
    public bool ApplyLightingMethod { get; init; } = true;

    /// <summary>Cancellation for the compile and the bake.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Optional compile-stage progress sink (invoked on the compiling thread).</summary>
    public Action<CompileProgress>? Progress { get; init; }

    /// <summary>Optional bake progress sink (invoked from the bake driver thread).</summary>
    public Action<BakeProgress>? BakeProgress { get; init; }
}
