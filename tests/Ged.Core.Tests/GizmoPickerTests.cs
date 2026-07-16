using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// CPU per-handle ray-picking: a world ray unprojected from the cursor selects the
/// correct manipulator handle for the active tool (hover highlight / press-to-drag).
/// </summary>
public sealed class GizmoPickerTests
{
    // World-axis gizmo at the origin, length 4.
    private static GizmoPose Pose() => new(
        Vec3.Zero, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), 4f);

    [Fact]
    public void Move_Picks_The_Axis_Arrow_Under_The_Ray()
    {
        // Ray crossing the +X arrow around its midpoint.
        GizmoHandle h = GizmoPicker.Pick(Pose(), GizmoTool.Move,
            rayOrigin: new Vec3(2, 5, 0), rayDir: new Vec3(0, -1, 0), worldTol: 0.3f);
        Assert.Equal(GizmoHandle.MoveX, h);
    }

    [Fact]
    public void Move_Picks_The_Plane_Quad_Near_The_Corner()
    {
        // Ray hitting the XY plane inside the plane-quad band (offsets ~0.3·len on X and Y).
        GizmoHandle h = GizmoPicker.Pick(Pose(), GizmoTool.Move,
            rayOrigin: new Vec3(1.2f, 1.2f, 5), rayDir: new Vec3(0, 0, -1), worldTol: 0.3f);
        Assert.Equal(GizmoHandle.PlaneXY, h);
    }

    [Fact]
    public void Rotate_Picks_The_Ring_On_Its_Circle()
    {
        // Ray down −Z hitting the Z-normal ring (radius 4) at (4,0,0).
        GizmoHandle h = GizmoPicker.Pick(Pose(), GizmoTool.Rotate,
            rayOrigin: new Vec3(4, 0, 6), rayDir: new Vec3(0, 0, -1), worldTol: 0.4f);
        Assert.Equal(GizmoHandle.RotateZ, h);
    }

    [Fact]
    public void Scale_Picks_The_Axis_Box_At_The_Tip()
    {
        GizmoHandle h = GizmoPicker.Pick(Pose(), GizmoTool.Scale,
            rayOrigin: new Vec3(0, 5, 4), rayDir: new Vec3(0, -1, 0), worldTol: 0.4f);
        Assert.Equal(GizmoHandle.ScaleZ, h);
    }

    [Fact]
    public void Scale_Picks_The_Uniform_Centre()
    {
        // A diagonal ray onto the pivot (an axis-aligned ray would strike an axis box first).
        GizmoHandle h = GizmoPicker.Pick(Pose(), GizmoTool.Scale,
            rayOrigin: new Vec3(3, 3, 3), rayDir: new Vec3(-1, -1, -1), worldTol: 0.4f);
        Assert.Equal(GizmoHandle.ScaleUniform, h);
    }

    [Fact]
    public void Empty_Space_Picks_Nothing()
    {
        GizmoHandle h = GizmoPicker.Pick(Pose(), GizmoTool.Move,
            rayOrigin: new Vec3(50, 50, 5), rayDir: new Vec3(0, 0, -1), worldTol: 0.3f);
        Assert.Equal(GizmoHandle.None, h);
    }

    [Fact]
    public void Local_Basis_Rotates_The_Handles()
    {
        // A pose whose axes are the world axes rotated 90° about Z: local +X points along world +Y.
        var pose = new GizmoPose(Vec3.Zero, new Vec3(0, 1, 0), new Vec3(-1, 0, 0), new Vec3(0, 0, 1), 4f);
        GizmoHandle h = GizmoPicker.Pick(pose, GizmoTool.Move,
            rayOrigin: new Vec3(0, 2, 0), rayDir: new Vec3(-1, 0, 0), worldTol: 0.3f);
        Assert.Equal(GizmoHandle.MoveX, h); // the local-X arrow now lies along world +Y
    }
}
