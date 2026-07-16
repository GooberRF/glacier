using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Flagship 25 — DEFECT 2 (breakable materials) + DEFECT 3 (geoable lost on load+resave).
/// Geoable/breakable state lives in alpine_level_properties as (brush_uid → room_uid) tables; the
/// game matches each room_uid against a compiled DETAIL room (destruction.cpp apply_geoable_flags /
/// apply_breakable_materials). GED used to keep the room_uids from the level's ORIGINAL compile, so
/// after a rebuild they pointed at rooms that no longer existed: geomod found nothing (geoable dead)
/// and every breakable defaulted to Glass instead of its authored material. RED recomputes these
/// tables on every save (editor_patch/level.cpp compute_geoable_room_uids / compute_breakable_room_uids);
/// this gate pins that GED now does too, and that a geoable brush (infinite life, no editor flag) is
/// isolated into its own detail room.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class AlpineGeoableBreakableTests
{
    private readonly ITestOutputHelper _out;

    public AlpineGeoableBreakableTests(ITestOutputHelper output) => _out = output;

    private const string Level = "dmabruptdecayrc2a27.rfl";

    [Fact]
    public void Geoable_And_Breakable_Room_Uids_Match_Compiled_Rooms()
    {
        if (!TryLoad(out RflFile rfl))
        {
            return;
        }

        CompiledLevel result = BuildAndApply(rfl);
        AlpineLevelPropertiesSection alp = Alpine(rfl);
        Geometry g = result.Geometry;

        var roomUids = new HashSet<int>(g.Rooms.Select(r => r.Id));
        var detailRoomUids = new HashSet<int>(g.Rooms.Where(r => r.IsSubroom != 0).Select(r => r.Id));

        Assert.NotEmpty(alp.GeoableEntries);
        Assert.NotEmpty(alp.BreakableEntries);

        // Every geoable/breakable entry maps to a distinct, existing, DETAIL room.
        AssertAllMapToDistinctDetailRooms("geoable", alp.GeoableEntries.Select(e => e.RoomUid), roomUids, detailRoomUids);
        AssertAllMapToDistinctDetailRooms("breakable", alp.BreakableEntries.Select(e => e.RoomUid), roomUids, detailRoomUids);

        // Breakable materials are carried through untouched (defect 2: material handling).
        Assert.All(alp.BreakableEntries, e => Assert.InRange(e.Material & 0x7F, 0, 6));
        _out.WriteLine($"geoable={alp.GeoableEntries.Count} breakable={alp.BreakableEntries.Count} " +
                       $"rooms={g.Rooms.Count} materials={string.Join(",", alp.BreakableEntries.Select(e => e.Material).Distinct())}");
    }

    [Fact]
    public void Room_Uids_Survive_Build_Save_Reload()
    {
        if (!TryLoad(out RflFile rfl))
        {
            return;
        }

        BuildAndApply(rfl);
        byte[] bytes = rfl.Save(updateTimestamp: false);

        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();
        AlpineLevelPropertiesSection alp = Alpine(reloaded);
        Geometry g = reloaded.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        var roomUids = new HashSet<int>(g.Rooms.Select(r => r.Id));

        Assert.All(alp.GeoableEntries, e => Assert.Contains(e.RoomUid, roomUids));
        Assert.All(alp.BreakableEntries, e => Assert.Contains(e.RoomUid, roomUids));
    }

    /// <summary>
    /// Flag preservation across load→save (no-op) and load→build→save: the persistent geoable /
    /// breakable brush-uid sets, breakable materials, and the on-disk brush flags (portal / air /
    /// detail) all round-trip. The build+save path additionally leaves the brush section
    /// byte-identical (geoable is never written to the RED brush record).
    /// </summary>
    [Fact]
    public void Brush_And_Alpine_Flags_Survive_Round_Trips()
    {
        if (!TryLoad(out RflFile rfl))
        {
            return;
        }

        BrushesSection brushesIn = Brushes(rfl);
        var flagsByUid = brushesIn.Brushes.ToDictionary(b => b.Uid, b => b.Flags & 0x1F); // portal|air|detail|scroll bits
        AlpineLevelPropertiesSection alpIn = Alpine(rfl);
        var geoIn = alpIn.GeoableEntries.Select(e => e.BrushUid).OrderBy(x => x).ToList();
        var brkIn = alpIn.BreakableEntries
            .Select(e => (e.BrushUid, e.Material)).OrderBy(x => x.BrushUid).ToList();
        byte[] brushBytesIn = Section(rfl, SectionType.Brushes)!.GetBodyBytes(rfl.Context);

        // load → build → save → reload
        BuildAndApply(rfl);
        Assert.True(Section(rfl, SectionType.Brushes)!.GetBodyBytes(rfl.Context).SequenceEqual(brushBytesIn),
            "brush section changed by build+apply (geoable must not touch the brush record)");

        RflFile reloaded = RflFile.Load(rfl.Save(updateTimestamp: false));
        reloaded.ParseAllKnownSections();

        var flagsOut = Brushes(reloaded).Brushes.ToDictionary(b => b.Uid, b => b.Flags & 0x1F);
        Assert.Equal(flagsByUid, flagsOut); // portal/air/detail flags preserved for every brush

        AlpineLevelPropertiesSection alpOut = Alpine(reloaded);
        Assert.Equal(geoIn, alpOut.GeoableEntries.Select(e => e.BrushUid).OrderBy(x => x).ToList());
        Assert.Equal(brkIn, alpOut.BreakableEntries.Select(e => (e.BrushUid, e.Material)).OrderBy(x => x.BrushUid).ToList());
    }

    private static void AssertAllMapToDistinctDetailRooms(
        string label, IEnumerable<int> roomUidList, HashSet<int> allRooms, HashSet<int> detailRooms)
    {
        var uids = roomUidList.ToList();
        Assert.All(uids, uid => Assert.Contains(uid, allRooms));
        Assert.All(uids, uid => Assert.Contains(uid, detailRooms));
        Assert.Equal(uids.Count, uids.Distinct().Count()); // each brush isolated into its own room
    }

    private static CompiledLevel BuildAndApply(RflFile rfl)
    {
        var options = new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = false };
        CompiledLevel result = GeometryBuildService.Build(rfl, options);
        GeometryBuildService.Apply(rfl, result);
        return result;
    }

    private static bool TryLoad(out RflFile rfl)
    {
        rfl = null!;
        if (!Corpus.Available)
        {
            return false;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return false;
        }

        rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        return true;
    }

    private static RflSection? Section(RflFile rfl, SectionType type) =>
        rfl.Sections.FirstOrDefault(s => s.TypeId == (uint)type);

    private static BrushesSection Brushes(RflFile rfl) =>
        rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().First();

    private static AlpineLevelPropertiesSection Alpine(RflFile rfl) =>
        rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();
}
