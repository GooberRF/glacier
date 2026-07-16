using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// End-to-end Avalonia input simulation for the composited OpenGL pane: real pointer
/// press / drag / release (with modifiers) and key events are routed through a headless
/// window hosting a <see cref="GlViewportSurface"/>, exercising the pane's actual Avalonia
/// handlers (button mapping, device-pixel scaling, modifier extraction, key translation) and
/// through them the shared gesture router. This is the "simulate pointer press/drag/release +
/// modifiers on the GL surface pane" path; the gesture semantics themselves are proven
/// backend-identical in <see cref="ViewportGestureParityTests"/>.
/// </summary>
public sealed class GlViewportAvaloniaInputTests
{
    private static CommandDispatcher NewDispatcher() =>
        new(CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));

    private static (Window Window, GlViewportSurface Gl, List<string> Log) Show(ViewType view)
    {
        var gl = new GlViewportSurface(NewDispatcher(), CameraSchemeKind.RedClassic, view);
        var log = new List<string>();
        gl.MarqueeEnabled = true;
        gl.MarqueeStarted += (x, y) => log.Add($"MarqueeStarted {x},{y}");
        gl.MarqueeMovedTo += (x, y) => log.Add($"MarqueeMovedTo {x},{y}");
        gl.MarqueeEnded += (x, y, add) => log.Add($"MarqueeEnded {x},{y},{add}");
        gl.DrawClick += (x, y) => log.Add($"DrawClick {x},{y}");
        gl.BrushDragStarted += () => log.Add("BrushDragStarted");
        gl.BrushDragPixels += (dx, dy, axis) => log.Add($"BrushDragPixels {dx},{dy},{axis}");
        gl.BrushDragEnded += () => log.Add("BrushDragEnded");

        var window = new Window { Width = 300, Height = 300, Content = gl };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, gl, log);
    }

    [AvaloniaFact]
    public void PointerPressDragRelease_Runs_A_Marquee_Through_The_Real_Avalonia_Path()
    {
        (Window window, _, List<string> log) = Show(ViewType.Top);

        window.MouseDown(new Point(40, 40), MouseButton.Left);
        window.MouseMove(new Point(120, 130));
        window.MouseMove(new Point(160, 170));
        window.MouseUp(new Point(160, 170), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "MarqueeStarted 40,40", "MarqueeMovedTo 120,130", "MarqueeMovedTo 160,170", "MarqueeEnded 160,170,False" },
            log);
    }

    [AvaloniaFact]
    public void CtrlModifier_Reaches_The_Marquee_As_Additive_Through_The_Real_Path()
    {
        (Window window, _, List<string> log) = Show(ViewType.Top);

        window.MouseDown(new Point(20, 20), MouseButton.Left, RawInputModifiers.Control);
        window.MouseMove(new Point(90, 90), RawInputModifiers.Control);
        window.MouseUp(new Point(90, 90), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("MarqueeEnded 90,90,True", log);
    }

    [AvaloniaFact]
    public void HeldTransformKey_Plus_PointerDrag_Runs_A_Brush_Move_Through_The_Real_Path()
    {
        (Window window, GlViewportSurface gl, List<string> log) = Show(ViewType.Top);
        gl.Focus();

        window.KeyPress(Key.M, RawInputModifiers.None, PhysicalKey.M, "m");
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseMove(new Point(70, 62));
        window.MouseUp(new Point(70, 62), MouseButton.Left);
        window.KeyRelease(Key.M, RawInputModifiers.None, PhysicalKey.M, "m");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "BrushDragStarted", "BrushDragPixels 20,12,False", "BrushDragEnded" },
            log);
    }
}
