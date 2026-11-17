using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// RED and Alpine RED read a level's movers ONLY from the editor <c>groups</c> section (0x3000000): RED's loader
/// <c>FUN_0042f030</c> skips <c>movers</c> (0x2000) and <c>moving_groups</c> (0x3000), and its saver
/// <c>FUN_00430bf0</c> writes every moving group into both sections through the same writer. A mover missing from
/// 0x3000000 is folded into the static world by RED's rebuild (and dropped by its next save). These tests pin
/// Glacier's writer to RED's bytes (<see cref="MovingGroupMirror"/>): RED-authored levels mirror and stay
/// byte-identical, a Glacier mover is written the same way, and an older Glacier level is repaired on load.
/// </summary>
public sealed class MovingGroupMirrorTests
{
    private static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static (EditorDocument Doc, BrushEditor Brushes, MoverService Movers, int Box) NewLevel()
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        var be = new BrushEditor(doc);
        int box = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(4, 1, 0), Mat3.Identity);
        doc.MarkSaved();
        return (doc, be, new MoverService(doc), box);
    }

    private static string? CorpusLevel(string name)
    {
        if (Corpus.Directory is not { } dir)
        {
            return null;
        }

        string path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    private static RflSection? Section(RflFile rfl, SectionType type) => rfl.Sections.FirstOrDefault(s => s.TypeId == (uint)type);

    private static byte[] Body(RflFile rfl, SectionType type) => Section(rfl, type)!.GetBodyBytes(rfl.Context);

    private static RflFile Reload(byte[] bytes)
    {
        RflFile rfl = RflFile.Load(bytes);
        rfl.ParseAllKnownSections();
        return rfl;
    }

    private static List<Group> Groups(RflFile rfl, SectionType type) => ((GroupsSection?)Section(rfl, type)?.Content)?.Groups ?? new List<Group>();

    /// <summary>The rule itself, on RED-authored levels: every moving group is in 0x3000000, byte-identical and in order.</summary>
    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm06.rfl")]
    [InlineData("dm17.rfl")]
    public void Red_Authored_Movers_Are_Listed_In_The_Editor_Groups_Byte_Identical(string name)
    {
        if (CorpusLevel(name) is not { } path)
        {
            return; // corpus absent
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        List<Group> moving = Groups(rfl, SectionType.MovingGroups);
        Assert.NotEmpty(moving);
        Assert.False(MovingGroupMirror.NeedsRepair(rfl), name);

        // The groups section sits after brushes, where RED's group reader can resolve the member brushes.
        int brushes = rfl.Sections.FindIndex(s => s.TypeId == (uint)SectionType.Brushes);
        int editor = rfl.Sections.FindIndex(s => s.TypeId == (uint)SectionType.Groups);
        Assert.True(editor > brushes && brushes >= 0, name);
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm06.rfl")]
    public void Red_Mover_Levels_Stay_Byte_Identical_And_Project_Each_Keyframe_Once(string name)
    {
        if (CorpusLevel(name) is not { } path)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        EditorDocument doc = EditorDocument.OpenBytes(original, path);
        Assert.Equal(0, new MoverService(doc).RepairEditorGroupMirror());
        Assert.False(doc.IsDirty);
        Assert.True(original.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), name);

        int keyframes = new MoverService(doc).Movers.Sum(g => g.MovingData!.Keyframes.Count);
        Assert.Equal(keyframes, doc.Objects.Count(o => o.Kind == LevelObjectKind.Keyframe));
    }

    /// <summary>
    /// A mover made in Glacier is written exactly as RED writes one: the editor groups body equals the
    /// moving_groups body byte for byte, the section follows brushes, and the fresh keyframe carries RED's
    /// constructor defaults — the same values RED wrote for dmabrupt's untouched start keyframe.
    /// </summary>
    [Fact]
    public void New_Mover_Is_Written_Like_A_Red_Mover()
    {
        (EditorDocument doc, _, MoverService movers, int box) = NewLevel();
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "lift");
        movers.AddKeyframe(g, new Vec3(4, 6, 0), Mat3.Identity);

        RflFile saved = Reload(doc.SaveToBytes(updateTimestamp: false));
        Assert.Equal(Body(saved, SectionType.MovingGroups), Body(saved, SectionType.Groups));
        Assert.False(MovingGroupMirror.NeedsRepair(saved));
        Assert.True(saved.Sections.FindIndex(s => s.TypeId == (uint)SectionType.Groups) >
                    saved.Sections.FindIndex(s => s.TypeId == (uint)SectionType.Brushes));

        Group editorCopy = Assert.Single(Groups(saved, SectionType.Groups));
        Assert.Equal(new[] { box }, editorCopy.Brushes);
        Keyframe ours = editorCopy.MovingData!.Keyframes[0];

        // RED's keyframe constructor FUN_00452770: name "Keyframe", pause 0, travel times 1.0, accel/decel 0,
        // event/item UIDs -1, no rotation about the axis.
        Assert.Equal("Keyframe", ours.ScriptName);
        Assert.Equal(0f, ours.PauseTime);
        Assert.Equal(1f, ours.DepartTravelTime);
        Assert.Equal(1f, ours.ReturnTravelTime);
        Assert.Equal(0f, ours.AccelTime);
        Assert.Equal(0f, ours.DecelTime);

        // The fields level designers do not tune match what RED wrote for dmabrupt's start keyframe.
        if (CorpusLevel("dmabruptdecayrc2a27.rfl") is { } path)
        {
            RflFile dmabrupt = RflFile.Load(path);
            dmabrupt.ParseAllKnownSections();
            Keyframe red = Groups(dmabrupt, SectionType.Groups).First(x => x.Name == "bridge1").MovingData!.Keyframes[0];
            Assert.Equal(red.ScriptName, ours.ScriptName);
            Assert.Equal(red.HiddenInEditor, ours.HiddenInEditor);
            Assert.Equal(red.EventUid, ours.EventUid);
            Assert.Equal(red.ItemUid1, ours.ItemUid1);
            Assert.Equal(red.ItemUid2, ours.ItemUid2);
            Assert.Equal(red.DegreesAboutAxis, ours.DegreesAboutAxis);
        }
        else
        {
            Assert.Equal(-1, ours.EventUid);
            Assert.Equal(-1, ours.ItemUid1);
            Assert.Equal(-1, ours.ItemUid2);
        }

        // Projected once, from moving_groups.
        Assert.Equal(2, doc.Objects.Count(o => o.Kind == LevelObjectKind.Keyframe));
    }

    [Fact]
    public void Create_Mover_Adds_The_Editor_Copy_And_Undo_Removes_It_Byte_Exactly()
    {
        (EditorDocument doc, _, MoverService movers, int box) = NewLevel();
        bool hadGroups = Section(doc.Rfl, SectionType.Groups) is not null;
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "lift");
        Assert.Same(g, Assert.Single(Groups(doc.Rfl, SectionType.Groups), x => x.IsMoving != 0));
        doc.Undo.Undo();
        Assert.Equal(hadGroups, Section(doc.Rfl, SectionType.Groups) is not null);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        doc.Undo.Redo();
        Assert.Same(g, Assert.Single(Groups(doc.Rfl, SectionType.Groups), x => x.IsMoving != 0));
    }

    /// <summary>
    /// A level an earlier Glacier saved has its mover only in moving_groups. Opening it repairs it (once, marking the
    /// document dirty) and the next save writes the editor copy RED reads.
    /// </summary>
    [Fact]
    public void An_Earlier_Glacier_Mover_Level_Is_Repaired_On_Load()
    {
        (EditorDocument doc, _, MoverService movers, int box) = NewLevel();
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "lift");
        movers.AddKeyframe(g, new Vec3(4, 6, 0), Mat3.Identity);

        // The shape an earlier Glacier wrote: no editor groups section at all.
        RflFile old = Reload(doc.SaveToBytes(updateTimestamp: false));
        old.Sections.Remove(Section(old, SectionType.Groups)!);
        byte[] oldBytes = old.Save();
        Assert.True(MovingGroupMirror.NeedsRepair(Reload(oldBytes)));

        EditorDocument reopened = EditorDocument.OpenBytes(oldBytes);
        Assert.False(reopened.IsDirty);
        var service = new MoverService(reopened);
        Assert.Equal(1, service.RepairEditorGroupMirror());
        Assert.True(reopened.IsDirty);
        Assert.Equal(0, service.RepairEditorGroupMirror());
        Assert.Equal(2, reopened.Objects.Count(o => o.Kind == LevelObjectKind.Keyframe));

        RflFile repaired = Reload(reopened.SaveToBytes(updateTimestamp: false));
        Assert.Equal(Body(repaired, SectionType.MovingGroups), Body(repaired, SectionType.Groups));
    }

    /// <summary>
    /// The save path alone repairs too (CLI / automation saves), and the repair keeps user groups where they are:
    /// a stale copy is replaced in place, a dissolved mover's copy goes, a missing mover is appended.
    /// </summary>
    [Fact]
    public void Reconcile_Keeps_User_Groups_In_Place_And_Orders_Movers_Like_Moving_Groups()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, int box) = NewLevel();
        int box2 = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(-4, 1, 0), Mat3.Identity);
        int box3 = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 1, 6), Mat3.Identity);
        Group a = movers.CreateMover(new[] { box }, Array.Empty<int>(), "a");
        Group b = movers.CreateMover(new[] { box2 }, Array.Empty<int>(), "b");
        new GroupService(doc).CreateGroup("user", new[] { box3 }, Array.Empty<int>());

        // Editor list: [a, b, user]. Make it [user1, stale a, user, ghost] — b missing, a stale, ghost dissolved.
        List<Group> editor = Groups(doc.Rfl, SectionType.Groups);
        Group user = editor.Single(x => x.IsMoving == 0);
        var staleA = new Group
        {
            Name = "a",
            IsMoving = 1,
            MovingData = new MovingGroupData { Keyframes = { new Keyframe { Uid = a.MovingData!.Keyframes[0].Uid, Position = new Vec3(99, 0, 0) } } },
            Brushes = { box },
        };
        var ghost = new Group { Name = "ghost", IsMoving = 1, MovingData = new MovingGroupData(), Brushes = { 12345 } };
        var user1 = new Group { Name = "user1", Brushes = { box3 } };
        editor.Clear();
        editor.AddRange(new[] { user1, staleA, user, ghost });

        Assert.True(MovingGroupMirror.NeedsRepair(doc.Rfl));
        Assert.Equal(3, MovingGroupMirror.Reconcile(doc.Rfl)); // a rewritten, ghost removed, b appended
        Assert.Equal(new[] { user1, a, user, b }, Groups(doc.Rfl, SectionType.Groups));
        Assert.False(MovingGroupMirror.NeedsRepair(doc.Rfl));
        Assert.Equal(0, MovingGroupMirror.Reconcile(doc.Rfl));
    }

    [Fact]
    public void Editing_A_Red_Mover_Updates_Its_Editor_Copy()
    {
        if (CorpusLevel("dm06.rfl") is not { } path)
        {
            return;
        }

        EditorDocument doc = EditorDocument.Open(path);
        var movers = new MoverService(doc);
        Keyframe k = movers.Movers[0].MovingData!.Keyframes[^1];
        Vec3 before = k.Position;
        Vec3 after = before.Add(new Vec3(0, 1, 0));
        movers.EditKeyframe(k, "Move keyframe", x => x.Position = after, x => x.Position = before);

        RflFile saved = Reload(doc.SaveToBytes(updateTimestamp: false));
        Assert.False(MovingGroupMirror.NeedsRepair(saved));
        Group copy = Groups(saved, SectionType.Groups).First(x => x.IsMoving != 0 && x.MovingData!.Keyframes.Any(f => f.Uid == k.Uid));
        Assert.Equal(after, copy.MovingData!.Keyframes.Single(f => f.Uid == k.Uid).Position);
    }

    [Fact]
    public void Dissolving_A_Red_Mover_Removes_Its_Editor_Copy_And_Undo_Restores_The_Bytes()
    {
        if (CorpusLevel("dm06.rfl") is not { } path)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        EditorDocument doc = EditorDocument.OpenBytes(original, path);
        var movers = new MoverService(doc);
        int count = movers.Movers.Count;
        movers.DissolveMover(movers.Movers[0]);

        RflFile saved = Reload(doc.SaveToBytes(updateTimestamp: false));
        Assert.False(MovingGroupMirror.NeedsRepair(saved));
        Assert.Equal(count - 1, Groups(saved, SectionType.Groups).Count(x => x.IsMoving != 0));

        doc.Undo.Undo();
        Assert.True(original.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    /// <summary>The ratchet over the whole corpus (stock singleplayer and multiplayer, community, Alpine).</summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void Every_Corpus_Mover_Level_Mirrors_And_Stays_Byte_Identical()
    {
        if (Corpus.Directory is not { } dir)
        {
            return;
        }

        int checkedLevels = 0;
        foreach (string path in Directory.GetFiles(dir, "*.rfl", SearchOption.AllDirectories).OrderBy(p => p))
        {
            byte[] original = File.ReadAllBytes(path);
            RflFile rfl = RflFile.Load(original);
            rfl.ParseAllKnownSections();
            if (Groups(rfl, SectionType.MovingGroups).Count == 0)
            {
                continue;
            }

            Assert.False(MovingGroupMirror.NeedsRepair(rfl), path);
            EditorDocument doc = EditorDocument.OpenBytes(original, path);
            Assert.Equal(0, new MoverService(doc).RepairEditorGroupMirror());
            Assert.True(original.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), path);
            checkedLevels++;
        }

        Assert.True(checkedLevels >= 60, $"only {checkedLevels} mover levels found");
    }
}
