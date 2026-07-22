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
/// RED-parity edit-time planarity-guard COVERAGE: every remaining commit path that can move a
/// subset of a brush's vertices is either wired to <see cref="FacePlanarizer"/> (a bend-inducing
/// case triangulates, faces end planar; a planar case never splits) or produces planar output by
/// construction (asserted, not hooked). Companion to <see cref="FacePlanarizerTests"/>, which pins
/// the guard itself and the gizmo-drag / deformer / vertex-op paths.
/// </summary>
public sealed class FacePlanarizerCoverageTests
{
    private const string Tex = "t";
    private const float Tol = FacePlanarizer.PlanarityTolerance;

    private static Geometry Box() => BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);

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

    // ================= WIRED OPS: bend-inducing case + no-split guard =================

    [Fact]
    public void MeshSmooth_Bent_Quad_Triangulates_Its_Cells_And_Stays_Planar()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 0, 0, Tex);
        // Bend a corner off the z = 0 plane FIRST: the bilinear sub-cells inherit the saddle and bend.
        int corner = g.Faces[0].Vertices[2].Index;
        g.Vertices[corner] = g.Vertices[corner].Add(new Vec3(0, 0, 1f));

        OpResult r = FaceOps.MeshSmooth(g, new[] { 0 });

        Assert.True(r.Success, r.Message);
        Assert.True(r.FacesTriangulated > 0, "the bent smooth cells must triangulate");
        AssertAllPlanar(g);
    }

    [Fact]
    public void MeshSmooth_Planar_Quad_Never_Splits()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 0, 0, Tex);

        OpResult r = FaceOps.MeshSmooth(g, new[] { 0 });

        Assert.True(r.Success, r.Message);
        Assert.Equal(0, r.FacesTriangulated); // four planar cells, none split
        Assert.Equal(4, g.Faces.Count);
        AssertAllPlanar(g);
    }

    [Fact]
    public void FaceCollapse_That_Bends_A_Neighbour_Triangulates_It()
    {
        // Face A lies in z = 0, Face B in x = 0, sharing vertex 0. Collapsing A moves that shared
        // vertex to A's centroid (x = 1), which leaves B's x = 0 plane → B bends → triangulated.
        var g = new Geometry();
        g.Textures.Add(Tex);
        g.Vertices.Add(new Vec3(0, 0, 0)); // 0 (shared)
        g.Vertices.Add(new Vec3(2, 0, 0)); // 1
        g.Vertices.Add(new Vec3(2, 2, 0)); // 2
        g.Vertices.Add(new Vec3(0, 2, 0)); // 3
        g.Vertices.Add(new Vec3(0, -2, 0)); // 4
        g.Vertices.Add(new Vec3(0, -2, 2)); // 5
        g.Vertices.Add(new Vec3(0, 0, 2)); // 6
        g.Faces.Add(Quad(0, 1, 2, 3)); // A in z = 0
        g.Faces.Add(Quad(0, 4, 5, 6)); // B in x = 0
        GeometryUtil.RecomputeAllPlanes(g);

        OpResult r = FaceOps.Collapse(g, 0);

        Assert.True(r.Success, r.Message);
        Assert.True(r.FacesTriangulated > 0, "the neighbour bent by the collapse must triangulate");
        AssertAllPlanar(g);
    }

    [Fact]
    public void EdgeCollapse_Cube_Edge_Triangulates_The_Bent_Side_Faces_And_Stays_Planar()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];

        OpResult r = EdgeOps.Collapse(g, e);

        Assert.True(r.Success, r.Message);
        // Merging the endpoints to the edge midpoint pulls the two side faces (that touched only one
        // endpoint) off-plane; the guard triangulates them.
        Assert.True(r.FacesTriangulated > 0, "the two bent side faces must triangulate");
        AssertAllPlanar(g);
    }

    [Fact]
    public void EdgeBevel_Orthogonal_Cube_Edge_Keeps_A_Flat_Chamfer()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];

        OpResult r = EdgeOps.Bevel(g, e, 0.3f);

        Assert.True(r.Success, r.Message);
        Assert.Equal(0, r.FacesTriangulated); // orthogonal faces → a planar chamfer, no split
        AssertAllPlanar(g);
    }

    [Fact]
    public void BrushSnapToGrid_Off_Grid_Corner_Triangulates_The_Bent_Face()
    {
        // Composes SnapSelectionToGrid's brush branch: snap every vertex to the grid, then planarize.
        // Snapping is per-vertex (non-affine), so a corner that snaps to a different node than its
        // face-mates bends that face.
        Brush b = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Texture = Tex }, 1);
        Geometry g = b.Geometry;
        // Push corner 0 half a grid cell in X so snapping lands it on the NEXT node (a real, isolated move).
        g.Vertices[0] = g.Vertices[0].Add(new Vec3(0.6f, 0, 0));

        BrushTransform.SnapVerticesToGrid(b, 1f);
        int tri = FacePlanarizer.Planarize(g);

        Assert.True(tri > 0, "the face whose lone corner snapped off-plane must triangulate");
        AssertAllPlanar(g);
    }

    // ================= KEYBOARD NUDGE (explicit face + edge cases) =================
    // These pin the mechanism MainWindow.NudgeSubGeometryMove runs for a keyboard nudge: move the
    // selected sub-geometry's pool vertices by a delta, then planarize scoped to exactly those
    // vertices (identical semantics to the gizmo commit — one edit, moved-vertex scoping).

    [Fact]
    public void KeyboardNudge_Face_Move_Triangulates_A_Bent_Neighbour_And_Stays_Planar()
    {
        // A uniform face translation only tilts an edge-sharing neighbour (it stays a flat
        // parallelogram), but a neighbour sharing a SINGLE corner bends. Face A (z = 0) and Face B
        // (x = 0) share only vertex 0; nudging A along +X drags vertex 0 off B's x = 0 plane.
        var g = new Geometry();
        g.Textures.Add(Tex);
        g.Vertices.Add(new Vec3(0, 0, 0)); // 0 (shared)
        g.Vertices.Add(new Vec3(2, 0, 0)); // 1
        g.Vertices.Add(new Vec3(2, 2, 0)); // 2
        g.Vertices.Add(new Vec3(0, 2, 0)); // 3
        g.Vertices.Add(new Vec3(0, -2, 0)); // 4
        g.Vertices.Add(new Vec3(0, -2, 2)); // 5
        g.Vertices.Add(new Vec3(0, 0, 2)); // 6
        g.Faces.Add(Quad(0, 1, 2, 3)); // A in z = 0
        g.Faces.Add(Quad(0, 4, 5, 6)); // B in x = 0
        GeometryUtil.RecomputeAllPlanes(g);

        var moved = new HashSet<int>(g.Faces[0].Vertices.Select(v => v.Index));
        var delta = new Vec3(0.5f, 0, 0); // A stays in z = 0 (planar), but vertex 0 leaves B's plane
        foreach (int vi in moved)
        {
            g.Vertices[vi] = g.Vertices[vi].Add(delta);
        }

        int tri = FacePlanarizer.Planarize(g, moved);

        Assert.True(tri > 0, "the neighbour that shares only the nudged corner must triangulate");
        AssertAllPlanar(g);
    }

    [Fact]
    public void KeyboardNudge_Edge_Move_Triangulates_Bent_Faces_And_Stays_Planar()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];
        var moved = new HashSet<int> { e.V0, e.V1 };
        var delta = new Vec3(0.5f, 0.5f, 0.5f);

        g.Vertices[e.V0] = g.Vertices[e.V0].Add(delta);
        g.Vertices[e.V1] = g.Vertices[e.V1].Add(delta);
        int tri = FacePlanarizer.Planarize(g, moved);

        Assert.True(tri > 0, "the faces sharing the nudged edge must triangulate");
        AssertAllPlanar(g);
    }

    // ================= PLANAR BY CONSTRUCTION: asserted, never hooked =================

    [Fact]
    public void Pinwheel_Produces_Only_Triangles_So_It_Never_Splits()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 0, 0, Tex);
        int corner = g.Faces[0].Vertices[2].Index;
        g.Vertices[corner] = g.Vertices[corner].Add(new Vec3(0, 0, 1f)); // even a bent source is fine

        Assert.True(FaceOps.Pinwheel(g, 0).Success);

        Assert.All(g.Faces, f => Assert.Equal(3, f.Vertices.Count));
        Assert.Equal(0, FacePlanarizer.Planarize(g)); // any 3 points are coplanar
    }

    [Fact]
    public void FlipEdge_Keeps_Two_Triangles_So_It_Never_Splits()
    {
        var g = new Geometry();
        g.Textures.Add(Tex);
        g.Vertices.Add(new Vec3(0, 0, 0));
        g.Vertices.Add(new Vec3(2, 0, 0));
        g.Vertices.Add(new Vec3(2, 2, 0));
        g.Vertices.Add(new Vec3(0, 2, 0));
        g.Faces.Add(Tri(0, 1, 2));
        g.Faces.Add(Tri(0, 2, 3));
        GeometryUtil.RecomputeAllPlanes(g);

        Assert.True(FaceOps.FlipEdge(g, new[] { 0, 1 }).Success);

        Assert.All(g.Faces, f => Assert.Equal(3, f.Vertices.Count));
        Assert.Equal(0, FacePlanarizer.Planarize(g));
    }

    [Fact]
    public void FaceExtrude_Walls_Are_Flat_Parallelograms()
    {
        Geometry g = Box();
        Assert.True(FaceOps.Extrude(g, 0, 2f).Success);

        Assert.Equal(0, FacePlanarizer.Planarize(g)); // extruded walls span edge×axis → planar
        AssertAllPlanar(g);
    }

    [Fact]
    public void FaceBevel_Ring_Stays_In_The_Source_Plane()
    {
        Geometry g = Box();
        Assert.True(FaceOps.Bevel(g, 0, 0.25f).Success);

        Assert.Equal(0, FacePlanarizer.Planarize(g)); // inset + ring quads lie in the face plane
        AssertAllPlanar(g);
    }

    [Fact]
    public void FaceNWaySplit_Children_Stay_In_The_Face_Plane()
    {
        Geometry g = Box();
        Assert.True(FaceOps.NWaySplit(g, 0, 3, alongU: true).Success);

        Assert.Equal(0, FacePlanarizer.Planarize(g)); // sub-polygons of a planar face
        AssertAllPlanar(g);
    }

    [Fact]
    public void FaceCombine_Of_Coplanar_Halves_Stays_Flat()
    {
        Geometry g = Box();
        Assert.True(FaceOps.NWaySplit(g, 0, 2, alongU: true).Success); // face 0 → two coplanar halves at the end
        int n = g.Faces.Count;

        Assert.True(FaceOps.Combine(g, new[] { n - 2, n - 1 }).Success);

        Assert.Equal(0, FacePlanarizer.Planarize(g));
        AssertAllPlanar(g);
    }

    [Fact]
    public void EdgeExtrude_Quad_Is_Coplanar_With_Its_Face()
    {
        Geometry g = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Face, Width = 2f, Height = 2f, Texture = Tex }, 1).Geometry;
        Dictionary<BrushEdge, List<(int, int)>> adj = EdgeTopology.Adjacency(g);
        BrushEdge boundary = adj.First(kv => kv.Value.Count == 1).Key;

        Assert.True(EdgeOps.Extrude(g, boundary, 0.5f).Success);

        Assert.Equal(0, FacePlanarizer.Planarize(g)); // the new quad lies in the source face's plane
        AssertAllPlanar(g);
    }

    // ================= EditBrushes propagation (the latent-drop fix) =================

    [Fact]
    public void EditBrushes_Surfaces_The_Guard_Count_On_Success()
    {
        // EditBrushes seeds `overall` with a fresh Ok(description); before the fix a successful op's
        // FacesTriangulated was dropped, so the App's NotePlanarized never fired. Now it is carried out.
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, default, Mat3.Identity);

        OpResult r = ed.EditBrushes(new[] { uid }, "bend + planarize", b =>
        {
            b.Geometry.Vertices[0] = b.Geometry.Vertices[0].Add(new Vec3(0.5f, 0.5f, 0.5f));
            int t = FacePlanarizer.Planarize(b.Geometry, new[] { 0 });
            return OpResult.Ok() with { FacesTriangulated = t };
        });

        Assert.Equal(3, r.FacesTriangulated); // the three faces at that corner (0 before the fix)
    }

    // ---- helpers ----

    private static Face Quad(int a, int b, int c, int d) => new()
    {
        Texture = 0,
        SurfaceIndex = -1,
        RoomIndex = -1,
        Vertices = new List<FaceVertex> { Fv(a), Fv(b), Fv(c), Fv(d) },
    };

    private static Face Tri(int a, int b, int c) => new()
    {
        Texture = 0,
        SurfaceIndex = -1,
        RoomIndex = -1,
        Vertices = new List<FaceVertex> { Fv(a), Fv(b), Fv(c) },
    };

    private static FaceVertex Fv(int i) => new() { Index = i };

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "planarize-coverage.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }
}
