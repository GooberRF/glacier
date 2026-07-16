using System.Linq;
using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>Marquee (drag-box) selection: rectangle math, click-vs-drag, and filter gating.</summary>
public sealed class MarqueeSelectionTests
{
    [Fact]
    public void FromCorners_Normalizes_Regardless_Of_Drag_Direction()
    {
        MarqueeSelection.Rect r = MarqueeSelection.FromCorners(80, 60, 20, 10);
        Assert.Equal(20, r.MinX);
        Assert.Equal(10, r.MinY);
        Assert.Equal(80, r.MaxX);
        Assert.Equal(60, r.MaxY);
    }

    [Fact]
    public void IsMarquee_Distinguishes_A_Click_From_A_Drag()
    {
        Assert.False(MarqueeSelection.IsMarquee(100, 100, 101, 101)); // a click
        Assert.True(MarqueeSelection.IsMarquee(100, 100, 140, 100));  // a drag
    }

    [Fact]
    public void Select_Respects_The_Filter_Chips_And_The_Rectangle()
    {
        MarqueeSelection.Rect rect = MarqueeSelection.FromCorners(0, 0, 100, 100);
        var candidates = new[]
        {
            new MarqueeSelection.Candidate(1, SelectKinds.Objects, 50, 50),  // in rect, allowed
            new MarqueeSelection.Candidate(2, SelectKinds.Objects, 200, 50), // outside rect
            new MarqueeSelection.Candidate(3, SelectKinds.Brushes, 40, 40),  // in rect, wrong kind
            new MarqueeSelection.Candidate(4, SelectKinds.Objects, 10, 90),  // in rect, allowed
        };

        var hits = MarqueeSelection.Select(rect, candidates, SelectKinds.Objects);
        Assert.Equal(new[] { 1, 4 }, hits.OrderBy(i => i));
    }

    [Fact]
    public void Select_Honours_MultiKind_Filter()
    {
        MarqueeSelection.Rect rect = MarqueeSelection.FromCorners(0, 0, 100, 100);
        var candidates = new[]
        {
            new MarqueeSelection.Candidate(1, SelectKinds.Objects, 50, 50),
            new MarqueeSelection.Candidate(2, SelectKinds.Brushes, 60, 60),
        };

        var hits = MarqueeSelection.Select(rect, candidates, SelectKinds.Objects | SelectKinds.Brushes);
        Assert.Equal(new[] { 1, 2 }, hits.OrderBy(i => i));
    }
}
