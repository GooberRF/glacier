using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Editing;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>One built library module: its description plus its local-frame brushes.</summary>
public sealed record BuiltModule(ModuleInfo Info, IReadOnlyList<Brush> Brushes);

/// <summary>
/// Turns harvested occurrences into the library: de-duplication (exact key, then near-duplicate key; mirror images
/// fold into one; across groups the earlier group in manifest order keeps the module and lists every provenance),
/// the representative (highest single-occurrence score; ties by source path, then position), the final score
/// (occurrences on a log scale + contact + member sweet spot + texture validity + classification confidence),
/// ids, suggested pitch, and the deterministic files (<c>index.json</c> + one .rfg per module).
/// </summary>
public static class ModuleLibraryBuilder
{
    /// <summary>The .rfg format version modules are written in (stock RED reads it).</summary>
    public const int RfgVersion = 0xC8;

    /// <summary>De-duplicates and scores <paramref name="harvested"/>; <paramref name="groupOrder"/> decides cross-group ownership.</summary>
    public static List<BuiltModule> Build(IReadOnlyList<(string Group, HarvestedModule Module)> harvested, IReadOnlyList<string> groupOrder, HarvestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(harvested);
        HarvestOptions o = options ?? new HarvestOptions();
        int n = harvested.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        foreach (var g in Enumerable.Range(0, n).GroupBy(i => harvested[i].Module.ExactKey, StringComparer.Ordinal))
        {
            int first = g.First();
            foreach (int i in g.Skip(1))
            {
                Union(first, i);
            }
        }

        foreach (var g in Enumerable.Range(0, n).GroupBy(i => harvested[i].Module.NearKey, StringComparer.Ordinal))
        {
            int first = g.First();
            foreach (int i in g.Skip(1))
            {
                Union(first, i);
            }
        }

        int GroupRank(string group)
        {
            int r = -1;
            for (int i = 0; i < groupOrder.Count; i++)
            {
                if (string.Equals(groupOrder[i], group, StringComparison.Ordinal))
                {
                    r = i;
                    break;
                }
            }

            return r < 0 ? int.MaxValue : r;
        }

        var built = new List<BuiltModule>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clusters = Enumerable.Range(0, n).GroupBy(Find).Select(g => g.ToList()).ToList();

        // Deterministic cluster order: by the representative's key so ids never depend on enumeration order.
        var ordered = clusters.Select(c =>
        {
            int ownerRank = c.Min(i => GroupRank(harvested[i].Group));
            int rep = c.Where(i => GroupRank(harvested[i].Group) == ownerRank)
                .OrderByDescending(i => harvested[i].Module.BaseScore)
                .ThenBy(i => harvested[i].Module.Provenance.Level, StringComparer.Ordinal)
                .ThenBy(i => harvested[i].Module.Provenance.Position.X)
                .ThenBy(i => harvested[i].Module.Provenance.Position.Y)
                .ThenBy(i => harvested[i].Module.Provenance.Position.Z)
                .First();
            return (Cluster: c, Rep: rep);
        })
        .OrderBy(x => harvested[x.Rep].Module.ExactKey, StringComparer.Ordinal)
        .ThenBy(x => harvested[x.Rep].Module.Provenance.Level, StringComparer.Ordinal)
        .ToList();

        foreach ((List<int> cluster, int rep) in ordered)
        {
            HarvestedModule r = harvested[rep].Module;
            string group = harvested[rep].Group;
            int occurrences = cluster.Count;
            var others = cluster.Where(i => i != rep)
                .Select(i => harvested[i].Module.Provenance)
                .OrderBy(p => p.Level, StringComparer.Ordinal)
                .ThenBy(p => p.Position.X).ThenBy(p => p.Position.Y).ThenBy(p => p.Position.Z)
                .ToList();
            var provenance = new List<ModuleProvenance> { r.Provenance };
            provenance.AddRange(others.Take(Math.Max(0, o.MaxProvenance - 1)));

            double occ = Math.Min(1.0, Math.Log(1 + occurrences) / Math.Log(1 + 20));
            double score = Math.Round(Math.Clamp((0.30 * occ) + r.BaseScore, 0, 1), 4);

            string hash = ModuleShape.Hex(r.ExactKey);
            string id = $"{r.Info.Category}_{hash[..8]}";
            for (int len = 10; !usedIds.Add(id) && len <= hash.Length; len += 2)
            {
                id = $"{r.Info.Category}_{hash[..len]}";
            }

            ModuleInfo i0 = r.Info;
            var info = new ModuleInfo
            {
                Id = id,
                Group = group,
                Category = i0.Category,
                Role = i0.Role,
                Attach = i0.Attach,
                Styles = i0.Styles,
                Size = i0.Size,
                BoundsMin = i0.BoundsMin,
                BoundsMax = i0.BoundsMax,
                StretchX = i0.StretchX,
                StretchY = i0.StretchY,
                StretchZ = i0.StretchZ,
                Tiles = i0.Tiles,
                Textures = i0.Textures,
                Brushes = i0.Brushes,
                Faces = i0.Faces,
                HasAir = i0.HasAir,
                RecessDepth = i0.RecessDepth,
                MountHeight = i0.MountHeight,
                CeilingGap = i0.CeilingGap,
                Opening = i0.Opening,
                Score = score,
                Occurrences = occurrences,
                SuggestedPitch = SuggestedPitch(cluster.Select(i => harvested[i].Module.Provenance).ToList()),
                Provenance = provenance,
                File = $"{i0.Category}/{id}.rfg",
            };
            built.Add(new BuiltModule(info, r.LocalBrushes));
        }

        return built
            .OrderBy(b => GroupRank(b.Info.Group))
            .ThenBy(b => b.Info.Category, StringComparer.Ordinal)
            .ThenBy(b => b.Info.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The median spacing between recurrences that line up along the module's local X in one level (same yaw, same
    /// local y and z within 0.05 m), when at least three such recurrences exist; else null.
    /// </summary>
    internal static double? SuggestedPitch(IReadOnlyList<ModuleProvenance> occurrences)
    {
        var spacings = new List<double>();
        foreach (var level in occurrences.GroupBy(p => p.Level, StringComparer.Ordinal))
        {
            foreach (var byYaw in level.GroupBy(p => (long)Math.Round(YawFrame.Normalize(p.Yaw) % 180.0)))
            {
                var rows = byYaw
                    .Select(p => (P: p, L: YawFrame.Rotate(p.Position, -p.Yaw)))
                    .GroupBy(x => ((long)Math.Round(x.L.Y / 0.1), (long)Math.Round(x.L.Z / 0.1)));
                foreach (var row in rows)
                {
                    var xs = row.Select(x => x.L.X).OrderBy(x => x).ToList();
                    if (xs.Count < 3)
                    {
                        continue;
                    }

                    for (int i = 1; i < xs.Count; i++)
                    {
                        double d = xs[i] - xs[i - 1];
                        if (d > 0.05)
                        {
                            spacings.Add(d);
                        }
                    }
                }
            }
        }

        return spacings.Count == 0 ? null : ModuleJson.R(ModuleFraming.Median(spacings));
    }

    /// <summary>
    /// Writes the library under <paramref name="outDir"/> (<c>&lt;group&gt;/index.json</c> and
    /// <c>&lt;group&gt;/&lt;category&gt;/&lt;id&gt;.rfg</c>). Files a previous index of the same group listed are removed
    /// first, so a re-harvest leaves no stale modules. Returns the paths written.
    /// </summary>
    public static List<string> Write(string outDir, IReadOnlyList<BuiltModule> modules, IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(outDir);
        string root = Path.GetFullPath(outDir);
        Directory.CreateDirectory(root);
        var written = new List<string>();
        foreach (string group in groups)
        {
            string gdir = Path.Combine(root, group);
            RemovePrevious(gdir);
            var mine = modules.Where(m => m.Info.Group == group).ToList();
            Directory.CreateDirectory(gdir);
            foreach (BuiltModule m in mine)
            {
                string path = Path.Combine(gdir, m.Info.File.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, ToRfg(m).Save());
                written.Add(path);
            }

            string index = Path.Combine(gdir, "index.json");
            File.WriteAllText(index, ModuleJson.WriteIndex(group, mine.Select(m => m.Info).ToList()), new UTF8Encoding(false));
            written.Add(index);
        }

        return written;
    }

    /// <summary>The module's .rfg: one group named after the module holding its brushes (identity rotation, local frame).</summary>
    public static RfgFile ToRfg(BuiltModule m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var file = new RfgFile { Version = RfgVersion };
        var group = new RfgGroup { Name = m.Info.Id };
        foreach (Brush b in m.Brushes)
        {
            group.Brushes.Brushes.Add(GeometryClone.Deep(b));
        }

        file.Groups.Add(group);
        return file;
    }

    private static void RemovePrevious(string gdir)
    {
        string index = Path.Combine(gdir, "index.json");
        if (!File.Exists(index))
        {
            return;
        }

        string full = Path.GetFullPath(gdir) + Path.DirectorySeparatorChar;
        try
        {
            foreach (ModuleInfo m in ModuleJson.ReadIndex(File.ReadAllText(index), gdir))
            {
                if (m.RfgPath is { } p && p.StartsWith(full, StringComparison.OrdinalIgnoreCase)
                    && p.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                {
                    File.Delete(p);
                    string? dir = Path.GetDirectoryName(p);
                    if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // An unreadable old index is simply replaced.
        }

        File.Delete(index);
    }
}
