using System;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The walk's function checks (G30-G31) on synthetic-truth levels built by the kit: a flight that serves a route and
/// one built against a wall with nothing beyond its top, two flights into a dead-end pit in a bridge's gap, and two
/// rooms whose only route makes players hop a 1 m step; the readings reach spatial.purpose, the locations and the grade.
/// </summary>
public sealed class SpatialFlightTests
{
    private readonly ITestOutputHelper _out;

    public SpatialFlightTests(ITestOutputHelper output) => _out = output;

    private static JsonObject FlightNear(JsonObject purpose, double x, double z) =>
        purpose["flights"]!.AsArray().Select(f => f!.AsObject())
            .OrderBy(f => Math.Abs(f["pos"]![0]!.GetValue<double>() - x) + Math.Abs(f["pos"]![2]!.GetValue<double>() - z)).First();

    [Fact]
    public void A_Flight_To_A_Gallery_Serves_A_Route_And_One_Against_A_Wall_Does_Not()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("flights");
        JsonObject p = a.Purpose(50);
        _out.WriteLine(p["flights"]!.ToJsonString());
        Assert.True(p["flights_checked"]!.GetValue<int>() >= 2, p.ToJsonString());

        JsonObject up = FlightNear(p, -10, 0);
        Assert.Null(up["fault"]);
        Assert.Equal("route", up["role"]!.GetValue<string>());
        Assert.InRange(up["rise"]!.GetValue<double>(), 3.5, 4.5);
        Assert.True(up["top_exit"]!.GetValue<bool>());

        JsonObject wall = FlightNear(p, 10, -7);
        Assert.NotNull(wall["fault"]);
        Assert.True(wall["counted"]!.GetValue<bool>(), wall.ToJsonString());
        Assert.Contains(wall["kind"]!.GetValue<string>(), new[] { "into_obstacle", "onto_nothing" });
        Assert.Equal(1, p["purposeless_flights"]!.GetValue<int>());

        JsonArray loc = a.Locations("purposeless_flights", 8);
        Assert.Single(loc);
        Assert.Equal("purposeless_flights", loc[0]!["kind"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(loc[0]!["why"]!.GetValue<string>()));
    }

    [Fact]
    public void Flights_Into_A_Dead_End_Pit_Under_A_Bridge_Serve_No_Route()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("pit_bridge");
        JsonObject p = a.Purpose(50);
        _out.WriteLine(p["flights"]!.ToJsonString());
        _out.WriteLine(p["bridges"]!.ToJsonString());
        JsonObject[] inGap = p["flights"]!.AsArray().Select(f => f!.AsObject()).Where(f => f["kind"]?.GetValue<string>() == "in_gap").ToArray();
        Assert.Equal(2, inGap.Length);
        Assert.All(inGap, f =>
        {
            Assert.NotNull(f["bridge_gap"]);
            Assert.True(f["pocket"]!.GetValue<double>() < SpatialAnalysis.FlightPocket);
            Assert.Contains("gap the bridge", f["fault"]!.GetValue<string>(), StringComparison.Ordinal);
        });
        Assert.Equal(2, p["purposeless_flights"]!.GetValue<int>());

        // The grade: G30 fails with the two flights as its places to act on.
        JsonObject g = DesignGrader.Grade(a, new GradeOptions { Gametype = "dm" });
        JsonObject g30 = g["failures"]!.AsArray().Select(f => f!.AsObject()).Single(f => f["id"]!.GetValue<string>() == "G30");
        Assert.Equal(2, g30["values"]!["purposeless_flights"]!.GetValue<double>());
        Assert.Equal(2, g30["locations"]!.AsArray().Count);
        Assert.Contains("kit.stairs", g30["fix"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Route_That_Makes_Players_Hop_Between_Two_Floors_Is_A_Route_Ledge()
    {
        SpatialAnalysis a = SpatialFixtures.Analyze("ledge");
        JsonObject p = a.Purpose(50);
        _out.WriteLine(p["ledges"]!.ToJsonString());
        Assert.Equal(1, p["route_ledges"]!.GetValue<int>());
        JsonObject l = p["ledges"]![0]!.AsObject();
        Assert.InRange(l["step"]!.GetValue<double>(), 0.9, 1.1);
        Assert.True(l["low_landing"]!.GetValue<double>() >= SpatialAnalysis.LedgeMinLanding);
        Assert.True(l["high_landing"]!.GetValue<double>() >= SpatialAnalysis.LedgeMinLanding);
        Assert.InRange(l["pos"]![0]!.GetValue<double>(), 18, 22);
        JsonArray loc = a.Locations("route_ledges", 8);
        Assert.Single(loc);
        Assert.Contains("hop", loc[0]!["why"]!.GetValue<string>(), StringComparison.Ordinal);

        // The flights hall has no route ledge: its gallery is joined by stairs.
        Assert.Equal(0, SpatialFixtures.Analyze("flights").Purpose()["route_ledges"]!.GetValue<int>());
    }

    [Fact]
    public void Lua_And_The_Query_Read_The_Same_Flights()
    {
        JsonObject q = SpatialQueries.Run(SpatialFixtures.Get("flights").Level, "purpose", new JsonObject { ["max"] = 10 });
        Assert.True(q["flights_checked"]!.GetValue<int>() >= 2);
        Assert.Contains("Flight:", q["rules"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(q["connections"]);

        (ScriptRunResult run, ScriptLog log) = ScriptTestFixture.Run(SpatialFixtures.Get("flights").Doc, """
            local p = spatial.purpose{max = 10}
            assert(p.purposeless_flights == 1, "one purposeless flight, got " .. tostring(p.purposeless_flights))
            local bad = 0
            for _, f in ipairs(p.flights) do
              if f.fault then bad = bad + 1 assert(f.top.x ~= nil, "flight ends are vecs") end
            end
            assert(bad >= 1)
            assert(p.connections.areas >= 1)
            """);
        Assert.True(run.Success, run.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries.Select(e => e.Message)));
    }
}
