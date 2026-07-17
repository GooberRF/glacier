using System;

namespace Ged.Core.Prefabs;

/// <summary>
/// The metadata record stored as <c>manifest.json</c> inside a <c>.gedprefab</c>
/// package. <see cref="FormatVersion"/> is bumped on breaking changes; readers load
/// any manifest whose version they recognise and preserve unknown JSON fields for
/// forward compatibility (System.Text.Json ignores them).
/// </summary>
public sealed class PrefabManifest
{
    /// <summary>The current writer format version (v2 introduced the fixed pivot-based payload).</summary>
    public const int CurrentVersion = 2;

    public int FormatVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// True when the <c>payload.rfg</c> is stored in FIXED prefab-local space — its local origin IS
    /// the prefab pivot — so placement and propagation pose it at each instance's pose record without
    /// deriving a pivot from content (which would shift existing members when the content's bounds
    /// change). Legacy v1 packages have this false; the editor establishes their pivot once (bbox
    /// centre) on load and treats it as fixed thereafter.
    /// </summary>
    public bool PivotBased { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC, ISO-8601 in JSON).</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Object/brush counts, for the browser tooltip (informational).</summary>
    public int BrushCount { get; set; }

    public int ObjectCount { get; set; }
}
