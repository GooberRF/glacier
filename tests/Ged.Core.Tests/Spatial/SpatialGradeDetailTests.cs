using System;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// What the queries and the grade say about steering a number: sky over a kit.canyon (show_sky tops count as sky), the
/// G06 detail (every counted space, how many more small ones the median needs), the floor-height class bins, and payload
/// spawn safety counting only the spawn pairs live in the same checkpoint stage.
/// </summary>
public sealed class SpatialGradeDetailTests
{
    private readonly ITestOutputHelper _out;

    public SpatialGradeDetailTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void A_Canyon_Under_Sky_Faces_Reads_As_Sky()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("canyon");
        JsonObject area = a.AreaReport();
        _out.WriteLine(area.ToJsonString());
        Assert.True(area["outdoor_fraction"]!.GetValue<double>() > 0.8, "outdoor_fraction");
        Assert.Equal(0, area["open_sky_fraction"]!.GetValue<double>()); // a sealed level: every sample has a ceiling (the sky faces)
        Assert.True(area["spaces"]![0]!["sky"]!.GetValue<double>() > 0.8);

        JsonNode largest = a.SetPieces()["largest_spaces"]![0]!;
        Assert.True(largest["sky"]!.GetValue<double>() > 0.8, "set-piece sky");
        Assert.Equal(0, largest["open_sky"]!.GetValue<double>());
        Assert.True(a.Locations("spaces", 1)[0]!["sky"]!.GetValue<double>() > 0.8);
    }

    [Fact]
    public void G06_Detail_Lists_The_Spaces_And_What_Moves_The_Median()
    {
        JsonObject grade = DesignGrader.Grade(SpatialFixtures.Analyze("empty"), new GradeOptions { Gametype = "dm" });
        JsonNode g06 = grade["failures"]!.AsArray().First(f => f!["id"]!.GetValue<string>() == "G06")!;
        JsonNode d = g06["detail"]!;
        _out.WriteLine(d.ToJsonString());
        Assert.Equal("space_area", d["kind"]!.GetValue<string>());
        Assert.Equal(1, d["counted"]!.GetValue<int>());
        Assert.Equal(1, d["over_limit"]!.GetValue<int>());
        Assert.Equal(2, d["spaces_needed"]!.GetValue<int>()); // [30, 30, ~1000] has a median of 30
        Assert.Equal(260, d["limit"]!.GetValue<double>());
        Assert.Contains("add 2 space(s)", d["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("raises the median", d["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonNode s = d["spaces"]![0]!;
        Assert.Equal("over", s["side"]!.GetValue<string>());
        Assert.NotNull(s["min"]);
        Assert.Contains("raises the median", g06["fix"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Floor_Height_Classes_Advisory_Shows_Its_Bins()
    {
        JsonObject grade = DesignGrader.Grade(SpatialFixtures.Analyze("setpieces"), new GradeOptions { Gametype = "dm", Players = 6, IncludeMetrics = true });
        JsonNode adv = grade["advisories"]!.AsArray().First(x => x!["metric"]!.GetValue<string>() == "floor_height_classes")!;
        JsonNode d = adv["detail"]!;
        _out.WriteLine(d.ToJsonString());
        Assert.Equal(grade["metrics"]!["floor_height_classes"]!.GetValue<double>(), d["classes"]!.GetValue<int>());
        Assert.Equal(d["classes"]!.GetValue<int>(), d["bins"]!.AsArray().Count);
        double min = d["min_area_m2"]!.GetValue<double>();
        Assert.All(d["bins"]!.AsArray(), b => Assert.True(b!["area"]!.GetValue<double>() >= min - 0.01));
        Assert.All(d["near_misses"]!.AsArray(), b => Assert.True(b!["area"]!.GetValue<double>() < min));
        Assert.Contains("0.5 m", d["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("2%", d["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_Spawn_Safety_Counts_Only_Pairs_Live_Together()
    {
        // The payload fixture: checkpoints 1 and 2 (35 m apart along the yard) each link 2 red and 2 blue spawns; two more
        // blue spawns are linked to no checkpoint (live in every stage).
        SpatialAnalysis p = SpatialFixtures.Analyze("payload");
        JsonObject spawns = p.Report()["spawns"]!.AsObject();
        _out.WriteLine(spawns.ToJsonString());
        Assert.True(spawns["staged"]!.GetValue<bool>());
        Assert.True(spawns["pairs_within30_live"]!.GetValue<int>() < spawns["pairs_within30"]!.GetValue<int>());
        Assert.NotNull(DesignMetrics.Extract(p.Report())["spawn_los30_live"]);

        JsonNode g19 = DesignGrader.Table["checks"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "G19")!;
        Assert.Contains("spawn_los30_live", g19["payload"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("spawn_los30_live", g19["rule"]!.ToJsonString(), StringComparison.Ordinal);

        // Elsewhere every pair is live: the two numbers agree.
        SpatialAnalysis loop = SpatialFixtures.Analyze("loop");
        JsonObject ls = loop.Report()["spawns"]!.AsObject();
        Assert.False(ls["staged"]!.GetValue<bool>());
        Assert.Equal(ls["pairs_within30"]!.GetValue<int>(), ls["pairs_within30_live"]!.GetValue<int>());
        var lm = DesignMetrics.Extract(loop.Report());
        Assert.Equal(lm["spawn_los30"], lm["spawn_los30_live"]);
    }
}
