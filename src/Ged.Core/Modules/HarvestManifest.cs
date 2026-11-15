using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ged.Core.Modules;

/// <summary>One manifest entry: files to harvest into a group.</summary>
public sealed class HarvestSourceEntry
{
    /// <summary>Glob patterns relative to the root (<c>*</c>, <c>?</c> and <c>[a-z]</c> classes in the file name only).</summary>
    public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();

    public string Group { get; init; } = "default";

    /// <summary>Packfiles (relative to the root) mounted with each matched level (their textures resolve).</summary>
    public IReadOnlyList<string> MountWith { get; init; } = Array.Empty<string>();

    /// <summary>A style family every module from these sources leans to.</summary>
    public string? StyleHint { get; init; }
}

/// <summary>One file matched by the manifest.</summary>
/// <param name="FullPath">The file (.rfl or .vpp).</param>
/// <param name="RelativePath">Relative to the root, '/'-separated.</param>
/// <param name="Entry">The entry that took it (first match wins).</param>
/// <param name="Order">Manifest order (entry index, then path).</param>
public sealed record HarvestSourceFile(string FullPath, string RelativePath, HarvestSourceEntry Entry, int Order);

/// <summary>
/// The harvest manifest (<c>library/sources.json</c>): data, never code — which level files and packfiles feed which
/// group, plus optional companion packfiles and style hints. Paths are relative to a root (default: the manifest's
/// repository root, i.e. the folder above <c>library/</c>).
/// </summary>
public sealed class HarvestManifest
{
    public const string Schema = "glacier.module-sources/1";

    public IReadOnlyList<HarvestSourceEntry> Sources { get; init; } = Array.Empty<HarvestSourceEntry>();

    /// <summary>The groups in manifest order (earlier groups own modules found in several).</summary>
    public IReadOnlyList<string> Groups => Sources.Select(s => s.Group).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Parses manifest JSON.</summary>
    public static HarvestManifest Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var list = new List<HarvestSourceEntry>();
        if (doc.RootElement.TryGetProperty("sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in sources.EnumerateArray())
            {
                string group = s.TryGetProperty("group", out JsonElement g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : "default";
                if (!IsValidGroup(group))
                {
                    throw new FormatException($"Manifest group '{group}' is not a valid folder name (letters, digits, '-' and '_').");
                }

                list.Add(new HarvestSourceEntry
                {
                    Include = StringOrArray(s, "include"),
                    Exclude = StringOrArray(s, "exclude"),
                    Group = group,
                    MountWith = StringOrArray(s, "mount_with"),
                    StyleHint = s.TryGetProperty("style_hint", out JsonElement h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
                });
            }
        }

        return new HarvestManifest { Sources = list };
    }

    /// <summary>A one-file manifest for an ad-hoc harvest of a level or packfile.</summary>
    public static HarvestManifest Single(string fileName, string group) =>
        new() { Sources = new[] { new HarvestSourceEntry { Include = new[] { fileName }, Group = group } } };

    /// <summary>True for a group usable as a folder name.</summary>
    public static bool IsValidGroup(string group) => Regex.IsMatch(group ?? string.Empty, "^[A-Za-z0-9_-]{1,64}$");

    /// <summary>
    /// The files the manifest matches under <paramref name="root"/>, in manifest order (each file once: the first
    /// entry that matches it takes it). Only .rfl and .vpp files are considered; nothing is opened.
    /// </summary>
    public List<HarvestSourceFile> Resolve(string root, List<string>? missing = null)
    {
        string full = Path.GetFullPath(root);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<HarvestSourceFile>();
        int order = 0;
        foreach (HarvestSourceEntry e in Sources)
        {
            var matched = new List<(string Full, string Rel)>();
            foreach (string pattern in e.Include)
            {
                List<(string, string)> hits = Glob(full, pattern);
                if (hits.Count == 0)
                {
                    missing?.Add(pattern);
                }

                matched.AddRange(hits);
            }

            var excluded = e.Exclude.SelectMany(x => Glob(full, x)).Select(x => x.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach ((string f, string rel) in matched.Distinct().OrderBy(m => m.Rel, StringComparer.Ordinal))
            {
                if (excluded.Contains(f) || !taken.Add(f))
                {
                    continue;
                }

                files.Add(new HarvestSourceFile(f, rel, e, order++));
            }
        }

        return files;
    }

    /// <summary>The folders holding the matched sources and their companion packfiles (never written to).</summary>
    public static List<string> SourceFolders(string root, IEnumerable<HarvestSourceFile> files)
    {
        var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HarvestSourceFile f in files)
        {
            dirs.Add(Path.GetDirectoryName(f.FullPath)!);
            foreach (string m in f.Entry.MountWith)
            {
                dirs.Add(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, m)))!);
            }
        }

        return dirs.ToList();
    }

    private static List<(string, string)> Glob(string root, string pattern)
    {
        string p = pattern.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(p))
        {
            p = Path.GetRelativePath(root, p).Replace('\\', '/');
        }

        int slash = p.LastIndexOf('/');
        string dirPart = slash < 0 ? string.Empty : p[..slash];
        string namePart = slash < 0 ? p : p[(slash + 1)..];
        string dir = Path.GetFullPath(Path.Combine(root, dirPart.Replace('/', Path.DirectorySeparatorChar)));
        var result = new List<(string, string)>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        var rx = new Regex("^" + GlobToRegex(namePart) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string f in Directory.EnumerateFiles(dir, "*", options))
        {
            string name = Path.GetFileName(f);
            string ext = Path.GetExtension(name);
            if (!rx.IsMatch(name) || !(ext.Equals(".rfl", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vpp", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add((Path.GetFullPath(f), Path.GetRelativePath(root, f).Replace('\\', '/')));
        }

        return result;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    int close = glob.IndexOf(']', i + 1);
                    if (close > i + 1)
                    {
                        sb.Append('[').Append(Regex.Escape(glob[(i + 1)..close]).Replace("\\-", "-", StringComparison.Ordinal)).Append(']');
                        i = close;
                    }
                    else
                    {
                        sb.Append("\\[");
                    }

                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }

        return sb.ToString();
    }

    private static List<string> StringOrArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v))
        {
            return new List<string>();
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => new List<string> { v.GetString()! },
            JsonValueKind.Array => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
            _ => new List<string>(),
        };
    }
}
