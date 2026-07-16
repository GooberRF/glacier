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
/// DEFECT 1 diagnostic — runs the <see cref="CapFaceEarClip"/> oracle (Alpine's in-game cap triangulator,
/// reimplemented from spec) over every compiled output face of GED-built dmabrupt AND over RED's original
/// baked geometry, tallying faces the game's ear clip would STALL on ("[CapFace] Ear clip stuck"), plus
/// collinear-vertex and repeated-vertex counts. Geoable/breakable (dug) rooms are reported separately —
/// those are the surfaces geomod destruction actually operates on. RED's baseline is expected ~zero.
/// Pure diagnostic (prints, no asserts); the gate lives in <see cref="EarClipCapGateTests"/>.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class EarClipCapDiag
{
    private const string Level = "dmabruptdecayrc2a27.rfl";
    private readonly ITestOutputHelper _out;

    public EarClipCapDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Report_EarClip_Stalls_Ged_Vs_Red()
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

        HashSet<int> redGeoRooms = GeoableRoomIndices(red, alp, uidToRoomUid: null);
        HashSet<int> gedGeoRooms = GeoableRoomIndices(ged, alp, result.BrushRoomUid);

        _out.WriteLine($"=== RED original (baseline, expect ~zero) ===");
        Tally(red, redGeoRooms, "RED");
        _out.WriteLine($"=== GED build (default shipping path) ===");
        Tally(ged, gedGeoRooms, "GED");
    }

    private void Tally(Geometry g, HashSet<int> geoRooms, string label)
    {
        int worldStuck = 0, worldCollinear = 0, worldRepeated = 0, worldFaces = 0;
        int geoStuck = 0, geoCollinear = 0, geoRepeated = 0, geoFaces = 0, geoDegenerate = 0;
        var examples = new List<string>();

        foreach (Face f in g.Faces)
        {
            if (f.IsPortalFace || f.Vertices.Count < 3 || f.Texture < 0)
            {
                continue;
            }

            List<Vec3> loop = CapFaceEarClip.LoopOf(g, f);
            CapFaceEarClip.Probe probe = CapFaceEarClip.ProbeLoop(loop);
            bool geo = geoRooms.Contains(f.RoomIndex);

            if (geo)
            {
                geoFaces++;
                if (probe.Outcome == CapFaceEarClip.Outcome.Stuck)
                {
                    geoStuck++;
                }

                if (probe.Outcome == CapFaceEarClip.Outcome.Degenerate)
                {
                    geoDegenerate++;
                }

                if (probe.CollinearVertices > 0)
                {
                    geoCollinear++;
                }

                if (probe.RepeatedVertices > 0)
                {
                    geoRepeated++;
                }

                if (probe.Outcome == CapFaceEarClip.Outcome.Stuck && examples.Count < 12)
                {
                    examples.Add(
                        $"  {label} geo room#{f.RoomIndex} n={probe.Vertices} stuck@{probe.Remaining} " +
                        $"collinear={probe.CollinearVertices} repeated={probe.RepeatedVertices} tex#{f.Texture}");
                }
            }
            else
            {
                worldFaces++;
                if (probe.Outcome == CapFaceEarClip.Outcome.Stuck)
                {
                    worldStuck++;
                }

                if (probe.CollinearVertices > 0)
                {
                    worldCollinear++;
                }

                if (probe.RepeatedVertices > 0)
                {
                    worldRepeated++;
                }
            }
        }

        _out.WriteLine(
            $"  GEO/BREAKABLE rooms ({geoRooms.Count} rooms, {geoFaces} faces): " +
            $"stuck={geoStuck} degenerate={geoDegenerate} collinear-faces={geoCollinear} repeated-faces={geoRepeated}");
        _out.WriteLine(
            $"  WORLD rooms ({worldFaces} faces): " +
            $"stuck={worldStuck} collinear-faces={worldCollinear} repeated-faces={worldRepeated}");
        foreach (string e in examples)
        {
            _out.WriteLine(e);
        }
    }

    /// <summary>Room indices that host a geoable/breakable brush (the dug rooms). For GED uses the
    /// compile's brush→room-uid map; for RED uses the alpine table's stored room UIDs.</summary>
    internal static HashSet<int> GeoableRoomIndices(
        Geometry g, AlpineLevelPropertiesSection alp, IReadOnlyDictionary<int, int>? uidToRoomUid)
    {
        var roomUids = new HashSet<int>();
        IEnumerable<int> brushUids = alp.GeoableEntries.Select(e => e.BrushUid)
            .Concat(alp.BreakableEntries.Select(e => e.BrushUid));

        if (uidToRoomUid is null)
        {
            // RED baseline: the alpine table already stores each brush's compiled room UID.
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
            foreach (int uid in brushUids)
            {
                if (uidToRoomUid.TryGetValue(uid, out int roomUid))
                {
                    roomUids.Add(roomUid);
                }
            }
        }

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
