using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ged.App.Controls;
using Ged.App.Services;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 3: the viewport progress overlay appears while an operation is in flight, hides once the
/// set of operations empties, and stacks a card per operation when several overlap. Driven through
/// the same <see cref="OperationProgressService"/> the build/bake/hole-check paths report to.
/// </summary>
public sealed class ProgressOverlayTests
{
    private static ProgressOverlay Mount(OperationProgressService svc)
    {
        var overlay = new ProgressOverlay(svc);
        var win = new Window { Content = overlay };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return overlay;
    }

    [AvaloniaFact]
    public void Overlay_Appears_During_An_Operation_And_Hides_After()
    {
        var svc = new OperationProgressService();
        ProgressOverlay overlay = Mount(svc);

        Assert.False(overlay.IsVisible);

        OperationProgress op = svc.Begin("Building geometry");
        op.Report("Rooms", 3, 10);
        Dispatcher.UIThread.RunJobs();

        Assert.True(overlay.IsVisible);
        Assert.Equal(1, overlay.ActiveCardCount);

        op.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.False(overlay.IsVisible);
        Assert.Equal(0, overlay.ActiveCardCount);
    }

    [AvaloniaFact]
    public void Overlay_Stacks_A_Card_Per_Overlapping_Operation()
    {
        var svc = new OperationProgressService();
        ProgressOverlay overlay = Mount(svc);

        OperationProgress build = svc.Begin("Building geometry");
        OperationProgress holes = svc.Begin("Check for Holes");
        Dispatcher.UIThread.RunJobs();

        Assert.True(overlay.IsVisible);
        Assert.Equal(2, overlay.ActiveCardCount);

        build.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsVisible);
        Assert.Equal(1, overlay.ActiveCardCount);

        holes.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.False(overlay.IsVisible);
    }

    [AvaloniaFact]
    public void Overlay_Never_Blocks_Viewport_Input()
    {
        var svc = new OperationProgressService();
        ProgressOverlay overlay = Mount(svc);
        // Informational only — it must never take a hit or steal focus from the viewport beneath.
        Assert.False(overlay.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void Overlay_Raises_ActiveChanged_Only_When_The_Operation_Set_Crosses_Empty()
    {
        // The shell rehosts this stack in a native popup over the D3D11 viewport HWND and opens/closes
        // it from ActiveChanged. That must fire exactly once on empty→non-empty and once on
        // non-empty→empty — never on the intermediate overlaps — so the popup does not churn.
        var svc = new OperationProgressService();
        ProgressOverlay overlay = Mount(svc);

        var events = new List<bool>();
        overlay.ActiveChanged += active => events.Add(active);

        OperationProgress a = svc.Begin("Building geometry"); // empty → non-empty: raise true
        Dispatcher.UIThread.RunJobs();
        OperationProgress b = svc.Begin("Check for Holes");   // still non-empty: no raise
        Dispatcher.UIThread.RunJobs();

        a.Dispose(); // still one live op: no raise
        Dispatcher.UIThread.RunJobs();
        b.Dispose(); // non-empty → empty: raise false
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { true, false }, events);
    }
}
