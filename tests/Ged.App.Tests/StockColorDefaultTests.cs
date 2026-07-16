using Ged.App;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 8: GED's default element colours are RED's stock values (the background + axis
/// triad keep GED's own defaults). Existing users' settings.cfg values are untouched —
/// these are only the unset defaults / Reset-to-defaults target.
/// </summary>
public sealed class StockColorDefaultTests
{
    [Fact]
    public void Default_Element_Colors_Are_The_Stock_RED_Values()
    {
        var s = new AppSettings();
        Assert.Equal("#8080FF", s.ColorGrid);          // 128,128,255
        Assert.Equal("#00FFFF", s.ColorCookieCutter);  // 0,255,255
        Assert.Equal("#FFFFFF", s.ColorBrush);         // 255,255,255
        Assert.Equal("#A0A0A0", s.ColorBrushLocked);   // 160,160,160
        Assert.Equal("#00FF00", s.ColorBrushDetail);   // 0,255,0
        Assert.Equal("#FFFF00", s.ColorBrushPortal);   // 255,255,0
        Assert.Equal("#00FF00", s.ColorMover);         // 0,255,0
        Assert.Equal("#0000FF", s.ColorLinks);         // 0,0,255
        Assert.Equal("#00FF00", s.ColorNodes);         // 0,255,0
        Assert.Equal("#C8C864", s.ColorBoundingBox);   // 200,200,100
        Assert.Equal("#0000FF", s.ColorTriggers);      // 0,0,255
        Assert.Equal("#00B03B", s.ColorRegions);       // 0,176,59
    }

    [Fact]
    public void Background_And_Axis_Triad_Keep_GED_Defaults()
    {
        var s = new AppSettings();
        Assert.Equal("#1A1C21", s.ColorBackground);
        Assert.Equal("#D55E00", s.ColorAxisX);
        Assert.Equal("#009E73", s.ColorAxisY);
        Assert.Equal("#56B4E9", s.ColorAxisZ);
    }

    /// <summary>Item 0g: a fresh launch (no settings.cfg) uses stock RED's 1.0 m grid.</summary>
    [Fact]
    public void Default_Grid_Size_Is_One_Metre()
    {
        Assert.Equal(1f, new AppSettings().GridSize);
        Assert.Equal(1f, new Ged.App.EditorSession().GridSize);
    }
}
