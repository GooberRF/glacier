using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Linting;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The shipped corpus norms and their consumers (CORPUS-NORMS-SPEC §5.3–§5.5, §8.2): the embedded file, Lua
/// <c>catalog.norms</c>, the kit defaults pinned to the norms, and the out-of-norm lint.
/// </summary>
public sealed class CorpusNormsTests
{
    [Fact]
    public void Embedded_Norms_Load_With_Aggregates_Only()
    {
        Assert.True(CorpusNorms.Available);
        Assert.Equal("glacier.norms/1", CorpusNorms.Document!["schema"]!.GetValue<string>());
        Assert.Equal(CorpusNorms.MetricIds.OrderBy(m => m, StringComparer.Ordinal), CorpusNorms.MetricIds);
        Assert.Contains("opening.width", CorpusNorms.MetricIds);
        Assert.Matches(@"^\d{4}-\d{2}$", CorpusNorms.Generated!);

        // Every row is a monotone percentile set over at least one level.
        foreach (string id in CorpusNorms.MetricIds)
        {
            foreach (string b in CorpusNorms.Metric(id)!.Buckets)
            {
                NormStats s = CorpusNorms.GetBucket(id, b)!;
                Assert.True(s.N > 0, $"{id}/{b}");
                Assert.True(s.P5 <= s.P25 && s.P25 <= s.P50 && s.P50 <= s.P75 && s.P75 <= s.P95, $"{id}/{b}");
            }
        }
    }

    [Fact]
    public void Reference_Resolution_Falls_Back_To_Overall()
    {
        Assert.Equal(new[] { CorpusNorms.ReferenceBucket + ".dm", CorpusNorms.ReferenceBucket, CorpusNorms.OverallBucket },
            CorpusNorms.ResolutionOrder("dm"));
        NormStats? s = CorpusNorms.Get("opening.width");
        Assert.NotNull(s);
        Assert.Equal(CorpusNorms.GetBucket("opening.width", s!.Bucket), s);
        Assert.Null(CorpusNorms.GetBucket("opening.width", "no_such_bucket"));
        Assert.Null(CorpusNorms.Get("no.such_metric"));

        // A gametype bucket the reference set does not publish falls back to the reference bucket, then overall.
        NormStats any = CorpusNorms.Get("opening.width", "no_such_gametype")!;
        Assert.Contains(any.Bucket, new[] { CorpusNorms.ReferenceBucket, CorpusNorms.OverallBucket });
    }

    /// <summary>
    /// Norms v2 (DESIGN-QUALITY-SPEC E12): a lookup for a level class and player class tries the per-class buckets first
    /// and falls through to the v1 chain where they are not published; with neither class it IS the v1 chain.
    /// </summary>
    [Fact]
    public void Class_Resolution_Tries_Class_Buckets_Then_The_V1_Chain()
    {
        string r = CorpusNorms.ReferenceBucket;
        Assert.Equal(CorpusNorms.ResolutionOrder("dm"), CorpusNorms.ResolutionOrder("dm", null, null));
        Assert.Equal(new[] { r + ".dm.open_air", "dm.open_air", r + ".dm.S", r + ".dm", r, CorpusNorms.OverallBucket },
            CorpusNorms.ResolutionOrder("dm", "open_air", "S"));
        Assert.Equal(new[] { r + ".dm.standard", r + ".dm.M", r + ".dm", r, CorpusNorms.OverallBucket },
            CorpusNorms.ResolutionOrder("dm", "standard", "M"));
        Assert.Equal(CorpusNorms.ResolutionOrder("pay"), CorpusNorms.ResolutionOrder("pay", "payload", null)); // its gametype is its class

        // The shipped norms publish DM-S/M/L for the default-driving metrics (one vote per level, community and retail).
        foreach (string size in new[] { "S", "M", "L" })
        {
            Assert.NotNull(CorpusNorms.GetBucket("lights.per_100m2", "dm." + size));
        }

        // Unpublished class buckets fall through: the kit's pinned defaults read the same numbers as before.
        Assert.Equal(CorpusNorms.Get("corridor.width", "dm"), CorpusNorms.Get("corridor.width", "dm", "standard", "S"));
        Assert.Equal(CorpusNorms.Get("corridor.width", "dm")!.Bucket,
            RunLua("return catalog.norms_for{metric = 'corridor.width', gametype = 'dm', class = 'standard', players = 8}.bucket"));
        Assert.Contains("'class' must be", RunLuaError("catalog.norms_for{metric = 'corridor.width', class = 'indoor'}"), StringComparison.Ordinal);
    }

    // ---- Lua catalog.norms --------------------------------------------------------------------

    [Fact]
    public void Catalog_Norms_Equal_The_Embedded_File()
    {
        NormStats expected = CorpusNorms.Get("corridor.width")!;
        string r = RunLua("local n = catalog.norms('corridor.width') return string.format('%s|%s|%d|%.4f|%.4f|%.4f|%.4f|%.4f|%s|%s', "
            + "n.metric, n.bucket, n.n, n.p5, n.p25, n.p50, n.p75, n.p95, n.unit, n.use)");
        string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
        Assert.Equal($"corridor.width|{expected.Bucket}|{expected.N}|{F(expected.P5)}|{F(expected.P25)}|{F(expected.P50)}|{F(expected.P75)}|{F(expected.P95)}|{expected.Unit}|{expected.Use}", r);

        NormStats overall = CorpusNorms.GetBucket("corridor.width", "overall")!;
        Assert.Equal(overall.P50.ToString("0.0000", CultureInfo.InvariantCulture),
            RunLua("return string.format('%.4f', catalog.norms('corridor.width', 'overall').p50)"));
        Assert.Equal("nil", RunLua("return tostring(catalog.norms('corridor.width', 'no_such_bucket'))"));
        Assert.Equal(string.Join(",", CorpusNorms.MetricIds), RunLua("return table.concat(catalog.norm_metrics(), ',')"));
    }

    [Fact]
    public void Catalog_Norms_Unknown_Metric_Suggests_The_Nearest()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 0), "return catalog.norms('opening.widht')");
        Assert.False(r.Success);
        Assert.Contains("unknown metric", r.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("opening.width", r.Error!.ToDisplayString(), StringComparison.Ordinal);
    }

    private static string RunLua(string code)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 0), code);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r.ReturnValue!;
    }

    private static string RunLuaError(string code)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 0), code);
        Assert.False(r.Success);
        return r.Error!.ToDisplayString();
    }

    // ---- kit defaults follow the norms (§5.4) ------------------------------------------------

    /// <summary>A norm-sourced kit default: its metric, how the metric's p50 becomes the default, and the rounding step.</summary>
    private sealed record KitNormRow(string Key, string Metric, Func<double, double> FromP50, double Step, double? Min = null, string? Gametype = null);

    private static readonly KitNormRow[] KitNormRows =
    {
        new("door_width", "opening.width", p => p, 0.25, Min: 1.8 + 2 * 0.5), // limits.min_clear_width + 2 x 0.5 m clearance
        // Corridor width follows the multiplayer exemplars (exemplar.dm): the whole-exemplar bucket is pulled wider by the
        // single-player levels, and the kit's main audience is multiplayer layouts.
        new("corridor_width", "corridor.width", p => p, 0.5, Gametype: "dm"),
        new("corridor_height", "corridor.height", p => p, 0.5),
        new("light_spacing", "lights.per_100m2", p => Math.Sqrt(100 / p), 1),
    };

    /// <summary>
    /// Defaults that deliberately differ from the rounded p50 of their row's metric, with the reason. Each still lies
    /// inside its metric's p25–p75.
    /// </summary>
    private static readonly Dictionary<string, (string Metric, double Value, string Reason)> KitDefaultOverrides = new(StringComparer.Ordinal)
    {
        ["door_height"] = ("opening.height", 0,
            "opening.height is biased upward by pillar and prop gaps that reach the ceiling (seen in the detector's debug renders); "
            + "the kit takes the portal-agreeing value, portal.height p50 rounded to 0.25 m."),
    };

    [Fact]
    public void KitDefaultsFollowNorms()
    {
        string Default(string key) => RunLua($"local kit = ged.import('kit') return string.format('%.4f', kit.defaults.{key})");
        double D(string key) => double.Parse(Default(key), CultureInfo.InvariantCulture);
        static double Round(double v, double step) => Math.Round(v / step, MidpointRounding.AwayFromZero) * step;

        foreach (KitNormRow row in KitNormRows)
        {
            NormStats n = CorpusNorms.Get(row.Metric, row.Gametype)!;
            Assert.True(n.DrivesDefaults, $"{row.Metric} must be a default-driving metric (use default, reliability >= medium).");
            double expected = Round(row.FromP50(n.P50), row.Step);
            if (row.Min is double min && expected < min)
            {
                expected = Math.Ceiling(min / row.Step) * row.Step; // clamped up to the next step
            }

            double lo = Math.Min(row.FromP50(n.P25), row.FromP50(n.P75)), hi = Math.Max(row.FromP50(n.P25), row.FromP50(n.P75));
            Assert.Equal(expected, D(row.Key), 3);
            Assert.InRange(D(row.Key), lo, hi);
        }

        // door_height: the portal-agreeing value (see KitDefaultOverrides), inside opening.height's p25–p75.
        (string metric, _, string reason) = KitDefaultOverrides["door_height"];
        Assert.False(string.IsNullOrWhiteSpace(reason));
        NormStats portal = CorpusNorms.Get("portal.height")!, opening = CorpusNorms.Get(metric)!;
        Assert.Equal(Round(portal.P50, 0.25), D("door_height"), 3);
        Assert.InRange(D("door_height"), opening.P25, opening.P75);
        Assert.True(D("door_height") >= 2.0); // limits.min_headroom

        // tile_metres is reported only: it stays the editor's new-brush tile, which is also the corpus median.
        Assert.Equal(Editing.GeometryUtil.DefaultTileMetres, D("tile_metres"), 3);
        Assert.Equal(Editing.GeometryUtil.DefaultTileMetres, CorpusNorms.Get("texel.tile_metres")!.P50, 3);

        // ppm (texel density) [norm texel.tile_metres p50 = 4 m on the exemplars' 256 px textures, DESIGN-QUALITY-SPEC
        // §(a.4): 256 px / 4 m = 64 px/m, which is also RED's own density on the stock levels for 32, 64 and 128 px
        // textures]. Without a texture library the 256 px fallback makes it map exactly like tile_metres.
        Assert.Equal(Editing.UvOps.DefaultTextureSize / CorpusNorms.Get("texel.tile_metres")!.P50, D("ppm"), 3);
        Assert.Equal(Editing.UvOps.DefaultTextureSize / D("ppm"), D("tile_metres"), 3);
    }

    // ---- out-of-norm lint (§5.5) --------------------------------------------------------------

    private static RflFile BuiltLevel(string kit) => RflFile.Load(AnalysisFixtures.Build(kit));

    private const string WideDoor =
        "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {14, 6, 14}}\n" +
        "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {14, 6, 14}}\n" +
        "kit.doorway{name = 'd', a = a, b = b, width = 5.75, height = 3}";

    private const string NormalDoor =
        "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {14, 6, 14}}\n" +
        "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {14, 6, 14}}\n" +
        "kit.doorway{name = 'd', a = a, b = b}";

    private static string Doc(string metric) => CorpusNorms.Metric(metric)!.Doc.TrimEnd('.');

    [Fact]
    public void Lint_Out_Of_Norm_Is_Info_Capped_And_Never_Blocks()
    {
        RflFile wide = BuiltLevel(WideDoor);
        LintReport report = LevelLinter.Lint(wide, new LintOptions { CheckNorms = true, CheckGeometryLeaks = false });
        List<LintFinding> norm = report.Findings.Where(f => f.Category == LintCategory.OutOfNorm).ToList();
        Assert.All(norm, f => Assert.Equal(LintSeverity.Info, f.Severity));
        Assert.All(norm, f => Assert.False(f.BlocksSave));
        Assert.True(norm.Count <= 6);

        LevelFingerprint fp = LevelMetrics.Compute(wide, new MetricOptions { MaxFloorSamples = 5_000 }, System.Threading.CancellationToken.None);
        NormComparison door = NormComparisons.Compare(fp.Json).Single(c => c.Metric == "opening.width");
        Assert.Equal("above", door.Verdict);
        Assert.Contains(norm, f => f.Message.StartsWith(Doc("opening.width"), StringComparison.Ordinal) && f.Message.Contains("median", StringComparison.Ordinal));

        LintReport capped = LevelLinter.Lint(wide, new LintOptions { CheckNorms = true, CheckGeometryLeaks = false, MaxNormFindings = 1 });
        Assert.Single(capped.Findings, f => f.Category == LintCategory.OutOfNorm);

        // Off by default (the implicit save-time lint).
        Assert.DoesNotContain(LevelLinter.Lint(wide, new LintOptions { CheckGeometryLeaks = false }).Findings, f => f.Category == LintCategory.OutOfNorm);
    }

    [Fact]
    public void Lint_In_Range_Doorway_Raises_No_Doorway_Hint()
    {
        LintReport report = LevelLinter.Lint(BuiltLevel(NormalDoor), new LintOptions { CheckNorms = true, CheckGeometryLeaks = false });
        Assert.DoesNotContain(report.Findings, f => f.Category == LintCategory.OutOfNorm && f.Message.StartsWith(Doc("opening.width"), StringComparison.Ordinal));
    }

    [Fact]
    public void Lint_Skips_The_Norms_On_Preview_Or_Missing_Geometry_With_A_Note()
    {
        RflFile wide = BuiltLevel(WideDoor);
        LintReport preview = LevelLinter.Lint(wide, new LintOptions { CheckNorms = true, CheckGeometryLeaks = false, GeometryIsPreview = true });
        Assert.DoesNotContain(preview.Findings, f => f.Category == LintCategory.OutOfNorm);
        Assert.Contains(preview.Notes, n => n.Contains("preview", StringComparison.Ordinal));

        EditorDocument empty = ScriptTestFixture.NewDoc(entities: 0);
        LintReport none = LevelLinter.Lint(empty.Rfl, new LintOptions { CheckNorms = true, CheckGeometryLeaks = false });
        Assert.DoesNotContain(none.Findings, f => f.Category == LintCategory.OutOfNorm);
        Assert.Contains(none.Notes, n => n.Contains("no compiled geometry", StringComparison.Ordinal));
    }
}
