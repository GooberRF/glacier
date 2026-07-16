using System.Numerics;
using Ged.Rendering;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Item 2 regression coverage for the orthographic camera operations the schemes
/// build on: view-plane pan, OrthoZoom scaling, cursor-centred zoom, and the
/// MoveLocal up-axis fix (world Y is the invisible depth axis of a Top pane).
/// </summary>
public sealed class OrthoCameraOpsTests
{
    private static Camera TopCam() => new()
    {
        Projection = CameraProjection.Orthographic,
        Ortho = OrthoView.Top,
        Position = Vector3.Zero,
        OrthoZoom = 10f,
        AspectRatio = 1f,
    };

    [Fact]
    public void Pan_Moves_In_The_View_Plane_Of_A_Top_Pane()
    {
        Camera cam = TopCam();
        cam.Pan(2f, 3f);

        // Top view: Right = +X, view-plane Up = +Z; world Y (the depth axis) must not move.
        Assert.Equal(2f, cam.Position.X, 3);
        Assert.Equal(0f, cam.Position.Y, 3);
        Assert.Equal(3f, cam.Position.Z, 3);
    }

    [Fact]
    public void MoveLocal_Up_Follows_The_View_Plane_In_Ortho()
    {
        Camera cam = TopCam();
        cam.MoveLocal(0f, 5f, 0f);

        // The old behavior moved world +Y — invisible in a Top pane. Up must scroll the plane.
        Assert.Equal(0f, cam.Position.Y, 3);
        Assert.Equal(5f, cam.Position.Z, 3);
    }

    [Fact]
    public void MoveLocal_Up_Stays_World_Y_In_Perspective()
    {
        var cam = new Camera { Projection = CameraProjection.Perspective, Position = Vector3.Zero, Yaw = 0.7f, Pitch = 0.3f };
        cam.MoveLocal(0f, 5f, 0f);
        Assert.Equal(5f, cam.Position.Y, 3);
    }

    [Fact]
    public void ZoomOrtho_Scales_And_Clamps()
    {
        Camera cam = TopCam();
        cam.ZoomOrtho(0.5f);
        Assert.Equal(5f, cam.OrthoZoom, 3);

        cam.ZoomOrtho(1e-9f);
        Assert.Equal(0.5f, cam.OrthoZoom, 3); // floor

        cam.ZoomOrtho(1e9f);
        Assert.Equal(20000f, cam.OrthoZoom, 1); // ceiling
    }

    [Fact]
    public void ZoomOrthoAt_Keeps_The_World_Point_Under_The_Cursor()
    {
        Camera cam = TopCam();
        cam.Position = new Vector3(10f, 0f, 5f);
        cam.AspectRatio = 800f / 600f;

        const float px = 600f, py = 150f;
        Vector3 anchorBefore = cam.PixelRay(px, py, 800f, 600f).Origin;

        cam.ZoomOrthoAt(px, py, 800f, 600f, 0.85f);
        Assert.Equal(8.5f, cam.OrthoZoom, 3);

        Vector3 anchorAfter = cam.PixelRay(px, py, 800f, 600f).Origin;
        Assert.True(Vector3.Distance(anchorBefore, anchorAfter) < 1e-3f,
            $"cursor anchor drifted from {anchorBefore} to {anchorAfter}");
    }

    [Fact]
    public void ZoomOrthoAt_Center_Pixel_Keeps_Position()
    {
        Camera cam = TopCam();
        cam.Position = new Vector3(3f, 0f, -2f);
        cam.ZoomOrthoAt(400f, 300f, 800f, 600f, 0.85f);

        // Zooming at the view centre must not pan.
        Assert.Equal(3f, cam.Position.X, 3);
        Assert.Equal(-2f, cam.Position.Z, 3);
    }

    [Fact]
    public void ZoomOrthoAt_Is_A_NoOp_For_Perspective()
    {
        var cam = new Camera { Projection = CameraProjection.Perspective, Position = Vector3.Zero, OrthoZoom = 10f };
        cam.ZoomOrthoAt(100f, 100f, 800f, 600f, 0.5f);
        Assert.Equal(10f, cam.OrthoZoom, 3);
        Assert.Equal(Vector3.Zero, cam.Position);
    }
}
