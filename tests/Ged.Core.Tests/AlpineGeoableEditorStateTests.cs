using System.Collections.Generic;
using System.IO;
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
/// Goober's report: "isn't brush 10992 geoable? it doesn't have that flag in GED but doesn't it
/// have that flag in the source rfl file?" (dmabruptdecayrc2a27.rfl).
///
/// <para>Geoable state is NOT an on-disk brush-flag bit — it lives in alpine_level_properties as a
/// <c>brush_uid → room_uid</c> table (Alpine editor_patch keeps it in <c>props.geoable_brush_uids</c>;
/// the "Is Geoable" checkbox is that membership). Flagship 25 fixed the compiler side; the residual
/// gap was that on LOAD nothing populated each brush's editor-visible geoable state from the table,
/// so a brush that is geoable in the file (10992) showed unchecked and un-badged. These gates pin the
/// load population, the byte-preservation round-trip, and the toggle → table reconciliation.</para>
/// </summary>
public sealed class AlpineGeoableEditorStateTests
{
    private const string Level = "dmabruptdecayrc2a27.rfl";

    /// <summary>The 23 geoable brush UIDs enumerated from dmabrupt's alpine_level_properties geoable table.</summary>
    private static readonly int[] GeoableUids =
    {
        63, 64, 154, 156, 172, 7999, 10753, 10754, 10765, 10768, 10789, 10852, 10884,
        10890, 10989, 10990, 10992, 11040, 11317, 11340, 11351, 11494, 11496,
    };

    /// <summary>The 30 breakable brush UIDs enumerated from dmabrupt's alpine_level_properties breakable table.</summary>
    private static readonly int[] BreakableUids =
    {
        10394, 10395, 10396, 10397, 10398, 10399, 10400, 10401, 10402, 10403, 10404, 10405,
        10778, 10779, 10780, 10781, 10782, 11035, 11036, 11037, 11038, 11039,
        11104, 11105, 11106, 11107, 11108, 11109, 11145, 11146,
    };

    [Fact]
    public void Load_Populates_Geoable_Editor_State_For_All_23_Including_10992()
    {
        if (!TryOpen(out EditorDocument doc))
        {
            return;
        }

        List<Brush> brushes = Brushes(doc);
        Dictionary<int, Brush> byUid = brushes.ToDictionary(b => b.Uid);

        // Goober's question, answered in a test: brush 10992 IS geoable in the file and now carries
        // the editor-visible geoable state on load.
        Assert.True(byUid.ContainsKey(10992), "brush 10992 present");
        Assert.True(IsGeoable(byUid[10992]), "brush 10992 must be geoable on load (Goober's report)");

        InspectorField isGeoableField = BrushInspectorCatalog.Fields.First(f => f.Label == "Is Geoable");
        Dictionary<int, LayerRow> rows = LayersModel.BuildRows(brushes).ToDictionary(r => r.Uid);

        foreach (int uid in GeoableUids)
        {
            Assert.True(byUid.ContainsKey(uid), $"geoable brush {uid} present");
            Assert.True(IsGeoable(byUid[uid]), $"geoable flag not set on load for {uid}");
            Assert.True(isGeoableField.Get(byUid[uid]) is true, $"Properties 'Is Geoable' unchecked for {uid}");
            Assert.True(rows[uid].Geoable, $"Layers 'G' badge missing for {uid}");
        }

        // Exactly the 23 carry the flag — nothing spuriously geoable.
        List<int> flagged = brushes.Where(IsGeoable).Select(b => b.Uid).OrderBy(x => x).ToList();
        Assert.Equal(GeoableUids.OrderBy(x => x).ToList(), flagged);
    }

    [Fact]
    public void Load_Populates_Breakable_Editor_State_For_All_30()
    {
        if (!TryOpen(out EditorDocument doc))
        {
            return;
        }

        List<Brush> brushes = Brushes(doc);
        Dictionary<int, Brush> byUid = brushes.ToDictionary(b => b.Uid);
        Dictionary<int, LayerRow> rows = LayersModel.BuildRows(brushes).ToDictionary(r => r.Uid);

        foreach (int uid in BreakableUids)
        {
            Assert.True(byUid.ContainsKey(uid), $"breakable brush {uid} present");
            Assert.True(rows[uid].Breakable, $"Layers 'B' badge missing for {uid}");
            // The breakable material is read straight from alpine_level_properties (undo-safe).
            Assert.InRange(BrushBreakableProps.GetMaterial(doc, uid), 0, 6);
        }
    }

    [Fact]
    public void Load_Save_NoEdits_Is_Byte_Identical()
    {
        if (!Corpus.Available || !File.Exists(LevelPath))
        {
            return;
        }

        byte[] original = File.ReadAllBytes(LevelPath);
        EditorDocument doc = EditorDocument.Open(LevelPath);

        Assert.False(doc.IsDirty); // populating the geoable flags is a pure in-memory mirror

        byte[] saved = doc.SaveToBytes(updateTimestamp: false);
        Assert.Equal(original, saved); // no section is dirtied → verbatim round-trip
    }

    [Fact]
    public void Toggle_Off_One_Brush_Removes_Exactly_That_Entry()
    {
        if (!TryOpen(out EditorDocument doc))
        {
            return;
        }

        Dictionary<int, int> roomByUid = Alpine(doc).GeoableEntries.ToDictionary(e => e.BrushUid, e => e.RoomUid);

        // Un-mark brush 10992 exactly as the Properties "Is Geoable" checkbox does — clear the flag.
        Brush b = Brushes(doc).First(x => x.Uid == 10992);
        b.Flags &= ~(uint)BrushFlags.Geoable;

        AlpineLevelPropertiesSection after = ReloadAlpine(doc);
        List<int> afterUids = after.GeoableEntries.Select(e => e.BrushUid).ToList();

        Assert.DoesNotContain(10992, afterUids);
        Assert.Equal(GeoableUids.Where(u => u != 10992).OrderBy(x => x).ToList(), afterUids.OrderBy(x => x).ToList());

        // Every surviving entry keeps its room UID — no churn of the untouched 22.
        Assert.All(after.GeoableEntries, e => Assert.Equal(roomByUid[e.BrushUid], e.RoomUid));
    }

    [Fact]
    public void Toggle_On_A_Brush_Adds_Exactly_That_Entry()
    {
        if (!TryOpen(out EditorDocument doc))
        {
            return;
        }

        Brush target = Brushes(doc).First(b => !IsGeoable(b));
        target.Flags |= (uint)BrushFlags.Geoable;

        AlpineLevelPropertiesSection after = ReloadAlpine(doc);
        List<int> afterUids = after.GeoableEntries.Select(e => e.BrushUid).ToList();

        Assert.Contains(target.Uid, afterUids);
        Assert.Equal(GeoableUids.Length + 1, afterUids.Count);
        // A newly-marked entry defers its room UID to the next build.
        Assert.Equal(0, after.GeoableEntries.First(e => e.BrushUid == target.Uid).RoomUid);
    }

    [Fact]
    public void Geoable_Bit_Is_Never_Written_To_The_Brush_Record()
    {
        if (!TryOpen(out EditorDocument doc))
        {
            return;
        }

        Brush b = Brushes(doc).First();
        b.Flags |= (uint)BrushFlags.Geoable;

        // Force the brush section to re-serialize (a genuine edit would).
        doc.Rfl.Sections.First(s => s.TypeId == (uint)SectionType.Brushes).Dirty = true;

        RflFile reloaded = RflFile.Load(doc.SaveToBytes(updateTimestamp: false));
        reloaded.ParseAllKnownSections();
        Brush reBrush = reloaded.Sections.Select(s => s.Content).OfType<BrushesSection>()
            .First().Brushes.First(x => x.Uid == b.Uid);

        Assert.False(IsGeoable(reBrush), "geoable is never stored in the RED/Alpine brush record");
    }

    private static bool IsGeoable(Brush b) => ((BrushFlags)b.Flags & BrushFlags.Geoable) != 0;

    private static string LevelPath => Path.Combine(Corpus.Directory!, Level);

    private static bool TryOpen(out EditorDocument doc)
    {
        doc = null!;
        if (!Corpus.Available || !File.Exists(LevelPath))
        {
            return false;
        }

        doc = EditorDocument.Open(LevelPath);
        return true;
    }

    private static List<Brush> Brushes(EditorDocument doc) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().First().Brushes;

    private static AlpineLevelPropertiesSection Alpine(EditorDocument doc) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();

    private static AlpineLevelPropertiesSection ReloadAlpine(EditorDocument doc)
    {
        RflFile reloaded = RflFile.Load(doc.SaveToBytes(updateTimestamp: false));
        reloaded.ParseAllKnownSections();
        return reloaded.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();
    }
}
