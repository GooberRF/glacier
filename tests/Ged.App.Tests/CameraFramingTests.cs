using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Ged.Core.Model;
using Ged.Rendering;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// REAL-PATH regression for "Layers double-click / Jump-To doesn't move the camera": the
/// framing call must move the actual perspective pane and PERSIST, even when that pane has
/// no live native swapchain (detached / headless). Before the fix, <c>ViewportSurface.Frame</c>
/// early-returned when the GPU camera was absent, so the persisted pose was never updated and
/// framing was silently lost.
/// </summary>
public class CameraFramingTests
{
    private static ViewportGrid NewGrid()
    {
        var dispatcher = new CommandDispatcher(
            CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));
        return new ViewportGrid(dispatcher, CameraSchemeKind.RedClassic, RenderMode.TexturesAndLightmaps);
    }

    [AvaloniaFact]
    public void Frame_Moves_The_Perspective_Camera_Pose_Even_Without_A_Live_Surface()
    {
        ViewportGrid grid = NewGrid();
        ((IViewportInput)grid.Panes[1].Surface).OnPointerActivate(); // perspective pane active
        IViewportSurface cam = grid.CameraSurface;

        Vector3 before = cam.CameraPosition;
        var far = new Aabb(new Vec3(100, 100, 100), new Vec3(110, 110, 110));
        cam.Frame(far);
        Vector3 after = cam.CameraPosition;

        Assert.True(Vector3.Distance(before, after) > 50f,
            $"framing a distant brush must move the perspective camera (before {before}, after {after})");
        // The framed eye should sit out near the box it is looking at, not at the origin.
        Assert.True(after.Length() > 50f, $"camera should be near the framed box, got {after}");
    }

    [AvaloniaFact]
    public void FramePoint_Jump_To_Also_Moves_The_Persisted_Pose()
    {
        ViewportGrid grid = NewGrid();
        ((IViewportInput)grid.Panes[1].Surface).OnPointerActivate();
        IViewportSurface cam = grid.CameraSurface;

        Vector3 before = cam.CameraPosition;
        cam.FramePoint(new Vector3(-80, 40, 200));
        Assert.True(Vector3.Distance(before, cam.CameraPosition) > 50f);
    }

    [AvaloniaFact]
    public void ViewFrom_Persists_The_Position_Without_A_Live_Surface()
    {
        ViewportGrid grid = NewGrid();
        ((IViewportInput)grid.Panes[1].Surface).OnPointerActivate();
        IViewportSurface cam = grid.CameraSurface;

        var target = new Vector3(15, 6, -22);
        cam.ViewFrom(target);
        Assert.True(Vector3.Distance(cam.CameraPosition, target) < 1e-3f);
    }

    [AvaloniaFact]
    public void ViewFrom_Oriented_Sets_The_Perspective_Pose_To_The_Object_Pose()
    {
        ViewportGrid grid = NewGrid();
        ((IViewportInput)grid.Panes[1].Surface).OnPointerActivate();
        IViewportSurface cam = grid.CameraSurface;

        // An object at a known position facing a non-axis-aligned direction — its rotation
        // matrix's Forward row is what "View From" derives the camera yaw/pitch from.
        var pos = new Vector3(12f, 5f, -30f);
        Vec3 fwd = new Vec3(1f, 0.5f, -2f).Normalized();
        var rot = new Mat3(fwd, new Vec3(1, 0, 0), new Vec3(0, 1, 0)); // only Forward is used

        cam.ViewFrom(pos, new Vector3(rot.Forward.X, rot.Forward.Y, rot.Forward.Z));

        // Camera pose == object position + forward-derived orientation. CameraForward is
        // computed FROM the persisted yaw/pitch, so matching it to the object forward proves
        // yaw/pitch were derived from that forward (roll is ignored).
        Assert.True(Vector3.Distance(cam.CameraPosition, pos) < 1e-3f,
            $"camera should sit at the object position (got {cam.CameraPosition})");
        var expected = Vector3.Normalize(new Vector3(fwd.X, fwd.Y, fwd.Z));
        Assert.True(Vector3.Distance(cam.CameraForward, expected) < 1e-3f,
            $"camera forward {cam.CameraForward} should match the object forward {expected}");
    }
}
