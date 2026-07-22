using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Rendering.Picking;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 5: strict mode-scoped picking. Each mode's strict default chip set (what a
/// mode switch resets to) admits ONLY that mode's kinds; out-of-mode kinds are
/// ignored even when hit first in the id-buffer. Ctrl+chip remains the explicit
/// opt-in for multi-kind picking.
/// </summary>
public sealed class PickGateTests
{
    private static SelectKinds StrictDefault(EditMode mode) => SelectionFilter.PrimaryKindFor(mode);

    // ---- Per-mode pick gating matrix (strict defaults) ------------------------

    [Theory]
    // Object mode admits NO brush-editor kinds (objects route through the document gate).
    [InlineData(EditMode.Object, PickKind.Object, false)]
    [InlineData(EditMode.Object, PickKind.Mesh, false)]
    [InlineData(EditMode.Object, PickKind.Brush, false)]
    [InlineData(EditMode.Object, PickKind.BrushFace, false)]
    [InlineData(EditMode.Object, PickKind.BrushVertex, false)]
    // Brush mode: brushes only.
    [InlineData(EditMode.Brush, PickKind.Brush, true)]
    [InlineData(EditMode.Brush, PickKind.BrushFace, false)]
    [InlineData(EditMode.Brush, PickKind.BrushVertex, false)]
    // Face mode: faces only.
    [InlineData(EditMode.Face, PickKind.BrushFace, true)]
    [InlineData(EditMode.Face, PickKind.Brush, false)]
    [InlineData(EditMode.Face, PickKind.BrushVertex, false)]
    // Vertex mode: vertices only.
    [InlineData(EditMode.Vertex, PickKind.BrushVertex, true)]
    [InlineData(EditMode.Vertex, PickKind.Brush, false)]
    [InlineData(EditMode.Vertex, PickKind.BrushFace, false)]
    // Group mode: a whole-brush pick selects (B4 — brushes are group members, exactly like
    // objects), but faces/vertices stay strict to their own chip.
    [InlineData(EditMode.Group, PickKind.Brush, true)]
    [InlineData(EditMode.Group, PickKind.BrushFace, false)]
    [InlineData(EditMode.Group, PickKind.BrushVertex, false)]
    public void BrushEditor_Gate_Matrix(EditMode mode, PickKind kind, bool expected) =>
        Assert.Equal(expected, PickGate.AllowsBrushEditor(StrictDefault(mode), kind));

    // ---- B4: in Group mode a brush pick and an object pick are BOTH admitted, so a brush click
    //      resolves to a selection exactly like an object click (the click and marquee paths both
    //      gate on these two predicates). ----
    [Fact]
    public void Group_Mode_Admits_Both_Brush_And_Object_Picks_Symmetrically()
    {
        SelectKinds group = StrictDefault(EditMode.Group);
        Assert.True(PickGate.AllowsBrushEditor(group, PickKind.Brush), "a brush pick must select in Group mode");
        Assert.True(PickGate.AllowsDocumentSelect(group, PickKind.Object, isMoverObject: false), "an object pick selects in Group mode");
        // Object mode stays asymmetric on purpose: an editable brush is NOT selectable there.
        SelectKinds obj = StrictDefault(EditMode.Object);
        Assert.False(PickGate.AllowsBrushEditor(obj, PickKind.Brush));
    }

    [Theory]
    // Object mode: level objects yes; plain brushes NO even when hit first; movers yes.
    [InlineData(EditMode.Object, PickKind.Object, false, true)]
    [InlineData(EditMode.Object, PickKind.Mesh, false, true)]
    [InlineData(EditMode.Object, PickKind.Brush, false, false)]
    [InlineData(EditMode.Object, PickKind.Brush, true, true)]
    // Group mode: group members = objects AND brushes (stock).
    [InlineData(EditMode.Group, PickKind.Object, false, true)]
    [InlineData(EditMode.Group, PickKind.Mesh, false, true)]
    [InlineData(EditMode.Group, PickKind.Brush, false, true)]
    // Brush/Face/Vertex modes never document-select objects.
    [InlineData(EditMode.Brush, PickKind.Object, false, false)]
    [InlineData(EditMode.Face, PickKind.Object, false, false)]
    [InlineData(EditMode.Vertex, PickKind.Mesh, false, false)]
    // Gizmo / static-face hits never document-select.
    [InlineData(EditMode.Object, PickKind.Gizmo, false, false)]
    [InlineData(EditMode.Object, PickKind.Face, false, false)]
    public void Document_Select_Gate_Matrix(EditMode mode, PickKind kind, bool isMover, bool expected) =>
        Assert.Equal(expected, PickGate.AllowsDocumentSelect(StrictDefault(mode), kind, isMover));

    // ---- Ctrl+chip multi-kind opt-in ------------------------------------------

    [Fact]
    public void CtrlChip_Opt_In_Admits_The_Added_Kind()
    {
        var filter = new SelectionFilter(EditMode.Object);

        // Strict Object default rejects brush-editor picks…
        Assert.False(PickGate.AllowsBrushEditor(filter.Active, PickKind.Brush));

        // …until Brushes is Ctrl-clicked in.
        filter.ToggleAdditional(SelectKinds.Brushes);
        Assert.True(PickGate.AllowsBrushEditor(filter.Active, PickKind.Brush));
        Assert.Equal(EditMode.Object, filter.Mode); // mode unchanged (opt-in, not a switch)
    }

    // ---- Chip reset on mode switch ---------------------------------------------

    [Theory]
    [InlineData(EditMode.Object, SelectKinds.Objects)]
    [InlineData(EditMode.Brush, SelectKinds.Brushes)]
    [InlineData(EditMode.Face, SelectKinds.Faces)]
    [InlineData(EditMode.Vertex, SelectKinds.Vertices)]
    [InlineData(EditMode.Group, SelectKinds.Groups)]
    public void Mode_Switch_Resets_Chips_To_The_Strict_Default(EditMode mode, SelectKinds expected)
    {
        var filter = new SelectionFilter(EditMode.Object);

        // Widen the filter via Ctrl+chip opt-ins…
        filter.ToggleAdditional(SelectKinds.Brushes);
        filter.ToggleAdditional(SelectKinds.Faces);
        Assert.NotEqual(SelectKinds.Objects, filter.Active);

        // …then a mode switch resets to exactly the mode's strict default set.
        filter.SyncFromMode(mode);
        Assert.Equal(expected, filter.Active);
        Assert.Equal(mode, filter.Mode);
    }
}
