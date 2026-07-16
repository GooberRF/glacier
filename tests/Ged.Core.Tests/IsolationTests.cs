using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 3 (B6): Isolate Selection is a non-destructive view overlay — while active only
/// the isolated set renders, and exiting restores the EXACT prior visibility (including
/// pre-existing hidden objects), because it never touches the undoable Hidden flags.
/// </summary>
public sealed class IsolationTests
{
    private static EditorDocument NewDocWithEntities(int count)
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        var es = new EntitiesSection();
        for (int i = 0; i < count; i++)
        {
            es.Entities.Add(new Entity { Uid = i + 1, ClassName = "Guard", ScriptName = $"e{i + 1}", Position = new Vec3(i, 0, 0) });
        }

        rfl.Sections.Add(new RflSection((uint)SectionType.Entities, Array.Empty<byte>()) { Content = es });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Isolating_Hides_Everything_Except_The_Isolated_Set()
    {
        EditorDocument doc = NewDocWithEntities(4);
        LevelObject keep = doc.FindByUid(2)!;

        doc.IsolateSelection(new[] { keep.Uid });

        Assert.True(doc.IsIsolated);
        Assert.False(doc.IsEffectivelyHidden(keep));
        foreach (LevelObject o in doc.Objects.Where(o => o.Uid != keep.Uid))
        {
            Assert.True(doc.IsEffectivelyHidden(o), $"uid {o.Uid} should be hidden while isolated");
        }
    }

    [Fact]
    public void Exiting_Restores_The_Exact_Prior_Visibility_Including_Pre_Existing_Hidden()
    {
        EditorDocument doc = NewDocWithEntities(4);
        LevelObject alreadyHidden = doc.FindByUid(1)!;
        LevelObject keep = doc.FindByUid(2)!;

        // Pre-existing hidden object (undoable Hide).
        doc.Select(alreadyHidden);
        doc.HideSelected();
        Assert.True(alreadyHidden.Hidden);

        doc.IsolateSelection(new[] { keep.Uid });
        Assert.True(doc.IsEffectivelyHidden(alreadyHidden)); // hidden while isolated too
        Assert.False(doc.IsEffectivelyHidden(keep));

        doc.ExitIsolation();

        Assert.False(doc.IsIsolated);
        // The pre-existing hidden object is STILL hidden (exact restore, not unhide-all)...
        Assert.True(alreadyHidden.Hidden);
        Assert.True(doc.IsEffectivelyHidden(alreadyHidden));
        // ...and the objects that were visible before are visible again.
        Assert.False(doc.IsEffectivelyHidden(keep));
        Assert.False(doc.IsEffectivelyHidden(doc.FindByUid(3)!));
    }

    [Fact]
    public void Re_Isolating_Replaces_The_Visible_Set()
    {
        EditorDocument doc = NewDocWithEntities(3);
        doc.IsolateSelection(new[] { 1 });
        Assert.False(doc.IsEffectivelyHidden(doc.FindByUid(1)!));
        Assert.True(doc.IsEffectivelyHidden(doc.FindByUid(2)!));

        doc.IsolateSelection(new[] { 2 });
        Assert.True(doc.IsEffectivelyHidden(doc.FindByUid(1)!));
        Assert.False(doc.IsEffectivelyHidden(doc.FindByUid(2)!));
    }

    [Fact]
    public void Exit_When_Not_Isolated_Is_A_No_Op()
    {
        EditorDocument doc = NewDocWithEntities(2);
        doc.ExitIsolation(); // no throw
        Assert.False(doc.IsIsolated);
    }

    [Fact]
    public void IsVisibleUnderIsolation_Covers_Brush_Uids_Not_Projected_As_Objects()
    {
        EditorDocument doc = NewDocWithEntities(2);
        // Brush UID 500 is in the isolation set even though it is not a LevelObject.
        doc.IsolateSelection(new[] { 500 });
        Assert.True(doc.IsVisibleUnderIsolation(500));
        Assert.False(doc.IsVisibleUnderIsolation(999));

        doc.ExitIsolation();
        Assert.True(doc.IsVisibleUnderIsolation(999)); // no filter when not isolated
    }
}
