namespace Ged.Core.Assets;

/// <summary>
/// Where a resolved asset actually lives: the winning bare file name, the mount
/// that provided it (a VPP name or a directory path), whether that mount is a
/// packfile or a loose directory, the absolute path for loose files (null inside
/// a VPP), and its byte size. Feeds the asset-browser info tooltip, the library
/// health report and the packfile builder's base-game-skip decision.
/// </summary>
public sealed record AssetLocation(
    string ResolvedName,
    string SourceDescription,
    AssetSourceKind SourceKind,
    string? LoosePath,
    long Size)
{
    /// <summary>True when the file lives inside a game/mod packfile (skipped by the level packer).</summary>
    public bool IsPackfile => SourceKind == AssetSourceKind.Packfile;

    /// <summary>A one-line source description: the loose path when known, else "&lt;vpp&gt; (packfile)".</summary>
    public string Origin => LoosePath ?? $"{SourceDescription} (packfile)";
}
