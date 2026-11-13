using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Editing.Organic;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The grader's floor set and sampling on synthetic levels with known answers: playable floor is standable floor reached
/// on foot (a stack top reached only by dropping off it and a narrow trim band are out; a crate stepped onto is in), eye
/// points stand under the ceiling, stable sampling keeps its spots when the level changes elsewhere, payload pockets are
/// located and a payload map's advisories use the payload budget, and bridge set pieces read wide railed decks, stop
/// spans at approach corridors and put the landmark bridge first. The study settings keep the study's floor set.
/// </summary>
public sealed class SpatialStandableTests
{
    private readonly ITestOutputHelper _out;

    public SpatialStandableTests(ITestOutputHelper output) => _out = output;

    private static bool Near(Vec3d p, double x0, double x1, double y0, double y1, double z0, double z1) =>
        p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && p.Z >= z0 && p.Z <= z1;

    private static int Playable(SpatialAnalysis a, Func<Vec3d, bool> where) => a.Walk.PlayIdx.Count(i => where(a.Walk.S[i].P));

    [Fact]
    public void Playable_Floor_Is_Standable_Floor_Reached_On_Foot()
    {
        SpatialAnalysis study = SpatialFixtures.Analyze("standable", new SpatialOptions { StandableFloor = false, StableSampling = false });
        SpatialAnalysis a = SpatialFixtures.Analyze("standable");
        static bool Stack(Vec3d p) => Near(p, 5.2, 10.8, 4.8, 5.2, 5.2, 10.8);
        static bool Trim(Vec3d p) => Near(p, -9.8, 9.8, 1.0, 1.4, -16, -15.6);
        static bool Crate(Vec3d p) => Near(p, -8.8, -7.2, 1.0, 1.4, 7.2, 8.8);
        _out.WriteLine($"study: stack {Playable(study, Stack)} trim {Playable(study, Trim)} crate {Playable(study, Crate)}; standable: stack {Playable(a, Stack)} trim {Playable(a, Trim)} crate {Playable(a, Crate)}");

        // The study counted the stack top (joined to the floor by a drop off its edge) and the trim band.
        Assert.True(Playable(study, Stack) > 50);
        Assert.True(Playable(study, Trim) > 10);

        // Standable floor: neither, but the crate (a step up from the floor) stays.
        Assert.Equal(0, Playable(a, Stack));
        Assert.Equal(0, Playable(a, Trim));
        Assert.True(Playable(a, Crate) >= 4);
        Assert.True(a.PlayableArea < study.PlayableArea - 30);

        // Eye points stand under the ceiling: the shelf under the 1.4 m soffit is floor, but no sightline starts there.
        static bool Shelf(Vec3d p) => Near(p, -15.8, -10.2, 0.9, 1.1, -10.8, -5.2);
        Assert.True(Playable(a, Shelf) > 20);
        Assert.DoesNotContain(a.EyeIdx, i => Shelf(a.Walk.S[i].P));
        Assert.All(a.EyeIdx, i => Assert.True(a.Walk.S[i].Headroom >= SpatialAnalysis.Eye + SpatialAnalysis.EyeClearance));
        Assert.Contains(study.EyeIdx, i => Shelf(study.Walk.S[i].P));

        // The study settings are the study's floor set and random draws.
        Assert.False(SpatialOptions.Study.StandableFloor);
        Assert.False(SpatialOptions.Study.StableSampling);
        Assert.False(SpatialOptions.Study.MovementLinks);
    }

    [Fact]
    public void Stable_Sampling_Keeps_Its_Spots_When_The_Level_Changes_Elsewhere()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("standable");
        SpatialAnalysis b = SpatialFixtures.Analyze("standable_cover"); // plus one cover block in a corner
        JsonObject ra = a.Report(), rb = b.Report();
        Assert.Equal("stable", ra["sight"]!["sampling"]!.GetValue<string>());

        // The first spots of both levels are the same floor points, except where the block now stands.
        static HashSet<(double, double, double)> Spots(SpatialAnalysis s) =>
            s.StableOrder.Take(SpatialAnalysis.StableSpots).Select(i => (s.Walk.S[i].P.X, s.Walk.S[i].P.Y, s.Walk.S[i].P.Z)).ToHashSet();
        HashSet<(double, double, double)> sa = Spots(a), sb = Spots(b);
        int shared = sa.Count(sb.Contains);
        _out.WriteLine($"spots {sa.Count} / {sb.Count}, shared {shared}");
        Assert.True(shared >= 0.97 * sa.Count, $"only {shared} of {sa.Count} spots kept");

        // The measures move only by the block's own effect (a few hundredths), not by a redraw of every sample.
        double Engagement(JsonObject r) => r["sight"]!["engagement_distance"]!["p50"]!.GetValue<double>();
        double Within40(JsonObject r) => r["sight"]!["pair_visible_fraction_within40"]!.GetValue<double>();
        Assert.InRange(Math.Abs(Engagement(ra) - Engagement(rb)), 0, 0.75);
        Assert.InRange(Math.Abs(Within40(ra) - Within40(rb)), 0, 0.03);

        // Deterministic: the same level measures the same.
        Assert.Equal(ra["sight"]!.ToJsonString(), SpatialFixtures.Analyze("standable").Report()["sight"]!.ToJsonString());
    }

    [Fact]
    public void Payload_Pockets_Are_Located_Per_Segment_And_Advisories_Use_The_Payload_Budget()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("payload_pocket");
        JsonArray pockets = a.Locations("segment_pockets", 8);
        _out.WriteLine(pockets.ToJsonString());
        JsonNode nook = Assert.Single(pockets, p => Math.Abs(p!["pos"]![0]!.GetValue<double>() + 36) < 6 && p["pos"]![2]!.GetValue<double>() > 16)!;
        Assert.Equal(0, nook["segment"]!.GetValue<int>());
        Assert.True(nook["share"]!.GetValue<double>() > 0);

        JsonObject grade = DesignGrader.Grade(a, new GradeOptions { Gametype = "payload", Players = 12 });
        Assert.Equal("payload", grade["class"]!.GetValue<string>());
        Assert.Equal(new[] { 12000.0, 24000.0 }, grade["targets"]!["playable_area"]!.AsArray().Select(x => x!.GetValue<double>()));
        Assert.Equal(new[] { 180.0, 800.0 }, grade["targets"]!["area_per_spawn"]!.AsArray().Select(x => x!.GetValue<double>()));
        Assert.Equal(new[] { 0.75, 1.85 }, grade["targets"]!["loops8_per1000"]!.AsArray().Select(x => x!.GetValue<double>())); // player class M
        JsonNode area = grade["advisories"]!.AsArray().Single(x => x!["metric"]!.GetValue<string>() == "playable_area")!;
        Assert.Equal(12000, area["target"]![0]!.GetValue<double>());
        Assert.StartsWith("payload", grade["targets_basis"]!.GetValue<string>(), StringComparison.Ordinal);

        // P03 lists its pockets first (when it fails), so the fix has a place.
        JsonNode p03 = DesignGrader.Table["checks"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "P03")!;
        Assert.Equal("segment_pockets", p03["locate"]![0]!.GetValue<string>());
        if (grade["failures"]!.AsArray().FirstOrDefault(f => f!["id"]!.GetValue<string>() == "P03") is JsonNode failed)
        {
            Assert.Equal("segment_pockets", failed["locations"]![0]!["kind"]!.GetValue<string>());
        }

        // Graded without the level classes (a standard map), the player-class row stands.
        JsonObject dm = DesignGrader.Grade(a, new GradeOptions { Gametype = "dm", Players = 12, Classes = false });
        Assert.Equal(new[] { 4400.0, 6400.0 }, dm["targets"]!["playable_area"]!.AsArray().Select(x => x!.GetValue<double>()));
    }

    [Fact]
    public void A_Railed_Road_Bridge_Is_The_Landmark_And_Spans_Stop_At_Approach_Corridors()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("cartbridge");
        JsonObject r = a.SetPieces(20);
        _out.WriteLine(r.ToJsonString());
        JsonArray bridges = r["bridges"]!.AsArray();
        JsonNode road = Assert.Single(bridges, b => Math.Abs(b!["pos"]![2]!.GetValue<double>() + 8) < 2)!;
        JsonNode foot = Assert.Single(bridges, b => Math.Abs(b!["pos"]![2]!.GetValue<double>() - 10) < 2)!;

        // The rails along the 8 m deck do not split it: one bridge, its full width, over the whole gap.
        Assert.InRange(road["width"]!.GetValue<double>(), 7.5, 9.0);
        Assert.InRange(road["gap_width"]!.GetValue<double>(), 26, 33);
        Assert.InRange(road["gap_depth"]!.GetValue<double>(), 11, 13);
        Assert.DoesNotContain(r["walkways"]!.AsArray(), w => Math.Abs(w!["pos"]![2]!.GetValue<double>() + 8) < 4 && w["area"]!.GetValue<double>() > 20);

        // The footbridge's deck runs on into walled corridors: its span ends at the corridor mouths, not 6 m into them.
        Assert.InRange(foot["span"]!.GetValue<double>(), 30, 34.5);

        // The landmark bridge leads the list and the grade's headline.
        Assert.Same(road, bridges[0]);
        SortedDictionary<string, double?> m = DesignMetrics.Extract(a.Report());
        Assert.Equal(road["span"]!.GetValue<double>(), m["bridge_span"]);
        Assert.Equal(road["width"]!.GetValue<double>(), m["bridge_width"]);
        Assert.Equal(road["gap_depth"]!.GetValue<double>(), m["bridge_gap_depth"]);
    }

    [Fact]
    public void A_Deep_Crossing_Outranks_A_Long_Low_Catwalk()
    {
        static SpatialAnalysis.SetPieceBridge B(double span, double gap, double width, double depth) =>
            new(default, default, default, span, gap, width, depth, 0, true, null, null, null);
        var catwalk = B(26.6, 26.6, 2, 4.5);
        var road = B(22, 22, 8, 12);
        var foot = B(22, 22, 3, 12);
        Assert.True(SpatialAnalysis.Significance(road) > SpatialAnalysis.Significance(foot));
        Assert.True(SpatialAnalysis.Significance(foot) > SpatialAnalysis.Significance(catwalk));
    }
}
