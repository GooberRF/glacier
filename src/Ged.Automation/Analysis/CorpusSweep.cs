using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Analysis;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Vpp;
using Ged.Core.Tables;

namespace Ged.Automation.Analysis;

/// <summary>Settings for one corpus sweep.</summary>
public sealed class CorpusSweepOptions
{
    public required CorpusConfig Config { get; init; }

    /// <summary>The output directory (validated by <see cref="CorpusOutput"/>).</summary>
    public required string OutputDir { get; init; }

    /// <summary>Parallel workers (default: cores − 1, at least 1).</summary>
    public int Jobs { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Per-level cooperative time limit.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long after the time limit a level that ignores cancellation is abandoned.</summary>
    public TimeSpan WatchdogGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>More abandoned workers than this stop the sweep (exit 1; the state is resumable).</summary>
    public int MaxAbandoned { get; init; } = 4;

    /// <summary>Fingerprint at most this many unique levels (a trial run); null = all.</summary>
    public int? Limit { get; init; }

    /// <summary>Levels larger than this are excluded (<c>too_large</c>; spec: 256 MB).</summary>
    public long MaxLevelBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Record version-series collapse (default on): only the newest level of a series votes in the aggregate.</summary>
    public bool Collapse { get; init; } = true;

    /// <summary>Take <c>corpus-snapshot.json</c> before reading when the output has none yet (default on).</summary>
    public bool Snapshot { get; init; } = true;

    /// <summary>The Glacier version recorded in the manifest.</summary>
    public string GlacierVersion { get; init; } = "0.0.0";

    /// <summary>Progress lines (stderr in the CLI).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Write plan images of this many seeded-random fingerprinted levels to <c>debug/</c> (§4-3 eyeball set; 0 = none).</summary>
    public int DebugRender { get; init; }

    /// <summary>The seed choosing the debug-render levels (same seed + same corpus = same picks).</summary>
    public int DebugSeed { get; init; } = 1;

    /// <summary>Test hook: the sampler probe for an instance file name (forces a slow level). Never set in production.</summary>
    public Func<string, Action?>? ProbeFor { get; init; }
}

/// <summary>The outcome of a sweep (also written to <c>manifest.json</c>).</summary>
public sealed class CorpusSweepResult
{
    public required string OutputDir { get; init; }

    public int Instances { get; init; }

    public int Unique { get; init; }

    public int Duplicates { get; init; }

    public int CacheHits { get; init; }

    public int Collapsed { get; init; }

    public int Abandoned { get; init; }

    public bool Aborted { get; init; }

    public required IReadOnlyDictionary<string, int> ByStatus { get; init; }

    public required IReadOnlyDictionary<string, int> ExcludedByReason { get; init; }

    public required JsonObject Manifest { get; init; }
}

/// <summary>
/// The parallel, resumable, read-only fingerprint sweep (CORPUS-NORMS-SPEC §2). Phase 1 enumerates every loose
/// <c>.rfl</c> and every <c>.rfl</c> entry of every <c>.vpp</c> under the roots (one packfile per worker, entries in
/// order), applies the exclusions and hashes the bytes. Phase 2 fingerprints each distinct SHA-256 once, from its
/// canonical instance (first in root order, then ordinal path order), with a cooperative per-level timeout and a
/// watchdog; a cached fingerprint with the current extractor version is reused. Phase 3 records version-series
/// collapse and writes <c>index.jsonl</c>, <c>failures.jsonl</c> and <c>manifest.json</c>. Every level is isolated: a
/// failure is a row, never an abort. All writes go through <see cref="CorpusOutput"/>.
/// </summary>
public static class CorpusSweep
{
    private sealed class Instance
    {
        public required int RootIndex { get; init; }

        public required string Source { get; init; }

        public required string OrderKey { get; init; }

        public string? Container { get; init; }

        public string? Entry { get; init; }

        public required string FileName { get; init; }

        public long Size { get; init; }

        public string? Sha { get; set; }

        public required string Origin { get; init; }

        public string Status { get; set; } = "pending";

        public string? Reason { get; set; }

        public bool Canonical { get; set; }

        public string? DuplicateOf { get; set; }

        public List<string> Duplicates { get; } = new();

        public string? Series { get; set; }

        public bool? Vote { get; set; }
    }

    private sealed record Container(int RootIndex, string FullPath, string RelativePath, bool IsVpp, bool Retail, long Length);

    private sealed record Failure(string? Sha, string Source, string Status, string Message);

    /// <summary>Runs the sweep.</summary>
    public static async Task<CorpusSweepResult> RunAsync(CorpusSweepOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        CorpusConfig cfg = o.Config;
        var output = new CorpusOutput(o.OutputDir, cfg.ProtectedRoots);
        foreach (string m in cfg.MissingRoots)
        {
            o.Log?.Invoke($"warning: corpus root not found: {m}");
        }

        if (o.Snapshot && !output.Exists("corpus-snapshot.json"))
        {
            o.Log?.Invoke("Taking the corpus snapshot (corpus-snapshot.json)…");
            JsonObject snap = await CorpusSnapshot.TakeAsync(cfg.Roots, cfg.RfInstall, o.Jobs, ct).ConfigureAwait(false);
            output.WriteText("corpus-snapshot.json", LevelFingerprint.Serialize(snap));
        }

        // ---- phase 1: enumerate, exclude, hash ----------------------------------------------------
        List<Container> containers = Containers(cfg, ct);
        o.Log?.Invoke($"{containers.Count} level files and packfiles under {cfg.Roots.Count} root(s).");
        var instances = new ConcurrentBag<Instance>();
        var failures = new ConcurrentBag<Failure>();
        await Parallel.ForEachAsync(containers, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Jobs), CancellationToken = ct }, async (c, token) =>
        {
            await Task.Yield();
            Enumerate(c, o, instances, failures, token);
        }).ConfigureAwait(false);

        List<Instance> all = instances.OrderBy(i => i.RootIndex).ThenBy(i => i.OrderKey, StringComparer.Ordinal).ToList();

        // ---- phase 2: one fingerprint per distinct SHA-256, from its canonical instance -------------------
        var groups = all.Where(i => i.Status == "pending" && i.Sha is not null)
            .GroupBy(i => i.Sha!, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();
        var canonical = new List<(Instance Inst, string Origin)>();
        foreach (List<Instance> g in groups)
        {
            Instance first = g[0]; // `all` is in canonical order
            first.Canonical = true;
            foreach (Instance dup in g.Skip(1))
            {
                dup.DuplicateOf = first.Source;
                first.Duplicates.Add(dup.Source);
            }

            canonical.Add((first, g.Any(i => i.Origin == "retail") ? "retail" : "community"));
        }

        canonical = canonical.OrderBy(c => c.Inst.RootIndex).ThenBy(c => c.Inst.OrderKey, StringComparer.Ordinal).ToList();
        if (o.Limit is int limit && canonical.Count > limit)
        {
            canonical = canonical.Take(Math.Max(0, limit)).ToList();
        }

        (ItemCatalog? items, Func<string, bool>? isStock) = StockData(cfg, o.Log);
        int cacheHits = 0, abandoned = 0;
        bool aborted = false;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fingerprints = new ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal);
        var work = canonical.GroupBy(c => c.Inst.Container ?? c.Inst.Source).ToList();
        try
        {
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Jobs), CancellationToken = abort.Token }, async (group, token) =>
            {
                VppArchive? vpp = null;
                try
                {
                    foreach ((Instance inst, string origin) in group)
                    {
                        token.ThrowIfCancellationRequested();
                        string rel = FingerprintPath(inst.Sha!);
                        if (TryCached(output, rel) is { } cached
                            && cached["identity"]?["fileName"]?.GetValue<string>() == inst.FileName
                            && cached["identity"]?["origin"]?.GetValue<string>() == origin)
                        {
                            Interlocked.Increment(ref cacheHits);
                            fingerprints[inst.Sha!] = cached;
                            if (cached["status"]?.GetValue<string>() == FingerprintSchema.Status.UnsupportedVersion)
                            {
                                failures.Add(new Failure(inst.Sha, inst.Source, FingerprintSchema.Status.UnsupportedVersion, cached["message"]?.GetValue<string>() ?? "unsupported version"));
                            }

                            continue;
                        }

                        byte[] bytes;
                        try
                        {
                            if (inst.Container is not null)
                            {
                                vpp ??= CorpusReader.OpenVpp(inst.Container);
                                bytes = vpp.Read(inst.Entry!);
                            }
                            else
                            {
                                bytes = CorpusReader.ReadAll(inst.Source);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException)
                        {
                            failures.Add(new Failure(inst.Sha, inst.Source, FingerprintSchema.Status.ParseError, "read failed: " + ex.Message));
                            continue;
                        }

                        if (LevelMetrics.Sha256Hex(bytes) != inst.Sha)
                        {
                            failures.Add(new Failure(inst.Sha, inst.Source, FingerprintSchema.Status.ParseError, "the file changed during the sweep"));
                            continue;
                        }

                        var mo = new MetricOptions
                        {
                            FileName = inst.FileName,
                            Origin = origin,
                            AuthorAliases = cfg.AuthorAliases,
                            Items = items,
                            IsStockTexture = isStock,
                            SamplerProbe = o.ProbeFor?.Invoke(inst.FileName),
                            LevelClass = true, // identity.class for the per-class norm buckets (E12)
                        };
                        (LevelFingerprint? fp, string status, string? message, bool wasAbandoned) = await AnalyzeWithWatchdog(bytes, mo, o, token).ConfigureAwait(false);
                        if (wasAbandoned && Interlocked.Increment(ref abandoned) > o.MaxAbandoned)
                        {
                            aborted = true;
                            o.Log?.Invoke($"error: more than {o.MaxAbandoned} levels ignored their time limit; stopping (the sweep can be resumed).");
                            abort.Cancel();
                        }

                        if (fp is null)
                        {
                            failures.Add(new Failure(inst.Sha, inst.Source, status, message ?? status));
                            fingerprints[inst.Sha!] = new JsonObject { ["status"] = status };
                            continue;
                        }

                        if (fp.Status == FingerprintSchema.Status.UnsupportedVersion)
                        {
                            failures.Add(new Failure(inst.Sha, inst.Source, fp.Status, fp.Json["message"]?.GetValue<string>() ?? fp.Status));
                        }

                        output.WriteText(rel, fp.ToJsonText());
                        fingerprints[inst.Sha!] = fp.Json;
                    }
                }
                finally
                {
                    vpp?.Dispose();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborted && !ct.IsCancellationRequested)
        {
            // The watchdog stopped the sweep: fall through and write the (partial, resumable) index.
        }

        // ---- phase 3: statuses, series collapse, index / failures / manifest --------------------------------
        foreach (Instance inst in all.Where(i => i.Sha is not null && i.Status == "pending"))
        {
            if (fingerprints.TryGetValue(inst.Sha!, out JsonObject? fpj))
            {
                inst.Status = fpj["status"]?.GetValue<string>() ?? FingerprintSchema.Status.Ok;
            }
        }

        int collapsed = o.Collapse ? MarkSeries(all, fingerprints) : 0;
        output.WriteLines("index.jsonl", all.Select(IndexRow));
        output.WriteLines("failures.jsonl", failures
            .OrderBy(f => f.Source, StringComparer.Ordinal).ThenBy(f => f.Status, StringComparer.Ordinal)
            .Select(f => new JsonObject { ["sha256"] = f.Sha, ["source"] = f.Source, ["status"] = f.Status, ["message"] = f.Message }.ToJsonString()));

        var byStatus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Instance inst in all.Where(i => i.Canonical || i.Status == FingerprintSchema.Status.Excluded || i.Sha is null))
        {
            byStatus[inst.Status] = byStatus.GetValueOrDefault(inst.Status) + 1;
        }

        var excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Instance inst in all.Where(i => i.Status == FingerprintSchema.Status.Excluded))
        {
            excluded[inst.Reason ?? "?"] = excluded.GetValueOrDefault(inst.Reason ?? "?") + 1;
        }

        int unique = all.Count(i => i.Canonical);
        int dups = all.Count(i => i.DuplicateOf is not null);
        var manifest = new JsonObject
        {
            ["schema"] = "glacier.corpus-manifest/1",
            ["extractorVersion"] = FingerprintSchema.ExtractorVersion,
            ["glacierVersion"] = o.GlacierVersion,
            ["roots"] = new JsonArray(cfg.Roots.Select(r => (JsonNode)r).ToArray()),
            ["rfInstall"] = cfg.RfInstall,
            ["instances"] = all.Count,
            ["unique"] = unique,
            ["duplicates"] = dups,
            ["byStatus"] = ToJson(byStatus),
            ["excludedByReason"] = ToJson(excluded),
            ["collapse"] = o.Collapse,
            ["collapsedNotVoting"] = collapsed,
            ["limit"] = o.Limit,
            ["abandonedWorkers"] = abandoned,
            ["aborted"] = aborted,
        };
        output.WriteText("manifest.json", LevelFingerprint.Serialize(manifest));
        if (o.DebugRender > 0 && !aborted)
        {
            DebugRenders(all, fingerprints, output, o, ct);
        }

        return new CorpusSweepResult
        {
            OutputDir = output.Root,
            Instances = all.Count,
            Unique = unique,
            Duplicates = dups,
            CacheHits = cacheHits,
            Collapsed = collapsed,
            Abandoned = abandoned,
            Aborted = aborted,
            ByStatus = byStatus,
            ExcludedByReason = excluded,
            Manifest = manifest,
        };
    }

    /// <summary>
    /// The eyeball set (§4-3): plan images of <see cref="CorpusSweepOptions.DebugRender"/> seeded-random fingerprinted
    /// levels with compiled geometry, plus <c>debug/index.json</c> naming each image's level (local output).
    /// </summary>
    private static void DebugRenders(List<Instance> all, ConcurrentDictionary<string, JsonObject> fingerprints, CorpusOutput output, CorpusSweepOptions o, CancellationToken ct)
    {
        List<Instance> pool = all
            .Where(i => i.Canonical && fingerprints.TryGetValue(i.Sha!, out JsonObject? fp) && fp["space"] is JsonObject)
            .OrderBy(i => i.Sha, StringComparer.Ordinal)
            .ToList();
        var rng = new Random(o.DebugSeed);
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var rows = new JsonArray();
        foreach (Instance inst in pool.Take(o.DebugRender).OrderBy(i => i.Sha, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                byte[] bytes;
                if (inst.Container is not null)
                {
                    using VppArchive vpp = CorpusReader.OpenVpp(inst.Container);
                    bytes = vpp.Read(inst.Entry!);
                }
                else
                {
                    bytes = CorpusReader.ReadAll(inst.Source);
                }

                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(o.Timeout);
                AnalysisDebugView? view = AnalysisDebugView.Compute(bytes, new MetricOptions { FileName = inst.FileName }, limit.Token);
                if (view is null)
                {
                    continue;
                }

                string png = $"debug/{inst.Sha![..16]}.png";
                output.WriteBytes(png, CorpusDebugRender.RenderPng(view, inst.FileName));
                rows.Add(new JsonObject
                {
                    ["png"] = png,
                    ["sha256"] = inst.Sha,
                    ["fileName"] = inst.FileName,
                    ["source"] = inst.Source,
                    ["openings"] = view.Openings.Count,
                    ["stairs"] = view.Stairs.Count,
                    ["pitch"] = view.Pitch,
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                o.Log?.Invoke($"warning: debug render of {inst.FileName} failed: {ex.Message}");
            }
        }

        output.WriteText("debug/index.json", LevelFingerprint.Serialize(new JsonObject { ["seed"] = o.DebugSeed, ["images"] = rows }));
    }

    /// <summary>The output-relative fingerprint path: <c>fingerprints/&lt;first two hex&gt;/&lt;sha256&gt;.json</c>.</summary>
    public static string FingerprintPath(string sha) => $"fingerprints/{sha[..2]}/{sha}.json";

    private static JsonObject? TryCached(CorpusOutput output, string rel)
    {
        if (!output.Exists(rel))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(output.ReadText(rel)) is JsonObject j
                && j["extractorVersion"]?.GetValue<int>() == FingerprintSchema.ExtractorVersion
                && j["schema"]?.GetValue<string>() == FingerprintSchema.Schema)
            {
                return j;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            // A damaged cache entry is recomputed.
        }

        return null;
    }

    private static async Task<(LevelFingerprint? Fp, string Status, string? Message, bool Abandoned)> AnalyzeWithWatchdog(
        byte[] bytes, MetricOptions mo, CorpusSweepOptions o, CancellationToken sweep)
    {
        // The token source lives as long as the worker (an abandoned worker keeps polling a live, cancelled token).
        var limit = CancellationTokenSource.CreateLinkedTokenSource(sweep);
        limit.CancelAfter(o.Timeout);
        Task<LevelFingerprint> task = Task.Run(() => LevelMetrics.Analyze(bytes, mo, limit.Token), CancellationToken.None);
        _ = task.ContinueWith(t => { _ = t.Exception; limit.Dispose(); }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Task finished;
        try
        {
            finished = await Task.WhenAny(task, Task.Delay(o.Timeout + o.WatchdogGrace, sweep)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            finished = Task.CompletedTask;
        }

        if (finished != task)
        {
            sweep.ThrowIfCancellationRequested();
            return (null, FingerprintSchema.Status.Timeout, $"abandoned by the watchdog after {(o.Timeout + o.WatchdogGrace).TotalSeconds:0} s", true);
        }

        try
        {
            return (await task.ConfigureAwait(false), FingerprintSchema.Status.Ok, null, false);
        }
        catch (OperationCanceledException) when (!sweep.IsCancellationRequested)
        {
            return (null, FingerprintSchema.Status.Timeout, $"exceeded the {o.Timeout.TotalSeconds:0} s time limit", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return (null, FingerprintSchema.Status.ParseError, $"{ex.GetType().Name}: {ex.Message}", false);
        }
    }

    private static List<Container> Containers(CorpusConfig cfg, CancellationToken ct)
    {
        var result = new List<Container>();
        var seen = new HashSet<string>(CorpusOutput.PathComparer);
        bool InInstall(string fullPath) =>
            cfg.RfInstall is not null && string.Equals(Path.GetDirectoryName(fullPath), cfg.RfInstall, StringComparison.OrdinalIgnoreCase);

        for (int r = 0; r < cfg.Roots.Count; r++)
        {
            foreach (CorpusFile f in CorpusReader.EnumerateFiles(cfg.Roots[r], ct))
            {
                bool rfl = f.FullPath.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase);
                bool vpp = f.FullPath.EndsWith(".vpp", StringComparison.OrdinalIgnoreCase);
                if ((rfl || vpp) && seen.Add(f.FullPath))
                {
                    result.Add(new Container(r, f.FullPath, f.RelativePath, vpp, vpp && InInstall(f.FullPath) && StockContent.IsStockPackfile(f.FullPath), f.Length));
                }
            }
        }

        if (cfg.RfInstall is { } install)
        {
            int idx = cfg.Roots.Count;
            foreach (CorpusFile f in CorpusReader.TopLevelFiles(install, "*.vpp"))
            {
                if (seen.Add(f.FullPath))
                {
                    result.Add(new Container(idx, f.FullPath, f.RelativePath, true, StockContent.IsStockPackfile(f.FullPath), f.Length));
                }
            }
        }

        return result;
    }

    private static void Enumerate(Container c, CorpusSweepOptions o, ConcurrentBag<Instance> instances, ConcurrentBag<Failure> failures, CancellationToken ct)
    {
        string origin = c.Retail ? "retail" : "community";
        if (!c.IsVpp)
        {
            var inst = new Instance
            {
                RootIndex = c.RootIndex,
                Source = c.FullPath,
                OrderKey = c.RelativePath,
                FileName = Path.GetFileName(c.FullPath),
                Size = c.Length,
                Origin = origin,
            };
            Classify(inst, () => CorpusReader.ReadAll(c.FullPath), o, failures);
            instances.Add(inst);
            return;
        }

        VppArchive vpp;
        try
        {
            vpp = CorpusReader.OpenVpp(c.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException or ArgumentException)
        {
            failures.Add(new Failure(null, c.FullPath, FingerprintSchema.Status.ParseError, "packfile could not be opened: " + ex.Message));
            return;
        }

        using (vpp)
        {
            foreach (VppEntry e in vpp.Entries.Where(e => e.Name.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var inst = new Instance
                {
                    RootIndex = c.RootIndex,
                    Source = c.FullPath + "|" + e.Name,
                    OrderKey = c.RelativePath + "|" + e.Name,
                    Container = c.FullPath,
                    Entry = e.Name,
                    FileName = e.Name,
                    Size = e.Size,
                    Origin = origin,
                };
                Classify(inst, () => vpp.Read(e), o, failures);
                instances.Add(inst);
            }
        }
    }

    /// <summary>Applies the exclusions (autosave name, size, magic, ai tags) and hashes the bytes.</summary>
    private static void Classify(Instance inst, Func<byte[]> read, CorpusSweepOptions o, ConcurrentBag<Failure> failures)
    {
        if (LevelIdentity.IsAutosaveName(inst.FileName))
        {
            Exclude(inst, "autosave");
            return;
        }

        if (inst.Size > o.MaxLevelBytes)
        {
            Exclude(inst, "too_large");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException)
        {
            inst.Status = FingerprintSchema.Status.ParseError;
            inst.Reason = "read failed";
            failures.Add(new Failure(null, inst.Source, FingerprintSchema.Status.ParseError, "read failed: " + ex.Message));
            return;
        }

        inst.Sha = LevelMetrics.Sha256Hex(bytes);
        if (!LevelMetrics.HasRflMagic(bytes))
        {
            Exclude(inst, "not_rfl");
            return;
        }

        try
        {
            if (LevelMetrics.HasAiTags(RflFile.Load(bytes)))
            {
                Exclude(inst, "ai_tagged");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unparseable: phase 2 records it as parse_error.
        }
    }

    private static void Exclude(Instance inst, string reason)
    {
        inst.Status = FingerprintSchema.Status.Excluded;
        inst.Reason = reason;
    }

    /// <summary>
    /// Version-series collapse bookkeeping: community levels with the same canonical author (non-empty) and the same
    /// series base name form a series; only the newest header timestamp votes (ties: the lowest SHA). Returns the
    /// number of levels that do not vote.
    /// </summary>
    private static int MarkSeries(List<Instance> all, ConcurrentDictionary<string, JsonObject> fingerprints)
    {
        var members = new List<(Instance Inst, string Key, long Ts)>();
        foreach (Instance inst in all.Where(i => i.Canonical))
        {
            if (!fingerprints.TryGetValue(inst.Sha!, out JsonObject? fp)
                || fp["status"]?.GetValue<string>() is not (FingerprintSchema.Status.Ok or FingerprintSchema.Status.OkPartial))
            {
                continue;
            }

            JsonNode? id = fp["identity"];
            string origin = id?["origin"]?.GetValue<string>() ?? "community";
            string author = id?["author"]?["canonical"]?.GetValue<string>() ?? string.Empty;
            inst.Vote = true;
            if (origin == "retail" || author.Length == 0)
            {
                continue;
            }

            string key = author + "|" + LevelIdentity.SeriesBaseName(inst.FileName);
            inst.Series = key;
            members.Add((inst, key, LongOf(id?["timestamp"])));
        }

        int notVoting = 0;
        foreach (var g in members.GroupBy(m => m.Key, StringComparer.Ordinal))
        {
            var ordered = g.OrderByDescending(m => m.Ts).ThenBy(m => m.Inst.Sha, StringComparer.Ordinal).ToList();
            foreach (var m in ordered.Skip(1))
            {
                m.Inst.Vote = false;
                notVoting++;
            }
        }

        return notVoting;
    }

    /// <summary>A JSON number as a long, whichever CLR type backs it (parsed text, or an in-memory uint/int).</summary>
    private static long LongOf(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue(out long l) ? l : v.TryGetValue(out uint u) ? u : v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (long)d : 0
        : 0;

    private static string IndexRow(Instance i)
    {
        var row = new JsonObject
        {
            ["source"] = i.Source,
            ["container"] = i.Container,
            ["entry"] = i.Entry,
            ["sha256"] = i.Sha,
            ["size"] = i.Size,
            ["origin"] = i.Origin,
            ["status"] = i.Status,
        };
        if (i.Reason is not null)
        {
            row["reason"] = i.Reason;
        }

        if (i.Canonical)
        {
            row["canonical"] = true;
            row["duplicates"] = new JsonArray(i.Duplicates.Select(d => (JsonNode)d).ToArray());
            if (i.Vote is bool v)
            {
                row["vote"] = v;
            }

            if (i.Series is not null)
            {
                row["series"] = i.Series;
            }
        }
        else if (i.DuplicateOf is not null)
        {
            row["canonical"] = false;
            row["duplicateOf"] = i.DuplicateOf;
        }

        return row.ToJsonString();
    }

    /// <summary>
    /// Item table and stock-texture lookup from the RF install (read-only): <c>items.tbl</c> from the stock packfiles,
    /// and the set of texture base names the stock packfiles hold. Both null without an install.
    /// </summary>
    private static (ItemCatalog? Items, Func<string, bool>? IsStock) StockData(CorpusConfig cfg, Action<string>? log)
    {
        if (cfg.RfInstall is not { } install)
        {
            return (null, null);
        }

        ItemCatalog? items = null;
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CorpusFile f in CorpusReader.TopLevelFiles(install, "*.vpp").Where(f => StockContent.IsStockPackfile(f.FullPath)))
        {
            try
            {
                using VppArchive vpp = CorpusReader.OpenVpp(f.FullPath);
                foreach (VppEntry e in vpp.Entries)
                {
                    string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                    if (ext is ".tga" or ".vbm" or ".dds")
                    {
                        textures.Add(Path.GetFileNameWithoutExtension(e.Name));
                    }
                }

                if (items is null && vpp.Find("items.tbl") is { } tbl)
                {
                    items = ItemCatalog.Load(vpp.Read(tbl));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException)
            {
                log?.Invoke($"warning: could not read {f.RelativePath}: {ex.Message}");
            }
        }

        return (items, textures.Count > 0 ? name => textures.Contains(Path.GetFileNameWithoutExtension(name ?? string.Empty)) : null);
    }

    private static JsonObject ToJson(SortedDictionary<string, int> map)
    {
        var o = new JsonObject();
        foreach ((string k, int v) in map)
        {
            o[k] = v;
        }

        return o;
    }
}
