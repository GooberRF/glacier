using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// Study parity (DESIGN-QUALITY-SPEC §(g) Q1 gate) and grader calibration. The measurement core reproduces the
/// exemplar study: on every study level, every metric of research/exemplar-study/data/metrics.csv within ±10% (whole
/// counts within ±1), measured with the study's settings (no movement links); and the grader's per-level failure lists
/// equal data/grader.json. With the level classes on (and with the default movement links), the open-air exemplars
/// (E6, E9, E11, dm20) reach PASS or REVIEW, no counter-example passes and both generated levels FAIL. Skipped without
/// the local study data.
/// </summary>
[Trait("Category", "DeepGate")]
public sealed class StudyParityTests
{
    private readonly ITestOutputHelper _out;

    public StudyParityTests(ITestOutputHelper output) => _out = output;

    /// <summary>The stock study levels (research/example_rfls), or one null row without the study data.</summary>
    public static IEnumerable<object?[]> StockLevels()
    {
        if (!StudyData.Available || Corpus.Directory is null)
        {
            yield return new object?[] { null };
            yield break;
        }

        foreach (string stem in StudyData.Metrics.Keys.Where(k => System.IO.File.Exists(System.IO.Path.Combine(Corpus.Directory, k + ".rfl"))).OrderBy(k => k, StringComparer.Ordinal))
        {
            yield return new object?[] { stem };
        }
    }

    /// <summary>Every study level whose input copy is present, or one null row.</summary>
    public static IEnumerable<object?[]> AllLevels()
    {
        if (!StudyData.Available || StudyData.Labels.Keys.All(k => StudyData.LevelPath(k) is null))
        {
            yield return new object?[] { null };
            yield break;
        }

        foreach (string stem in StudyData.Labels.Keys.Where(k => StudyData.LevelPath(k) is not null).OrderBy(k => k, StringComparer.Ordinal))
        {
            yield return new object?[] { stem };
        }
    }

    [Theory]
    [MemberData(nameof(StockLevels))]
    public void Stock_Level_Metrics_Match_The_Study(string? stem)
    {
        if (stem is null)
        {
            return;
        }

        SpatialAnalysis a = SpatialAnalysis.Run(StudyData.Load(System.IO.Path.Combine(Corpus.Directory!, stem + ".rfl")), SpatialOptions.Study);
        SortedDictionary<string, double?> mine = DesignMetrics.Extract(a.Report());
        var off = new List<string>();
        int compared = 0;
        foreach ((string id, double? reference) in StudyData.Metrics[stem])
        {
            if (!mine.TryGetValue(id, out double? value) || (StudyData.TextureSizes is null && StudyData.TextureSizeMetrics.Contains(id)))
            {
                continue;
            }

            compared++;
            if (!Within(reference, value))
            {
                off.Add($"{id}: study {Fmt(reference)} vs {Fmt(value)}");
            }
        }

        _out.WriteLine($"{stem}: {compared} metrics compared");
        Assert.True(compared >= 100, $"only {compared} metrics compared");
        Assert.True(off.Count == 0, $"{stem}: {off.Count} metric(s) outside ±10% (counts ±1):\n" + string.Join("\n", off));

        List<string> failed = DesignGrader.FailedIds(DesignGrader.Grade(a, new GradeOptions { Classes = false, Gametype = StudyData.Labels[stem].Gametype })).OrderBy(x => x).ToList();
        Assert.Equal(StudyData.Failures[stem].OrderBy(x => x), failed);
    }

    [Theory]
    [MemberData(nameof(AllLevels))]
    public void Grader_Calibration_Reproduces_The_Study_And_The_Class_Gates(string? stem)
    {
        if (stem is null)
        {
            return;
        }

        (string label, string role, string gametype) = StudyData.Labels[stem];
        SpatialLevel level = StudyData.Load(StudyData.LevelPath(stem)!);

        // The study's own measurement: the per-level failure lists of grader.json.
        SpatialAnalysis study = SpatialAnalysis.Run(level, SpatialOptions.Study);
        List<string> failed = DesignGrader.FailedIds(DesignGrader.Grade(study, new GradeOptions { Classes = false, Gametype = gametype })).OrderBy(x => x).ToList();
        Assert.Equal(StudyData.Failures[stem].OrderBy(x => x), failed);

        // The product defaults (movement links on, level classes on) keep every group where it belongs.
        foreach (SpatialAnalysis a in new[] { study, SpatialAnalysis.Run(level) })
        {
            JsonObject g = DesignGrader.Grade(a, new GradeOptions { Gametype = gametype });
            string verdict = g["verdict"]!.GetValue<string>();
            _out.WriteLine($"{label} ({role}, links {a.Options.MovementLinks}): {verdict} class {g["class"]} {string.Join(",", DesignGrader.FailedIds(g))}");
            if (label is "E6" or "E9" or "E11" or "dm20")
            {
                Assert.Equal("open_air", g["class"]!.GetValue<string>());
                Assert.True(verdict is "PASS" or "REVIEW", $"{label} is an open-air exemplar but grades {verdict}");
            }

            if (role == "counter")
            {
                Assert.NotEqual("PASS", verdict);
            }

            if (role == "generated")
            {
                Assert.Equal("FAIL", verdict);
            }
        }
    }

    private static bool Within(double? reference, double? value)
    {
        if (reference is null || value is null)
        {
            return reference is null && value is null;
        }

        double r = reference.Value, v = value.Value;
        bool counts = r == Math.Floor(r) && v == Math.Floor(v) && Math.Abs(r) >= 2;
        return counts ? Math.Abs(r - v) <= 1 : Math.Abs(r - v) <= (0.10 * Math.Abs(r)) + 1e-9;
    }

    private static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "null";
}
