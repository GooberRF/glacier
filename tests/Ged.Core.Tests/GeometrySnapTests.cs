using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Feature 2 (B1) snap-to-geometry: harvesting + query math, the vertex &gt; midpoint &gt;
/// face priority, per-type enable flags, and the drag-integration case (a move near a
/// known vertex lands exactly on it).
/// </summary>
public sealed class GeometrySnapTests
{
    private static readonly IReadOnlyList<Vec3> LineVerts = new[]
    {
        new Vec3(0, 0, 0),
        new Vec3(10, 0, 0),
    };

    private static GeometrySnapIndex LineIndex() =>
        GeometrySnapIndex.Build(LineVerts, new[] { (0, 1) }, Array.Empty<SnapFace>());

    private static SnapFace Quad() => new(
        new[] { new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 2, 0), new Vec3(0, 2, 0) },
        new Vec3(0, 0, 1),
        0f);

    [Fact]
    public void Snaps_To_A_Nearby_Vertex_Exactly()
    {
        GeometrySnapIndex idx = LineIndex();
        SnapResult? hit = idx.Query(new Vec3(10.05f, 0.03f, 0f), radius: 0.2f, SnapKinds.Geometry);

        Assert.NotNull(hit);
        Assert.Equal(SnapKinds.Vertices, hit!.Value.Kind);
        Assert.Equal(new Vec3(10, 0, 0), hit.Value.Position); // lands EXACTLY on the vertex
    }

    [Fact]
    public void Out_Of_Range_Returns_Null()
    {
        Assert.Null(LineIndex().Query(new Vec3(5, 5, 5), radius: 0.5f, SnapKinds.Geometry));
    }

    [Fact]
    public void Harvests_Edge_Midpoints()
    {
        GeometrySnapIndex idx = LineIndex();
        // The edge (0,0,0)-(10,0,0) midpoint is (5,0,0); vertices are the endpoints.
        SnapResult? hit = idx.Query(new Vec3(5.02f, 0, 0), radius: 0.2f, SnapKinds.Midpoints);
        Assert.NotNull(hit);
        Assert.Equal(SnapKinds.Midpoints, hit!.Value.Kind);
        Assert.Equal(new Vec3(5, 0, 0), hit.Value.Position);
    }

    [Fact]
    public void Vertex_Beats_Midpoint_Even_When_Midpoint_Is_Nearer()
    {
        // Verts at 0 and 1 → midpoint at 0.5. Query at 0.45: midpoint (0.5) is nearer than
        // vertex (0 or 1), but vertex priority must win when both are in range.
        var verts = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0) };
        GeometrySnapIndex idx = GeometrySnapIndex.Build(verts, new[] { (0, 1) }, Array.Empty<SnapFace>());

        SnapResult? hit = idx.Query(new Vec3(0.45f, 0, 0), radius: 0.6f, SnapKinds.Geometry);
        Assert.NotNull(hit);
        Assert.Equal(SnapKinds.Vertices, hit!.Value.Kind);
        Assert.Equal(new Vec3(0, 0, 0), hit.Value.Position);
    }

    [Fact]
    public void Per_Type_Flags_Gate_Candidates()
    {
        GeometrySnapIndex idx = LineIndex();
        Vec3 nearVertex = new(0.05f, 0, 0);

        // Vertices disabled → the nearby vertex is ignored.
        Assert.Null(idx.Query(nearVertex, 0.2f, SnapKinds.Midpoints | SnapKinds.Faces));
        // Vertices enabled → found.
        Assert.NotNull(idx.Query(nearVertex, 0.2f, SnapKinds.Vertices));
    }

    [Fact]
    public void Snaps_Onto_A_Face_Plane_When_The_Projection_Is_Inside()
    {
        GeometrySnapIndex idx = GeometrySnapIndex.Build(
            Array.Empty<Vec3>(), Array.Empty<(int, int)>(), new[] { Quad() });

        // Point 0.1 above the quad centre → projects to (1,1,0), inside the polygon.
        SnapResult? hit = idx.Query(new Vec3(1f, 1f, 0.1f), radius: 0.2f, SnapKinds.Faces);
        Assert.NotNull(hit);
        Assert.Equal(SnapKinds.Faces, hit!.Value.Kind);
        Assert.Equal(0f, hit.Value.Position.Z, 4);
        Assert.Equal(1f, hit.Value.Position.X, 4);
        Assert.Equal(1f, hit.Value.Position.Y, 4);
    }

    [Fact]
    public void Face_Snap_Rejects_Projections_Outside_The_Polygon()
    {
        GeometrySnapIndex idx = GeometrySnapIndex.Build(
            Array.Empty<Vec3>(), Array.Empty<(int, int)>(), new[] { Quad() });

        // Above (3,3) — projects to (3,3,0), outside the 2x2 quad → no face hit.
        Assert.Null(idx.Query(new Vec3(3f, 3f, 0.1f), radius: 0.2f, SnapKinds.Faces));
    }

    [Fact]
    public void Vertex_Beats_Face_By_Priority()
    {
        // A quad plus a vertex both within radius of the query; vertex must win.
        var verts = new[] { new Vec3(1f, 1f, 0.05f) };
        GeometrySnapIndex idx = GeometrySnapIndex.Build(verts, Array.Empty<(int, int)>(), new[] { Quad() });

        SnapResult? hit = idx.Query(new Vec3(1f, 1f, 0.06f), radius: 0.2f, SnapKinds.Geometry);
        Assert.NotNull(hit);
        Assert.Equal(SnapKinds.Vertices, hit!.Value.Kind);
    }
}
