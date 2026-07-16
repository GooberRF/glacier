using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Ged.Rendering;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Regression coverage for TAB routing (item 1): TAB must be the viewport
/// maximize/restore toggle when a viewport pane has focus OR the pointer is over any
/// viewport pane, and normal focus traversal otherwise. The pointer-over state is the
/// same one that drives the active-pane red border (native WM_MOUSEMOVE enter /
/// WM_MOUSELEAVE clear), simulated here through the IViewportInput plumbing.
/// </summary>
public class TabRoutingTests
{
    private static ViewportGrid NewGrid()
    {
        var dispatcher = new CommandDispatcher(
            CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));
        return new ViewportGrid(dispatcher, CameraSchemeKind.RedClassic, RenderMode.TexturesAndLightmaps);
    }

    [AvaloniaFact]
    public void Tab_Is_Focus_Traversal_When_Pointer_And_Focus_Are_Elsewhere()
    {
        ViewportGrid grid = NewGrid();

        Assert.False(grid.IsPointerOverViewport);
        Assert.False(grid.TabTargetsViewport(null));

        // Focus in a text box that is not part of any viewport pane.
        var stray = new TextBox();
        Assert.False(grid.TabTargetsViewport(stray));
    }

    [AvaloniaFact]
    public void Tab_Is_Maximize_Toggle_While_Pointer_Is_Over_A_Pane()
    {
        ViewportGrid grid = NewGrid();
        var input = (IViewportInput)grid.Panes[2].Surface;

        // Native WM_MOUSEMOVE enter (the red-border activation path).
        input.OnPointerActivate();
        Assert.True(grid.Panes[2].Surface.IsPointerInside);
        Assert.True(grid.IsPointerOverViewport);
        Assert.True(grid.TabTargetsViewport(null));

        // Pointer over a pane wins even when focus sits in a text box elsewhere.
        Assert.True(grid.TabTargetsViewport(new TextBox()));

        // The pane the pointer entered became the active pane, so the toggle
        // targets it (ToggleMaximize maximizes the active pane).
        Assert.Same(grid.Panes[2].Surface, grid.ActiveSurface);

        // Native WM_MOUSELEAVE reverts TAB to focus traversal.
        input.OnPointerLeave();
        Assert.False(grid.Panes[2].Surface.IsPointerInside);
        Assert.False(grid.IsPointerOverViewport);
        Assert.False(grid.TabTargetsViewport(null));
    }

    [AvaloniaFact]
    public void Tab_Is_Maximize_Toggle_When_Focus_Is_Inside_A_Pane()
    {
        ViewportGrid grid = NewGrid();

        // Focus on any element inside a viewport pane (the pane itself, its toolbar,
        // or the surface host) routes TAB to the maximize toggle.
        Assert.True(grid.TabTargetsViewport(grid.Panes[0]));
        Assert.True(grid.TabTargetsViewport(grid.Panes[1].Surface));
    }

    [AvaloniaFact]
    public void Pointer_Leave_On_One_Pane_Does_Not_Clear_Another()
    {
        ViewportGrid grid = NewGrid();
        var a = (IViewportInput)grid.Panes[0].Surface;
        var b = (IViewportInput)grid.Panes[1].Surface;

        a.OnPointerActivate();
        b.OnPointerActivate(); // crossed into pane B before A's leave arrives
        a.OnPointerLeave();

        Assert.True(grid.IsPointerOverViewport);
        Assert.True(grid.TabTargetsViewport(null));

        b.OnPointerLeave();
        Assert.False(grid.IsPointerOverViewport);
    }
}
