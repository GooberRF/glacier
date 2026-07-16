using System;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The manipulation math driven through the shared <see cref="SnapPolicy"/> (magnet +
/// Alt-invert) exactly as the gizmo drag does, plus the ESC-cancel transaction
/// rollback and the single-undo-entry commit on release.
/// </summary>
public sealed class GizmoDragTests
{
    private const float Eps = 1e-3f;

    // ---- Snap through the gizmo math ------------------------------------------

    [Fact]
    public void Axis_Move_Snaps_The_Pivot_To_The_Grid()
    {
        var snap = new SnapPolicy { Enabled = true, GridSize = 1f };
        Vec3 axis = new(1, 0, 0);
        Vec3 pivot = Vec3.Zero;

        float s0 = GizmoMath.ClosestAxisParam(pivot, axis, new Vec3(0, 10, 0), new Vec3(0, -1, 0));
        float s1 = GizmoMath.ClosestAxisParam(pivot, axis, new Vec3(3.7f, 10, 0), new Vec3(0, -1, 0));
        Vec3 accum = axis.Scale(s1 - s0);

        Vec3 snapped = snap.MovedPivot(pivot, accum, invert: false);
        Assert.True(snapped.ApproxEquals(new Vec3(4, 0, 0), Eps));

        // Alt-invert makes the same drag free/continuous through the identical path.
        Vec3 free = snap.MovedPivot(pivot, accum, invert: true);
        Assert.True(free.ApproxEquals(new Vec3(3.7f, 0, 0), Eps));
    }

    [Fact]
    public void Ring_Rotate_Snaps_To_The_Rotation_Step_And_Builds_The_Matrix()
    {
        var snap = new SnapPolicy { Enabled = true, RotationStepDegrees = 15f };
        Vec3 axisZ = new(0, 0, 1);
        Vec3 d0 = new(1, 0, 0);
        Vec3 d1 = new(MathF.Cos(0.6f), MathF.Sin(0.6f), 0); // ~34.38°

        float sweptDeg = GizmoMath.SignedAngle(d0, d1, axisZ) * 180f / MathF.PI;
        float snappedDeg = snap.RotationDegrees(sweptDeg, invert: false);
        Assert.Equal(30f, snappedDeg, 3);

        Mat3 rot = Mat3Math.FromAxisAngle(axisZ, TransformMath.DegToRad(snappedDeg));
        Vec3 rotated = rot.Transform(new Vec3(2, 0, 0));
        Assert.True(rotated.ApproxEquals(new Vec3(1.7320508f, 1.0f, 0), Eps)); // 2·(cos30,sin30)
    }

    [Fact]
    public void Axis_Scale_Snaps_To_The_Scale_Step()
    {
        var snap = new SnapPolicy { Enabled = true, ScaleStep = 0.05f };
        float factor = GizmoMath.AxisScaleFactor(startParam: 4f, currentParam: 4.53f); // 1.1325
        float snapped = snap.ScaleFactor(factor, invert: false);
        Assert.Equal(1.15f, snapped, 4);
    }

    // ---- ESC cancel / commit-one-undo-entry -----------------------------------

    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xB4;
        rfl.Header.LevelName = "gizmodrag";
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Esc_During_Drag_Rolls_Back_And_Leaves_The_Document_Unchanged()
    {
        var doc = NewDoc();
        LevelObject a = doc.PlaceObject(LevelObjectKind.Entity, new Vec3(0, 0, 0))!;
        LevelObject b = doc.PlaceObject(LevelObjectKind.Entity, new Vec3(10, 0, 0))!;
        Vec3 beforeA = a.Position;
        Vec3 beforeB = b.Position;
        int posBefore = doc.Undo.Position;

        UndoStack.Transaction tx = doc.Undo.BeginTransaction("Move (gizmo)");
        doc.EditValue(a.Section, "Move", a.Position, a.Position.Add(new Vec3(5, 0, 0)), v => a.Position = v);
        doc.EditValue(a.Section, "Move", a.Position, a.Position.Add(new Vec3(2, 0, 0)), v => a.Position = v);
        Assert.True(a.Position.ApproxEquals(new Vec3(7, 0, 0), Eps)); // moved mid-drag

        tx.Rollback(); // ESC

        Assert.True(a.Position.ApproxEquals(beforeA, Eps));
        Assert.True(b.Position.ApproxEquals(beforeB, Eps));
        Assert.Equal(posBefore, doc.Undo.Position); // no undo entry left behind
    }

    [Fact]
    public void Release_Commits_Exactly_One_Undo_Entry()
    {
        var doc = NewDoc();
        LevelObject a = doc.PlaceObject(LevelObjectKind.Entity, new Vec3(0, 0, 0))!;
        Vec3 before = a.Position;
        int posBefore = doc.Undo.Position;

        UndoStack.Transaction tx = doc.Undo.BeginTransaction("Move (gizmo)");
        doc.EditValue(a.Section, "Move", a.Position, a.Position.Add(new Vec3(3, 0, 0)), v => a.Position = v);
        doc.EditValue(a.Section, "Move", a.Position, a.Position.Add(new Vec3(1, 0, 0)), v => a.Position = v);
        tx.Commit(); // mouse release

        Assert.Equal(posBefore + 1, doc.Undo.Position); // one entry for the whole drag
        Assert.True(a.Position.ApproxEquals(new Vec3(4, 0, 0), Eps));

        doc.Undo.Undo();
        Assert.True(a.Position.ApproxEquals(before, Eps));
    }
}
