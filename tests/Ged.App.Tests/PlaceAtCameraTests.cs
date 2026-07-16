using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Ged.Rendering;
using Xunit;
using Cam = Ged.Rendering.Camera;

namespace Ged.App.Tests;

/// <summary>
/// Item 5 regression coverage: palette "place at camera" must use the active
/// PERSPECTIVE pane's live camera. The raw active pane is whatever pane the pointer
/// last crossed on its way to the palette — usually an ortho pane whose camera is a
/// pan center on a fixed axis, which put placed objects somewhere unrelated to the
/// perspective view.
/// </summary>
public class PlaceAtCameraTests
{
    private static ViewportGrid NewGrid()
    {
        var dispatcher = new CommandDispatcher(
            CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));
        return new ViewportGrid(dispatcher, CameraSchemeKind.RedClassic, RenderMode.TexturesAndLightmaps);
    }

    [Fact]
    public void PlaceAtCameraPoint_Is_Camera_Position_Plus_Forward_Times_Four()
    {
        var cam = new Cam
        {
            Projection = CameraProjection.Perspective,
            Position = new Vector3(12f, 3f, -7f),
            Yaw = 0.6f,
            Pitch = -0.2f,
        };

        Vector3 p = ViewportGrid.PlaceAtCameraPoint(cam);
        Vector3 expected = cam.Position + (cam.Forward * 4f);
        Assert.True(Vector3.Distance(p, expected) < 1e-4f, $"expected {expected}, got {p}");
    }

    [AvaloniaFact]
    public void CameraSurface_Is_The_Active_Pane_When_It_Is_Perspective()
    {
        ViewportGrid grid = NewGrid();
        ((IViewportInput)grid.Panes[1].Surface).OnPointerActivate(); // pane 1 = Perspective

        Assert.Same(grid.Panes[1].Surface, grid.ActiveSurface);
        Assert.Same(grid.Panes[1].Surface, grid.CameraSurface);
    }

    [AvaloniaFact]
    public void CameraSurface_Skips_An_Ortho_Active_Pane()
    {
        ViewportGrid grid = NewGrid();

        // The pointer crosses the Top pane on its way to the palette: pane 0 becomes
        // active, but placement must still use the perspective pane's camera.
        ((IViewportInput)grid.Panes[0].Surface).OnPointerActivate();

        Assert.Same(grid.Panes[0].Surface, grid.ActiveSurface);
        Assert.Same(grid.Panes[1].Surface, grid.CameraSurface);
        Assert.Equal(ViewType.Perspective, grid.CameraSurface.ViewType);
    }

    [AvaloniaFact]
    public void CameraSurface_Remembers_The_Last_Active_Perspective_Pane()
    {
        ViewportGrid grid = NewGrid();

        // Make a second pane perspective, activate it, then wander onto an ortho pane.
        grid.Panes[3].Surface.SetViewType(ViewType.Perspective);
        ((IViewportInput)grid.Panes[3].Surface).OnPointerActivate();
        ((IViewportInput)grid.Panes[0].Surface).OnPointerActivate();

        Assert.Same(grid.Panes[3].Surface, grid.CameraSurface);
    }

    [AvaloniaFact]
    public void CameraSurface_Falls_Back_To_Any_Perspective_Pane_When_Last_Changed_Type()
    {
        ViewportGrid grid = NewGrid();

        // Activate the perspective pane, then flip it to ortho: the remembered pane is
        // no longer perspective, so the fallback scan must find another one.
        ((IViewportInput)grid.Panes[1].Surface).OnPointerActivate();
        grid.Panes[3].Surface.SetViewType(ViewType.Perspective);
        grid.Panes[1].Surface.SetViewType(ViewType.Top);
        ((IViewportInput)grid.Panes[0].Surface).OnPointerActivate();

        Assert.Same(grid.Panes[3].Surface, grid.CameraSurface);
        Assert.Equal(ViewType.Perspective, grid.CameraSurface.ViewType);
    }
}
