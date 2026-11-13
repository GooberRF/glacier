using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.ScriptMutationAssert;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Golden tests for the V2 geometry surface: <c>level.brush{}</c>, brush handle reads and
/// mutations, clip / carve / copy / reorder / lock (selection never changes, P12), and texture roles.
/// Every mutation goes through <see cref="ScriptMutationAssert.AssertMutation"/>.
/// </summary>
public sealed class ScriptBrushApiTests
{
    /// <summary>Two entities + three 2 m metal boxes at x = 0, 4, 8.</summary>
    internal static EditorDocument Boxes() => ScriptTestFixture.NewDoc(entities: 2, boxes: 3);

    /// <summary>One 4×2×2 box yawed 30° at (4, 1, 2).</summary>
    internal static EditorDocument Rotated()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        var ed = new BrushEditor(doc);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 2, Depth = 2, Texture = "metal01.tga" },
            new Vec3(4, 1, 2), ScriptRotation.FromEuler(30, 0, 0));
        doc.MarkSaved();
        return doc;
    }

    internal static List<Brush> BrushesOf(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        return doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault()?.Brushes ?? new List<Brush>();
    }

    private static void AssertSameGeometry(Brush expected, Brush actual, float eps = 1e-4f)
    {
        Assert.True(expected.Position.ApproxEquals(actual.Position, eps), $"position {expected.Position} vs {actual.Position}");
        Assert.True(expected.Rotation.ApproxEquals(actual.Rotation, eps), "rotation differs");
        Assert.Equal(expected.Geometry.Vertices.Count, actual.Geometry.Vertices.Count);
        for (int i = 0; i < expected.Geometry.Vertices.Count; i++)
        {
            Assert.True(expected.Geometry.Vertices[i].ApproxEquals(actual.Geometry.Vertices[i], eps), $"vertex {i}");
        }

        Assert.Equal(expected.Geometry.Faces.Count, actual.Geometry.Faces.Count);
    }

    // ---- level.brush{} ---------------------------------------------------------------

    public static IEnumerable<object[]> ShapeCases() => new[]
    {
        new object[] { "{}", new BrushCreateParams { Shape = BrushShape.Box } },
        new object[] { "{size = {2, 3, 4}, splits = {1, 2, 0}}", new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 3, Depth = 4, WidthSplits = 1, HeightSplits = 2 } },
        new object[] { "{shape = 'cylinder'}", new BrushCreateParams { Shape = BrushShape.Cylinder, WidthSplits = 12 } },
        new object[] { "{shape = 'cylinder', sides = 8, stacks = 3, size = {2, 6, 2}}", new BrushCreateParams { Shape = BrushShape.Cylinder, WidthSplits = 8, HeightSplits = 2, Width = 2, Height = 6, Depth = 2 } },
        new object[] { "{shape = 'cone', sides = 5}", new BrushCreateParams { Shape = BrushShape.Cone, WidthSplits = 5 } },
        new object[] { "{shape = 'sphere'}", new BrushCreateParams { Shape = BrushShape.Sphere, WidthSplits = 12, HeightSplits = 5 } },
        new object[] { "{shape = 'sphere', sides = 6, stacks = 4}", new BrushCreateParams { Shape = BrushShape.Sphere, WidthSplits = 6, HeightSplits = 3 } },
        new object[] { "{shape = 'wedge', size = {6, 2, 3}}", new BrushCreateParams { Shape = BrushShape.Wedge, Width = 6, Height = 2, Depth = 3 } },
        new object[] { "{shape = 'quad', size = {3, 2}}", new BrushCreateParams { Shape = BrushShape.Face, Width = 3, Height = 2 } },
        new object[] { "{shape = 'face', size = 2, splits = {1, 1, 0}}", new BrushCreateParams { Shape = BrushShape.Face, Width = 2, Height = 2, WidthSplits = 1, HeightSplits = 1 } },
    };

    [Theory]
    [MemberData(nameof(ShapeCases))]
    public void Level_Brush_Shapes_Match_The_Factory(string table, BrushCreateParams expected)
    {
        AssertMutation(() => ScriptTestFixture.NewDoc(entities: 0), $"level.brush{table}", (doc, _) =>
        {
            Brush made = BrushesOf(doc).Single();
            Brush reference = BrushFactory.Create(expected, made.Uid);
            AssertSameGeometry(reference, made);
            Assert.Equal(reference.Flags, made.Flags);
            Assert.Equal(BrushState.Normal, made.State);
        });
    }

    [Fact]
    public void Level_Brush_Places_At_End_Of_Time_With_Pose()
    {
        AssertMutation(Boxes, "local b = level.brush{pos = {1, 2, 3}, rot = {yaw = 90}, size = {8, 4, 8}, air = true} return b.time_index", (doc, r) =>
        {
            Assert.Equal("3", r.ReturnValue);
            Brush b = BrushesOf(doc).Last();
            Assert.Equal(new Vec3(1, 2, 3), b.Position);
            Assert.True(b.Rotation.Forward.ApproxEquals(new Vec3(1, 0, 0)));
            Assert.Equal((uint)BrushFlags.Air, b.Flags);
            Assert.Equal("Create brush", doc.Undo.Current.Description);
        });
    }

    [Theory]
    [InlineData("{air = true}", BrushFlags.Air)]
    [InlineData("{portal = true}", BrushFlags.Air | BrushFlags.Portal)]
    [InlineData("{portal = true, air = true}", BrushFlags.Air | BrushFlags.Portal)]
    [InlineData("{detail = true}", BrushFlags.Detail)]
    [InlineData("{geoable = true}", BrushFlags.Geoable | BrushFlags.Detail)]
    [InlineData("{emits_steam = true}", BrushFlags.EmitsSteam)]
    public void Level_Brush_Flags(string table, BrushFlags flags)
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, $"level.brush{table}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal((uint)flags, BrushesOf(doc).Single().Flags);
    }

    [Fact]
    public void Level_Brush_Life()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "return level.brush{detail = true, life = 50}.life");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("50", r.ReturnValue);
        Assert.Equal(50, BrushesOf(doc).Single().Life);
    }

    private static string TextureOfFacing(Brush b, int dir)
    {
        float outward = BrushOrientation.OutwardSign(b); // RED stores an air brush's faces inward
        foreach (Face f in b.Geometry.Faces)
        {
            float ny = b.Rotation.Transform(GeometryUtil.Normal(GeometryUtil.Corners(b.Geometry, f))).Y * outward;
            int d = ny >= 0.7f ? 1 : (ny <= -0.7f ? -1 : 0);
            if (d == dir)
            {
                return b.Geometry.Textures[f.Texture];
            }
        }

        return "?";
    }

    [Theory]
    [InlineData(false, "F.tga", "W.tga", "C.tga")]
    [InlineData(true, "C.tga", "W.tga", "F.tga")] // air: the floor you stand on is the bottom face
    public void Texture_Roles_Are_Player_Relative(bool air, string up, string side, string down)
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            $"level.brush{{air = {(air ? "true" : "false")}, rot = {{yaw = 30}}, textures = {{floor = 'F.tga', wall = 'W.tga', ceiling = 'C.tga'}}}}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Brush b = BrushesOf(doc).Single();
        Assert.Equal(up, TextureOfFacing(b, 1));
        Assert.Equal(side, TextureOfFacing(b, 0));
        Assert.Equal(down, TextureOfFacing(b, -1));
    }

    [Fact]
    public void Texture_Falls_Back_Per_Role()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "level.brush{texture = 'all.tga'} level.brush{textures = {floor = 'F.tga'}}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        List<Brush> bs = BrushesOf(doc);
        Assert.All(bs[0].Geometry.Faces, f => Assert.Equal("all.tga", bs[0].Geometry.Textures[f.Texture]));
        Assert.Equal("F.tga", TextureOfFacing(bs[1], 1));
        Assert.Equal(BrushCreateParams.DefaultTexture, TextureOfFacing(bs[1], 0));
        Assert.Equal(BrushCreateParams.DefaultTexture, TextureOfFacing(bs[1], -1));
    }

    [Theory]
    [InlineData("{shape = 'mesh'}", "not supported yet", null)]
    [InlineData("{shape = 'cube'}", "unknown shape 'cube'", "Did you mean")]
    [InlineData("{sides = 6}", "'sides' applies only", null)]
    [InlineData("{shape = 'cone', stacks = 2}", "'stacks' applies only", null)]
    [InlineData("{shape = 'sphere', stacks = 1}", "between 2 and 64", null)]
    [InlineData("{shape = 'cylinder', sides = 2}", "between 3 and 64", null)]
    [InlineData("{shape = 'wedge', splits = {1, 0, 0}}", "'splits' applies only", null)]
    [InlineData("{splits = {33, 0, 0}}", "between 0 and 32", null)]
    [InlineData("{size = {0, 1, 1}}", "greater than 0.01", null)]
    [InlineData("{size = 5000}", "at most 4096", null)]
    [InlineData("{portal = true, air = false}", "always air", null)]
    [InlineData("{texture = 'a.tga', textures = {floor = 'b.tga'}}", "either 'texture'", null)]
    [InlineData("{texture = ''}", "empty string", null)]
    [InlineData("{life = -2}", "'life' must be -1", null)]
    [InlineData("{rot = {yawn = 3}}", "unknown option 'yawn'", "'yaw'")]
    [InlineData("{rot = 90}", "must be a rotation", "ged.rot")]
    [InlineData("{tags = {a = 1}}", "the value of tag 'a' must be a string (got number)", "tostring")]
    [InlineData("{tags = {['Bad Key'] = 'x'}}", "Tag key 'Bad Key' is invalid", "kit.part")]
    public void Level_Brush_Errors_Are_Actionable(string table, string message, string? hint)
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        ScriptApiV2ContractTests.AssertApiError(doc, $"level.brush{table}", message, hint);
        Assert.Empty(BrushesOf(doc));
    }

    // ---- reads -----------------------------------------------------------------------

    [Fact]
    public void Reads_On_A_Rotated_Brush()
    {
        EditorDocument doc = Rotated();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local b = level.brushes[1]\n" +
            "local bb = b.bounds\n" +
            "return string.format('%.3f|%.3f|%.3f|%s|%s|%s|%d|%s|%s|%s|%d|%s', b.rot.yaw, bb.size.x, bb.size.y, " +
            "tostring(b:contains(4, 1, 2)), tostring(b:contains(4.75, 1, 3.299)), tostring(b:contains(4 + 1.6 * math.cos(math.rad(30)), 1, 2 - 1.6 * math.sin(math.rad(30)))), " +
            "b.time_index, tostring(b.locked), tostring(b.hidden), tostring(b.selected), b.life, b.size.x .. ',' .. b.size.y .. ',' .. b.size.z)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        double expectX = (4 * Math.Cos(Math.PI / 6)) + (2 * Math.Sin(Math.PI / 6));
        Assert.Equal($"30.000|{expectX:0.000}|2.000|true|false|true|0|false|false|false|-1|4,2,2", r.ReturnValue);
    }

    [Fact]
    public void Faces_Facing_Uses_World_Normals()
    {
        // Roll 90°: the right vector points up, so the local +X face (factory face 0) faces world up.
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local b = level.brush{rot = {roll = 90}}\n" +
            "local up, down, side = b:faces_facing('up'), b:faces_facing('down'), b:faces_facing('side')\n" +
            "return #up .. ',' .. #down .. ',' .. #side .. ':' .. up[1].index .. ',' .. down[1].index");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("1,1,4:0,1", r.ReturnValue);

        ScriptApiV2ContractTests.AssertApiError(doc, "level.brushes[1]:faces_facing('north')", "unknown direction", "\"up\"");
    }

    // ---- transforms vs the Core op on a clone -------------------------------------------

    public static IEnumerable<object[]> TransformCases() => new[]
    {
        new object[] { "b:set_pos(3, 4, 5)", (Action<Brush>)(c => c.Position = new Vec3(3, 4, 5)) },
        new object[] { "b:set_rot{yaw = 90}", (Action<Brush>)(c => c.Rotation = ScriptRotation.FromEuler(90, 0, 0).Orthonormalize()) },
        new object[] { "b:set_rot(ged.rot(10, 20, 30))", (Action<Brush>)(c => c.Rotation = ScriptRotation.FromEuler(10, 20, 30).Orthonormalize()) },
        new object[] { "b:set_rot{forward = {1, 0, 0}, up = {0, 1, 0}}", (Action<Brush>)(c => c.Rotation = ScriptRotation.FromEuler(90, 0, 0)) },
        new object[] { "b:rotate_about('y', 90, 0, 0, 0)", (Action<Brush>)(c => BrushTransform.RotateAboutPivot(c, Mat3Math.RotationY(MathF.PI / 2), Vec3.Zero)) },
        new object[] { "b:scale(2, 1, 0.5)", (Action<Brush>)(c => BrushTransform.ScaleBy(c, new Vec3(2, 1, 0.5f))) },
        new object[] { "b:stretch_to(6, 0, 1)", (Action<Brush>)(c => BrushTransform.StretchToDimensions(c, 6, 0, 1)) },
        new object[] { "b:mirror('x')", (Action<Brush>)(c => BrushOps.Mirror(c, 0)) },
        new object[] { "b:set_pos(1, 1, 1):set_rot{yaw = 90}", (Action<Brush>)(c => { c.Position = new Vec3(1, 1, 1); c.Rotation = ScriptRotation.FromEuler(90, 0, 0).Orthonormalize(); }) },
    };

    [Theory]
    [MemberData(nameof(TransformCases))]
    public void Transforms_Match_The_Core_Op(string call, Action<Brush> op)
    {
        Brush expected = GeometryClone.Deep(BrushesOf(Rotated())[0]);
        op(expected);
        AssertMutation(Rotated, $"local b = level.brushes[1] {call}", (doc, _) => AssertSameGeometry(expected, BrushesOf(doc)[0]));
    }

    [Theory]
    [InlineData("b:scale(0, 1, 1)", "greater than 0")]
    [InlineData("b:stretch_to(-1, 0, 0)", "between 0 and")]
    [InlineData("b:mirror('w')", "unknown axis")]
    [InlineData("b:rotate_about('q', 90, 0, 0, 0)", "Unknown axis")]
    [InlineData("b:set_life(-2)", "-1 (infinite)")]
    [InlineData("b:set_life(1.5)", "must be an integer")]
    [InlineData("b:reorder('middle')", "unknown position")]
    [InlineData("b:reorder(1.5)", "must be an integer")]
    [InlineData("b:clip{point = {0, 0, 0}}", "'normal' is required")]
    [InlineData("b:clip{point = {0, 0, 0}, normal = {1, 0, 0}, mode = 'slice'}", "unknown mode")]
    [InlineData("b:clip{point = {100, 0, 0}, normal = {1, 0, 0}}", "does not pass through")]
    [InlineData("b:set_textures{}", "at least one")]
    [InlineData("b:box_map(-1)", "ppm")]
    public void Brush_Mutation_Errors(string call, string message)
    {
        EditorDocument doc = Boxes();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        ScriptApiV2ContractTests.AssertApiError(doc, $"local b = level.brushes[1] {call}", message, null);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    [Fact]
    public void Deleted_Brush_Handle_Reports_Clearly()
    {
        EditorDocument doc = Boxes();
        ScriptApiV2ContractTests.AssertApiError(doc, "local b = level.brushes[1] b:delete() b:set_pos(0, 0, 0)", "no longer exists", null);
    }

    // ---- clip / carve / copy / reorder / lock (P12: selection untouched) --------------------

    private static (EditorDocument Doc, BrushEditor Be, int Selected) WithSelection()
    {
        EditorDocument doc = Boxes();
        var be = new BrushEditor(doc);
        int sel = be.Brushes[0].Uid;
        be.SelectBrush(sel);
        return (doc, be, sel);
    }

    private static ScriptRunResult RunShared(EditorDocument doc, BrushEditor be, string src)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, src, brushes: be);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r;
    }

    [Fact]
    public void Clip_Split_Returns_Pieces_And_Leaves_Selection()
    {
        (EditorDocument doc, BrushEditor be, int sel) = WithSelection();
        ScriptRunResult r = RunShared(doc, be,
            "local b = level.brushes[2] local p = b:clip{point = {4, 0, 0}, normal = {1, 0, 0}, mode = 'split'}\n" +
            "return #p .. ':' .. tostring(p[1].uid == b.uid) .. ':' .. p[2].time_index .. ':' .. level.brush_count");
        Assert.Equal("2:true:2:4", r.ReturnValue);
        Assert.Equal(new[] { sel }, be.SelectedBrushes.ToArray());

        AssertMutation(Boxes, "level.brushes[2]:clip{point = {4, 0, 0}, normal = {1, 0, 0}, mode = 'split'}");
    }

    [Fact]
    public void Clip_Cut_Discards_The_Side_The_Normal_Points_To()
    {
        AssertMutation(Boxes, "local p = level.brushes[2]:clip{point = {4, 0, 0}, normal = {1, 0, 0}} return #p .. ':' .. p[1].bounds.max.x", (doc, r) =>
        {
            Assert.Equal("1:4", r.ReturnValue);
            Assert.Equal(3, BrushesOf(doc).Count);
        });
    }

    [Fact]
    public void Carve_Subtracts_From_Touching_Brushes_And_Keeps_The_Cutter()
    {
        (EditorDocument doc, BrushEditor be, int sel) = WithSelection();
        int target = be.Brushes[1].Uid;
        ScriptRunResult r = RunShared(doc, be,
            "local c = level.brush{pos = {5, 0, 0}, size = {2, 1, 1}}\n" +
            "local none = c:carve({level.brushes[1]})\n" +
            "local q = c:carve()\n" +
            "return none.count .. ':' .. q.count .. ':' .. q:get(1).uid .. ':' .. level.brush_count .. ':' .. q:get(1).face_count");
        string[] parts = r.ReturnValue!.Split(':');
        Assert.Equal("0", parts[0]);
        Assert.Equal("1", parts[1]);
        Assert.Equal(target.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[2]);
        Assert.Equal("4", parts[3]);
        Assert.True(int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture) > 6);
        Assert.Equal(new[] { sel }, be.SelectedBrushes.ToArray());

        AssertMutation(Boxes, "level.brush{pos = {5, 0, 0}, size = {2, 1, 1}}:carve()");
    }

    [Fact]
    public void Copy_Duplicates_At_End_Of_Time()
    {
        (EditorDocument doc, BrushEditor be, int sel) = WithSelection();
        ScriptRunResult r = RunShared(doc, be,
            "local b = level.brushes[1] b:set_locked(true) local c = b:copy(10, 0, 0)\n" +
            "return (c.pos.x - b.pos.x) .. ':' .. c.time_index .. ':' .. tostring(c.locked) .. ':' .. c.face_count");
        Assert.Equal("10:3:false:6", r.ReturnValue);
        Assert.False(be.HasClipboard);

        AssertMutation(Boxes, "level.brushes[1]:copy(0, 5, 0)", (d, _) =>
        {
            List<Brush> bs = BrushesOf(d);
            Assert.Equal(4, bs.Count);
            Assert.Equal(bs[0].Position.Add(new Vec3(0, 5, 0)), bs[3].Position);
        });
    }

    [Theory]
    [InlineData("level.brushes[3]:reorder('start')", 2, 0)]
    [InlineData("level.brushes[1]:reorder('end')", 0, 2)]
    [InlineData("level.brushes[1]:reorder(1)", 0, 1)]
    [InlineData("level.brushes[1]:reorder(99)", 0, 2)]
    [InlineData("level.brushes[3]:reorder(-5)", 2, 0)]
    public void Reorder_Moves_In_Time(string call, int from, int to)
    {
        int uid = BrushesOf(Boxes())[from].Uid;
        AssertMutation(Boxes, call, (doc, _) => Assert.Equal(uid, BrushesOf(doc)[to].Uid));
    }

    [Fact]
    public void Lock_Is_Saved_And_Undoable_And_Hide_Is_Session_Only()
    {
        AssertMutation(Boxes, "return tostring(level.brushes[2]:set_locked(true).locked)", (doc, r) =>
        {
            Assert.Equal("true", r.ReturnValue);
            Assert.Equal(BrushState.Locked, BrushesOf(doc)[1].State);
        });

        (EditorDocument doc, BrushEditor be, int sel) = WithSelection();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        ScriptRunResult hide = RunShared(doc, be, "return tostring(level.brushes[2]:set_hidden(true).hidden)");
        Assert.Equal("true", hide.ReturnValue);
        Assert.Equal(0, hide.UndoNodesAdded);
        Assert.True(be.IsBrushHidden(be.Brushes[1].Uid));
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));

        RunShared(doc, be, "level.brushes[2]:set_locked(true)");
        Assert.Equal(new[] { sel }, be.SelectedBrushes.ToArray()); // locking an unselected brush leaves the selection
    }

    [Fact]
    public void Flag_And_Life_Setters()
    {
        AssertMutation(Boxes, "level.brushes[1]:set_geoable(true):set_emits_steam(true):set_life(50)", (doc, _) =>
        {
            Brush b = BrushesOf(doc)[0];
            Assert.Equal((uint)(BrushFlags.Geoable | BrushFlags.Detail | BrushFlags.EmitsSteam), b.Flags);
            Assert.Equal(50, b.Life);
        });

        // V1 set_detail keeps its raw single-bit semantics (no geoable ⇒ detail re-implication).
        EditorDocument v1 = Boxes();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(v1, "local b = level.brushes[1] b:set_geoable(true) b:set_detail(false) return tostring(b.detail)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("false", r.ReturnValue);
    }

    [Theory]
    [InlineData(false, "F.tga", "W.tga", "C.tga")]
    [InlineData(true, "C.tga", "W.tga", "F.tga")]
    public void Set_Textures_By_Role(bool air, string up, string side, string down)
    {
        AssertMutation(Rotated, $"local b = level.brushes[1] b:set_air({(air ? "true" : "false")}) b:set_textures{{floor = 'F.tga', wall = 'W.tga', ceiling = 'C.tga'}}", (doc, _) =>
        {
            Brush b = BrushesOf(doc)[0];
            Assert.Equal(up, TextureOfFacing(b, 1));
            Assert.Equal(side, TextureOfFacing(b, 0));
            Assert.Equal(down, TextureOfFacing(b, -1));
        });

        // A subset touches only its role.
        EditorDocument partial = Rotated();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(partial, "level.brushes[1]:set_textures{wall = 'W2.tga'}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Brush p = BrushesOf(partial)[0];
        Assert.Equal("metal01.tga", TextureOfFacing(p, 1));
        Assert.Equal("W2.tga", TextureOfFacing(p, 0));
    }

    [Fact]
    public void Brush_Box_Map_Matches_UvOps_And_Skips_Portal_Faces()
    {
        Brush expected = GeometryClone.Deep(BrushesOf(Rotated())[0]);
        UvOps.BoxMap(expected.Geometry, Enumerable.Range(0, expected.Geometry.Faces.Count), expected.Rotation, expected.Position, 128, 256, 256);
        AssertMutation(Rotated, "level.brushes[1]:box_map(128)", (doc, _) => AssertUvs(expected, BrushesOf(doc)[0]));

        EditorDocument portal = Rotated();
        BrushesOf(portal)[0].Geometry.Faces[0].Texture = -1; // a portal face
        (ScriptRunResult r, _) = ScriptTestFixture.Run(portal, "level.brushes[1]:box_map()");
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }

    internal static void AssertUvs(Brush expected, Brush actual)
    {
        for (int fi = 0; fi < expected.Geometry.Faces.Count; fi++)
        {
            List<FaceVertex> e = expected.Geometry.Faces[fi].Vertices;
            List<FaceVertex> a = actual.Geometry.Faces[fi].Vertices;
            Assert.Equal(e.Count, a.Count);
            for (int i = 0; i < e.Count; i++)
            {
                Assert.True(Math.Abs(e[i].TextureCoords.U - a[i].TextureCoords.U) < 1e-6 && Math.Abs(e[i].TextureCoords.V - a[i].TextureCoords.V) < 1e-6,
                    $"face {fi} corner {i}: {e[i].TextureCoords} vs {a[i].TextureCoords}");
            }
        }
    }

    // ---- V1 members still behave -------------------------------------------------------

    [Fact]
    public void V1_Brush_Members_Unchanged()
    {
        EditorDocument doc = Boxes();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local b = level.brushes[1] local x = select('#', b:move(1, 0, 0)) b:rotate('y', 90) b:set_texture('rock.tga') b:set_air(true) " +
            "return tostring(x) .. ':' .. b.pos.x .. ':' .. tostring(b.air) .. ':' .. b.faces[1].texture");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("0:1:true:rock.tga", r.ReturnValue);
    }
}
