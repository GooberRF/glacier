using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Pins the UID-explicit <see cref="BrushEditor"/> overloads and that the
/// selection-driven GUI paths which now delegate to them behave exactly as before.
/// </summary>
public sealed class BrushEditorUidOverloadTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static int Box(BrushEditor ed, Vec3 pos, float size = 2) =>
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = size, Height = size, Depth = size }, pos, Mat3.Identity);

    [Fact]
    public void CarveSelected_Uses_The_First_Selected_Brush_As_Cutter()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = Box(ed, new Vec3(0, 0, 0));
        int b = Box(ed, new Vec3(3, 0, 0));
        int cutter = Box(ed, new Vec3(1.5f, 0, 0), 2);
        ed.SelectBrush(cutter);
        int facesA = ed.FindBrush(a)!.Geometry.Faces.Count;

        Assert.Equal(2, ed.CarveSelected());
        Assert.Equal("Carve 2 brush(es)", doc.Undo.Current.Description);
        Assert.True(ed.FindBrush(a)!.Geometry.Faces.Count >= facesA);
        Assert.NotNull(ed.FindBrush(cutter));
        Assert.Equal(new[] { cutter }, ed.SelectedBrushes.ToArray());

        doc.Undo.Undo();
        Assert.Equal(facesA, ed.FindBrush(a)!.Geometry.Faces.Count);

        ed.ClearSelection();
        Assert.Equal(0, ed.CarveSelected()); // nothing selected
    }

    [Fact]
    public void Carve_With_Explicit_Targets_Leaves_Selection_And_Others()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = Box(ed, new Vec3(0, 0, 0));
        int b = Box(ed, new Vec3(3, 0, 0));
        int cutter = Box(ed, new Vec3(1.5f, 0, 0), 2);
        ed.SelectBrush(a);
        Geometry bBefore = ed.FindBrush(b)!.Geometry;

        Assert.Equal(new[] { a }, ed.Carve(cutter, new[] { a }).ToArray());
        Assert.Same(bBefore, ed.FindBrush(b)!.Geometry);
        Assert.Equal(new[] { a }, ed.SelectedBrushes.ToArray());

        Assert.Equal(new[] { b }, ed.Carve(cutter, new[] { b, cutter }).ToArray()); // the cutter never carves itself
        Assert.Empty(ed.Carve(9999, null));                                       // unknown cutter
    }

    [Fact]
    public void Clip_By_Uid_Reports_Pieces_In_List_Order_Without_Selection()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = Box(ed, new Vec3(0, 0, 0));
        int b = Box(ed, new Vec3(0, 0, 4));

        OpResult r = ed.Clip(new[] { b, a }, new Vec3(0, 0, 0), new Vec3(1, 0, 0), ClipMode.Split, false, out var pieces);
        Assert.True(r.Success, r.Message);
        Assert.Equal(4, pieces.Count);
        Assert.Equal(a, pieces[0]);
        Assert.Equal(b, pieces[2]);
        Assert.Equal(pieces, ed.Brushes.Select(x => x.Uid).ToArray());
        Assert.Empty(ed.SelectedBrushes);
        Assert.Equal("Clip: split", doc.Undo.Current.Description);

        OpResult miss = ed.Clip(new[] { a }, new Vec3(50, 0, 0), new Vec3(1, 0, 0), ClipMode.Cut, false, out var none);
        Assert.False(miss.Success);
        Assert.Empty(none);
    }

    [Fact]
    public void Clip_Selection_Path_Is_Unchanged()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        Assert.Equal("Select a brush to clip.", ed.Clip(Vec3.Zero, new Vec3(1, 0, 0), ClipMode.Cut, false).Message);
        int a = Box(ed, Vec3.Zero);
        Box(ed, new Vec3(5, 0, 0));
        Assert.Equal("Select a brush to clip.", ed.Clip(Vec3.Zero, new Vec3(1, 0, 0), ClipMode.Cut, false).Message);

        ed.SelectBrush(a);
        OpResult cut = ed.Clip(Vec3.Zero, new Vec3(1, 0, 0), ClipMode.Cut, false);
        Assert.True(cut.Success, cut.Message);
        Assert.Equal(2, ed.Brushes.Count);
        Assert.True(ed.FindBrush(a)!.Geometry.Vertices.Max(v => v.X) <= 1e-4f);
        Assert.Equal(new[] { a }, ed.SelectedBrushes.ToArray());
        Assert.Equal("Clip: cut", doc.Undo.Current.Description);
    }

    [Fact]
    public void Duplicate_Clones_In_Given_Order_Without_Clipboard_Or_Selection()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = Box(ed, new Vec3(0, 0, 0));
        int b = Box(ed, new Vec3(4, 0, 0));
        ed.SetBrushLocked(new[] { b }, true);
        ed.SelectBrush(a);

        var copies = ed.Duplicate(new[] { b, a, b, 777 }, new Vec3(0, 10, 0));
        Assert.Equal(2, copies.Count);
        Assert.Equal("Duplicate 2 brush(es)", doc.Undo.Current.Description);
        Assert.Equal(new[] { a, b, copies[0], copies[1] }, ed.Brushes.Select(x => x.Uid).ToArray());
        Assert.Equal(new Vec3(4, 10, 0), ed.FindBrush(copies[0])!.Position);
        Assert.Equal(BrushState.Normal, ed.FindBrush(copies[0])!.State);
        Assert.NotSame(ed.FindBrush(b)!.Geometry, ed.FindBrush(copies[0])!.Geometry);
        Assert.False(ed.HasClipboard);
        Assert.Equal(new[] { a }, ed.SelectedBrushes.ToArray());

        doc.Undo.Undo();
        Assert.Equal(2, ed.Brushes.Count);
        Assert.Empty(ed.Duplicate(Array.Empty<int>(), Vec3.Zero));
    }

    [Fact]
    public void Paste_Keeps_Its_Description_And_Clipboard()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = Box(ed, Vec3.Zero);
        ed.SelectBrush(a);
        ed.CopySelected();
        var pasted = ed.Paste(new Vec3(1, 0, 0));
        Assert.Single(pasted);
        Assert.Equal("Paste 1 brush(es)", doc.Undo.Current.Description);
        Assert.True(ed.HasClipboard);
        Assert.Equal(new Vec3(1, 0, 0), ed.FindBrush(pasted[0])!.Position);
    }

    [Fact]
    public void NotifyExternalBrushChange_Raises_BrushesChanged()
    {
        var ed = new BrushEditor(EmptyDoc());
        int fired = 0;
        ed.BrushesChanged += () => fired++;
        ed.NotifyExternalBrushChange();
        Assert.Equal(1, fired);
        Assert.Null(ed.LastChangedBrushUids);
    }
}
