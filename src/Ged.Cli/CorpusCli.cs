using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Analysis;

namespace Ged.Cli;

/// <summary>
/// <c>glacier-cli corpus sweep | snapshot | verify | aggregate</c> (CORPUS-NORMS-SPEC §6): headless-only tools that fingerprint a
/// folder of levels (and packfiles) READ-ONLY into a separate output folder. Nothing is ever written under a corpus
/// root: the output folder may not equal, lie inside, or contain one.
/// </summary>
internal static class CorpusCli
{
    private const string Verb = "corpus";

    public static async Task<int> RunAsync(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env)
    {
        if (a.Attach)
        {
            throw new UsageException("'corpus' runs without --attach: it reads level files directly, never the editor's document.", Verb);
        }

        string sub = a.Positionals.FirstOrDefault()
            ?? throw new UsageException("corpus needs a subcommand: sweep, snapshot, verify or aggregate.", Verb);
        if (a.Positionals.Count > 1)
        {
            throw new UsageException($"Unexpected argument '{a.Positionals[1]}'.", Verb);
        }

        if (a.Save || a.DryRun || a.Build || a.Light || a.Sealed || a.Steps is not null || a.Detail is not null || a.NoTag || a.NoBuild
            || a.FailOnHoles || a.RenderOptions.Count > 0 || a.Markdown || a.Lua || a.Member is not null || a.AllowImports.Count > 0
            || a.AllowDestructive || a.NoShadows)
        {
            throw new UsageException("corpus takes only --out, --root, --rf-dir, --no-install, --jobs, --timeout, --limit, --debug-render, --no-collapse, --no-snapshot, --snapshot and --json.", Verb);
        }

        if (sub != "sweep" && (a.Limit is not null || a.NoSnapshot || a.TimeoutSeconds is not null || a.DebugRender is not null))
        {
            throw new UsageException("--limit, --no-snapshot, --debug-render and --timeout only apply to 'corpus sweep'.", Verb);
        }

        if (sub is not ("sweep" or "aggregate") && a.NoCollapse)
        {
            throw new UsageException("--no-collapse only applies to 'corpus sweep' and 'corpus aggregate'.", Verb);
        }

        if (sub != "aggregate" && a.Promote)
        {
            throw new UsageException("--promote only applies to 'corpus aggregate'.", Verb);
        }

        if (sub != "verify" && a.SnapshotPath is not null)
        {
            throw new UsageException("--snapshot only applies to 'corpus verify'.", Verb);
        }

        string? repo = env.RepoRoot();
        int jobs = a.Jobs ?? Math.Max(1, Environment.ProcessorCount - 1);
        switch (sub)
        {
            case "sweep":
                return await SweepAsync(a, stdout, stderr, env, repo, jobs);
            case "snapshot":
                return await SnapshotAsync(a, stdout, stderr, env, repo, jobs);
            case "verify":
                return await VerifyAsync(a, stdout, repo, jobs);
            case "aggregate":
                return Aggregate(a, stdout, repo, env);
            default:
                throw new UsageException($"Unknown corpus subcommand '{sub}' (use sweep, snapshot, verify or aggregate).", Verb);
        }
    }

    private static CorpusConfig Config(CliArguments a, CliEnvironment env, string? repo, TextWriter stderr)
    {
        if (a.RfDir is not null && !Directory.Exists(a.RfDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"--rf-dir: directory not found: {a.RfDir}");
        }

        CorpusConfig cfg = CorpusConfig.Resolve(a.Roots.Count > 0 ? a.Roots : null, a.RfDir, a.NoInstall, env.GetEnvironmentVariable, repo);
        foreach (string m in cfg.MissingRoots)
        {
            stderr.WriteLine($"glacier-cli: warning: corpus root not found: {m}");
        }

        if (cfg.Roots.Count == 0 && cfg.RfInstall is null)
        {
            throw new UsageException("No corpus folders: pass --root <dir> (repeatable), set GED_CORPUS_DIRS, or list folders in research/corpus-dirs.txt.", Verb);
        }

        return cfg;
    }

    private static async Task<int> SweepAsync(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env, string? repo, int jobs)
    {
        CorpusConfig cfg = Config(a, env, repo, stderr);
        string outDir = a.Out ?? CorpusConfig.DefaultOutputDir(repo);
        CorpusOutput.Guard(outDir, cfg.ProtectedRoots); // refuse before any work
        var options = new CorpusSweepOptions
        {
            Config = cfg,
            OutputDir = outDir,
            Jobs = jobs,
            Timeout = a.TimeoutSeconds is double t && t > 0 ? TimeSpan.FromSeconds(t) : TimeSpan.FromSeconds(60),
            Limit = a.Limit,
            Collapse = !a.NoCollapse,
            Snapshot = !a.NoSnapshot,
            DebugRender = a.DebugRender ?? 0,
            GlacierVersion = AutomationCommands.ProductVersion,
            Log = line => stderr.WriteLine($"glacier-cli: {line}"),
        };
        CorpusSweepResult r = await CorpusSweep.RunAsync(options, CancellationToken.None);
        JsonObject result = (JsonObject)r.Manifest.DeepClone();
        result["outputDir"] = r.OutputDir;
        result["cacheHits"] = r.CacheHits;
        if (a.Json)
        {
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse { Ok = !r.Aborted, Command = "corpus sweep", Result = result }, indented: true));
        }
        else
        {
            stdout.WriteLine($"Swept {r.Instances} level instance(s): {r.Unique} distinct, {r.Duplicates} duplicate(s), {r.CacheHits} reused from the cache.");
            stdout.WriteLine("Status:   " + string.Join(", ", r.ByStatus.Select(kv => $"{kv.Key} {kv.Value}")));
            if (r.ExcludedByReason.Count > 0)
            {
                stdout.WriteLine("Excluded: " + string.Join(", ", r.ExcludedByReason.Select(kv => $"{kv.Key} {kv.Value}")));
            }

            if (r.Collapsed > 0)
            {
                stdout.WriteLine($"Version series: {r.Collapsed} older version(s) do not vote in the aggregate (--no-collapse to count them).");
            }

            stdout.WriteLine($"Output:   {r.OutputDir}");
            if (r.Aborted)
            {
                stderr.WriteLine("error: the sweep stopped early (too many levels ignored their time limit); run it again to resume.");
            }
        }

        return r.Aborted ? ExitCodes.Failed : ExitCodes.Ok;
    }

    private static int Aggregate(CliArguments a, TextWriter stdout, string? repo, CliEnvironment env)
    {
        if (a.Roots.Count > 0 || a.RfDir is not null || a.NoInstall || a.Jobs is not null)
        {
            throw new UsageException("corpus aggregate reads a sweep's output folder: it takes only --out, --no-collapse, --promote and --json.", Verb);
        }

        string outDir = a.Out ?? CorpusConfig.DefaultOutputDir(repo);
        string? promoteTo = null;
        if (a.Promote)
        {
            promoteTo = repo is null
                ? throw new UsageException("--promote only works inside a repository checkout (it writes src/Ged.Core/Analysis/corpus-norms.json).", Verb)
                : Path.Combine(repo, "src", "Ged.Core", "Analysis", "corpus-norms.json");
        }

        (JsonObject norms, string _) = NormsAggregator.Run(outDir, new NormsOptions
        {
            Collapse = !a.NoCollapse,
            ExemplarListPath = env.GetEnvironmentVariable(CorpusConfig.ExemplarListVariable) is { Length: > 0 } list ? list
                : repo is null ? null : Path.Combine(repo, "research", "exemplar-levels.txt"),
            ExemplarStockDir = repo is null ? null : Path.Combine(repo, "research", "example_rfls"),
        });
        string path = Path.Combine(CorpusConfig.Normalize(outDir), "norms", "corpus-norms.json");
        JsonObject? promoted = null;
        if (promoteTo is not null)
        {
            (string text, int? cap) = NormsAggregator.PrepareForShipping(norms);
            int bytes = System.Text.Encoding.UTF8.GetByteCount(text);
            if (bytes > NormsAggregator.ShippedMaxBytes)
            {
                throw new AutomationException(AutomationErrorCodes.Io, $"The norms are {bytes / 1024} KB even with one motif per bucket (limit {NormsAggregator.ShippedMaxBytes / 1024} KB): not promoted.");
            }

            File.WriteAllText(promoteTo, text);
            promoted = new JsonObject { ["path"] = promoteTo, ["bytes"] = bytes, ["motifCap"] = cap };
        }
        if (a.Json)
        {
            JsonObject result = new()
            {
                ["path"] = path,
                ["report"] = Path.Combine(CorpusConfig.Normalize(outDir), "norms", "norms-report.md"),
                ["corpus"] = norms["corpus"]!.DeepClone(),
                ["validation"] = norms["validation"]!.DeepClone(),
                ["metrics"] = norms["metrics"]!.AsObject().Count,
                ["promoted"] = promoted,
            };
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse { Ok = true, Command = "corpus aggregate", Result = result }, indented: true));
        }
        else
        {
            JsonNode corpus = norms["corpus"]!;
            stdout.WriteLine($"Aggregated {corpus["levels"]} level(s) ({corpus["retail"]} retail, {corpus["community"]} community) into {norms["metrics"]!.AsObject().Count} metric(s).");
            stdout.WriteLine($"Norms:  {path}");
            stdout.WriteLine($"Report: {Path.Combine(CorpusConfig.Normalize(outDir), "norms", "norms-report.md")}");
            if (promoted is not null)
            {
                stdout.WriteLine($"Promoted: {promoteTo} ({(int)promoted["bytes"]! / 1024} KB{(promoted["motifCap"] is JsonNode c ? $", motifs cut to the top {c} per bucket to fit" : string.Empty)}); review it in the diff and rebuild.");
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>The snapshot file named by --out: a .json path, or a folder that receives corpus-snapshot.json.</summary>
    private static (string Dir, string File) SnapshotTarget(string? path, string? repo, string? explicitFile = null)
    {
        if (explicitFile is not null)
        {
            string full = Path.GetFullPath(explicitFile);
            return (Path.GetDirectoryName(full)!, Path.GetFileName(full));
        }

        if (path is not null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(path);
            return (Path.GetDirectoryName(full)!, Path.GetFileName(full));
        }

        return (Path.GetFullPath(path ?? CorpusConfig.DefaultOutputDir(repo)), "corpus-snapshot.json");
    }

    private static async Task<int> SnapshotAsync(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env, string? repo, int jobs)
    {
        CorpusConfig cfg = Config(a, env, repo, stderr);
        (string dir, string file) = SnapshotTarget(a.Out, repo);
        var output = new CorpusOutput(dir, cfg.ProtectedRoots);
        JsonObject snap = await CorpusSnapshot.TakeAsync(cfg.Roots, cfg.RfInstall, jobs, CancellationToken.None);
        output.WriteText(file, Ged.Core.Analysis.LevelFingerprint.Serialize(snap));
        string written = output.PathOf(file);
        int files = snap["fileCount"]!.GetValue<int>();
        int dirs = snap["directories"]!.AsArray().Count;
        if (a.Json)
        {
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse
            {
                Ok = true,
                Command = "corpus snapshot",
                Result = new JsonObject { ["path"] = written, ["files"] = files, ["directories"] = dirs, ["roots"] = snap["roots"]!.DeepClone() },
            }, indented: true));
        }
        else
        {
            stdout.WriteLine($"Snapshot of {files} file(s) in {dirs} folder(s) under {cfg.ProtectedRoots.Count} root(s): {written}");
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> VerifyAsync(CliArguments a, TextWriter stdout, string? repo, int jobs)
    {
        (string dir, string file) = SnapshotTarget(a.Out, repo, a.SnapshotPath);
        string path = Path.Combine(dir, file);
        if (!File.Exists(path))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"No corpus snapshot at '{path}': take one with 'glacier-cli corpus snapshot' (or a sweep) first.");
        }

        JsonObject before = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new AutomationException(AutomationErrorCodes.Io, $"'{path}' is not a corpus snapshot.");
        (var roots, string? install) = CorpusSnapshot.Coverage(before);
        JsonObject after = await CorpusSnapshot.TakeAsync(roots, install, jobs, CancellationToken.None);
        SnapshotDiff diff = CorpusSnapshot.Compare(before, after);
        if (a.Json)
        {
            JsonObject result = diff.ToJson();
            result["snapshot"] = path;
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse
            {
                Ok = diff.Clean,
                Command = "corpus verify",
                Result = result,
                Error = diff.Clean ? null : new AutomationError("corpus_changed", "The corpus differs from the snapshot."),
            }, indented: true));
        }
        else
        {
            stdout.WriteLine(diff.Clean
                ? $"Corpus unchanged: {diff.Files} file(s) match the snapshot ({path})."
                : $"CORPUS CHANGED since the snapshot ({path}): {diff.Changed.Count} changed, {diff.Missing.Count} missing, {diff.Added.Count} new file(s), {diff.AddedDirectories.Count} new folder(s), {diff.MissingDirectories.Count} missing folder(s).");
            foreach (string s in diff.Changed.Take(50))
            {
                stdout.WriteLine($"  changed: {s}");
            }

            foreach (string s in diff.Missing.Take(50))
            {
                stdout.WriteLine($"  missing: {s}");
            }

            foreach (string s in diff.Added.Concat(diff.AddedDirectories).Take(50))
            {
                stdout.WriteLine($"  new:     {s}");
            }
        }

        return diff.Clean ? ExitCodes.Ok : ExitCodes.Failed;
    }
}
