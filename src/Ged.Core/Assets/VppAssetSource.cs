using Ged.Core.IO.Vpp;

namespace Ged.Core.Assets;

/// <summary>An asset source backed by a mounted VPP packfile.</summary>
public sealed class VppAssetSource : IAssetSource, IDisposable
{
    private readonly VppArchive _archive;

    public VppAssetSource(VppArchive archive, string description, string? category = null)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        Description = description;
        Category = category;
    }

    public string Description { get; }

    public AssetSourceKind Kind => AssetSourceKind.Packfile;

    public string? Category { get; }

    /// <summary>Opens a VPP file on disk as an asset source.</summary>
    public static VppAssetSource Open(string path, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new VppAssetSource(VppArchive.Open(path), Path.GetFileName(path), category);
    }

    public bool Contains(string name) => _archive.Contains(name);

    public byte[]? Read(string name) => _archive.Find(name) is { } e ? _archive.Read(e) : null;

    public long? GetSize(string name) => _archive.Find(name)?.Size;

    public IEnumerable<string> EnumerateFiles() => _archive.Entries.Select(e => e.Name);

    public void Rescan()
    {
        // Packfiles are immutable while mounted; nothing to re-scan.
    }

    public void Dispose() => _archive.Dispose();
}
