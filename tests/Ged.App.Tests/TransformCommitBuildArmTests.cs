using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Q3 — after committing a brush TRANSFORM, the background live-CSG preview build must arm so the merged
/// brushwork stash (the compiled world the moved brush's OLD location still draws from) refreshes — even
/// on levels ABOVE <see cref="GeometryBuildController.LivePreviewBrushLimit"/> (ctf06 has 955 brushes).
/// The per-keystroke debounce keeps the small-level cap; only the transform-commit arm is uncapped.
/// </summary>
public sealed class TransformCommitBuildArmTests
{
    private static GeometryBuildController Controller(EditorSession session) =>
        new(session, _ => { }, () => { }, (_, _) => { });

    private static EditorSession SessionWith(int brushCount)
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        for (int i = 0; i < brushCount; i++)
        {
            be.CreateBrush(new BrushCreateParams { Width = 2, Height = 2, Depth = 2 },
                new Vec3(i * 4, 0, 0), Mat3.Identity);
        }

        return session;
    }

    [AvaloniaFact]
    public void PostTransformBuild_Arms_Above_The_Cap_While_The_Capped_Path_Does_Not()
    {
        // A level well ABOVE the 350-brush cap (ctf06-class).
        EditorSession session = SessionWith(GeometryBuildController.LivePreviewBrushLimit + 5);
        GeometryBuildController bc = Controller(session);
        bc.InvalidateGeometry(); // geometry dirty, but nothing armed yet
        Assert.False(bc.LivePreviewPending);

        // The per-keystroke / drag-cancel path respects the cap: on a big level it does NOT arm.
        bc.ArmLivePreviewIfPending();
        Assert.False(bc.LivePreviewPending);

        // The transform-commit path IGNORES the cap: it arms the background build on any size level.
        bc.ArmPostTransformBuild();
        Assert.True(bc.LivePreviewPending);
    }

    [AvaloniaFact]
    public void On_A_Small_Level_Both_Paths_Arm()
    {
        EditorSession session = SessionWith(4); // well under the cap
        GeometryBuildController bc = Controller(session);
        bc.InvalidateGeometry();

        bc.ArmLivePreviewIfPending();
        Assert.True(bc.LivePreviewPending);
    }

    [AvaloniaFact]
    public void PostTransformBuild_Is_A_No_Op_When_Nothing_Is_Dirty()
    {
        EditorSession session = SessionWith(GeometryBuildController.LivePreviewBrushLimit + 5);
        GeometryBuildController bc = Controller(session);
        // No InvalidateGeometry: GeometryDirty is false, so a spurious commit arms nothing.
        bc.ArmPostTransformBuild();
        Assert.False(bc.LivePreviewPending);
    }

    // ---- Item 3: undo/redo of a brush move re-arms the uncapped rebuild -----------------------------

    [AvaloniaFact]
    public void Undoing_A_Brush_Move_On_A_Big_Level_Arms_The_Post_Transform_Build()
    {
        // Mirrors the Q3 commit-arm case for UNDO: on a level ABOVE the cap, undoing a brush move must
        // re-arm the uncapped background rebuild so the merged/compiled world the moved brush's OLD spot
        // draws from refreshes (before this fix the live CSG preview stayed stale after undo).
        EditorSession session = SessionWith(GeometryBuildController.LivePreviewBrushLimit + 5);
        BrushEditor be = session.BrushEditor!;
        GeometryBuildController bc = Controller(session);
        bc.Attach(); // subscribe OnBrushesChanged → GeometryDirty

        int uid = be.Brushes.First().Uid;
        be.EditBrushesCoalesced(new[] { uid }, "Move",
            b => { BrushTransform.Move(b, new Vec3(4, 0, 0)); return OpResult.Ok(); }, coalesceKey: "drag");
        // The move itself respects the cap on a big level: it did NOT arm.
        Assert.False(bc.LivePreviewPending);

        bc.ApplyUndoRedo(redo: false, coalesce: true);

        Assert.True(bc.LivePreviewPending); // the undo re-armed the uncapped build
        Assert.True(be.FindBrush(uid)!.Position.ApproxEquals(new Vec3(0, 0, 0))); // and landed at pre-move
    }

    [AvaloniaFact]
    public void Redoing_A_Brush_Move_On_A_Big_Level_Also_Arms()
    {
        EditorSession session = SessionWith(GeometryBuildController.LivePreviewBrushLimit + 5);
        BrushEditor be = session.BrushEditor!;
        GeometryBuildController bc = Controller(session);
        bc.Attach();

        int uid = be.Brushes.First().Uid;
        be.EditBrushesCoalesced(new[] { uid }, "Move",
            b => { BrushTransform.Move(b, new Vec3(0, 5, 0)); return OpResult.Ok(); }, coalesceKey: "drag");
        bc.ApplyUndoRedo(redo: false, coalesce: true); // undo (arms)
        // Rebuild would clear GeometryDirty in the real app; simulate a settled state so redo's arm is the
        // one under test.
        bc.ApplyUndoRedo(redo: true, coalesce: true);

        Assert.True(bc.LivePreviewPending);
        Assert.True(be.FindBrush(uid)!.Position.ApproxEquals(new Vec3(0, 5, 0)));
    }

    [AvaloniaFact]
    public void Undoing_An_Object_Only_Change_Does_Not_Arm_A_Geometry_Build()
    {
        // An object move raises no BrushesChanged, so the undo must not arm the geometry rebuild.
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        GeometryBuildController bc = Controller(session);
        bc.Attach();

        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 0, 0))!;
        var section = light.Section;
        doc.EditValue(section, "Move", light.Position, light.Position.Add(new Vec3(3, 0, 0)), v => light.Position = v);

        bc.ApplyUndoRedo(redo: false, coalesce: true);

        Assert.False(bc.LivePreviewPending);
    }
}
