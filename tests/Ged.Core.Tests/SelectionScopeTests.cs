using System;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Mode / selection-filter switches must drop any selection whose kind the new filter no
/// longer allows, so a selection made under one granularity cannot be transformed under
/// an incompatible mode (the reported brush-selected-then-Object-mode bug). Selections of
/// kinds still enabled — including several at once via the multi-kind filter — survive.
/// </summary>
public sealed class SelectionScopeTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static (EditorDocument Doc, BrushEditor Ed) SetupAllKinds()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int b = ed.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Light, new Vec3(5, 5, 5))!;

        // One selection of every kind, all live at once.
        ed.ClearSelection();
        doc.ClearSelection();
        ed.SelectBrush(b);
        ed.SelectFace(b, 0);
        ed.SelectVertex(b, 0);
        doc.Select(obj);

        Assert.Single(ed.SelectedBrushes);
        Assert.Single(ed.SelectedFaces);
        Assert.Single(ed.SelectedVertices);
        Assert.Single(doc.Selection);
        return (doc, ed);
    }

    [Theory]
    [InlineData(EditMode.Object, false, false, false, true)]
    [InlineData(EditMode.Group, false, false, false, true)]
    [InlineData(EditMode.Brush, true, false, false, false)]
    [InlineData(EditMode.Face, false, true, false, false)] // Face (incl. the Texture/UV tab) picks faces
    [InlineData(EditMode.Vertex, false, false, true, false)]
    public void Switching_Into_A_Mode_Keeps_Only_That_Modes_Kind(
        EditMode target, bool brush, bool face, bool vertex, bool obj)
    {
        (EditorDocument doc, BrushEditor ed) = SetupAllKinds();

        // Mirror ApplyMode: the chips follow the mode exclusively, then invalid clears.
        var filter = new SelectionFilter();
        filter.SyncFromMode(target);
        SelectionScope.ClearInvalid(filter.Active, ed, doc);

        Assert.Equal(brush, ed.SelectedBrushes.Count > 0);
        Assert.Equal(face, ed.SelectedFaces.Count > 0);
        Assert.Equal(vertex, ed.SelectedVertices.Count > 0);
        Assert.Equal(obj, doc.Selection.Count > 0);
    }

    [Fact]
    public void Multi_Kind_Filter_Keeps_Every_Enabled_Kind()
    {
        (EditorDocument doc, BrushEditor ed) = SetupAllKinds();

        // Brush mode + Ctrl-add Faces: both survive; vertices and objects clear.
        var filter = new SelectionFilter();
        filter.SyncFromMode(EditMode.Brush);
        filter.ToggleAdditional(SelectKinds.Faces);
        Assert.Equal(SelectKinds.Brushes | SelectKinds.Faces, filter.Active);

        SelectionScope.ClearInvalid(filter.Active, ed, doc);

        Assert.Single(ed.SelectedBrushes);
        Assert.Single(ed.SelectedFaces);
        Assert.Empty(ed.SelectedVertices);
        Assert.Empty(doc.Selection);
    }

    [Fact]
    public void Reported_Bug_Brush_Selected_Then_Object_Mode_Clears_The_Brush()
    {
        // Exact repro: a brush selected in Brush mode must not remain a transform target
        // after switching to Object mode.
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int b = ed.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        ed.ClearSelection();
        ed.SelectBrush(b);
        Assert.Single(ed.SelectedBrushes);

        var filter = new SelectionFilter();
        filter.SyncFromMode(EditMode.Object);
        SelectionScope.ClearInvalid(filter.Active, ed, doc);

        // No brush remains selected, so a move/rotate in Object mode has nothing to act on.
        Assert.Empty(ed.SelectedBrushes);
        Assert.Empty(doc.Selection);
    }
}
