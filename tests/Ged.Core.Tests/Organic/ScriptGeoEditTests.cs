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
/// The <c>geo</c> edits through Lua (ORGANIC-GEOMETRY-SPEC §2.1–§2.4): hull-mode results are closed, convex,
/// planar, unrotated and on the 1/1024 m grid; slab pieces follow their source in time order, carry the
/// source tags plus <c>geo.piece</c> and meet on bit-identical faces; every error has its
/// message and hint and changes nothing; one undo node with an exact undo; deterministic; selection untouched.
/// </summary>
public sealed class ScriptGeoEditTests
{
    private const string Column = "local c = level.brush{shape = 'box', pos = {0, 2, 0}, size = {2, 4, 2}}\n";

    private static EditorDocument NewLevel() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    private static (EditorDocument Doc, ScriptRunResult Result, ScriptLog Log) Ok(string script, EditorDocument? doc = null)
    {
        doc ??= NewLevel();
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, script);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return (doc, r, log);
    }

    private static void AssertError(string script, string message, string? hint)
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult setup, _) = ScriptTestFixture.Run(doc, "-- nothing");
        Assert.True(setup.Success);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.False(r.Success, "expected an error: " + script);
        Assert.Equal(message, r.Error!.Message);
        Assert.Equal(hint, r.Error.Hint);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    private static List<Brush> Brushes(EditorDocument doc) => new BrushEditor(doc).Brushes.ToList();

    /// <summary>Every brush is a valid hull: closed, outward, convex, planar, unrotated, every world vertex on the grid.</summary>
    internal static void AssertHull(Brush b)
    {
        GeometryReport r = GeometryCheck.Check(b);
        Assert.True(r.Closed && r.Outward && r.Convex && r.Planar, $"brush {b.Uid}: {string.Join(" ", r.Issues)}");
        Assert.Equal(Mat3.Identity, b.Rotation);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            Vec3 w = b.Position.Add(v);
            foreach (float c in new[] { w.X, w.Y, w.Z })
            {
                Assert.Equal(Math.Round(c * 1024.0) / 1024.0, c);
            }
        }
    }

    private static List<Vec3d> World(Brush b) => HullDeform.WorldVertices(b);

    // ---- all-or-nothing ----------------------------------------------------------------------------

    [Fact]
    public void A_Multi_Brush_Edit_That_Throws_Midway_Restores_Every_Brush_And_Records_Nothing()
    {
        (EditorDocument doc, _, _) = Ok("level.brush{shape = 'box', pos = {0, 2, 0}, size = {2, 4, 2}}\nlevel.brush{shape = 'box', pos = {6, 2, 0}, size = {2, 4, 2}}");
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;
        var editor = new BrushEditor(doc);
        List<int> uids = editor.Brushes.Select(b => b.Uid).ToList();
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => editor.EditBrushesWithPieces(uids, "inject", b =>
        {
            // The first brush is moved for real; the second edit fails with a non-script exception.
            if (++calls == 2)
            {
                throw new InvalidOperationException("injected");
            }

            b.Position = b.Position.Add(new Vec3(1, 0, 0));
            return (OpResult.Ok(), Array.Empty<Brush>());
        }, out _));
        Assert.Equal(2, calls);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        Assert.Equal(nodes, doc.Undo.NodeCount);

        calls = 0;
        Assert.Throws<InvalidOperationException>(() => editor.EditBrushes(uids, "inject", b =>
        {
            if (++calls == 2)
            {
                throw new InvalidOperationException("injected");
            }

            b.Position = b.Position.Add(new Vec3(1, 0, 0));
            return OpResult.Ok();
        }));
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        Assert.Equal(nodes, doc.Undo.NodeCount);
    }

    // ---- jitter ----------------------------------------------------------------------------------

    [Fact]
    public void Jitter_Rebuilds_A_Convex_Grid_Hull()
    {
        (EditorDocument doc, ScriptRunResult r, _) = Ok(Column + "local q = geo.jitter{brushes = c, amount = 0.3, seed = 3}\nassert(q.count == 1 and q.uids[1] == c.uid)");
        Brush b = Assert.Single(Brushes(doc));
        AssertHull(b);
        Assert.NotEqual(8, World(b).Distinct().Count(p => Math.Abs(p.X) == 1 && Math.Abs(p.Z) == 1)); // corners moved
        Assert.Equal(1, r.UndoNodesAdded);
    }

    [Fact]
    public void Jitter_With_A_Zero_Y_Amount_Keeps_Floor_And_Top_Flat()
    {
        (EditorDocument doc, _, _) = Ok(Column + "geo.jitter{brushes = c, amount = {0.3, 0, 0.3}, seed = 9}");
        Brush b = Assert.Single(Brushes(doc));
        AssertHull(b);
        Assert.All(World(b), p => Assert.True(p.Y == 0 || p.Y == 4, $"y = {p.Y}"));
        Assert.Contains(b.Geometry.Faces, f => b.Rotation.Transform(f.Plane.Normal).Y == 1);
        Assert.Contains(b.Geometry.Faces, f => b.Rotation.Transform(f.Plane.Normal).Y == -1);
    }

    [Fact]
    public void Jitter_Is_Deterministic_And_Independent_Of_Uids()
    {
        const string Script = "geo.jitter{brushes = level.all_brushes():where(function(b) return b.size.y == 4 end), amount = 0.4, seed = 5}";
        (EditorDocument a, _, _) = Ok(Column + Script);
        (EditorDocument b, _, _) = Ok(Column + Script);
        Assert.Equal(a.SaveToBytes(updateTimestamp: false), b.SaveToBytes(updateTimestamp: false));

        // Shift every UID by creating (and keeping) an unrelated object first: the jittered shape is unchanged.
        (EditorDocument c, _, _) = Ok("level.place('Light', 50, 2, 50) level.place('Light', 51, 2, 50)\n" + Column + Script);
        Brush ja = Brushes(a).Single(), jc = Brushes(c).Single();
        Assert.NotEqual(ja.Uid, jc.Uid);
        Assert.Equal(World(ja), World(jc));

        (EditorDocument d, _, _) = Ok(Column + Script.Replace("seed = 5", "seed = 6"));
        Assert.NotEqual(World(ja), World(Brushes(d).Single()));
    }

    [Fact]
    public void Jitter_Triangulate_Mode_Keeps_Faces_Planar()
    {
        (EditorDocument doc, _, _) = Ok("local c = level.brush{shape = 'box', pos = {0, 2, 0}, size = {4, 4, 4}, splits = {1, 1, 1}}\n" +
                                        "geo.jitter{brushes = c, amount = 0.2, mode = 'triangulate'}");
        Brush b = Assert.Single(Brushes(doc));
        GeometryReport r = GeometryCheck.Check(b);
        Assert.True(r.Closed && r.Planar, string.Join(" ", r.Issues));
    }

    // ---- twist / bend / taper --------------------------------------------------------------------

    [Fact]
    public void Twist_Pieces_Follow_The_Source_In_Time_Order_And_Meet_On_Identical_Faces()
    {
        (EditorDocument doc, _, ScriptLog log) = Ok(
            Column +
            "c:tag('kit.part', 'column')\n" +
            "local after = level.brush{shape = 'box', pos = {8, 2, 0}, size = {2, 4, 2}}\n" +
            "local q = geo.twist{brushes = c, degrees = 90, pieces = 4}\n" +
            "assert(q.count == 4 and q.uids[1] == c.uid)\n" +
            "for i = 1, 4 do assert(level.find_brush(q.uids[i]).time_index == i - 1) end\n" +
            "assert(after.time_index == 4)");
        Assert.DoesNotContain(log.Entries, e => e.Level == ScriptLogLevel.Warning);
        List<Brush> all = Brushes(doc);
        Assert.Equal(5, all.Count);
        List<Brush> pieces = all.Take(4).ToList();
        pieces.ForEach(AssertHull);

        IReadOnlyDictionary<int, Dictionary<string, string>> tags = KitTestHarness.Tags(doc);
        for (int k = 0; k < 4; k++)
        {
            Assert.Equal($"{k + 1}/4", tags[pieces[k].Uid]["geo.piece"]);
            Assert.Equal("column", tags[pieces[k].Uid]["kit.part"]);
            Assert.Equal(all[0].Flags, pieces[k].Flags);
        }

        // Neighbours share their cut face bit-for-bit (y = 1, 2, 3).
        for (int k = 0; k < 3; k++)
        {
            double y = k + 1;
            var top = World(pieces[k]).Where(p => p.Y == y).OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
            var bottom = World(pieces[k + 1]).Where(p => p.Y == y).OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
            Assert.True(top.Count >= 3);
            Assert.Equal(top, bottom);
        }

        // The top of the column is turned by 90° (a square: its corners land on the turned positions).
        Assert.All(World(pieces[3]).Where(p => p.Y == 4), p => Assert.Equal(1, Math.Max(Math.Abs(p.X), Math.Abs(p.Z)), 3));
    }

    [Fact]
    public void Taper_Scales_The_Top_And_Bend_Curls_Toward_Its_Axis()
    {
        (EditorDocument doc, _, _) = Ok(Column + "geo.taper{brushes = c, scale = 0.5, pieces = 2}");
        List<Brush> pieces = Brushes(doc);
        Assert.Equal(2, pieces.Count);
        pieces.ForEach(AssertHull);
        Assert.All(World(pieces[1]).Where(p => p.Y == 4), p => Assert.Equal(0.5, Math.Abs(p.X), 6));

        (EditorDocument bent, _, _) = Ok("local beam = level.brush{shape = 'box', pos = {0, 1, 0}, size = {1, 1, 12}}\n" +
                                         "local q = geo.bend{brushes = beam, degrees = 90}\nassert(q.count == 6)");
        List<Brush> arc = Brushes(bent);
        Assert.Equal(6, arc.Count); // max(2, ceil(90 / 15))
        arc.ForEach(AssertHull);
        double rise = arc.SelectMany(World).Max(p => p.Y);
        Assert.True(rise > 3, $"the ends curl up toward +y (highest point {rise})");
    }

    [Fact]
    public void Twist_Triangulate_Mode_Is_The_Editor_Deformer()
    {
        (EditorDocument doc, _, _) = Ok(Column + "geo.twist{brushes = c, degrees = 30, mode = 'triangulate'}");
        Brush b = Assert.Single(Brushes(doc));
        GeometryReport r = GeometryCheck.Check(b);
        Assert.True(r.Closed && r.Planar, string.Join(" ", r.Issues));
    }

    // ---- extrude / bevel / weld / snap -----------------------------------------------------------

    [Fact]
    public void Extrude_And_Bevel_Add_Faces_And_Keep_A_Closed_Brush()
    {
        (EditorDocument doc, _, _) = Ok(Column + "local q = geo.extrude{faces = c:faces_facing('up'), distance = 2}\nassert(q.count == 1)\nassert(math.abs(c.bounds.max.y - 6) < 1e-4)");
        Brush b = Assert.Single(Brushes(doc));
        GeometryReport r = GeometryCheck.Check(b);
        Assert.True(r.Closed && r.Outward && r.Planar, string.Join(" ", r.Issues));
        Assert.Equal(10, b.Geometry.Faces.Count);

        (EditorDocument doc2, _, _) = Ok(Column + "geo.bevel{faces = c:faces_facing('up'), amount = 0.25}");
        Brush b2 = Assert.Single(Brushes(doc2));
        Assert.Equal(10, b2.Geometry.Faces.Count);
        Assert.True(GeometryCheck.Check(b2).Closed);
    }

    [Fact]
    public void Weld_Merges_Close_Vertices_And_Snap_Repairs_Float_Noise()
    {
        (EditorDocument doc, _, _) = Ok(Column + "geo.weld{brushes = {c}}");
        Assert.True(GeometryCheck.Check(Brushes(doc).Single()).Closed);

        (EditorDocument doc2, _, _) = Ok("local c = level.brush{shape = 'box', pos = {0.3333, 2.0001, 0.1}, size = {2, 4, 2}, rot = {yaw = 30}}\n" +
                                         "geo.snap{brushes = c, grid = 1/64}");
        Brush s = Assert.Single(Brushes(doc2));
        AssertHull(s);
        Assert.All(World(s), p => Assert.Equal(Math.Round(p.X * 64) / 64, p.X));
    }

    // ---- errors ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(Column + "geo.jitter{brushes = c, amount = 0.1, sede = 3}", "geo.jitter: unknown option 'sede'.", "Did you mean 'mode'? Valid options: brushes, amount, seed, mode, tile.")]
    [InlineData(Column + "geo.jitter{amount = 0.1}", "geo.jitter: 'brushes' is required.", "Pass a brush, a list of brushes or UIDs, or a brush_query.")]
    [InlineData(Column + "geo.jitter{brushes = {}, amount = 0.1}", "geo.jitter: 'brushes' lists no brushes.", "Pass a brush, a list of brushes or UIDs, or a non-empty brush_query.")]
    [InlineData(Column + "geo.jitter{brushes = c}", "geo.jitter: 'amount' is required.", "The largest move per axis in metres, e.g. amount = 0.2 or amount = {0.3, 0, 0.3}.")]
    [InlineData(Column + "geo.jitter{brushes = c, amount = 17}", "geo.jitter: 'amount' must be 0 to 16 metres on each axis (got 17).", "Nearest valid value: 16.")]
    [InlineData(Column + "geo.jitter{brushes = c, amount = 0.1, mode = 'hul'}", "geo.jitter: unknown mode 'hul'.", "Did you mean: hull?")]
    [InlineData(Column + "c:set_locked(true) geo.jitter{brushes = c, amount = 0.1}", "geo.jitter: brush#UID is locked.", "Unlock it with b:set_locked(false), or leave it out.")]
    [InlineData("local p = level.brush{shape = 'box', size = {4, 4, 0.5}, portal = true} geo.jitter{brushes = p, amount = 0.1}",
        "geo.jitter: brush#UID: a portal brush cannot be deformed.", "Leave portal brushes out, e.g. q:where(function(b) return not b.portal end).")]
    [InlineData(Column + "geo.twist{brushes = c}", "geo.twist: 'degrees' is required.", "A number from -360 to 360 degrees.")]
    [InlineData(Column + "geo.twist{brushes = c, degrees = 400}", "geo.twist: 'degrees' must be -360 to 360 degrees (got 400).", "Nearest valid value: 360.")]
    [InlineData(Column + "geo.twist{brushes = c, degrees = 45, axis = 'w'}", "geo.twist: unknown axis 'w'.", "Did you mean: x, y, z?")]
    [InlineData(Column + "geo.twist{brushes = c, degrees = 45, pieces = 33}", "geo.twist: 'pieces' must be 1 to 32 (got 33).", "Nearest valid value: 32.")]
    [InlineData(Column + "geo.twist{brushes = c, degrees = 45, pieces = 2, mode = 'triangulate'}", "geo.twist: 'pieces' applies only to mode = \"hull\".", "Drop 'pieces', or use mode = \"hull\".")]
    [InlineData("local c = level.brush{shape = 'box', size = {2, 0.03, 2}} geo.twist{brushes = c, degrees = 45, pieces = 8}",
        "geo.twist: brush#UID: pieces = 8 makes slabs 0.003281 m thin; a slab must be at least 0.01 m.", "Use at most 2 pieces.")]
    [InlineData(Column + "geo.bend{brushes = c, degrees = 45, axis = 'y', toward = 'y'}", "geo.bend: 'toward' must differ from 'axis' (both are \"y\").", "Use toward = \"x\" or \"z\".")]
    [InlineData(Column + "geo.bend{brushes = c, degrees = 0}", "geo.bend: 'degrees' must not be 0.", "Give the total arc, e.g. degrees = 45.")]
    [InlineData("local c = level.brush{shape = 'box', size = {1, 6, 2}} geo.bend{brushes = c, degrees = 180}",
        "geo.bend: brush#UID is 6 m thick toward y, but a 180° bend of its 2 m length curls with a radius of only 0.63662 m.", "Bend fewer degrees, or bend a longer brush.")]
    [InlineData(Column + "geo.taper{brushes = c, scale = 0.01}", "geo.taper: 'scale' must be 0.05 to 20 (got 0.01).", "Nearest valid value: 0.05.")]
    [InlineData(Column + "geo.extrude{faces = c:faces_facing('up'), distance = 0}", "geo.extrude: 'distance' must be at least 0.001 m in size (got 0).", "Pass a positive distance to extrude outward, a negative one to push the face in.")]
    [InlineData(Column + "geo.bevel{faces = c:faces_facing('up'), amount = 1}", "geo.bevel: 'amount' must be 0.01 to 0.99 (got 1).", "Nearest valid value: 0.99.")]
    [InlineData(Column + "geo.weld{brushes = c, tolerance = 5}", "geo.weld: 'tolerance' must be 0.00001 to 1 metre (got 5).", "Nearest valid value: 1.")]
    [InlineData("local c = level.brush{shape = 'box', size = {0.5, 4, 2}} geo.weld{brushes = c, tolerance = 1}", "geo.weld: welding at 1 m collapses brush#UID.", "Use a smaller tolerance.")]
    [InlineData(Column + "geo.snap{brushes = c, grid = 0.3}", "geo.snap: 'grid' must be a power of two from 1/1024 to 16 (got 0.3).", "Nearest valid value: 1/4.")]
    [InlineData(Column + "geo.jitter{brushes = c, amount = 0.1, tile = 100}", "geo.jitter: 'tile' must be between 0.25 and 64 metres (got 100).", "Nearest valid value: 64.")]
    public void Errors_Have_The_Stated_Message_And_Hint(string script, string message, string? hint)
    {
        // The edited brush is the first one the script creates; its UID is resolved in the message.
        EditorDocument doc = NewLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int uid = doc.UidWatermark;
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.False(r.Success, "expected an error: " + script);
        Assert.Equal(message.Replace("UID", uid.ToString(System.Globalization.CultureInfo.InvariantCulture)), r.Error!.Message);
        Assert.Equal(hint, r.Error.Hint);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Hull_Mode_Refuses_A_Non_Convex_Brush()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            Column + "geo.extrude{faces = c:faces_facing('up'), distance = 1}\n" +
            "geo.bevel{faces = c:faces_facing('up'), amount = 0.5}\n" +
            "geo.extrude{faces = c:faces_facing('up'), distance = -0.5}\n" +
            "geo.jitter{brushes = c, amount = 0.1}");
        Assert.False(r.Success);
        Assert.Matches(@"^geo\.jitter: brush#\d+ is not convex\.$", r.Error!.Message);
        Assert.Equal("Use mode = \"triangulate\", or rebuild it with level.brush_from_points.", r.Error.Hint);
    }

    [Fact]
    public void Pieces_Of_A_Mover_Brush_Are_Refused()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            Column + "level.create_mover{name = 'm', brushes = {c}}\n" +
            "geo.twist{brushes = c, degrees = 30, pieces = 2}");
        Assert.False(r.Success);
        Assert.Matches(@"^geo\.twist: brush#\d+ belongs to mover 'm'; cutting it into 2 pieces would leave the new pieces out of the mover\.$", r.Error!.Message);

        (EditorDocument ok, _, _) = Ok(Column + "level.create_mover{name = 'm', brushes = {c}}\ngeo.twist{brushes = c, degrees = 30}");
        AssertHull(Brushes(ok).Single());
    }

    // ---- undo, determinism, selection ------------------------------------------------------------

    [Fact]
    public void Deformers_Are_Deterministic()
    {
        const string Script = Column +
            "geo.twist{brushes = c, degrees = 75, pieces = 5}\n" +
            "local beam = level.brush{shape = 'cylinder', pos = {6, 5, 0}, size = {1, 10, 1}, sides = 10}\n" +
            "geo.bend{brushes = beam, axis = 'y', toward = 'x', degrees = 70}\n" +
            "geo.taper{brushes = level.brush{shape = 'sphere', pos = {-6, 2, 0}, size = 3}, scale = 0.4, pieces = 3}";
        (EditorDocument a, _, _) = Ok(Script);
        (EditorDocument b, _, _) = Ok(Script);
        Assert.Equal(a.SaveToBytes(updateTimestamp: false), b.SaveToBytes(updateTimestamp: false));
        Brushes(a).ForEach(AssertHull);
    }

    [Fact]
    public void Every_Edit_Is_One_Undo_Node_With_An_Exact_Undo()
    {
        foreach (string edit in new[]
        {
            "geo.jitter{brushes = c, amount = 0.2}",
            "geo.twist{brushes = c, degrees = 60, pieces = 3}",
            "geo.bend{brushes = c, axis = 'y', toward = 'x', degrees = 30}",
            "geo.taper{brushes = c, scale = 2}",
            "geo.extrude{faces = c:faces_facing('up'), distance = 1}",
            "geo.bevel{faces = c:faces_facing('down'), amount = 0.4}",
            "geo.weld{brushes = c}",
            "geo.snap{brushes = c, grid = 1/16}",
        })
        {
            EditorDocument doc = NewLevel();
            Ok(Column + "c:tag('k', 'v')", doc);
            byte[] before = doc.SaveToBytes(updateTimestamp: false);
            var brushes = new BrushEditor(doc);
            brushes.SelectBrush(brushes.Brushes[0].Uid, additive: false);
            (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local c = level.brushes[1]\n" + edit, brushes: brushes);
            Assert.True(r.Success, edit + ": " + r.Error?.ToDisplayString());
            Assert.Equal(1, r.UndoNodesAdded);
            Assert.True(brushes.IsBrushSelected(brushes.Brushes[0].Uid), edit + ": the selection must be untouched");
            Assert.Single(brushes.SelectedBrushes);
            doc.Undo.Undo();
            Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        }
    }
}
