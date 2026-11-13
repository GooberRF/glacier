using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The Lua <c>spatial</c> and <c>design</c> globals (DESIGN-QUALITY-SPEC §(e)): the same results as the core queries,
/// as tables with vec points, freshness that notices brush edits, and actionable errors.
/// </summary>
public sealed class ScriptSpatialTests
{
    private static EditorDocument BuiltRoom()
    {
        KitTestHarness.KitRun run = KitTestHarness.Run("""
            local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}
            local b = kit.room{name = 'b', pos = {30, 0, 0}, size = {16, 6, 16}}
            kit.corridor{a = a, b = b, width = 4.5, offset = 4}
            for i = 1, 4 do level.place{kind = 'respawn', pos = {i * 3 - 7, 0, -4}} end
            """);
        GeometryBuildService.Apply(run.Doc.Rfl, KitTestHarness.Sealed(run.Doc, sharedBsp: true));
        return run.Doc;
    }

    private static void Ok(EditorDocument doc, string lua)
    {
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, lua);
        Assert.True(r.Success, r.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries.Select(e => e.Message)));
    }

    [Fact]
    public void Point_Queries_Return_Tables_With_Vecs()
    {
        Ok(BuiltRoom(), """
            local h = spatial.raycast{from = {0, 1, 0}, dir = {1, 0, 0}}
            assert(h.hit and math.abs(h.distance - 8) < 0.01, "raycast distance " .. tostring(h.distance))
            assert(math.abs(h.pos.x - 8) < 0.01, "hit pos is a vec")
            assert(spatial.los({-4, 1.6, -4}, {4, 1.6, 4}))
            assert(not spatial.los({0, 1.6, 0}, {0, 1.6, 30}))
            local p = spatial.point_info({0, 1, 0})
            assert(not p.in_solid and math.abs(p.headroom - 6) < 0.01)
            assert(spatial.clearance({0, 0, 0}).fits)
            assert(spatial.freshness().state == "unknown" or spatial.freshness().state == "built")
            """);
    }

    [Fact]
    public void Measurements_And_The_Grader_Run_From_Lua()
    {
        Ok(BuiltRoom(), """
            local w = spatial.walk{from = {0, 0, 0}}
            assert(w.from.reachable_fraction == 1, "all floor reachable")
            assert(#w.unreachable_spawns == 0)
            local g = spatial.graph{kind = "cell", scale = 8}
            assert(#g.chokepoints >= 1, "the corridor is a chokepoint")
            local r = spatial.routes{a = {0, 0, 0}, b = {30, 0, 0}, k = 3}
            assert(r.disjoint_routes == 1 and r.walk_distance > 25)
            assert(r.shortest.waypoints[1].x ~= nil, "waypoints are vecs")
            local area = spatial.area_report{around = {0, 0, 0}, radius = 8}
            assert(area.floor_area > 150 and area.targets ~= nil)
            assert(#spatial.locations("chokepoints").locations >= 1)
            local grade = design.grade{gametype = "dm", players = 4}
            assert(grade.verdict == "PASS" or grade.verdict == "REVIEW" or grade.verdict == "FAIL")
            assert(#grade.failures == grade.failures_count)
            for _, f in ipairs(grade.failures) do assert(f.fix ~= nil and f.id ~= nil) end
            assert(#design.checks() >= 30)
            """);
    }

    [Fact]
    public void Freshness_Notices_Brush_Edits_And_Build_Requests()
    {
        Ok(BuiltRoom(), """
            local b = level.brushes[1] local p = b.pos b:set_pos(p.x, p.y, p.z + 1)
            assert(spatial.freshness().state == "stale", "a moved brush makes the geometry stale")
            local w = spatial.walk{build = "sealed"}
            assert(w.geometry.state == "built", "build = sealed measures the current brushes: " .. w.geometry.state)
            """);
    }

    [Fact]
    public void Bad_Arguments_And_Missing_Geometry_Explain_Themselves()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(BuiltRoom(), "spatial.graph{kind = 'cell', scale = 6}");
        Assert.False(r.Success);
        Assert.Contains("8 m or 4 m", r.Error!.ToDisplayString(), System.StringComparison.Ordinal);

        (r, _) = ScriptTestFixture.Run(BuiltRoom(), "spatial.walk{fromm = {0, 0, 0}}");
        Assert.False(r.Success);
        Assert.Contains("from", r.Error!.ToDisplayString(), System.StringComparison.Ordinal);

        (r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(KitTestHarness.FixedDate), "spatial.walk()");
        Assert.False(r.Success);
        Assert.Contains("no compiled geometry", r.Error!.ToDisplayString(), System.StringComparison.Ordinal);
    }
}
