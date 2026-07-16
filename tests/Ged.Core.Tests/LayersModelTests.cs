using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Layers panel view-model logic: build/time order conveyed by row
/// position (no layer number), the THREE-group filter matrix ({Air|Solid} × {Detail|Portal|
/// Geoable|Breakable} × {Normal|Locked|Hidden}, OR within / AND between), multi-select block
/// drag order, single-step nudge, and the BrushEditor reorder (undo + geometry-dirty).
/// </summary>
public sealed class LayersModelTests
{
    private static Brush Brush(int uid, BrushFlags flags = BrushFlags.None, int life = -1, int state = BrushState.Normal) =>
        new() { Uid = uid, Flags = (uint)flags, Life = life, State = state };

    // ---- Row order (position conveys build order; no layer number) ----

    [Fact]
    public void Rows_Follow_Build_Order()
    {
        var brushes = new List<Brush> { Brush(10), Brush(20), Brush(30) };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);
        Assert.Equal(new[] { 10, 20, 30 }, rows.Select(r => r.Uid));

        // Reorder → row order follows the NEW build position (position IS the order).
        var reordered = new List<Brush> { brushes[2], brushes[0], brushes[1] };
        IReadOnlyList<LayerRow> rows2 = LayersModel.BuildRows(reordered);
        Assert.Equal(new[] { 30, 10, 20 }, rows2.Select(r => r.Uid));
    }

    [Fact]
    public void TimeIndex_Is_Zero_Based_Build_Position_And_Tracks_Reorders()
    {
        // RED prints "t=X" as the 0-based position of the brush in build order (RED.exe
        // brush_mode_handle_selection walks the list from the head). The row's TimeIndex must
        // match, and follow the order on reorder.
        var brushes = new List<Brush> { Brush(10), Brush(20), Brush(30) };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);
        Assert.Equal(new[] { 0, 1, 2 }, rows.Select(r => r.TimeIndex));
        Assert.Equal(0, rows.First(r => r.Uid == 10).TimeIndex);

        // Move brush 30 to the front → it becomes t=0 and the others shift up by one.
        var reordered = new List<Brush> { brushes[2], brushes[0], brushes[1] };
        IReadOnlyList<LayerRow> rows2 = LayersModel.BuildRows(reordered);
        Assert.Equal(0, rows2.First(r => r.Uid == 30).TimeIndex);
        Assert.Equal(1, rows2.First(r => r.Uid == 10).TimeIndex);
        Assert.Equal(2, rows2.First(r => r.Uid == 20).TimeIndex);
    }

    [Fact]
    public void Row_Properties_Reflect_Flags_Life_And_State()
    {
        var brushes = new List<Brush>
        {
            Brush(1, BrushFlags.Air | BrushFlags.Detail),
            Brush(2, BrushFlags.Portal),
            Brush(3, BrushFlags.Geoable, life: 100),          // breakable (life != -1)
            Brush(4, BrushFlags.None, state: BrushState.Locked),
        };
        var hidden = new HashSet<int> { 2 };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes, hidden);

        Assert.True(rows[0].Air && rows[0].Detail && !rows[0].Solid);
        Assert.True(rows[1].Portal && rows[1].Solid && rows[1].Hidden);
        Assert.True(rows[2].Geoable && rows[2].Breakable);
        Assert.False(rows[2].Breakable == false);
        Assert.True(rows[3].Locked && rows[3].Visibility == BrushVisibility.Locked);
    }

    // ---- Filter matrix (three independent groups: OR within, AND between) ----

    [Fact]
    public void Filter_Air_Or_Solid_And_Only_Locked_Shows_Locked_Air_Or_Solid()
    {
        var brushes = new List<Brush>
        {
            Brush(1, BrushFlags.Air, state: BrushState.Locked),   // locked air → shown
            Brush(2, BrushFlags.None, state: BrushState.Locked),  // locked solid → shown
            Brush(3, BrushFlags.Air),                              // unlocked air → hidden by vis
            Brush(4, BrushFlags.None),                             // unlocked solid → hidden by vis
        };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);

        // Solidity set = Air OR Solid; Properties = all (none of these plain brushes carry a
        // modifier, so the property group is a no-op); Visibility set = Locked only.
        List<int> shown = rows
            .Where(r => LayersModel.Passes(r, LayerSolidity.Air | LayerSolidity.Solid, LayerProps.All, LayerVis.Locked))
            .Select(r => r.Uid).ToList();
        Assert.Equal(new[] { 1, 2 }, shown);
    }

    [Fact]
    public void All_Filters_On_Shows_Everything()
    {
        var brushes = new List<Brush> { Brush(1, BrushFlags.Air), Brush(2, BrushFlags.Detail), Brush(3, BrushFlags.Portal, state: BrushState.Locked) };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);
        Assert.All(rows, r => Assert.True(LayersModel.Passes(r, LayerSolidity.All, LayerProps.All, LayerVis.All)));
    }

    [Fact]
    public void Solidity_Group_Splits_Air_From_Solid()
    {
        var brushes = new List<Brush> { Brush(1, BrushFlags.Air), Brush(2, BrushFlags.None) };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);

        Assert.Equal(new[] { 1 }, rows.Where(r => LayersModel.Passes(r, LayerSolidity.Air, LayerProps.All, LayerVis.All)).Select(r => r.Uid));
        Assert.Equal(new[] { 2 }, rows.Where(r => LayersModel.Passes(r, LayerSolidity.Solid, LayerProps.All, LayerVis.All)).Select(r => r.Uid));
    }

    [Fact]
    public void Property_Group_Filters_Modifiers_But_Never_Hides_Plain_Brushes()
    {
        var brushes = new List<Brush>
        {
            Brush(1, BrushFlags.Detail),   // detail modifier
            Brush(2, BrushFlags.Portal),   // portal modifier
            Brush(3, BrushFlags.None),     // plain (no modifier)
        };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);

        // Only Detail enabled: the detail brush shows, the portal brush is filtered out, and the
        // plain brush is NEVER hidden by the property group (it carries no modifier to filter on).
        List<int> shown = rows
            .Where(r => LayersModel.Passes(r, LayerSolidity.All, LayerProps.Detail, LayerVis.All))
            .Select(r => r.Uid).ToList();
        Assert.Equal(new[] { 1, 3 }, shown);
    }

    [Fact]
    public void Three_Groups_And_Together()
    {
        var brushes = new List<Brush>
        {
            Brush(1, BrushFlags.Air | BrushFlags.Portal, state: BrushState.Locked),  // air + portal + locked
            Brush(2, BrushFlags.Air | BrushFlags.Portal),                            // air + portal + normal
            Brush(3, BrushFlags.Portal, state: BrushState.Locked),                   // solid + portal + locked
        };
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(brushes);

        // Air AND Portal AND Locked ⇒ only brush 1 (2 fails vis, 3 fails solidity).
        List<int> shown = rows
            .Where(r => LayersModel.Passes(r, LayerSolidity.Air, LayerProps.Portal, LayerVis.Locked))
            .Select(r => r.Uid).ToList();
        Assert.Equal(new[] { 1 }, shown);
    }

    // ---- Reorder math ----

    [Fact]
    public void MoveBlock_Multi_Select_Preserves_Relative_Order()
    {
        var order = new[] { 1, 2, 3, 4, 5 };
        // Move {2,4} to the front.
        Assert.Equal(new[] { 2, 4, 1, 3, 5 }, LayersModel.MoveBlock(order, new[] { 2, 4 }, 0));
        // Move {2,4} to the end.
        Assert.Equal(new[] { 1, 3, 5, 2, 4 }, LayersModel.MoveBlock(order, new[] { 2, 4 }, 5));
        // Move {1,2} to between 4 and 5 (dropIndex = 4).
        Assert.Equal(new[] { 3, 4, 1, 2, 5 }, LayersModel.MoveBlock(order, new[] { 1, 2 }, 4));
    }

    [Fact]
    public void Nudge_Moves_One_Position_And_Clamps()
    {
        var order = new[] { 1, 2, 3 };
        Assert.Equal(new[] { 2, 1, 3 }, LayersModel.Nudge(order, 1, +1)); // down
        Assert.Equal(new[] { 1, 3, 2 }, LayersModel.Nudge(order, 3, -1)); // up
        Assert.Equal(new[] { 1, 2, 3 }, LayersModel.Nudge(order, 1, -1)); // clamp at top
        Assert.Equal(new[] { 1, 2, 3 }, LayersModel.Nudge(order, 3, +1)); // clamp at bottom
    }

    // ---- BrushEditor reorder: undo + geometry-dirty ----

    private static (EditorDocument Doc, BrushEditor Ed) NewDocWithBrushes(params int[] uids)
    {
        var rfl = new RflFile { };
        rfl.Header.Version = 0xC8;
        var bs = new BrushesSection();
        foreach (int uid in uids)
        {
            bs.Brushes.Add(new Brush { Uid = uid });
        }

        rfl.Sections.Add(new RflSection((uint)SectionType.Brushes, Array.Empty<byte>()) { Content = bs });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);
        return (doc, new BrushEditor(doc));
    }

    [Fact]
    public void ReorderTo_Applies_Order_Is_Undoable_And_Notifies()
    {
        (EditorDocument doc, BrushEditor ed) = NewDocWithBrushes(1, 2, 3, 4);
        int changed = 0;
        ed.BrushesChanged += () => changed++;

        ed.ReorderTo(new[] { 4, 3, 2, 1 });
        Assert.Equal(new[] { 4, 3, 2, 1 }, ed.Brushes.Select(b => b.Uid));
        Assert.True(changed > 0, "reorder must raise BrushesChanged (drives geometry-dirty + live preview)");

        doc.Undo.Undo();
        Assert.Equal(new[] { 1, 2, 3, 4 }, ed.Brushes.Select(b => b.Uid));
        doc.Undo.Redo();
        Assert.Equal(new[] { 4, 3, 2, 1 }, ed.Brushes.Select(b => b.Uid));
    }

    [Fact]
    public void Selection_Maps_Between_Rows_And_Brushes_Both_Ways()
    {
        (_, BrushEditor ed) = NewDocWithBrushes(1, 2, 3);

        // View → rows: selecting brushes marks the matching rows (even when filtered).
        ed.SelectBrush(2);
        ed.SelectBrush(3, additive: true);
        var selected = new HashSet<int>(ed.SelectedBrushes);
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(ed.Brushes.ToList());
        List<int> selectedRows = rows.Where(r => selected.Contains(r.Uid)).Select(r => r.Uid).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 2, 3 }, selectedRows);

        // Rows → view: selecting a row's UID selects that brush.
        ed.SelectBrush(1);
        Assert.Contains(1, ed.SelectedBrushes);
        Assert.DoesNotContain(2, ed.SelectedBrushes); // non-additive replaced the selection
    }

    [Fact]
    public void Lock_And_Hide_Toggle_Brush_State()
    {
        (_, BrushEditor ed) = NewDocWithBrushes(1, 2);
        int visChanges = 0;
        ed.VisibilityChanged += () => visChanges++;

        ed.SetBrushLocked(new[] { 1 }, true);
        Assert.Equal(BrushState.Locked, ed.Brushes.First(b => b.Uid == 1).State);

        ed.SetBrushHidden(new[] { 2 }, true);
        Assert.True(ed.IsBrushHidden(2));
        Assert.False(ed.IsBrushHidden(1));
        Assert.True(visChanges >= 2);
    }
}
