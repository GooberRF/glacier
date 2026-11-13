using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Mesh;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Ged.Core.Tables;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// kit.light_room's fixtures stand on the floor, sit on the wall and hang from the ceiling actually under, behind and
/// over them — measured on the sealed compiled level, independently of the kit's own brush probe: a floor-standing
/// fixture's base (a detail lamp post, or the stock lamp post's mesh in the engine's model space: LOD 0 vertices plus
/// the submesh offset) lies on the floor below it, a wall lamp's back is on the wall behind it, a hanging fixture's top
/// touches the ceiling above it, and every outdoor light keeps its height over the floor right under it. The layout has
/// a raised deck beside the doorway of a yard open to the sky and a lowered ceiling over half of an indoor hall.
/// </summary>
public sealed class KitFixtureGroundingTests
{
    private const double Tolerance = 0.05;

    private readonly ITestOutputHelper _out;

    public KitFixtureGroundingTests(ITestOutputHelper output) => _out = output;

    private const string Layout = """
        local kit = ged.import('kit')
        local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {30, 12, 26} }
        local hall = kit.room{ name = "hall", pos = {34, 0, 0}, size = {16, 6, 16} }
        kit.doorway{ a = yard, b = hall, width = 4 }
        kit.sky_room{ open = { yard } }
        -- A raised deck along the east wall, beside the doorway to the hall.
        kit.platform{ name = "deck", pos = {12, 0, -8}, size = {6, 10}, height = 1.5 }
        -- A lowered ceiling over the hall's east half (a solid slab from 4.5 m up).
        level.brush{ pos = {38, 5.25, 0}, size = {8, 1.5, 16} }
        kit.light_room{ room = yard, theme = "industrial" }
        kit.light_room{ room = hall, theme = "industrial" }
        """;

    private static (EditorDocument Doc, ScriptRunResult Result) RunLayout(AssetVfs? vfs)
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Assets = vfs },
            Layout,
            new ScriptRunOptions { ChunkName = "fixture-grounding", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return (doc, r);
    }

    private static double? FloorBelow(SpatialWorld w, double x, double y, double z) =>
        w.Raycast(new Vec3d(x, y + 0.02, z), new Vec3d(0, -1, 0), 20) is { } h && h["hit"]!.GetValue<bool>() && h["normal"]![1]!.GetValue<double>() > 0.5
            ? y + 0.02 - h["distance"]!.GetValue<double>()
            : null;

    private static double? CeilingAbove(SpatialWorld w, double x, double y, double z) =>
        w.Raycast(new Vec3d(x, y - 0.02, z), new Vec3d(0, 1, 0), 20) is { } h && h["hit"]!.GetValue<bool>() && h["normal"]![1]!.GetValue<double>() < -0.5
            ? y - 0.02 + h["distance"]!.GetValue<double>()
            : null;

    /// <summary>The distance from <paramref name="p"/> to the nearest wall along the four horizontal axes, or null.</summary>
    private static double? WallBehind(SpatialWorld w, Vec3d p)
    {
        double? best = null;
        foreach (Vec3d d in new[] { new Vec3d(1, 0, 0), new Vec3d(-1, 0, 0), new Vec3d(0, 0, 1), new Vec3d(0, 0, -1) })
        {
            if (w.Raycast(p, d, 5) is { } h && h["hit"]!.GetValue<bool>())
            {
                double t = h["distance"]!.GetValue<double>();
                best = best is null ? t : Math.Min(best.Value, t);
            }
        }

        return best;
    }

    /// <summary>Every yard light hangs at its fixture's height over the floor right under it (wall lamps 8 m, posts 7.6 m).</summary>
    private static List<double> AssertYardLightHeights(EditorDocument doc, SpatialWorld w, IReadOnlyDictionary<int, Dictionary<string, string>> tags)
    {
        var floors = new List<double>();
        foreach (LevelObject o in doc.Objects.Where(o => o.Kind == LevelObjectKind.Light))
        {
            if (!tags.TryGetValue(o.Uid, out Dictionary<string, string>? t) || t.GetValueOrDefault("kit.name") != "yard" || t.GetValueOrDefault("kit.part") == "glow")
            {
                continue;
            }

            double? fy = FloorBelow(w, o.Position.X, o.Position.Y, o.Position.Z);
            Assert.True(fy is not null, $"yard light {o.Uid} has no floor below it");
            double above = o.Position.Y - fy!.Value;
            Assert.True(Math.Abs(above - 8.0) <= Tolerance || Math.Abs(above - 7.6) <= Tolerance,
                $"yard light {o.Uid} at {o.Position} is {above:0.000} m over the floor under it");
            floors.Add(fy.Value);
        }

        Assert.InRange(floors.Count, 3, 6);                              // about 0.5 per 100 m² of the 780 m² yard
        return floors;
    }

    [Fact]
    public void Brush_Lamp_Posts_And_Wall_Lamps_Stand_On_Their_Floor_And_Wall_And_Lamps_Hang_From_The_Ceiling()
    {
        (EditorDocument doc, _) = RunLayout(vfs: null);
        SpatialWorld w = SpatialWorld.For(Sealed(doc, sharedBsp: true).Geometry);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(doc);
        var posts = new List<double>();
        var lamps = new List<double>();
        int wallLamps = 0;
        foreach (Brush b in new BrushEditor(doc).Brushes)
        {
            if (!tags.TryGetValue(b.Uid, out Dictionary<string, string>? t) || t.GetValueOrDefault("kit") != "light_room")
            {
                continue;
            }

            (Vec3 mn, Vec3 mx) = WorldBounds(b);
            double cx = (mn.X + mx.X) / 2, cy = (mn.Y + mx.Y) / 2, cz = (mn.Z + mx.Z) / 2;
            if (t.GetValueOrDefault("kit.part") == "post")
            {
                // Measured just beside the 0.2 m post (toward the yard's middle): the post's own bottom face lies on the floor.
                double? fy = FloorBelow(w, cx - (Math.Sign(cx) * 0.15), mn.Y, cz);
                Assert.True(fy is not null, $"post {b.Uid} at ({cx:0.00}, {mn.Y:0.00}, {cz:0.00}) has no floor below it");
                Assert.True(Math.Abs(mn.Y - fy!.Value) <= Tolerance, $"post {b.Uid} base {mn.Y:0.000} m, floor {fy:0.000} m");
                posts.Add(fy.Value);
            }
            else if (t.GetValueOrDefault("kit.part") == "fixture" && cx > 26)
            {
                // The hall's ceiling lamps.
                double? c = CeilingAbove(w, cx, mx.Y, cz);
                Assert.True(c is not null, $"lamp {b.Uid} has no ceiling above it");
                Assert.True(Math.Abs(mx.Y - c!.Value) <= Tolerance, $"lamp {b.Uid} top {mx.Y:0.000} m, ceiling {c:0.000} m");
                lamps.Add(c.Value);
            }
            else if (t.GetValueOrDefault("kit.part") == "fixture" && mx.Y - mn.Y > 0.2)
            {
                // A yard wall lamp (0.25 m tall, 0.3 m deep; a post's head is 0.15 m tall): its back on the wall.
                double? d = WallBehind(w, new Vec3d(cx, cy, cz));
                Assert.True(d is not null && Math.Abs(d.Value - 0.15) <= Tolerance, $"wall lamp {b.Uid} at ({cx:0.00}, {cy:0.00}, {cz:0.00}) is {d} m from a wall");
                wallLamps++;
            }
        }

        List<double> lightFloors = AssertYardLightHeights(doc, w, tags);
        _out.WriteLine($"posts on floors {string.Join(", ", posts.Select(v => v.ToString("0.00")))}; {wallLamps} wall lamps; lamps under {string.Join(", ", lamps.Select(v => v.ToString("0.00")))}; yard lights over floors {string.Join(", ", lightFloors.Select(v => v.ToString("0.00")))}");
        Assert.Contains(posts, y => Math.Abs(y - 1.5) < Tolerance);     // the doorway's post stands on the raised deck
        Assert.True(wallLamps > 0, "the yard's walls carry lamps");
        Assert.Contains(lightFloors, y => Math.Abs(y) < Tolerance);      // lights over the yard floor
        Assert.Contains(lamps, y => Math.Abs(y - 4.5) < Tolerance);     // under the lowered ceiling
        Assert.Contains(lamps, y => Math.Abs(y - 6.0) < Tolerance);     // under the hall ceiling
    }

    [Fact]
    public void Stock_Lamp_Posts_And_Ceiling_Lights_Touch_Their_Floor_And_Ceiling()
    {
        if (TestPaths.RfInstall is null)
        {
            return; // no RF install: the brush fixtures above cover the placement rules
        }

        using AssetVfs vfs = GameMount.Mount(TestPaths.RfInstall);
        ClutterCatalog clutter = GameCatalogs.Load(vfs).Clutter!;
        (EditorDocument doc, _) = RunLayout(vfs);
        SpatialWorld w = SpatialWorld.For(Sealed(doc, sharedBsp: true).Geometry);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = new GedObjectMetadataService(doc).AllTags();
        int poles = 0, hanging = 0, wallLamps = 0;
        foreach (LevelObject o in doc.Objects.Where(o => o.Kind == LevelObjectKind.Clutter))
        {
            if (!tags.TryGetValue(o.Uid, out Dictionary<string, string>? t) || t.GetValueOrDefault("kit") != "light_room")
            {
                continue;
            }

            V3dFile mesh = vfs.LoadMesh(clutter.Find(o.ClassName)!.V3dFilename!)!;
            List<Vec3> pts = mesh.Submeshes.SelectMany(s => s.Lods[0].Batches
                .SelectMany(b => b.Positions.Take(b.NumVertices)).Select(p => p.Add(s.Offset))).ToList();
            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            Mat3 rot = ((Clutter)o.Model).Header.Rotation;
            Vec3 World(Vec3 m) => o.Position.Add(rot.Right.Scale(m.X)).Add(rot.Up.Scale(m.Y)).Add(rot.Forward.Scale(m.Z));
            if (o.ClassName == "Wedge Light 3")
            {
                // A wall lamp: the wall is right behind its back (model -Z), at mid height.
                float minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
                Vec3 front = World(new Vec3(0, (minY + maxY) / 2, maxZ));
                Vec3 back = rot.Forward.Scale(-1);
                JsonObject h = w.Raycast(new Vec3d(front.X, front.Y, front.Z), new Vec3d(back.X, back.Y, back.Z), 5);
                Assert.True(h["hit"]!.GetValue<bool>(), $"wall lamp {o.Uid} has no wall behind it");
                double gap = h["distance"]!.GetValue<double>() - (maxZ - minZ);
                Assert.True(Math.Abs(gap) <= Tolerance, $"wall lamp {o.Uid} back is {gap:0.000} m off the wall");
                wallLamps++;
            }
            else if (o.ClassName.StartsWith("Pole", StringComparison.OrdinalIgnoreCase))
            {
                // The foot: the mesh's lowest vertices, in world space.
                List<Vec3> foot = pts.Where(p => p.Y < minY + 0.05f).Select(World).ToList();
                double fx = foot.Average(p => p.X), fz = foot.Average(p => p.Z), baseY = foot.Min(p => p.Y);
                double? fy = FloorBelow(w, fx, baseY, fz);
                Assert.True(fy is not null, $"lamp post {o.Uid} has no floor below it");
                Assert.True(Math.Abs(baseY - fy!.Value) <= Tolerance, $"lamp post {o.Uid} base {baseY:0.000} m, floor {fy:0.000} m");
                poles++;
            }
            else
            {
                Vec3 top = World(new Vec3(0, maxY, 0));
                double? cy = CeilingAbove(w, o.Position.X, top.Y, o.Position.Z);
                Assert.True(cy is not null, $"fixture {o.Uid} has no ceiling above it");
                Assert.True(Math.Abs(top.Y - cy!.Value) <= Tolerance, $"fixture {o.Uid} ({o.ClassName}) top {top.Y:0.000} m, ceiling {cy:0.000} m");
                hanging++;
            }
        }

        AssertYardLightHeights(doc, w, tags);
        _out.WriteLine($"{poles} lamp posts, {wallLamps} wall lamps, {hanging} ceiling fixtures");
        Assert.True(poles > 0, "the yard open to the sky gets a stock lamp post beside its doorway");
        Assert.True(wallLamps > 0, "the yard's walls get stock wall lamps");
        Assert.True(hanging > 0, "the hall gets stock ceiling fixtures");
    }

    private static (Vec3 Min, Vec3 Max) WorldBounds(Brush b)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            Vec3 w = BrushTransform.WorldVertex(b, v);
            min = Vec3Math.Min(min, w);
            max = Vec3Math.Max(max, w);
        }

        return (min, max);
    }
}
