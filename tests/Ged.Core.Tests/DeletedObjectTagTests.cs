using System;
using System.Collections.Generic;
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
/// Tags never outlive what they label. Deleting a brush or an object drops its Tags block inside the
/// delete command (undo restores it), and a level's UID allocator never re-issues a UID that still
/// carries metadata — so after save → reopen a newly created brush or object cannot inherit the tags
/// of something deleted earlier.
/// </summary>
public sealed class DeletedObjectTagTests
{
    private static EditorDocument NewLevel() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    private static ScriptRunResult Run(EditorDocument doc, string lua)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, lua);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r;
    }

    private static bool HasMetadataChunk(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        return doc.Rfl.Sections.Any(s => s.Content is GedObjectMetadataSection);
    }

    [Fact]
    public void A_Deleted_Tagged_Brush_Does_Not_Pass_Its_Tags_To_A_New_Brush_After_Reload()
    {
        EditorDocument doc = NewLevel();
        int uid = int.Parse(Run(doc, "return level.brush{pos = {0, 2, 0}, size = 4, air = true, tags = {['kit.name'] = 'room1', kit = 'room'}}.uid").ReturnValue!,
            System.Globalization.CultureInfo.InvariantCulture);
        Run(doc, $"level.find_brush({uid}):delete()");
        Assert.Empty(new GedObjectMetadataService(doc).Tags(uid));

        EditorDocument reloaded = EditorDocument.OpenBytes(doc.SaveToBytes(updateTimestamp: false), "t.rfl");
        ScriptRunResult created = Run(reloaded, "local b = level.brush{pos = {0, 2, 0}, size = 4, air = true} return b.uid .. ':' .. #level.brushes_tagged('kit.name', 'room1').brushes");
        string[] parts = created.ReturnValue!.Split(':');
        Assert.Equal("0", parts[1]);
        int fresh = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Empty(new GedObjectMetadataService(reloaded).Tags(fresh));
    }

    [Fact]
    public void Undoing_The_Delete_Restores_The_Tags_And_The_Bytes()
    {
        EditorDocument doc = NewLevel();
        Run(doc, "level.brush{pos = {0, 2, 0}, size = 4, air = true, tags = {kit = 'room'}}\n" +
                 "level.place{kind = 'light', pos = {0, 3, 0}, tags = {kit = 'light_room'}}");
        byte[] tagged = doc.SaveToBytes(updateTimestamp: false);
        Run(doc, "level.all_brushes():delete() level.objects_of('Light')[1]:delete()");
        Assert.Empty(new GedObjectMetadataService(doc).AllTags());
        Assert.False(HasMetadataChunk(doc)); // the last tags gone: the chunk goes too

        doc.Undo.Undo();
        Assert.Equal(tagged, doc.SaveToBytes(updateTimestamp: false));
        Assert.Equal(2, new GedObjectMetadataService(doc).AllTags().Count);
    }

    [Fact]
    public void Deleting_Through_The_Editor_Commands_Drops_Only_The_Tags_Block()
    {
        EditorDocument doc = NewLevel();
        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 3, 0))!;
        var meta = new GedObjectMetadataService(doc);
        meta.SetBlock(light.Uid, GedMetadataType.Tags, ObjectTags.Encode(new Dictionary<string, string> { ["k"] = "v" }));
        meta.SetCookie(light.Uid, "cookie.tga");
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        doc.SelectMany(new[] { doc.FindByUid(light.Uid)! });
        doc.DeleteSelection();
        Assert.Empty(meta.Tags(light.Uid));
        Assert.Equal("cookie.tga", meta.Cookie(light.Uid)); // other block types are left alone

        // The cookie keeps the UID reserved: a new object never re-uses it.
        EditorDocument reloaded = EditorDocument.OpenBytes(doc.SaveToBytes(updateTimestamp: false));
        Assert.True(reloaded.AllocateUid() > light.Uid);

        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void A_Uid_Carrying_Metadata_Is_Never_Reallocated()
    {
        // An orphaned entry (left by an older save) above every live UID: the allocator skips past it.
        EditorDocument doc = NewLevel();
        int brush = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 1, 0), Mat3.Identity);
        int orphan = brush + 50;
        new GedObjectMetadataService(doc).SetBlock(orphan, GedMetadataType.Tags, ObjectTags.Encode(new Dictionary<string, string> { ["kit.name"] = "ghost" }));

        EditorDocument reloaded = EditorDocument.OpenBytes(doc.SaveToBytes(updateTimestamp: false));
        int next = reloaded.AllocateUid();
        Assert.True(next > orphan, $"allocated {next}, orphan metadata at {orphan}");
    }

    [Fact]
    public void Deleting_Untagged_Content_Writes_No_Metadata()
    {
        EditorDocument doc = NewLevel();
        byte[] pristine = doc.SaveToBytes(updateTimestamp: false);
        Run(doc, "local b = level.brush{pos = {0, 2, 0}, size = 4, air = true} local l = level.place{kind = 'light', pos = {0, 3, 0}}\n" +
                 "b:delete() l:delete()");
        Assert.False(HasMetadataChunk(doc));
        doc.Undo.Undo();
        Assert.Equal(pristine, doc.SaveToBytes(updateTimestamp: false));
    }
}
