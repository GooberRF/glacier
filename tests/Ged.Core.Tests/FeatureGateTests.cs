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
/// The FeatureGate compatibility analysis (kept as infrastructure — it will gate
/// future &gt;305 versions). GED always saves Alpine v305, so this never blocks a
/// save; it reports whether a level's Alpine-only features would also run on the
/// stock RF (v200) reference engine. Building a level with Alpine features itemizes
/// them; stripping them clears the report; and every save produces a byte-stable v305
/// file.
/// </summary>
public class FeatureGateTests
{
    /// <summary>An Alpine (v300) document seeded with one usage of every gated feature category.</summary>
    private static EditorDocument BuildAlpineLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion; // v305
        rfl.Header.LevelName = "gate.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);

        // Alpine event.
        doc.PlaceEvent(EventSchemaCatalog.Find("HUD_Message")!, new Vec3(1, 2, 3));

        // Alpine object sections.
        ((AlpineMeshObjectsSection)rfl.GetOrCreateSection(SectionType.AlpineMeshObjects,
            () => new AlpineMeshObjectsSection()).Content!).Meshes.Add(new AlpineMeshObject { Uid = 900, MeshFilename = "x.v3m" });
        ((AlpineNoteObjectsSection)rfl.GetOrCreateSection(SectionType.AlpineNoteObjects,
            () => new AlpineNoteObjectsSection()).Content!).Notes.Add(new AlpineNoteObject { Uid = 901 });
        ((AlpineCoronaObjectsSection)rfl.GetOrCreateSection(SectionType.AlpineCoronaObjects,
            () => new AlpineCoronaObjectsSection()).Content!).Coronas.Add(new AlpineCoronaObject { Uid = 902 });
        ((AlpineBagObjectsSection)rfl.GetOrCreateSection(SectionType.AlpineBagObjects,
            () => new AlpineBagObjectsSection()).Content!).Bags.Add(new AlpineBagObject { Uid = 903 });

        // alpine_level_properties: flags + geoable/breakable/hold-open tables.
        var alp = (AlpineLevelPropertiesSection)rfl.GetOrCreateSection(SectionType.AlpineLevelProperties,
            () => new AlpineLevelPropertiesSection { Version = 4 }).Content!;
        alp.LegacyMovers = 1;
        alp.GeoableEntries.Add(new AlpineGeoableEntry { BrushUid = 10, RoomUid = 20 });
        alp.BreakableEntries.Add(new AlpineBreakableEntry { BrushUid = 11, RoomUid = 21, Material = 1 });
        alp.HoldOpenKeyframeUids.Add(500);

        return doc;
    }

    [Fact]
    public void Stock_Compatibility_Check_Itemizes_Every_Alpine_Feature()
    {
        EditorDocument doc = BuildAlpineLevel();

        // The Alpine target is always clear.
        Assert.False(doc.EvaluateSaveTarget(SaveTarget.Alpine).Blocked);

        FeatureGateReport report = doc.EvaluateSaveTarget(SaveTarget.StockRf);
        Assert.True(report.Blocked);

        // Every seeded feature category is itemized.
        string all = string.Join("\n", report.Findings.Select(f => f.Feature));
        Assert.Contains("Alpine event \"HUD_Message\"", all);
        Assert.Contains("Mesh objects (Alpine)", all);
        Assert.Contains("Note objects (Alpine)", all);
        Assert.Contains("Corona objects (Alpine)", all);
        Assert.Contains("Bag objects (Alpine)", all);
        Assert.Contains("legacy movers", all);
        Assert.Contains("Geoable brushes", all);
        Assert.Contains("Breakable brushes", all);
        Assert.Contains("Hold Open", all);

        // The mesh finding carries its jump-list uid; the summary lists it.
        GateFinding mesh = report.Findings.First(f => f.Feature.StartsWith("Mesh objects", System.StringComparison.Ordinal));
        Assert.Contains(900, mesh.Uids);
        Assert.Contains("uid 900", report.Summary());
    }

    [Fact]
    public void Stripping_Alpine_Features_Clears_The_Stock_Compatibility_Report()
    {
        EditorDocument doc = BuildAlpineLevel();

        // Remove every Alpine-only usage.
        doc.Rfl.Sections.RemoveAll(s => RflFile.IsAlpineOnlySection(s.TypeId));
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.Content is EventsSection es)
            {
                es.Events.RemoveAll(e => (EventSchemaCatalog.Find(e.ClassName)?.MinVersion ?? 0) >= SaveTargets.FirstAlpineVersion);
                s.Dirty = true;
            }
        }

        // With the Alpine-only features gone, the stock compatibility check is clear.
        Assert.False(doc.EvaluateSaveTarget(SaveTarget.StockRf).Blocked);

        // The level still saves as Alpine v305 (GED never writes stock), byte-stably.
        byte[] saved = SaveAlpine(doc);
        var reloaded = RflFile.Load(saved);
        Assert.Equal(RflFile.AlpineSaveVersion, reloaded.Header.Version);
        Assert.Equal(saved, reloaded.Save()); // fixpoint
    }

    [Fact]
    public void V305_Save_Is_Byte_Stable()
    {
        EditorDocument doc = BuildAlpineLevel();
        byte[] alpine = SaveAlpine(doc);
        var reloaded = RflFile.Load(alpine);
        Assert.Equal(RflFile.AlpineSaveVersion, reloaded.Header.Version);
        Assert.Equal(alpine, reloaded.Save());
    }

    [Fact]
    public void Alpine_Event_Rotation_Is_Gated_At_The_Version_Boundaries()
    {
        // Clone_Entity persists rot at ≥ 300 (0x12C); Anchor_Marker_Orient only at ≥ 301 (0x12D).
        Assert.True(RflEvent.HasRotation("Clone_Entity", 0x12C));
        Assert.False(RflEvent.HasRotation("Clone_Entity", 0xC8));
        Assert.False(RflEvent.HasRotation("Anchor_Marker_Orient", 0x12C));
        Assert.True(RflEvent.HasRotation("Anchor_Marker_Orient", 0x12D));
    }

    private static byte[] SaveAlpine(EditorDocument doc)
    {
        doc.Rfl.UpgradeToAlpine(); // GED writes Alpine v305 always
        return doc.Rfl.Save();
    }
}
