namespace Ged.Core.Assets;

/// <summary>
/// Classifies a mount for the packfile builder's "base-game vs user content"
/// rule: files that resolve from a <see cref="Packfile"/> are game-provided and
/// skipped when packing a level (the engine already ships them), whereas files
/// from a <see cref="LooseDirectory"/> (user_maps, custom-texture dirs, mod
/// overrides) are the mapper's own content and get included.
/// </summary>
public enum AssetSourceKind
{
    /// <summary>A directory of loose files (user_maps / custom / install-root overrides) — includable.</summary>
    LooseDirectory,

    /// <summary>A mounted VPP packfile — treated as game-provided (skipped when packing a level).</summary>
    Packfile,
}

/// <summary>
/// A single mount in the asset VFS: a packfile or a directory. Lookups are by
/// bare file name (no path) and case-insensitive, matching Red Faction's flat,
/// case-insensitive file namespace.
/// </summary>
public interface IAssetSource
{
    /// <summary>Human-readable description for diagnostics (e.g. a VPP name or directory path).</summary>
    string Description { get; }

    /// <summary>Whether this mount is a packfile (game-provided) or a loose directory (user content).</summary>
    AssetSourceKind Kind { get; }

    /// <summary>
    /// Texture-browser category label for this source (e.g. "Custom - abruptdecay"),
    /// or null if the source is not a distinct browsable category.
    /// </summary>
    string? Category { get; }

    /// <summary>True if a file with this bare name exists in this source (case-insensitive).</summary>
    bool Contains(string name);

    /// <summary>Reads a file's bytes by bare name, or null if absent.</summary>
    byte[]? Read(string name);

    /// <summary>The file's logical size in bytes without reading its data, or null if absent.</summary>
    long? GetSize(string name);

    /// <summary>Enumerates all bare file names in this source.</summary>
    IEnumerable<string> EnumerateFiles();

    /// <summary>Re-scans the backing store (directory sources); no-op for immutable packfiles.</summary>
    void Rescan();
}
