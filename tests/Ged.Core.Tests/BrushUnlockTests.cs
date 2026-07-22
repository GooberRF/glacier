using System;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// P2 — a brush lock is a PERSISTED state field (<see cref="BrushState.Locked"/> = 2) that ships in
/// the RFL (ctf06 UID 414). A level shipped with locked brushes could not be unlocked: "Unlock All"
/// / Shift+Q were wired to <see cref="EditorDocument.UnlockAll"/>, which clears only the SESSION
/// object-lock set and never touches brush state; and the Layers "Unlock" button operated on the
/// SELECTED brushes — but a locked brush is unselectable, so the target set was always empty. The
/// prior lock tests all locked via <see cref="BrushEditor.SetBrushLocked"/> and unlocked the same
/// way, so they exercised the working primitive and never the broken command wiring. The fix is
/// <see cref="BrushEditor.UnlockAll"/> (mutates the persisted state, undoable, dirties the file) and
/// wiring it into every unlock surface. These tests load a real disk-locked brush and unlock it.
/// </summary>
public sealed class BrushUnlockTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static SelectionRouter BrushRouter(EditorDocument doc, BrushEditor be) =>
        new(() => doc, () => be, () => SelectKinds.Brushes);

    [Fact]
    public void A_Disk_Locked_Brush_Loads_Locked_Unselectable_And_UnlockAll_Frees_It()
    {
        // Author a level whose brush ships LOCKED, then reload it from bytes (the disk path — exactly
        // how ctf06's UID 414 arrives with state=2).
        EditorDocument authoring = EmptyDoc();
        var beAuthor = new BrushEditor(authoring);
        int uid = beAuthor.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);
        beAuthor.SetBrushLocked(new[] { uid }, locked: true);
        byte[] locked = authoring.SaveToBytes(updateTimestamp: false);

        EditorDocument doc = EditorDocument.OpenBytes(locked);
        var be = new BrushEditor(doc);
        Assert.True(be.IsBrushLocked(uid)); // loaded from disk as locked (state field round-trips)

        SelectionRouter router = BrushRouter(doc, be);
        Assert.False(router.SelectBrush(uid)); // locked ⇒ unselectable through the router
        Assert.Empty(be.SelectedBrushes);

        int freed = be.UnlockAll();
        Assert.Equal(1, freed);
        Assert.False(be.IsBrushLocked(uid));
        Assert.True(router.SelectBrush(uid)); // now selectable
        Assert.Contains(uid, be.SelectedBrushes);
    }

    [Fact]
    public void UnlockAll_Is_Undoable_And_A_No_Op_Round_Trip_Stays_Byte_Identical()
    {
        EditorDocument authoring = EmptyDoc();
        var beAuthor = new BrushEditor(authoring);
        int uid = beAuthor.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);
        beAuthor.SetBrushLocked(new[] { uid }, locked: true);
        byte[] lockedBytes = authoring.SaveToBytes(updateTimestamp: false);

        // Reload + resave WITHOUT unlocking: byte-identical (the byte-identity gate is untouched
        // unless the user actually unlocks).
        EditorDocument doc = EditorDocument.OpenBytes(lockedBytes);
        var be = new BrushEditor(doc);
        Assert.True(doc.SaveToBytes(updateTimestamp: false).AsSpan().SequenceEqual(lockedBytes));

        // Unlock ⇒ the persisted state changes, so the bytes now differ...
        be.UnlockAll();
        byte[] unlockedBytes = doc.SaveToBytes(updateTimestamp: false);
        Assert.False(unlockedBytes.AsSpan().SequenceEqual(lockedBytes));
        Assert.Equal(BrushState.Normal, EditorDocument.OpenBytes(unlockedBytes) is { } r
            ? new BrushEditor(r).FindBrush(uid)!.State
            : -1);

        // ...and undo restores the locked state (and byte image).
        doc.Undo.Undo();
        Assert.True(be.IsBrushLocked(uid));
        Assert.True(doc.SaveToBytes(updateTimestamp: false).AsSpan().SequenceEqual(lockedBytes));
    }

    [Fact]
    public void Ctf06_Ships_Locked_Brushes_And_UnlockAll_Frees_Every_One()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "ctf06.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        EditorDocument doc = EditorDocument.Open(path);
        var be = new BrushEditor(doc);

        var lockedUids = be.Brushes.Where(b => b.State == BrushState.Locked).Select(b => b.Uid).ToList();
        Assert.NotEmpty(lockedUids); // ctf06 ships locked brushes (e.g. UID 414) — non-vacuous

        int freed = be.UnlockAll();
        Assert.Equal(lockedUids.Count, freed);
        Assert.DoesNotContain(be.Brushes, b => b.State == BrushState.Locked);

        // A previously file-locked brush is now selectable through the router.
        SelectionRouter router = BrushRouter(doc, be);
        Assert.True(router.SelectBrush(lockedUids[0]));
    }
}
