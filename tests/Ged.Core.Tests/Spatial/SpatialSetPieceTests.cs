using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// E9 set-piece detection (DESIGN-QUALITY-SPEC §(e)) on kit-built levels with known answers: a through-truss bridge
/// measures its whole deck (the study's proxy fragments it), an island deck, a one-sided ledge and short pieces are not
/// bridges, a free-standing tower is a landmark with its height, and the numbers reach the grade, the
/// <c>setpieces</c> query and Lua.
/// </summary>
public sealed class SpatialSetPieceTests
{
    private readonly ITestOutputHelper _out;

    public SpatialSetPieceTests(ITestOutputHelper output) => _out = output;

    private JsonObject SetPieces(string fixture)
    {
        JsonObject r = SpatialFixtures.Analyze(fixture).SetPieces(20);
        _out.WriteLine(r.ToJsonString());
        return r;
    }

    [Fact]
    public void A_Truss_Bridge_Between_Ledges_Measures_Its_Whole_Deck()
    {
        JsonObject r = SetPieces("setpieces");
        Assert.Equal(1, r["bridge_count"]!.GetValue<int>());
        JsonNode b = r["bridges"]![0]!;
        Assert.InRange(b["span"]!.GetValue<double>(), 31, 33); // the deck runs 32 m, 2 m onto each ledge
        Assert.InRange(b["gap_width"]!.GetValue<double>(), 24, 30.5); // the free length over the 28 m gap
        Assert.InRange(b["width"]!.GetValue<double>(), 3.5, 5.5);
        Assert.InRange(b["gap_depth"]!.GetValue<double>(), 7.5, 8.5);
        Assert.InRange(b["deck_y"]!.GetValue<double>(), 8.1, 8.15);
        Assert.True(b["under_playable"]!.GetValue<bool>());
        Assert.Equal(b["span"]!.GetValue<double>(), r["bridge_span_max"]!.GetValue<double>());

        // The island deck (no floor at its ends) is a walkway with its reason; the ledge along the wall (a drop on one
        // side) is never a bridge — only its corners, where the front and the end both drop, read as short pieces (a deck
        // read reaches 8.5 m across, the widest bridge deck, so they run up to about 7 m along the ledge).
        JsonArray walkways = r["walkways"]!.AsArray();
        Assert.Contains(walkways, w => w!["reason"]!.GetValue<string>().Contains("island", StringComparison.Ordinal)
            && Math.Abs(w["pos"]![0]!.GetValue<double>() - 6) < 2 && Math.Abs(w["pos"]![2]!.GetValue<double>() - 7) < 2);
        Assert.DoesNotContain(walkways, w => w!["pos"]![2]!.GetValue<double>() < -8 && w["gap_width"]!.GetValue<double>() >= 8);
        Assert.DoesNotContain(r["bridges"]!.AsArray(), b => b!["pos"]![2]!.GetValue<double>() < -8);
    }

    [Fact]
    public void A_Free_Standing_Tower_Is_A_Landmark_With_Its_Height()
    {
        JsonObject r = SetPieces("setpieces");
        JsonArray landmarks = r["landmarks"]!.AsArray();
        Assert.Single(landmarks);
        JsonNode l = landmarks[0]!;
        Assert.InRange(l["height"]!.GetValue<double>(), 14, 16);
        Assert.InRange(l["pos"]![0]!.GetValue<double>(), -8, -4);
        Assert.InRange(l["pos"]![2]!.GetValue<double>(), 5, 9);
        Assert.False(l["open_air"]!.GetValue<bool>()); // indoors, under the hall's ceiling
        Assert.Equal(l["height"]!.GetValue<double>(), r["landmark_height_max"]!.GetValue<double>());
        Assert.NotEmpty(r["largest_spaces"]!.AsArray());
        Assert.InRange(r["atrium_height_max"]!.GetValue<double>(), 12, 24.5); // the tall hall, crossed by the bridge deck
        Assert.True(r["atria"]![0]!["storeys"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public void The_Study_Proxy_Counts_An_Island_Deck_But_E9_Does_Not()
    {
        SortedDictionary<string, double?> m = DesignMetrics.Extract(SpatialFixtures.Analyze("bridge").Report());
        Assert.True(m["bridge_count"] >= 1); // the study's proxy: any long drop-sided piece
        Assert.Equal(0, m["setpiece_bridges"]);
        Assert.Null(m["bridge_span_max"]);
        JsonArray walkways = SpatialFixtures.Analyze("bridge").SetPieces()["walkways"]!.AsArray();
        Assert.Contains(walkways, w => w!["reason"]!.GetValue<string>().Contains("island", StringComparison.Ordinal) && w["gap_width"]!.GetValue<double>() > 25);
    }

    [Fact]
    public void Set_Pieces_Reach_The_Grade_The_Query_And_Lua()
    {
        SpatialLevel level = SpatialFixtures.Get("setpieces").Level;
        JsonObject grade = SpatialQueries.Grade(level, new GradeOptions { Gametype = "dm", Players = 6, IncludeMetrics = true });
        Assert.InRange(grade["metrics"]!["bridge_span_max"]!.GetValue<double>(), 31, 33);
        Assert.InRange(grade["set_pieces"]!["bridge_span_max"]!.GetValue<double>(), 31, 33);
        Assert.InRange(grade["set_pieces"]!["landmark_height_max"]!.GetValue<double>(), 14, 16);

        // Advisories: every player-class target, labelled, never counted in the verdict.
        JsonArray adv = grade["advisories"]!.AsArray();
        Assert.Equal(new[] { "playable_area", "area_per_spawn", "loops8_per1000", "floor_height_classes" }, adv.Select(a => a!["metric"]!.GetValue<string>()));
        Assert.All(adv, a => Assert.True(a!["advisory"]!.GetValue<bool>()));
        Assert.Contains(adv, a => a!["status"]!.GetValue<string>() != "within");
        Assert.Equal(grade["failures"]!.AsArray().Count, grade["failures_count"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(grade["advisory_rule"]!.GetValue<string>()));

        JsonObject q = SpatialQueries.Run(level, "setpieces", new JsonObject { ["max"] = 1 });
        Assert.Equal("stored", q["geometry"]!["measured"]!.GetValue<string>());
        Assert.True(q["walkways"]!.AsArray().Count <= 1);
        Assert.False(string.IsNullOrEmpty(q["rules"]!.GetValue<string>()));

        (ScriptRunResult run, ScriptLog log) = ScriptTestFixture.Run(SpatialFixtures.Get("setpieces").Doc, """
            local s = spatial.setpieces{max = 5}
            assert(s.bridge_count == 1, "one bridge")
            local b = s.bridges[1]
            assert(b.span > 31 and b.span < 33, "span " .. tostring(b.span))
            assert(b.from.x ~= nil and b.to.x ~= nil, "deck ends are vecs")
            assert(math.abs(b.from.x - b.to.x) > 30)
            assert(s.landmarks[1].height > 14)
            """);
        Assert.True(run.Success, run.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries.Select(e => e.Message)));
    }
}
