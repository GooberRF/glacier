using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// .rfg export/import gate: export a selection to an .rfg and re-import it into a
/// fresh document at a camera offset, asserting every UID is remapped, intra-import
/// links are repaired onto the clones, and positions are offset.
/// </summary>
public class RfgInteropTests
{
    private static EditorDocument NewAlpineDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "src.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Export_Then_Import_Remaps_Uids_Links_And_Offsets_Positions()
    {
        EditorDocument src = NewAlpineDoc();
        var editor = new BrushEditor(src);
        int brushUid = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        LevelObject a = src.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(1, 0, 0))!;
        LevelObject b = src.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(2, 0, 0))!;
        ((RflEvent)a.Model).Links.Add(b.Uid);

        RfgFile rfg = RfgInterop.Export(src, new[] { brushUid }, new[] { a.Uid, b.Uid }, alpine: true);

        // Persisted .rfg round-trips through the file format.
        rfg = RfgFile.Load(rfg.Save());
        Assert.Equal(0x12C, rfg.Version);

        // Import into a document pre-seeded so fresh UIDs are unambiguously new.
        EditorDocument dst = NewAlpineDoc();
        for (int i = 0; i < 8; i++)
        {
            dst.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(0, -50, i));
        }

        int firstFresh = dst.Objects.Max(o => o.Uid) + 1;
        var offset = new Vec3(10, 0, 0);
        System.Collections.Generic.IReadOnlyList<int> placed = RfgInterop.Import(dst, rfg, offset);

        Assert.Equal(3, placed.Count); // 1 brush + 2 events
        Assert.All(placed, uid => Assert.True(uid >= firstFresh, "Imported UID was not freshly allocated."));

        // The imported events (fresh UIDs) with the remapped intra-import link.
        var imported = dst.Objects.Where(o => o.Kind == LevelObjectKind.Event && placed.Contains(o.Uid)).ToList();
        Assert.Equal(2, imported.Count);
        LevelObject ca = imported.Single(o => ((RflEvent)o.Model).Links.Count > 0);
        LevelObject cb = imported.Single(o => o.Uid != ca.Uid);

        Assert.NotEqual(ca.Uid, cb.Uid);
        Assert.Equal(11f, ca.Position.X, 3); // 1 + 10

        // The link now targets the clone of B (remapped into the destination space).
        Assert.Equal(new[] { cb.Uid }, ((RflEvent)ca.Model).Links);

        // The brush is placed at the offset with a fresh UID.
        dst.Rfl.ParseAllKnownSections();
        var brushes = dst.Rfl.Sections.Select(s => s.Content).OfType<Ged.Core.IO.Rfl.Sections.BrushesSection>().Single();
        Brush placedBrush = Assert.Single(brushes.Brushes);
        Assert.Contains(placedBrush.Uid, placed);
        Assert.Equal(10f, placedBrush.Position.X, 3);

        // Import is one undo entry: every imported object is gone, the pre-seeded ones remain.
        dst.Undo.Undo();
        Assert.All(placed, uid => Assert.Null(dst.FindByUid(uid)));
    }
}
