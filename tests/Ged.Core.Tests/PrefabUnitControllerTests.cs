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
/// Feature F — prefab-instance UNIT selection. The controller state machine + the mixed-kind unit
/// gate through <see cref="SelectionRouter"/>: a member click selects the WHOLE instance (all member
/// brushes + objects), a double-click enters the instance for member editing and ESC/exit returns to
/// unit level, a rigid unit transform freshens the pose record in ONE undo step, and (Feature G) an
/// instance with a locked member is unselectable as a unit.
/// </summary>
public sealed class PrefabUnitControllerTests
{
    private sealed class Fixture
    {
        public EditorDocument Doc = null!;
        public BrushEditor Be = null!;
        public PrefabInstanceService Svc = null!;
        public SelectionRouter Router = null!;
        public PrefabUnitController Unit = null!;
        public PrefabInstanceRecord Rec = null!;
        public int B1;
        public int B2;
        public LevelObject Obj = null!;
        public SelectKinds Active = SelectKinds.Objects;
    }

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    /// <summary>An instance whose members are two brushes and one object (the mixed-kind case).</summary>
    private static Fixture NewFixture()
    {
        var doc = EmptyDoc();
        var be = new BrushEditor(doc);
        int b1 = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, new Vec3(0, 0, 0), Mat3.Identity);
        int b2 = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, new Vec3(2, 0, 0), Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Item, new Vec3(1, 0, 0))!;

        var svc = new PrefabInstanceService(doc);
        PrefabInstanceRecord rec = svc.RecordInstance("sample", "h", new[] { b1, b2, obj.Uid }, new Vec3(1, 0, 0), Mat3.Identity);

        var f = new Fixture { Doc = doc, Be = be, Svc = svc, Rec = rec, B1 = b1, B2 = b2, Obj = obj };
        f.Router = new SelectionRouter(() => f.Doc, () => f.Be, () => f.Active);
        f.Unit = new PrefabUnitController(svc, doc, be, f.Router);
        return f;
    }

    // ---- Unit selection ------------------------------------------------------

    [Fact]
    public void Click_On_A_Member_Selects_The_Whole_Instance_As_A_Unit()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects; // Object mode: clicking the object member

        PrefabUnitController.ClickOutcome outcome = f.Unit.ClickMember(f.Obj.Uid, doubleClick: false);

        Assert.Equal(PrefabUnitController.ClickOutcome.UnitSelected, outcome);
        Assert.Equal(f.Rec.InstanceId, f.Unit.UnitInstanceId);

        // Every member is selected — brushes AND the object — even though only Objects is lit
        // (mixed-kind unit gate, modelled on group selection).
        Assert.Contains(f.B1, f.Be.SelectedBrushes);
        Assert.Contains(f.B2, f.Be.SelectedBrushes);
        Assert.True(f.Doc.IsSelected(f.Obj));
    }

    [Fact]
    public void A_Brush_Member_Click_Also_Selects_The_Unit_In_Brush_Mode()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Brushes;

        Assert.Equal(PrefabUnitController.ClickOutcome.UnitSelected, f.Unit.ClickMember(f.B1, doubleClick: false));
        Assert.True(f.Doc.IsSelected(f.Obj));
        Assert.Contains(f.B2, f.Be.SelectedBrushes);
    }

    [Fact]
    public void Non_Member_Uid_Is_Not_Handled()
    {
        Fixture f = NewFixture();
        LevelObject stray = f.Doc.PlaceObject(LevelObjectKind.Item, new Vec3(50, 0, 0))!;
        Assert.Equal(PrefabUnitController.ClickOutcome.NotHandled, f.Unit.ClickMember(stray.Uid, doubleClick: false));
        Assert.Null(f.Unit.UnitInstanceId);
    }

    // ---- Enter / exit member editing -----------------------------------------

    [Fact]
    public void Double_Click_Enters_Member_Mode_And_Exit_Returns_To_Unit()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;

        Assert.Equal(PrefabUnitController.ClickOutcome.UnitSelected, f.Unit.ClickMember(f.Obj.Uid, doubleClick: false));

        // Double-click ENTERS: unit state clears, entered state set.
        Assert.Equal(PrefabUnitController.ClickOutcome.EnteredMember, f.Unit.ClickMember(f.Obj.Uid, doubleClick: true));
        Assert.Equal(f.Rec.InstanceId, f.Unit.EnteredInstanceId);
        Assert.Null(f.Unit.UnitInstanceId);

        // Inside, member clicks are individual (NotHandled → normal per-kind selection).
        Assert.Equal(PrefabUnitController.ClickOutcome.NotHandled, f.Unit.ClickMember(f.Obj.Uid, doubleClick: false));

        // Exit (ESC / empty click) returns to unit level and re-selects the whole instance.
        Assert.True(f.Unit.ExitToUnit());
        Assert.Null(f.Unit.EnteredInstanceId);
        Assert.Equal(f.Rec.InstanceId, f.Unit.UnitInstanceId);
        Assert.True(f.Doc.IsSelected(f.Obj));
        Assert.Contains(f.B1, f.Be.SelectedBrushes);
    }

    [Fact]
    public void Exit_Is_A_NoOp_When_Not_Entered()
    {
        Fixture f = NewFixture();
        Assert.False(f.Unit.ExitToUnit());
    }

    // ---- Rigid unit transform freshens the pose record in ONE undo step -------

    [Fact]
    public void Rigid_Unit_Translate_Moves_All_Members_And_Pose_In_One_Undo_Step()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Unit.SelectUnit(f.Rec.InstanceId));

        Vec3 poseBefore = f.Svc.ById(f.Rec.InstanceId)!.PivotPosition;
        Vec3 objBefore = f.Doc.FindByUid(f.Obj.Uid)!.Position;
        Vec3 brushBefore = f.Be.FindBrush(f.B1)!.Position;
        int posBefore = f.Doc.Undo.Position;

        var delta = new Vec3(3, 4, 5);
        Assert.True(f.Unit.RigidTransformUnit(Mat3.Identity, delta, Vec3.Zero));

        // Pose record + every member moved by the delta…
        Assert.True(f.Svc.ById(f.Rec.InstanceId)!.PivotPosition.ApproxEquals(poseBefore.Add(delta)));
        Assert.True(f.Doc.FindByUid(f.Obj.Uid)!.Position.ApproxEquals(objBefore.Add(delta)));
        Assert.True(f.Be.FindBrush(f.B1)!.Position.ApproxEquals(brushBefore.Add(delta)));

        // …in exactly ONE undo step, which fully reverts.
        Assert.Equal(posBefore + 1, f.Doc.Undo.Position);
        f.Doc.Undo.Undo();
        Assert.True(f.Svc.ById(f.Rec.InstanceId)!.PivotPosition.ApproxEquals(poseBefore));
        Assert.True(f.Doc.FindByUid(f.Obj.Uid)!.Position.ApproxEquals(objBefore));
        Assert.True(f.Be.FindBrush(f.B1)!.Position.ApproxEquals(brushBefore));
    }

    [Fact]
    public void Rigid_Unit_Rotate_Composes_Into_The_Pose_Orientation_In_One_Step()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Unit.SelectUnit(f.Rec.InstanceId));

        int posBefore = f.Doc.Undo.Position;
        Mat3 rot = Mat3Math.RotationY(MathF.PI / 2f);
        Assert.True(f.Unit.RigidTransformUnit(rot, Vec3.Zero, f.Svc.ById(f.Rec.InstanceId)!.PivotPosition));

        Assert.True(f.Svc.ById(f.Rec.InstanceId)!.PivotRotation.ApproxEquals(rot));
        Assert.Equal(posBefore + 1, f.Doc.Undo.Position);
    }

    [Fact]
    public void Rigid_Transform_Without_A_Selected_Unit_Is_A_NoOp()
    {
        Fixture f = NewFixture();
        Assert.False(f.Unit.RigidTransformUnit(Mat3.Identity, new Vec3(1, 0, 0), Vec3.Zero));
    }

    // ---- Feature G: a locked member makes the instance unselectable as a unit --

    [Fact]
    public void An_Instance_With_A_Locked_Member_Cannot_Be_Selected_As_A_Unit()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        f.Be.SetBrushLocked(new[] { f.B1 }, locked: true);

        Assert.False(f.Unit.CanSelectAsUnit(f.Rec.InstanceId));
        Assert.Equal(PrefabUnitController.ClickOutcome.UnitBlockedLocked, f.Unit.ClickMember(f.Obj.Uid, doubleClick: false));
        Assert.Null(f.Unit.UnitInstanceId);
        Assert.False(f.Doc.IsSelected(f.Obj)); // nothing selected — all-or-nothing
    }

    [Fact]
    public void Removing_The_Instance_Invalidates_Unit_State()
    {
        Fixture f = NewFixture();
        f.Active = SelectKinds.Objects;
        Assert.True(f.Unit.SelectUnit(f.Rec.InstanceId));
        Assert.True(f.Svc.Orphan(f.Rec.InstanceId));

        Assert.True(f.Unit.ValidateExisting());
        Assert.Null(f.Unit.UnitInstanceId);
        Assert.Null(f.Unit.UnitRecord);
    }
}
