using System;
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
/// Flagship 27 GATE — per-face geoable/breakable carve coverage on dmabruptdecayrc2a27.
/// Goober's in-game re-test of the rebuild: both geoable brushes fire the geomod blast but only
/// carve partially — 10992 only the top part and only the rifle-facing side. The game carves per
/// face by ROOM MEMBERSHIP (game_patch destruction.cpp: a face participates iff
/// face-&gt;which_room == the brush's target geoable detail room), and RED consolidates every room
/// an isolated brush's faces flood into (editor_patch/level.cpp merge_geoable_interior_rooms) so
/// the whole brush shares one geoable room. GED used to leave a concave (or coincident-welded)
/// brush's faces scattered across several detail rooms while the alpine table named only one, so
/// the other sides never geomodded. This gate pins that EVERY exposed surface of EVERY
/// geoable/breakable brush is covered by an output face belonging to that brush's isolated room —
/// at parity with RED's own (in-game-correct) original, and with 10765 &amp; 10992 checked in full.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class GeoableFaceCoverageDiag
{
    private readonly ITestOutputHelper _out;

    public GeoableFaceCoverageDiag(ITestOutputHelper output) => _out = output;

    private const string Level = "dmabruptdecayrc2a27.rfl";

    // The two brushes Goober re-tested: only partially geomodding before the fix.
    private static readonly int[] Witnessed = { 10765, 10992 };

    /// <summary>
    /// Every geoable/breakable brush's source surfaces must be covered by faces in that brush's
    /// isolated room, and no worse than RED's original (which carves correctly in-game). Coverage
    /// in GED's isolated room must be a superset of coverage in RED's isolated room, per source face.
    /// </summary>
    [Fact]
    public void All_Isolated_Brush_Surfaces_Covered_At_Red_Parity()
    {
        if (!TryLoad(out RflFile rfl, out Geometry red, out List<Brush> brushes, out AlpineLevelPropertiesSection alp))
        {
            return;
        }

        var result = GeometryBuildService.Build(rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = true });
        Geometry ged = result.Geometry;
        var redByUid = RoomIndexByUid(red);
        var gedByUid = RoomIndexByUid(ged);

        var redUidOf = alp.GeoableEntries.ToDictionary(e => e.BrushUid, e => e.RoomUid);
        foreach (AlpineBreakableEntry e in alp.BreakableEntries)
        {
            redUidOf[e.BrushUid] = e.RoomUid;
        }

        var isolatedUids = alp.GeoableEntries.Select(e => e.BrushUid)
            .Concat(alp.BreakableEntries.Select(e => e.BrushUid)).Distinct().ToList();
        Assert.True(isolatedUids.Count >= 40, $"expected the full geoable+breakable tables, got {isolatedUids.Count}");

        var regressions = new List<string>();
        int checkedBrushes = 0;
        foreach (int uid in isolatedUids)
        {
            Brush? b = brushes.FirstOrDefault(x => x.Uid == uid);
            if (b is null)
            {
                continue;
            }

            int redRoom = redByUid.GetValueOrDefault(redUidOf.GetValueOrDefault(uid, int.MinValue), -1);
            int gedRoom = gedByUid.GetValueOrDefault(result.BrushRoomUid.GetValueOrDefault(uid, int.MinValue), -1);
            if (gedRoom < 0)
            {
                regressions.Add($"uid {uid}: no GED isolated room");
                continue;
            }

            checkedBrushes++;
            List<CsgFace> world = BrushWorld.ToWorldFaces(b, 0, out _);
            for (int fi = 0; fi < world.Count; fi++)
            {
                CsgFace sf = world[fi];
                if (sf.Area() < 1e-3f)
                {
                    continue;
                }

                bool inGed = CoveredInRoom(ged, gedRoom, sf);
                bool inRed = redRoom >= 0 && CoveredInRoom(red, redRoom, sf);
                if (inRed && !inGed)
                {
                    regressions.Add($"uid {uid} face#{fi} tex='{Short(sf.Texture)}' area={sf.Area():F2}: in RED iso room, MISSING from GED iso room");
                }
            }
        }

        _out.WriteLine($"checked {checkedBrushes}/{isolatedUids.Count} isolated brushes; regressions={regressions.Count}");
        Assert.True(checkedBrushes >= (int)(isolatedUids.Count * 0.9),
            $"only {checkedBrushes}/{isolatedUids.Count} isolated brushes reached a GED room");
        Assert.True(regressions.Count == 0,
            "geoable/breakable surfaces lost from their isolated room vs RED (would not geomod):\n  " +
            string.Join("\n  ", regressions.Take(40)));
    }

    /// <summary>
    /// Full-coverage check on the two brushes Goober re-tested (10765, 10992): EVERY source surface
    /// must be covered by a face in the brush's isolated room, matching RED's own isolated-room face
    /// set. This is the direct pin on the reported "only the top / only the rifle-facing side" bug.
    /// </summary>
    [Fact]
    public void Witnessed_Brushes_Fully_Covered_By_Isolated_Room()
    {
        if (!TryLoad(out RflFile rfl, out Geometry red, out List<Brush> brushes, out AlpineLevelPropertiesSection alp))
        {
            return;
        }

        var result = GeometryBuildService.Build(rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = true });
        Geometry ged = result.Geometry;
        var redByUid = RoomIndexByUid(red);
        var gedByUid = RoomIndexByUid(ged);
        var redUidOf = alp.GeoableEntries.ToDictionary(e => e.BrushUid, e => e.RoomUid);

        var failures = new List<string>();
        foreach (int uid in Witnessed)
        {
            Brush b = brushes.First(x => x.Uid == uid);
            int redRoom = redByUid.GetValueOrDefault(redUidOf.GetValueOrDefault(uid, int.MinValue), -1);
            int gedRoom = gedByUid.GetValueOrDefault(result.BrushRoomUid.GetValueOrDefault(uid, int.MinValue), -1);
            Assert.True(gedRoom >= 0, $"uid {uid}: no GED isolated room");

            int redFaces = red.Faces.Count(f => f.RoomIndex == redRoom);
            int gedFaces = ged.Faces.Count(f => f.RoomIndex == gedRoom);
            _out.WriteLine($"uid {uid}: RED iso faces={redFaces} GED iso faces={gedFaces}");

            List<CsgFace> world = BrushWorld.ToWorldFaces(b, 0, out _);
            for (int fi = 0; fi < world.Count; fi++)
            {
                CsgFace sf = world[fi];
                if (sf.Area() < 1e-3f)
                {
                    continue;
                }

                if (!CoveredInRoom(ged, gedRoom, sf))
                {
                    failures.Add($"uid {uid} face#{fi} tex='{Short(sf.Texture)}' n=({sf.Plane.Normal.X:F2},{sf.Plane.Normal.Y:F2},{sf.Plane.Normal.Z:F2}) area={sf.Area():F2}: not in GED isolated room#{gedRoom}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "witnessed geoable brush surfaces missing from their isolated room (partial geomod):\n  " +
            string.Join("\n  ", failures));
    }

    /// <summary>True iff some non-portal face in <paramref name="room"/> is coplanar with and overlaps the source face.</summary>
    private static bool CoveredInRoom(Geometry g, int room, CsgFace sf)
    {
        CsgPlane pl = sf.Plane;
        (Vec3 mn, Vec3 mx) = Aabb(sf.Vertices.Select(v => v.Position));
        for (int i = 0; i < g.Faces.Count; i++)
        {
            Face of = g.Faces[i];
            if (of.RoomIndex != room || of.Vertices.Count < 3 || of.IsPortalFace)
            {
                continue;
            }

            List<Vec3> ov = of.Vertices.Select(fv => g.Vertices[fv.Index]).ToList();
            if (MathF.Abs(Newell(ov).Dot(pl.Normal)) < 0.99f)
            {
                continue;
            }

            if (MathF.Abs(pl.Distance(Avg(ov))) > 0.08f)
            {
                continue;
            }

            (Vec3 omn, Vec3 omx) = Aabb(ov);
            if (AabbOverlap(mn, mx, omn, omx, 0.05f))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<int, int> RoomIndexByUid(Geometry g)
    {
        var d = new Dictionary<int, int>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            d[g.Rooms[i].Id] = i;
        }

        return d;
    }

    private static bool TryLoad(out RflFile rfl, out Geometry red, out List<Brush> brushes, out AlpineLevelPropertiesSection alp)
    {
        rfl = null!;
        red = null!;
        brushes = null!;
        alp = null!;
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
        red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().Select(g => g.Geometry).FirstOrDefault()!;
        brushes = rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().Select(b => b.Brushes).FirstOrDefault()!;
        alp = rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().FirstOrDefault()!;
        return red is not null && brushes is not null && alp is not null;
    }

    private static (Vec3, Vec3) Aabb(IEnumerable<Vec3> pts)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 p in pts)
        {
            mn = Vec3Math.Min(mn, p);
            mx = Vec3Math.Max(mx, p);
        }

        return (mn, mx);
    }

    private static bool AabbOverlap(Vec3 amn, Vec3 amx, Vec3 bmn, Vec3 bmx, float m) =>
        amn.X - m <= bmx.X && amx.X + m >= bmn.X &&
        amn.Y - m <= bmx.Y && amx.Y + m >= bmn.Y &&
        amn.Z - m <= bmx.Z && amx.Z + m >= bmn.Z;

    private static Vec3 Newell(List<Vec3> v)
    {
        var n = new Vec3(0, 0, 0);
        for (int i = 0; i < v.Count; i++)
        {
            Vec3 a = v[i], b = v[(i + 1) % v.Count];
            n = n.Add(new Vec3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)));
        }

        return n.Normalized();
    }

    private static Vec3 Avg(List<Vec3> v)
    {
        var c = new Vec3(0, 0, 0);
        foreach (Vec3 p in v)
        {
            c = c.Add(p);
        }

        return c.Scale(1f / v.Count);
    }

    private static string Short(string s) => string.IsNullOrEmpty(s) ? "(none)" : Path.GetFileName(s);
}
