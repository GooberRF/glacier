using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Ged.Rendering;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Regression coverage for the TAB maximize/restore toggle. Toggling the viewport grid
/// maximize state re-parents the panes: a maximized pane becomes the grid's Content
/// directly, and restoring re-composes the multi-pane layout. Re-adding a pane that was
/// not first detached from the Content threw "control already has a visual parent" —
/// the reported crash on the second TAB.
/// </summary>
public class ViewportLayoutTests
{
    private static ViewportGrid NewGrid()
    {
        var dispatcher = new CommandDispatcher(
            CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));
        return new ViewportGrid(dispatcher, CameraSchemeKind.RedClassic, RenderMode.TexturesAndLightmaps);
    }

    private static List<IViewportSurface> Surfaces(ViewportGrid grid)
    {
        var list = new List<IViewportSurface>();
        grid.ForEachSurface(list.Add);
        return list;
    }

    [AvaloniaFact]
    public void Global_Camera_Scheme_Propagates_To_All_Panes()
    {
        // The camera scheme is a single GLOBAL setting (View ▸ Camera Scheme): setting
        // it on the grid must apply to every pane, and every pane starts on the shared
        // scheme (there is no per-pane scheme dropdown any more).
        ViewportGrid grid = NewGrid();
        foreach (IViewportSurface s in Surfaces(grid))
        {
            Assert.Equal(CameraSchemeKind.RedClassic, s.SchemeKind);
        }

        grid.SetScheme(CameraSchemeKind.Orbit);
        foreach (IViewportSurface s in Surfaces(grid))
        {
            Assert.Equal(CameraSchemeKind.Orbit, s.SchemeKind);
        }

        grid.SetScheme(CameraSchemeKind.ModernFps);
        foreach (IViewportSurface s in Surfaces(grid))
        {
            Assert.Equal(CameraSchemeKind.ModernFps, s.SchemeKind);
        }
    }

    [AvaloniaFact]
    public void Default_Layout_Starts_With_The_Perspective_Pane_Maximized()
    {
        // Item 1: the first-launch default is the perspective viewport maximized. The
        // underlying layout is still the 4-pane grid (so TAB restores it) and the active
        // maximized pane is the perspective one.
        ViewportGrid grid = NewGrid();

        Assert.True(grid.IsMaximized);
        Assert.Equal(4, grid.LayoutMode);
        Assert.Equal(ViewType.Perspective, grid.ActiveSurface.ViewType);
    }

    [AvaloniaFact]
    public void Maximize_Restore_Toggle_Repeatedly_Does_Not_Throw()
    {
        ViewportGrid grid = NewGrid();
        List<IViewportSurface> original = Surfaces(grid);
        Assert.Equal(4, original.Count);

        // The default starts maximized (item 1); TAB toggles it to the 4-pane grid.
        Assert.True(grid.IsMaximized);

        for (int i = 0; i < 5; i++)
        {
            grid.ToggleMaximize();
            Assert.False(grid.IsMaximized);

            grid.ToggleMaximize();
            Assert.True(grid.IsMaximized);
        }

        // The four panes/surfaces survive every re-parent (same instances, none dropped).
        Assert.Equal(original, Surfaces(grid));
    }

    [AvaloniaFact]
    public void Reset_Layout_Restores_The_Maximized_Perspective_Default()
    {
        ViewportGrid grid = NewGrid();
        List<IViewportSurface> original = Surfaces(grid);

        // Drop out of the maximized default into the plain 4-pane grid, then reset.
        grid.ToggleMaximize();
        Assert.False(grid.IsMaximized);

        // Reset restores the first-launch default: perspective maximized over a 4-pane grid.
        grid.ResetLayout();
        Assert.True(grid.IsMaximized);
        Assert.Equal(4, grid.LayoutMode);
        Assert.Equal(ViewType.Perspective, grid.ActiveSurface.ViewType);
        Assert.Equal(original, Surfaces(grid));
    }

    [AvaloniaFact]
    public void Layout_Changes_While_Maximized_Are_Stable()
    {
        ViewportGrid grid = NewGrid();

        grid.ToggleMaximize();
        grid.SetLayout(2); // switching layout clears maximize
        Assert.False(grid.IsMaximized);
        Assert.Equal(2, grid.LayoutMode);

        grid.ToggleMaximize();
        grid.ToggleMaximize();
        Assert.Equal(4, Surfaces(grid).Count);
    }
}
