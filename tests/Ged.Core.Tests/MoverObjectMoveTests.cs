using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Whole-mover Object-mode transforms carry both stored copies (<see cref="MoverObjectTransformLink"/>):
/// moving or rotating the <see cref="LevelObjectKind.Mover"/> object — through the generic
/// <see cref="EditorDocument.EditValue{T}"/> path every GUI transform uses, the script API, or a raw
/// rotation command — moves its member brush and recomputes its member offset in the same undo step,
/// leaving the keyframes in place. Also gates RED's new-keyframe timing defaults.
/// </summary>
public sealed class MoverObjectMoveTests
{
    private static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static (EditorDocument Doc, BrushEditor Brushes, MoverService Movers, Group Mover, int Member) NewMoverLevel()
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        var be = new BrushEditor(doc);
        int box = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(4, 1, 0), Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        movers.AddKeyframe(g, new Vec3(4, 6, 0), Mat3.Identity);
        doc.MarkSaved();
        return (doc, be, movers, g, box);
    }

    private static Brush CopyOf(MoverService movers, int uid) => movers.MoverBrushes.Single(b => b.Uid == uid);

    private static LevelObject MoverObject(EditorDocument doc, int uid) =>
        doc.Objects.Single(o => o.Kind == LevelObjectKind.Mover && o.Uid == uid);

    private static Vec3 Start(Group g) => g.MovingData!.Keyframes[g.MovingData.StartingKeyframe].Position;

    private static MovingGroupMemberTransform Offset(Group g, int uid) => g.MovingData!.MemberTransforms.Single(t => t.Uid == uid);

    /// <summary>Asserts the member brush and its copy share a pose and content, and the offset is what a sync derives.</summary>
    private static void AssertPairConsistent(BrushEditor be, MoverService movers, Group g, int uid)
    {
        Brush member = be.FindBrush(uid)!;
        Brush copy = CopyOf(movers, uid);
        Assert.Equal(member.Position, copy.Position);
        Assert.Equal(member.Rotation, copy.Rotation);
        Assert.True(MoverService.SameContent(copy, member));
        Assert.Equal(member.Position.Sub(Start(g)), Offset(g, uid).Position);
        Assert.Equal(member.Rotation, Offset(g, uid).Rotation);
    }

    // ---- 1. RED's new-keyframe defaults --------------------------------------------------------

    [Fact]
    public void New_Keyframes_Get_REDs_One_Second_Travel_Defaults()
    {
        var (_, _, _, g, _) = NewMoverLevel();
        Assert.Equal(2, g.MovingData!.Keyframes.Count);
        foreach (Keyframe k in g.MovingData.Keyframes)
        {
            // RED.exe keyframe ctor FUN_00452770: depart/return travel 1.0, pause/accel/decel/degrees 0, UIDs -1.
            Assert.Equal((1f, 1f), (k.DepartTravelTime, k.ReturnTravelTime));
            Assert.Equal((0f, 0f, 0f, 0f), (k.PauseTime, k.AccelTime, k.DecelTime, k.DegreesAboutAxis));
            Assert.Equal((-1, -1, -1), (k.EventUid, k.ItemUid1, k.ItemUid2));
        }
    }

    [Fact]
    public void Script_Keyframes_Default_To_One_Second_And_Keep_Explicit_Props()
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        int box = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 1, 0), Mat3.Identity);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            $"local m = level.create_mover{{brushes = {{level.find_brush({box})}}}}\n" +
            "m:add_keyframe{offset = {0, 3, 0}}\n" +
            "m:add_keyframe{offset = {0, 6, 0}, props = {travel_time_to_next = 2.5, return_travel = 0}}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        List<Keyframe> kfs = new MoverService(doc).Movers.Single().MovingData!.Keyframes;
        Assert.Equal((1f, 1f), (kfs[0].DepartTravelTime, kfs[0].ReturnTravelTime));
        Assert.Equal((1f, 1f), (kfs[1].DepartTravelTime, kfs[1].ReturnTravelTime));
        Assert.Equal((2.5f, 0f), (kfs[2].DepartTravelTime, kfs[2].ReturnTravelTime));
    }

    // ---- 2. whole-mover moves carry both copies --------------------------------------------------

    [Fact]
    public void Object_Mode_Move_Carries_Member_Offset_And_Keyframes_Stay_Put()
    {
        var (doc, be, movers, g, uid) = NewMoverLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        Vec3[] keyframes = g.MovingData!.Keyframes.Select(k => k.Position).ToArray();
        Vec3 oldOffset = Offset(g, uid).Position;
        LevelObject mover = MoverObject(doc, uid);
        int nodes = doc.Undo.NodeCount;

        Vec3 target = mover.Position.Add(new Vec3(3, 0.5f, -2));
        doc.EditValue(mover.Section, "Move (gizmo)", mover.Position, target, v => mover.Position = v);

        Assert.Equal(1, doc.Undo.NodeCount - nodes);
        Assert.Equal(target, be.FindBrush(uid)!.Position);
        AssertPairConsistent(be, movers, g, uid);
        Assert.Equal(oldOffset.Add(new Vec3(3, 0.5f, -2)), Offset(g, uid).Position);
        Assert.Equal(keyframes, g.MovingData.Keyframes.Select(k => k.Position).ToArray()); // path objects stay put
        Assert.Empty(movers.StaleMovers()); // moved together: nothing for a sync to repair

        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        Assert.Empty(movers.StaleMovers());

        doc.Undo.Redo();
        Assert.Equal(target, CopyOf(movers, uid).Position);
        AssertPairConsistent(be, movers, g, uid);
    }

    [Fact]
    public void Member_Edit_After_Object_Move_Does_Not_Snap_The_Mover_Back()
    {
        var (doc, be, movers, g, uid) = NewMoverLevel();
        LevelObject mover = MoverObject(doc, uid);
        Vec3 target = mover.Position.Add(new Vec3(0, 0, 5));
        doc.EditValue(mover.Section, "Set coordinates", mover.Position, target, v => mover.Position = v);

        // A later Brush-mode edit of the member, then the lazy sync a save/build runs.
        be.EditBrushesCoalesced(new[] { uid }, "Reshape", b =>
        {
            b.Geometry.Vertices[0] = b.Geometry.Vertices[0].Add(new Vec3(0.125f, 0, 0));
            GeometryUtil.RecomputeAllPlanes(b.Geometry);
            return OpResult.Ok();
        }, null);
        Assert.Equal(1, doc.SyncStaleMoverCopies());

        Assert.Equal(target, CopyOf(movers, uid).Position);
        AssertPairConsistent(be, movers, g, uid);
    }

    [Fact]
    public void Object_Move_Of_A_Stale_Member_Keeps_The_Pending_Edit()
    {
        var (doc, be, movers, g, uid) = NewMoverLevel();
        be.EditBrushesCoalesced(new[] { uid }, "Nudge member", b => { BrushTransform.Move(b, new Vec3(0, 1, 0)); return OpResult.Ok(); }, null);
        Vec3 memberBefore = be.FindBrush(uid)!.Position;
        LevelObject mover = MoverObject(doc, uid);
        doc.EditValue(mover.Section, "Move (gizmo)", mover.Position, mover.Position.Add(new Vec3(2, 0, 0)), v => mover.Position = v);

        Assert.Equal(memberBefore.Add(new Vec3(2, 0, 0)), be.FindBrush(uid)!.Position); // carried by the same delta
        Assert.Single(movers.StaleMovers()); // the Brush-mode edit is still pending for the copy
        doc.SyncStaleMoverCopies();
        AssertPairConsistent(be, movers, g, uid);
    }

    [Fact]
    public void Gizmo_Style_Rotate_Is_One_Step_And_Rotates_Both_Copies()
    {
        var (doc, be, movers, g, uid) = NewMoverLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        LevelObject mover = MoverObject(doc, uid);
        int nodes = doc.Undo.NodeCount;
        Vec3 pivot = new(0, 0, 0);
        Mat3 rot = Mat3Math.FromAxisAngle(new Vec3(0, 1, 0), MathF.PI / 2);

        // The gizmo rotate path: a position EditValue plus a rotation EditValue, in one drag transaction.
        using (doc.Undo.BeginTransaction("Rotate (gizmo)"))
        {
            Vec3 np = pivot.Add(rot.Transform(mover.Position.Sub(pivot)));
            doc.EditValue(mover.Section, "Rotate (gizmo)", mover.Position, np, v => mover.Position = v);
            Mat3 cur = ObjectRotation.Get(mover.Model)!.Value;
            doc.EditValue(mover.Section, "Rotate (gizmo)", cur, Mat3Math.Compose(rot, cur).Orthonormalize(), v => ObjectRotation.Set(mover.Model, v));
        }

        Assert.Equal(1, doc.Undo.NodeCount - nodes);
        Assert.NotEqual(Mat3.Identity, be.FindBrush(uid)!.Rotation);
        AssertPairConsistent(be, movers, g, uid);

        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Script_Move_And_Set_Rot_On_A_Mover_Object_Carry_The_Member()
    {
        ScriptMutationAssert.AssertMutation(() => NewMoverLevel().Doc,
            "local m = level.objects_of('Mover')[1]\n" +
            "m:move(1, 2, 3)\n" +
            "m:set_rot{yaw = 90}",
            (d, res) =>
            {
                var movers = new MoverService(d);
                Group g = movers.Movers.Single();
                int uid = g.Brushes.Single();
                var be = new BrushEditor(d);
                Assert.Equal(new Vec3(5, 3, 3), be.FindBrush(uid)!.Position);
                AssertPairConsistent(be, movers, g, uid);
            });
    }

    [Fact]
    public void Moved_Mover_Saves_Reloads_Consistent_And_Stays_Out_Of_The_Static_Fold()
    {
        var (doc, _, _, _, uid) = NewMoverLevel();
        LevelObject mover = MoverObject(doc, uid);
        doc.EditValue(mover.Section, "Move", mover.Position, mover.Position.Add(new Vec3(-6, 0, 2)), v => mover.Position = v);

        byte[] saved = doc.SaveToBytes(updateTimestamp: false);
        EditorDocument reloaded = EditorDocument.OpenBytes(saved);
        var movers = new MoverService(reloaded);
        var be = new BrushEditor(reloaded);
        Group g = movers.Movers.Single();
        AssertPairConsistent(be, movers, g, uid);
        Assert.Empty(movers.StaleMovers());
        Assert.Equal(saved, reloaded.SaveToBytes(updateTimestamp: false));

        Assert.Contains(uid, MoverBrushes.CollectMoverUids(reloaded.Rfl));
        Assert.DoesNotContain(MoverBrushes.StaticWorldBrushes(reloaded.Rfl), b => b.Uid == uid);
    }

    [Fact]
    public void Ordinary_Edits_Leave_Mover_Members_Alone()
    {
        var (doc, be, movers, g, uid) = NewMoverLevel();
        Vec3 member = be.FindBrush(uid)!.Position;
        Vec3 offset = Offset(g, uid).Position;

        // Moving a keyframe, editing a mover field and moving the member itself never trigger a carry.
        Keyframe second = g.MovingData!.Keyframes[1];
        LevelObject kf = doc.Objects.Single(o => o.Kind == LevelObjectKind.Keyframe && o.Uid == second.Uid);
        doc.EditValue(kf.Section, "Move keyframe", kf.Position, kf.Position.Add(new Vec3(0, 1, 0)), v => kf.Position = v);
        movers.EditMover(g.MovingData, "type", d => d.MovementType = 2, d => d.MovementType = 1);
        Assert.Equal(member, be.FindBrush(uid)!.Position);
        Assert.Equal(offset, Offset(g, uid).Position);

        be.EditBrushesCoalesced(new[] { uid }, "Move member", b => { BrushTransform.Move(b, new Vec3(1, 0, 0)); return OpResult.Ok(); }, null);
        Assert.Equal(member.Add(new Vec3(1, 0, 0)), be.FindBrush(uid)!.Position);
        Assert.Equal(member, CopyOf(movers, uid).Position); // the copy waits for the lazy sync, as before
    }
}
