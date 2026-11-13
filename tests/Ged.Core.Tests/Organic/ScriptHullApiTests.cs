using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// <c>level.brush_from_points</c>, <c>level.brushes_from_points</c> and <c>geo.check</c> through Lua: the
/// §1.3 error table (message + hint), player-relative role textures, tags, one undo node with an exact
/// undo, strict mode, determinism and an untouched selection.
/// </summary>
public sealed class ScriptHullApiTests
{
    private const string Box = "{{0,0,0},{4,0,0},{0,0,4},{4,0,4},{0,3,0},{4,3,0},{0,3,4},{4,3,4}}";

    private static EditorDocument NewLevel() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    private static ScriptRunResult Ok(EditorDocument doc, string script)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r;
    }

    private static void AssertError(string script, string message, string? hint)
    {
        EditorDocument doc = NewLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.False(r.Success, "expected an error: " + script);
        Assert.Equal(message, r.Error!.Message);
        Assert.Equal(hint, r.Error.Hint);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    // ---- the error table ------------------------------------------------------------------------

    [Theory]
    [InlineData("level.brush_from_points{points = {{0,0,0},{1,0,0},{0,1,0}}}",
        "level.brush_from_points: 'points' must list 4 to 256 points (got 3).", "Split a larger shape into several convex brushes.")]
    [InlineData("level.brush_from_points{air = true}",
        "level.brush_from_points: 'points' must list 4 to 256 points (got none).", "Split a larger shape into several convex brushes.")]
    [InlineData("local p = {} for i = 1, 257 do p[i] = {i, i % 7, i % 5} end level.brush_from_points{points = p}",
        "level.brush_from_points: 'points' must list 4 to 256 points (got 257).", "Split a larger shape into several convex brushes.")]
    [InlineData("level.brush_from_points{points = {{0,0,0},{1,0,0},{0,1,0},{0,0,1/0}}}",
        "level.brush_from_points: points[4] must be a vec, {x, y, z} or {x=, y=, z=} of finite numbers.", null)]
    [InlineData("level.brush_from_points{points = {{0,0,0},{9000,0,0},{0,1,0},{0,0,1}}}",
        "level.brush_from_points: points[2] is 9000 m from the origin on an axis; the limit is 8192 m.", "Keep generated geometry within ±8192 m, where every grid point is exact.")]
    [InlineData("level.brush_from_points{points = {{0,0,0},{1,0,0},'up',{0,0,1}}}",
        "level.brush_from_points: points[3] must be a vec, {x, y, z} or {x=, y=, z=} of finite numbers.", null)]
    [InlineData("level.brush_from_points{points = {{0,0,0},{0.2,0,0},{0,0.2,0},{0,0,0.2},{3,0,0}}, snap = 1}",
        "level.brush_from_points: only 2 distinct points remain after snapping to 1 m.", "Move points further apart, or pass a finer snap.")]
    [InlineData("level.brush_from_points{points = {{0,0,0},{4,0,0},{0,0,4},{4,0,4}}}",
        "level.brush_from_points: the points are coplanar (thinnest extent 0 m); a brush needs volume.", "Add a point off the plane, at least 0.01 m away.")]
    [InlineData("level.brush_from_points{points = {{3,0,0},{0,3,0},{0,0,3},{1,1,1},{2,1,0}}}",
        "level.brush_from_points: the points are coplanar (thinnest extent 0 m); a brush needs volume.", "Add a point off the plane, at least 0.01 m away.")]
    [InlineData("level.brush_from_points{points = {{0,0,0},{5000,0,0},{0,1,0},{0,0,1}}}",
        "level.brush_from_points: the hull spans 5000 m; the limit is 4096 m.", null)]
    [InlineData("level.brush_from_points{points = " + Box + ", snap = 0.3}",
        "level.brush_from_points: 'snap' must be a power of two from 1/1024 to 1 (got 0.3).", "Nearest valid value: 1/4.")]
    [InlineData("level.brush_from_points{points = " + Box + ", tile = 100}",
        "level.brush_from_points: 'tile' must be between 0.25 and 64 metres (got 100).", "Nearest valid value: 64.")]
    [InlineData("level.brush_from_points{points = " + Box + ", tile = 0.1}",
        "level.brush_from_points: 'tile' must be between 0.25 and 64 metres (got 0.1).", "Nearest valid value: 0.25.")]
    [InlineData("level.brush_from_points{points = {{0,0,0},{4,0,0},{0,0,4},{4,0,4},{0,3,0},{4,3,0},{0,3,4},{4,3,4},{2,1,2}}, strict = true}",
        "level.brush_from_points: points[9] is not a corner of the hull (it lies inside the hull).", "Remove it, or drop strict = true.")]
    [InlineData("level.brush_from_points{points = {{0,0,0},{4,0,0},{0,0,4},{4,0,4},{0,3,0},{4,3,0},{0,3,4},{4,3,4},{4,3,4.0001}}, strict = true}",
        "level.brush_from_points: points[9] is not a corner of the hull (it repeats points[8]).", "Remove it, or drop strict = true.")]
    [InlineData("level.brush_from_points{points = " + Box + ", life = -2}",
        "level.brush_from_points: 'life' must be -1 (infinite) or more (got -2).", null)]
    [InlineData("level.brushes_from_points{hulls = {" + Box + ", {{0,0,0},{1,0,0},{0,1,0}}}}",
        "level.brushes_from_points: hulls[2] must list 4 to 256 points (got 3).", "Split a larger shape into several convex brushes.")]
    [InlineData("level.brushes_from_points{hulls = {" + Box + ", {{0,0,0},{1,0,0},{0,1,0},{0,0,0/0}}}}",
        "level.brushes_from_points: hulls[2]: points[4] must be a vec, {x, y, z} or {x=, y=, z=} of finite numbers.", null)]
    [InlineData("level.brushes_from_points{hulls = {" + Box + ", {{0,0,0},{4,0,0},{0,0,4},{4,0,4}}}}",
        "level.brushes_from_points: hulls[2]: the points are coplanar (thinnest extent 0 m); a brush needs volume.", "Add a point off the plane, at least 0.01 m away.")]
    [InlineData("level.brushes_from_points{hulls = {}}",
        "level.brushes_from_points: 'hulls' must list 1 to 2000 point lists (got 0).", "Each entry is a list of 4 to 256 points, e.g. hulls = {{{0,0,0}, {4,0,0}, {0,0,4}, {2,3,2}}}.")]
    public void Errors_Have_The_Stated_Message_And_Hint(string script, string message, string? hint) =>
        AssertError(script, message, hint);

    [Fact]
    public void Over_Limits_Names_The_Counts()
    {
        // 200 points on a sphere: far more than 64 hull vertices.
        const string script =
            "local p = {} for i = 1, 200 do local u = (i - 0.5) / 100 - 1 local t = i * 2.399963 local s = math.sqrt(1 - u * u)\n" +
            "p[i] = {10 * s * math.cos(t), 10 * u, 10 * s * math.sin(t)} end\n" +
            "level.brush_from_points{points = p}";
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.False(r.Success);
        Assert.Matches(@"^level\.brush_from_points: the hull has \d+ vertices and \d+ faces; the limit is 64 vertices / 128 faces\.$", r.Error!.Message);
        Assert.Equal("Use fewer points, or split the shape into several brushes.", r.Error.Hint);
    }

    [Fact]
    public void A_Bulk_Call_Over_The_Hull_Building_Budget_Fails_Before_Building_Anything()
    {
        // 33 hulls of 256 points: 33 x 256^2 = 2,162,688 points^2, over the 2,097,152 budget of one call.
        EditorDocument doc = NewLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local hulls = {} for k = 1, 33 do local pts = {} for i = 1, 256 do pts[i] = {k * 12 + 4 * math.cos(i), i % 7, 4 * math.sin(i)} end hulls[k] = pts end " +
            "level.brushes_from_points{hulls = hulls}");
        Assert.False(r.Success);
        Assert.Contains("the 33 hulls hold 2162688 points² of hull-building work; one call may hold at most 2097152", r.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Use fewer points per hull", r.Error.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Unknown_Keys_Are_Rejected_With_The_Nearest_Name()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "level.brush_from_points{points = " + Box + ", pos = {0, 0, 0}}");
        Assert.False(r.Success);
        Assert.Equal("level.brush_from_points: unknown option 'pos'.", r.Error!.Message);
        (ScriptRunResult b, _) = ScriptTestFixture.Run(doc, "level.brushes_from_points{hulls = {" + Box + "}, hull = {}}");
        Assert.False(b.Success);
        Assert.Contains("Did you mean 'hulls'?", b.Error!.Hint, StringComparison.Ordinal);
    }

    // ---- behaviour --------------------------------------------------------------------------------

    [Fact]
    public void Brush_From_Points_Is_One_Undo_Node_And_Undo_Restores_Bytes()
    {
        ScriptMutationAssert.AssertMutation(NewLevel,
            "local b = level.brush_from_points{points = " + Box + ", air = true, tags = {kit = 'test'}}\n" +
            "assert(b.air and b.face_count == 6)",
            (doc, _) =>
            {
                Brush b = Assert.Single(new BrushEditor(doc).Brushes);
                Assert.Equal(Mat3.Identity, b.Rotation);
                Assert.Equal(new Vec3(2, 1.5f, 2), b.Position);
                Assert.Equal("test", KitTestHarness.Tags(doc)[b.Uid]["kit"]);
            });
    }

    [Fact]
    public void Brushes_From_Points_Is_One_Undo_Node_In_List_Order()
    {
        ScriptMutationAssert.AssertMutation(NewLevel,
            "local q = level.brushes_from_points{hulls = {" + Box + ", {{10,0,0},{12,0,0},{10,0,2},{11,2,1}}, {{20,0,0},{22,0,0},{20,0,2},{21,2,1}}}, detail = true, tags = {kit = 'rocks'}}\n" +
            "assert(q.count == 3) assert(q.brushes[1].face_count == 6) assert(q.brushes[2].face_count == 4)\n" +
            "assert(q.brushes[2].pos.x == 11 and q.brushes[3].pos.x == 21)",
            (doc, _) =>
            {
                var brushes = new BrushEditor(doc).Brushes;
                Assert.Equal(3, brushes.Count);
                Assert.True(brushes[0].Uid < brushes[1].Uid && brushes[1].Uid < brushes[2].Uid);
                Assert.All(brushes, b => Assert.Equal((uint)BrushFlags.Detail, b.Flags));
                IReadOnlyDictionary<int, Dictionary<string, string>> tags = KitTestHarness.Tags(doc);
                Assert.All(brushes, b => Assert.Equal("rocks", tags[b.Uid]["kit"]));
            });
    }

    [Fact]
    public void Role_Textures_Are_Player_Relative()
    {
        EditorDocument doc = NewLevel();
        Ok(doc, "level.brush_from_points{points = " + Box + ", textures = {floor = 'f.tga', wall = 'w.tga', ceiling = 'c.tga'}}\n" +
                "level.brushes_from_points{hulls = {" + Box + "}, air = true, textures = {floor = 'f.tga', wall = 'w.tga', ceiling = 'c.tga'}}");
        IReadOnlyList<Brush> brushes = new BrushEditor(doc).Brushes;
        string Tex(Brush b, float ny) => b.Geometry.Textures[b.Geometry.Faces.Single(f => f.Plane.Normal.Y == ny).Texture];
        Assert.Equal("f.tga", Tex(brushes[0], 1));   // solid: the up-facing face is stood on
        Assert.Equal("c.tga", Tex(brushes[0], -1));
        // air: the bottom face is the floor. RED stores an air brush's faces inward, so the floor's stored normal
        // points up into the room — the side the player sees, exactly as for the solid's top face.
        Assert.Equal("f.tga", Tex(brushes[1], 1));
        Assert.Equal("c.tga", Tex(brushes[1], -1));
        Assert.All(brushes, b => Assert.All(b.Geometry.Faces.Where(f => f.Plane.Normal.Y == 0), f => Assert.Equal("w.tga", b.Geometry.Textures[f.Texture])));

        Ok(doc, "level.brush_from_points{points = " + Box + ", texture = 'all.tga'}");
        Brush one = new BrushEditor(doc).Brushes[2];
        Assert.Equal(new[] { "all.tga" }, one.Geometry.Textures);
    }

    [Fact]
    public void Flags_Tile_And_Snap_Apply()
    {
        EditorDocument doc = NewLevel();
        Ok(doc, "local b = level.brush_from_points{points = {{0.3,0,0.3},{4.2,0,0},{0,0,4},{4,0,4},{0,3,0},{4,3,0},{0,3,4},{4.4,3.1,4}}, geoable = true, life = 50, snap = 0.5, tile = 8}\n" +
                "assert(b.geoable and b.detail)");
        Brush b = Assert.Single(new BrushEditor(doc).Brushes);
        Assert.Equal(50, b.Life);
        Assert.All(b.Geometry.Vertices, v => Assert.True(BrushTransform.WorldVertex(b, v).X * 2 % 1 == 0));
    }

    [Fact]
    public void Strict_Accepts_A_Point_Set_Of_Corners()
    {
        EditorDocument doc = NewLevel();
        ScriptRunResult r = Ok(doc, "return level.brush_from_points{points = " + Box + ", strict = true}.face_count");
        Assert.Equal("6", r.ReturnValue);
    }

    [Fact]
    public void Selection_Is_Untouched()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1, boxes: 1);
        var be = new BrushEditor(doc);
        int sel = be.Brushes[0].Uid;
        be.SelectBrush(sel);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "level.brush_from_points{points = " + Box + "} level.brushes_from_points{hulls = {" + Box + "}} geo.check(level.brushes[1])", brushes: be);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(new[] { sel }, be.SelectedBrushes.ToArray());
    }

    [Fact]
    public void Same_Script_On_Fresh_Documents_Saves_Identical_Bytes()
    {
        const string script =
            "local hulls = {}\n" +
            "for k = 1, 20 do local p = {} for i = 1, 12 do p[i] = {k * 6 + rng.range(-2, 2), rng.range(0, 3), rng.range(-2, 2)} end hulls[k] = p end\n" +
            "level.brushes_from_points{hulls = hulls, detail = true}\n" +
            "level.brush_from_points{points = " + Box + ", air = true}";
        EditorDocument a = NewLevel(), b = NewLevel();
        Ok(a, script);
        Ok(b, script);
        Assert.Equal(a.SaveToBytes(updateTimestamp: false), b.SaveToBytes(updateTimestamp: false));
        Assert.Equal(21, new BrushEditor(a).Brushes.Count);
    }

    // ---- geo.check --------------------------------------------------------------------------------

    [Fact]
    public void Geo_Check_Reports_A_Hull_And_A_Broken_Brush()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0, boxes: 1);
        ScriptRunResult r = Ok(doc,
            "local h = geo.check(level.brush_from_points{points = " + Box + "})\n" +
            "local bx = geo.check(level.brushes[1])\n" +
            "return tostring(h.ok) .. ',' .. tostring(h.convex) .. ',' .. h.face_count .. ',' .. h.vertex_count .. ',' .. h.max_face_corners .. ',' .. #h.issues .. ',' .. tostring(bx.ok)");
        Assert.Equal("true,true,6,8,4,0,true", r.ReturnValue);

        // Bend one corner of the box: no longer planar or convex; the issues say so.
        Brush box = new BrushEditor(doc).Brushes[0];
        box.Geometry.Vertices[0] = box.Geometry.Vertices[0].Add(new Vec3(0, 0.3f, 0));
        ScriptRunResult bad = Ok(doc,
            "local r = geo.check(level.brushes[1])\n" +
            "return tostring(r.ok) .. ',' .. tostring(r.closed) .. ',' .. tostring(r.planar) .. ',' .. tostring(#r.issues > 0) .. ',' .. tostring(r.max_planar_error > 0.01)");
        Assert.Equal("false,true,false,true,true", bad.ReturnValue);

        (ScriptRunResult nil, _) = ScriptTestFixture.Run(doc, "geo.check(nil)");
        Assert.False(nil.Success);
        Assert.Equal("geo.check: a brush is required.", nil.Error!.Message);
    }
}
