using System;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// "Instant" undo must not ANIMATE a multi-step drag. A gizmo drag commits as a transaction that folds
/// its per-frame move commands into one <see cref="CompositeCommand"/> undo entry; undoing that node ran
/// each accumulated inverse with a <c>BrushesChanged</c> (scene refresh) between them, so the user watched
/// the brush walk backward frame by frame. The fix coalesces the whole atomic Undo/Redo into one refresh
/// (via <see cref="UndoStack.AtomicApplyScope"/> + <see cref="BrushEditor.BatchChanges"/>); the Replay
/// path (coalesce: false) still fires per sub-command so it steps deliberately. The document state lands
/// at the exact pre-drag brush either way — only the refresh count differs.
/// </summary>
public sealed class CoalescedUndoRefreshTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static int CreateBox(BrushEditor ed, Vec3 pos = default) =>
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, pos, Mat3.Identity);

    /// <summary>Simulates a gizmo drag: one undo transaction, N per-frame coalesced move commands, commit.</summary>
    private static (Vec3 Pre, Vec3 Post) GizmoDrag(EditorDocument doc, BrushEditor ed, int uid, int frames, Vec3 perFrame)
    {
        Vec3 pre = ed.FindBrush(uid)!.Position;
        using (doc.Undo.BeginTransaction("Move (gizmo)"))
        {
            for (int i = 0; i < frames; i++)
            {
                ed.EditBrushesCoalesced(new[] { uid }, "Move (gizmo)",
                    b => { BrushTransform.Move(b, perFrame); return OpResult.Ok(); }, coalesceKey: null);
            }
        }

        return (pre, ed.FindBrush(uid)!.Position);
    }

    [Fact]
    public void Instant_Undo_Of_A_Composite_Drag_Fires_One_Refresh_And_Lands_At_Pre_Drag()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        (Vec3 pre, Vec3 post) = GizmoDrag(doc, ed, uid, frames: 8, perFrame: new Vec3(1, 0, 0));
        Assert.True(post.ApproxEquals(pre.Add(new Vec3(8, 0, 0)))); // drag actually moved 8 units

        int refreshes = 0;
        ed.BrushesChanged += () => refreshes++;

        doc.Undo.Undo(); // Instant (default coalesce)

        Assert.Equal(1, refreshes); // ONE refresh for the whole entry — no frame-by-frame walk-back
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(pre)); // exact pre-drag state
    }

    [Fact]
    public void Replay_Undo_Of_A_Composite_Drag_Still_Steps_Per_Frame()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        const int frames = 8;
        (Vec3 pre, _) = GizmoDrag(doc, ed, uid, frames, perFrame: new Vec3(1, 0, 0));

        int refreshes = 0;
        ed.BrushesChanged += () => refreshes++;

        doc.Undo.Undo(coalesce: false); // Replay: let each sub-command notify

        Assert.Equal(frames, refreshes); // one per accumulated frame — the deliberate visible step
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(pre)); // same exact landing as Instant
    }

    [Fact]
    public void Instant_Redo_Of_A_Composite_Drag_Also_Fires_One_Refresh()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        (_, Vec3 post) = GizmoDrag(doc, ed, uid, frames: 6, perFrame: new Vec3(0, 1, 0));
        doc.Undo.Undo();

        int refreshes = 0;
        ed.BrushesChanged += () => refreshes++;

        doc.Undo.Redo(); // Instant

        Assert.Equal(1, refreshes);
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(post)); // back to the drag's end state
    }

    [Fact]
    public void Coalesced_MN_Style_Node_Undoes_In_One_Refresh()
    {
        // The M-N brush drag folds per-frame moves into ONE node via a shared coalesce key (no
        // transaction). Its single net inverse already lands at pre-drag in one step; confirm the batch
        // wrapping leaves that intact (never MORE than one refresh) — "keyboard-nudge coalescing behaves
        // the same".
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        Vec3 pre = ed.FindBrush(uid)!.Position;

        for (int i = 0; i < 10; i++)
        {
            ed.EditBrushesCoalesced(new[] { uid }, "Move",
                b => { BrushTransform.Move(b, new Vec3(0, 0, 1)); return OpResult.Ok(); }, coalesceKey: "brushdrag1");
        }

        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(pre.Add(new Vec3(0, 0, 10))));

        int refreshes = 0;
        ed.BrushesChanged += () => refreshes++;

        doc.Undo.Undo();

        Assert.Equal(1, refreshes);
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(pre));
    }

    [Fact]
    public void Discrete_Nudges_Are_Independent_Entries_Each_Undoing_Once()
    {
        // A keyboard nudge uses a NULL coalesce key: each press is its own entry. Undo peels ONE nudge,
        // firing one refresh — unchanged by the batch mechanism.
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        Vec3 pre = ed.FindBrush(uid)!.Position;

        ed.EditBrushesCoalesced(new[] { uid }, "Move", b => { BrushTransform.Move(b, new Vec3(2, 0, 0)); return OpResult.Ok(); }, coalesceKey: null);
        ed.EditBrushesCoalesced(new[] { uid }, "Move", b => { BrushTransform.Move(b, new Vec3(2, 0, 0)); return OpResult.Ok(); }, coalesceKey: null);

        int refreshes = 0;
        ed.BrushesChanged += () => refreshes++;

        doc.Undo.Undo();

        Assert.Equal(1, refreshes);
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(pre.Add(new Vec3(2, 0, 0)))); // only one nudge peeled
    }
}
