using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Analysis;
using Xunit;

namespace Ged.Automation.Tests.Analysis;

/// <summary>NormsAggregator (CORPUS-NORMS-SPEC §5.1/§8.2): thresholds, series collapse, one level one vote, percentile math.</summary>
public sealed class NormsAggregatorTests
{
    private static NormsLevel L(string sha, string author, double? floor = null, string gametype = "dm", string origin = "community",
        bool vote = true, bool conflict = false, string[]? motifs = null, Dictionary<string, double>? families = null, double? crossDiff = null)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (floor is double f)
        {
            values["space.floor_area"] = f;
        }

        return new NormsLevel
        {
            Sha = sha,
            Gametype = gametype,
            Origin = origin,
            Author = author,
            Vote = vote,
            Conflict = conflict,
            Values = values,
            Motifs = new HashSet<string>(motifs ?? Array.Empty<string>(), StringComparer.Ordinal),
            Families = families ?? new Dictionary<string, double>(StringComparer.Ordinal),
            OpeningPortalDiff = crossDiff,
        };
    }

    private static readonly NormsOptions Small = new() { MinLevels = 5, MinAuthors = 3, MinRetailLevels = 3, Generated = "2026-11" };

    [Fact]
    public void Percentiles_Are_Type7_Over_One_Value_Per_Level()
    {
        // Five levels, one vote each: floor areas 1, 2, 3, 4, 10.
        var levels = new[] { 1.0, 2, 3, 4, 10 }.Select((v, i) => L($"s{i}", $"a{i}", v)).ToList();
        (JsonObject norms, _) = NormsAggregator.Aggregate(levels, Small);
        JsonNode row = norms["metrics"]!["space.floor_area"]!["buckets"]!["overall"]!;
        Assert.Equal(5, row["n"]!.GetValue<int>());
        Assert.Equal(1.2, row["p5"]!.GetValue<double>());
        Assert.Equal(2.0, row["p25"]!.GetValue<double>());
        Assert.Equal(3.0, row["p50"]!.GetValue<double>());
        Assert.Equal(4.0, row["p75"]!.GetValue<double>());
        Assert.Equal(8.8, row["p95"]!.GetValue<double>());
    }

    [Fact]
    public void Exemplar_Buckets_Use_The_Curated_Levels_Regardless_Of_Collapse()
    {
        // 6 voting levels; 4 of them exemplars, plus 1 non-voting (older version) exemplar that still counts there.
        var levels = new List<NormsLevel>();
        for (int i = 0; i < 6; i++)
        {
            NormsLevel l = L($"s{i}", $"a{i}", 10 * (i + 1));
            levels.Add(i < 4 ? Exemplar(l) : l);
        }

        levels.Add(Exemplar(L("old", "a9", 1000, vote: false)));
        var options = new NormsOptions { MinLevels = 5, MinAuthors = 3, MinRetailLevels = 3, MinExemplarLevels = 5, Generated = "2026-11" };
        (JsonObject norms, string report) = NormsAggregator.Aggregate(levels, options);
        JsonObject buckets = norms["metrics"]!["space.floor_area"]!["buckets"]!.AsObject();
        Assert.Equal(6, buckets["overall"]!["n"]!.GetValue<int>()); // the old version does not vote in overall…
        Assert.Equal(5, buckets["exemplar"]!["n"]!.GetValue<int>()); // …but is in the curated set
        Assert.Equal(30, buckets["exemplar"]!["p50"]!.GetValue<double>()); // 10, 20, 30, 40, 1000
        Assert.Equal(5, buckets["exemplar.dm"]!["n"]!.GetValue<int>());
        Assert.Equal(5, norms["corpus"]!["exemplar"]!.GetValue<int>());
        Assert.Contains("exemplar", report, StringComparison.Ordinal);

        // Below the curated threshold the bucket is not published (consumers fall back to overall).
        (JsonObject few, _) = NormsAggregator.Aggregate(levels, new NormsOptions { MinLevels = 5, MinAuthors = 3, MinExemplarLevels = 6, Generated = "2026-11" });
        Assert.False(few["metrics"]!["space.floor_area"]!["buckets"]!.AsObject().ContainsKey("exemplar"));

        static NormsLevel Exemplar(NormsLevel l) => new()
        {
            Sha = l.Sha, Gametype = l.Gametype, Origin = l.Origin, Author = l.Author, Vote = l.Vote, Conflict = l.Conflict,
            Values = l.Values, Motifs = l.Motifs, Families = l.Families, Exemplar = true,
        };
    }

    [Fact]
    public void Shipping_Cuts_Motif_Lists_Until_The_Norms_Fit()
    {
        var motifs = new JsonArray(Enumerable.Range(0, 400).Select(i => (JsonNode)new JsonObject { ["motif"] = $"Trigger>Event:Motif{i:D4}", ["levelFraction"] = 0.5 }).ToArray());
        var norms = new JsonObject { ["schema"] = NormsAggregator.Schema, ["metrics"] = new JsonObject(), ["motifs"] = new JsonObject { ["overall"] = motifs } };
        (string full, int? none) = NormsAggregator.PrepareForShipping(norms, 1_000_000);
        Assert.Null(none);
        (string cut, int? cap) = NormsAggregator.PrepareForShipping(norms, full.Length / 3);
        Assert.NotNull(cap);
        Assert.True(cut.Length <= full.Length / 3);
        Assert.Equal(cap, ((JsonArray)JsonNode.Parse(cut)!["motifs"]!["overall"]!).Count);
        Assert.Contains("Motif0000", cut, StringComparison.Ordinal); // the most common entries are kept
    }

    /// <summary>
    /// Norms v2 per-class buckets (DESIGN-QUALITY-SPEC E12): dm.S / dm.M / dm.L by spawn count and &lt;gametype&gt;.&lt;class&gt;
    /// by level class, for the default-driving metrics only, under the usual thresholds and one vote per level; a
    /// gametype conflict, a single-player level and a payload class add none; exemplars vote in exemplar.&lt;bucket&gt;.
    /// </summary>
    [Fact]
    public void Class_Buckets_Split_Default_Metrics_By_Player_And_Level_Class()
    {
        NormsLevel C(string sha, string author, double v, string gametype, string? cls, string? size, bool conflict = false, bool exemplar = false) => new()
        {
            Sha = sha,
            Gametype = gametype,
            Origin = "community",
            Author = author,
            Conflict = conflict,
            Exemplar = exemplar,
            LevelClass = cls,
            PlayerClass = size,
            Values = new Dictionary<string, double>(StringComparer.Ordinal) { ["lights.per_100m2"] = v, ["space.floor_area"] = v * 1000 },
            Motifs = new HashSet<string>(StringComparer.Ordinal),
            Families = new Dictionary<string, double>(StringComparer.Ordinal),
        };

        var levels = new List<NormsLevel>();
        for (int i = 0; i < 6; i++)
        {
            levels.Add(C($"s{i}", $"a{i % 3}", 1 + i, "dm", "standard", "S", exemplar: i < 4));
            levels.Add(C($"o{i}", $"b{i % 3}", 10 + i, "dm", "open_air", "M"));
            levels.Add(C($"c{i}", $"c{i % 3}", 20 + i, "ctf", "open_air", "L"));
            levels.Add(C($"x{i}", $"d{i % 3}", 30 + i, "dm", "open_air", "M", conflict: true));
            levels.Add(C($"p{i}", $"e{i % 3}", 40 + i, "pay", "payload", "L"));
            levels.Add(C($"q{i}", $"f{i % 3}", 50 + i, "sp", "standard", "S"));
        }

        (JsonObject norms, string report) = NormsAggregator.Aggregate(levels, new NormsOptions { MinLevels = 5, MinAuthors = 3, MinRetailLevels = 3, MinExemplarLevels = 4, Generated = "2026-11" });
        JsonObject lights = norms["metrics"]!["lights.per_100m2"]!["buckets"]!.AsObject();
        Assert.Equal(6, lights["dm.S"]!["n"]!.GetValue<int>());
        Assert.Equal(6, lights["dm.M"]!["n"]!.GetValue<int>()); // the conflicting open-air dm levels vote in none
        Assert.Equal(6, lights["dm.standard"]!["n"]!.GetValue<int>());
        Assert.Equal(12.5, lights["dm.open_air"]!["p50"]!.GetValue<double>());
        Assert.Equal(6, lights["ctf.open_air"]!["n"]!.GetValue<int>());
        Assert.False(lights.ContainsKey("ctf.L")); // only DM splits by size (E12: DM-S/M/L, CTF, KOTH)
        Assert.False(lights.ContainsKey("pay.payload")); // payload is its own gametype
        Assert.False(lights.ContainsKey("sp.S"));
        Assert.False(lights.ContainsKey("sp.standard"));
        Assert.Equal(4, lights["exemplar.dm.S"]!["n"]!.GetValue<int>());
        Assert.Equal(4, lights["exemplar.dm.standard"]!["n"]!.GetValue<int>());

        // Reference-only metrics keep the v1 buckets; motifs never split by class.
        Assert.DoesNotContain(norms["metrics"]!["space.floor_area"]!["buckets"]!.AsObject(), kv => kv.Key.Contains(".open_air", StringComparison.Ordinal) || kv.Key.EndsWith(".S", StringComparison.Ordinal));
        Assert.Contains("| dm.open_air | 6 |", report, StringComparison.Ordinal);
        Assert.False(norms.ToJsonString().Contains("\"s0\"", StringComparison.Ordinal)); // aggregates only, no hashes or names
    }

    [Fact]
    public void Buckets_Below_The_Threshold_Are_Dropped()
    {
        // 6 dm levels from 3 authors (qualifies); 4 ctf levels (too few); 6 levels from 1 author in "koth" (too few authors).
        var levels = new List<NormsLevel>();
        for (int i = 0; i < 6; i++)
        {
            levels.Add(L($"d{i}", $"a{i % 3}", 100 + i));
        }

        for (int i = 0; i < 4; i++)
        {
            levels.Add(L($"c{i}", $"b{i}", 50, gametype: "ctf"));
        }

        for (int i = 0; i < 6; i++)
        {
            levels.Add(L($"k{i}", "solo", 70, gametype: "koth"));
        }

        (JsonObject norms, string report) = NormsAggregator.Aggregate(levels, Small);
        JsonObject buckets = norms["metrics"]!["space.floor_area"]!["buckets"]!.AsObject();
        Assert.True(buckets.ContainsKey("overall"));
        Assert.True(buckets.ContainsKey("dm"));
        Assert.False(buckets.ContainsKey("ctf"));
        Assert.False(buckets.ContainsKey("koth"));
        Assert.Contains("| ctf | 4 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Retail_Buckets_Use_The_Single_Publisher_Threshold_And_Conflicts_Skip_Gametype_Buckets()
    {
        var levels = Enumerable.Range(0, 3).Select(i => L($"r{i}", "publisher", 10 + i, origin: "retail")).ToList();
        levels.AddRange(Enumerable.Range(0, 6).Select(i => L($"c{i}", $"a{i}", 20 + i, conflict: true)));
        (JsonObject norms, _) = NormsAggregator.Aggregate(levels, Small);
        JsonObject buckets = norms["metrics"]!["space.floor_area"]!["buckets"]!.AsObject();
        Assert.Equal(3, buckets["retail"]!["n"]!.GetValue<int>());
        Assert.Equal(3, buckets["retail.dm"]!["n"]!.GetValue<int>());
        Assert.Equal(6, buckets["community"]!["n"]!.GetValue<int>());
        Assert.False(buckets.ContainsKey("dm")); // the community dm levels all conflict; retail dm (3 levels, 1 author) is below the community threshold
        Assert.Equal(9, buckets["overall"]!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Version_Series_Collapse_Removes_Older_Versions()
    {
        var levels = Enumerable.Range(0, 5).Select(i => L($"s{i}", $"a{i}", 10)).ToList();
        levels.AddRange(Enumerable.Range(0, 4).Select(i => L($"old{i}", "a0", 1000, vote: false)));
        (JsonObject collapsed, _) = NormsAggregator.Aggregate(levels, Small);
        (JsonObject all, _) = NormsAggregator.Aggregate(levels, new NormsOptions { MinLevels = 5, MinAuthors = 3, Collapse = false, Generated = "2026-11" });
        Assert.Equal(5, collapsed["corpus"]!["levels"]!.GetValue<int>());
        Assert.Equal(10, collapsed["metrics"]!["space.floor_area"]!["buckets"]!["overall"]!["p95"]!.GetValue<double>());
        Assert.Equal(9, all["corpus"]!["levels"]!.GetValue<int>());
        Assert.Equal(1000, all["metrics"]!["space.floor_area"]!["buckets"]!["overall"]!["p95"]!.GetValue<double>());
    }

    [Fact]
    public void Motifs_Families_And_Opening_Validation()
    {
        var fam = new Dictionary<string, double> { ["mtl"] = 0.5, ["(none)"] = 0.5 };
        var levels = Enumerable.Range(0, 6).Select(i => L($"s{i}", $"a{i}", 10, motifs: i < 3 ? new[] { "Trigger>Event:Delay" } : null,
            families: i < 5 ? fam : new Dictionary<string, double> { ["myown"] = 1 }, crossDiff: 0.25)).ToList();
        (JsonObject norms, _) = NormsAggregator.Aggregate(levels, Small);
        JsonNode motif = norms["motifs"]!["overall"]!.AsArray().Single()!;
        Assert.Equal("Trigger>Event:Delay", motif["motif"]!.GetValue<string>());
        Assert.Equal(0.5, motif["levelFraction"]!.GetValue<double>());
        JsonObject families = norms["textureFamilies"]!["overall"]!.AsObject();
        Assert.True(families.ContainsKey("mtl"));
        Assert.False(families.ContainsKey("(none)"));
        Assert.False(families.ContainsKey("myown")); // one author only
        Assert.True(norms["validation"]!["openingsValidated"]!.GetValue<bool>());

        (JsonObject failing, _) = NormsAggregator.Aggregate(levels.Select(l => L(l.Sha, l.Author, 10, crossDiff: 2)).ToList(), Small);
        Assert.False(failing["validation"]!["openingsValidated"]!.GetValue<bool>());
    }

    /// <summary>End to end on the synthetic corpus: sweep → aggregate; deterministic; no corpus strings in the norms.</summary>
    [Fact]
    public async Task Synthetic_Sweep_Aggregates_Deterministically_Without_Corpus_Strings()
    {
        using var c = new CorpusTestCorpus();
        var options = new NormsOptions { MinLevels = 1, MinAuthors = 0, MinRetailLevels = 1, Generated = "2026-11" };
        string out1 = Path.Combine(c.Out, "j1"), out8 = Path.Combine(c.Out, "j8");
        await CorpusSweep.RunAsync(new CorpusSweepOptions { Config = CorpusConfig.Explicit(new[] { c.Root }), OutputDir = out1, Jobs = 1, GlacierVersion = "test" }, CancellationToken.None);
        await CorpusSweep.RunAsync(new CorpusSweepOptions { Config = CorpusConfig.Explicit(new[] { c.Root }), OutputDir = out8, Jobs = 8, GlacierVersion = "test" }, CancellationToken.None);
        NormsAggregator.Run(out1, options);
        NormsAggregator.Run(out8, options);
        string n1 = File.ReadAllText(Path.Combine(out1, "norms", "corpus-norms.json"));
        Assert.Equal(n1, File.ReadAllText(Path.Combine(out8, "norms", "corpus-norms.json")));
        Assert.True(File.Exists(Path.Combine(out1, "norms", "norms-report.md")));
        JsonNode norms = JsonNode.Parse(n1)!;
        Assert.Equal(NormsAggregator.Schema, norms["schema"]!.GetValue<string>());
        Assert.True(norms["metrics"]!.AsObject().Count > 10);
        foreach (string forbidden in new[] { "alpha", "beta", "series", "packed", "Tester", "tester", ".rfl", ".tga", "rck_default" })
        {
            Assert.DoesNotContain(forbidden, n1, StringComparison.Ordinal);
        }

        foreach (string line in File.ReadAllLines(Path.Combine(out1, "index.jsonl")))
        {
            if (JsonNode.Parse(line)!["sha256"]?.GetValue<string>() is { } sha)
            {
                Assert.DoesNotContain(sha, n1, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("dm02", "dm02", false, null)]
    [InlineData("  ctf-hunab8.rfl	# among the best CTF designs", "ctf-hunab8.rfl", false, "among the best CTF designs")]
    [InlineData("DM Airship(1.2).rfl", "DM Airship(1.2).rfl", false, null)]
    [InlineData("!ctf06	# too big", "ctf06", true, "too big")]
    [InlineData("! dm14 # too simple", "dm14", true, "too simple")]
    public void ExemplarListLine_Parses_Name_Note_And_CounterExample(string raw, string name, bool counter, string? note)
    {
        NormsAggregator.ExemplarLine? e = NormsAggregator.ParseExemplarLine(raw);
        Assert.NotNull(e);
        Assert.Equal(name, e!.Value.Name);
        Assert.Equal(counter, e.Value.CounterExample);
        Assert.Equal(note, e.Value.Note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("!")]
    public void ExemplarListLine_Skips_Blank_Comment_And_Empty(string raw) => Assert.Null(NormsAggregator.ParseExemplarLine(raw));
}
