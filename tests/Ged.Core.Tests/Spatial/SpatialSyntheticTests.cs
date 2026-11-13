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
/// Synthetic-truth gates of the measurement core (DESIGN-QUALITY-SPEC §(g) Q1): kit-generated levels with known answers
/// — a loop, a chokepoint, a pocket, a bridge, cover, an open-air platform field and a payload chain — measured
/// through the same code the grader and the agent queries use.
/// </summary>
public sealed class SpatialSyntheticTests
{
    private readonly ITestOutputHelper _out;

    public SpatialSyntheticTests(ITestOutputHelper output) => _out = output;

    private SortedDictionary<string, double?> Metrics(string fixture)
    {
        SortedDictionary<string, double?> m = DesignMetrics.Extract(SpatialFixtures.Analyze(fixture).Report());
        _out.WriteLine(fixture + ": " + string.Join(", ", m.Where(kv => kv.Value is not null).Select(kv => $"{kv.Key}={kv.Value}")));
        return m;
    }

    [Fact]
    public void A_Ring_Of_Rooms_Is_One_Loop_Without_Chokepoints()
    {
        SortedDictionary<string, double?> m = Metrics("loop");
        Assert.True(m["loops8"] >= 1, $"loops8 = {m["loops8"]}");
        Assert.Equal(0, m["chokes_cell8"]);
        Assert.Equal(0, m["pocket_area_cell8"]);
        Assert.True(m["pair_routes_ge2"] is null or > 0.5, $"pair routes >= 2: {m["pair_routes_ge2"]}");
    }

    [Fact]
    public void A_Single_Corridor_Between_Two_Rooms_Is_A_Chokepoint()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("choke");
        SortedDictionary<string, double?> m = Metrics("choke");
        Assert.True(m["chokes_cell8"] >= 1, $"chokes = {m["chokes_cell8"]}");
        Assert.Equal(0, m["loops8"]);

        // The chokepoint locations lie on the corridor (between the rooms' facing walls at x = 12 and x = 48).
        JsonArray chokes = a.Locations("chokepoints");
        Assert.NotEmpty(chokes);
        Assert.All(chokes, c => Assert.InRange(c!["pos"]![0]!.GetValue<double>(), 8, 52));

        // And the routes query agrees: one node-disjoint route from room to room.
        JsonObject routes = a.Routes(new Vec3d(0, 0, 0), new Vec3d(60, 0, 0), 3);
        Assert.Equal(1, routes["disjoint_routes"]!.GetValue<int>());
        Assert.InRange(routes["walk_distance"]!.GetValue<double>(), 55, 70);
    }

    [Fact]
    public void A_Dead_End_Room_Is_A_Pocket()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("pocket");
        SortedDictionary<string, double?> m = Metrics("pocket");
        _out.WriteLine(a.WalkQuery().ToJsonString());
        Assert.InRange(m["pocket_area_cell8"]!.Value, 0.02, 0.12);
        JsonArray pockets = a.Locations("pockets");
        Assert.NotEmpty(pockets);
        Assert.InRange(pockets[0]!["pos"]![0]!.GetValue<double>(), -40, -8); // the dead end lies west of room a
        Assert.True(m["loops8"] >= 1);
    }

    [Fact]
    public void A_Deck_Over_A_Hall_Is_A_Bridge()
    {
        SortedDictionary<string, double?> m = Metrics("bridge");
        Assert.True(m["bridge_count"] >= 1, $"bridges = {m["bridge_count"]}");
        Assert.InRange(m["bridge_len_max"]!.Value, 20, 31);
        Assert.InRange(m["bridge_width_max"]!.Value, 2, 4);
    }

    [Fact]
    public void Free_Standing_Blocks_Are_Cover()
    {
        SortedDictionary<string, double?> cover = Metrics("cover"), empty = Metrics("empty");
        Assert.True(cover["cover_fraction"] > empty["cover_fraction"] + 0.05, $"cover {cover["cover_fraction"]} vs empty {empty["cover_fraction"]}");
        Assert.True(cover["dist_cover_p90"] < empty["dist_cover_p90"], $"p90 {cover["dist_cover_p90"]} vs {empty["dist_cover_p90"]}");
        Assert.True(cover["dressing_per100"] >= empty["dressing_per100"]);
    }

    [Fact]
    public void Floating_Platforms_Are_The_Open_Air_Class()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("open_air");
        SortedDictionary<string, double?> m = DesignMetrics.Extract(a.Report());
        _out.WriteLine(string.Join(", ", m.Where(kv => kv.Key.Contains("fraction", StringComparison.Ordinal)).Select(kv => $"{kv.Key}={kv.Value}")));
        _out.WriteLine(a.WalkQuery().ToJsonString());
        Assert.True(m["open_air_fraction"] >= 0.9, $"open air {m["open_air_fraction"]}");
        Assert.True(m["void_edge_fraction"] >= 0.04, $"void edges {m["void_edge_fraction"]}");
        Assert.Equal("open_air", DesignGrader.LevelClass(a, m, "dm"));
        Assert.Equal("open_air", DesignGrader.Grade(a)["class"]!.GetValue<string>());

        // An enclosed level is not.
        SpatialAnalysis loop = SpatialFixtures.Analyze("loop");
        Assert.Equal("standard", DesignGrader.LevelClass(loop, DesignMetrics.Extract(loop.Report()), "dm"));
    }

    [Fact]
    public void A_Payload_Chain_Has_Segments_Boundaries_And_Spawn_Hand_Off()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("payload");
        JsonObject p = a.Report()["payload"]!.AsObject();
        _out.WriteLine(p.ToJsonString());
        Assert.Equal(2, p["checkpoints"]!.GetValue<int>());
        Assert.Equal(3, p["segments"]!.GetValue<int>());
        Assert.Equal(1.0, p["handoff_coverage"]!.GetValue<double>());
        Assert.InRange(p["segment_balance"]!.GetValue<double>(), 0.5, 1.0);
        Assert.True(p["boundary_routes_min"]!.GetValue<int>() >= 3, "a 24 m-wide yard has many routes across each checkpoint");

        JsonObject g = DesignGrader.Grade(a);
        Assert.Equal("payload", g["class"]!.GetValue<string>());
        Assert.Equal("payload", g["gametype"]!.GetValue<string>());
        List<string> failed = DesignGrader.FailedIds(g);
        Assert.Contains("P01", failed); // 2 checkpoints: payload maps have 3-5
        Assert.DoesNotContain("P06", failed);
        Assert.DoesNotContain("G12", failed.Concat(g["passed"]!.AsArray().Select(x => x!.GetValue<string>()))); // replaced by P05
        Assert.NotEmpty(a.Locations("boundaries"));
    }

    [Fact]
    public void Measurements_Do_Not_Depend_On_Threads_Or_Section_Order()
    {
        SpatialLevel level = SpatialFixtures.Get("loop").Level;
        string one = SpatialAnalysis.Run(level, new SpatialOptions { Parallelism = 1 }).Report().ToJsonString();
        var shuffled = SpatialAnalysis.Run(level, new SpatialOptions { Parallelism = 4 });
        _ = shuffled.ItemReportQuery(); // the sampled sections first, in another order
        _ = shuffled.Sightlines();
        Assert.Equal(one, shuffled.Report().ToJsonString());
    }

    [Fact]
    public void Point_Queries_Answer_From_The_Compiled_Faces()
    {
        SpatialWorld w = SpatialWorld.For(SpatialFixtures.Get("empty").Level.Geometry);
        JsonObject hit = w.Raycast(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0));
        Assert.True(hit["hit"]!.GetValue<bool>());
        Assert.Equal(16, hit["distance"]!.GetValue<double>(), 2); // the hall is 32 m wide
        Assert.True(w.LineOfSight(new Vec3d(-10, 1.6, -10), new Vec3d(10, 1.6, 10)));
        Assert.False(w.LineOfSight(new Vec3d(0, 1.6, 0), new Vec3d(40, 1.6, 0)));

        JsonObject inside = w.PointInfo(new Vec3d(0, 1, 0));
        Assert.False(inside["in_solid"]!.GetValue<bool>());
        Assert.Equal(6, inside["headroom"]!.GetValue<double>(), 2);
        Assert.True(w.PointInfo(new Vec3d(0, -3, 0))["in_solid"]!.GetValue<bool>());

        Assert.True(w.Clearance(new Vec3d(0, 0, 0))["fits"]!.GetValue<bool>());
        JsonObject wall = w.Clearance(new Vec3d(15.8, 0, 0));
        Assert.False(wall["fits"]!.GetValue<bool>());
        Assert.Contains("wall", wall["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Walk_Reports_Spawns_Off_The_Floor_And_Reachability()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("choke");
        JsonObject w = a.WalkQuery(new Vec3d(0, 0, 0));
        Assert.Empty(w["unreachable_spawns"]!.AsArray());
        Assert.Equal(1.0, w["from"]!["reachable_fraction"]!.GetValue<double>(), 3);
        Assert.InRange(w["from"]!["farthest"]!["walk_distance"]!.GetValue<double>(), 55, 90);
        Assert.Equal("built", w["geometry"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void Area_Report_And_Graph_Queries_Cover_A_Region()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("loop");
        JsonObject r = a.AreaReport(SpatialRegion.Around(new Vec3d(0, 0, 0), 9));
        Assert.InRange(r["floor_area"]!.GetValue<double>(), 200, 270); // one 16 m room
        Assert.InRange(r["headroom"]!["p50"]!.GetValue<double>(), 5.9, 6.1);
        Assert.NotNull(r["targets"]);

        JsonObject g = a.Graph("cell", 8, null, 5);
        Assert.True(g["truncated"]!.GetValue<bool>());
        Assert.Equal(5, g["node_list"]!.AsArray().Count);
        Assert.True(a.Graph("space")["spaces"]!.GetValue<int>() >= 4);
        Assert.Throws<SpatialException>(() => a.Graph("cell", 6));
    }
}
