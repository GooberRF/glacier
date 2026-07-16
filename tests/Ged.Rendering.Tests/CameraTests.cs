using System.Numerics;
using Ged.Rendering;
using Xunit;

namespace Ged.Rendering.Tests;

public sealed class CameraTests
{
    private static Vector4 Project(Camera cam, Vector3 world) =>
        Vector4.Transform(new Vector4(world, 1f), cam.ViewProjectionMatrix);

    [Fact]
    public void PerspectiveCentersPointDirectlyAhead()
    {
        var cam = new Camera
        {
            Projection = CameraProjection.Perspective,
            Position = Vector3.Zero,
            Yaw = 0f,
            Pitch = 0f,
            AspectRatio = 1f,
        };

        Vector4 clip = Project(cam, new Vector3(0f, 0f, 5f));
        Assert.True(clip.W > 0f, "A point ahead of the camera must have positive w.");

        // NDC of a dead-ahead point is the screen center.
        Assert.True(MathF.Abs(clip.X / clip.W) < 1e-3f);
        Assert.True(MathF.Abs(clip.Y / clip.W) < 1e-3f);

        float ndcZ = clip.Z / clip.W;
        Assert.InRange(ndcZ, 0f, 1f);
    }

    [Fact]
    public void PointBehindCameraHasNonPositiveW()
    {
        var cam = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f, AspectRatio = 1f };
        Vector4 clip = Project(cam, new Vector3(0f, 0f, -5f));
        Assert.True(clip.W <= 0f, "A point behind the camera must be clipped (w <= 0).");
    }

    [Fact]
    public void RightwardPointProjectsToPositiveX()
    {
        var cam = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f, AspectRatio = 1f };
        Vector4 clip = Project(cam, new Vector3(3f, 0f, 5f));
        Assert.True(clip.X / clip.W > 0f, "A point to the right should map to +X in NDC.");
    }

    [Fact]
    public void OrthographicKeepsParallelScaleIndependentOfDepth()
    {
        var cam = new Camera
        {
            Projection = CameraProjection.Orthographic,
            Ortho = OrthoView.Top,
            Position = Vector3.Zero,
            OrthoZoom = 10f,
            AspectRatio = 1f,
        };

        // Two points at the same XZ but different heights map to the same NDC x/y
        // under a top-down orthographic view (no perspective foreshortening).
        Vector4 a = Project(cam, new Vector3(4f, 0f, 2f));
        Vector4 b = Project(cam, new Vector3(4f, 6f, 2f));
        Assert.Equal(a.X / a.W, b.X / b.W, 3);
        Assert.Equal(a.Y / a.W, b.Y / b.W, 3);
    }

    [Fact]
    public void PixelRay_CenterPixel_Points_Along_Forward()
    {
        var cam = new Camera { Projection = CameraProjection.Perspective, Position = Vector3.Zero, Yaw = 0f, Pitch = 0f, AspectRatio = 1f };
        (Vector3 origin, Vector3 dir) = cam.PixelRay(400, 300, 800, 600);
        Assert.True(Vector3.Distance(origin, Vector3.Zero) < 1e-4f);
        Assert.True(Vector3.Distance(dir, new Vector3(0, 0, 1)) < 1e-3f);
    }

    [Fact]
    public void WorldToScreen_Maps_A_Forward_Point_To_The_Center()
    {
        var cam = new Camera { Projection = CameraProjection.Perspective, Position = Vector3.Zero, Yaw = 0f, Pitch = 0f, AspectRatio = 1f };
        Assert.True(cam.WorldToScreen(new Vector3(0, 0, 5), 800, 600, out Vector2 s));
        Assert.Equal(400f, s.X, 1);
        Assert.Equal(300f, s.Y, 1);
    }

    [Fact]
    public void PixelRay_And_WorldToScreen_RoundTrip_Perspective()
    {
        var cam = new Camera { Projection = CameraProjection.Perspective, Position = new Vector3(1, 2, -3), Yaw = 0.3f, Pitch = -0.1f, AspectRatio = 16f / 9f };
        (Vector3 origin, Vector3 dir) = cam.PixelRay(500, 200, 800, 600);
        Vector3 pt = origin + (dir * 12f);
        Assert.True(cam.WorldToScreen(pt, 800, 600, out Vector2 s));
        Assert.Equal(500f, s.X, 0);
        Assert.Equal(200f, s.Y, 0);
    }

    [Fact]
    public void PixelRay_And_WorldToScreen_RoundTrip_Ortho()
    {
        var cam = new Camera { Projection = CameraProjection.Orthographic, Ortho = OrthoView.Top, Position = Vector3.Zero, OrthoZoom = 10f, AspectRatio = 1f };
        (Vector3 origin, Vector3 dir) = cam.PixelRay(600, 150, 800, 600);
        Vector3 pt = origin + (dir * 5f);
        Assert.True(cam.WorldToScreen(pt, 800, 600, out Vector2 s));
        Assert.Equal(600f, s.X, 1);
        Assert.Equal(150f, s.Y, 1);
    }

    [Fact]
    public void MoveLocalMovesAlongForward()
    {
        var cam = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        cam.MoveLocal(0f, 0f, 2f);
        Assert.True(cam.Position.Z > 1.9f, "Forward movement at yaw 0 should advance +Z.");
    }
}
