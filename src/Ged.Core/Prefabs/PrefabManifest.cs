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
    /// <summary>The current writer format version.</summary>
    public const int CurrentVersion = 1;

    public int FormatVersion { get; set; } = CurrentVersion;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC, ISO-8601 in JSON).</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Object/brush counts, for the browser tooltip (informational).</summary>
    public int BrushCount { get; set; }

    public int ObjectCount { get; set; }
}
