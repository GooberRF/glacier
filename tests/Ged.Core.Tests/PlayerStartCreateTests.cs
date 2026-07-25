using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for <see cref="EditorDocument.CreatePlayerStart"/>: the "create a spawn when the level
/// has none" path behind Move Player Start Here. Creation is one undo entry (undo removes the
/// section), a level only ever gets one start, and the writer recomputes player_start_offset.
/// </summary>
public sealed class PlayerStartCreateTests
{
    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D; // Alpine
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void CreatePlayerStart_Adds_A_Start_When_Absent()
    {
        EditorDocument doc = NewDoc();
        Assert.DoesNotContain(doc.Objects, o => o.Kind == LevelObjectKind.PlayerStart);

        LevelObject created = doc.CreatePlayerStart(new Vec3(2, 3, 4));

        Assert.Equal(LevelObjectKind.PlayerStart, created.Kind);
        var section = doc.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>().Single();
        Assert.Equal(new Vec3(2, 3, 4), section.Position);
        Assert.Equal(Mat3.Identity, section.Rotation);
    }

    [Fact]
    public void CreatePlayerStart_Is_One_Undo_Entry_That_Removes_The_Section()
    {
        EditorDocument doc = NewDoc();
        doc.CreatePlayerStart(new Vec3(2, 3, 4));
        Assert.Single(doc.Objects, o => o.Kind == LevelObjectKind.PlayerStart);

        doc.Undo.Undo();
        Assert.DoesNotContain(doc.Objects, o => o.Kind == LevelObjectKind.PlayerStart);
        Assert.DoesNotContain(doc.Rfl.Sections.Select(s => s.Content), c => c is PlayerStartSection);

        doc.Undo.Redo();
        Assert.Single(doc.Objects, o => o.Kind == LevelObjectKind.PlayerStart);
    }

    [Fact]
    public void CreatePlayerStart_Returns_The_Existing_Start_Without_Duplicating()
    {
        EditorDocument doc = NewDoc();
        LevelObject first = doc.CreatePlayerStart(new Vec3(2, 3, 4));

        LevelObject again = doc.CreatePlayerStart(new Vec3(9, 9, 9));

        Assert.Same(first, again);
        Assert.Single(doc.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>());
        // The second call did not move the existing start.
        Assert.Equal(new Vec3(2, 3, 4), doc.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>().Single().Position);
    }

    [Fact]
    public void CreatePlayerStart_Sets_The_Header_Offset_On_Save()
    {
        EditorDocument doc = NewDoc();
        doc.CreatePlayerStart(new Vec3(2, 3, 4));

        byte[] bytes = doc.SaveToBytes();
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        Assert.True(reloaded.Header.PlayerStartOffset > 0);
        Assert.Equal(new Vec3(2, 3, 4), reloaded.Sections.Select(s => s.Content).OfType<PlayerStartSection>().Single().Position);
    }
}
