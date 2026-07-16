using System;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Feature 2 (B1) integration through the shared SnapPolicy: a move drag whose pivot
/// lands within radius of a known vertex snaps EXACTLY onto it (geometry &gt; grid),
/// records the target for the highlight marker, and falls back to grid / free move
/// otherwise.
/// </summary>
public sealed class SnapPolicyGeometryTests
{
    private static GeometrySnapIndex IndexWithVertexAt(Vec3 v) =>
        GeometrySnapIndex.Build(new[] { v }, Array.Empty<(int, int)>(), Array.Empty<SnapFace>());

    [Fact]
    public void A_Move_Drag_Near_A_Vertex_Lands_Exactly_On_It()
    {
        var snap = new SnapPolicy
        {
            Enabled = true,
            Kinds = SnapKinds.Grid | SnapKinds.Vertices,
            GridSize = 1f,
            GeometryIndex = IndexWithVertexAt(new Vec3(5.3f, 2.1f, -1.4f)),
        };

        // Pivot at origin, dragged to ~(5.32, 2.08, -1.42): within a 0.2 m snap radius.
        Vec3 landed = snap.MovedPivotSnapped(
            Vec3.Zero, new Vec3(5.32f, 2.08f, -1.42f), invert: false, worldRadius: 0.2f);

        Assert.Equal(new Vec3(5.3f, 2.1f, -1.4f), landed); // exact vertex, NOT a grid multiple
        Assert.NotNull(snap.LastGeometrySnap);
        Assert.Equal(SnapKinds.Vertices, snap.LastGeometrySnap!.Value.Kind);
    }

    [Fact]
    public void Falls_Back_To_Grid_When_No_Geometry_Target_Is_In_Range()
    {
        var snap = new SnapPolicy
        {
            Enabled = true,
            Kinds = SnapKinds.Grid | SnapKinds.Vertices,
            GridSize = 1f,
            GeometryIndex = IndexWithVertexAt(new Vec3(100, 0, 0)), // far away
        };

        Vec3 landed = snap.MovedPivotSnapped(Vec3.Zero, new Vec3(3.4f, 0f, 0f), invert: false, worldRadius: 0.2f);
        Assert.Equal(3f, landed.X, 4); // grid-snapped to the nearest metre
        Assert.Null(snap.LastGeometrySnap);
    }

    [Fact]
    public void Magnet_Off_Is_A_Free_Move()
    {
        var snap = new SnapPolicy
        {
            Enabled = false,
            Kinds = SnapKinds.Grid | SnapKinds.Vertices,
            GeometryIndex = IndexWithVertexAt(new Vec3(3, 0, 0)),
        };

        Vec3 landed = snap.MovedPivotSnapped(Vec3.Zero, new Vec3(2.9f, 0f, 0f), invert: false, worldRadius: 0.2f);
        Assert.Equal(2.9f, landed.X, 4);
        Assert.Null(snap.LastGeometrySnap);
    }

    [Fact]
    public void Disabling_The_Vertices_Kind_Leaves_Only_Grid()
    {
        var snap = new SnapPolicy
        {
            Enabled = true,
            Kinds = SnapKinds.Grid, // geometry kinds off
            GridSize = 1f,
            GeometryIndex = IndexWithVertexAt(new Vec3(5.3f, 0, 0)),
        };

        Vec3 landed = snap.MovedPivotSnapped(Vec3.Zero, new Vec3(5.3f, 0f, 0f), invert: false, worldRadius: 0.2f);
        Assert.Equal(5f, landed.X, 4); // grid multiple, vertex ignored
        Assert.Null(snap.LastGeometrySnap);
    }

    [Fact]
    public void SnapWorldPoint_Snaps_A_Free_Placement_Point()
    {
        var snap = new SnapPolicy
        {
            Enabled = true,
            Kinds = SnapKinds.Vertices,
            GeometryIndex = IndexWithVertexAt(new Vec3(2, 3, 4)),
        };

        Vec3 snapped = snap.SnapWorldPoint(new Vec3(2.05f, 3.02f, 3.98f), worldRadius: 0.2f);
        Assert.Equal(new Vec3(2, 3, 4), snapped);
    }
}
