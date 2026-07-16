using System.Collections.Generic;
using Ged.Core.Editing.Graph;
using Ged.Core.Lighting;

namespace Ged.Core.Editing;

/// <summary>
/// The full editor-only <c>&lt;level&gt;.gedlayout.json</c> sidecar: link-graph node
/// positions, the per-level lightmap bake method (feature 1) and the measurement
/// annotations (feature 4). It is never written into the RFL and is excluded from the
/// packfile scanner. Each block is optional; a missing block loads as its default.
/// </summary>
public sealed class LevelSidecar
{
    /// <summary>Sidecar schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Link-graph node positions (the original sidecar payload).</summary>
    public GraphLayout Graph { get; set; } = new();

    /// <summary>Per-level lightmap bake method, or null to use the global default.</summary>
    public LightingMethod? Lighting { get; set; }

    /// <summary>Editor-only dimension/measurement annotations.</summary>
    public List<Annotation> Annotations { get; set; } = new();
}
