using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ged.Core.Assets;

/// <summary>Where one instance of a shadowed/duplicated file lives.</summary>
public sealed record HealthLocation(string SourceDescription, AssetSourceKind Kind, string? LoosePath, long Size)
{
    public bool IsPackfile => Kind == AssetSourceKind.Packfile;

    public string Origin => LoosePath ?? $"{SourceDescription} (packfile)";
}

/// <summary>
/// A file name present in more than one mount. The winner (highest priority) is
/// first; the rest are shadowed and never resolve. This is the classic "I edited
/// a texture but the game keeps loading the old one" trap.
/// </summary>
public sealed record ShadowedName(string Name, IReadOnlyList<HealthLocation> Mounts)
{
    public HealthLocation Winner => Mounts[0];

    public IEnumerable<HealthLocation> Shadowed => Mounts.Skip(1);
}

/// <summary>Two or more differently-named files with byte-identical content.</summary>
public sealed record ContentDuplicate(string ContentHash, IReadOnlyList<(string Name, HealthLocation Location)> Files);

/// <summary>The result of a library health scan: name collisions + content duplicates.</summary>
public sealed class LibraryHealthReport
{
    public LibraryHealthReport(
        IReadOnlyList<ShadowedName> shadowed,
        IReadOnlyList<ContentDuplicate> duplicates,
        int filesScanned)
    {
        Shadowed = shadowed;
        Duplicates = duplicates;
        FilesScanned = filesScanned;
    }

    /// <summary>Names present in more than one mount (winner first).</summary>
    public IReadOnlyList<ShadowedName> Shadowed { get; }

    /// <summary>Groups of identical-content, different-name files.</summary>
    public IReadOnlyList<ContentDuplicate> Duplicates { get; }

    public int FilesScanned { get; }

    public bool IsHealthy => Shadowed.Count == 0 && Duplicates.Count == 0;

    /// <summary>A human-readable, actionable dump (used for the panel text and the test artifact).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Library Health Report");
        sb.AppendLine("=====================");
        sb.AppendLine($"Files scanned: {FilesScanned}");
        sb.AppendLine($"Name collisions (shadowing): {Shadowed.Count}");
        sb.AppendLine($"Identical-content duplicates: {Duplicates.Count}");
        sb.AppendLine();

        sb.AppendLine("-- Name collisions (same name in multiple mounts) --");
        if (Shadowed.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (ShadowedName s in Shadowed)
            {
                sb.AppendLine($"* {s.Name}");
                sb.AppendLine($"    WINS  : {s.Winner.Origin} ({s.Winner.Size} bytes)");
                foreach (HealthLocation loc in s.Shadowed)
                {
                    sb.AppendLine($"    shadow: {loc.Origin} ({loc.Size} bytes)");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("-- Identical-content duplicates (different names, same bytes) --");
        if (Duplicates.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (ContentDuplicate d in Duplicates)
            {
                sb.AppendLine($"* hash {d.ContentHash[..16]}... ({d.Files.Count} files)");
                foreach ((string name, HealthLocation loc) in d.Files)
                {
                    sb.AppendLine($"    {name} <- {loc.Origin} ({loc.Size} bytes)");
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Scans the mounted asset library for the two collaboration hazards Natalie hit:
/// (a) the same file name living in more than one mount (only the highest-priority
/// copy wins — the rest silently shadow), and (b) byte-identical files under
/// different names (wasted space / confusion). Content identity is a BCL SHA-256
/// over the raw file bytes.
/// </summary>
public static class LibraryHealth
{
    /// <summary>
    /// Analyzes the mounts. <paramref name="names"/> scopes the scan (e.g. just the
    /// texture set); null scans every file across every mount.
    /// </summary>
    public static LibraryHealthReport Analyze(AssetVfs vfs, IEnumerable<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        // Distinct file names across all mounts (or the caller's scope).
        IReadOnlyList<string> scope = (names ?? vfs.Sources.SelectMany(s => s.EnumerateFiles()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shadowed = new List<ShadowedName>();
        var hashToFiles = new Dictionary<string, List<(string Name, HealthLocation Location)>>(StringComparer.Ordinal);
        int scanned = 0;

        foreach (string name in scope)
        {
            IReadOnlyList<IAssetSource> sources = vfs.FindAllSources(name);
            if (sources.Count == 0)
            {
                continue;
            }

            scanned++;
            var mounts = sources.Select(s => Locate(s, name)).ToList();
            if (mounts.Count > 1)
            {
                shadowed.Add(new ShadowedName(name, mounts));
            }

            // Hash the winning (highest-priority) copy for content-duplicate detection.
            byte[]? data = vfs.ReadFile(name);
            if (data is not null)
            {
                string hash = Convert.ToHexString(SHA256.HashData(data));
                if (!hashToFiles.TryGetValue(hash, out List<(string, HealthLocation)>? list))
                {
                    list = new List<(string, HealthLocation)>();
                    hashToFiles[hash] = list;
                }

                list.Add((name, mounts[0]));
            }
        }

        var duplicates = hashToFiles
            .Where(kv => kv.Value.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(kv => new ContentDuplicate(kv.Key, kv.Value))
            .OrderByDescending(d => d.Files.Count)
            .ToList();

        return new LibraryHealthReport(shadowed, duplicates, scanned);
    }

    private static HealthLocation Locate(IAssetSource src, string name)
    {
        string? loose = src is DirectoryAssetSource d ? d.GetFullPath(name) : null;
        return new HealthLocation(src.Description, src.Kind, loose, src.GetSize(name) ?? 0);
    }
}
