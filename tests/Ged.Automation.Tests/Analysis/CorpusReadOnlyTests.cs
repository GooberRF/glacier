using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Analysis;
using Ged.Core.Analysis;
using Xunit;


namespace Ged.Automation.Tests.Analysis;

/// <summary>
/// The corpus read-only contract (CORPUS-NORMS-SPEC §2.2/§2.3/§8.1) on SYNTHETIC temp corpora only: a sweep (and
/// snapshot / verify / analyze) never changes, adds or removes anything under a corpus root; the output folder can
/// never overlap a root; junctions are not followed; and the analysis source never calls a write API outside
/// <see cref="CorpusOutput"/>. Also: exclusions, dedupe, series bookkeeping, resume, determinism, timeouts.
/// </summary>
public sealed class CorpusReadOnlyTests
{
    private static CorpusSweepOptions Options(CorpusTestCorpus c, string? outDir = null, int jobs = 2) => new()
    {
        Config = CorpusConfig.Explicit(new[] { c.Root }),
        OutputDir = outDir ?? c.Out,
        Jobs = jobs,
        GlacierVersion = "test",
    };

    private static List<JsonObject> Index(string outDir) =>
        File.ReadAllLines(Path.Combine(outDir, "index.jsonl")).Where(l => l.Length > 0).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();

    private static JsonObject Row(List<JsonObject> index, string suffix) =>
        index.Single(r => r["source"]!.GetValue<string>().Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal));

    // ---- the contract ---------------------------------------------------------------------------

    [Fact]
    public async Task Synthetic_Corpus_Is_Untouched_By_Sweep_Snapshot_Verify_Analyze_And_Aggregate()
    {
        using var c = new CorpusTestCorpus();
        List<string> before = CorpusTestCorpus.Snapshot(c.Root);

        CorpusSweepResult r = await CorpusSweep.RunAsync(Options(c), CancellationToken.None);

        (int code, string o, string e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "snapshot", "--root", c.Root, "--out", Path.Combine(c.Out, "before.json"));
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "verify", "--out", c.Out, "--json");
        Assert.True(code == 0, o + e); // the sweep's own corpus-snapshot.json
        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "verify", "--snapshot", Path.Combine(c.Out, "before.json"));
        Assert.True(code == 0, o + e);
        Assert.Contains("Corpus unchanged", o, StringComparison.Ordinal);
        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "analyze", Path.Combine(c.Root, "maps", "alpha.rfl"), "--json");
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "aggregate", "--out", c.Out);
        Assert.True(code == 0, o + e);
        Assert.True(File.Exists(Path.Combine(c.Out, "norms", "corpus-norms.json")));

        Assert.Equal(before, CorpusTestCorpus.Snapshot(c.Root)); // identical files, sizes, times, hashes, attributes, folders

        // The sweep's outputs exist, in the output folder only.
        foreach (string f in new[] { "manifest.json", "index.jsonl", "failures.jsonl", "corpus-snapshot.json" })
        {
            Assert.True(File.Exists(Path.Combine(c.Out, f)), f);
        }

        Assert.False(r.Aborted);
        List<JsonObject> index = Index(c.Out);
        Assert.Equal("ok", Row(index, "maps/alpha.rfl")["status"]!.GetValue<string>());
        Assert.True(Row(index, "maps/alpha.rfl")["canonical"]!.GetValue<bool>());
        JsonObject copy = Row(index, "pack.vpp|alpha_copy.rfl");
        Assert.False(copy["canonical"]!.GetValue<bool>());
        Assert.EndsWith("alpha.rfl", copy["duplicateOf"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ok", Row(index, "pack.vpp|packed.rfl")["status"]!.GetValue<string>());
        Assert.Equal("ok_partial", Row(index, "maps/beta.rfl")["status"]!.GetValue<string>()); // no compiled geometry
        Assert.Equal(("excluded", "autosave"), (Row(index, "alpha_autosave.rfl")["status"]!.GetValue<string>(), Row(index, "alpha_autosave.rfl")["reason"]!.GetValue<string>()));
        Assert.Equal("not_rfl", Row(index, "notalevel.rfl")["reason"]!.GetValue<string>());
        Assert.Equal("ai_tagged", Row(index, "tagged.rfl")["reason"]!.GetValue<string>());
        Assert.Equal("unsupported_version", Row(index, "future.rfl")["status"]!.GetValue<string>());
        Assert.Contains(Row(index, "broken.rfl")["status"]!.GetValue<string>(), new[] { "parse_error", "ok_partial" });
        Assert.DoesNotContain(index, row => row["source"]!.GetValue<string>().EndsWith("readme.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(index, row => row["source"]!.GetValue<string>().EndsWith("notes.txt", StringComparison.Ordinal));

        // Version series: same author + base name; only the newest timestamp votes.
        Assert.False(Row(index, "series_v1.rfl")["vote"]!.GetValue<bool>());
        Assert.True(Row(index, "series_v2.rfl")["vote"]!.GetValue<bool>());
        Assert.Equal(1, r.Collapsed);

        // A fingerprint per distinct level; the failures file names the unsupported version.
        string sha = Row(index, "maps/alpha.rfl")["sha256"]!.GetValue<string>();
        LevelFingerprint fp = LevelFingerprint.Parse(File.ReadAllText(Path.Combine(c.Out, CorpusSweep.FingerprintPath(sha))));
        Assert.Equal("alpha.rfl", fp.Json["identity"]!["fileName"]!.GetValue<string>());
        Assert.Equal("community", fp.Json["identity"]!["origin"]!.GetValue<string>());
        Assert.Contains("future.rfl", File.ReadAllText(Path.Combine(c.Out, "failures.jsonl")), StringComparison.Ordinal);
        Assert.Equal(r.Unique, Directory.EnumerateFiles(Path.Combine(c.Out, "fingerprints"), "*.json", SearchOption.AllDirectories).Count()
            + index.Count(x => x["canonical"]?.GetValue<bool>() == true && x["status"]!.GetValue<string>() is "parse_error" or "timeout"));
    }

    [Fact]
    public void Output_Overlapping_A_Root_Is_Refused_And_Nothing_Is_Written()
    {
        using var c = new CorpusTestCorpus(readOnly: false);
        string inside = Path.Combine(c.Root, "maps", "out-here");
        string parent = Path.GetDirectoryName(c.Root)!;
        foreach (string bad in new[] { c.Root, inside, c.Root + Path.DirectorySeparatorChar, c.Root.ToUpperInvariant() == c.Root ? c.Root.ToLowerInvariant() : c.Root.ToUpperInvariant() })
        {
            if (!OperatingSystem.IsWindows() && !string.Equals(bad, c.Root, StringComparison.Ordinal) && !bad.StartsWith(c.Root, StringComparison.Ordinal))
            {
                continue; // case variants only alias on Windows
            }

            var ex = Assert.Throws<AutomationException>(() => new CorpusOutput(bad, new[] { c.Root }));
            Assert.Equal(AutomationErrorCodes.Usage, ex.Code);
        }

        Assert.Throws<AutomationException>(() => new CorpusOutput(parent, new[] { c.Root })); // contains the root
        Assert.False(Directory.Exists(inside));
    }

    [Fact]
    public async Task Cli_Refuses_An_Output_Inside_The_Root_Before_Reading()
    {
        using var c = new CorpusTestCorpus(readOnly: false);
        List<string> before = CorpusTestCorpus.Snapshot(c.Root);
        string inside = Path.Combine(c.Root, "maps", "out");
        (int code, string o, string e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "sweep", "--root", c.Root, "--out", inside);
        Assert.Equal(2, code);
        Assert.Contains("inside the corpus root", o + e, StringComparison.Ordinal);
        (code, _, _) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "snapshot", "--root", c.Root, "--out", Path.Combine(inside, "s.json"));
        Assert.Equal(2, code);
        Assert.False(Directory.Exists(inside));
        Assert.Equal(before, CorpusTestCorpus.Snapshot(c.Root));
    }

    [Fact]
    public async Task Reparse_Points_Under_A_Root_Are_Not_Traversed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var c = new CorpusTestCorpus(readOnly: false);
        string target = Directory.CreateTempSubdirectory("ged-corpus-junction-target-").FullName;
        string link = Path.Combine(c.Root, "linked");
        try
        {
            File.WriteAllBytes(Path.Combine(target, "outside.rfl"), CorpusTestCorpus.Compiled(TestLevels.RoomDocument()));
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
            {
                p!.WaitForExit(10_000);
            }

            if (!Directory.Exists(link))
            {
                return; // junction creation not permitted here: skipped
            }

            await CorpusSweep.RunAsync(Options(c), CancellationToken.None);
            Assert.DoesNotContain(Index(c.Out), r => r["source"]!.GetValue<string>().Contains("outside.rfl", StringComparison.Ordinal));
            JsonObject snap = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(c.Out, "corpus-snapshot.json")))!;
            Assert.Contains(snap["skippedReparsePoints"]!.AsArray(), n => n!.GetValue<string>().EndsWith("linked", StringComparison.Ordinal));
            Assert.DoesNotContain(snap["files"]!.AsArray(), n => n!["path"]!.GetValue<string>().Contains("outside", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link); // removes the junction itself, never the target's contents
            }

            Directory.Delete(target, recursive: true);
        }
    }

    /// <summary>
    /// Source gate: the analysis code (Core metrics, Automation corpus plumbing) contains no write API except in
    /// CorpusOutput.cs — the one guarded writer.
    /// </summary>
    [Fact]
    public void Analysis_Source_Has_No_Write_Apis_Outside_CorpusOutput()
    {
        string root = TestLevels.RepoRoot ?? throw new InvalidOperationException("repo root not found");
        string[] banned =
        {
            @"File\.Write", @"File\.Create", @"File\.Move", @"File\.Delete", @"File\.Copy", @"File\.Replace", @"File\.Append",
            @"File\.SetAttributes", @"File\.Open\(", @"FileMode\.Create", @"FileMode\.Append", @"FileMode\.Truncate", @"FileMode\.OpenOrCreate",
            @"FileAccess\.Write", @"FileAccess\.ReadWrite", @"Directory\.CreateDirectory", @"Directory\.Delete", @"Directory\.Move",
            @"\.SaveAsync\(", @"LevelSidecarStore", @"GraphLayoutStore", @"EditorDocument\.Open\(", @"\.Save\(\s*""", @"VppBuilder",
            @"Environment\.CurrentDirectory\s*=", @"SetCurrentDirectory",
        };
        var files = new[] { Path.Combine(root, "src", "Ged.Core", "Analysis"), Path.Combine(root, "src", "Ged.Automation", "Analysis") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !string.Equals(Path.GetFileName(f), "CorpusOutput.cs", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count >= 8, "expected the analysis sources");
        var hits = new List<string>();
        foreach (string f in files)
        {
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = Regex.Replace(lines[i], @"^\s*///.*$|//.*$", string.Empty);
                foreach (string b in banned)
                {
                    if (Regex.IsMatch(code, b))
                    {
                        hits.Add($"{Path.GetFileName(f)}:{i + 1}: {b}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0, "write APIs in the analysis code:\n" + string.Join("\n", hits));
    }

    /// <summary>
    /// The shipped norms (when present) carry no level name, file name, author or hash from the local index, and no
    /// texture file name. Skips when either the shipped file or a local sweep index is absent.
    /// </summary>
    [Fact]
    public void Shipped_Norms_Contain_No_Corpus_Strings()
    {
        string? root = TestLevels.RepoRoot;
        string norms = root is null ? string.Empty : Path.Combine(root, "src", "Ged.Core", "Analysis", "corpus-norms.json");
        string index = root is null ? string.Empty : Path.Combine(root, "artifacts", "corpus", "index.jsonl");
        if (!File.Exists(norms))
        {
            return;
        }

        string text = File.ReadAllText(norms);
        Assert.DoesNotMatch(@"(?i)\.(tga|vbm|dds)\b", text);
        if (!File.Exists(index))
        {
            return;
        }

        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(index))
        {
            if (JsonNode.Parse(line) is not JsonObject row)
            {
                continue;
            }

            if (row["sha256"]?.GetValue<string>() is { Length: > 0 } sha)
            {
                forbidden.Add(sha);
                string fpPath = Path.Combine(root!, "artifacts", "corpus", CorpusSweep.FingerprintPath(sha));
                if (File.Exists(fpPath) && JsonNode.Parse(File.ReadAllText(fpPath)) is JsonObject fp)
                {
                    foreach (string? s in new[] { fp["identity"]?["name"]?.GetValue<string>(), fp["identity"]?["fileName"]?.GetValue<string>(),
                        fp["identity"]?["author"]?["raw"]?.GetValue<string>(), fp["identity"]?["author"]?["canonical"]?.GetValue<string>() })
                    {
                        if (s is { Length: >= 4 })
                        {
                            forbidden.Add(s);
                        }
                    }
                }
            }
        }

        // The curated exemplar list (local only): its level names never reach the shipped norms either.
        string exemplars = Path.Combine(root!, "research", "exemplar-levels.txt");
        if (File.Exists(exemplars))
        {
            foreach (string raw in File.ReadLines(exemplars))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                foreach (string s in new[] { line, NormsAggregator.ExemplarKey(line), NormsAggregator.ExemplarKey(line) + ".rfl" })
                {
                    if (s.Length >= 4)
                    {
                        forbidden.Add(s);
                    }
                }
            }
        }

        var strings = Regex.Matches(text, "\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(strings, forbidden.Contains);
        Assert.DoesNotContain(strings, s => forbidden.Contains(s.ToLowerInvariant()));
    }

    // ---- behaviour ------------------------------------------------------------------------------

    [Fact]
    public async Task Sweep_Is_Deterministic_Across_Job_Counts_And_Resumes_From_The_Cache()
    {
        using var c = new CorpusTestCorpus();
        string out1 = Path.Combine(c.Out, "j1");
        string out8 = Path.Combine(c.Out, "j8");
        CorpusSweepResult a = await CorpusSweep.RunAsync(Options(c, out1, jobs: 1), CancellationToken.None);
        CorpusSweepResult b = await CorpusSweep.RunAsync(Options(c, out8, jobs: 8), CancellationToken.None);
        Assert.Equal(0, a.CacheHits);
        Dictionary<string, string> Read(string dir) => Directory.EnumerateFiles(Path.Combine(dir, "fingerprints"), "*.json", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllText);
        Dictionary<string, string> fa = Read(out1), fb = Read(out8);
        Assert.Equal(fa.Keys.OrderBy(k => k), fb.Keys.OrderBy(k => k));
        foreach ((string k, string v) in fa)
        {
            Assert.Equal(v, fb[k]);
        }

        Assert.Equal(File.ReadAllText(Path.Combine(out1, "index.jsonl")), File.ReadAllText(Path.Combine(out8, "index.jsonl")));
        Assert.Equal(File.ReadAllText(Path.Combine(out1, "manifest.json")), File.ReadAllText(Path.Combine(out8, "manifest.json")));

        // Resume: everything fingerprinted is reused, and the outputs do not change.
        string indexBefore = File.ReadAllText(Path.Combine(out1, "index.jsonl"));
        CorpusSweepResult again = await CorpusSweep.RunAsync(Options(c, out1, jobs: 4), CancellationToken.None);
        Assert.Equal(fa.Count, again.CacheHits);
        Assert.Equal(indexBefore, File.ReadAllText(Path.Combine(out1, "index.jsonl")));
    }

    [Fact]
    public async Task Debug_Render_Writes_Plan_Images_To_The_Output_Only()
    {
        using var c = new CorpusTestCorpus();
        List<string> before = CorpusTestCorpus.Snapshot(c.Root);
        CorpusSweepOptions o = Options(c);
        await CorpusSweep.RunAsync(new CorpusSweepOptions { Config = o.Config, OutputDir = o.OutputDir, Jobs = 2, GlacierVersion = "test", DebugRender = 2 }, CancellationToken.None);
        JsonArray images = JsonNode.Parse(File.ReadAllText(Path.Combine(c.Out, "debug", "index.json")))!["images"]!.AsArray();
        Assert.Equal(2, images.Count);
        foreach (JsonNode? img in images)
        {
            byte[] png = File.ReadAllBytes(Path.Combine(c.Out, img!["png"]!.GetValue<string>()));
            Assert.True(png.Length > 100 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G');
        }

        Assert.Equal(before, CorpusTestCorpus.Snapshot(c.Root));
    }

    [Fact]
    public async Task A_Slow_Level_Times_Out_And_The_Sweep_Continues()
    {
        using var c = new CorpusTestCorpus();
        CorpusSweepOptions o = Options(c);
        var slow = new CorpusSweepOptions
        {
            Config = o.Config,
            OutputDir = o.OutputDir,
            Jobs = 2,
            Timeout = TimeSpan.FromMilliseconds(400),
            GlacierVersion = "test",
            ProbeFor = name => name == "packed.rfl" ? () => Thread.Sleep(2) : null,
        };
        CorpusSweepResult r = await CorpusSweep.RunAsync(slow, CancellationToken.None);
        List<JsonObject> index = Index(c.Out);
        Assert.Equal("timeout", Row(index, "pack.vpp|packed.rfl")["status"]!.GetValue<string>());
        Assert.Equal("ok", Row(index, "maps/alpha.rfl")["status"]!.GetValue<string>());
        Assert.False(r.Aborted);
        Assert.Equal(0, r.Abandoned);
        Assert.Contains("packed.rfl", File.ReadAllText(Path.Combine(c.Out, "failures.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Watchdog_Abandons_A_Level_That_Ignores_Its_Time_Limit()
    {
        using var c = new CorpusTestCorpus();
        CorpusSweepOptions o = Options(c);
        bool stop = false;
        var stuck = new CorpusSweepOptions
        {
            Config = o.Config,
            OutputDir = o.OutputDir,
            Jobs = 2,
            Timeout = TimeSpan.FromMilliseconds(200),
            WatchdogGrace = TimeSpan.FromMilliseconds(300),
            MaxAbandoned = 0,
            GlacierVersion = "test",
            ProbeFor = name => name == "packed.rfl" ? () => { if (!Volatile.Read(ref stop)) { Thread.Sleep(1500); } } : null,
        };
        try
        {
            CorpusSweepResult r = await CorpusSweep.RunAsync(stuck, CancellationToken.None);
            Assert.Equal(1, r.Abandoned);
            Assert.True(r.Aborted); // more than MaxAbandoned (0): the sweep stops, resumable
            Assert.True(File.Exists(Path.Combine(c.Out, "index.jsonl")));
            Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(c.Out, "manifest.json")))!["aborted"]!.GetValue<bool>());
        }
        finally
        {
            Volatile.Write(ref stop, true); // let the abandoned worker run out quickly
        }
    }

    [Fact]
    public async Task Verify_Reports_A_Changed_And_A_New_File()
    {
        using var c = new CorpusTestCorpus(readOnly: false);
        string snap = Path.Combine(c.Out, "snap.json");
        (int code, string o, string e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "snapshot", "--root", c.Root, "--out", snap);
        Assert.True(code == 0, o + e);
        File.AppendAllText(Path.Combine(c.Root, "readme.txt"), "!");
        File.WriteAllText(Path.Combine(c.Root, "maps", "new.rfl"), "x");
        Directory.CreateDirectory(Path.Combine(c.Root, "newdir"));
        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "corpus", "verify", "--snapshot", snap, "--json");
        Assert.Equal(1, code);
        JsonNode result = JsonNode.Parse(o)!["result"]!;
        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.Contains(result["changed"]!.AsArray(), n => n!.GetValue<string>().EndsWith("readme.txt", StringComparison.Ordinal));
        Assert.Contains(result["added"]!.AsArray(), n => n!.GetValue<string>().EndsWith("maps/new.rfl", StringComparison.Ordinal));
        Assert.Contains(result["addedDirectories"]!.AsArray(), n => n!.GetValue<string>().EndsWith("newdir", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("corpus")]
    [InlineData("corpus frob")]
    [InlineData("corpus sweep --attach")]
    [InlineData("corpus snapshot --limit 3")]
    [InlineData("corpus sweep --jobs 0")]
    [InlineData("corpus sweep")]          // no roots configured in the isolated environment
    [InlineData("lint a.rfl --jobs 2")]
    [InlineData("analyze a.rfl --out x.json")]
    public async Task Corpus_Usage_Errors(string args)
    {
        (int code, _, _) = await CliTests.Cli(CorpusTestCorpus.Env(), args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Norms_Lists_Metrics_Prints_One_And_Compares_A_Level()
    {
        (int code, string o, string e) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--json");
        Assert.True(code == 0, o + e);
        JsonNode list = JsonNode.Parse(o)!["result"]!;
        Assert.Equal(CorpusNorms.ReferenceBucket, list["referenceBucket"]!.GetValue<string>());
        Assert.Equal(CorpusNorms.MetricIds, list["metrics"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));

        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--metric", "corridor.width", "--gametype", "overall", "--json");
        Assert.True(code == 0, o + e);
        JsonNode one = JsonNode.Parse(o)!["result"]!["metric"]!;
        Assert.Equal(CorpusNorms.GetBucket("corridor.width", "overall")!.P50, one["buckets"]!["overall"]!["p50"]!.GetValue<double>());
        Assert.Single(one["buckets"]!.AsObject());

        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--metric", "corridor.widht", "--json");
        Assert.NotEqual(0, code);
        Assert.Contains("corridor.width", o + e, StringComparison.Ordinal);

        using var c = new CorpusTestCorpus();
        string level = Path.Combine(c.Root, "maps", "alpha.rfl");
        (code, o, e) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--compare", level, "--json");
        Assert.True(code == 0, o + e);
        JsonNode cmp = JsonNode.Parse(o)!["result"]!;
        Assert.All(cmp["comparisons"]!.AsArray(), r => Assert.Contains(r!["verdict"]!.GetValue<string>(), new[] { "below", "within", "above" }));
        Assert.NotEmpty(cmp["comparisons"]!.AsArray());
        (code, o, _) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--compare", level);
        Assert.Equal(0, code);
        Assert.Contains("p5-p95", o, StringComparison.Ordinal);

        (code, _, _) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--compare");
        Assert.Equal(2, code);
        (code, _, _) = await CliTests.Cli(CorpusTestCorpus.Env(), "norms", "--gametype", "dm");
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Analyze_Prints_A_Fingerprint_Without_Opening_The_Level()
    {
        using var c = new CorpusTestCorpus();
        string level = Path.Combine(c.Root, "maps", "alpha.rfl");
        (int code, string o, string e) = await CliTests.Cli(CorpusTestCorpus.Env(), "analyze", level, "--json");
        Assert.True(code == 0, o + e);
        JsonNode fp = JsonNode.Parse(o)!["result"]!;
        Assert.Equal(FingerprintSchema.Schema, fp["schema"]!.GetValue<string>());
        Assert.Equal("alpha.rfl", fp["identity"]!["fileName"]!.GetValue<string>());
        Assert.True(fp["space"]!["floor_area"]!.GetValue<double>() > 100);
        (code, o, _) = await CliTests.Cli(CorpusTestCorpus.Env(), "analyze", level);
        Assert.Equal(0, code);
        Assert.Contains("Floor:", o, StringComparison.Ordinal);

        // Headless host: the analyzed level never becomes the session document.
        using var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, () => TestLevels.Fixed);
        AutomationResponse resp = await new AutomationCommands(host).ExecuteAsync(new AutomationRequest("analyze", new JsonObject { ["level"] = level }));
        Assert.True(resp.Ok, resp.Error?.Message);
        Assert.Null(host.Document);
    }

    // ---- quarantined real-corpus tiers (opt-in; never part of a default run) ------------------------

    /// <summary>
    /// research/example_rfls sweep: hash before/after + every level ok/ok_partial. DeepGate AND opt-in
    /// (GED_CORPUS_SWEEP_TESTS=1): it reads a real level collection, so it never runs by default.
    /// </summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public async Task ExampleRfls_Sweep_Leaves_The_Corpus_Untouched()
    {
        string? root = TestLevels.RepoRoot is { } r ? Path.Combine(r, "research", "example_rfls") : null;
        if (Environment.GetEnvironmentVariable("GED_CORPUS_SWEEP_TESTS") != "1" || root is null || !Directory.Exists(root))
        {
            return;
        }

        string outDir = Directory.CreateTempSubdirectory("ged-corpus-example-out-").FullName;
        try
        {
            List<string> before = CorpusTestCorpus.Snapshot(root);
            CorpusSweepResult res = await CorpusSweep.RunAsync(new CorpusSweepOptions { Config = CorpusConfig.Explicit(new[] { root }), OutputDir = outDir, GlacierVersion = "test" }, CancellationToken.None);
            Assert.Equal(before, CorpusTestCorpus.Snapshot(root));
            Assert.All(Index(outDir).Where(x => x["canonical"]?.GetValue<bool>() == true),
                x => Assert.Contains(x["status"]!.GetValue<string>(), new[] { "ok", "ok_partial", "unsupported_version" }));
            Assert.False(res.Aborted);
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    /// <summary>
    /// Full configured-corpus sweep + verify (CORPUS-NORMS-SPEC §8.1, Perf-style quarantine): run once, serially, on
    /// demand, with GED_CORPUS_SWEEP_TESTS=1 and GED_CORPUS_DIRS / research/corpus-dirs.txt configured.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public async Task Configured_Corpus_Sweep_And_Verify()
    {
        if (Environment.GetEnvironmentVariable("GED_CORPUS_SWEEP_TESTS") != "1")
        {
            return;
        }

        CorpusConfig cfg = CorpusConfig.Resolve(null, null, noInstall: false, Environment.GetEnvironmentVariable, TestLevels.RepoRoot);
        if (cfg.Roots.Count == 0)
        {
            return;
        }

        string outDir = Directory.CreateTempSubdirectory("ged-corpus-full-out-").FullName;
        try
        {
            CorpusSweepResult res = await CorpusSweep.RunAsync(new CorpusSweepOptions { Config = cfg, OutputDir = outDir, GlacierVersion = "test" }, CancellationToken.None);
            JsonObject snap = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "corpus-snapshot.json")))!;
            (var roots, string? install) = CorpusSnapshot.Coverage(snap);
            SnapshotDiff diff = CorpusSnapshot.Compare(snap, await CorpusSnapshot.TakeAsync(roots, install, Environment.ProcessorCount, CancellationToken.None));
            Assert.True(diff.Clean, diff.ToJson().ToJsonString());
            int supported = res.ByStatus.Where(kv => kv.Key is "ok" or "ok_partial" or "parse_error" or "timeout").Sum(kv => kv.Value);
            int ok = res.ByStatus.GetValueOrDefault("ok") + res.ByStatus.GetValueOrDefault("ok_partial");
            Assert.True(supported == 0 || ok >= 0.95 * supported, $"only {ok} of {supported} supported levels fingerprinted");
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }
}
