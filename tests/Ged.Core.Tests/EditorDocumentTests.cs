using System;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

public sealed class EditorDocumentTests
{
    private static EditorDocument NewDocWithEntities(int count)
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        var es = new EntitiesSection();
        for (int i = 0; i < count; i++)
        {
            es.Entities.Add(new Entity
            {
                Uid = i + 1,
                ClassName = "Guard",
                ScriptName = $"e{i + 1}",
                Position = new Vec3(i, 0, 0),
            });
        }

        rfl.Sections.Add(new RflSection((uint)SectionType.Entities, Array.Empty<byte>()) { Content = es });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Enumerates_All_Objects()
    {
        EditorDocument doc = NewDocWithEntities(3);
        Assert.Equal(3, doc.Objects.Count);
        Assert.All(doc.Objects, o => Assert.Equal(LevelObjectKind.Entity, o.Kind));
        Assert.NotNull(doc.FindByUid(2));
    }

    [Fact]
    public void Invert_Selection_Swaps_Selected_And_Unselected()
    {
        EditorDocument doc = NewDocWithEntities(3);
        LevelObject first = doc.Objects[0];
        doc.Select(first);

        doc.InvertSelection();

        Assert.False(doc.IsSelected(first));
        Assert.Equal(2, doc.Selection.Count);
    }

    [Fact]
    public void Select_By_Uid_Selects_Match()
    {
        EditorDocument doc = NewDocWithEntities(3);
        LevelObject? o = doc.SelectByUid(2);
        Assert.NotNull(o);
        Assert.Equal(2, o!.Uid);
        Assert.Single(doc.Selection);
    }

    [Fact]
    public void Hide_Selected_Sets_Flag_Dirties_Section_And_Is_Undoable()
    {
        EditorDocument doc = NewDocWithEntities(3);
        LevelObject o = doc.Objects[0];
        doc.Select(o);
        RflSection section = o.Section;

        doc.HideSelected();

        Assert.True(o.Hidden);
        Assert.True(section.Dirty);
        Assert.True(doc.IsDirty);

        doc.Undo.Undo();
        Assert.False(o.Hidden);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Invert_Hidden_Toggles_Every_Object()
    {
        EditorDocument doc = NewDocWithEntities(3);
        doc.Objects[1].Hidden = true;

        doc.InvertHidden();

        Assert.True(doc.Objects[0].Hidden);
        Assert.False(doc.Objects[1].Hidden);
        Assert.True(doc.Objects[2].Hidden);
    }

    [Fact]
    public void Hide_Except_Clutter_Entities_Keeps_Entities_Visible()
    {
        EditorDocument doc = NewDocWithEntities(2);
        doc.HideExceptClutterEntities();
        Assert.All(doc.Objects, o => Assert.False(o.Hidden)); // all are entities → none hidden
    }

    [Fact]
    public void Lock_Is_Session_State_And_Does_Not_Dirty()
    {
        EditorDocument doc = NewDocWithEntities(2);
        LevelObject o = doc.Objects[0];
        doc.Select(o);
        doc.LockSelected();

        Assert.True(doc.IsLocked(o));
        Assert.False(doc.IsDirty);

        doc.UnlockAll();
        Assert.False(doc.IsLocked(o));
    }

    [Fact]
    public void Copy_Paste_Adds_Clone_With_Fresh_Uid()
    {
        EditorDocument doc = NewDocWithEntities(2);
        LevelObject src = doc.Objects[0];
        doc.Select(src);
        doc.CopySelection();

        var newUids = doc.Paste();

        Assert.Single(newUids);
        Assert.Equal(3, doc.Objects.Count);
        LevelObject pasted = doc.FindByUid(newUids[0])!;
        Assert.NotEqual(src.Uid, pasted.Uid);
        Assert.Equal("Guard", pasted.ClassName); // fields cloned
        Assert.True(pasted.Section.Dirty);
        Assert.NotSame(src.Model, pasted.Model); // deep clone, not shared
    }

    [Fact]
    public void Paste_Is_Undoable()
    {
        EditorDocument doc = NewDocWithEntities(2);
        doc.Select(doc.Objects[0]);
        doc.CopySelection();
        doc.Paste();
        Assert.Equal(3, doc.Objects.Count);

        doc.Undo.Undo();
        Assert.Equal(2, doc.Objects.Count);

        doc.Undo.Redo();
        Assert.Equal(3, doc.Objects.Count);
    }

    [Fact]
    public void Repeated_Paste_Gives_Distinct_Uids()
    {
        EditorDocument doc = NewDocWithEntities(1);
        doc.Select(doc.Objects[0]);
        doc.CopySelection();
        int a = doc.Paste()[0];
        int b = doc.Paste()[0];
        Assert.NotEqual(a, b);
        Assert.Equal(3, doc.Objects.Count);
    }

    [Fact]
    public void Delete_Removes_And_Undo_Restores()
    {
        EditorDocument doc = NewDocWithEntities(3);
        LevelObject middle = doc.Objects[1];
        int uid = middle.Uid;
        doc.Select(middle);

        doc.DeleteSelection();
        Assert.Equal(2, doc.Objects.Count);
        Assert.Null(doc.FindByUid(uid));

        doc.Undo.Undo();
        Assert.Equal(3, doc.Objects.Count);
        Assert.NotNull(doc.FindByUid(uid));
    }

    [Fact]
    public void Cut_Copies_Then_Deletes()
    {
        EditorDocument doc = NewDocWithEntities(2);
        doc.Select(doc.Objects[0]);
        doc.CutSelection();
        Assert.Single(doc.Objects);
        Assert.True(doc.HasClipboard);

        doc.Paste();
        Assert.Equal(2, doc.Objects.Count);
    }

    [Fact]
    public void Property_Edit_Through_EditValue_Is_Undoable_And_Dirties()
    {
        EditorDocument doc = NewDocWithEntities(1);
        LevelObject o = doc.Objects[0];
        var entity = (Entity)o.Model;
        string oldName = entity.ScriptName;

        doc.EditValue(o.Section, "Rename", oldName, "renamed", v => entity.ScriptName = v);

        Assert.Equal("renamed", entity.ScriptName);
        Assert.True(o.Section.Dirty);
        Assert.True(doc.IsDirty);

        doc.Undo.Undo();
        Assert.Equal(oldName, entity.ScriptName);
    }

    [Fact]
    public void Coalesced_Property_Edits_Collapse()
    {
        EditorDocument doc = NewDocWithEntities(1);
        LevelObject o = doc.Objects[0];
        var entity = (Entity)o.Model;

        doc.EditValue(o.Section, "Move", entity.Position, new Vec3(1, 0, 0), v => entity.Position = v, "move-uid");
        doc.EditValue(o.Section, "Move", new Vec3(1, 0, 0), new Vec3(2, 0, 0), v => entity.Position = v, "move-uid");

        Assert.Equal(new Vec3(2, 0, 0), entity.Position);
        Assert.Equal(1, doc.Undo.Position); // one coalesced entry
        doc.Undo.Undo();
        Assert.Equal(new Vec3(0, 0, 0), entity.Position);
    }

    // ---- Round-trip invariant through EditorDocument (extends the P1 test) ----

    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Open_Then_Save_Through_Document_Is_Byte_Identical(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        byte[] original = File.ReadAllBytes(path);

        EditorDocument doc = EditorDocument.OpenBytes(original, path);
        Assert.False(doc.IsDirty); // opening (and enumerating objects) must not dirty anything

        byte[] resaved = doc.SaveToBytes(updateTimestamp: false);

        Assert.Equal(original.Length, resaved.Length);
        Assert.True(original.AsSpan().SequenceEqual(resaved),
            $"{fileName}: bytes differ after open/enumerate/save through EditorDocument.");
    }

    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Corpus_Levels_Enumerate_Objects(string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        EditorDocument doc = EditorDocument.Open(path);

        // Every enumerated object must resolve through the UID registry (player
        // start reports uid 0 and is excluded).
        foreach (LevelObject o in doc.Objects.Where(o => o.Uid != 0))
        {
            Assert.NotNull(doc.FindByUid(o.Uid));
        }
    }
}
