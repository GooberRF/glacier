using Ged.Core.IO.Tex;

namespace Ged.Core.Assets;

/// <summary>
/// Builds the texture browser's category list the way RED.exe does: the stock
/// categories are RED's built-in (display name, dev-tree directory) table, and
/// their membership is driven by the <c>maps*.txt</c> texture-list files shipped
/// in <c>tables.vpp</c> (plus Alpine's <c>maps_af.txt</c> from
/// <c>alpinefaction.vpp</c> when present). See
/// docs/research/red-texture-categories.md for the reverse-engineered mechanism.
/// </summary>
/// <remarks>
/// RED reads a NULL-terminated file-name array (<c>maps.txt</c> …
/// <c>maps4.txt</c>; Alpine patches it to append <c>maps_af.txt</c>), collects
/// one full dev-tree path per line, groups the paths by their exact directory,
/// and shows a stock browser folder's textures by matching the folder's
/// configured directory against a group (case-insensitive equality). GED
/// reproduces that pipeline over its VFS: list entries resolve through the
/// supercede chain to the winning file, empty stock categories are omitted, the
/// existing "Custom - &lt;dir&gt;" mount categories follow, then an
/// "Uncategorized" bucket for VFS textures no category claimed, then "All".
/// </remarks>
public static class TextureCategoryCatalog
{
    /// <summary>
    /// The texture-list files consulted, in RED's scan order (RED.exe array at
    /// 0x00575674) plus Alpine's <c>maps_af.txt</c> appended the way the Alpine
    /// editor patch does. Missing files are skipped.
    /// </summary>
    public static readonly IReadOnlyList<string> StockListFiles = new[]
    {
        "maps.txt", "maps1.txt", "maps2.txt", "maps3.txt", "maps4.txt", "maps_af.txt",
    };

    /// <summary>Name of the fallback category holding textures no other category claimed.</summary>
    public const string UncategorizedName = "Uncategorized";

    /// <summary>Name of the synthesized category spanning every mount.</summary>
    public const string AllName = "All";

    /// <summary>
    /// RED's built-in stock categories in its initialization order: display name +
    /// the dev-tree directory whose listed textures belong to it (recovered from
    /// RED.exe's default category init, FUN_004778e0 / string table at
    /// 0x0057ae14-0x0057b59f). RED's "Missing" (its missing-texture bucket) and
    /// "Custom" (<c>user_maps\textures</c>, which GED mounts directly) entries are
    /// intentionally not reproduced here.
    /// </summary>
    private static readonly (string Name, string Path)[] StockCategories =
    {
        ("Animating", @"data\maps\textures\anim"),
        ("Blends", @"data\maps\textures\blends"),
        ("Ceiling - Cement", @"data\maps\textures\ceiling\cement"),
        ("Ceiling - Metal", @"data\maps\textures\ceiling\metal"),
        ("Ceiling - Misc", @"data\maps\textures\ceiling\misc"),
        ("Ceiling - Rock", @"data\maps\textures\ceiling\rock"),
        ("Crates", @"data\maps\textures\crates"),
        ("Damage", @"data\maps\textures\damage"),
        ("Doors", @"data\maps\textures\doors"),
        ("Effects", @"data\maps\textures\fx"),
        ("Floor - Cement", @"data\maps\textures\floor\cement"),
        ("Floor - Metal", @"data\maps\textures\floor\metal"),
        ("Floor - Misc", @"data\maps\textures\floor\misc"),
        ("Floor - Rock", @"data\maps\textures\floor\rock"),
        ("Glass", @"data\maps\textures\glass"),
        ("Gore", @"data\maps\textures\gore"),
        ("Grating", @"data\maps\textures\grating"),
        ("Lights", @"data\maps\textures\lights"),
        ("Liquids", @"data\maps\textures\liquids"),
        ("Pipes", @"data\maps\textures\pipes"),
        ("Plants", @"data\maps\textures\plants"),
        ("Root", @"data\maps\textures"),
        ("Signs - Admin", @"data\maps\textures\signs\areas\admin"),
        ("Signs - Art", @"data\maps\textures\signs\art"),
        ("Signs - CTF Logos", @"data\maps\textures\signs\ctf_logos"),
        ("Signs - Depth", @"data\maps\textures\signs\depth"),
        ("Signs - Directives", @"data\maps\textures\signs\directives"),
        ("Signs - Enter", @"data\maps\textures\signs\directives\enter"),
        ("Signs - Industrial", @"data\maps\textures\signs\areas\industrial"),
        ("Signs - Propaganda", @"data\maps\textures\signs\propaganda"),
        ("Signs - Restricted", @"data\maps\textures\signs\directives\restricted"),
        ("Signs - Wanted", @"data\maps\textures\signs\wanted"),
        ("Signs - Warnings", @"data\maps\textures\signs\directives\warnings"),
        ("Sky", @"data\maps\textures\sky"),
        ("Supports", @"data\maps\textures\supports"),
        ("Tech", @"data\maps\textures\tech"),
        ("Test", @"data\maps\test"),
        ("Text - White", @"data\maps\textures\text\white"),
        ("Trim", @"data\maps\textures\trim"),
        ("Wall - Cement", @"data\maps\textures\wall\cement"),
        ("Wall - Metal", @"data\maps\textures\wall\metal"),
        ("Wall - Misc", @"data\maps\textures\wall\misc"),
        ("Wall - Rock", @"data\maps\textures\wall\rock"),
    };

    /// <summary>
    /// Builds the ordered browser categories for <paramref name="vfs"/>: stock
    /// categories (RED order, empty ones omitted), then the category-labelled
    /// mounts ("Custom - &lt;dir&gt;", "Custom") in mount order, then
    /// <see cref="UncategorizedName"/> (when non-empty), then <see cref="AllName"/>.
    /// </summary>
    public static IReadOnlyList<AssetCategory> Build(AssetVfs vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        var categories = new List<AssetCategory>();

        // Names any category claims (including every supercede sibling of a
        // resolved list entry) so "Uncategorized" holds only the leftovers.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Canonical stored casing per file name: list entries are matched
        // case-insensitively but categories display the VFS's real name.
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in vfs.EnumerateTextures())
        {
            canonical.TryAdd(file, file);
        }

        categories.AddRange(BuildStockCategories(vfs, claimed, canonical));
        categories.AddRange(BuildSourceCategories(vfs, claimed));

        var uncategorized = vfs.EnumerateTextures().Where(f => !claimed.Contains(f)).ToList();
        if (uncategorized.Count > 0)
        {
            categories.Add(new AssetCategory(UncategorizedName, uncategorized));
        }

        categories.Add(new AssetCategory(AllName, vfs.EnumerateTextures()));
        return categories;
    }

    private static IEnumerable<AssetCategory> BuildStockCategories(
        AssetVfs vfs, HashSet<string> claimed, Dictionary<string, string> canonical)
    {
        // Directory -> stock-category index (case-insensitive exact match, as in RED).
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < StockCategories.Length; i++)
        {
            byPath[StockCategories[i].Path] = i;
        }

        var members = new List<string>?[StockCategories.Length];
        var seen = new HashSet<string>?[StockCategories.Length];

        foreach (string listFile in StockListFiles)
        {
            byte[]? data = vfs.ReadFile(listFile);
            if (data is null)
            {
                continue;
            }

            foreach ((string directory, string fileName) in ParseListEntries(data))
            {
                if (!byPath.TryGetValue(directory, out int cat))
                {
                    continue; // listed under a directory RED has no folder for (e.g. data\maps\skins)
                }

                // Resolve through the supercede chain so a listed .tga matches a
                // superseding .dds/.png/… override; skip names absent from the VFS.
                string? resolved = vfs.ResolveTexture(fileName);
                if (resolved is null)
                {
                    continue;
                }

                if (canonical.TryGetValue(resolved, out string? stored))
                {
                    resolved = stored;
                }

                seen[cat] ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (seen[cat]!.Add(resolved))
                {
                    (members[cat] ??= new List<string>()).Add(resolved);

                    // Claim every supercede sibling so a shadowed wall.tga does not
                    // leak into "Uncategorized" when wall.dds won the resolution.
                    foreach (string candidate in SupercedeChain.Candidates(fileName))
                    {
                        claimed.Add(candidate);
                    }
                }
            }
        }

        for (int i = 0; i < StockCategories.Length; i++)
        {
            if (members[i] is { Count: > 0 } files)
            {
                files.Sort(StringComparer.OrdinalIgnoreCase);
                yield return new AssetCategory(StockCategories[i].Name, files);
            }
        }
    }

    private static IEnumerable<AssetCategory> BuildSourceCategories(AssetVfs vfs, HashSet<string> claimed)
    {
        var extSet = new HashSet<string>(SupercedeChain.Extensions, StringComparer.OrdinalIgnoreCase);
        foreach (IAssetSource s in vfs.Sources)
        {
            if (s.Category is null)
            {
                continue;
            }

            var files = s.EnumerateFiles()
                .Where(f => extSet.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            claimed.UnionWith(files);
            yield return new AssetCategory(s.Category, files);
        }
    }

    /// <summary>
    /// Parses a texture-list file: one dev-tree path per line
    /// (<c>data\maps\textures\crates\crate01.tga</c>). Tolerates blank lines,
    /// surrounding whitespace, <c>//</c>/<c>#</c>/<c>;</c> comment lines, and
    /// forward slashes; skips "-mip" LOD variants like RED's browser validator.
    /// Yields (directory, bare file name) pairs with the directory normalized to
    /// backslashes with no trailing separator.
    /// </summary>
    public static IEnumerable<(string Directory, string FileName)> ParseListEntries(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string text = System.Text.Encoding.Latin1.GetString(data);

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith('#') ||
                line.StartsWith(';'))
            {
                continue;
            }

            line = line.Replace('/', '\\');
            int sep = line.LastIndexOf('\\');
            string directory = sep >= 0 ? line[..sep].TrimEnd('\\') : string.Empty;
            string fileName = sep >= 0 ? line[(sep + 1)..] : line;
            if (fileName.Length == 0)
            {
                continue;
            }

            // RED's texture-browser validator (0x00470330) hides "-mip" LOD variants.
            if (fileName.Contains("-mip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return (directory, fileName);
        }
    }
}
