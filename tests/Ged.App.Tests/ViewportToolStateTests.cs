using System.Collections.Generic;
using Ged.App.Services;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The exclusive Select / Draw / Ruler tool state machine (item 11), including the user's
/// exact repro: draw → ruler → ruler-off must land on Select (never back on Draw).
/// </summary>
public sealed class ViewportToolStateTests
{
    [Fact]
    public void Default_Is_Select()
    {
        Assert.Equal(ViewportTool.Select, new ViewportToolState().Active);
    }

    [Fact]
    public void Activating_One_Deactivates_The_Others()
    {
        var s = new ViewportToolState();
        s.Request(ViewportTool.Draw);
        Assert.Equal(ViewportTool.Draw, s.Active);

        s.Request(ViewportTool.Ruler);
        Assert.Equal(ViewportTool.Ruler, s.Active); // Draw is no longer active
    }

    [Fact]
    public void Reactivating_Active_Tool_Toggles_Back_To_Select()
    {
        var s = new ViewportToolState();
        s.Request(ViewportTool.Draw);
        Assert.Equal(ViewportTool.Select, s.Request(ViewportTool.Draw));
    }

    [Fact]
    public void User_Repro_Draw_Then_Ruler_Then_Ruler_Off_Lands_On_Select()
    {
        var s = new ViewportToolState();
        s.Request(ViewportTool.Draw);   // Draw
        s.Request(ViewportTool.Ruler);  // Ruler (Draw off)
        s.Request(ViewportTool.Ruler);  // Ruler off ⇒ must be Select, NOT Draw

        Assert.Equal(ViewportTool.Select, s.Active);
    }

    [Fact]
    public void Draw_And_Ruler_Fire_Changed_Only_On_Real_Transitions()
    {
        var s = new ViewportToolState();
        s.Request(ViewportTool.Draw); // arm Draw first so re-requesting it is a no-op transition
        var log = new List<ViewportTool>();
        s.Changed += log.Add;

        s.Request(ViewportTool.Draw); // Draw→Select (toggle off): event Select
        s.Request(ViewportTool.Ruler); // Select→Ruler: event Ruler

        Assert.Equal(new[] { ViewportTool.Select, ViewportTool.Ruler }, log);
    }

    [Fact]
    public void Clicking_Active_Select_Does_Not_Deactivate_It()
    {
        // The user's repro: Select is the floor and has no "off" state. Re-requesting it
        // while active must keep it active (never leave the viewport tool-less).
        var s = new ViewportToolState();
        Assert.Equal(ViewportTool.Select, s.Active);

        Assert.Equal(ViewportTool.Select, s.Request(ViewportTool.Select));
        Assert.Equal(ViewportTool.Select, s.Active);
    }

    [Fact]
    public void Requesting_Select_While_Active_Reasserts_And_Notifies()
    {
        // Re-asserting Select fires Changed (with no state change) so the host can re-check
        // the Select toolbar button that its ToggleButton auto-unchecked on the click.
        var s = new ViewportToolState();
        var log = new List<ViewportTool>();
        s.Changed += log.Add;

        s.Request(ViewportTool.Select); // already Select → re-assert → event Select
        s.Request(ViewportTool.Select); // still Select → re-assert again

        Assert.Equal(new[] { ViewportTool.Select, ViewportTool.Select }, log);
        Assert.Equal(ViewportTool.Select, s.Active);
    }

    [Fact]
    public void Reset_Returns_To_Select_From_Any_Tool()
    {
        var s = new ViewportToolState();
        s.Request(ViewportTool.Ruler);
        Assert.Equal(ViewportTool.Select, s.Reset());
    }
}
