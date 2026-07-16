using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Group gates: create/save/reload round-trip; deep-duplicate with fresh UIDs and
/// remapped intra-group links; and Alpine group Mirror reflecting member brushes
/// (valid plane normals) and objects (position + orientation) together.
/// </summary>
public class GroupServiceTests
{
    private static EditorDocument NewAlpineDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "group.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Create_Group_Round_Trips()
    {
        EditorDocument doc = NewAlpineDoc();
        var groups = new GroupService(doc);
        LevelObject a = doc.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(0, 0, 0))!;
        LevelObject b = doc.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(1, 0, 0))!;

        groups.CreateGroup("Trap", System.Array.Empty<int>(), new[] { a.Uid, b.Uid });

        var reloaded = RflFile.Load(doc.SaveToBytes());
        reloaded.ParseAllKnownSections();
        GroupsSection gs = reloaded.Sections
            .Where(s => s.TypeId == (uint)SectionType.Groups)
            .Select(s => (GroupsSection)s.Content!).Single();
        Group g = Assert.Single(gs.Groups);
        Assert.Equal("Trap", g.Name);
        Assert.Equal(new[] { a.Uid, b.Uid }, g.Objects);
    }

    [Fact]
    public void Duplicate_Group_Gives_Fresh_Uids_And_Remaps_Intra_Group_Links()
    {
        EditorDocument doc = NewAlpineDoc();
        var groups = new GroupService(doc);
        LevelObject a = doc.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(0, 0, 0))!;
        LevelObject b = doc.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(1, 0, 0))!;

        // A links to B (an intra-group link) and to an external UID 9999.
        ((RflEvent)a.Model).Links.Add(b.Uid);
        ((RflEvent)a.Model).Links.Add(9999);

        Group original = groups.CreateGroup("Trap", System.Array.Empty<int>(), new[] { a.Uid, b.Uid });
        Group copy = groups.Duplicate(original);

        // Fresh, unused UIDs.
        Assert.Equal(2, copy.Objects.Count);
        Assert.DoesNotContain(a.Uid, copy.Objects);
        Assert.DoesNotContain(b.Uid, copy.Objects);

        int cloneAUid = copy.Objects[0];
        int cloneBUid = copy.Objects[1];
        var cloneA = (RflEvent)doc.FindByUid(cloneAUid)!.Model;

        // The intra-group link points at the CLONE of B; the external link is unchanged.
        Assert.Contains(cloneBUid, cloneA.Links);
        Assert.DoesNotContain(b.Uid, cloneA.Links);
        Assert.Contains(9999, cloneA.Links);

        // The original's links are untouched.
        Assert.Contains(b.Uid, ((RflEvent)a.Model).Links);
    }

    [Fact]
    public void Mirror_Group_Reflects_Brush_And_Object_Together()
    {
        EditorDocument doc = NewAlpineDoc();
        var editor = new BrushEditor(doc);
        int brushUid = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(2, 0, 0))!;

        var groups = new GroupService(doc);
        Group g = groups.CreateGroup("Rig", new[] { brushUid }, new[] { light.Uid });

        groups.MirrorGroup(g, axis: 0); // mirror across X, pivot = centroid of (0,0,0) and (2,0,0) = (1,0,0)

        // The light reflected across X = 1: 2 -> 0.
        Assert.Equal(0f, light.Position.X, 3);

        // The brush centroid reflected: 0 -> 2.
        Brush brush = editor.FindBrush(brushUid)!;
        Assert.Equal(2f, BrushTransform.WorldCentroid(brush).X, 3);

        // Every mirrored face carries a valid (unit-length) plane normal.
        foreach (Face f in brush.Geometry.Faces)
        {
            Assert.Equal(1f, f.Plane.Normal.Length(), 3);
        }

        // Undo restores the pre-mirror positions.
        doc.Undo.Undo();
        Assert.Equal(2f, light.Position.X, 3);
        Assert.Equal(0f, BrushTransform.WorldCentroid(editor.FindBrush(brushUid)!).X, 3);
    }
}
