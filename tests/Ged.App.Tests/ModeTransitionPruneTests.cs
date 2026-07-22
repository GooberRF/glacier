using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Rendering.Picking;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Tests;

/// <summary>
/// P3 — entering a mode must deselect anything not selectable in that mode. The reported symptom
/// (an EAX region selected in Object mode staying "selected" after switching to Brush mode, until a
/// brush was clicked) was the transient pick HIGHLIGHT box surviving the mode switch: the object
/// selection state was already pruned, but the last-pick highlight overlay was only reset on the
/// NEXT click. <see cref="EditorSession.SyncSelectionToKinds"/> is now the single mode-transition
/// chokepoint — table-driven off <see cref="SelectKinds"/> (no per-mode lists) — that prunes the
/// selection AND drops the pick highlight together. These tests pin the transition matrix and the
/// highlight reset that was the actual leak.
/// </summary>
public sealed class ModeTransitionPruneTests
{
    private static (EditorSession Session, LevelObject Obj, int BrushUid) Fresh()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        BrushEditor be = session.BrushEditor!;

        // Select an object AND a brush together (multi-kind filter) so every transition can be tested.
        // The brush is added ADDITIVELY: with both kinds co-selectable, a plain (non-additive) select of
        // one kind REPLACES the whole cross-kind selection (a plain click never leaves the other kind's
        // stale highlight lingering), so a mixed selection is built by Ctrl-adding the second kind — the
        // real-app gesture.
        session.ActiveSelectKinds = SelectKinds.Objects | SelectKinds.Brushes;
        LevelObject obj = doc.PlaceObject(LevelObjectKind.RoomEffect, new CoreVec3(0, 0, 0))!;
        int uid = be.CreateBrush(new BrushCreateParams { Width = 2, Height = 2, Depth = 2 }, new CoreVec3(6, 0, 0), Ged.Core.Model.Mat3.Identity);
        session.Selection.SelectObject(obj);
        session.Selection.SelectBrush(uid, additive: true);
        return (session, obj, uid);
    }

    [AvaloniaTheory]
    [InlineData(EditMode.Object, true, false)]   // Object: objects stay, brush sub-selection drops
    [InlineData(EditMode.Group, true, true)]     // Group: BOTH kinds survive (widened gate)
    [InlineData(EditMode.Brush, false, true)]    // Brush: objects drop, whole-brush selection stays
    [InlineData(EditMode.Face, false, false)]    // Face: objects drop, whole-brush selection drops
    [InlineData(EditMode.Vertex, false, false)]  // Vertex: same
    [InlineData(EditMode.Edge, false, false)]    // Edge: same
    public void Transition_Matrix_Prunes_Exactly_The_Table(EditMode mode, bool keepObject, bool keepBrush)
    {
        (EditorSession session, LevelObject obj, int uid) = Fresh();

        session.SyncSelectionToKinds(SelectionFilter.PrimaryKindFor(mode));

        Assert.Equal(keepObject, session.Document!.Selection.Contains(obj));
        Assert.Equal(keepBrush, session.BrushEditor!.SelectedBrushes.Contains(uid));
    }

    [AvaloniaFact]
    public void Entering_Brush_Mode_Drops_The_Object_Selection_And_Its_Pick_Highlight()
    {
        (EditorSession session, LevelObject obj, _) = Fresh();

        // The object click also lit a pick-highlight box (the actual EAX leak surface).
        session.PickHighlight = new PickId(PickKind.Object, obj.Uid);
        Assert.False(session.PickHighlight.IsNone);

        session.SyncSelectionToKinds(SelectKinds.Brushes);

        Assert.DoesNotContain(obj, session.Document!.Selection); // state pruned
        Assert.True(session.PickHighlight.IsNone);               // highlight dropped (the fix)
        Assert.Empty(session.BuildSelectionLines(session.PickHighlight)); // ⇒ no phantom box drawn
    }

    [AvaloniaFact]
    public void Group_Mode_Preserves_Both_Object_And_Brush_Selection()
    {
        (EditorSession session, LevelObject obj, int uid) = Fresh();

        session.SyncSelectionToKinds(SelectKinds.Groups);

        Assert.Contains(obj, session.Document!.Selection);
        Assert.Contains(uid, session.BrushEditor!.SelectedBrushes);
    }

    [AvaloniaFact]
    public void Ctf06_Eax_Region_Is_Dropped_Entering_Brush_Mode()
    {
        string? path = Ctf06Path();
        if (path is null)
        {
            return; // corpus absent
        }

        var session = new EditorSession();
        session.OpenLevel(path);
        EditorDocument doc = session.Document!;
        LevelObject? eax = doc.Objects.FirstOrDefault(o => o.Kind == LevelObjectKind.Eax);
        if (eax is null)
        {
            return; // no EAX region in this level
        }

        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(eax);
        session.PickHighlight = new PickId(PickKind.Object, eax.Uid);
        Assert.Contains(eax, doc.Selection);

        // Switch to Brush mode — the EAX region (an object) is not selectable there.
        session.SyncSelectionToKinds(SelectKinds.Brushes);

        Assert.DoesNotContain(eax, doc.Selection);
        Assert.True(session.PickHighlight.IsNone);
    }

    internal static string? Ctf06Path()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        string path = Path.Combine(dir.FullName, "research", "example_rfls", "ctf06.rfl");
        return File.Exists(path) ? path : null;
    }
}
