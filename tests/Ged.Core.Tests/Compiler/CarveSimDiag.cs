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
/// ITEM 1 diagnostic — runs the <see cref="CarveSimulation"/> harness (Alpine's runtime material-debris
/// shatter, reimplemented from spec) over the reachable breakable rooms of GED-built dmabrupt AND RED's
/// original geometry, tallying the cap loops the game's ear clip would STALL on
/// ("[CapFace] Ear clip stuck"). This is the loop Goober saw — a boundary loop assembled ACROSS faces
/// after bisection, which per-compiled-face probing (see <see cref="EarClipCapDiag"/>) cannot detect.
/// Pure diagnostic (prints, no asserts); the gate lives in <see cref="CarveSimGateTests"/>.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class CarveSimDiag
{
    private const string Level = "dmabruptdecayrc2a27.rfl";
    private readonly ITestOutputHelper _out;

    public CarveSimDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Report_CarveSim_Stalls_Ged_Vs_Red()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        AlpineLevelPropertiesSection alp =
            rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();

        CompiledLevel result = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = true });
        Geometry ged = result.Geometry;

        HashSet<int> redRooms = ReachableBreakableRooms(red, alp, uidToRoomUid: null);
        HashSet<int> gedRooms = ReachableBreakableRooms(ged, alp, result.BrushRoomUid);

        _out.WriteLine($"reachable breakable(non-glass,debris) rooms: RED={redRooms.Count} GED={gedRooms.Count}");
        Report(red, redRooms, "RED");
        Report(ged, gedRooms, "GED");

        // Also report the thorough superset: all geoable ∪ breakable rooms shattered.
        HashSet<int> redAll = AllGeoableBreakableRooms(red, alp, uidToRoomUid: null);
        HashSet<int> gedAll = AllGeoableBreakableRooms(ged, alp, result.BrushRoomUid);
        _out.WriteLine($"--- thorough superset (all geoable ∪ breakable rooms) ---");
        Report(red, redAll, "RED-all");
        Report(ged, gedAll, "GED-all");
    }

    /// <summary>Per-brush divergence: for every reachable breakable brush, shatter its RED room and its
    /// GED room (matched by stable authored brush UID) and dump loops where GED stalls but RED does not,
    /// with full vertex geometry so the degeneracy (collinear run / tiny edge / coincident) is visible.</summary>
    [Fact]
    public void Report_Per_Brush_Divergence()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        AlpineLevelPropertiesSection alp =
            rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();
        CompiledLevel result = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = true });
        Geometry ged = result.Geometry;

        var redByUid = new Dictionary<int, int>();
        for (int i = 0; i < red.Rooms.Count; i++)
        {
            redByUid[red.Rooms[i].Id] = i;
        }

        var gedByUid = new Dictionary<int, int>();
        for (int i = 0; i < ged.Rooms.Count; i++)
        {
            gedByUid[ged.Rooms[i].Id] = i;
        }

        int totalRed = 0, totalGed = 0, divergentBrushes = 0;
        foreach (AlpineBreakableEntry e in alp.BreakableEntries.OrderBy(x => x.BrushUid))
        {
            int mat = e.Material & 0x7F;
            bool noDebris = (e.Material & 0x80) != 0;
            if (mat == 0 || mat >= 6 || noDebris)
            {
                continue;
            }

            if (!redByUid.TryGetValue(e.RoomUid, out int redIdx))
            {
                continue;
            }

            if (!result.BrushRoomUid.TryGetValue(e.BrushUid, out int gedRoomUid) ||
                !gedByUid.TryGetValue(gedRoomUid, out int gedIdx))
            {
                continue;
            }

            var redRes = new CarveSimulation.Result();
            CarveSimulation.ShatterRoom(red, redIdx, redRes, exampleCap: 64);
            var gedRes = new CarveSimulation.Result();
            CarveSimulation.ShatterRoom(ged, gedIdx, gedRes, exampleCap: 64);

            totalRed += redRes.Stuck;
            totalGed += gedRes.Stuck;

            if (gedRes.Stuck > redRes.Stuck)
            {
                divergentBrushes++;
                _out.WriteLine(
                    $"BRUSH uid={e.BrushUid} mat={mat}: RED room#{redIdx} stuck={redRes.Stuck} loops={redRes.Loops} " +
                    $"| GED room#{gedIdx} stuck={gedRes.Stuck} loops={gedRes.Loops}  <-- GED WORSE by {gedRes.Stuck - redRes.Stuck}");
                foreach (CarveSimulation.StuckLoop sl in gedRes.Examples.Take(3))
                {
                    _out.WriteLine($"    GED stuck loop cut#{sl.Cut} n={sl.Vertices} remaining={sl.Remaining}:");
                    for (int k = 0; k < sl.Loop.Count; k++)
                    {
                        Vec3 v = sl.Loop[k];
                        _out.WriteLine($"      [{k}] ({v.X:F4}, {v.Y:F4}, {v.Z:F4})");
                    }
                }
            }
        }

        _out.WriteLine($"=== totals: RED stuck={totalRed} GED stuck={totalGed} divergentBrushes={divergentBrushes} ===");
    }

    /// <summary>Decisive experiment: does the T-joint / seam pass explain brush 10781's GED-only stalls?
    /// Builds GED with FixTJoints on and off and reports the divergent brush's stuck count each way.</summary>
    [Fact]
    public void Experiment_TJoints_Cause_Divergence()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        AlpineLevelPropertiesSection alp =
            rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();

        foreach (bool fixTj in new[] { true, false })
        {
            CompiledLevel result = GeometryBuildService.Build(
                rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = fixTj });
            Geometry ged = result.Geometry;
            HashSet<int> gedRooms = ReachableBreakableRooms(ged, alp, result.BrushRoomUid);

            var total = new CarveSimulation.Result();
            foreach (int r in gedRooms.OrderBy(x => x))
            {
                CarveSimulation.ShatterRoom(ged, r, total);
            }

            // brush 10781 specifically
            int stuck10781 = 0;
            if (result.BrushRoomUid.TryGetValue(10781, out int ru))
            {
                int idx = -1;
                for (int i = 0; i < ged.Rooms.Count; i++)
                {
                    if (ged.Rooms[i].Id == ru)
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0)
                {
                    var r10781 = new CarveSimulation.Result();
                    CarveSimulation.ShatterRoom(ged, idx, r10781);
                    stuck10781 = r10781.Stuck;
                }
            }

            _out.WriteLine($"FixTJoints={fixTj}: GED total stuck={total.Stuck} loops={total.Loops}; brush10781 stuck={stuck10781}");
        }
    }

    private void Report(Geometry g, HashSet<int> rooms, string label)
    {
        var result = new CarveSimulation.Result();
        foreach (int r in rooms.OrderBy(x => x))
        {
            CarveSimulation.ShatterRoom(g, r, result);
        }

        _out.WriteLine(
            $"  {label}: rooms={result.Rooms} capLoops={result.Loops} STUCK={result.Stuck} " +
            $"degenerate={result.Degenerate} maxRemaining={result.MaxRemaining}");
        foreach (CarveSimulation.StuckLoop e in result.Examples)
        {
            _out.WriteLine(
                $"    {label} stuck: room#{e.RoomIndex} uid={e.RoomUid} cut#{e.Cut} n={e.Vertices} remaining={e.Remaining}");
        }
    }

    /// <summary>Rooms hosting a breakable brush with a NON-glass material and debris enabled — the only
    /// rooms whose destruction runs do_material_debris_shatter and can emit "[CapFace]".</summary>
    internal static HashSet<int> ReachableBreakableRooms(
        Geometry g, AlpineLevelPropertiesSection alp, IReadOnlyDictionary<int, int>? uidToRoomUid)
    {
        var roomUids = new HashSet<int>();
        foreach (AlpineBreakableEntry e in alp.BreakableEntries)
        {
            int mat = e.Material & 0x7F;
            bool noDebris = (e.Material & 0x80) != 0;
            if (mat == 0 || mat >= 6 || noDebris)
            {
                continue; // glass / out-of-range / no-debris ⇒ no material shatter
            }

            int roomUid = uidToRoomUid is null
                ? e.RoomUid
                : uidToRoomUid.TryGetValue(e.BrushUid, out int ru) ? ru : int.MinValue;
            if (roomUid != int.MinValue)
            {
                roomUids.Add(roomUid);
            }
        }

        return ToRoomIndices(g, roomUids);
    }

    internal static HashSet<int> AllGeoableBreakableRooms(
        Geometry g, AlpineLevelPropertiesSection alp, IReadOnlyDictionary<int, int>? uidToRoomUid)
    {
        var roomUids = new HashSet<int>();
        if (uidToRoomUid is null)
        {
            foreach (AlpineGeoableEntry e in alp.GeoableEntries)
            {
                roomUids.Add(e.RoomUid);
            }

            foreach (AlpineBreakableEntry e in alp.BreakableEntries)
            {
                roomUids.Add(e.RoomUid);
            }
        }
        else
        {
            foreach (int uid in alp.GeoableEntries.Select(e => e.BrushUid)
                         .Concat(alp.BreakableEntries.Select(e => e.BrushUid)))
            {
                if (uidToRoomUid.TryGetValue(uid, out int ru))
                {
                    roomUids.Add(ru);
                }
            }
        }

        return ToRoomIndices(g, roomUids);
    }

    private static HashSet<int> ToRoomIndices(Geometry g, HashSet<int> roomUids)
    {
        var indices = new HashSet<int>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (roomUids.Contains(g.Rooms[i].Id))
            {
                indices.Add(i);
            }
        }

        return indices;
    }
}
