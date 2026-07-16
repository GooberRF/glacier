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
/// The mandatory <see cref="SelectionRouter"/> is the single gated entry point for every
/// selection mutation. These tests drive the router the way each real entry point does — a
/// simulated hit of a given kind under a given mode's active chips — and assert the strict
/// contract: a selection whose kind the mode/chips do not permit is DROPPED (no store
/// mutation, a Dropped notification), so out-of-mode selection is impossible. Includes the
/// user's exact repros (object clicked in Brush mode, face clicked in Object mode) and the
/// chip-lifecycle rule (chips equal the mode default at start and after every mode switch).
/// </summary>
public sealed class SelectionRouterTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "t.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private sealed class Fixture
    {
        public EditorDocument Doc = null!;
        public BrushEditor Be = null!;
        public SelectionRouter Router = null!;
        public LevelObject Obj = null!;
        public int BrushUid;
        public SelectKinds Active = SelectKinds.Objects;
        public readonly List<SelectKinds> Dropped = new();
    }

    private static Fixture NewFixture()
    {
        var doc = EmptyDoc();
        var be = new BrushEditor(doc);
        int brush = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Item, new Vec3(4, 0, 0))!;

        var f = new Fixture { Doc = doc, Be = be, Obj = obj, BrushUid = brush };
        f.Router = new SelectionRouter(() => f.Doc, () => f.Be, () => f.Active, k => f.Dropped.Add(k));
        return f;
    }

    // ---- Per-mode matrix: only the mode's own kind (plus group members in Group mode) selects ----

    [Theory]
    [InlineData(SelectKinds.Objects)] // Object mode
    [InlineData(SelectKinds.Brushes)] // Brush mode
    [InlineData(SelectKinds.Faces)]   // Face mode
    [InlineData(SelectKinds.Vertices)] // Vertex mode
    [InlineData(SelectKinds.Edges)]   // Edge mode
    [InlineData(SelectKinds.Groups)]  // Group mode
    public void Only_Permitted_Kinds_Select_Under_Each_Modes_Default_Chip(SelectKinds mode)
    {
        Fixture f = NewFixture();
        f.Active = mode;

        bool obj = f.Router.SelectObject(f.Obj);
        bool brush = f.Router.SelectBrush(f.BrushUid);
        bool face = f.Router.SelectFace(f.BrushUid, 0);
        bool vert = f.Router.SelectVertex(f.BrushUid, 0);
        bool edge = f.Router.SelectEdge(f.BrushUid, 0, 1);

        // Objects & whole brushes are also group members (Group mode widens their gate).
        Assert.Equal(mode is SelectKinds.Objects or SelectKinds.Groups, obj);
        Assert.Equal(mode is SelectKinds.Brushes or SelectKinds.Groups, brush);
        Assert.Equal(mode == SelectKinds.Faces, face);
        Assert.Equal(mode == SelectKinds.Vertices, vert);
        Assert.Equal(mode == SelectKinds.Edges, edge);

        // The stores only ever hold the permitted kinds.
        Assert.Equal(obj, f.Doc.IsSelected(f.Obj));
        Assert.Equal(brush, f.Be.SelectedBrushes.Contains(f.BrushUid));
        Assert.Equal(face, f.Be.SelectedFaces.Contains((f.BrushUid, 0)));
    }

    // ---- The user's exact repros ----

    [Fact]
    public void Object_Clicked_In_Brush_Mode_Is_A_NoOp()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Brushes; // Brush mode, strict default chips

        bool selected = f.Router.SelectObject(f.Obj);

        Assert.False(selected);
        Assert.Empty(f.Doc.Selection);
        Assert.Contains(SelectKinds.Objects, f.Dropped);
    }

    [Fact]
    public void Face_Clicked_In_Object_Mode_Is_A_NoOp()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects; // Object mode, strict default chips

        bool selected = f.Router.SelectFace(f.BrushUid, 0);

        Assert.False(selected);
        Assert.Empty(f.Be.SelectedFaces);
        Assert.Contains(SelectKinds.Faces, f.Dropped);
    }

    // ---- Ctrl+chip multi-kind opt-in permits the added kind ----

    [Fact]
    public void Ctrl_Added_Kind_Is_Permitted_Alongside_The_Primary()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects | SelectKinds.Brushes; // Object mode + Ctrl+Brushes chip

        Assert.True(f.Router.SelectObject(f.Obj));
        Assert.True(f.Router.SelectBrush(f.BrushUid));
        Assert.Empty(f.Dropped);
    }

    // ---- A dropped selection leaves an existing selection untouched ----

    [Fact]
    public void A_Dropped_Selection_Does_Not_Disturb_The_Current_Selection()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Router.SelectObject(f.Obj)); // legitimately selected in Object mode

        f.Active = SelectKinds.Brushes; // now in Brush mode
        Assert.False(f.Router.SelectObject(f.Obj)); // a stray object request is dropped

        Assert.True(f.Doc.IsSelected(f.Obj)); // the prior selection survives untouched
    }

    // ---- Chip-lifecycle: chips equal the mode default at start and after every switch ----

    [Theory]
    [InlineData(EditMode.Object, SelectKinds.Objects)]
    [InlineData(EditMode.Brush, SelectKinds.Brushes)]
    [InlineData(EditMode.Face, SelectKinds.Faces)]
    [InlineData(EditMode.Vertex, SelectKinds.Vertices)]
    [InlineData(EditMode.Edge, SelectKinds.Edges)]
    [InlineData(EditMode.Group, SelectKinds.Groups)]
    public void Fresh_Filter_Equals_The_Mode_Strict_Default(EditMode mode, SelectKinds expected)
    {
        // A fresh SelectionFilter models "app start" — chips are exactly the mode default,
        // never a rehydrated multi-kind state (chips are not disk-persisted).
        var filter = new SelectionFilter(mode);
        Assert.Equal(expected, filter.Active);
    }

    [Fact]
    public void Mode_Switch_Resets_Chips_To_The_Strict_Default_Dropping_Ctrl_Additions()
    {
        var filter = new SelectionFilter(EditMode.Object);
        filter.ToggleAdditional(SelectKinds.Brushes); // Ctrl+chip multi-kind add
        Assert.Equal(SelectKinds.Objects | SelectKinds.Brushes, filter.Active);

        filter.SyncFromMode(EditMode.Brush); // any mode switch
        Assert.Equal(SelectKinds.Brushes, filter.Active); // addition dropped, strict default only

        filter.SyncFromMode(EditMode.Object);
        Assert.Equal(SelectKinds.Objects, filter.Active);
    }
}
