using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// RED-parity edit-time planarity guard (<see cref="FacePlanarizer"/>): moving a vertex off a face's
/// plane triangulates that face so brush faces stay flat, carrying UVs/flags, never splitting planar
/// faces, and folding into the edit's single undo entry.
/// </summary>
public sealed class FacePlanarizerTests
{
    private const string Tex = "test.tga";
    private const float Tol = FacePlanarizer.PlanarityTolerance;

    private static float Deviation(Geometry g, Face f)
    {
        List<Vec3> c = GeometryUtil.Corners(g, f);
        Vec3 n = GeometryUtil.Normal(c);
        Vec3 mid = GeometryUtil.Centroid(c);
        float max = 0f;
        foreach (Vec3 p in c)
        {
            max = System.MathF.Max(max, System.MathF.Abs(n.Dot(p.Sub(mid))));
        }

        return max;
    }

    private static void AssertAllPlanar(Geometry g)
    {
        foreach (Face f in g.Faces)
        {
            Assert.True(Deviation(g, f) <= Tol + 1e-6f, $"Face with {f.Vertices.Count} verts is non-planar ({Deviation(g, f)} m).");
        }
    }

    [Fact]
    public void Drag_One_Cube_Corner_Off_Plane_Triangulates_Its_Three_Faces()
    {
        Geometry g = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);
        Assert.Equal(6, g.Faces.Count);

        // Move corner 0 diagonally outward so it leaves all three axis-aligned faces meeting there.
        int moved = 0;
        var before = new List<Vec3>(g.Vertices);
        g.Vertices[moved] = g.Vertices[moved].Add(new Vec3(0.5f, 0.5f, 0.5f));

        int count = FacePlanarizer.Planarize(g, new[] { moved });

        Assert.Equal(3, count);                       // the three faces at that corner
        Assert.Equal(9, g.Faces.Count);               // 3 quads kept + 3 quads -> 6 triangles
        Assert.Equal(6, g.Faces.Count(f => f.Vertices.Count == 3));
        Assert.Equal(3, g.Faces.Count(f => f.Vertices.Count == 4)); // the untouched faces stay quads
        AssertAllPlanar(g);

        // No new pool vertices were introduced (the fan reuses corners).
        Assert.Equal(before.Count, g.Vertices.Count);
    }

    [Fact]
    public void Coplanar_Preserving_Move_Does_Not_Triangulate()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 0, 0, Tex); // one quad in the z = 0 plane
        Assert.Single(g.Faces);

        // Slide a corner within the face plane (x/y only): the quad stays flat.
        g.Vertices[0] = g.Vertices[0].Add(new Vec3(0.3f, 0.2f, 0f));

        int count = FacePlanarizer.Planarize(g, new[] { 0 });

        Assert.Equal(0, count);
        Assert.Single(g.Faces);
        Assert.Equal(4, g.Faces[0].Vertices.Count);
    }

    [Fact]
    public void Triangles_Carry_Texture_Flags_Smoothing_And_Per_Corner_Uvs()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 0, 0, Tex);
        Face src = g.Faces[0];
        src.Texture = 3;
        src.Flags = (ushort)(FaceFlags.IsDetail | FaceFlags.FullBright);
        src.SmoothingGroups = 0xABCD;
        src.RoomIndex = 7;
        for (int i = 0; i < src.Vertices.Count; i++)
        {
            src.Vertices[i].TextureCoords = new Uv(i * 0.25f, i * 0.5f);
        }

        var uvByIndex = src.Vertices.ToDictionary(v => v.Index, v => v.TextureCoords);

        // Bend one corner out of the z = 0 plane.
        g.Vertices[g.Faces[0].Vertices[2].Index] = g.Vertices[g.Faces[0].Vertices[2].Index].Add(new Vec3(0, 0, 1f));
        int count = FacePlanarizer.Planarize(g, new[] { g.Faces[0].Vertices[2].Index });

        Assert.Equal(1, count);
        Assert.Equal(2, g.Faces.Count);
        var ids = new HashSet<int>();
        foreach (Face t in g.Faces)
        {
            Assert.Equal(3, t.Vertices.Count);
            Assert.Equal(3, t.Texture);
            Assert.Equal((ushort)(FaceFlags.IsDetail | FaceFlags.FullBright), t.Flags);
            Assert.Equal(0xABCDu, t.SmoothingGroups);
            Assert.Equal(7, t.RoomIndex);
            Assert.True(ids.Add(t.FaceId), "each triangle gets a distinct face id");
            foreach (FaceVertex fv in t.Vertices)
            {
                Assert.Equal(uvByIndex[fv.Index], fv.TextureCoords); // per-corner UV preserved
            }
        }
    }

    [Fact]
    public void Planarize_Scoped_To_Moved_Vertices_Leaves_Untouched_Faces_Alone()
    {
        // A brush that already carries a bent face; editing an unrelated vertex must not split it.
        Geometry g = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);
        int bentCorner = 0;
        g.Vertices[bentCorner] = g.Vertices[bentCorner].Add(new Vec3(0.5f, 0.5f, 0.5f)); // pre-existing non-planarity, not planarized

        // Move a DIFFERENT corner and scope planarization to it only.
        int other = Enumerable.Range(0, g.Vertices.Count).First(i => i != bentCorner
            && !g.Faces.Any(f => f.Vertices.Any(v => v.Index == bentCorner) && f.Vertices.Any(v => v.Index == i)));
        var faceCountsBefore = g.Faces.Count;
        g.Vertices[other] = g.Vertices[other].Add(new Vec3(0.5f, 0.5f, 0.5f));
        int count = FacePlanarizer.Planarize(g, new[] { other });

        // Only faces touching `other` split; the pre-existing bent faces at `bentCorner` are untouched.
        Assert.Equal(3, count);
        Assert.True(g.Faces.Any(f => f.Vertices.Count == 4 && Deviation(g, f) > Tol),
            "the pre-existing bent face (not touched by this edit) is left intact, still a bent quad");
    }

    [Theory]
    [InlineData("align")]
    [InlineData("snap")]
    [InlineData("stretch")]
    [InlineData("twist")]
    [InlineData("jitter")]
    public void Each_Triggering_Op_Triangulates_Bent_Faces(string op)
    {
        Geometry g = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);

        // VertexOps report the count directly; deformers are composed the way the App's ApplyDeformer
        // does — run the deformer, then planarize.
        int triangulated = op switch
        {
            // Align the min-X and max-X corners onto their mean X (=0): both leave their side faces.
            "align" => VertexOps.Align(g, CornersDifferingIn(g, axis: 0), 0).FacesTriangulated,

            // Push a corner off-grid, then snap it back to a coarse grid: its faces bend.
            "snap" => SnapCase(g),

            // Stretch a vertical edge's two corners apart in Z: the bottom/top faces bend.
            "stretch" => DeformerCase(g, gg => Deformers.Stretch(gg, new Vec3(1f, 1f, 2f), VerticalEdge(gg))),

            "twist" => DeformerCase(g, gg => Deformers.Twist(gg, 1, 45f)),
            "jitter" => DeformerCase(g, gg => Deformers.Jitter(gg, 0.4f, seed: 7)),
            _ => 0,
        };

        Assert.True(triangulated > 0, $"{op} should have triangulated at least one bent face");
        AssertAllPlanar(g);
    }

    private static int SnapCase(Geometry g)
    {
        // Push corner 0 past the grid midpoint so snapping lands it on a NEW grid node (a real move).
        g.Vertices[0] = g.Vertices[0].Add(new Vec3(0.6f, 0.6f, 0.6f));
        return VertexOps.SnapToGrid(g, new[] { 0 }, 1f).FacesTriangulated;
    }

    [Fact]
    public void Bend_On_Axis_Aligned_Box_Needs_No_Split_And_The_Guard_Leaves_It_Planar()
    {
        // Bend is a 2D warp extruded along the third axis, so it maps each axis-aligned box face to
        // another planar face — the planarity guard correctly triangulates nothing (constraint: planar
        // faces are never needlessly split), while still composing through the same ApplyDeformer path.
        Geometry g = BrushFactory.Box(4, 4, 4, 0, 0, 0, Tex);
        Deformers.Bend(g, 0, 1, 45f);
        int count = FacePlanarizer.Planarize(g);
        Assert.Equal(0, count);
        Assert.Equal(6, g.Faces.Count);
        AssertAllPlanar(g);
    }

    private static int DeformerCase(Geometry g, System.Action<Geometry> deform)
    {
        deform(g);
        return FacePlanarizer.Planarize(g);
    }

    /// <summary>The min- and max-coordinate corners along an axis (they lie on opposite side faces).</summary>
    private static List<int> CornersDifferingIn(Geometry g, int axis)
    {
        int lo = 0, hi = 0;
        for (int i = 1; i < g.Vertices.Count; i++)
        {
            if (g.Vertices[i].Component(axis) < g.Vertices[lo].Component(axis))
            {
                lo = i;
            }

            if (g.Vertices[i].Component(axis) > g.Vertices[hi].Component(axis))
            {
                hi = i;
            }
        }

        return new List<int> { lo, hi };
    }

    /// <summary>A vertical edge: two corners sharing X and Y but differing in Z.</summary>
    private static List<int> VerticalEdge(Geometry g)
    {
        for (int i = 0; i < g.Vertices.Count; i++)
        {
            for (int j = i + 1; j < g.Vertices.Count; j++)
            {
                Vec3 a = g.Vertices[i], b = g.Vertices[j];
                if (System.MathF.Abs(a.X - b.X) < 1e-4f && System.MathF.Abs(a.Y - b.Y) < 1e-4f && System.MathF.Abs(a.Z - b.Z) > 0.1f)
                {
                    return new List<int> { i, j };
                }
            }
        }

        return new List<int> { 0, 1 };
    }

    [Fact]
    public void Weld_That_Bends_A_Face_Triangulates()
    {
        // Weld one corner of a box onto a non-adjacent corner: the faces the moved corner belonged to bend.
        Geometry g = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);
        // Pick a corner and a far corner not sharing a face with it.
        int from = 0;
        int to = Enumerable.Range(0, g.Vertices.Count).First(i => i != from
            && !g.Faces.Any(f => f.Vertices.Any(v => v.Index == from) && f.Vertices.Any(v => v.Index == i)));
        OpResult r = VertexOps.Weld(g, new[] { to, from }); // weld `from` onto `to` (last is kept)
        Assert.True(r.Success, r.Message);
        AssertAllPlanar(g);
    }

    // ---- Undo + round-trip through the editor service --------------------------

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "planarize.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void One_Undo_Restores_The_Original_Cube_Exactly()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, default, Mat3.Identity);

        Brush b0 = ed.FindBrush(uid)!;
        var origVerts = b0.Geometry.Vertices.Select(v => v).ToList();
        var origFaces = b0.Geometry.Faces.Select(f => f.Vertices.Select(v => v.Index).ToList()).ToList();
        Assert.Equal(6, origFaces.Count);

        // Simulate the gizmo drag commit: move a corner off-plane, then planarize — ONE undo entry.
        ed.EditBrushes(new[] { uid }, "Move vertex + planarize", b =>
        {
            b.Geometry.Vertices[0] = b.Geometry.Vertices[0].Add(new Vec3(0.5f, 0.5f, 0.5f));
            FacePlanarizer.Planarize(b.Geometry, new[] { 0 });
            return OpResult.Ok();
        });
        Assert.Equal(9, ed.FindBrush(uid)!.Geometry.Faces.Count);

        doc.Undo.Undo(); // single undo

        Brush b1 = ed.FindBrush(uid)!;
        Assert.Equal(origVerts.Count, b1.Geometry.Vertices.Count);
        for (int i = 0; i < origVerts.Count; i++)
        {
            Assert.True(origVerts[i].ApproxEquals(b1.Geometry.Vertices[i]), $"vertex {i} not restored");
        }

        var backFaces = b1.Geometry.Faces.Select(f => f.Vertices.Select(v => v.Index).ToList()).ToList();
        Assert.Equal(origFaces.Count, backFaces.Count);
        for (int i = 0; i < origFaces.Count; i++)
        {
            Assert.Equal(origFaces[i], backFaces[i]);
        }
    }

    [Fact]
    public void Triangulated_Brush_Round_Trips_Through_Save_Reload()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, default, Mat3.Identity);
        ed.EditBrushes(new[] { uid }, "Move vertex + planarize", b =>
        {
            b.Geometry.Vertices[0] = b.Geometry.Vertices[0].Add(new Vec3(0.5f, 0.5f, 0.5f));
            FacePlanarizer.Planarize(b.Geometry, new[] { 0 });
            return OpResult.Ok();
        });
        Assert.Equal(9, ed.FindBrush(uid)!.Geometry.Faces.Count);

        byte[] saved = doc.SaveToBytes();
        var reloaded = EditorDocument.OpenBytes(saved);
        var ed2 = new BrushEditor(reloaded);
        Brush back = ed2.Brushes.First(b => b.Uid == uid);

        Assert.Equal(9, back.Geometry.Faces.Count);
        Assert.Equal(6, back.Geometry.Faces.Count(f => f.Vertices.Count == 3));
        Assert.True(GeometryUtil.Validate(back.Geometry));
    }
}
