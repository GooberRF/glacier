using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The design kit's proportion rules, non-box shaping primitives, structures (beam, truss, bridge, landmark), openings,
/// the cave-mouth and organic-moderation checks, the road and the ground provider (DESIGN-QUALITY-SPEC §c.1-2 to c.1-9).
/// Every construction is measured on sealed builds under both build methods.
/// </summary>
public sealed class KitStructTests
{
    private static string[] Parts(KitRun run) => run.Result.ReturnValue!.Split(':');

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string Value(string body)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(FixedDate), "local kit = ged.import('kit')\n" + body);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r.ReturnValue!;
    }

    // ---- proportions ---------------------------------------------------------------------------

    [Fact]
    public void Proportion_Follows_The_Exemplar_Rules()
    {
        string v = Value(
            "local function f(t) local p = kit.proportion(t) return p.kind .. '=' .. p.height .. '[' .. p.min .. '-' .. p.max .. ']' end\n" +
            "return table.concat({ f{area = 50}, f{size = {16, 16}}, f{area = 900}, f{kind = 'corridor'}, f{kind = 'hub', area = 250} }, ' ')");
        Assert.Equal("room=5.5[3.5-6] hall=7.5[6-9] atrium=12[12-24] corridor=4.5[4-5] hub=9[9-16]", v);
        string check = Value("local p = kit.proportion{area = 50, height = 11} return tostring(p.ok) .. '|' .. p.message");
        Assert.StartsWith("false|11 m is outside the room range 3.5-6 m", check, StringComparison.Ordinal);
        Assert.Equal("true", Value("return tostring(kit.proportion{area = 250, height = 8}.ok)"));
    }

    [Fact]
    public void Room_Without_A_Height_Takes_The_Proportion_Height()
    {
        KitRun run = Run("local r = kit.room{name = 'r', pos = {0, 0, 0}, size = {16, nil, 16}} return r.max.y .. ':' .. r.size.y");
        Assert.Equal("7.5:7.5", run.Result.ReturnValue); // 256 m² is a hall: 6 + 3 x 156 / 300 = 7.56, to 0.5 m
        AssertAccepted(run);
    }

    // ---- non-box rooms -------------------------------------------------------------------------

    [Theory]
    [InlineData("shape = 'L', size = {20, 16}, ceiling = 'stepped', region = {-9, -7, 9, -2}", 0.0)]
    [InlineData("shape = 'T', size = {20, 16}, chamfer = 1.5, ceiling = 'gable', rise = 3, region = {-9, 2, 9, 7}", 0.02)]
    [InlineData("shape = 'U', size = {24, 18}, chamfer = 2", 0.02)]
    [InlineData("shape = 'cross', size = {22, 22}, ceiling = 'coffered', region = {-4, -4, 4, 4}", 0.0)]
    [InlineData("shape = 'octagon', size = {20, 20}, ceiling = 'shed', rise = 3", 0.2)]
    [InlineData("shape = 'rect', size = {16, 12}, chamfer = 2, ceiling = 'beamed'", 0.05)]
    [InlineData("footprint = {{-8, -6}, {8, -6}, {10, 2}, {0, 9}, {-10, 2}}, ceiling = 'stepped', inset = 3", 0.3)]
    [InlineData("footprint = {{-10, -8}, {10, -8}, {10, 8}, {4, 8}, {0, 0}, {-4, 8}, {-10, 8}}, height = 6", 0.05)]
    public void Shaped_Room_Variants_Build_Sealed(string options, double offAxisMin)
    {
        KitRun run = Run($"local r = kit.shaped_room{{name = 'r', pos = {{0, 0, 0}}, {options}}}\n" +
                         "return #r.brushes .. ':' .. r.height .. ':' .. r.top_y .. ':' .. r.area");
        string[] p = Parts(run);
        Assert.True(int.Parse(p[0], CultureInfo.InvariantCulture) >= 1);
        (CompiledLevel shared, _) = AssertAccepted(run);
        SpaceMetrics m = SpaceMetricsProbe.Measure(shared.Geometry);
        Assert.True(m.OffAxisShare >= offAxisMin, $"{options}: {m}");
        float top = shared.Geometry.Vertices.Max(v => v.Y);
        Assert.True(Math.Abs(top - D(p[2])) < 0.01, $"{options}: compiled top {top}, record top_y {p[2]}");
    }

    [Fact]
    public void Shaped_Room_Errors_Name_The_Fix()
    {
        Assert.Contains("lies outside the room's plan", Fail("kit.shaped_room{pos = {0, 0, 0}, shape = 'L', size = {20, 16}, ceiling = 'stepped', region = {2, 2, 8, 6}}").Message, StringComparison.Ordinal);
        Assert.Contains("'ceiling' must be", Fail("kit.shaped_room{pos = {0, 0, 0}, size = {8, 8}, ceiling = 'dome'}").Message, StringComparison.Ordinal);
        Assert.Contains("not a simple polygon", Fail("kit.shaped_room{pos = {0, 0, 0}, footprint = {{0, 0}, {12, 0}, {12, 12}, {6, -4}, {0, 12}}}").Message, StringComparison.Ordinal);
        Assert.Contains("leaves no raised bay", Fail("kit.shaped_room{pos = {0, 0, 0}, size = {8, 8}, ceiling = 'stepped', inset = 5}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Alcoves_Pilasters_And_Floor_Sections_Shape_A_Room()
    {
        KitRun run = Run(
            "local r = kit.shaped_room{name = 'r', pos = {0, 0, 0}, size = {24, 16}, chamfer = 2, height = 6}\n" +
            "local a = kit.alcoves{room = r, side = '+z', count = 3}\n" +
            "local p = kit.pilasters{room = r, sides = {'-z', '+x'}, spacing = 4}\n" +
            "local up = kit.floor_section{room = r, min = {6, 0, -6}, max = {10, 0, -2}, rise = 1}\n" +
            "local down = kit.floor_section{room = r, min = {-8, 0, -6}, max = {-2, 0, 0}, rise = -1.25}\n" +
            "return a.count .. ':' .. p.count .. ':' .. tostring(up.needs_steps) .. ':' .. tostring(down.needs_steps) .. ':' .. down.floor_y");
        Assert.Equal("3:8:false:true:-1.25", run.Result.ReturnValue);
        (CompiledLevel shared, _) = AssertAccepted(run);
        Assert.True(shared.Geometry.Vertices.Max(v => v.Z) > 8 + 1.4f, "the alcoves cut 1.5 m into the +z wall");
        Assert.Contains("within 0.5 m of another space", Fail(
            "local r = kit.room{name = 'r', pos = {0, 0, 0}, size = {12, 5, 12}} kit.room{name = 'n', pos = {0, 0, 13.5}, size = {12, 5, 12}}\n" +
            "kit.alcoves{room = r, side = '+z', count = 1}").Message, StringComparison.Ordinal);
    }

    // ---- beams, trusses, bridges, landmarks ----------------------------------------------------

    [Fact]
    public void Beam_And_Truss_Are_Detail_Members()
    {
        KitRun run = Run(
            "kit.room{name = 'r', pos = {0, 0, 0}, size = {24, 10, 12}}\n" +
            "local b = kit.beam{from = {-10, 6, -4}, to = {10, 8, 4}, size = {0.3, 0.5}}\n" +
            "local t1 = kit.truss{from = {-10, 1, 0}, to = {10, 1, 0}, depth = 2, style = 'warren'}\n" +
            "local t2 = kit.truss{from = {-10, 4, -3}, to = {10, 4, -3}, depth = 2, width = 2, style = 'pratt'}\n" +
            "return string.format('%.3f:%d:%d:%d:%d', b.length, t1.panels, #t1.brushes, t2.panels, #t2.brushes)");
        // 20 m at depth 2 = 10 panels. Warren: 2 chords + 2 end posts + 10 diagonals. Pratt x2 planes: (2 + 11 + 10) x 2 + 11 ties.
        Assert.Equal("21.633:10:14:10:57", run.Result.ReturnValue);
        List<Brush> brushes = new BrushEditor(run.Doc).Brushes.ToList();
        Assert.All(brushes.Skip(1), b => Assert.True((b.Flags & (uint)BrushFlags.Detail) != 0));
        AssertAccepted(run);
    }

    private const string Chasm =
        "kit.room{name = 'hall', pos = {0, 0, 0}, size = {48, 14, 16}}\n" +
        "kit.floor_section{min = {-11, 0, -8}, max = {11, 0, 8}, y = 0, rise = -10}\n";

    [Theory]
    [InlineData("truss")]
    [InlineData("arch")]
    [InlineData("girder")]
    [InlineData("plank")]
    public void Bridge_Styles_Span_A_Chasm_At_Landmark_Scale(string style)
    {
        KitRun run = Run(Chasm +
            $"local b = kit.bridge{{name = 'br', from = {{-14, 0.125, 0}}, to = {{14, 0.125, 0}}, width = 3.5, style = '{style}'}}\n" +
            "return string.format('%.2f:%.2f:%.2f:%d:%d', b.span, b.gap, b.drop, #b.warnings, #b.brushes)");
        string[] p = Parts(run);
        Assert.Equal(28, D(p[0]), 2);
        Assert.InRange(D(p[1]), 21, 23.5);   // the unsupported gap is the chasm's 22 m, read through the ground provider
        Assert.InRange(D(p[2]), 9.4, 10.2);  // the drop under mid-span: 10 m chasm minus the deck
        Assert.Equal("0", p[3]);
        Assert.True(int.Parse(p[4], CultureInfo.InvariantCulture) >= 8);
        AssertAccepted(run);
    }

    [Fact]
    public void Bridge_Warns_Below_Landmark_Scale_And_Reads_Ends_From_The_Ground()
    {
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(LevelTemplates.CreateNew(FixedDate), "local kit = ged.import('kit')\n" +
            "kit.room{name = 'hall', pos = {0, 0, 0}, size = {30, 8, 10}}\n" +
            "local b = kit.bridge{name = 'small', from = {-4, 0}, to = {4, 0}, style = 'plank', purpose = 'off'}\n" +
            "local c = kit.bridge{name = 'quiet', from = {-4, 2, 3}, to = {4, 2, 3}, style = 'plank', landmark = false, purpose = 'off'}\n" +
            "return b.from.y .. ':' .. #b.warnings .. ':' .. #c.warnings .. ':' .. b.warnings[1] .. '|' .. b.warnings[2]");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] p = r.ReturnValue!.Split(':', 4);
        Assert.Equal("0", p[0]);  // {x, z} ends take the floor under them
        Assert.Equal("2", p[1]);
        Assert.Equal("0", p[2]);
        Assert.Contains("spans 8 m; landmark bridges span 12-27 m", p[3], StringComparison.Ordinal);
        Assert.Contains("landmark bridges cross 5-12 m drops", p[3], StringComparison.Ordinal);
        Assert.Contains(log.Entries, e => e.Message.Contains("kit.bridge: small spans 8 m", StringComparison.Ordinal));
    }

    [Fact]
    public void Landmarks_Stand_25_To_40_Metres()
    {
        KitRun run = Run(
            "local y = kit.room{name = 'yard', pos = {0, 0, 0}, size = {60, 46, 30}}\n" +
            "local a = kit.landmark{pos = {-18, 0, 0}, height = 32, style = 'tower'}\n" +
            "local b = kit.landmark{pos = {0, 0, 0}, height = 30, base = 6, style = 'pylon'}\n" +
            "local c = kit.landmark{pos = {18, 0, 0}, height = 36, style = 'spire'}\n" +
            "return #a.warnings + #b.warnings + #c.warnings .. ':' .. a.max.y .. ':' .. c.max.y");
        Assert.Equal("0:32:36", run.Result.ReturnValue);
        (CompiledLevel shared, _) = AssertAccepted(run);
        Assert.True(shared.Geometry.Vertices.Max(v => v.Y) >= 36 - 0.01f);
        string warned = Value("kit.room{pos = {0, 0, 0}, size = {20, 60, 20}} return kit.landmark{pos = {0, 0, 0}, height = 50}.warnings[1]");
        Assert.Contains("exemplar outdoor landmarks stand 25-40 m", warned, StringComparison.Ordinal);
    }

    // ---- openings and mouths -------------------------------------------------------------------

    [Fact]
    public void Openings_Join_Spaces_In_Every_Shape()
    {
        KitRun run = Run(
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 8, 16}}\n" +
            "local b = kit.room{name = 'b', pos = {17, 0, 0}, size = {16, 8, 16}}\n" +
            "local w = kit.opening{a = a, b = b, shape = 'window', offset = -5}\n" +
            "local h = kit.opening{a = a, b = b, shape = 'arch', offset = 1}\n" +
            "local r = kit.opening{a = a, b = b, shape = 'rough', offset = 5.5, width = 3, seed = 3}\n" +
            "return tostring(w.sight_only) .. ':' .. w.sill .. ':' .. tostring(h.sight_only) .. ':' .. h.height .. ':' .. #w.warnings + #h.warnings + #r.warnings");
        Assert.Equal("true:1.2:false:5:0", run.Result.ReturnValue);
        (CompiledLevel shared, _) = AssertAccepted(run);
        Assert.Single(shared.Geometry.Rooms, rm => rm.IsSkyroom == 0);
    }

    /// <summary>kit.opening{portal = true}: the arch itself is the air portal brush that cuts the wall (RED's portal
    /// construct), so the two walls it opens are two-sided portals and the arch a room between them, under both build
    /// methods (the arch's cross-section: a 4 x 3 m rectangle under an 8-segment half disc of radius 2 m).</summary>
    [Fact]
    public void Portal_Opening_Cuts_The_Wall_And_Builds_Two_Sided_Portals()
    {
        KitRun run = Run(
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 8, 16}}\n" +
            "local b = kit.room{name = 'b', pos = {17, 0, 0}, size = {16, 8, 16}}\n" +
            "local h = kit.opening{name = 'h', a = a, b = b, shape = 'arch', portal = true}\n" +
            "return tostring(h.portal_brush.portal) .. ':' .. tostring(h.portal_brush.uid == h.brush.uid)");
        Assert.Equal("true:true", run.Result.ReturnValue);
        float area = 12 + 8 * 0.5f * 4 * MathF.Sin(MathF.PI / 8);
        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        foreach (CompiledLevel c in new[] { shared, incremental })
        {
            KitAcceptanceTests.AssertTwoSidedDoorwayPortals(c.Geometry, 8, 9, area);
        }
    }

    [Fact]
    public void Wide_Mouths_Warn_That_Two_Spaces_Merge()
    {
        string v = Value(
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {6, 5, 16}}\n" +
            "local b = kit.room{name = 'b', pos = {13, 0, 0}, size = {16, 8, 16}}\n" +
            "local o = kit.opening{a = a, b = b, shape = 'arch', width = 5.5, height = 4.5}\n" +
            "return #o.warnings .. '|' .. table.concat(o.warnings, '|')");
        string[] p = v.Split('|');
        Assert.Equal("2", p[0]);
        Assert.Contains("more than 0.8 x the 6 m clear width of a", p[1], StringComparison.Ordinal);
        Assert.Contains("more than 0.8 x its 5 m headroom", p[2], StringComparison.Ordinal);

        // The same rule on a cave entering a narrow room (mouth 2 x radius x (1 + roughness) = 9.6 m into 8 m).
        string cave = Value(
            "kit.room{name = 'narrow', pos = {0, 0, 0}, size = {8, 7, 14}}\n" +
            "local c = kit.cave{path = {{0, 0, 6}, {0, 0, 30}}, radius = 4, height = 5}\n" +
            "return #c.warnings .. '|' .. (c.warnings[1] or '')");
        Assert.StartsWith("1|", cave, StringComparison.Ordinal);
        Assert.Contains("mouth is 9.6 m wide, more than 0.8 x the 8 m clear width of narrow", cave, StringComparison.Ordinal);
        Assert.Equal("0", Value("kit.room{pos = {0, 0, 0}, size = {16, 7, 14}} return #kit.cave{path = {{0, 0, 6}, {0, 0, 30}}, radius = 4, height = 5}.warnings"));
    }

    [Fact]
    public void Organic_Report_Flags_Sloped_Tubes_Without_Built_Elements()
    {
        string v = Value(
            "local c = kit.cave{name = 'c', path = {{0, 0, 0}, {0, 0, 10}, {0, 6, 30}, {0, 12, 50}}, radius = 3}\n" +
            "local r = kit.organic_report{along = c}\n" +
            "return string.format('%.2f:%.2f:%s:%d:%d', r.flat_share, r.built_per_100m2, tostring(r.ok), #r.warnings, #c.warnings)");
        string[] p = v.Split(':');
        Assert.True(D(p[0]) < 0.3, v);
        Assert.Equal("0.00", p[1]);
        Assert.Equal("false", p[2]);
        Assert.Equal("2", p[3]);
        Assert.Equal("1", p[4]); // kit.cave itself warns when the tube is mostly sloped
    }

    // ---- the road ------------------------------------------------------------------------------

    [Fact]
    public void Road_Grades_And_Turns_Build_Sealed()
    {
        KitRun run = Run(
            "local r = kit.road{name = 'r', path = {{0, 0, -40}, {0, 0, -20}, {0, 3, 4}, {14, 3, 18}, {14, 3, 40}}, tunnels = {4}}\n" +
            "return string.format('%d:%.4f:%.4f:%d:%d', #r.brushes, r.grades[1], r.grades[2], #r.warnings, #r.stations)");
        Assert.Equal("7:0.0000:0.1875:1:5", run.Result.ReturnValue); // 4 legs + 3 pads; 3 m over the 16 m between pads warns (> 15%)
        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        SpaceMetrics m = SpaceMetricsProbe.Measure(shared.Geometry, -1, 4);
        Assert.True(m.SkyShare > 0.6 && m.OffAxisShare > 0.05, m.ToString());
        Assert.NotEmpty(incremental.Geometry.Faces);
        Assert.Contains("turns more than 90°", Fail("kit.road{path = {{0, 0, 0}, {0, 0, 20}, {10, 0, 10}}}").Message, StringComparison.Ordinal);
        Assert.Contains("climbs only 1.25%", Fail("kit.road{path = {{0, 0, 0}, {0, 0.25, 20}}}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Road_Defaults_To_The_Corridor_Height_Rule_And_Names_The_Slope_It_Grades()
    {
        // The design grader's G04 caps the median corridor height at 8.5 m: kit.road's default cut is 8 m. A grade is the
        // rise over the sloped stretch, which excludes the level turn pads, and the warning says so with the numbers.
        string v = Value(
            "local r = kit.road{name = 'r', path = {{0, 0, -40}, {0, 0, -20}, {0, 3, 4}, {14, 3, 18}}}\n" +
            "return r.height .. '|' .. (r.max.y - r.min.y) .. '|' .. (r.warnings[1] or '')");
        string[] p = v.Split('|');
        Assert.Equal("8", p[0]);
        Assert.Equal("11", p[1]); // 3 m of climb + the 8 m cut
        Assert.Contains("leg 2 climbs 3 m over 16 m of slope (its 24 m less 8 m of level turn pads): a 18.75% grade, steeper than 15%", p[2], StringComparison.Ordinal);
        Assert.Contains("Lengthen the leg to at least 28 m", p[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Canyon_Joined_Under_A_Room_Ceiling_Does_Not_Warn_About_Its_Rim()
    {
        // kit.canyon refuses a rim that reaches the joined room's ceiling, so the rim cannot also be judged as a mouth
        // "taller than 0.8 x the headroom": a correctly joined canyon (rim 10.5 m in a 12 m room) gives no warning.
        string v = Value(
            "kit.room{name = 'yard', pos = {0, 0, 0}, size = {30, 12, 30}}\n" +
            "local c = kit.canyon{name = 'cut', path = {{0, 0, 10}, {0, 0, 60}}, width = 8, height = 10.5, seed = 1}\n" +
            "return #c.warnings .. '|' .. table.concat(c.warnings, '|')");
        Assert.Equal("0|", v);
    }

    // ---- the ground provider -------------------------------------------------------------------

    [Fact]
    public void Ground_Reads_Brush_Floors_Storeys_Details_And_Slopes()
    {
        string v = Value(
            "kit.room{name = 'low', pos = {0, 2, 0}, size = {20, 5, 20}}\n" +
            "kit.room{name = 'up', pos = {0, 8.5, 0}, size = {20, 4, 20}}\n" +
            "kit.platform{pos = {6, 2, 6}, size = {3, 3}, height = 1}\n" +
            "kit.platform{pos = {-6, 2, 6}, size = {3, 3}, height = 2, thickness = 0.25, detail = true}\n" +
            "kit.ramp{pos = {-6, 2, -8}, dir = '+z', rise = 2, run = 4, width = 3}\n" +
            "local function g(x, z, y) local r = kit.ground_at{x = x, z = z, y = y} return (r.y and string.format('%.3f', r.y) or 'nil') .. '/' .. r.kind end\n" +
            "local s = kit.ground_at{x = -6, z = -6}\n" +
            "local reg = kit.ground_region{min = {-8, 0, -8}, max = {-2, 0, -2}, step = 2, y = 7}\n" +
            "return table.concat({ g(0, 0), g(0, 0, 7), g(6, 6, 7), g(-6, 6, 7), g(40, 40), string.format('%.1f', kit.ground_at{x = -6, z = -6, y = 7}.slope),\n" +
            "  reg.count .. '/' .. reg.found .. '/' .. string.format('%.2f', reg.min_y) .. '/' .. reg.provider, kit.ground_at{x = 0, z = 0}.provider }, ' ')");
        Assert.Equal("8.500/brush 2.000/brush 3.000/brush 4.000/detail nil/void 26.6 16/16/2.00/brush brush", v);
    }

    [Fact]
    public void Ground_Provider_Is_A_Swappable_Seam()
    {
        string v = Value(
            "kit.room{name = 'r', pos = {0, 0, 0}, size = {40, 12, 40}}\n" +
            "local prev = kit.ground_provider{name = 'terrain', at = function(x, z) return {y = 0.05 * x, normal = {-0.05, 1, 0}, kind = 'terrain'} end}\n" +
            "local a = kit.ground_at{x = 10, z = 0}\n" +
            "local b = kit.bridge{from = {-14, 0}, to = {14, 0}, landmark = false, style = 'plank', purpose = 'off'}\n" +
            "local back = kit.ground_provider{}\n" +
            "local c = kit.ground_at{x = 10, z = 6}\n" + // off the new deck: the brush provider sees the bridge itself
            "return prev .. ':' .. a.provider .. ':' .. a.kind .. ':' .. a.y .. ':' .. string.format('%.2f:%.2f', b.from.y, b.to.y) .. ':' .. back .. ':' .. c.provider .. ':' .. c.y");
        Assert.Equal("brush:terrain:terrain:0.5:-0.70:0.70:terrain:brush:0", v);
    }
}
