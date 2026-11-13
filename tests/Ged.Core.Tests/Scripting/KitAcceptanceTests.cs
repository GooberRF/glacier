using System;
using System.Collections.Generic;
using System.IO;
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
/// The design kit's acceptance table. Every construction is measured on SEALED builds
/// only, under BOTH build methods (RED-authentic Shared BSP and Incremental): zero holes, no new lint
/// findings, exactly one undo node, and an exact undo back to the pre-run bytes.
/// </summary>
public sealed class KitAcceptanceTests
{
    private static List<Face> FacesFacing(Geometry g, float ny) =>
        g.Faces.Where(f => f.Texture >= 0 && (ny > 0 ? f.Plane.Normal.Y > ny : f.Plane.Normal.Y < ny)).ToList();

    private static Brush BrushOf(EditorDocument doc, int uid) => new BrushEditor(doc).FindBrush(uid)!;

    private static List<Brush> KitBrushes(EditorDocument doc, string name)
    {
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(doc);
        return new BrushEditor(doc).Brushes
            .Where(b => tags.TryGetValue(b.Uid, out var t) && t.TryGetValue("kit.name", out string? n) && n == name)
            .ToList();
    }

    private static (Vec3 Min, Vec3 Max) WorldBounds(Brush b)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            Vec3 w = BrushTransform.WorldVertex(b, b.Geometry.Vertices[i]);
            min = Vec3Math.Min(min, w);
            max = Vec3Math.Max(max, w);
        }

        return (min, max);
    }

    private static void AssertBox(Brush b, Vec3 min, Vec3 max)
    {
        (Vec3 lo, Vec3 hi) = WorldBounds(b);
        Assert.True(Near(lo, min) && Near(hi, max), $"brush {b.Uid} bounds {lo}..{hi}, expected {min}..{max}");
    }

    private static bool Near(Vec3 a, Vec3 b) =>
        MathF.Abs(a.X - b.X) < 1e-3f && MathF.Abs(a.Y - b.Y) < 1e-3f && MathF.Abs(a.Z - b.Z) < 1e-3f;

    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    // ---- room ----------------------------------------------------------------------------------

    [Fact]
    public void Room()
    {
        KitRun run = Run("local r = kit.room{pos = {0, 0, 0}, size = {16, 6, 16}, textures = {floor = 'kitfloor.tga', wall = 'kitwall.tga', ceiling = 'kitceil.tga'}}\n" +
                         "return r.name .. ':' .. r.floor_y .. ':' .. r.max.y .. ':' .. r.center.y");
        Assert.Equal("room1:0:6:3", run.Result.ReturnValue);
        Brush b = Assert.Single(new BrushEditor(run.Doc).Brushes);
        Assert.True((b.Flags & (uint)BrushFlags.Air) != 0);
        AssertBox(b, V(-8, 0, -8), V(8, 6, 8));

        foreach (bool shared in new[] { true, false })
        {
            CompiledLevel c = AssertWatertight(run.Doc, shared);
            Assert.All(FacesFacing(c.Geometry, 0.9f), f => Assert.Equal("kitfloor.tga", c.Geometry.Textures[f.Texture]));
            Assert.All(FacesFacing(c.Geometry, -0.9f), f => Assert.Equal("kitceil.tga", c.Geometry.Textures[f.Texture]));
            Assert.NotEmpty(FacesFacing(c.Geometry, 0.9f));
            Assert.NotEmpty(FacesFacing(c.Geometry, -0.9f));
        }

        AssertLintClean(run);
        AssertUndoRestores(run);
    }

    // ---- corridor ------------------------------------------------------------------------------

    [Fact]
    public void Corridor_AB()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {12, 6, 12}}\n" +
                         "local b = kit.room{name = 'b', pos = {18, 0, 0}, size = {12, 6, 12}}\n" + // gap x[6,12] = 6 m
                         "local c = kit.corridor{name = 'c', a = a, b = b}\n" +
                         "return c.axis .. ':' .. #c.brushes .. ':' .. c.width .. 'x' .. c.height");
        Assert.Equal("x:1:4.5x4.5", run.Result.ReturnValue); // kit.defaults corridor_width 4.5, corridor_height 4.5
        Assert.Equal(3, new BrushEditor(run.Doc).Brushes.Count);
        AssertBox(Assert.Single(KitBrushes(run.Doc, "c")), V(6, 0, -2.25f), V(12, 4.5f, 2.25f));
        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        Assert.Single(shared.Geometry.Rooms);
        Assert.Single(incremental.Geometry.Rooms);
    }

    [Fact]
    public void Corridor_Path()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +            // x,z [-8, 8]
                         "local b = kit.room{name = 'b', pos = {20, 0, 20}, size = {8, 6, 8}}\n" +            // x,z [16, 24]
                         "local c = kit.corridor{name = 'c', path = {{8, 0, 0}, {20, 0, 0}, {20, 0, 16}}}\n" +
                         "return #c.brushes .. ':' .. tostring(c.axis)");
        Assert.Equal("2:nil", run.Result.ReturnValue);
        Assert.Equal(4, new BrushEditor(run.Doc).Brushes.Count);
        List<Brush> segs = KitBrushes(run.Doc, "c");
        AssertBox(segs[0], V(8, 0, -2.25f), V(22.25f, 4.5f, 2.25f));   // extended by width/2 at the interior joint only
        AssertBox(segs[1], V(17.75f, 0, -2.25f), V(22.25f, 4.5f, 16));
        (CompiledLevel shared, _) = AssertAccepted(run);
        Assert.Single(shared.Geometry.Rooms);
    }

    [Fact]
    public void Corridor_Errors()
    {
        ScriptDiagnostic touch = Fail("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}}\n" +
                                      "local b = kit.room{name = 'b', pos = {8, 0, 0}, size = {8, 4, 8}}\n" +
                                      "kit.corridor{a = a, b = b}");
        Assert.Contains("a and b already touch — touching air brushes are already open to each other.", touch.Message, StringComparison.Ordinal);
        Assert.Equal(ScriptErrorKind.Api, touch.Kind);
        Assert.Equal("Move them at least kit.defaults.wall apart, or skip the connection.", touch.Hint);
        Assert.Equal("test", touch.Chunk);
        Assert.Equal(4, touch.Line); // the kit.corridor call (line 1 is the import)

        ScriptDiagnostic diagonal = Fail("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}}\n" +
                                         "local b = kit.room{name = 'b', pos = {20, 0, 20}, size = {8, 4, 8}}\n" +
                                         "kit.corridor{a = a, b = b}");
        Assert.Contains("not separated along a single axis", diagonal.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("x[-4, 4] z[-4, 4]", diagonal.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("path", diagonal.ToDisplayString(), StringComparison.Ordinal);

        ScriptDiagnostic narrow = Fail("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}}\n" +
                                       "local b = kit.room{name = 'b', pos = {12, 0, 6}, size = {8, 4, 8}}\n" + // z overlap [2, 4] = 2 m < 4
                                       "kit.corridor{a = a, b = b}");
        Assert.Contains("overlap only 2 m along z", narrow.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("kit.corridor{path", narrow.ToDisplayString(), StringComparison.Ordinal);

        ScriptDiagnostic floors = Fail("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}}\n" +
                                       "local b = kit.room{name = 'b', pos = {12, 1, 0}, size = {8, 4, 8}}\n" +
                                       "kit.corridor{a = a, b = b}");
        Assert.Contains("have different floors", floors.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("Use kit.stairs or kit.ramp, or pass floor_y.", floors.ToDisplayString(), StringComparison.Ordinal);

        ScriptDiagnostic tight = Fail("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}}\n" +
                                      "local b = kit.room{name = 'b', pos = {12, 0, 0}, size = {8, 4, 8}}\n" +
                                      "kit.corridor{a = a, b = b, width = 1.5}");
        Assert.Contains("below the 1.8 m player collision diameter", tight.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("allow_tight", tight.ToDisplayString(), StringComparison.Ordinal);
    }

    // ---- doorway -------------------------------------------------------------------------------

    [Fact]
    public void Doorway()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +   // x [-8, 8]
                         "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {12, 6, 12}}\n" +  // x [9, 21]
                         "local d = kit.doorway{name = 'd', a = a, b = b}\n" +
                         "return d.axis .. ':' .. d.length .. ':' .. d.width .. 'x' .. d.height .. ':' .. tostring(d.portal_brush)");
        Assert.Equal("x:1:3x4:nil", run.Result.ReturnValue); // kit.defaults door_width 3, door_height 4
        Assert.Equal(3, new BrushEditor(run.Doc).Brushes.Count);
        AssertBox(Assert.Single(KitBrushes(run.Doc, "d")), V(8, 0, -1.5f), V(9, 4, 1.5f));
        (CompiledLevel shared, _) = AssertAccepted(run);
        Assert.Single(shared.Geometry.Rooms);
    }

    /// <summary>
    /// portal = true makes the opening itself the air portal brush, the construct RED builds a portal from: it cuts
    /// the wall, the two wall faces it opens become two-sided portals and the doorway a room between them
    /// (compiler-parity-notes.md "Air portal brushes and inside-out rooms", rule 1) — under both build methods.
    /// </summary>
    [Fact]
    public void Doorway_Portal()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {12, 6, 12}}\n" +
                         "local d = kit.doorway{name = 'd', a = a, b = b, portal = true}\n" +
                         "return tostring(d.portal_brush.portal) .. ':' .. tostring(d.portal_brush.air) .. ':' .. tostring(d.portal_brush.uid == d.brush.uid)");
        Assert.Equal("true:true:true", run.Result.ReturnValue);
        Assert.Equal(3, new BrushEditor(run.Doc).Brushes.Count);
        AssertBox(Assert.Single(KitBrushes(run.Doc, "d")), V(8, 0, -1.5f), V(9, 4, 1.5f)); // fills the wall gap exactly
        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        foreach (CompiledLevel c in new[] { shared, incremental })
        {
            AssertTwoSidedDoorwayPortals(c.Geometry, 8, 9, 3 * 4);
        }

        Assert.True(shared.Report.SharedBspUsed);
        Assert.False(incremental.Report.SharedBspUsed);
    }

    /// <summary>Three main rooms (the two sides and the doorway between them) joined by two portals, each a pair of
    /// opposed portal faces covering the opening's cross-section on one wall plane (x = <paramref name="x0"/> and
    /// x = <paramref name="x1"/>), and no holes.</summary>
    internal static void AssertTwoSidedDoorwayPortals(Geometry g, float x0, float x1, float area)
    {
        Assert.Equal(3, g.Rooms.Count(r => r.IsSubroom == 0 && r.IsSkyroom == 0));
        Assert.Equal(2, g.Portals.Count);
        List<Face> portalFaces = g.Faces.Where(f => f.PortalIndexPlus2 >= 2).ToList();
        foreach (float x in new[] { x0, x1 })
        {
            List<Face> onPlane = portalFaces.Where(f => f.Vertices.All(v => MathF.Abs(g.Vertices[v.Index].X - x) < 1e-3f)).ToList();
            Assert.True(onPlane.Count >= 2, $"x = {x}: {onPlane.Count} portal face(s)");
            float front = 0, back = 0;
            foreach (Face f in onPlane)
            {
                Vec3 a = g.Vertices[f.Vertices[0].Index], n = default;
                for (int i = 1; i + 1 < f.Vertices.Count; i++)
                {
                    n = n.Add(g.Vertices[f.Vertices[i].Index].Sub(a).Cross(g.Vertices[f.Vertices[i + 1].Index].Sub(a)));
                }

                if (n.X > 0)
                {
                    front += n.Length() / 2;
                }
                else
                {
                    back += n.Length() / 2;
                }
            }

            Assert.Equal(area, front, 1); // two-sided: the same opening, facing both ways
            Assert.Equal(area, back, 1);
        }

        Assert.Empty(HoleDetector.Detect(g));
    }

    // ---- stairs --------------------------------------------------------------------------------

    [Fact]
    public void Stairs()
    {
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local s = kit.stairs{name = 's', pos = {0, 0, -4}, dir = '+z', rise = 1.5, width = 2}\n" +
                         "return s.steps .. ':' .. s.run .. ':' .. s.top.y .. ':' .. s.top.z .. ':' .. #s.group.brushes");
        Assert.Equal("6:3:1.5:-1:7", run.Result.ReturnValue);
        List<Brush> steps = KitBrushes(run.Doc, "s");
        Assert.Equal(7, steps.Count); // six steps, then the collision ramp
        Assert.Equal(8, new BrushEditor(run.Doc).Brushes.Count);
        for (int i = 0; i < 6; i++)
        {
            AssertBox(steps[i], V(-1, 0, -4 + (i * 0.5f)), V(1, (i + 1) * 0.25f, -4 + ((i + 1) * 0.5f)));
        }

        Group g = Assert.Single(new GroupService(run.Doc).Groups);
        Assert.Equal("s", g.Name);
        Assert.Equal(7, g.Brushes.Count);
        AssertAccepted(run);
    }

    [Fact]
    public void Stairs_Get_An_Invisible_Collision_Ramp_Over_The_Step_Edges()
    {
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local s = kit.stairs{name = 's', pos = {0, 0, -4}, dir = '+z', rise = 1.5, width = 2}\n" +
                         "return s.ramp:tags()['kit.part'] .. ':' .. tostring(s.ramp.detail) .. ':' .. s.ramp.time_index");
        Assert.Equal("ramp:true:7", run.Result.ReturnValue); // after the room and the six steps
        Brush ramp = KitBrushes(run.Doc, "s").Last();
        AssertBox(ramp, V(-1, 0, -4.5f), V(1, 1.5f, -1.5f)); // one tread in front of the first riser to the top step's front edge
        Assert.All(ramp.Geometry.Faces, f => Assert.Equal("sld_invisible01.tga", ramp.Geometry.Textures[f.Texture]));

        // The slope passes through every step nosing: (z, y) = (-4 + (i - 1) * 0.5, i * 0.25).
        Face slope = ramp.Geometry.Faces.Single(f => MathF.Abs(ramp.Rotation.Transform(f.Plane.Normal).Y) is > 0.1f and < 0.99f);
        Vec3 n = ramp.Rotation.Transform(slope.Plane.Normal);
        Vec3 onSlope = BrushTransform.WorldVertex(ramp, ramp.Geometry.Vertices[slope.Vertices[0].Index]);
        for (int i = 1; i <= 6; i++)
        {
            Vec3 nosing = V(0, i * 0.25f, -4 + ((i - 1) * 0.5f));
            Assert.True(MathF.Abs(n.Dot(nosing.Sub(onSlope))) < 1e-3f, $"nosing {i} is off the ramp slope");
        }

        foreach (bool shared in new[] { true, false })
        {
            CompiledLevel c = AssertWatertight(run.Doc, shared);
            List<Face> invisible = c.Geometry.Faces.Where(f => f.Texture >= 0 && c.Geometry.Textures[f.Texture] == "sld_invisible01.tga").ToList();
            Assert.NotEmpty(invisible);
            Assert.All(invisible, f => Assert.True((f.Flags & (ushort)FaceFlags.IsInvisible) != 0, "ramp face not compiled invisible"));
        }

        AssertLintClean(run);
        KitRun without = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                             "local s = kit.stairs{name = 's', pos = {0, 0, -4}, dir = '+z', rise = 1.5, width = 2, collision_ramp = false}\n" +
                             "return tostring(s.ramp) .. ':' .. #s.brushes");
        Assert.Equal("nil:6", without.Result.ReturnValue);
    }

    [Fact]
    public void Stairs_Carve()
    {
        // Lower room floor 0, upper room floor 3; the stairs climb through 6 m of solid between them.
        KitRun run = Run("kit.room{name = 'low', pos = {0, 0, 0}, size = {8, 6, 8}}\n" +        // z [-4, 4]
                         "kit.room{name = 'high', pos = {0, 3, 14}, size = {8, 4, 8}}\n" +      // z [10, 18], y [3, 7]
                         "local s = kit.stairs{name = 's', pos = {0, 0, 4}, dir = '+z', rise = 3, width = 2, carve = true}\n" +
                         "return s.steps .. ':' .. s.envelope.time_index .. ':' .. s.brushes[2].time_index .. ':' .. s.top.z");
        Assert.Equal("12:2:3:10", run.Result.ReturnValue);
        List<Brush> brushes = KitBrushes(run.Doc, "s");
        Assert.Equal(14, brushes.Count); // envelope, 12 steps, the collision ramp
        AssertBox(brushes[0], V(-1, 0, 4), V(1, 7.5f, 10)); // envelope: rise 3 + headroom 4.5 (kit.defaults.corridor_height)
        var ed = new BrushEditor(run.Doc);
        int[] order = brushes.Select(b => ed.TimeIndex(b.Uid)).ToArray();
        Assert.Equal(order.OrderBy(i => i).ToArray(), order); // envelope first, then steps in order
        AssertAccepted(run);
    }

    // ---- ramp ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("+x", 1, 0)]
    [InlineData("-x", -1, 0)]
    [InlineData("+z", 0, 1)]
    [InlineData("-z", 0, -1)]
    public void Ramp(string dir, int dx, int dz)
    {
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         $"local p = kit.ramp{{name = 'p', pos = {{0, 0, 0}}, dir = '{dir}', rise = 1.5, width = 3}}\n" +
                         "return p.run .. ':' .. p.top.y");
        Assert.Equal("3:1.5", run.Result.ReturnValue);
        Assert.Equal(2, new BrushEditor(run.Doc).Brushes.Count);

        // The highest vertices are on the dir side (the far end of the run).
        Brush wedge = Assert.Single(KitBrushes(run.Doc, "p"));
        var top = Enumerable.Range(0, wedge.Geometry.Vertices.Count).Select(i => BrushTransform.WorldVertex(wedge, wedge.Geometry.Vertices[i]))
            .Where(v => v.Y > 1.49f).ToList();
        Assert.Equal(2, top.Count);
        Assert.All(top, v => Assert.True(MathF.Abs((v.X * dx) + (v.Z * dz) - 3f) < 1e-3f, $"high vertex {v} not at the {dir} end"));
        var low = Enumerable.Range(0, wedge.Geometry.Vertices.Count).Select(i => BrushTransform.WorldVertex(wedge, wedge.Geometry.Vertices[i]))
            .Where(v => v.Y < 0.01f).ToList();
        Assert.All(low, v => Assert.InRange((v.X * dx) + (v.Z * dz), -1e-3f, 3.001f));
        AssertAccepted(run);
    }

    [Fact]
    public void Ramp_Steeper_Than_45_Degrees_Errors()
    {
        ScriptDiagnostic steep = Fail("kit.ramp{pos = {0, 0, 0}, dir = '+x', rise = 2, run = 1}");
        Assert.Contains("steeper than 45 degrees", steep.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("allow_steep", steep.ToDisplayString(), StringComparison.Ordinal);
    }

    // ---- platform ------------------------------------------------------------------------------

    [Fact]
    public void Platform_Block()
    {
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local p = kit.platform{name = 'p', pos = {2, 0, 2}, size = {4, 6}, height = 1.5}\n" +
                         "return #p.brushes .. ':' .. p.top_y .. ':' .. tostring(p.group)");
        Assert.Equal("1:1.5:nil", run.Result.ReturnValue);
        Assert.Equal(2, new BrushEditor(run.Doc).Brushes.Count);
        AssertBox(Assert.Single(KitBrushes(run.Doc, "p")), V(0, 0, -1), V(4, 1.5f, 5));
        Assert.Empty(new GroupService(run.Doc).Groups);
        AssertAccepted(run);
    }

    [Fact]
    public void Platform_Slab_With_Supports()
    {
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local p = kit.platform{name = 'p', pos = {0, 0, 0}, size = {6, 4}, height = 2.5, thickness = 0.5, supports = true}\n" +
                         "return #p.brushes .. ':' .. p.top_y .. ':' .. #p.group.brushes");
        Assert.Equal("5:2.5:5", run.Result.ReturnValue);
        Assert.Equal(6, new BrushEditor(run.Doc).Brushes.Count);
        List<Brush> parts = KitBrushes(run.Doc, "p");
        AssertBox(parts[0], V(-3, 2, -2), V(3, 2.5f, 2));       // slab first
        AssertBox(parts[1], V(-3, 0, -2), V(-2, 2, -1));        // legs flush with the slab corners
        AssertBox(parts[4], V(2, 0, 1), V(3, 2, 2));
        AssertAccepted(run);
    }

    // ---- pillars -------------------------------------------------------------------------------

    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    public void PillarRow(string shape)
    {
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 5, 16}}\n" +
                         $"local p = kit.pillar_row{{name = 'p', from = {{-4, 0, 0}}, to = {{4, 0, 0}}, count = 3, height = 5, shape = '{shape}'}}\n" +
                         "return #p.brushes .. ':' .. #p.group.brushes .. ':' .. p.brushes[2].pos.x");
        Assert.Equal("3:3:0", run.Result.ReturnValue);
        Assert.Equal(4, new BrushEditor(run.Doc).Brushes.Count);
        Assert.All(KitBrushes(run.Doc, "p"), b => Assert.True((b.Flags & (uint)BrushFlags.Detail) == 0, "pillars default to structural"));
        AssertAccepted(run);
    }

    // ---- door ----------------------------------------------------------------------------------

    [Fact]
    public void Door()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {12, 6, 12}}\n" +
                         "local way = kit.doorway{name = 'way', a = a, b = b}\n" +
                         "local d = kit.door{name = 'gate', opening = way, buttons = {{7.5, 1.5, 2.5}, {9.5, 1.5, 2.5}}}\n" +
                         "local m = d.mover\n" +
                         "local s = m.start_keyframe\n" +
                         "local out = {#m.keyframes, m:get('movement_type'), tostring(m:get('is_door')), s:get('travel_time_to_next'),\n" +
                         "  d.keyframes[2]:get('pause_time'), d.keyframes[2]:get('return_travel'), #d.triggers}\n" +
                         "for _, t in ipairs(d.triggers) do\n" +
                         "  local l = t:links()\n" +
                         "  out[#out + 1] = t.name .. '=' .. #l .. ':' .. tostring(l[1] == s.uid) .. ':' .. tostring(t:get('use_key_required')) .. ':' .. t:get('resets_after')\n" +
                         "end\n" +
                         "out[#out + 1] = tostring(d.brush.mover ~= nil)\n" +
                         "out[#out + 1] = d.keyframes[2].pos.y - s.pos.y\n" +
                         "return table.concat(out, ',')");
        Assert.Equal("2,Ping Pong Once,true,1,3,1,2,gate_button1=1:true:true:5,gate_button2=1:true:true:5,true,4", run.Result.ReturnValue); // the slab rises by its 4 m height
        Assert.DoesNotContain(run.Log.Entries, e => e.Message.Contains("refreshed the mover copy", StringComparison.Ordinal)); // the kit makes the mover last

        Brush door = Assert.Single(KitBrushes(run.Doc, "gate"));
        AssertBox(door, V(8.375f, 0, -1.5f), V(8.625f, 4, 1.5f));
        Assert.NotNull(new MoverService(run.Doc).FindGroupForMember(door.Uid));

        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        foreach (CompiledLevel c in new[] { shared, incremental })
        {
            // The mover-owned door never reaches the static world: no compiled face lies on its two big planes.
            Assert.DoesNotContain(c.Geometry.Faces, f => MathF.Abs(f.Plane.Normal.X) > 0.99f &&
                (MathF.Abs(MathF.Abs(f.Plane.Offset) - 8.375f) < 1e-3f || MathF.Abs(MathF.Abs(f.Plane.Offset) - 8.625f) < 1e-3f));
            Assert.Single(c.Geometry.Rooms);
        }
    }

    [Fact]
    public void Door_Auto_Trigger_And_Slides()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local b = kit.room{name = 'b', pos = {0, 0, 20}, size = {16, 6, 16}}\n" +   // gap z [8, 12]
                         "local c = kit.corridor{name = 'c', a = a, b = b}\n" +
                         "local d = kit.door{name = 'slider', opening = c, slide = 'right'}\n" +
                         "local t = d.triggers[1]\n" +
                         "local off = d.keyframes[2].pos.x - d.keyframes[1].pos.x\n" +
                         "return #d.triggers .. ':' .. t.name .. ':' .. t:get('sphere_radius') .. ':' .. t.pos.y .. ':' .. off .. ':' .. tostring(t:get('use_key_required'))");
        // Walking +z through the corridor, "right" is +x (left-handed: +X right, +Z forward).
        Assert.Equal("1:slider_trigger:4.5:2.25:4.5:false", run.Result.ReturnValue); // the 4.5 x 4.5 m default corridor
        AssertAccepted(run);
    }

    // ---- lights + spawns -----------------------------------------------------------------------

    [Fact]
    public void LightRoom()
    {
        KitRun run = Run("local r = kit.room{name = 'hall', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local lights = kit.light_room{room = r, spacing = 8, fixture = false}\n" + // 8 m cells (the default spacing follows the norms); bare lights
                         "local out = {}\n" +
                         "for _, l in ipairs(lights) do out[#out + 1] = l.name .. '@' .. l.pos.x .. ',' .. l.pos.y .. ',' .. l.pos.z end\n" +
                         "out[#out + 1] = string.format('%.4f', lights[1]:get('size_range'))\n" +
                         "local c = lights[1]:get('color') out[#out + 1] = c.r .. '/' .. c.g .. '/' .. c.b\n" +
                         "return table.concat(out, ';')");
        double range = 0.9 * Math.Sqrt((8 * 8) + (8 * 8) + (6 * 6));
        Assert.Equal("hall_light1@-4,4.5,-4;hall_light2@4,4.5,-4;hall_light3@-4,4.5,4;hall_light4@4,4.5,4;" +
                     range.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ";255/240/200",
            run.Result.ReturnValue);
        AssertAccepted(run);
    }

    [Fact]
    public void SpawnPoints()
    {
        // v2: spread apart (>= 8 m), clear of the walls by the margin, 1 m above the floor, facing into the
        // room (the longest open heading when the room has no doorway), team flags set.
        KitRun run = Run("local r = kit.room{name = 'hall', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local spawns = kit.spawn_points{room = r, count = 4, team = 'red'}\n" +
                         "local out = {}\n" +
                         "for _, s in ipairs(spawns) do\n" +
                         "  local f = s.rot.forward\n" +
                         "  out[#out + 1] = string.format('%s@%g,%g,%g>%.3f,%.3f,%.3f:%s%s', s.name, s.pos.x, s.pos.y, s.pos.z, f.x, f.y, f.z,\n" +
                         "    tostring(s:get('red_team')), tostring(s:get('blue_team')))\n" +
                         "end\n" +
                         "return table.concat(out, ';') .. '|' .. spawns.report.nn_min");
        string[] halves = run.Result.ReturnValue!.Split('|');
        string[] spawns = halves[0].Split(';');
        Assert.Equal(4, spawns.Length);
        Assert.True(double.Parse(halves[1], System.Globalization.CultureInfo.InvariantCulture) >= 8 - 1e-6, run.Result.ReturnValue);
        for (int i = 0; i < spawns.Length; i++)
        {
            string s = spawns[i];
            Assert.StartsWith($"hall_spawn{i + 1}@", s, StringComparison.Ordinal);
            Assert.EndsWith(":truefalse", s, StringComparison.Ordinal);
            float[] p = s.Split('@')[1].Split('>')[0].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            float[] f = s.Split('>')[1].Split(':')[0].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(1f, p[1], 3);
            Assert.InRange(MathF.Abs(p[0]), 0f, 6.5f + 1e-3f);    // 1.5 m margin from the walls
            Assert.InRange(MathF.Abs(p[2]), 0f, 6.5f + 1e-3f);
            Assert.True((p[0] * f[0]) + (p[2] * f[2]) <= 1e-3f, $"{s} faces the wall behind it");
        }

        AssertAccepted(run);
    }

    [Fact]
    public void LightRoom_Never_Places_A_Light_Inside_A_Solid()
    {
        // A pillar right on the cell centre (the light's default spot) and a cell completely filled by a
        // solid block: the first light moves to the nearest free spot of its cell, the second is skipped.
        KitRun run = Run("local r = kit.room{name = 'hall', pos = {0, 0, 0}, size = {16, 6, 8}}\n" +            // cells x[-8,0], x[0,8]
                         "kit.pillar_row{name = 'p', from = {-4, 0, 0}, count = 1, height = 6}\n" + // spacing 8: two 8 m cells           // on cell 1's centre
                         "level.brush{pos = {4, 3, 0}, size = {8, 6, 8}}\n" +                                    // fills cell 2
                         "local lights = kit.light_room{room = r, spacing = 8, fixture = false}\n" +
                         "local l = lights[1]\n" +
                         "return #lights .. ':' .. l.pos.x .. ',' .. l.pos.y .. ',' .. l.pos.z");
        string[] parts = run.Result.ReturnValue!.Split(':');
        Assert.Equal("1", parts[0]);
        Assert.Single(run.Log.Entries, e => e.Level == ScriptLogLevel.Warning && e.Message.Contains("cell 2,1 is filled with solid", StringComparison.Ordinal));
        float[] p = parts[1].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // Clear of the 1 m pillar (x[-4.5, -3.5], z[-0.5, 0.5]) by the 0.25 m clearance, still in its cell.
        Assert.True(MathF.Abs(p[0] + 4) >= 0.75f - 1e-3f || MathF.Abs(p[2]) >= 0.75f - 1e-3f, $"light at {parts[1]} is within the pillar's clearance");
        Assert.InRange(p[0], -8f, 0f);
        Assert.InRange(p[2], -4f, 4f);
        Assert.Equal(4.5f, p[1], 3);
    }

    [Fact]
    public void Default_Names_Skip_Names_Already_In_The_Level()
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        Run("kit.room{pos = {0, 0, 0}, size = {8, 4, 8}}", doc);                       // room1
        KitRun second = Run("local r = kit.room{pos = {0, 0, 20}, size = {8, 4, 8}} return r.name", doc);
        Assert.Equal("room2", second.Result.ReturnValue);
        Assert.Equal(1, new BrushEditor(doc).Brushes.Count(b => Tags(doc).TryGetValue(b.Uid, out var t) && t["kit.name"] == "room1"));
    }

    [Fact]
    public void Sky_Room_Builds_A_Sealed_Sky_Box_With_A_Sky_Room_Effect_And_Opens_The_Ceilings()
    {
        // The stock construction (dm01, dm04): a separate small air box textured with a sky, a Sky Room
        // effect inside it, and show-sky faces where the play space sees the sky.
        KitRun run = Run("local yard = kit.room{name = 'yard', pos = {0, 0, 0}, size = {16, 8, 16}}\n" +
                         "local sky = kit.sky_room{open = {yard}}\n" +
                         "return sky.name .. ':' .. sky.sky_faces .. ':' .. sky.effect:get('type') .. ':' .. sky.center.x");
        Assert.Equal("sky_room1:1:Sky Room:75", run.Result.ReturnValue); // 8 + 64 + 3
        Brush box = Assert.Single(KitBrushes(run.Doc, "sky_room1"));
        Assert.True((box.Flags & (uint)BrushFlags.Air) != 0);
        Assert.All(box.Geometry.Faces, f => Assert.Equal("Sky_Blu01_01.tga", box.Geometry.Textures[f.Texture]));
        Brush yard = Assert.Single(KitBrushes(run.Doc, "yard"));
        Face top = Assert.Single(yard.Geometry.Faces, f => (f.Flags & (ushort)FaceFlags.ShowSky) != 0);
        Assert.True(top.Plane.Normal.Y < -0.99f); // the ceiling: an air brush's faces are stored inward, as RED stores them
        Assert.Equal("Sky_Blu01_01.tga", yard.Geometry.Textures[top.Texture]); // the opened ceiling carries the sky texture, as kit.road's and kit.space's do

        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        foreach (CompiledLevel c in new[] { shared, incremental })
        {
            Assert.Equal(2, c.Geometry.Rooms.Count); // the yard and the sealed sky box
            Assert.Contains(c.Geometry.Faces, f => (f.Flags & (ushort)FaceFlags.ShowSky) != 0);
        }
    }

    [Fact]
    public void Door_Keyframes_Carry_The_Travel_Time_Both_Ways()
    {
        // The closed keyframe holds travel = return (RED's stock doors, read by the stock game for the
        // closing leg); the open keyframe holds the pause and the same times (read by Alpine's movers), so
        // the door closes over `travel` seconds in either mover mode instead of snapping shut.
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {12, 6, 12}}\n" +
                         "kit.door{name = 'gate', opening = kit.doorway{a = a, b = b}, travel = 1.5, pause = 2}");
        Group mover = Assert.Single(new MoverService(run.Doc).Movers);
        Keyframe closed = mover.MovingData!.Keyframes[0];
        Keyframe open = mover.MovingData.Keyframes[1];
        Assert.Equal((0f, 1.5f, 1.5f), (closed.PauseTime, closed.DepartTravelTime, closed.ReturnTravelTime));
        Assert.Equal((2f, 1.5f, 1.5f), (open.PauseTime, open.DepartTravelTime, open.ReturnTravelTime));
    }

    [Fact]
    public void Door_Button_Class_Marks_Each_Button()
    {
        KitRun run = Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {12, 6, 12}}\n" +
                         "local way = kit.doorway{name = 'way', a = a, b = b}\n" +
                         "local d = kit.door{name = 'gate', opening = way, buttons = {{7.5, 1.5, 2.5}, {9.5, 1.5, 2.5}}, button_class = 'Switch'}\n" +
                         "return #d.panels .. ':' .. d.panels[1].name .. ':' .. d.panels[1].kind .. ':' .. d.panels[2].pos.x");
        Assert.Equal("2:gate_panel1:Clutter:9.5", run.Result.ReturnValue);
        Assert.Contains("'button_class' needs 'buttons'", Fail(
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 16}} local b = kit.room{name = 'b', pos = {15, 0, 0}, size = {12, 6, 12}}\n" +
            "kit.door{opening = kit.doorway{a = a, b = b}, button_class = 'Switch'}").Message, StringComparison.Ordinal);
    }

    // ---- naming, tagging, strict keys ----------------------------------------------------------

    [Fact]
    public void Tags_And_Names()
    {
        KitRun run = Run("local a = kit.room{pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
                         "local b = kit.room{pos = {15, 0, 0}, size = {12, 6, 12}}\n" +
                         "local w = kit.doorway{a = a, b = b, portal = true}\n" +
                         "kit.stairs{pos = {-5, 0, 0}, dir = '-z', rise = 1, width = 2}\n" +
                         "kit.ramp{pos = {0, 0, 4}, dir = '+x', rise = 1, width = 2}\n" +
                         "kit.platform{pos = {-5, 0, -5}, size = {4, 4}, height = 1, thickness = 0.25, supports = true}\n" +
                         "kit.pillar_row{from = {4, 0, -6}, to = {6, 0, -6}, count = 2, height = 6}\n" +
                         "kit.door{opening = w, buttons = {{7, 1.5, 3}}}\n" +
                         "kit.light_room{room = a} kit.spawn_points{room = b, count = 3}\n" +
                         "return a.name .. ',' .. b.name .. ',' .. w.name");
        Assert.Equal("room1,room2,doorway1", run.Result.ReturnValue);

        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        foreach (Brush b in new BrushEditor(run.Doc).Brushes)
        {
            Assert.True(tags.TryGetValue(b.Uid, out var t), $"brush {b.Uid} has no tags");
            Assert.True(t!.ContainsKey("kit") && t.ContainsKey("kit.name") && t.ContainsKey("kit.part"), $"brush {b.Uid}: {string.Join(",", t.Keys)}");
        }

        foreach (LevelObject o in run.Doc.Objects.Where(o => o.Kind != LevelObjectKind.PlayerStart && o.Kind != LevelObjectKind.Mover))
        {
            Assert.False(string.IsNullOrEmpty(o.ScriptName), $"{o.Kind} {o.Uid} has no script name");
            Assert.True(tags.TryGetValue(o.Uid, out var t) && t.ContainsKey("kit") && t.ContainsKey("kit.name"), $"{o.Kind} {o.Uid} is not kit-tagged");
        }

        Assert.Equal(new[] { "pillar_row1", "platform1", "stairs1" }, new GroupService(run.Doc).Groups.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        AssertAccepted(run);
    }

    public static IEnumerable<object[]> KitCalls() => new[]
    {
        new object[] { "kit.room{pos = {0, 0, 0}, size = {4, 4, 4}, sise = 1}", "unknown option 'sise'", "'size'" },
        new object[] { "kit.corridor{path = {{0, 0, 0}, {8, 0, 0}}, widht = 4}", "unknown option 'widht'", "'width'" },
        new object[] { "kit.doorway{a = {}, b = {}, portl = true}", "unknown option 'portl'", "'portal'" },
        new object[] { "kit.stairs{pos = {0, 0, 0}, dir = '+x', rise = 1, riserr = 0.25}", "unknown option 'riserr'", "'riser'" },
        new object[] { "kit.ramp{pos = {0, 0, 0}, dir = '+x', rise = 1, rnu = 2}", "unknown option 'rnu'", "'run'" },
        new object[] { "kit.door{opening = {}, slid = 'up'}", "unknown option 'slid'", "'slide'" },
        new object[] { "kit.platform{pos = {0, 0, 0}, size = {2, 2}, height = 1, suports = true}", "unknown option 'suports'", "'supports'" },
        new object[] { "kit.pillar_row{from = {0, 0, 0}, count = 1, height = 2, cout = 2}", "unknown option 'cout'", "'count'" },
        new object[] { "kit.light_room{room = {}, spacng = 4}", "unknown option 'spacng'", "'spacing'" },
        new object[] { "kit.spawn_points{room = {}, count = 1, tem = 'red'}", "unknown option 'tem'", "'team'" },
        new object[] { "kit.room{position = {0, 0, 0}, size = {4, 4, 4}}", "unknown option 'position'", "Valid options:" },
    };

    [Theory]
    [MemberData(nameof(KitCalls))]
    public void Kit_Functions_Reject_Unknown_Keys(string call, string message, string hint)
    {
        ScriptDiagnostic e = Fail(call);
        Assert.Contains(message, e.ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains(hint, e.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Kit_Validates_Dimensions_And_Steps()
    {
        Assert.Contains("is not a whole number of 0.25 m risers", Fail("kit.stairs{pos = {0, 0, 0}, dir = '+x', rise = 1.1}").ToDisplayString(), StringComparison.Ordinal);
        Assert.Contains("above the 1 m step limit", Fail("kit.stairs{pos = {0, 0, 0}, dir = '+x', rise = 3, riser = 1.5, tread = 2}").Message, StringComparison.Ordinal);
        Assert.Contains("'dir' must be one of", Fail("kit.stairs{pos = {0, 0, 0}, dir = 'up', rise = 1}").Message, StringComparison.Ordinal);
        Assert.Contains("'size' components must be greater than 0", Fail("kit.room{pos = {0, 0, 0}, size = {4, 0, 4}}").Message, StringComparison.Ordinal);
        Assert.Contains("standing headroom", Fail(
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}} local b = kit.room{name = 'b', pos = {9, 0, 0}, size = {8, 4, 8}}\n" +
            "kit.doorway{a = a, b = b, height = 1.5}").Message, StringComparison.Ordinal);

        // allow_tight overrides the walkable minimum.
        Run("local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {8, 4, 8}} local b = kit.room{name = 'b', pos = {9, 0, 0}, size = {8, 4, 8}}\n" +
            "kit.doorway{a = a, b = b, width = 1, height = 1.5, allow_tight = true}");
    }

    // ---- the kit's own documentation -----------------------------------------------------------

    [Fact]
    public void Every_Kit_Function_Has_A_Kit_Doc_Line()
    {
        // kit.lua plus the kit_* modules it installs functions from, as the kit reference reads them.
        string root = ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        string source = string.Join("\n", ScriptKitSource.ModuleNames()
            .Select(n => File.ReadAllText(Path.Combine(root, "scripts", "lib", n + ".lua"))));
        Assert.Equal(new[] { "kit", "kit_dress", "kit_items", "kit_layout", "kit_probe", "kit_space", "kit_struct", "kit_uv" }, ScriptKitSource.ModuleNames().Where(n => n is "kit" or "kit_dress" or "kit_items" or "kit_layout" or "kit_probe" or "kit_space" or "kit_struct" or "kit_uv").ToArray());
        IReadOnlyList<ScriptApiReference.KitFunctionDoc> docs = ScriptApiReference.ParseKitDocs(source);
        string[] functions = System.Text.RegularExpressions.Regex.Matches(source, @"^function kit\.([a-z_]+)\(", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(45, functions.Length); // kit 17 + kit_dress 2 + kit_items 2 + kit_layout 5 + kit_space 9 + kit_struct 8 + kit_uv 2
        Assert.Equal(functions, docs.Select(d => d.Name).ToArray());
        Assert.All(docs, d =>
        {
            Assert.StartsWith("kit." + d.Name, d.Signature, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            Assert.All(d.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Type) || string.IsNullOrWhiteSpace(o.Doc), $"kit.{d.Name} option {o.Name}"));
        });
    }

    [Fact]
    public void Every_Kit_Option_Doc_Matches_The_Keys_The_Function_Accepts()
    {
        // The ---@opt lines are the kit's option documentation; the keys each function accepts are read
        // back from its own unknown-key error ("Valid options: …"), so the two can never drift apart.
        string source = ScriptKitSource.Combined() ?? throw new InvalidOperationException("kit not shipped");
        foreach (ScriptApiReference.KitFunctionDoc doc in ScriptApiReference.ParseKitDocs(source))
        {
            ScriptDiagnostic e = Fail($"kit.{doc.Name}{{zz_probe = 1}}");
            Assert.Contains("unknown option 'zz_probe'", e.Message, StringComparison.Ordinal);
            string hint = e.Hint ?? string.Empty;
            int at = hint.IndexOf("Valid options:", StringComparison.Ordinal);
            Assert.True(at >= 0, $"kit.{doc.Name}: {hint}");
            string[] accepted = hint[(at + "Valid options:".Length)..].Trim().TrimEnd('.')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            string[] documented = doc.Options.Select(o => o.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.True(accepted.SequenceEqual(documented),
                $"kit.{doc.Name}: accepts [{string.Join(", ", accepted)}] but documents [{string.Join(", ", documented)}]");
        }
    }

    [Fact]
    public void Kit_Help_Lists_Every_Function_And_Option()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(FixedDate), "local kit = ged.import('kit') return kit.help()");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string text = r.ReturnValue!;
        Assert.Contains("kit.room{", text, StringComparison.Ordinal);
        Assert.Contains("size: vec — Required: interior clear size", text, StringComparison.Ordinal);
        Assert.Contains("button_class: string = none", text, StringComparison.Ordinal);
        Assert.Equal(text, ScriptApiReference.Describe("kit")!.Text);
    }

    // ---- texture scale -------------------------------------------------------------------------

    [Fact]
    public void Kit_Walls_Tile_Like_A_Newly_Drawn_Brush()
    {
        // A kit room wall and a level.brush of the same size: the same UV span on the same face, i.e. one
        // texture repeat per GeometryUtil.DefaultTileMetres, whatever the texture's pixel size.
        KitRun run = Run("kit.room{name = 'r', pos = {0, 0, 0}, size = {8, 4, 8}}\n" +
                         "level.brush{pos = {0, 2, 20}, size = {8, 4, 8}, air = true}");
        List<Brush> brushes = new BrushEditor(run.Doc).Brushes.ToList();
        Assert.Equal(2, brushes.Count);

        static (float U, float V) Span(Brush b)
        {
            // The face whose outward normal is +X (a wall), measured across its corners.
            Face wall = b.Geometry.Faces.First(f => f.Plane.Normal.X > 0.99f || f.Plane.Normal.X < -0.99f);
            float u0 = wall.Vertices.Min(v => v.TextureCoords.U), u1 = wall.Vertices.Max(v => v.TextureCoords.U);
            float v0 = wall.Vertices.Min(v => v.TextureCoords.V), v1 = wall.Vertices.Max(v => v.TextureCoords.V);
            return (u1 - u0, v1 - v0);
        }

        (float ku, float kv) = Span(brushes[0]);
        (float bu, float bv) = Span(brushes[1]);
        Assert.Equal(8f / GeometryUtil.DefaultTileMetres, ku, 3);
        Assert.Equal(4f / GeometryUtil.DefaultTileMetres, kv, 3);
        Assert.Equal(bu, ku, 3);
        Assert.Equal(bv, kv, 3);
        AssertAccepted(run);
    }
}
