using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Edge mode — topology derivation, loop/ring traversal, the edge operators
/// (bevel/extrude/collapse/move; counts + no degenerates) and closest-edge-to-ray pick math.
/// </summary>
public sealed class EdgeModeTests
{
    private static Geometry Box() => BrushFactory.Box(2, 2, 2, 0, 0, 0, "t");

    private static Geometry FaceBrush() =>
        BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Face, Width = 2f, Height = 2f, Texture = "t" }, 1).Geometry;

    // ---- Topology derivation ----

    [Fact]
    public void Box_Derives_Twelve_Manifold_Edges()
    {
        Geometry g = Box();
        IReadOnlyList<BrushEdge> edges = EdgeTopology.Edges(g);
        Assert.Equal(12, edges.Count);

        // Every cube edge is shared by exactly two faces.
        Dictionary<BrushEdge, List<(int, int)>> adj = EdgeTopology.Adjacency(g);
        Assert.All(edges, e => Assert.Equal(2, adj[e].Count));

        // Canonicalization: (a,b) and (b,a) are the same edge.
        Assert.Equal(BrushEdge.Canonical(3, 1), BrushEdge.Canonical(1, 3));
    }

    // ---- Loop / ring ----

    [Fact]
    public void Ring_On_A_Box_Is_A_Four_Edge_Belt()
    {
        Geometry g = Box();
        BrushEdge seed = EdgeTopology.Edges(g)[0];
        IReadOnlyCollection<BrushEdge> ring = EdgeTopology.Ring(g, seed);
        Assert.Equal(4, ring.Count); // parallel edges around the cube
        Assert.Contains(seed, ring);
    }

    [Fact]
    public void Loop_And_Ring_On_A_Cylinder_Follow_Quad_Topology()
    {
        // sides=6, stacks=2 → the MIDDLE ring's vertices are 4-valent (up/down/left/right).
        Geometry g = BrushFactory.Cylinder(2, 2, 2, 6, 2, "t");
        Dictionary<int, List<BrushEdge>> incident = EdgeTopology.IncidentEdges(g);

        // A middle-ring horizontal edge = both endpoints 4-valent.
        BrushEdge mid = EdgeTopology.Edges(g)
            .First(e => incident[e.V0].Count == 4 && incident[e.V1].Count == 4);

        // The loop runs all the way around the ring (6 edges); the ring stacks the 3 parallel
        // horizontal edges (bottom/middle/top) — it stops at the n-gon caps.
        Assert.Equal(6, EdgeTopology.Loop(g, mid).Count);
        Assert.Equal(3, EdgeTopology.Ring(g, mid).Count);
    }

    [Fact]
    public void Loop_Falls_Back_Gracefully_On_Valence_3_Cube_Vertices()
    {
        Geometry g = Box();
        BrushEdge seed = EdgeTopology.Edges(g)[0];
        // Cube vertices are 3-valent, so the loop can't continue — it returns just the seed.
        Assert.Single(EdgeTopology.Loop(g, seed));
    }

    // ---- Operators ----

    [Fact]
    public void Bevel_Adds_A_Chamfer_Face_Without_Degenerates()
    {
        Geometry g = Box();
        int facesBefore = g.Faces.Count;
        BrushEdge e = EdgeTopology.Edges(g)[0];

        OpResult r = EdgeOps.Bevel(g, e, 0.3f);
        Assert.True(r.Success, r.Message);
        Assert.Equal(facesBefore + 1, g.Faces.Count); // one chamfer face
        Assert.True(GeometryUtil.Validate(g));
        // The chamfer created two new parallel edges where the sharp edge was.
        Assert.DoesNotContain(e, EdgeTopology.Edges(g));
    }

    [Fact]
    public void Extrude_Pulls_A_Boundary_Edge_Into_A_New_Quad()
    {
        Geometry g = FaceBrush();
        int facesBefore = g.Faces.Count;
        int vertsBefore = g.Vertices.Count;

        // Every edge of a single-face brush is a boundary (1-face) edge.
        Dictionary<BrushEdge, List<(int, int)>> adj = EdgeTopology.Adjacency(g);
        BrushEdge boundary = adj.First(kv => kv.Value.Count == 1).Key;

        OpResult r = EdgeOps.Extrude(g, boundary, 0.5f);
        Assert.True(r.Success, r.Message);
        Assert.Equal(facesBefore + 1, g.Faces.Count);
        Assert.Equal(vertsBefore + 2, g.Vertices.Count);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Extrude_Rejects_An_Interior_Edge()
    {
        Geometry g = Box();
        BrushEdge interior = EdgeTopology.Edges(g)[0]; // shared by two faces
        Assert.False(EdgeOps.Extrude(g, interior, 0.5f).Success);
    }

    [Fact]
    public void Collapse_Merges_The_Endpoints_Without_Degenerates()
    {
        Geometry g = Box();
        int vertsBefore = g.Vertices.Count;
        BrushEdge e = EdgeTopology.Edges(g)[0];

        OpResult r = EdgeOps.Collapse(g, e);
        Assert.True(r.Success, r.Message);
        Assert.True(g.Vertices.Count < vertsBefore); // the two endpoints welded to one
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Move_Translates_Both_Endpoint_Vertices()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];
        Vec3 a0 = g.Vertices[e.V0];
        Vec3 b0 = g.Vertices[e.V1];
        var delta = new Vec3(0, 1.5f, 0);

        OpResult r = EdgeOps.Move(g, new[] { e }, delta);
        Assert.True(r.Success, r.Message);
        Assert.Equal(a0.Add(delta), g.Vertices[e.V0]);
        Assert.Equal(b0.Add(delta), g.Vertices[e.V1]);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Rotate_About_Pivot_Preserves_Radius_And_Moves_Endpoints()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];
        Vec3 a0 = g.Vertices[e.V0];
        Mat3 rot = Mat3Math.FromAxisAngle(new Vec3(0, 1, 0), 0.5f);

        OpResult r = EdgeOps.Rotate(g, new[] { e }, rot, Vec3.Zero);
        Vec3 a1 = g.Vertices[e.V0];
        Assert.True(r.Success, r.Message);
        Assert.Equal(a0.Y, a1.Y, 3); // rotation about Y keeps Y fixed
        Assert.Equal((a0.X * a0.X) + (a0.Z * a0.Z), (a1.X * a1.X) + (a1.Z * a1.Z), 3); // radius preserved
        Assert.True(System.MathF.Abs(a1.X - a0.X) + System.MathF.Abs(a1.Z - a0.Z) > 0.01f); // actually moved
    }

    [Fact]
    public void Scale_Uniform_Scales_Endpoints_About_Pivot()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];
        Vec3 a0 = g.Vertices[e.V0];

        OpResult r = EdgeOps.Scale(g, new[] { e }, Vec3.Zero, 1.5f);
        Vec3 a1 = g.Vertices[e.V0];
        Assert.True(r.Success, r.Message);
        Assert.Equal(a0.X * 1.5f, a1.X, 3);
        Assert.Equal(a0.Y * 1.5f, a1.Y, 3);
        Assert.Equal(a0.Z * 1.5f, a1.Z, 3);
    }

    [Fact]
    public void ScaleAxis_Scales_Only_Along_The_Axis()
    {
        Geometry g = Box();
        BrushEdge e = EdgeTopology.Edges(g)[0];
        Vec3 a0 = g.Vertices[e.V0];

        OpResult r = EdgeOps.ScaleAxis(g, new[] { e }, Vec3.Zero, new Vec3(1, 0, 0), 1.5f);
        Vec3 a1 = g.Vertices[e.V0];
        Assert.True(r.Success, r.Message);
        Assert.Equal(a0.X * 1.5f, a1.X, 3);
        Assert.Equal(a0.Y, a1.Y, 3);
        Assert.Equal(a0.Z, a1.Z, 3);
    }

    // ---- Pick math ----

    [Fact]
    public void EdgePicker_Picks_The_Nearest_Edge_To_The_Ray()
    {
        // Two parallel edges along X, one at z=0 (near) and one at z=5 (far). A ray down −Z
        // from above the near one must pick the near edge.
        var near = new BrushEdge(0, 1);
        var far = new BrushEdge(2, 3);
        var edges = new[]
        {
            (near, new Vec3(-1, 0, 0), new Vec3(1, 0, 0)),
            (far, new Vec3(-1, 0, 5), new Vec3(1, 0, 5)),
        };

        BrushEdge? hit = EdgePicker.Pick(edges, new Vec3(0, 10, 0), new Vec3(0, -1, 0), tol: 0.5f);
        Assert.Equal(near, hit);

        // A ray that passes between both (offset in X beyond the segments) hits neither within tol.
        Assert.Null(EdgePicker.Pick(edges, new Vec3(50, 10, 0), new Vec3(0, -1, 0), tol: 0.5f));
    }
}
