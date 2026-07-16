using System;

namespace Ged.Core.Packaging;

/// <summary>
/// Editor-only sidecar files that live next to a level but must never be packed
/// into its <c>.vpp</c> (they are not game assets). The dependency scanner filters
/// any reference matching one of these suffixes, and the graph/layout sidecar
/// (<c>.gedlayout.json</c>) is documented in the README as editor-only.
/// </summary>
public static class EditorOnlyFiles
{
    /// <summary>File-name suffixes that mark an editor-only sidecar (never packed).</summary>
    public static readonly string[] Suffixes =
    {
        ".gedlayout.json", // per-level graph node positions (Link Graph 2.0)
        ".autosave.rfl",   // autosave snapshot
        ".gedprefab",      // prefab package
    };

    /// <summary>True when <paramref name="fileName"/> is an editor-only sidecar that must not be packed.</summary>
    public static bool IsEditorOnly(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (string s in Suffixes)
        {
            if (fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
