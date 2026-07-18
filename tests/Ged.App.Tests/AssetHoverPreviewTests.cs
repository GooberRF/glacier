using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Panels;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 1 (bug) — the Asset Browser large hover popover must re-key to a new tile the moment the
/// pointer moves onto it while the popover is open (an open Avalonia Popup otherwise keeps showing
/// the previous tile until it closes entirely). Also covers the order-safe close bridge.
/// </summary>
public sealed class AssetHoverPreviewTests
{
    [AvaloniaFact]
    public void Dwell_Is_350ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(350), AssetHoverPreview.Delay);
    }

    [AvaloniaFact]
    public void Schedule_Dwells_Then_ShowNow_Renders_And_Keys_To_The_Tile()
    {
        var rendered = new List<Control>();
        var preview = new AssetHoverPreview();
        var tileA = new Border();

        preview.Schedule(tileA, _ => rendered.Add(tileA));
        Assert.True(preview.HasPendingShow);
        Assert.False(preview.IsShowing);
        Assert.Empty(rendered); // nothing until the dwell elapses

        preview.ShowNow();
        Assert.True(preview.IsShowing);
        Assert.Same(tileA, preview.CurrentKey);
        Assert.Single(rendered);
    }

    [AvaloniaFact]
    public void Entering_A_Different_Tile_While_Open_Rekeys_And_Rerenders_Immediately()
    {
        var rendered = new List<Control>();
        var preview = new AssetHoverPreview();
        var tileA = new Border();
        var tileB = new Border();

        preview.Schedule(tileA, _ => rendered.Add(tileA));
        preview.ShowNow(); // showing A
        Assert.Same(tileA, preview.CurrentKey);
        Assert.Single(rendered);

        // Move directly onto tile B while the popover is open: it re-keys + re-renders NOW, no re-dwell.
        preview.Schedule(tileB, _ => rendered.Add(tileB));
        Assert.True(preview.IsShowing);
        Assert.Same(tileB, preview.CurrentKey);
        Assert.Equal(2, rendered.Count);
        Assert.Same(tileB, rendered[1]);
    }

    [AvaloniaFact]
    public void Re_Entering_The_Shown_Tile_Is_A_NoOp()
    {
        var renders = 0;
        var preview = new AssetHoverPreview();
        var tileA = new Border();

        preview.Schedule(tileA, _ => renders++);
        preview.ShowNow();
        Assert.Equal(1, renders);

        preview.Schedule(tileA, _ => renders++); // same tile → no churn / re-render
        Assert.Equal(1, renders);
        Assert.Same(tileA, preview.CurrentKey);
    }

    [AvaloniaFact]
    public void Out_Of_Order_Leave_Of_The_Old_Tile_Does_Not_Close_The_New_One()
    {
        var preview = new AssetHoverPreview();
        var tileA = new Border();
        var tileB = new Border();

        preview.Schedule(tileA, _ => { });
        preview.ShowNow();

        // Enter B (now showing B) THEN a stale Exit(A) arrives — it must not arm a close on B.
        preview.Schedule(tileB, _ => { });
        preview.ScheduleClose(tileA);

        Assert.True(preview.IsShowing);
        Assert.Same(tileB, preview.CurrentKey);
    }
}
