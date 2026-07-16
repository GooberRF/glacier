namespace Ged.Core.Assets;

/// <summary>
/// An asset source backed by a directory of loose files, indexed by bare file
/// name (case-insensitive). An optional extension allowlist keeps non-asset files
/// (e.g. .vpp, .exe, .dll) out of the index; an optional category label makes the
/// directory a browsable texture category (used for "Custom - &lt;dir&gt;").
/// </summary>
public sealed class DirectoryAssetSource : IAssetSource
{
    private readonly string _directory;
    private readonly bool _recursive;
    private readonly HashSet<string>? _extensions;
    private Dictionary<string, string> _index = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryAssetSource(
        string directory,
        string? category = null,
        bool recursive = false,
        IEnumerable<string>? extensions = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Category = category;
        _recursive = recursive;
        if (extensions is not null)
        {
            _extensions = new HashSet<string>(
                extensions.Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase);
        }

        Rescan();
    }

    public string Description => _directory;

    public AssetSourceKind Kind => AssetSourceKind.LooseDirectory;

    public string? Category { get; }

    /// <summary>The backing directory path.</summary>
    public string DirectoryPath => _directory;

    public bool Contains(string name) => name is not null && _index.ContainsKey(name);

    public byte[]? Read(string name)
    {
        if (name is not null && _index.TryGetValue(name, out string? full) && File.Exists(full))
        {
            return File.ReadAllBytes(full);
        }

        return null;
    }

    /// <summary>Resolves a bare file name to its absolute path, or null if not present.</summary>
    public string? GetFullPath(string name) =>
        name is not null && _index.TryGetValue(name, out string? full) ? full : null;

    /// <summary>
    /// The actual on-disk file name (original case) for a case-insensitive
    /// <paramref name="name"/>, from the cached directory snapshot, or null if
    /// absent. This is what lets a mixed-case reference like <c>Rck_012.TGA</c>
    /// resolve to a <c>rck_012.tga</c> file on a case-sensitive filesystem (ext4):
    /// the snapshot is keyed case-insensitively but stores the original-case path,
    /// so reads always open the real file and this returns its real name. Refreshed
    /// by <see cref="Rescan"/> (the Reload commands).
    /// </summary>
    public string? ResolveActualName(string name) =>
        name is not null && _index.TryGetValue(name, out string? full) ? Path.GetFileName(full) : null;

    public long? GetSize(string name)
    {
        if (name is not null && _index.TryGetValue(name, out string? full))
        {
            var info = new FileInfo(full);
            if (info.Exists)
            {
                return info.Length;
            }
        }

        return null;
    }

    public IEnumerable<string> EnumerateFiles() => _index.Keys;

    public void Rescan()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_directory))
        {
            var option = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string full in Directory.EnumerateFiles(_directory, "*", option))
            {
                if (_extensions is not null && !_extensions.Contains(Path.GetExtension(full)))
                {
                    continue;
                }

                string name = Path.GetFileName(full);
                // First occurrence wins so a stable, deterministic entry is kept per name.
                index.TryAdd(name, full);
            }
        }

        _index = index;
    }

    private static string NormalizeExtension(string ext)
    {
        ArgumentNullException.ThrowIfNull(ext);
        return ext.StartsWith('.') ? ext : "." + ext;
    }
}
