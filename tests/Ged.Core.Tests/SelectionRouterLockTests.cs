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
/// Feature G — real lock enforcement at the <see cref="SelectionRouter"/> chokepoint. Locked
/// brushes/objects are UNSELECTABLE through every router entry path (click, batch, invert, select-all,
/// by-UID, unit); a click resolving only to a locked hit selects nothing and raises
/// <see cref="SelectionRouter.LockBlocked"/> (the shell's "Locked — unlock to select." hint), while
/// batch paths silently skip locked items. Also covers lock-deselects-current for both object and
/// brush lock.
/// </summary>
public sealed class SelectionRouterLockTests
{
    private sealed class Fixture
    {
        public EditorDocument Doc = null!;
        public BrushEditor Be = null!;
        public SelectionRouter Router = null!;
        public LevelObject Obj = null!;
        public LevelObject Obj2 = null!;
        public int BrushUid;
        public SelectKinds Active = SelectKinds.Objects;
        public int LockBlockedCount;
    }

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static Fixture NewFixture()
    {
        var doc = EmptyDoc();
        var be = new BrushEditor(doc);
        int brush = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Item, new Vec3(4, 0, 0))!;
        LevelObject obj2 = doc.PlaceObject(LevelObjectKind.Item, new Vec3(8, 0, 0))!;

        var f = new Fixture { Doc = doc, Be = be, Obj = obj, Obj2 = obj2, BrushUid = brush };
        f.Router = new SelectionRouter(() => f.Doc, () => f.Be, () => f.Active);
        f.Router.LockBlocked += () => f.LockBlockedCount++;
        return f;
    }

    // ---- Click-select refuses locked items -----------------------------------

    [Fact]
    public void Locked_Object_Is_Unselectable_And_Hints()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        f.Doc.ToggleLock(f.Obj); // lock it

        Assert.False(f.Router.SelectObject(f.Obj));
        Assert.False(f.Doc.IsSelected(f.Obj));
        Assert.Equal(1, f.LockBlockedCount);
    }

    [Fact]
    public void Locked_Brush_Is_Unselectable_And_Hints()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Brushes;
        f.Be.SetBrushLocked(new[] { f.BrushUid }, locked: true);

        Assert.False(f.Router.SelectBrush(f.BrushUid));
        Assert.DoesNotContain(f.BrushUid, f.Be.SelectedBrushes);
        Assert.Equal(1, f.LockBlockedCount);
    }

    [Fact]
    public void Sub_Geometry_Of_A_Locked_Brush_Is_Off_Limits()
    {
        Fixture f = NewFixture();
        f.Be.SetBrushLocked(new[] { f.BrushUid }, locked: true);

        f.Active = SelectKinds.Faces;
        Assert.False(f.Router.SelectFace(f.BrushUid, 0));
        f.Active = SelectKinds.Vertices;
        Assert.False(f.Router.SelectVertex(f.BrushUid, 0));
        f.Active = SelectKinds.Edges;
        Assert.False(f.Router.SelectEdge(f.BrushUid, 0, 1));

        Assert.Empty(f.Be.SelectedFaces);
        Assert.Empty(f.Be.SelectedVertices);
        Assert.Empty(f.Be.SelectedEdges);
    }

    [Fact]
    public void A_Lock_Blocked_Click_Leaves_The_Existing_Selection_Untouched()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Router.SelectObject(f.Obj2)); // a legit selection stands

        f.Doc.ToggleLock(f.Obj);
        Assert.False(f.Router.SelectObject(f.Obj)); // the locked click is refused

        Assert.True(f.Doc.IsSelected(f.Obj2)); // prior selection survives
    }

    [Fact]
    public void Toggle_And_By_Uid_Also_Refuse_A_Locked_Object()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        f.Doc.ToggleLock(f.Obj);

        Assert.False(f.Router.ToggleObject(f.Obj));
        Assert.Null(f.Router.SelectObjectByUid(f.Obj.Uid));
        Assert.False(f.Doc.IsSelected(f.Obj));
    }

    // ---- Batch paths skip locked silently ------------------------------------

    [Fact]
    public void Select_All_Excludes_Locked_Objects()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        f.Doc.ToggleLock(f.Obj);

        Assert.True(f.Router.SelectAllObjects());
        Assert.False(f.Doc.IsSelected(f.Obj));
        Assert.True(f.Doc.IsSelected(f.Obj2));
    }

    [Fact]
    public void Invert_Excludes_Locked_Objects()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Router.SelectObject(f.Obj2)); // Obj2 selected → invert should target Obj
        f.Doc.ToggleLock(f.Obj);                    // …but Obj is locked

        Assert.True(f.Router.InvertObjects());
        Assert.False(f.Doc.IsSelected(f.Obj));  // locked, excluded from the inversion
        Assert.False(f.Doc.IsSelected(f.Obj2)); // was selected, now deselected by the invert
    }

    [Fact]
    public void Batch_Object_Select_Skips_Locked_Members()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        f.Doc.ToggleLock(f.Obj);

        Assert.True(f.Router.SelectObjects(new[] { f.Obj, f.Obj2 }));
        Assert.False(f.Doc.IsSelected(f.Obj));
        Assert.True(f.Doc.IsSelected(f.Obj2));
    }

    // ---- Lock removes the item from the current selection --------------------

    [Fact]
    public void Locking_A_Selected_Object_Removes_It_From_The_Selection()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Router.SelectObject(f.Obj));
        Assert.True(f.Doc.IsSelected(f.Obj));

        f.Doc.LockSelected();                  // stock Q on the selection
        Assert.False(f.Doc.IsSelected(f.Obj)); // deselected as part of the lock
        Assert.True(f.Doc.IsLocked(f.Obj));
    }

    [Fact]
    public void Toggle_Lock_On_A_Selected_Object_Deselects_It()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Router.SelectObject(f.Obj));

        f.Doc.ToggleLock(f.Obj);
        Assert.False(f.Doc.IsSelected(f.Obj));
        Assert.True(f.Doc.IsLocked(f.Obj));

        // Unlocking does not re-select it.
        f.Doc.ToggleLock(f.Obj);
        Assert.False(f.Doc.IsSelected(f.Obj));
        Assert.False(f.Doc.IsLocked(f.Obj));
    }

    [Fact]
    public void Locking_A_Selected_Brush_Removes_It_From_The_Selection()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Brushes;
        Assert.True(f.Router.SelectBrush(f.BrushUid));
        Assert.Contains(f.BrushUid, f.Be.SelectedBrushes);

        f.Be.SetBrushLocked(new[] { f.BrushUid }, locked: true);
        Assert.DoesNotContain(f.BrushUid, f.Be.SelectedBrushes);
        Assert.True(f.Be.IsBrushLocked(f.BrushUid));
    }

    // ---- Unlocked items still select normally (no over-blocking) -------------

    [Fact]
    public void Unlocked_Items_Select_Normally()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Router.SelectObject(f.Obj));
        Assert.True(f.Doc.IsSelected(f.Obj));
        Assert.Equal(0, f.LockBlockedCount);
    }
}
