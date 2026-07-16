using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The selection-filter model behind the top-toolbar chips: two-way sync with the
/// editing mode (mode→chip and chip→mode), Ctrl-add multi-kind picking, and the
/// filter→pick gating primitive (<see cref="SelectionFilter.Allows"/>).
/// </summary>
public sealed class SelectionFilterTests
{
    [Fact]
    public void Default_Is_Object_Mode_With_Objects_Chip()
    {
        var f = new SelectionFilter();
        Assert.Equal(EditMode.Object, f.Mode);
        Assert.True(f.Allows(SelectKinds.Objects));
        Assert.False(f.Allows(SelectKinds.Brushes));
    }

    // ---- chip -> mode (plain click is exclusive) ----

    [Theory]
    [InlineData(SelectKinds.Brushes, EditMode.Brush)]
    [InlineData(SelectKinds.Faces, EditMode.Face)]
    [InlineData(SelectKinds.Vertices, EditMode.Vertex)]
    [InlineData(SelectKinds.Objects, EditMode.Object)]
    [InlineData(SelectKinds.Groups, EditMode.Group)]
    public void SetPrimary_Switches_Mode_And_Is_Exclusive(SelectKinds kind, EditMode expectedMode)
    {
        var f = new SelectionFilter();
        f.SetPrimary(kind);
        Assert.Equal(expectedMode, f.Mode);
        Assert.Equal(kind, f.Active);
        Assert.True(f.Allows(kind));
    }

    // ---- mode -> chip ----

    [Fact]
    public void SyncFromMode_Lights_The_Matching_Chip()
    {
        var f = new SelectionFilter();
        f.SyncFromMode(EditMode.Vertex);
        Assert.Equal(EditMode.Vertex, f.Mode);
        Assert.Equal(SelectKinds.Vertices, f.Active);
    }

    [Fact]
    public void Face_Mode_Lights_The_Faces_Chip()
    {
        // Texturing is a tab of Face mode (item 0h) — there is no separate Texture mode; the
        // Faces chip covers both the Geometry and Texture/UV tabs.
        var f = new SelectionFilter();
        f.SyncFromMode(EditMode.Face);
        Assert.Equal(EditMode.Face, f.Mode);
        Assert.Equal(SelectKinds.Faces, f.Active);
        Assert.True(f.Allows(SelectKinds.Faces));
    }

    // ---- Ctrl-click multi-kind ----

    [Fact]
    public void ToggleAdditional_Adds_A_Kind_Without_Changing_Mode()
    {
        var f = new SelectionFilter();
        f.SetPrimary(SelectKinds.Faces);   // Face mode
        f.ToggleAdditional(SelectKinds.Objects);

        Assert.Equal(EditMode.Face, f.Mode);          // mode unchanged
        Assert.True(f.Allows(SelectKinds.Faces));     // primary stays
        Assert.True(f.Allows(SelectKinds.Objects));   // added kind picks too
    }

    [Fact]
    public void ToggleAdditional_Twice_Removes_The_Added_Kind()
    {
        var f = new SelectionFilter();
        f.SetPrimary(SelectKinds.Brushes);
        f.ToggleAdditional(SelectKinds.Objects);
        f.ToggleAdditional(SelectKinds.Objects);
        Assert.True(f.Allows(SelectKinds.Brushes));
        Assert.False(f.Allows(SelectKinds.Objects));
    }

    [Fact]
    public void ToggleAdditional_Never_Clears_The_Primary_Kind()
    {
        var f = new SelectionFilter();
        f.SetPrimary(SelectKinds.Faces);
        f.ToggleAdditional(SelectKinds.Faces); // try to turn the mode's own chip off
        Assert.True(f.Allows(SelectKinds.Faces));
    }

    [Fact]
    public void Switching_Mode_Resets_The_Additional_Kinds()
    {
        var f = new SelectionFilter();
        f.SetPrimary(SelectKinds.Faces);
        f.ToggleAdditional(SelectKinds.Objects);
        f.SetPrimary(SelectKinds.Brushes); // plain-click another chip

        Assert.Equal(SelectKinds.Brushes, f.Active); // exclusive again
        Assert.False(f.Allows(SelectKinds.Objects));
    }

    // ---- filter -> pick gating ----

    [Fact]
    public void Allows_Gates_Picks_To_The_Active_Kinds()
    {
        var f = new SelectionFilter();
        f.SetPrimary(SelectKinds.Faces);
        Assert.True(f.Allows(SelectKinds.Faces));
        Assert.False(f.Allows(SelectKinds.Brushes));
        Assert.False(f.Allows(SelectKinds.Objects));

        f.ToggleAdditional(SelectKinds.Objects);
        Assert.True(f.Allows(SelectKinds.Objects)); // now pickable
    }
}
