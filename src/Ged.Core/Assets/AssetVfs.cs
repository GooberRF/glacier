using Ged.Core.IO.Mesh;
using Ged.Core.IO.Tex;

namespace Ged.Core.Assets;

/// <summary>
/// The asset virtual file system: an ordered list of mounts (packfiles and
/// directories) resolved first-hit-wins, so higher-priority mounts (loose
/// user_maps content) override lower ones (base-game VPPs). Provides
/// case-insensitive lookup, texture resolution via the supercede chain, mesh
/// loading, texture-category enumeration for the browser, and explicit rescan.
/// </summary>
/// <remarks>
/// Precedence mirrors Red Faction: file search paths added via <c>file_add_path</c>
/// (user_maps, mod dirs) take precedence over packfile contents, and — per Alpine —
/// packfile overriding is disabled after init so base packfiles keep a stable
/// order. GED models this purely as mount order; <see cref="GameMount"/> assembles
/// the standard order for a game install. See docs/research/format-quirks.md §8.
/// </remarks>
public sealed class AssetVfs : IDisposable
{
    private static readonly string[] MeshExtensions = { ".v3m", ".v3c", ".vcm", ".v3d" };

    private readonly List<IAssetSource> _sources;
    private IReadOnlyList<AssetCategory>? _categoriesCache;

    public AssetVfs(IEnumerable<IAssetSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
    }

    /// <summary>Mounts in priority order; index 0 is highest priority (first-hit-wins).</summary>
    public IReadOnlyList<IAssetSource> Sources => _sources;

    /// <summary>Appends a mount at the lowest priority.</summary>
    public void AddSource(IAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
        _categoriesCache = null;
    }

    /// <summary>Inserts a mount at the highest priority (index 0).</summary>
    public void AddSourceOnTop(IAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Insert(0, source);
        _categoriesCache = null;
    }

    /// <summary>True if any mount contains a file with this bare name.</summary>
    public bool Exists(string name) => _sources.Any(s => s.Contains(name));

    /// <summary>The highest-priority mount that has this exact bare name, or null.</summary>
    public IAssetSource? FindSource(string name) =>
        name is null ? null : _sources.FirstOrDefault(s => s.Contains(name));

    /// <summary>Every mount that has this exact bare name, in priority order (winner first).</summary>
    public IReadOnlyList<IAssetSource> FindAllSources(string name) =>
        name is null ? Array.Empty<IAssetSource>() : _sources.Where(s => s.Contains(name)).ToList();

    /// <summary>
    /// Locates a file for a tooltip/where-source query. For a texture reference the
    /// supercede chain picks the winning sibling; other names resolve by exact match,
    /// then by mesh-extension probing. Returns null when nothing resolves.
    /// </summary>
    public AssetLocation? Locate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Exact hit first (a name that already carries a real extension).
        IAssetSource? src = FindSource(name);
        if (src is not null)
        {
            return Describe(name, src);
        }

        // Texture reference: resolve via the supercede chain.
        string? tex = ResolveTexture(name);
        if (tex is not null && FindSource(tex) is { } tsrc)
        {
            return Describe(tex, tsrc);
        }

        // Mesh reference: probe the real mesh extensions.
        string baseName = StripExtension(name);
        foreach (string ext in MeshExtensions)
        {
            string candidate = baseName + ext;
            if (FindSource(candidate) is { } msrc)
            {
                return Describe(candidate, msrc);
            }
        }

        return null;
    }

    private static AssetLocation Describe(string resolvedName, IAssetSource src)
    {
        string? loosePath = src is DirectoryAssetSource d ? d.GetFullPath(resolvedName) : null;
        long size = src.GetSize(resolvedName) ?? 0;
        return new AssetLocation(resolvedName, src.Description, src.Kind, loosePath, size);
    }

    /// <summary>
    /// Resolves a bare reference to the actual on-disk file name (original case) of
    /// the highest-priority LOOSE mount that has it, or null when no loose mount
    /// does. VFS reads are already case-insensitive — loose directory snapshots
    /// (<see cref="DirectoryAssetSource"/>) store original-case paths, so a
    /// mixed-case reference like <c>Rck_012.TGA</c> already opens a
    /// <c>rck_012.tga</c> file on ext4. This surfaces the original-case NAME for
    /// consumers that need exact identity (a case-mismatch linter rule, staging that
    /// copies by exact name). VPP-internal names are already normalized, so this
    /// deliberately ignores packfile mounts. Refreshed by <see cref="Rescan"/>.
    /// </summary>
    public string? ResolveActualName(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        foreach (IAssetSource s in _sources)
        {
            if (s is DirectoryAssetSource d && d.ResolveActualName(reference) is { } actual)
            {
                return actual;
            }
        }

        return null;
    }

    /// <summary>
    /// The actual absolute path (original case) of the highest-priority loose-mount
    /// hit for <paramref name="reference"/>, or null. Companion to
    /// <see cref="ResolveActualName"/> for consumers that need the on-disk path.
    /// </summary>
    public string? ResolveLoosePath(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        foreach (IAssetSource s in _sources)
        {
            if (s is DirectoryAssetSource d && d.GetFullPath(reference) is { } path)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Reads a file by bare name from the highest-priority mount that has it, or null.</summary>
    public byte[]? ReadFile(string name)
    {
        if (name is null)
        {
            return null;
        }

        foreach (IAssetSource s in _sources)
        {
            if (s.Contains(name))
            {
                byte[]? data = s.Read(name);
                if (data is not null)
                {
                    return data;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a texture reference to the winning file name via the supercede chain
    /// (<c>.atx &gt; .dds &gt; .png &gt; .jpg &gt; .jpeg &gt; .vbm &gt; .tga</c>),
    /// searching all mounts. Returns null if nothing matches.
    /// </summary>
    public string? ResolveTexture(string textureName) =>
        SupercedeChain.Resolve(textureName, Exists);

    /// <summary>Resolves and decodes a texture (handling ATX via this VFS as the frame resolver).</summary>
    public DecodedTexture? LoadTexture(string textureName)
    {
        string? resolved = ResolveTexture(textureName);
        if (resolved is null)
        {
            return null;
        }

        byte[]? data = ReadFile(resolved);
        if (data is null)
        {
            return null;
        }

        if (resolved.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
        {
            AtxDescriptor atx = AtxDescriptor.Parse(System.Text.Encoding.Latin1.GetString(data));
            return AtxDecoder.Decode(atx, frame =>
            {
                string? r = ResolveTexture(frame);
                return r is null ? null : ReadFile(r);
            });
        }

        return TextureDecoder.Decode(resolved, data);
    }

    /// <summary>
    /// Loads a mesh by name. Accepts an explicit extension, or bare/.v3d names which
    /// are probed against the real mesh extensions (.v3m/.v3c/.vcm/.v3d).
    /// </summary>
    public V3dFile? LoadMesh(string meshName)
    {
        byte[]? data = ReadMeshBytes(meshName);
        return data is null ? null : V3dReader.Read(data);
    }

    /// <summary>Resolves and reads a mesh file's raw bytes, or null.</summary>
    public byte[]? ReadMeshBytes(string meshName)
    {
        if (string.IsNullOrEmpty(meshName))
        {
            return null;
        }

        if (Exists(meshName))
        {
            return ReadFile(meshName);
        }

        string baseName = StripExtension(meshName);
        foreach (string ext in MeshExtensions)
        {
            string candidate = baseName + ext;
            if (Exists(candidate))
            {
                return ReadFile(candidate);
            }
        }

        return null;
    }

    /// <summary>All bare texture file names across every mount (deduplicated, highest-priority wins).</summary>
    public IReadOnlyList<string> EnumerateTextures() => EnumerateByExtensions(SupercedeChain.Extensions);

    /// <summary>All bare mesh file names across every mount (deduplicated).</summary>
    public IReadOnlyList<string> EnumerateMeshes() => EnumerateByExtensions(MeshExtensions);

    /// <summary>
    /// Texture categories for the browser, in order: RED's stock categories built
    /// from the <c>maps*.txt</c> texture lists (see
    /// <see cref="TextureCategoryCatalog"/>), one category per category-labelled
    /// mount (e.g. "Custom - &lt;dir&gt;"), an "Uncategorized" bucket for textures
    /// no category claimed, and a synthesized "All" spanning every mount. Cached
    /// until <see cref="Rescan"/> or a mount change.
    /// </summary>
    public IReadOnlyList<AssetCategory> GetTextureCategories() =>
        _categoriesCache ??= TextureCategoryCatalog.Build(this);

    public void Rescan()
    {
        foreach (IAssetSource s in _sources)
        {
            s.Rescan();
        }

        _categoriesCache = null;
    }

    public void Dispose()
    {
        foreach (IAssetSource s in _sources)
        {
            if (s is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    private IReadOnlyList<string> EnumerateByExtensions(IReadOnlyCollection<string> extensions)
    {
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (IAssetSource s in _sources)
        {
            foreach (string f in s.EnumerateFiles())
            {
                if (extSet.Contains(Path.GetExtension(f)) && seen.Add(f))
                {
                    result.Add(f);
                }
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }
}
