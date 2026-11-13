using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The Core pieces behind scripted movers, groups and tags: creating a mover or group on a level without those sections
/// is undone byte-exactly (the sections are created inside the command), <see cref="MoverService.SyncMemberCopies"/>
/// refreshes stale movers-section copies only, provenance tags encode canonically, and
/// <see cref="GedObjectMetadataService.SetBlocks"/> batches, drops empty chunks and keeps
/// other block types.
/// </summary>
public sealed class MoverSyncAndTagStorageTests
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

    private static bool Has(EditorDocument doc, SectionType type) => doc.Rfl.Sections.Any(s => s.TypeId == (uint)type);

    [Fact]
    public void Create_Mover_On_A_Level_Without_Mover_Sections_Undoes_Byte_Exactly()
    {
        (EditorDocument doc, _, MoverService movers, int box) = NewLevel();
        Assert.False(Has(doc, SectionType.Movers));
        Assert.False(Has(doc, SectionType.MovingGroups));
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        Assert.True(Has(doc, SectionType.Movers));
        Assert.True(Has(doc, SectionType.MovingGroups));

        doc.Undo.Undo();
        Assert.False(Has(doc, SectionType.Movers));
        Assert.False(Has(doc, SectionType.MovingGroups));
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        doc.Undo.Redo(); // redo re-inserts them
        Assert.Single(movers.Movers);
        Assert.Single(movers.MoverBrushes);
    }

    [Fact]
    public void Create_Mover_On_A_Corpus_Level_Undoes_Byte_Exactly()
    {
        string? path = Corpus.RflFiles.OrderBy(p => new FileInfo(p).Length).FirstOrDefault();
        if (path is null)
        {
            return; // corpus absent
        }

        EditorDocument doc = EditorDocument.Open(path);
        var be = new BrushEditor(doc);
        var movers = new MoverService(doc);
        int brush = be.Brushes.Select(b => b.Uid).First(u => movers.FindGroupForMember(u) is null);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        movers.CreateMover(new[] { brush }, Array.Empty<int>(), "Probe");
        doc.Undo.Undo();
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), Path.GetFileName(path));
    }

    [Fact]
    public void Create_Group_On_A_Level_Without_A_Groups_Section_Undoes_Byte_Exactly()
    {
        (EditorDocument doc, _, _, int box) = NewLevel();
        var groups = new GroupService(doc);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        groups.CreateGroup("g", new[] { box }, Array.Empty<int>());
        Assert.True(Has(doc, SectionType.Groups));
        doc.Undo.Undo();
        Assert.False(Has(doc, SectionType.Groups));
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Sync_Refreshes_A_Stale_Copy_And_Member_Transform_And_Undoes_Exactly()
    {
        (EditorDocument doc, BrushEditor be, MoverService movers, int box) = NewLevel();
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        Vec3 start = g.MovingData!.Keyframes[0].Position;

        // Unedited: nothing to do, and no undo entry.
        int nodes = doc.Undo.NodeCount;
        Assert.False(movers.SyncMemberCopies(g));
        Assert.Equal(nodes, doc.Undo.NodeCount);

        // The GUI edit path changes only the brushes-section member; the movers copy stays behind.
        be.EditBrushes(new[] { box }, "Move", b => { BrushTransform.Move(b, new Vec3(0, 3, 0)); return OpResult.Ok(); });
        Brush member = be.FindBrush(box)!;
        Brush copy = movers.MoverBrushes.Single();
        Assert.NotEqual(member.Position, copy.Position);
        byte[] stale = doc.SaveToBytes(updateTimestamp: false);

        Assert.True(movers.SyncMemberCopies(g));
        Assert.Equal(member.Position, copy.Position);
        Assert.Equal(member.Position.Sub(start), g.MovingData.MemberTransforms.Single(t => t.Uid == box).Position);
        Assert.NotSame(member.Geometry, copy.Geometry); // an independent clone

        doc.Undo.Undo();
        Assert.Equal(stale, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Sync_Leaves_Build_Owned_State_Of_An_Unchanged_Copy_Alone()
    {
        (EditorDocument doc, _, MoverService movers, int box) = NewLevel();
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        Brush copy = movers.MoverBrushes.Single();
        foreach (Face f in copy.Geometry.Faces)
        {
            f.FaceId += 100; // what the build's global renumbering does to a mover copy
        }

        Geometry geometry = copy.Geometry;
        Assert.False(movers.SyncMemberCopies(g));
        Assert.Same(geometry, copy.Geometry);
    }

    // ---- tags --------------------------------------------------------------------------

    [Fact]
    public void Tags_Encode_Canonically_And_Decode_Tolerantly()
    {
        var tags = new Dictionary<string, string> { ["b"] = "2", ["a"] = "x<y" };
        byte[] payload = ObjectTags.Encode(tags)!;
        Assert.Equal("{\"a\":\"x\\u003Cy\",\"b\":\"2\"}", Encoding.UTF8.GetString(payload));
        Assert.NotEqual(0xEF, payload[0]); // no BOM
        Assert.Equal(tags, ObjectTags.Decode(payload));
        Assert.Null(ObjectTags.Encode(new Dictionary<string, string>()));

        Assert.False(ObjectTags.TryDecode(Encoding.UTF8.GetBytes("not json"), out var bad));
        Assert.Empty(bad);
        Assert.False(ObjectTags.TryDecode(Encoding.UTF8.GetBytes("[1]"), out _));
        Assert.False(ObjectTags.TryDecode(Encoding.UTF8.GetBytes("{\"a\":1}"), out _));

        Assert.True(ObjectTags.IsValidKey("ai.session"));
        Assert.True(ObjectTags.IsValidKey("kit-2_x.y"));
        Assert.False(ObjectTags.IsValidKey("Kit"));
        Assert.False(ObjectTags.IsValidKey(".kit"));
        Assert.False(ObjectTags.IsValidKey(new string('a', 65)));
        Assert.Null(ObjectTags.ValueProblem(new string('v', ObjectTags.MaxValueLength)));
        Assert.NotNull(ObjectTags.ValueProblem(new string('v', ObjectTags.MaxValueLength + 1)));
        Assert.NotNull(ObjectTags.ValueProblem("a\nb"));
    }

    [Fact]
    public void SetBlocks_Batches_Into_One_Step_And_Drops_The_Chunk_When_Empty()
    {
        (EditorDocument doc, _, _, int box) = NewLevel();
        var meta = new GedObjectMetadataService(doc);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;

        byte[] payload = ObjectTags.Encode(new Dictionary<string, string> { ["k"] = "v" })!;
        meta.SetBlocks(Enumerable.Range(0, 500).Select(i => (box + 1000 + i, GedMetadataType.Tags, (byte[]?)payload)).ToList(), "Tag");
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        Assert.Equal(500, meta.AllTags().Count);
        Assert.True(Has(doc, SectionType.GedObjectMetadata));

        // Removing every block drops the entries and finally the chunk itself.
        meta.SetBlocks(Enumerable.Range(0, 500).Select(i => (box + 1000 + i, GedMetadataType.Tags, (byte[]?)null)).ToList(), "Untag");
        Assert.False(Has(doc, SectionType.GedObjectMetadata));
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        // A no-op batch records nothing.
        nodes = doc.Undo.NodeCount;
        meta.SetBlocks(new[] { (box, GedMetadataType.Tags, (byte[]?)null) }, "Untag");
        Assert.Equal(nodes, doc.Undo.NodeCount);
    }

    [Fact]
    public void Tag_Blocks_Coexist_With_Cookies_And_Unknown_Blocks_And_Undo_Restores_A_Loaded_Chunk()
    {
        (EditorDocument doc, _, _, _) = NewLevel();
        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 3, 0))!;
        var meta = new GedObjectMetadataService(doc);
        meta.SetCookie(light.Uid, "cookie.tga");
        meta.SetBlock(light.Uid, (GedMetadataType)77, new byte[] { 1, 2, 3 });

        // Reload so the chunk comes from bytes, as on a real level.
        EditorDocument loaded = EditorDocument.OpenBytes(doc.SaveToBytes(updateTimestamp: false));
        var m2 = new GedObjectMetadataService(loaded);
        byte[] before = loaded.SaveToBytes(updateTimestamp: false);
        int index = loaded.Rfl.Sections.FindIndex(s => s.TypeId == (uint)SectionType.GedObjectMetadata);

        m2.SetBlock(light.Uid, GedMetadataType.Tags, ObjectTags.Encode(new Dictionary<string, string> { ["a"] = "1" }));
        Assert.Equal("cookie.tga", m2.Cookie(light.Uid));
        Assert.Equal(new byte[] { 1, 2, 3 }, m2.BlockPayload(light.Uid, (GedMetadataType)77));
        Assert.Equal("1", m2.Tags(light.Uid)["a"]);

        m2.SetBlock(light.Uid, GedMetadataType.Tags, null); // last tag gone: the block goes, the entry stays
        Assert.Empty(m2.Tags(light.Uid));
        Assert.Equal("cookie.tga", m2.Cookie(light.Uid));

        loaded.Undo.Undo();
        loaded.Undo.Undo();
        Assert.Equal(index, loaded.Rfl.Sections.FindIndex(s => s.TypeId == (uint)SectionType.GedObjectMetadata));
        Assert.Equal(before, loaded.SaveToBytes(updateTimestamp: false));
    }
}
