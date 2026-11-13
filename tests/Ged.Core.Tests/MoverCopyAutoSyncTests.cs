using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Automatic refresh of stale mover copies. A mover is stored twice (member brush in <c>brushes</c>,
/// copy in <c>movers</c>); every editing path changes only the member, so the document records each
/// member's state when its copy was taken (<see cref="MoverCopyTracker"/>) and a save / build refreshes
/// exactly the movers whose members changed since (<see cref="EditorDocument.SyncStaleMoverCopies"/>),
/// folded into the latest undo step. Untouched movers — including every mover of a stock level opened
/// and saved — keep their bytes.
/// </summary>
public sealed class MoverCopyAutoSyncTests
{
    private static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>Stock (v180) corpus levels that carry movers, plus RED/Alpine-authored v304 ones (saved verbatim but for the version).</summary>
    public static IEnumerable<object[]> StockMoverLevels() =>
        new[] { "dm03.rfl", "dm06.rfl", "dm15.rfl", "dm17.rfl", "ctf01.rfl", "dmabruptdecayrc2a27.rfl", "dmwarzoneclassicb1.rfl" }
            .Select(n => new object[] { n });

    private static string? StockPath(string name)
    {
        if (!Corpus.Available)
        {
            return null;
        }

        string path = Path.Combine(Corpus.Directory!, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The serialized body of the first section of <paramref name="type"/>, or null.</summary>
    private static byte[]? Body(RflFile rfl, SectionType type) =>
        rfl.Sections.FirstOrDefault(s => s.TypeId == (uint)type)?.GetBodyBytes(rfl.Context);

    private static (EditorDocument Doc, BrushEditor Brushes, MoverService Movers, Group Mover, int Member) NewMoverLevel()
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        var be = new BrushEditor(doc);
        int box = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(4, 1, 0), Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        doc.MarkSaved();
        return (doc, be, movers, g, box);
    }

    private static Brush CopyOf(MoverService movers, int uid) => movers.MoverBrushes.Single(b => b.Uid == uid);

    private static OpResult Reshape(Brush b)
    {
        BrushTransform.Move(b, new Vec3(0, 0.5f, 0.25f));
        b.Geometry.Vertices[0] = b.Geometry.Vertices[0].Add(new Vec3(0.125f, 0, 0));
        GeometryUtil.RecomputeAllPlanes(b.Geometry);
        b.Life = 77;
        return OpResult.Ok();
    }

    /// <summary>The first mover whose member brush and copy are both present, with a start keyframe.</summary>
    private static (Group Mover, int Member)? FirstEditableMover(EditorDocument doc)
    {
        var movers = new MoverService(doc);
        var be = new BrushEditor(doc);
        foreach (Group g in movers.Movers)
        {
            if (g.MovingData is not { Keyframes.Count: > 0 })
            {
                continue;
            }

            foreach (int uid in g.Brushes)
            {
                if (be.FindBrush(uid) is not null && movers.MoverBrushes.Any(m => m.Uid == uid))
                {
                    return (g, uid);
                }
            }
        }

        return null;
    }

    private static Vec3 StartKeyframe(Group g) =>
        g.MovingData!.Keyframes[Math.Clamp(g.MovingData.StartingKeyframe, 0, g.MovingData.Keyframes.Count - 1)].Position;

    // ---- stock levels ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StockMoverLevels))]
    public void Stock_Mover_Level_Opened_And_Saved_Without_Edits_Is_Byte_Identical(string name)
    {
        if (StockPath(name) is not { } path)
        {
            return; // corpus absent
        }

        EditorDocument doc = EditorDocument.Open(path);
        var movers = new MoverService(doc);
        Assert.NotEmpty(movers.Movers);
        Assert.True(doc.MoverCopies.TrackedCount > 0, "the open snapshots every mover member");
        Assert.Empty(movers.StaleMovers());
        int nodes = doc.Undo.NodeCount;
        bool v304 = doc.Rfl.Header.Version >= 0xC8;

        string temp = Path.Combine(Path.GetTempPath(), $"ged-moversync-{Guid.NewGuid():N}.rfl");
        try
        {
            doc.Save(temp, updateTimestamp: false);
            byte[] saved = File.ReadAllBytes(temp);
            Assert.Equal(nodes, doc.Undo.NodeCount); // nothing synced, nothing recorded
            Assert.Equal(0, doc.SyncStaleMoverCopies());

            // Exactly the bytes the save wrote before mover sync existed: its reconcile + v306 upgrade + write.
            RflFile reference = EditorDocument.Open(path).Rfl;
            AlpineGeoableState.ReconcileTableFromBrushFlags(reference);
            LevelInfoReconciler.ReconcileHasMovers(reference);
            reference.UpgradeToAlpine();
            Assert.True(reference.Save(updateTimestamp: false).AsSpan().SequenceEqual(saved), name);

            // A v300+ source keeps its mover sections verbatim (only the header version changes).
            if (v304)
            {
                RflFile original = RflFile.Load(File.ReadAllBytes(path));
                RflFile written = RflFile.Load(saved);
                Assert.Equal(Body(original, SectionType.Movers), Body(written, SectionType.Movers));
                Assert.Equal(Body(original, SectionType.MovingGroups), Body(written, SectionType.MovingGroups));
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(StockMoverLevels))]
    public void Editing_A_Stock_Mover_Member_Then_Saving_Refreshes_Only_That_Mover_And_Undoes_As_A_Pair(string name)
    {
        if (StockPath(name) is not { } path)
        {
            return; // corpus absent
        }

        EditorDocument doc = EditorDocument.Open(path);
        doc.Rfl.UpgradeToAlpine(); // compare like with like: Save writes v306 (re-serializing v180 sections)
        (Group mover, int uid) = FirstEditableMover(doc) ?? throw new InvalidOperationException($"{name}: no editable mover");
        var be = new BrushEditor(doc);
        var movers = new MoverService(doc);
        byte[]? moversBefore = Body(doc.Rfl, SectionType.Movers);
        byte[]? groupsBefore = Body(doc.Rfl, SectionType.MovingGroups);

        // Every OTHER mover copy, as loaded (content + build-owned face ids).
        var others = movers.MoverBrushes.Where(m => m.Uid != uid)
            .ToDictionary(m => m.Uid, m => (Brush: GeometryClone.Deep(m), FaceIds: m.Geometry.Faces.Select(f => f.FaceId).ToArray()));

        Assert.True(be.EditBrushes(new[] { uid }, "Reshape mover brush", Reshape));
        Brush member = be.FindBrush(uid)!;
        Assert.False(MoverService.SameContent(CopyOf(movers, uid), member)); // the GUI edit leaves the copy behind
        (Group staleMover, IReadOnlyList<int> touched) = Assert.Single(movers.StaleMovers());
        Assert.Same(mover, staleMover);
        Assert.Equal(new[] { uid }, touched);
        int nodes = doc.Undo.NodeCount;

        string temp = Path.Combine(Path.GetTempPath(), $"ged-moversync-{Guid.NewGuid():N}.rfl");
        try
        {
            doc.Save(temp, updateTimestamp: false);
            Assert.Equal(nodes, doc.Undo.NodeCount); // folded into the edit's undo step
            Assert.Empty(movers.StaleMovers());

            // The saved file: the copy matches the edited member, and its offset is relative to the start keyframe.
            EditorDocument reloaded = EditorDocument.Open(temp);
            var rm = new MoverService(reloaded);
            Brush savedMember = new BrushEditor(reloaded).FindBrush(uid)!;
            Brush savedCopy = CopyOf(rm, uid);
            Assert.True(MoverService.SameContent(savedCopy, savedMember), name);
            Assert.Equal(member.Position, savedCopy.Position);
            Assert.Equal(member.Rotation, savedCopy.Rotation);
            Assert.Equal(77, savedCopy.Life);
            Group savedMover = rm.FindGroupForMember(uid)!;
            Assert.Equal(savedMember.Position.Sub(StartKeyframe(savedMover)),
                savedMover.MovingData!.MemberTransforms.Single(t => t.Uid == uid).Position);

            // Every other mover copy is exactly as loaded.
            foreach (Brush m in rm.MoverBrushes.Where(m => m.Uid != uid))
            {
                Assert.True(MoverService.SameContent(m, others[m.Uid].Brush), $"{name}: mover brush {m.Uid} changed");
                Assert.Equal(others[m.Uid].FaceIds, m.Geometry.Faces.Select(f => f.FaceId).ToArray());
            }
        }
        finally
        {
            File.Delete(temp);
        }

        // One undo reverses the edit AND the refresh: the member is back and the movers + moving-group
        // sections serialize byte for byte as loaded.
        doc.Undo.Undo();
        Assert.True(MoverService.SameContent(be.FindBrush(uid)!, CopyOf(movers, uid)));
        Assert.Equal(moversBefore, Body(doc.Rfl, SectionType.Movers));
        Assert.Equal(groupsBefore, Body(doc.Rfl, SectionType.MovingGroups));
        Assert.Empty(movers.StaleMovers());

        // Redo re-applies both.
        doc.Undo.Redo();
        Assert.True(MoverService.SameContent(CopyOf(movers, uid), be.FindBrush(uid)!));
        Assert.Empty(movers.StaleMovers());
    }

    // ---- the mechanism, on a fresh level ---------------------------------------------------

    [Fact]
    public void A_Gizmo_Edit_Is_Synced_In_The_Same_Undo_Step_And_Undo_Restores_Both_Copies()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, Group g, int box) = NewMoverLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        Brush copy = CopyOf(movers, box);
        Vec3 copyPos = copy.Position;

        // The viewport gizmo path (coalesced drag frames).
        be.EditBrushesCoalesced(new[] { box }, "Move (gizmo)", b => { BrushTransform.Move(b, new Vec3(0, 1, 0)); return OpResult.Ok(); }, "drag");
        be.EditBrushesCoalesced(new[] { box }, "Move (gizmo)", b => { BrushTransform.Move(b, new Vec3(0, 1, 0)); return OpResult.Ok(); }, "drag");
        int nodes = doc.Undo.NodeCount;
        Assert.Equal(copyPos, copy.Position); // stale until a save / build

        Assert.Equal(1, doc.SyncStaleMoverCopies());
        Assert.Equal(nodes, doc.Undo.NodeCount);
        Brush member = be.FindBrush(box)!;
        Assert.Equal(member.Position, copy.Position);
        Assert.Equal(member.Position.Sub(StartKeyframe(g)), g.MovingData!.MemberTransforms.Single().Position);
        Assert.Equal(0, doc.SyncStaleMoverCopies()); // current now

        doc.Undo.Undo();
        Assert.Equal(copyPos, copy.Position);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        Assert.Empty(movers.StaleMovers());

        doc.Undo.Redo();
        Assert.Equal(be.FindBrush(box)!.Position, copy.Position);
        Assert.Empty(movers.StaleMovers());
    }

    [Fact]
    public void An_Edit_That_Is_Undone_Leaves_The_Mover_Untouched()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, _, int box) = NewMoverLevel();
        Geometry geometry = CopyOf(movers, box).Geometry;
        be.EditBrushes(new[] { box }, "Reshape", Reshape);
        doc.Undo.Undo();
        Assert.Empty(movers.StaleMovers());
        Assert.Equal(0, doc.SyncStaleMoverCopies());
        Assert.Same(geometry, CopyOf(movers, box).Geometry);
    }

    [Fact]
    public void Face_And_Uv_Edits_Mark_The_Mover_Stale_And_A_Sync_With_Nothing_To_Fold_Into_Records_A_Node()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, _, int box) = NewMoverLevel();
        be.SetFaceUvs("UV", new[] { (box, 0, 0, new Uv(0.5f, 0.25f)) });
        Assert.Single(movers.StaleMovers());
        doc.SyncStaleMoverCopies();
        Assert.Equal(0.5f, CopyOf(movers, box).Geometry.Faces[0].Vertices[0].TextureCoords.U);

        // At the undo baseline there is no step to fold into: the refresh becomes its own step.
        var undo = new UndoStack();
        int applied = 0;
        UndoStack.Transaction tx = undo.BeginFoldedTransaction("Sync mover copies");
        undo.Execute(new RelayCommand("x", () => applied++, () => applied--));
        tx.Commit();
        Assert.Equal(1, undo.NodeCount);
        undo.Undo();
        Assert.Equal(0, applied);
    }

    [Fact]
    public void Moving_The_Mover_Copy_Directly_Does_Not_Mark_It_Stale()
    {
        // Only MEMBER edits make a copy stale; a direct change to the copy is never overwritten by a sync.
        (EditorDocument doc, _, MoverService movers, _, int box) = NewMoverLevel();
        LevelObject moverObject = doc.Objects.Single(o => o.Kind == LevelObjectKind.Mover && o.Uid == box);
        Vec3 moved = moverObject.Position.Add(new Vec3(3, 0, 0));
        doc.EditValue(moverObject.Section, "Move", moverObject.Position, moved, v => moverObject.Position = v);
        Assert.Empty(movers.StaleMovers());
        Assert.Equal(0, doc.SyncStaleMoverCopies());
        Assert.Equal(moved, CopyOf(movers, box).Position);
    }

    [Fact]
    public void The_Ghost_Shows_The_Edited_Member_Shape()
    {
        (_, BrushEditor be, MoverService movers, Group g, int box) = NewMoverLevel();
        be.EditBrushes(new[] { box }, "Reshape", Reshape);
        Brush shape = Assert.Single(movers.CurrentMemberShapes(g));
        Assert.Same(be.FindBrush(box), shape);
    }

    [Fact]
    public void The_Repair_Of_A_Movers_Only_Level_Snapshots_The_Restored_Members()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, _, int box) = NewMoverLevel();
        EditorDocument broken = EditorDocument.OpenBytes(doc.SaveToBytes(updateTimestamp: false));
        BrushesSection bs = broken.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().Single();
        bs.Brushes.RemoveAll(b => b.Uid == box);
        var bm = new MoverService(broken);
        Assert.Equal(1, bm.RepairStoredTwiceInvariant());
        Assert.Empty(bm.StaleMovers());
        new BrushEditor(broken).EditBrushes(new[] { box }, "Reshape", Reshape);
        Assert.Single(bm.StaleMovers());
        _ = (be, movers);
    }

    // ---- one mechanism shared with scripts -------------------------------------------------

    [Fact]
    public void A_Member_Edited_In_The_Editor_Is_Synced_By_A_Script_Build_But_Not_By_An_Unrelated_Run()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, _, int box) = NewMoverLevel();
        be.EditBrushes(new[] { box }, "Move", b => { BrushTransform.Move(b, new Vec3(0, 2, 0)); return OpResult.Ok(); });
        Vec3 stalePos = CopyOf(movers, box).Position;

        // A run that edits no mover member leaves the editor's pending refresh for the save/build.
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, "log.info('hello')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("refreshed the mover copy", StringComparison.Ordinal));
        Assert.Equal(stalePos, CopyOf(movers, box).Position);

        // ops.build reads the copies: it refreshes the editor-edited one first.
        (r, log) = ScriptTestFixture.Run(doc, "ops.build()");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Contains(log.Entries, e => e.Message.Contains("refreshed the mover copy", StringComparison.Ordinal));
        Assert.Equal(be.FindBrush(box)!.Position, CopyOf(movers, box).Position);
        Assert.Empty(movers.StaleMovers());
    }
}
