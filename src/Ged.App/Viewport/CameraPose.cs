using System;
using System.Numerics;
using Ged.Rendering;

namespace Ged.App.Viewport;

/// <summary>
/// A snapshot of a camera's pose. Persisted on the pane so a view survives native
/// swapchain re-creation (layout changes) and used per-frame to detect motion for
/// idle frame-skipping.
/// </summary>
internal record struct CameraPose(
    Vector3 Position,
    float Yaw,
    float Pitch,
    CameraProjection Projection,
    OrthoView Ortho,
    float OrthoZoom,
    float Roll = 0f)
{
    public static CameraPose Default => new(new Vector3(0f, 2f, -5f), 0f, 0f, CameraProjection.Perspective, OrthoView.Top, 20f);

    public static CameraPose Capture(Rendering.Camera cam) => new(
        cam.Position, cam.Yaw, cam.Pitch, cam.Projection, cam.Ortho, cam.OrthoZoom, cam.Roll);

    public void CaptureFrom(Rendering.Camera cam)
    {
        Position = cam.Position;
        Yaw = cam.Yaw;
        Pitch = cam.Pitch;
        Projection = cam.Projection;
        Ortho = cam.Ortho;
        OrthoZoom = cam.OrthoZoom;
        Roll = cam.Roll;
    }

    public readonly void ApplyTo(Rendering.Camera cam)
    {
        cam.Position = Position;
        cam.Yaw = Yaw;
        cam.Pitch = Pitch;
        cam.Projection = Projection;
        cam.Ortho = Ortho;
        cam.OrthoZoom = OrthoZoom;
        cam.Roll = Roll;
    }

    public void LookAt(Vector3 from, Vector3 to)
    {
        Position = from;
        Vector3 d = to - from;
        if (d.LengthSquared() < 1e-6f)
        {
            return;
        }

        d = Vector3.Normalize(d);
        Pitch = MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
        Yaw = MathF.Atan2(d.X, d.Z);
    }
}
