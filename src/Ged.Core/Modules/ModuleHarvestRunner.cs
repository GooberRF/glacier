using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Analysis;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Core.IO.Vpp;

namespace Ged.Core.Modules;

/// <summary>A harvest that cannot start (bad manifest, output inside a source folder, ...).</summary>
public sealed class ModuleHarvestException : Exception
{
    public ModuleHarvestException(string message)
        : base(message)
    {
    }

    public ModuleHarvestException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Settings for one harvest run.</summary>
public sealed class ModuleHarvestRequest
{
    /// <summary>The manifest (see <see cref="HarvestManifest"/>).</summary>
    public required HarvestManifest Manifest { get; init; }

    /// <summary>The folder manifest paths are relative to.</summary>
    public required string Root { get; init; }

    /// <summary>The library output folder (<c>…/library/modules</c>).</summary>
    public required string OutputDir { get; init; }

    /// <summary>Only harvest this group (null = every group).</summary>
    public string? Group { get; init; }

    /// <summary>The Red Faction install (stock textures, categories); null = none.</summary>
    public string? RfInstall { get; init; }

    public int Jobs { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    public HarvestOptions Options { get; init; } = new();

    /// <summary>Progress lines.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>The outcome of a harvest (also written, without timing, to <c>harvest-report.json</c>).</summary>
public sealed class ModuleHarvestReport
{
    public int LevelsRead { get; init; }

    public IReadOnlyList<(string Level, string Message)> Failures { get; init; } = Array.Empty<(string, string)>();

    public int Brushes { get; init; }

    public int Candidates { get; init; }

    public int Components { get; init; }

    public int Occurrences { get; init; }

    public IReadOnlyDictionary<string, int> Rejected { get; init; } = new SortedDictionary<string, int>();

    public IReadOnlyList<BuiltModule> Modules { get; init; } = Array.Empty<BuiltModule>();

    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingPatterns { get; init; } = Array.Empty<string>();

    public string OutputDir { get; init; } = string.Empty;

    public bool RfInstallMounted { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>How many entries the library's <c>curation.json</c> holds (it is never written by a harvest).</summary>
    public int CurationEntries { get; init; }

    /// <summary>Ids <c>curation.json</c> names that the harvested library no longer holds (sorted).</summary>
    public IReadOnlyList<string> CurationUnknownIds { get; init; } = Array.Empty<string>();

    /// <summary>Modules per category × group.</summary>
    public SortedDictionary<string, SortedDictionary<string, int>> ByCategoryAndGroup()
    {
        var t = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (BuiltModule m in Modules)
        {
            if (!t.TryGetValue(m.Info.Category, out SortedDictionary<string, int>? row))
            {
                t[m.Info.Category] = row = new SortedDictionary<string, int>(StringComparer.Ordinal);
            }

            row[m.Info.Group] = row.GetValueOrDefault(m.Info.Group) + 1;
        }

        return t;
    }

    /// <summary>Modules per primary style × group.</summary>
    public SortedDictionary<string, SortedDictionary<string, int>> ByStyleAndGroup()
    {
        var t = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (BuiltModule m in Modules)
        {
            if (!t.TryGetValue(m.Info.PrimaryStyle, out SortedDictionary<string, int>? row))
            {
                t[m.Info.PrimaryStyle] = row = new SortedDictionary<string, int>(StringComparer.Ordinal);
            }

            row[m.Info.Group] = row.GetValueOrDefault(m.Info.Group) + 1;
        }

        return t;
    }

    /// <summary>The deterministic report JSON (no timing, no machine paths).</summary>
    public string ToJson(bool includeTiming = false)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "glacier.module-harvest-report/1");
            w.WriteNumber("levels_read", LevelsRead);
            w.WriteNumber("levels_failed", Failures.Count);
            w.WriteStartArray("failures");
            foreach ((string level, string message) in Failures)
            {
                w.WriteStartObject();
                w.WriteString("level", level);
                w.WriteString("message", message);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteBoolean("rf_install_mounted", RfInstallMounted);
            w.WriteNumber("brushes", Brushes);
            w.WriteNumber("candidates", Candidates);
            w.WriteNumber("components", Components);
            w.WriteNumber("occurrences_kept", Occurrences);
            w.WriteNumber("modules", Modules.Count);
            w.WriteNumber("custom_texture_modules", Modules.Count(m => m.Info.CustomTextures));
            w.WriteStartObject("rejected");
            foreach ((string k, int v) in Rejected)
            {
                w.WriteNumber(k, v);
            }

            w.WriteEndObject();
            WriteTable(w, "by_group", Modules.GroupBy(m => m.Info.Group).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
            WriteNested(w, "by_category_group", ByCategoryAndGroup());
            WriteNested(w, "by_style_group", ByStyleAndGroup());
            w.WriteStartArray("missing_patterns");
            foreach (string p in MissingPatterns)
            {
                w.WriteStringValue(p);
            }

            w.WriteEndArray();
            w.WriteNumber("curation_entries", CurationEntries);
            w.WriteStartArray("curation_unknown_ids");
            foreach (string id in CurationUnknownIds)
            {
                w.WriteStringValue(id);
            }

            w.WriteEndArray();
            if (includeTiming)
            {
                w.WriteNumber("elapsed_s", Math.Round(Elapsed.TotalSeconds, 1));
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static void WriteTable(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, int> t)
    {
        w.WriteStartObject(name);
        foreach ((string k, int v) in t.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(k, v);
        }

        w.WriteEndObject();
    }

    private static void WriteNested(Utf8JsonWriter w, string name, SortedDictionary<string, SortedDictionary<string, int>> t)
    {
        w.WriteStartObject(name);
        foreach ((string k, SortedDictionary<string, int> row) in t)
        {
            WriteTable(w, k, row);
        }

        w.WriteEndObject();
    }

    /// <summary>A readable summary (several lines).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"Harvested {LevelsRead} level(s) ({Failures.Count} failed) in {Elapsed.TotalSeconds:0.0} s{(RfInstallMounted ? string.Empty : " (no RF install: stock textures unknown)")}.");
        sb.AppendLine(inv, $"Brushes {Brushes}, candidates {Candidates}, components {Components}, kept occurrences {Occurrences}, modules {Modules.Count} ({Modules.Count(m => m.Info.CustomTextures)} with custom textures).");
        if (Rejected.Count > 0)
        {
            sb.AppendLine("Rejected: " + string.Join(", ", Rejected.Select(kv => $"{kv.Key} {kv.Value}")));
        }

        List<string> groups = Groups.ToList();
        sb.AppendLine();
        sb.AppendLine("Modules by category" + string.Concat(groups.Select(g => " | " + g)) + " | total");
        foreach ((string cat, SortedDictionary<string, int> row) in ByCategoryAndGroup())
        {
            sb.Append(inv, $"  {cat,-15}");
            foreach (string g in groups)
            {
                sb.Append(inv, $" | {row.GetValueOrDefault(g),5}");
            }

            sb.AppendLine(inv, $" | {row.Values.Sum(),5}");
        }

        sb.AppendLine();
        sb.AppendLine("Modules by primary style" + string.Concat(groups.Select(g => " | " + g)));
        foreach ((string st, SortedDictionary<string, int> row) in ByStyleAndGroup())
        {
            sb.Append(inv, $"  {st,-15}");
            foreach (string g in groups)
            {
                sb.Append(inv, $" | {row.GetValueOrDefault(g),5}");
            }

            sb.AppendLine();
        }

        foreach ((string level, string message) in Failures)
        {
            sb.AppendLine(inv, $"failed: {level}: {message}");
        }

        foreach (string p in MissingPatterns)
        {
            sb.AppendLine(inv, $"warning: no file matches {p}");
        }

        if (CurationEntries > 0 || CurationUnknownIds.Count > 0)
        {
            sb.AppendLine(inv, $"curation.json kept as is ({CurationEntries} entr{(CurationEntries == 1 ? "y" : "ies")}).");
        }

        if (CurationUnknownIds.Count > 0)
        {
            sb.AppendLine(inv, $"warning: curation.json names {CurationUnknownIds.Count} module(s) this library no longer holds (ignored): {string.Join(", ", CurationUnknownIds)}");
        }

        sb.AppendLine(inv, $"Library: {OutputDir}");
        return sb.ToString();
    }
}

/// <summary>
/// Runs a harvest: resolves the manifest, refuses an output folder that is, contains, or lies inside a source folder,
/// reads every level READ-ONLY (loose .rfl, or each .rfl inside a .vpp; streams opened for reading only), mounts an
/// asset library per level (stock packfiles of the RF install + the level's own packfile + its companions) so texture
/// names resolve, harvests levels in parallel, then builds and writes the library deterministically.
/// </summary>
public static class ModuleHarvestRunner
{
    /// <summary>Throws when <paramref name="outputDir"/> equals, contains, or lies inside one of <paramref name="sourceFolders"/>.</summary>
    public static void GuardOutput(string outputDir, IEnumerable<string> sourceFolders)
    {
        string o = Norm(outputDir);
        foreach (string s in sourceFolders)
        {
            string f = Norm(s);
            if (string.Equals(o, f, StringComparison.OrdinalIgnoreCase)
                || o.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || f.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModuleHarvestException($"Refusing to write the module library to '{o}': it is, contains, or lies inside the source folder '{f}' (sources are read-only).");
            }
        }
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record LevelJob(int Order, HarvestSourceFile Source, string? Entry);

    /// <summary>Runs the harvest and writes the library + <c>harvest-report.json</c>.</summary>
    public static ModuleHarvestReport Run(ModuleHarvestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        string root = Path.GetFullPath(request.Root);
        var missing = new List<string>();
        List<HarvestSourceFile> files = request.Manifest.Resolve(root, missing)
            .Where(f => request.Group is null || string.Equals(f.Entry.Group, request.Group, StringComparison.Ordinal))
            .ToList();
        GuardOutput(request.OutputDir, HarvestManifest.SourceFolders(root, files));
        List<string> groups = request.Manifest.Groups.Where(g => request.Group is null || g == request.Group).ToList();

        // The stock asset library (retail + Alpine packfiles only; never loose user content).
        using StockAssets stock = StockAssets.Open(request.RfInstall, request.Log);

        // Enumerate levels (packfile entries in name order).
        var jobs = new List<LevelJob>();
        var failures = new ConcurrentBag<(int, string, string)>();
        foreach (HarvestSourceFile f in files)
        {
            if (f.FullPath.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase))
            {
                jobs.Add(new LevelJob(jobs.Count, f, null));
                continue;
            }

            try
            {
                using VppArchive vpp = VppArchive.Open(f.FullPath);
                foreach (VppEntry e in vpp.Entries
                    .Where(e => e.Name.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) && !LevelIdentity.IsAutosaveName(e.Name))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    jobs.Add(new LevelJob(jobs.Count, f, e.Name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException or ArgumentException or InvalidDataException)
            {
                failures.Add((int.MaxValue, f.RelativePath, "packfile could not be opened: " + ex.Message));
            }
        }

        request.Log?.Invoke($"{jobs.Count} level(s) in {files.Count} source file(s).");
        var results = new LevelHarvestResult?[jobs.Count];
        int done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, request.Jobs), CancellationToken = ct }, job =>
        {
            string level = job.Entry is null ? job.Source.RelativePath : job.Source.RelativePath + "|" + job.Entry;
            var levelClock = Stopwatch.StartNew();
            try
            {
                results[job.Order] = HarvestOne(job, level, root, stock, request, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add((job.Order, level, $"{ex.GetType().Name}: {ex.Message}"));
            }

            int n = Interlocked.Increment(ref done);
            request.Log?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{n}/{jobs.Count}] {level}: {results[job.Order]?.Modules.Count ?? 0} module occurrence(s) in {levelClock.Elapsed.TotalSeconds:0.0} s"));
        });

        var harvested = new List<(string Group, HarvestedModule Module)>();
        var rejected = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int brushes = 0, candidates = 0, components = 0, read = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            if (results[i] is not { } r)
            {
                continue;
            }

            read++;
            brushes += r.Brushes;
            candidates += r.Candidates;
            components += r.Components;
            foreach ((string k, int v) in r.Rejected)
            {
                rejected[k] = rejected.GetValueOrDefault(k) + v;
            }

            foreach (HarvestedModule m in r.Modules)
            {
                m.SourceOrder = i;
                harvested.Add((jobs[i].Source.Entry.Group, m));
            }
        }

        List<BuiltModule> built = ModuleLibraryBuilder.Build(harvested, groups, request.Options);
        ModuleLibraryBuilder.Write(request.OutputDir, built, groups);

        // The mapper's curation.json is never written here; report its entries that name modules no longer harvested.
        ModuleLibrary written = ModuleLibrary.Load(request.OutputDir);
        var report = new ModuleHarvestReport
        {
            LevelsRead = read,
            Failures = failures.OrderBy(f => f.Item1).ThenBy(f => f.Item2, StringComparer.Ordinal).Select(f => (f.Item2, f.Item3)).ToList(),
            Brushes = brushes,
            Candidates = candidates,
            Components = components,
            Occurrences = harvested.Count,
            Rejected = rejected,
            Modules = built,
            Groups = groups,
            MissingPatterns = missing,
            OutputDir = Path.GetFullPath(request.OutputDir),
            RfInstallMounted = stock.Mounted,
            Elapsed = sw.Elapsed,
            CurationEntries = written.CurationEntries,
            CurationUnknownIds = written.UnknownCurationIds,
        };
        File.WriteAllText(Path.Combine(Path.GetFullPath(request.OutputDir), "harvest-report.json"), report.ToJson(), new UTF8Encoding(false));
        return report;
    }

    private static LevelHarvestResult HarvestOne(LevelJob job, string level, string root, StockAssets stock, ModuleHarvestRequest request, CancellationToken ct)
    {
        byte[] bytes;
        var own = new List<(string Rel, VppAssetSource Src)>();
        try
        {
            if (job.Entry is null)
            {
                bytes = ReadOnlyBytes(job.Source.FullPath);
            }
            else
            {
                VppAssetSource src = VppAssetSource.Open(job.Source.FullPath);
                own.Add((job.Source.RelativePath, src));
                bytes = src.Read(job.Entry) ?? throw new IOException($"entry {job.Entry} is missing");
            }

            foreach (string m in job.Source.Entry.MountWith)
            {
                string full = Path.GetFullPath(Path.Combine(root, m));
                if (File.Exists(full))
                {
                    own.Add((Path.GetRelativePath(root, full).Replace('\\', '/'), VppAssetSource.Open(full)));
                }
            }

            RflFile rfl = RflFile.Load(bytes);
            var vfs = new AssetVfs(own.Select(o => (IAssetSource)o.Src).Concat(stock.Sources));
            var resolveCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string? Custom(string tex)
            {
                string baseName = SupercedeChain.GetBaseName(tex);
                foreach ((string rel, VppAssetSource src) in own)
                {
                    if (SupercedeChain.Extensions.Any(ext => src.Contains(baseName + ext)))
                    {
                        return rel;
                    }
                }

                return null;
            }

            var ctx = new HarvestLevelContext
            {
                Level = level,
                Packfile = job.Entry is null ? null : job.Source.RelativePath,
                IsStockTexture = stock.Mounted ? stock.IsStock : t => Custom(t) is null,
                TextureResolves = t => resolveCache.GetOrAdd(t, n => vfs.ResolveTexture(n) is not null),
                CustomTextureSource = Custom,
                TextureCategory = stock.CategoryOf,
                StyleHint = job.Source.Entry.StyleHint,
            };
            return ModuleHarvester.HarvestLevel(rfl, ctx, request.Options, ct);
        }
        finally
        {
            foreach ((_, VppAssetSource src) in own)
            {
                src.Dispose();
            }
        }
    }

    private static byte[] ReadOnlyBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>The stock packfiles of an RF install, their texture categories, and stock-texture lookups.</summary>
    private sealed class StockAssets : IDisposable
    {
        private readonly List<VppAssetSource> _sources = new();
        private readonly Dictionary<string, string> _category = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stockBase = new(StringComparer.OrdinalIgnoreCase);

        public bool Mounted => _sources.Count > 0;

        public IReadOnlyList<IAssetSource> Sources => _sources;

        public static StockAssets Open(string? install, Action<string>? log)
        {
            var s = new StockAssets();
            if (install is null || !Directory.Exists(install))
            {
                return s;
            }

            foreach (string vpp in Directory.EnumerateFiles(install, "*.vpp").OrderBy(v => Path.GetFileName(v), StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(vpp);
                bool alpine = string.Equals(name, StockContent.AlpinePackfile, StringComparison.OrdinalIgnoreCase);
                if (!StockContent.IsStockPackfile(name) && !alpine)
                {
                    continue;
                }

                try
                {
                    VppAssetSource src = VppAssetSource.Open(vpp);
                    s._sources.Add(src);
                    if (!alpine)
                    {
                        foreach (string f in src.EnumerateFiles())
                        {
                            s._stockBase.Add(SupercedeChain.GetBaseName(f));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException or InvalidDataException)
                {
                    log?.Invoke($"warning: could not read {name}: {ex.Message}");
                }
            }

            if (s._sources.Count > 0)
            {
                var vfs = new AssetVfs(s._sources);
                foreach (AssetCategory c in TextureCategoryCatalog.Build(vfs))
                {
                    if (c.Name is TextureCategoryCatalog.AllName or TextureCategoryCatalog.UncategorizedName)
                    {
                        continue;
                    }

                    foreach (string file in c.Files)
                    {
                        s._category.TryAdd(SupercedeChain.GetBaseName(file), c.Name);
                    }
                }
            }

            return s;
        }

        public bool IsStock(string texture) => _stockBase.Contains(SupercedeChain.GetBaseName(texture));

        public string? CategoryOf(string texture) => _category.TryGetValue(SupercedeChain.GetBaseName(texture), out string? c) ? c : null;

        public void Dispose()
        {
            foreach (VppAssetSource s in _sources)
            {
                s.Dispose();
            }
        }
    }
}
