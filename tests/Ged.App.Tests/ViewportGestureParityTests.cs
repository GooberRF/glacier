using System.Collections.Generic;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// PARITY regressions for the app-wide editing-gesture surface: the SAME simulated input
/// sequence is driven into both the Direct3D 11 pane (<see cref="ViewportSurface"/>) and the
/// composited OpenGL pane (<see cref="GlViewportSurface"/>) through their shared
/// <c>IViewportInput</c> entry point — exactly the path the D3D11 native WndProc and the GL
/// pane's Avalonia handlers each feed — and the resulting high-level editing events are
/// asserted IDENTICAL. Both surfaces drive the one shared <c>ViewportInputRouter</c>, so this
/// proves the OpenGL pane's editing behaviour is byte-for-byte the reference pane's for
/// transform drags, marquee, gizmo, draw-brush, ruler, nudges, ESC-cancels and snap-invert.
/// (Pick execution and ortho world-point math depend on a live GPU camera and are covered at
/// the router level in <see cref="ViewportGestureRouterTests"/>.)
/// </summary>
public sealed class ViewportGestureParityTests
{
    private const int VkM = 0x4D;
    private const int VkN = 0x4E;
    private const int VkR = 0x52;
    private const int VkEsc = 0x1B;
    private const int VkLeftArrow = 0x25;
    private const int VkUpArrow = 0x26;

    private static CommandDispatcher NewDispatcher()
    {
        return new CommandDispatcher(
            CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));
    }

    /// <summary>Runs <paramref name="script"/> against a fresh GL pane and a fresh D3D11 pane
    /// and returns the two recorded editing-event logs for comparison.</summary>
    private static (List<string> Gl, List<string> D3D) RunBoth(
        ViewType view, System.Action<IViewportInput, IViewportSurface> script)
    {
        List<string> Drive(IViewportSurface surface)
        {
            var log = new List<string>();
            Wire(surface, log);
            script((IViewportInput)surface, surface);
            return log;
        }

        var gl = new GlViewportSurface(NewDispatcher(), CameraSchemeKind.RedClassic, view);
        var d3d = new ViewportSurface(NewDispatcher(), CameraSchemeKind.RedClassic, view);
        return (Drive(gl), Drive(d3d));
    }

    private static void Wire(IViewportSurface s, List<string> log)
    {
        s.BrushDragStarted += () => log.Add("BrushDragStarted");
        s.BrushDragPixels += (dx, dy, axis) => log.Add($"BrushDragPixels {dx},{dy},{axis}");
        s.BrushDragEnded += () => log.Add("BrushDragEnded");
        s.GizmoDragStarted += (x, y) => log.Add($"GizmoDragStarted {x},{y}");
        s.GizmoDragMovedTo += (x, y) => log.Add($"GizmoDragMovedTo {x},{y}");
        s.GizmoDragEnded += () => log.Add("GizmoDragEnded");
        s.GizmoDragCancelled += () => log.Add("GizmoDragCancelled");
        s.GizmoHover += (x, y) => log.Add($"GizmoHover {x},{y}");
        s.MarqueeStarted += (x, y) => log.Add($"MarqueeStarted {x},{y}");
        s.MarqueeMovedTo += (x, y) => log.Add($"MarqueeMovedTo {x},{y}");
        s.MarqueeEnded += (x, y, add) => log.Add($"MarqueeEnded {x},{y},{add}");
        s.DrawHover += (x, y) => log.Add($"DrawHover {x},{y}");
        s.DrawClick += (x, y) => log.Add($"DrawClick {x},{y}");
        s.DrawCancelRequested += () => log.Add("DrawCancelRequested");
        s.RulerClick += (x, y) => log.Add($"RulerClick {x},{y}");
        s.RulerHover += (x, y) => log.Add($"RulerHover {x},{y}");
        s.RulerCancelRequested += () => log.Add("RulerCancelRequested");
        s.NudgeMove += v => log.Add($"NudgeMove {v.X},{v.Y},{v.Z}");
        s.NudgeRotate += v => log.Add($"NudgeRotate {v.X},{v.Y},{v.Z}");
    }

    [AvaloniaFact]
    public void SelectClick_Neither_Marquees_Nor_DragTransforms_On_Either_Backend()
    {
        // A press with no drag is a plain pick — it must NOT raise any marquee or brush-drag
        // event on either backend (the pick request itself is asserted in the router tests).
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Perspective, (input, s) =>
        {
            s.MarqueeEnabled = true;
            input.OnButton(ViewportButton.Left, down: true, 40, 40);
            input.OnButton(ViewportButton.Left, down: false, 40, 40);
        });

        Assert.Empty(gl);
        Assert.Empty(d3d);
    }

    [AvaloniaFact]
    public void Marquee_DragSelect_Is_Identical_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Top, (input, s) =>
        {
            s.MarqueeEnabled = true;
            input.OnButton(ViewportButton.Left, down: true, 20, 20);
            input.OnMouseMove(60, 70);   // exceeds the 3px slop → marquee begins
            input.OnMouseMove(90, 110);
            input.OnButton(ViewportButton.Left, down: false, 90, 110);
        });

        Assert.Equal(
            new[] { "MarqueeStarted 20,20", "MarqueeMovedTo 60,70", "MarqueeMovedTo 90,110", "MarqueeEnded 90,110,False" },
            gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void Transform_MoveDrag_Is_Identical_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Top, (input, s) =>
        {
            input.OnKey(VkM, down: true, extended: false); // hold M → move transform
            input.OnButton(ViewportButton.Left, down: true, 30, 30);
            input.OnMouseMove(45, 38);
            input.OnMouseMove(60, 50);
            input.OnButton(ViewportButton.Left, down: false, 60, 50);
            input.OnKey(VkM, down: false, extended: false);
        });

        Assert.Equal(
            new[] { "BrushDragStarted", "BrushDragPixels 15,8,False", "BrushDragPixels 15,12,False", "BrushDragEnded" },
            gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void Transform_AxisConstrainDrag_Sets_The_Constrain_Flag_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Front, (input, s) =>
        {
            input.OnKey(VkN, down: true, extended: false); // N → axis-constrained move
            input.OnButton(ViewportButton.Left, down: true, 10, 10);
            input.OnMouseMove(25, 10);
            input.OnButton(ViewportButton.Left, down: false, 25, 10);
        });

        Assert.Equal(
            new[] { "BrushDragStarted", "BrushDragPixels 15,0,True", "BrushDragEnded" },
            gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void Gizmo_HandleDrag_Is_Identical_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Perspective, (input, s) =>
        {
            s.GizmoActive = true;
            s.GizmoHitTestAt = (_, _, _) => true; // press lands on a handle
            input.OnButton(ViewportButton.Left, down: true, 50, 50);
            input.OnMouseMove(70, 55);
            input.OnButton(ViewportButton.Left, down: false, 70, 55);
        });

        Assert.Equal(
            new[] { "GizmoDragStarted 50,50", "GizmoDragMovedTo 70,55", "GizmoDragEnded" },
            gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void Gizmo_DragEscCancels_Identically_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Perspective, (input, s) =>
        {
            s.GizmoActive = true;
            s.GizmoHitTestAt = (_, _, _) => true;
            input.OnButton(ViewportButton.Left, down: true, 50, 50);
            input.OnMouseMove(70, 55);
            input.OnKey(VkEsc, down: true, extended: false); // revert the drag
        });

        Assert.Equal(
            new[] { "GizmoDragStarted 50,50", "GizmoDragMovedTo 70,55", "GizmoDragCancelled" },
            gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void DrawBrush_Click_Is_Identical_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Top, (input, s) =>
        {
            s.DrawToolArmed = true;
            input.OnMouseMove(33, 44);   // idle hover preview
            input.OnButton(ViewportButton.Left, down: true, 33, 44); // consumed as a place
        });

        Assert.Equal(new[] { "DrawHover 33,44", "DrawClick 33,44" }, gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void Ruler_ClickAndCancel_Is_Identical_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Top, (input, s) =>
        {
            s.RulerArmed = true;
            input.OnMouseMove(12, 12);
            input.OnButton(ViewportButton.Left, down: true, 12, 12);
            input.OnKey(VkEsc, down: true, extended: false); // cancel the measurement
        });

        Assert.Equal(new[] { "RulerHover 12,12", "RulerClick 12,12", "RulerCancelRequested" }, gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void Nudge_Move_And_Rotate_Are_Identical_On_Both_Backends()
    {
        (List<string> gl, List<string> d3d) = RunBoth(ViewType.Top, (input, s) =>
        {
            input.OnKey(VkM, down: true, extended: false);
            input.OnKey(VkLeftArrow, down: true, extended: false); // M+Left → move nudge
            input.OnKey(VkM, down: false, extended: false);
            input.OnKey(VkR, down: true, extended: false);
            input.OnKey(VkUpArrow, down: true, extended: false);  // R+Up → rotate nudge
            input.OnKey(VkR, down: false, extended: false);
        });

        // Top view: Left arrow moves -X; rotate Up is +X axis. Both backends compute this
        // from the shared router's view-relative axis table (negation yields signed zeros on
        // the untouched components — the point is the two backends agree exactly).
        Assert.Equal(new[] { "NudgeMove -1,-0,-0", "NudgeRotate 1,0,0" }, gl);
        Assert.Equal(gl, d3d);
    }

    [AvaloniaFact]
    public void SnapInvert_Alt_State_Tracks_Identically_On_Both_Backends()
    {
        const int vkAlt = 0x12;
        var gl = new GlViewportSurface(NewDispatcher(), CameraSchemeKind.RedClassic, ViewType.Top);
        var d3d = new ViewportSurface(NewDispatcher(), CameraSchemeKind.RedClassic, ViewType.Top);

        foreach (IViewportSurface s in new IViewportSurface[] { gl, d3d })
        {
            Assert.False(s.SnapInvertHeld);
            ((IViewportInput)s).OnKey(vkAlt, down: true, extended: false);
            Assert.True(s.AltHeld);
            Assert.True(s.SnapInvertHeld);
            ((IViewportInput)s).OnKey(vkAlt, down: false, extended: false);
            Assert.False(s.SnapInvertHeld);
        }
    }
}
