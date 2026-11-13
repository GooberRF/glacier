using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The design-kit fixes from the first AI level-design trial: level-aware item density, spawn exposure on the
/// grader's own measure, lights scaled to area and headroom (and every leg of a path corridor), real default
/// textures on every kit part, floor sections in chamfered rooms and kit.space parts with bounds.
/// </summary>
public sealed class KitTrialFixesTests
{
    private const string ThreeRooms = """
        local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {24, 7, 20} }
        local east = kit.room{ name = "east", pos = {22, 0, 0}, size = {14, 5, 14} }
        local west = kit.room{ name = "west", pos = {-22, 0, 0}, size = {14, 5, 14} }
        kit.corridor{ a = hall, b = east }
        kit.corridor{ a = hall, b = west }
        """;

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void Items_Follow_The_Level_Density_Across_Calls()
    {
        KitRun run = Run(ThreeRooms + """
            kit.spawn_points{ rooms = { hall, east, west }, count = 5 }
            local a = kit.items{ room = hall, seed = 1 }
            local b = kit.items{ room = east, seed = 2 }
            local c = kit.items{ room = west, seed = 3 }
            local again = kit.items{ seed = 4 }
            assert(type(a.count) == "number" and type(a.by_category) == "table")
            return string.format("%d;%d;%d;%d;%.4f;%.4f;%.4f", a.count, b.count, c.count, again.count, c.level_per100, a.per100, again.level_per100)
            """);
        double[] v = run.Result.ReturnValue!.Split(';').Select(Num).ToArray();
        Assert.True(v[0] > 0 && v[1] > 0 && v[2] > 0, run.Result.ReturnValue);
        Assert.Equal(0, v[3]);                         // the level is already stocked: a whole-level call adds nothing
        Assert.InRange(v[4], 1.2, 2.0);                // per-room calls add up to the level target
        Assert.InRange(v[5], 1.2, 2.0);                // per100 counts only the call's own spaces
        Assert.Equal(run.Doc.Objects.Count(o => o.Kind == LevelObjectKind.Item), (int)(v[0] + v[1] + v[2]));
    }

    [Fact]
    public void Spawn_Exposure_Is_The_Graders_Measure()
    {
        KitRun run = Run(ThreeRooms + """
            kit.pillar_row{ from = {-6, 0, 4}, to = {6, 0, 4}, count = 3, height = 7, size = 1.5 }
            kit.pillar_row{ from = {-6, 0, -4}, to = {6, 0, -4}, count = 3, height = 7, size = 1.5 }
            local s = kit.spawn_points{ rooms = { hall, east, west }, count = 6, seed = 2 }
            ops.build()
            local g = spatial.spawn_report()
            return string.format("%.4f;%.4f;%s", s.report.exposure_median, g.level.exposure40.p50, s.report.measure)
            """);
        string[] p = run.Result.ReturnValue!.Split(';');
        Assert.Contains("grader", p[2], StringComparison.Ordinal);
        // (G19 itself is held by the population gate layouts; three bare boxes cannot shelter every spawn.)
        // Same measure, floor set and stable targets: the planned median is the graded one (the report rounds to 0.001).
        Assert.InRange(Math.Abs(Num(p[0]) - Num(p[1])), 0, 0.01);
    }

    [Fact]
    public void Lights_Scale_With_Area_And_Light_Every_Leg_Of_A_Path_Corridor()
    {
        KitRun run = Run("""
            local side = kit.room{ name = "side", pos = {0, 0, 0}, size = {16, 5, 20} }
            local hall = kit.room{ name = "hall", pos = {0, 0, 40}, size = {24, 10, 24} }
            local c = kit.corridor{ name = "bend", path = { {20, 0, -20}, {50, 0, -20}, {50, 0, 20} } }
            local ls = kit.light_room{ room = side }
            local lh = kit.light_room{ room = hall }
            local lc = kit.light_room{ room = c }
            local lp = kit.light_path{ along = c, spacing = 6 }
            local leg2 = 0
            for _, l in ipairs(lc) do if l.pos.x > 47 and l.pos.z > -17 then leg2 = leg2 + 1 end end
            return string.format("%d;%d;%d;%d;%d", #ls, #lh, #lc, leg2, #lp)
            """);
        int[] v = run.Result.ReturnValue!.Split(';').Select(int.Parse).ToArray();
        Assert.InRange(v[0] * 100.0 / (16 * 20), 1.7, 3.0);   // a 320 m² side room: about 2 per 100 m², not a 5 m grid of 12
        Assert.InRange(v[1] * 100.0 / (24 * 24), 1.7, 2.6);   // a tall hall gets fewer per m²
        Assert.True(v[2] >= 15, $"a 70 m two-leg corridor got only {v[2]} lights");
        Assert.True(v[3] >= 7, $"the second leg got only {v[3]} lights");
        Assert.True(v[4] >= 10, $"light_path along the corridor placed {v[4]} lights");
    }

    [Fact]
    public void Canyon_Path_Lights_Hang_Low_On_The_Walls()
    {
        KitRun run = Run("""
            local c = kit.canyon{ name = "gorge", path = { {0, 0, -30}, {0, 0, 30} }, width = 20, height = 20, seed = 1 }
            local lights = kit.light_path{ along = c, spacing = 10 }
            local out = {}
            for _, l in ipairs(lights) do out[#out + 1] = string.format("%.2f,%.2f", l.pos.x, l.pos.y) end
            return table.concat(out, ";")
            """);
        var pts = run.Result.ReturnValue!.Split(';').Select(s => s.Split(',').Select(Num).ToArray()).ToList();
        Assert.NotEmpty(pts);
        Assert.All(pts, p => Assert.InRange(p[1], 2.5, 4.6));        // at most 4.5 m above the floor (was 12 m)
        Assert.Contains(pts, p => p[0] > 4);                         // mounted toward the walls, both sides
        Assert.Contains(pts, p => p[0] < -4);
    }

    [Fact]
    public void Kit_Parts_Get_Real_Default_Textures()
    {
        KitRun run = Run("""
            local r = kit.room{ name = "r", pos = {0, 0, 0}, size = {20, 6, 20} }
            kit.stairs{ name = "st", pos = {-5, 0, 0}, dir = "+z", rise = 2.5, width = 3 }
            kit.ramp{ name = "rp", pos = {5, 0, 0}, dir = "+z", rise = 1.5, width = 3 }
            kit.platform{ name = "pf", pos = {0, 0, 6}, size = {4, 3}, height = 1 }
            kit.rock_wall{ name = "rw", from = {-6, 0, -6}, to = {6, 0, -6}, height = 2, depth = 1.5, seed = 1 }
            """);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = new GedObjectMetadataService(run.Doc).AllTags();
        var kitBrushes = new BrushEditor(run.Doc).Brushes.Where(b => tags.TryGetValue(b.Uid, out var t) && t.ContainsKey("kit")).ToList();
        Assert.NotEmpty(kitBrushes);
        foreach (Brush b in kitBrushes)
        {
            foreach (Face f in b.Geometry.Faces)
            {
                string tex = f.Texture >= 0 && f.Texture < b.Geometry.Textures.Count ? b.Geometry.Textures[f.Texture] : string.Empty;
                Assert.False(string.IsNullOrEmpty(tex) || tex.StartsWith("rck_default", StringComparison.OrdinalIgnoreCase),
                    $"{tags[b.Uid]["kit"]} brush {b.Uid} has a placeholder face ({tex})");
            }
        }
    }

    [Fact]
    public void Floor_Section_Fits_A_Chamfered_Room_And_Space_Parts_Have_Bounds()
    {
        KitRun run = Run("""
            local room = kit.shaped_room{ name = "court", pos = {0, 0, 0}, size = {24, 18}, chamfer = 4, height = 8 }
            local dock = kit.floor_section{ name = "dock", room = room, min = {room.min.x + 0.5, 0, room.min.z + 0.5}, max = {room.max.x - 0.5, 0, room.min.z + 6}, rise = 1 }
            local sp = kit.space{ archetype = "sunken_court", name = "turb", pos = {0, 0, 60} }
            local n, bad = 0, {}
            for k, p in pairs(sp.parts) do
              n = n + 1
              if type(p) ~= "table" or p.min == nil or p.max == nil then bad[#bad + 1] = k end
            end
            return string.format("%s;%d;%d;%s;%.2f", tostring(dock.clipped), #dock.outline, n, table.concat(bad, ","), room.chamfer)
            """);
        string[] p = run.Result.ReturnValue!.Split(';');
        Assert.Equal("true", p[0]);                     // the rectangle crossed the chamfers and was cut to the plan
        Assert.True(int.Parse(p[1], CultureInfo.InvariantCulture) >= 5, run.Result.ReturnValue);
        Assert.True(int.Parse(p[2], CultureInfo.InvariantCulture) > 3, run.Result.ReturnValue);
        Assert.Equal(string.Empty, p[3]);               // every part has bounds
        Assert.Equal(4.0, Num(p[4]), 3);
        AssertWatertight(run.Doc, sharedBsp: true);
    }
}
