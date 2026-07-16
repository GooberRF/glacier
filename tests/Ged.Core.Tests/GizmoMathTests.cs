using System;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Hand-computed expectations for the transform-gizmo manipulation math — the
/// world-space ray/axis/plane/ring/scale computations that replace the old
/// pointer-delta heuristics.
/// </summary>
public sealed class GizmoMathTests
{
    private const float Eps = 1e-4f;

    // ---- Axis translate: closest point between the ray and the axis line ------

    [Fact]
    public void ClosestAxisParam_Projects_Ray_Onto_Axis()
    {
        // X axis through origin; a vertical ray at x=5 → closest axis point is (5,0,0).
        float s = GizmoMath.ClosestAxisParam(
            axisPoint: Vec3.Zero, axisDir: new Vec3(1, 0, 0),
            rayOrigin: new Vec3(5, 10, 0), rayDir: new Vec3(0, -1, 0));
        Assert.Equal(5f, s, 3);
    }

    [Fact]
    public void ClosestAxisParam_Handles_Offset_Axis_Point()
    {
        // X axis through (1,0,0); ray at x=4 → param measured from the axis point = 3.
        float s = GizmoMath.ClosestAxisParam(
            axisPoint: new Vec3(1, 0, 0), axisDir: new Vec3(1, 0, 0),
            rayOrigin: new Vec3(4, 7, 2), rayDir: new Vec3(0, -1, 0));
        Assert.Equal(3f, s, 3);
    }

    [Fact]
    public void ClosestAxisParam_Parallel_Ray_Projects_Origin()
    {
        // Ray parallel to the axis: fall back to projecting the ray origin (x = 8).
        float s = GizmoMath.ClosestAxisParam(
            axisPoint: Vec3.Zero, axisDir: new Vec3(1, 0, 0),
            rayOrigin: new Vec3(8, 3, 0), rayDir: new Vec3(1, 0, 0));
        Assert.Equal(8f, s, 3);
    }

    // ---- Plane translate: ray-plane intersection ------------------------------

    [Fact]
    public void RayPlane_Intersects_Z_Plane()
    {
        bool ok = GizmoMath.RayPlane(
            planePoint: Vec3.Zero, planeNormal: new Vec3(0, 0, 1),
            rayOrigin: new Vec3(2, 3, 5), rayDir: new Vec3(0, 0, -1), out Vec3 hit);
        Assert.True(ok);
        Assert.True(hit.ApproxEquals(new Vec3(2, 3, 0), Eps));
    }

    [Fact]
    public void RayPlane_Parallel_Ray_Misses()
    {
        bool ok = GizmoMath.RayPlane(
            planePoint: Vec3.Zero, planeNormal: new Vec3(0, 0, 1),
            rayOrigin: new Vec3(2, 3, 5), rayDir: new Vec3(1, 0, 0), out _);
        Assert.False(ok);
    }

    // ---- Rotate ring: pick direction + swept angle ----------------------------

    [Fact]
    public void RingPickDir_Projects_Into_Ring_Plane()
    {
        bool ok = GizmoMath.RingPickDir(
            pivot: Vec3.Zero, axis: new Vec3(0, 0, 1),
            rayOrigin: new Vec3(3, 0, 5), rayDir: new Vec3(0, 0, -1), out Vec3 dir);
        Assert.True(ok);
        Assert.True(dir.ApproxEquals(new Vec3(1, 0, 0), Eps));
    }

    [Fact]
    public void SignedAngle_Is_Signed_About_Axis()
    {
        // +X to +Y about +Z is +90°; +X to −Y about +Z is −90°.
        float a = GizmoMath.SignedAngle(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));
        Assert.Equal(MathF.PI / 2f, a, 4);

        float b = GizmoMath.SignedAngle(new Vec3(1, 0, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1));
        Assert.Equal(-MathF.PI / 2f, b, 4);
    }

    [Fact]
    public void SignedAngle_Accumulates_Past_360_Continuously()
    {
        // Walk a full turn in 30° steps; the accumulated total is a clean 2π.
        Vec3 axis = new(0, 0, 1);
        float total = 0f;
        Vec3 prev = new(1, 0, 0);
        for (int i = 1; i <= 12; i++)
        {
            float ang = i * MathF.PI / 6f;
            Vec3 cur = new(MathF.Cos(ang), MathF.Sin(ang), 0);
            total += GizmoMath.SignedAngle(prev, cur, axis);
            prev = cur;
        }

        Assert.Equal(2f * MathF.PI, total, 3);
    }

    // ---- Scale ----------------------------------------------------------------

    [Fact]
    public void AxisScaleFactor_Is_Param_Ratio_And_Guards_Zero()
    {
        Assert.Equal(2f, GizmoMath.AxisScaleFactor(2f, 4f), 4);
        Assert.Equal(0.5f, GizmoMath.AxisScaleFactor(4f, 2f), 4);
        Assert.Equal(1f, GizmoMath.AxisScaleFactor(0f, 5f), 4); // guarded
    }

    [Fact]
    public void RadialScaleFactor_Is_Radius_Ratio_And_Guards_Zero()
    {
        Assert.Equal(2.5f, GizmoMath.RadialScaleFactor(10f, 25f), 4);
        Assert.Equal(1f, GizmoMath.RadialScaleFactor(0f, 5f), 4); // guarded
    }

    // ---- Handle classification ------------------------------------------------

    [Theory]
    [InlineData(GizmoHandle.MoveX, GizmoTool.Move, 0)]
    [InlineData(GizmoHandle.MoveZ, GizmoTool.Move, 2)]
    [InlineData(GizmoHandle.RotateY, GizmoTool.Rotate, 1)]
    [InlineData(GizmoHandle.ScaleZ, GizmoTool.Scale, 2)]
    [InlineData(GizmoHandle.ScaleUniform, GizmoTool.Scale, -1)]
    public void Handle_Classification(GizmoHandle handle, GizmoTool tool, int axis)
    {
        Assert.Equal(tool, GizmoMath.ToolOf(handle));
        Assert.Equal(axis, GizmoMath.AxisOf(handle));
    }

    [Theory]
    [InlineData(GizmoHandle.PlaneYZ, 0)]
    [InlineData(GizmoHandle.PlaneZX, 1)]
    [InlineData(GizmoHandle.PlaneXY, 2)]
    [InlineData(GizmoHandle.MoveX, -1)]
    public void Plane_Normal_Axis(GizmoHandle handle, int normalAxis)
    {
        Assert.Equal(normalAxis, GizmoMath.PlaneNormalAxis(handle));
    }
}
