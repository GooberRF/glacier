using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Panels;
using Ged.Core.Editor;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Unit behaviour of the palette's floating mesh-preview popover: it dwells before opening
/// (so scrolling past a row never flashes it), renders the larger preview at the 384px size
/// through the injected loader, and closes cleanly. The panel wiring across all three class
/// tabs is covered in <see cref="ObjectPaletteStructureTests"/>.
/// </summary>
public sealed class MeshHoverPreviewTests
{
    [AvaloniaFact]
    public void Dwell_Delay_Is_300ms_So_Scrolling_Does_Not_Flash()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), MeshHoverPreview.Delay);
    }

    [AvaloniaFact]
    public void Schedule_Waits_For_The_Dwell_Then_ShowNow_Opens_A_384_Render()
    {
        var rendered = new List<(LevelObjectKind Kind, string? Class, int Size)>();
        var hover = new MeshHoverPreview
        {
            RenderInto = (kind, cls, img) => rendered.Add((kind, cls, (int)img.Width)),
        };

        hover.Schedule(new Border(), LevelObjectKind.Clutter, "barrel");
        Assert.True(hover.HasPendingShow);
        Assert.False(hover.IsShowing);
        Assert.Empty(rendered); // nothing rendered until the dwell elapses

        hover.ShowNow(); // the dwell timer's action
        Assert.False(hover.HasPendingShow);
        Assert.True(hover.IsShowing);

        // Rendered exactly once, for the hovered class, at the 384px preview size.
        var one = Assert.Single(rendered);
        Assert.Equal(LevelObjectKind.Clutter, one.Kind);
        Assert.Equal("barrel", one.Class);
        Assert.Equal(MeshHoverPreview.PreviewSize, one.Size);
        Assert.Equal(384, MeshHoverPreview.PreviewSize);
    }

    [AvaloniaFact]
    public void Cancel_Before_The_Dwell_Never_Renders_Or_Shows()
    {
        var rendered = 0;
        var hover = new MeshHoverPreview { RenderInto = (_, _, _) => rendered++ };

        hover.Schedule(new Border(), LevelObjectKind.Item, "First_Aid");
        hover.Cancel();

        Assert.False(hover.HasPendingShow);
        Assert.False(hover.IsShowing);
        Assert.Equal(0, rendered);

        // A stray timer tick after cancel is a no-op (no pending anchor).
        hover.ShowNow();
        Assert.False(hover.IsShowing);
        Assert.Equal(0, rendered);
    }

    [AvaloniaFact]
    public void Cancel_After_Show_Closes_The_Popover()
    {
        var hover = new MeshHoverPreview { RenderInto = (_, _, _) => { } };
        hover.Schedule(new Border(), LevelObjectKind.Entity, "Guard");
        hover.ShowNow();
        Assert.True(hover.IsShowing);

        hover.Cancel();
        Assert.False(hover.IsShowing);
        Assert.Null(hover.CurrentImage);
    }
}
