using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.ScriptBrushApiTests;
using static Ged.Core.Tests.Scripting.ScriptMutationAssert;

namespace Ged.Core.Tests.Scripting;

/// <summary>Golden tests for face reads + UV ops, queries and selection additions.</summary>
public sealed class ScriptFaceAndQueryApiTests
{
    // ---- face reads ------------------------------------------------------------------

    [Fact]
    public void Face_World_Reads()
    {
        EditorDocument doc = Rotated();
        Brush b = BrushesOf(doc)[0];
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local f = level.brushes[1].faces[1]\n" + // factory face 0 = local +X
            "local n, c = f.normal, f.center\n" +
            "return string.format('%.4f,%.4f,%.4f|%.4f,%.4f,%.4f|%.4f|%d|%d|%d', n.x, n.y, n.z, c.x, c.y, c.z, f.area, f.vertex_count, #f.vertices, #f.uvs)");
        Assert.True(r.Success, r.Error?.ToDisplayString());

        Vec3 n = b.Rotation.Transform(GeometryUtil.Normal(GeometryUtil.Corners(b.Geometry, b.Geometry.Faces[0])));
        Vec3 c = GeometryUtil.Centroid(GeometryUtil.Corners(b.Geometry, b.Geometry.Faces[0]).Select(v => BrushTransform.WorldVertex(b, v)).ToList());
        string expected = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{n.X:0.0000},{n.Y:0.0000},{n.Z:0.0000}|{c.X:0.0000},{c.Y:0.0000},{c.Z:0.0000}|4.0000|4|4|4");
        Assert.Equal(expected, r.ReturnValue);
        Assert.True(n.ApproxEquals(new Vec3(MathF.Cos(MathF.PI / 6), 0, -MathF.Sin(MathF.PI / 6)), 1e-4f)); // world right of a 30° yaw

        (ScriptRunResult uv, _) = ScriptTestFixture.Run(doc,
            "local u = level.brushes[1].faces[1].uvs[1] return string.format('%.5f,%.5f', u.u, u.v)");
        FaceVertex fv = b.Geometry.Faces[0].Vertices[0];
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{fv.TextureCoords.U:0.00000},{fv.TextureCoords.V:0.00000}"), uv.ReturnValue);
    }

    // ---- face UV ops vs the Core op on a clone ---------------------------------------------

    public static IEnumerable<object[]> UvCases()
    {
        static Vec3 WorldN(Brush b, Face f) => b.Rotation.Transform(GeometryUtil.Normal(GeometryUtil.Corners(b.Geometry, f)));
        return new[]
        {
            // No ppm = the editor's new-brush tiling (one repeat per GeometryUtil.DefaultTileMetres), whatever the texture size.
            new object[] { "box_map()", (Action<Brush, int>)((b, i) => UvOps.BoxMap(b.Geometry, b.Geometry.Faces[i], b.Rotation, b.Position, 1f / GeometryUtil.DefaultTileMetres, 1, 1)) },
            new object[] { "box_map(256)", (Action<Brush, int>)((b, i) => UvOps.BoxMap(b.Geometry, b.Geometry.Faces[i], b.Rotation, b.Position, 256, 256, 256)) },
            new object[] { "box_map(128)", (Action<Brush, int>)((b, i) => UvOps.BoxMap(b.Geometry, b.Geometry.Faces[i], b.Rotation, b.Position, 128, 256, 256)) },
            new object[] { "planar_map()", (Action<Brush, int>)((b, i) => UvOps.PlanarMap(b.Geometry, new[] { i }, b.Rotation, b.Position, WorldN(b, b.Geometry.Faces[i]), 1f / GeometryUtil.DefaultTileMetres, 1, 1)) },
            new object[] { "cylinder_map()", (Action<Brush, int>)((b, i) => UvOps.CylinderMap(b.Geometry, b.Geometry.Faces[i], b.Rotation, b.Position, 1, 1f / GeometryUtil.DefaultTileMetres, 1, 1)) },
            new object[] { "cylinder_map('x', 64)", (Action<Brush, int>)((b, i) => UvOps.CylinderMap(b.Geometry, b.Geometry.Faces[i], b.Rotation, b.Position, 0, 64, 256, 256)) },
            new object[] { "fit()", (Action<Brush, int>)((b, i) => Fit(b, i, true)) },
            new object[] { "fit(false)", (Action<Brush, int>)((b, i) => Fit(b, i, false)) },
            new object[] { "scale_uv(2, 3)", (Action<Brush, int>)((b, i) => UvOps.Scale(b.Geometry.Faces[i], 2, 3)) },
            new object[] { "offset_uv(0.5, -1)", (Action<Brush, int>)((b, i) => UvOps.Offset(b.Geometry.Faces[i], 0.5f, -1)) },
            new object[] { "rotate_uv(90)", (Action<Brush, int>)((b, i) => RotateRef(b.Geometry.Faces[i], 90)) },
            new object[] { "flip_uv('u')", (Action<Brush, int>)((b, i) => UvOps.FlipU(b.Geometry.Faces[i])) },
            new object[] { "flip_uv('v')", (Action<Brush, int>)((b, i) => UvOps.FlipV(b.Geometry.Faces[i])) },
            new object[] { "set_uvs{{0, 0}, {1, 0}, {u = 1, v = 1}, {0, 1}}", (Action<Brush, int>)((b, i) => SetRef(b.Geometry.Faces[i])) },
            new object[] { "box_map():offset_uv(0.25, 0)", (Action<Brush, int>)((b, i) => { UvOps.BoxMap(b.Geometry, b.Geometry.Faces[i], b.Rotation, b.Position, 1f / GeometryUtil.DefaultTileMetres, 1, 1); UvOps.Offset(b.Geometry.Faces[i], 0.25f, 0); }) },
        };
    }

    private static void Fit(Brush b, int i, bool aspect)
    {
        Face f = b.Geometry.Faces[i];
        UvOps.UvFitTransform t = UvOps.ComputeFitTransform(new[] { new UvOps.FitFace(b.Geometry, f, b.Rotation, b.Position) }, aspect);
        UvOps.ApplyFit(b.Geometry, f, b.Rotation, b.Position, t);
    }

    private static void RotateRef(Face f, float degrees)
    {
        var uvs = f.Vertices.Select(v => v.TextureCoords).ToList();
        UnwrapOps.Rotate(uvs, Enumerable.Range(0, uvs.Count).ToList(), degrees);
        for (int k = 0; k < uvs.Count; k++)
        {
            f.Vertices[k].TextureCoords = uvs[k];
        }
    }

    private static void SetRef(Face f)
    {
        Uv[] uvs = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        for (int k = 0; k < 4; k++)
        {
            f.Vertices[k].TextureCoords = uvs[k];
        }
    }

    [Theory]
    [MemberData(nameof(UvCases))]
    public void Face_Uv_Ops_Match_UvOps(string call, Action<Brush, int> op)
    {
        foreach (int face in new[] { 0, 2 })
        {
            Brush expected = GeometryClone.Deep(BrushesOf(Rotated())[0]);
            op(expected, face);
            AssertMutation(Rotated, $"local f = level.brushes[1].faces[{face + 1}] f:{call}",
                (doc, _) => AssertUvs(expected, BrushesOf(doc)[0]));
        }
    }

    [Theory]
    [InlineData("box_map()")]
    [InlineData("planar_map()")]
    [InlineData("cylinder_map()")]
    [InlineData("fit()")]
    [InlineData("scale_uv(2, 2)")]
    [InlineData("offset_uv(1, 1)")]
    [InlineData("rotate_uv(45)")]
    [InlineData("flip_uv('u')")]
    [InlineData("set_uvs{{0, 0}, {1, 0}, {1, 1}, {0, 1}}")]
    public void Portal_Faces_Are_An_Error_For_Single_Face_Ops(string call)
    {
        EditorDocument doc = Rotated();
        BrushesOf(doc)[0].Geometry.Faces[0].Texture = -1;
        ScriptApiV2ContractTests.AssertApiError(doc, $"level.brushes[1].faces[1]:{call}", "Face 0 is a portal face and has no texture mapping.", "is_portal");
    }

    [Theory]
    [InlineData("set_uvs{{0, 0}, {1, 0}}", "got 2 UV(s) but the face has 4 corners")]
    [InlineData("set_uvs{{0, 'a'}, {1, 0}, {1, 1}, {0, 1}}", "entry #1")]
    [InlineData("set_uvs{u = 1}", "list of {u, v} pairs")]
    [InlineData("flip_uv('w')", "unknown axis")]
    [InlineData("scale_uv(0, 1)", "non-zero")]
    [InlineData("cylinder_map('q')", "unknown axis")]
    [InlineData("box_map(-5)", "positive number")]
    public void Face_Uv_Errors(string call, string message)
    {
        ScriptApiV2ContractTests.AssertApiError(Rotated(), $"level.brushes[1].faces[1]:{call}", message, null);
    }

    // ---- queries -------------------------------------------------------------------------

    [Theory]
    [InlineData("{min = {3, -1, -1}, max = {3.5, 1, 1}}", "2")]           // inside box 2 only
    [InlineData("{min = {1, -1, -1}, max = {2, 1, 1}}", "1")]              // touching box 1's +X face counts
    [InlineData("{min = {-10, -10, -10}, max = {10, 10, 10}}", "1,2,3")]
    [InlineData("{min = {10, 10, 10}, max = {-10, -10, -10}}", "1,2,3")]   // corners in either order
    [InlineData("{min = {20, 0, 0}, max = {30, 1, 1}}", "")]
    public void Brushes_In_Intersects(string box, string expectedIndices)
    {
        EditorDocument doc = Boxes();
        List<Brush> bs = BrushesOf(doc);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, $"local q = level.brushes_in{box} local t = {{}} for i, u in ipairs(q.uids) do t[i] = u end return table.concat(t, ',')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string expected = string.Join(",", expectedIndices.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => bs[int.Parse(i, System.Globalization.CultureInfo.InvariantCulture) - 1].Uid));
        Assert.Equal(expected, r.ReturnValue ?? string.Empty);
    }

    [Fact]
    public void Object_Queries()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local q = level.objects_in{min = {0.5, -1, -1}, max = {2, 1, 1}}\n" +
            "local named = level.objects_named('E2')\n" +
            "return q.count .. ':' .. table.concat(q.uids, ',') .. ':' .. #named .. ':' .. named[1].uid .. ':' .. #level.objects_named('nobody')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("2:2,3:1:2:0", r.ReturnValue);

        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_in{min = {0, 0, 0}}", "'max' is required", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.brushes_in{min = {0, 0, 0}, max = {1, 1, 1}, maximum = 2}", "unknown option 'maximum'", "'max'");
    }

    [Fact]
    public void Brush_Query_Reads()
    {
        EditorDocument doc = Boxes();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local all = level.all_brushes()\n" +
            "local far = level.brushes_where(function(b) return b.pos.x > 2 end)\n" +
            "return all.count .. ':' .. far.count .. ':' .. far:get(1).pos.x .. ':' .. tostring(far:get(3)) .. ':' .. #all.uids");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("3:2:4:nil:3", r.ReturnValue);
    }

    [Theory]
    [InlineData("box_map(128)", "b:box_map(128)")]
    [InlineData("scale_uv(2, 0.5)", "for _, f in ipairs(b.faces) do f:scale_uv(2, 0.5) end")]
    [InlineData("offset_uv(0.25, 1)", "for _, f in ipairs(b.faces) do f:offset_uv(0.25, 1) end")]
    [InlineData("set_textures{floor = 'F.tga', wall = 'W.tga'}", "b:set_textures{floor = 'F.tga', wall = 'W.tga'}")]
    [InlineData("set_flags{geoable = true, emits_steam = true}", "b:set_geoable(true):set_emits_steam(true)")]
    [InlineData("set_flags{air = true, detail = false}", "b:set_air(true) b:set_detail(false)")]
    public void Bulk_Ops_Equal_Per_Item_Results(string bulk, string perItem)
    {
        EditorDocument a = Boxes();
        EditorDocument b = Boxes();
        AssertMutation(Boxes, $"level.all_brushes():{bulk}");
        (ScriptRunResult ra, _) = ScriptTestFixture.Run(a, $"level.all_brushes():{bulk}");
        (ScriptRunResult rb, _) = ScriptTestFixture.Run(b, $"for _, b in ipairs(level.brushes) do {perItem} end");
        Assert.True(ra.Success, ra.Error?.ToDisplayString());
        Assert.True(rb.Success, rb.Error?.ToDisplayString());
        Assert.True(a.SaveToBytes(updateTimestamp: false).AsSpan().SequenceEqual(b.SaveToBytes(updateTimestamp: false)), $"{bulk} != per-item {perItem}");
    }

    [Fact]
    public void Bulk_Reorder_Keeps_Relative_Order()
    {
        List<int> uids = BrushesOf(Boxes()).Select(x => x.Uid).ToList();
        AssertMutation(Boxes, "level.brushes_where(function(b) return b.pos.x < 5 end):reorder('end')", (doc, _) =>
            Assert.Equal(new[] { uids[2], uids[0], uids[1] }, BrushesOf(doc).Select(x => x.Uid).ToArray()));
        AssertMutation(Boxes, "level.brushes_where(function(b) return b.pos.x > 2 end):reorder('start')", (doc, _) =>
            Assert.Equal(new[] { uids[1], uids[2], uids[0] }, BrushesOf(doc).Select(x => x.Uid).ToArray()));
    }

    [Fact]
    public void Empty_Query_Bulk_Ops_Are_No_Ops()
    {
        EditorDocument doc = Boxes();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local q = level.brushes_where(function() return false end) q:box_map() q:scale_uv(2, 2) q:set_flags{air = true} return q.count");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(0, r.UndoNodesAdded);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.all_brushes():set_flags{}", "at least one flag", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.all_brushes():set_flags{solid = true}", "unknown option 'solid'", null);
    }

    // ---- selection -----------------------------------------------------------------------

    [Fact]
    public void Selection_Brushes_And_Faces()
    {
        EditorDocument doc = Boxes();
        var be = new BrushEditor(doc);
        List<Brush> bs = BrushesOf(doc);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "selection.set_brushes({level.brushes[3], level.brushes[1]})\n" +
            "selection.set_faces({level.brushes[2].faces[3], level.brushes[1].faces[2]})\n" +
            "local b, f = selection.brushes, selection.faces\n" +
            "return #b .. ':' .. b[1].uid .. ',' .. b[2].uid .. ':' .. #f .. ':' .. f[1].brush_uid .. '/' .. f[1].index .. ',' .. f[2].brush_uid .. '/' .. f[2].index",
            brushes: be);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal($"2:{bs[0].Uid},{bs[2].Uid}:2:{bs[0].Uid}/1,{bs[1].Uid}/2", r.ReturnValue);
        Assert.Equal(0, r.UndoNodesAdded);
        Assert.Equal(2, be.SelectedBrushes.Count);

        (ScriptRunResult clear, _) = ScriptTestFixture.Run(doc, "selection.set_brushes({}) selection.set_faces({}) return #selection.brushes + #selection.faces", brushes: be);
        Assert.True(clear.Success, clear.Error?.ToDisplayString());
        Assert.Equal("0", clear.ReturnValue);
        Assert.Empty(be.SelectedFaces);
    }
}
