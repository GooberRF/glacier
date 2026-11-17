using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ged.App;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Ged.Rendering;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Multi-pane regressions for the composited OpenGL viewport (Goober's Windows
/// OpenGL 4-pane bug: only the bottom-right pane selectable, TAB behaving erratically).
/// Root cause: <see cref="GlViewportSurface"/>'s custom hit-test returned true
/// unconditionally while the point arrives in GLOBAL coordinates, so every pane claimed
/// every point and the topmost (last-rendered = bottom-right) pane won ALL pointer input.
/// These tests drive a REAL 4-pane GL <see cref="ViewportGrid"/> in a headless window:
/// hit-testing must resolve each quadrant to its own pane, hover must activate (and focus)
/// exactly the pane under the cursor, gestures must reach only that pane, TAB must toggle
/// the layout exactly once per press through the MainWindow-style tunnel with no
/// focus-traversal cycling, and the D3D11 grid must agree on the activation outcomes.
/// </summary>
public sealed class GlViewportMultiPaneTests
{
    private static CommandDispatcher NewDispatcher() =>
        new(CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));

    /// <summary>A real 4-pane GL grid (maximize off) shown in a 400x400 headless window.</summary>
    private static (Window Window, ViewportGrid Grid, CommandDispatcher Dispatcher) ShowFourPane(bool useOpenGl = true)
    {
        CommandDispatcher dispatcher = NewDispatcher();
        var grid = new ViewportGrid(
            dispatcher, CameraSchemeKind.RedClassic, RenderMode.TexturesAndLightmaps, useOpenGl: useOpenGl);
        grid.SetLayout(4); // the bug repro: TAB from the maximized default into the 4-pane grid

        var window = new Window { Width = 400, Height = 400, Content = grid };
        window.Show();
        SettleComposition(window);
        return (window, grid, dispatcher);
    }

    /// <summary>
    /// Pumps the headless compositor until everything laid out so far has reached the
    /// composition server. Avalonia hit-tests (<c>InputHitTest</c> and the pointer-over
    /// refresh after a layout change) read the visual transforms the server last rendered,
    /// NOT the live layout. Headlessly the server only renders on the 60 Hz render timer,
    /// which <c>Dispatcher.UIThread.RunJobs()</c> fires only when wall-clock-due, and a new
    /// batch also waits behind any still-unprocessed batch left by an earlier test (the
    /// MediaContext commit throttle). Without this pump a freshly shown or re-laid-out grid
    /// hit-tests against stale or missing transforms whenever the timer is not yet due, which
    /// made these tests pass or fail on timing alone.
    /// </summary>
    private static void SettleComposition(TopLevel topLevel)
    {
        Compositor compositor = ElementComposition.GetElementVisual(topLevel)!.Compositor;
        Dispatcher.UIThread.RunJobs();
        Task applied = compositor.RequestCommitAsync();
        for (int tick = 0; !applied.IsCompleted; tick++)
        {
            Assert.True(tick < 16, "the headless compositor never applied the pending layout batch");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        // Run the pointer-over refresh the processed batch posted (at Input priority).
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Window-space points inside each pane's GL surface (quadrant centres are
    /// computed from the LIVE arranged bounds, so splitter/toolbar layout can never skew them).</summary>
    private static Point SurfaceCenter(Window window, ViewportPane pane)
    {
        Control surface = pane.Surface.AsControl();
        Point topLeft = surface.TranslatePoint(default, window)!.Value;
        return new Point(topLeft.X + (surface.Bounds.Width / 2), topLeft.Y + (surface.Bounds.Height / 2));
    }

    [AvaloniaFact]
    public void HitTest_Resolves_Each_Quadrant_To_Its_Own_Pane()
    {
        // The root-cause regression: with the unconditional custom hit-test, ALL four of
        // these resolved to panes[3] (bottom-right) and no other pane could ever be hovered.
        (Window window, ViewportGrid grid, _) = ShowFourPane();

        for (int i = 0; i < 4; i++)
        {
            Point p = SurfaceCenter(window, grid.Panes[i]);
            IInputElement? hit = window.InputHitTest(p);
            Assert.Same(grid.Panes[i].Surface, hit);
        }
    }

    [AvaloniaFact]
    public void Hover_Activates_The_Pane_Under_The_Cursor_In_Every_Quadrant()
    {
        (Window window, ViewportGrid grid, _) = ShowFourPane();

        // Visit the quadrants in a deliberately shuffled order (ending away from
        // bottom-right) so a pane that eats all input cannot fake a pass.
        foreach (int i in new[] { 3, 0, 2, 1 })
        {
            window.MouseMove(SurfaceCenter(window, grid.Panes[i]));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(grid.Panes[i].Surface, grid.ActiveSurface);
            Assert.True(grid.Panes[i].Surface.IsPointerInside, $"pane {i} should be pointer-inside");
        }
    }

    [AvaloniaFact]
    public void Hover_Also_Moves_Keyboard_Focus_So_Camera_Keys_Follow_The_Cursor()
    {
        (Window window, ViewportGrid grid, _) = ShowFourPane();

        foreach (int i in new[] { 1, 3, 0, 2 })
        {
            window.MouseMove(SurfaceCenter(window, grid.Panes[i]));
            Dispatcher.UIThread.RunJobs();
            object? focused = TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement();
            Assert.Same(grid.Panes[i].Surface, focused);
        }
    }

    [AvaloniaFact]
    public void Gestures_Reach_Only_The_Hovered_Pane_Never_The_Others()
    {
        (Window window, ViewportGrid grid, _) = ShowFourPane();
        var started = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            int captured = i;
            grid.Panes[i].Surface.BrushDragStarted += () => started.Add(captured);
        }

        // Hover top-left (pane 0), hold M (a transform key routed through the pane's own
        // key handler via hover-focus) and drag: the M+LMB brush move must begin on pane 0
        // only. Before the hit-test fix this landed on pane 3 regardless of the cursor.
        Point p = SurfaceCenter(window, grid.Panes[0]);
        window.MouseMove(p);
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.M, RawInputModifiers.None, PhysicalKey.M, "m");
        window.MouseDown(p, MouseButton.Left);
        window.MouseMove(new Point(p.X + 15, p.Y + 10));
        window.MouseUp(new Point(p.X + 15, p.Y + 10), MouseButton.Left);
        window.KeyRelease(Key.M, RawInputModifiers.None, PhysicalKey.M, "m");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { 0 }, started);
    }

    [AvaloniaFact]
    public void Gl_Surfaces_Are_Not_Tab_Stops()
    {
        // Native D3D11 panes are child HWNDs Avalonia focus-traversal cannot visit; the
        // composited panes must match, or a TAB that escapes the tunnel cycles keyboard
        // focus across the four panes (the endless cycling in the bug report).
        (_, ViewportGrid grid, _) = ShowFourPane();
        grid.ForEachSurface(s => Assert.False(((InputElement)s.AsControl()).IsTabStop));
    }

    [AvaloniaFact]
    public void Tab_Toggles_The_Layout_Exactly_Once_Per_Press_With_No_Focus_Cycling()
    {
        (Window window, ViewportGrid grid, CommandDispatcher dispatcher) = ShowFourPane();

        // The MainWindow tunnel wiring, replicated 1:1: bind ViewMaximize to the grid's
        // toggle and route TAB to it whenever the pointer is over a pane or focus is in one.
        int toggles = 0;
        dispatcher.Bind(CommandIds.ViewMaximize, () => { grid.ToggleMaximize(); toggles++; });
        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Tab
                    && GestureConvert.FromAvalonia(e.Key, e.KeyModifiers) is { } gesture
                    && grid.TabTargetsViewport(TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement())
                    && dispatcher.Dispatch(gesture))
                {
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel);

        // Hover the TOP-LEFT pane so it is active and focused (the bug's repro posture).
        window.MouseMove(SurfaceCenter(window, grid.Panes[0]));
        SettleComposition(window);
        Assert.Same(grid.Panes[0].Surface, grid.ActiveSurface);
        Assert.Same(grid.Panes[0].Surface, FocusedElement(grid));

        // TAB #1: 4-pane -> maximized, exactly one toggle, and the maximized pane is the
        // hovered one (top-left), not the bottom-right. The re-parent must not drop keyboard
        // focus: the pointer never left the pane, so no hover re-focus would ever restore it
        // and the pane's camera/transform keys would go dead.
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        SettleComposition(window);
        Assert.Equal(1, toggles);
        Assert.True(grid.IsMaximized);
        Assert.Same(grid.Panes[0].Surface, grid.ActiveSurface);
        Assert.Same(grid.Panes[0].Surface, FocusedElement(grid));

        // TAB #2: back to the 4-pane grid — still one toggle per press, and keyboard focus
        // stays on the same pane (never traversed to a DIFFERENT pane's surface: no cycling).
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        SettleComposition(window);
        Assert.Equal(2, toggles);
        Assert.False(grid.IsMaximized);
        Assert.Same(grid.Panes[0].Surface, FocusedElement(grid));
    }

    private static object? FocusedElement(Visual visual) =>
        TopLevel.GetTopLevel(visual)?.FocusManager?.GetFocusedElement();

    [AvaloniaFact]
    public void Hover_Activation_Outcomes_Match_The_D3D11_Grid()
    {
        // Backend parity for the activation contract itself: drive the same hover-activation
        // sequence through a D3D11 grid (via the IViewportInput seam its WndProc feeds — no
        // native window exists headlessly) and the GL grid (via REAL pointer events), and
        // assert both end with the same pane active + pointer-inside.
        (Window window, ViewportGrid glGrid, _) = ShowFourPane();
        var d3dGrid = new ViewportGrid(
            NewDispatcher(), CameraSchemeKind.RedClassic, RenderMode.TexturesAndLightmaps, useOpenGl: false);
        d3dGrid.SetLayout(4);

        foreach (int i in new[] { 2, 1, 3, 0 })
        {
            window.MouseMove(SurfaceCenter(window, glGrid.Panes[i]));
            Dispatcher.UIThread.RunJobs();
            ((IViewportInput)d3dGrid.Panes[i].Surface).OnPointerActivate();

            Assert.Same(glGrid.Panes[i].Surface, glGrid.ActiveSurface);
            Assert.Same(d3dGrid.Panes[i].Surface, d3dGrid.ActiveSurface);
            Assert.Equal(
                d3dGrid.Panes[i].Surface.IsPointerInside,
                glGrid.Panes[i].Surface.IsPointerInside);
        }
    }
}
