using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Flagship 21 — THE GAME-LOADER AUDIT. Reads a level's static_geometry section with a
/// deliberately INDEPENDENT raw byte decoder (NOT Ged.Core's parser classes), matching the
/// exact field layout RF.exe FUN_004ed520 consumes (rooms/portals/faces/surfaces/subrooms).
/// <para>
/// Purpose: every prior room/portal validation read GED's rebuilt sections back with GED's OWN
/// parser, so a writer bug the reader mirrors stays green. This dumper:
///   (1) proves reader/writer independence — raw-decode of a RED file must equal Ged.Core's parse;
///   (2) compares RED-original vs GED-rebuild field VALUES the structural diff never checked
///       (per-face room_index self-consistency, portal geometry, portal_index_plus_2, flags,
///       room AABB source);
///   (3) checks the invariants RF.exe's renderer relies on directly on the GED rebuild.
/// Artifacts land in tests/artifacts/gamefield_&lt;level&gt;.txt.
/// </para>
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class GameFieldDumpTests
{
    private readonly ITestOutputHelper _out;

    public GameFieldDumpTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Audit_Game_Fields(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"GAME-FIELD AUDIT — {file}");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        // ---- (1) RED original: raw decode + independence cross-check against Ged.Core parse ----
        byte[] redBytes = File.ReadAllBytes(path);
        RawGeo redRaw = RawGeo.Decode(redBytes);
        sb.AppendLine($"RED file version 0x{redRaw.Version:X}");
        sb.AppendLine($"RED raw: rooms={redRaw.Rooms.Count} subroomLists={redRaw.SubroomLists.Count} portals={redRaw.Portals.Count} verts={redRaw.Vertices.Count} faces={redRaw.Faces.Count} surfaces={redRaw.Surfaces.Count} textures={redRaw.Textures.Count}");

        Geometry redParsed = ParseWithGed(redBytes);
        string indep = IndependenceCheck(redRaw, redParsed);
        sb.AppendLine();
        sb.AppendLine("== READER INDEPENDENCE (raw decode vs Ged.Core parse of the SAME RED bytes) ==");
        sb.AppendLine(indep);
        sb.AppendLine();

        // ---- (2) GED rebuild: compile with shipping defaults, save real path, raw-decode result ----
        RawGeo? gedRaw = null;
        try
        {
            RflFile rfl = RflFile.Load(redBytes);
            rfl.ParseAllKnownSections();
            var options = new CompileOptions
            {
                Alpine = rfl.Context.IsAlpine,
                BuildSurfaces = true,
                FixTJoints = true,
            };
            CompiledLevel result = GeometryBuildService.Build(rfl, options);
            GeometryBuildService.Apply(rfl, result);
            byte[] gedBytes = rfl.Save(updateTimestamp: true);
            gedRaw = RawGeo.Decode(gedBytes);

            string scratch = Environment.GetEnvironmentVariable("GED_SCRATCH")
                             ?? Path.Combine(Path.GetTempPath(), "ged_audit");
            Directory.CreateDirectory(scratch);
            File.WriteAllBytes(Path.Combine(scratch, "ged_" + file), gedBytes);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"GED REBUILD FAILED: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
        }

        if (gedRaw is not null)
        {
            sb.AppendLine($"GED file version 0x{gedRaw.Version:X}");
            sb.AppendLine($"GED raw: rooms={gedRaw.Rooms.Count} subroomLists={gedRaw.SubroomLists.Count} portals={gedRaw.Portals.Count} verts={gedRaw.Vertices.Count} faces={gedRaw.Faces.Count} surfaces={gedRaw.Surfaces.Count} textures={gedRaw.Textures.Count}");
            sb.AppendLine();

            sb.AppendLine("== SELF-CONSISTENCY INVARIANTS (RF.exe renderer relies on these) ==");
            sb.AppendLine("-- RED original --");
            sb.Append(Invariants(redRaw));
            sb.AppendLine("-- GED rebuild --");
            sb.Append(Invariants(gedRaw));
            sb.AppendLine();

            sb.AppendLine("== FACE room_index self-consistency (does the WRITTEN room_index match the room whose AABB smallest-contains the face?) ==");
            sb.AppendLine("-- RED original --");
            sb.Append(RoomIndexConsistency(redRaw));
            sb.AppendLine("-- GED rebuild --");
            sb.Append(RoomIndexConsistency(gedRaw));
            sb.AppendLine();

            sb.AppendLine("== PORTAL geometry (point1/point2 extents) ==");
            sb.AppendLine($"RED portals: {redRaw.Portals.Count}; degenerate(pt1==pt2 or zero-size)={CountDegeneratePortals(redRaw)}");
            sb.AppendLine($"GED portals: {gedRaw.Portals.Count}; degenerate(pt1==pt2 or zero-size)={CountDegeneratePortals(gedRaw)}");
            sb.Append(PortalGeometrySummary("RED", redRaw));
            sb.Append(PortalGeometrySummary("GED", gedRaw));
            sb.AppendLine();

            sb.AppendLine("== portal_index_plus_2 usage (faces marked as portal faces) ==");
            sb.Append(PortalMarkerSummary("RED", redRaw));
            sb.Append(PortalMarkerSummary("GED", gedRaw));
            sb.AppendLine();

            sb.AppendLine("== FACE FLAGS histogram ==");
            sb.Append(FlagsHistogram("RED", redRaw));
            sb.Append(FlagsHistogram("GED", gedRaw));
            sb.AppendLine();

            // The portal point1/point2 AABB IS the render culling window: RF.exe (FUN_004d4860) projects it to
            // screen (FUN_00507ba0) and clips the traversal frustum to that rect. A box that undershoots the real
            // opening over-culls the room beyond ("things disappearing"). Match each RED portal to the nearest GED
            // portal by box centre and compare the TWO-LARGEST-axis extents (the projected opening width/height —
            // the thin normal axis is irrelevant to the screen silhouette).
            sb.AppendLine("== PORTAL culling-window (box) RED vs GED — matched by box centre ==");
            sb.Append(PortalBoxCompare(redRaw, gedRaw));
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact($"gamefield_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());

        // ---- WRITER-LEVEL GATES (flagship 21) — assert on the RAW decoded bytes, NOT parsed-by-GED --------
        // (1) Reader/writer independence: the from-scratch raw decoder must agree with Ged.Core's parser
        //     field-for-field on RED's own bytes. This is what proves a writer bug the reader mirrors would
        //     NOT stay hidden. Both must succeed for the audit to mean anything.
        Assert.Equal(redRaw.Rooms.Count, redParsed.Rooms.Count);
        Assert.Equal(redRaw.Portals.Count, redParsed.Portals.Count);
        Assert.Equal(redRaw.Faces.Count, redParsed.Faces.Count);
        Assert.True(FaceFieldMismatches(redRaw, redParsed) == 0,
            $"{file}: raw decoder disagrees with Ged.Core parser on RED bytes (reader not independent)");

        // (2) GED rebuild must satisfy the invariants RF.exe's loader/renderer relies on — checked on the
        //     RAW bytes GED wrote, so a writer that misplaced/mis-scaled a game-consumed field is caught here
        //     even though every parsed-by-GED structural test stays green.
        Assert.NotNull(gedRaw);
        AssertWriterInvariants(file, gedRaw!);

        // (3) Flagship 22 face-count / per-room BATCH shape. Over-tessellation (a runaway per-room face count)
        //     is the mechanical suspect for RF dropping over-budget render batches ("things disappearing").
        //     After the output-stage coplanar merge GED's total face count and worst-room face count sit AT OR
        //     BELOW RED's envelope. Pin that so a merge regression (or a fold that re-fragments) is caught here.
        AssertFaceCountShape(file, redRaw, gedRaw!);
    }

    /// <summary>Pins GED's compiled face-count shape to RED's: total faces and the worst room's face count
    /// (the per-room render batch pressure RF is sensitive to) stay within a small headroom of RED's.</summary>
    private static void AssertFaceCountShape(string file, RawGeo red, RawGeo ged)
    {
        // Per-level total-face and per-room-maxFace headroom over RED. dm04 carries its extra as invisible
        // portal-membrane fragments (a known, hole-exempt follow-on), so it gets a wider total bound; its
        // SOLID face count is at RED parity.
        (double total, double perRoom) cap = file switch
        {
            "dm04.rfl" => (1.35, 1.20),
            _ => (1.15, 1.15),
        };

        Assert.True(ged.Faces.Count <= red.Faces.Count * cap.total,
            $"{file}: GED {ged.Faces.Count} faces exceeds RED {red.Faces.Count} × {cap.total} — over-tessellation regressed");

        int redMax = MaxRoomFaces(red), gedMax = MaxRoomFaces(ged);
        Assert.True(gedMax <= (redMax * cap.perRoom) + 5,
            $"{file}: GED worst-room {gedMax} faces exceeds RED {redMax} × {cap.perRoom} — per-room batch pressure regressed");
    }

    private static int MaxRoomFaces(RawGeo g)
    {
        if (g.Rooms.Count == 0)
        {
            return g.Faces.Count;
        }

        var perRoom = new int[g.Rooms.Count];
        foreach (RawFace f in g.Faces)
        {
            if (f.RoomIndex >= 0 && f.RoomIndex < perRoom.Length)
            {
                perRoom[f.RoomIndex]++;
            }
        }

        int max = 0;
        foreach (int c in perRoom)
        {
            if (c > max)
            {
                max = c;
            }
        }

        return max;
    }

    private static int FaceFieldMismatches(RawGeo raw, Geometry parsed)
    {
        int mism = 0;
        int n = Math.Min(raw.Faces.Count, parsed.Faces.Count);
        for (int i = 0; i < n; i++)
        {
            RawFace rf = raw.Faces[i];
            Face gf = parsed.Faces[i];
            if (rf.Texture != gf.Texture || rf.SurfaceIndex != gf.SurfaceIndex || rf.FaceId != gf.FaceId ||
                rf.PortalIndexPlus2 != gf.PortalIndexPlus2 || rf.Flags != gf.Flags ||
                rf.SmoothingGroups != gf.SmoothingGroups || rf.RoomIndex != gf.RoomIndex ||
                rf.Verts.Count != gf.Vertices.Count)
            {
                mism++;
            }
        }

        return mism;
    }

    /// <summary>
    /// Hard gate over the RAW bytes GED wrote. Every field here is a value RF.exe's static-geometry loader
    /// (FUN_004ed520) or portal renderer (g_solid_portal_renderer / gr_d3d_render_face_list) reads directly:
    /// an out-of-range room_index, a portal linking a non-existent or identical room, a portal_index_plus_2
    /// pointing past the portal table, or a face vertex index past the pool would make the game read garbage
    /// for exactly the fields that drive visibility/derendering. All currently hold; this locks them in.
    /// </summary>
    private static void AssertWriterInvariants(string file, RawGeo g)
    {
        int roomOOB = 0, vertOOB = 0, portalRoomOOB = 0, portalSame = 0, subParentOOB = 0, subChildOOB = 0;
        int surfRoomOOB = 0, pip2OOB = 0, notContained = 0, probed = 0;
        var roomAabb = g.Rooms.Select(r => (r.AabbMin, r.AabbMax, Vol(r.AabbMin, r.AabbMax))).ToArray();

        foreach (RawFace f in g.Faces)
        {
            if (f.RoomIndex >= g.Rooms.Count)
            {
                roomOOB++;
            }

            foreach (RawVert v in f.Verts)
            {
                if (v.Index < 0 || v.Index >= g.Vertices.Count)
                {
                    vertOOB++;
                }
            }

            if (f.PortalIndexPlus2 >= 2 && (f.PortalIndexPlus2 - 2) >= g.Portals.Count)
            {
                pip2OOB++;
            }

            // Non-portal textured faces must be tagged to a room whose AABB actually encloses them (else the
            // renderer draws them under a room whose PVS cell is elsewhere = "things disappearing").
            if (f.Texture >= 0 && f.Verts.Count >= 3 && f.PortalIndexPlus2 < 2 &&
                f.RoomIndex >= 0 && f.RoomIndex < g.Rooms.Count)
            {
                probed++;
                if (!Contains(roomAabb[f.RoomIndex].AabbMin, roomAabb[f.RoomIndex].AabbMax, Centroid(g, f)))
                {
                    notContained++;
                }
            }
        }

        foreach (RawPortal p in g.Portals)
        {
            if (p.Room1 < 0 || p.Room1 >= g.Rooms.Count || p.Room2 < 0 || p.Room2 >= g.Rooms.Count)
            {
                portalRoomOOB++;
            }
            else if (p.Room1 == p.Room2)
            {
                portalSame++;
            }
        }

        foreach (RawSubroomList sl in g.SubroomLists)
        {
            if (sl.RoomIndex < 0 || sl.RoomIndex >= g.Rooms.Count)
            {
                subParentOOB++;
            }

            foreach (int si in sl.SubIndices)
            {
                if (si < 0 || si >= g.Rooms.Count)
                {
                    subChildOOB++;
                }
            }
        }

        foreach (RawSurface s in g.Surfaces)
        {
            if (s.RoomIndex >= g.Rooms.Count)
            {
                surfRoomOOB++;
            }
        }

        Assert.True(roomOOB == 0, $"{file}: {roomOOB} faces have room_index past the room table");
        Assert.True(vertOOB == 0, $"{file}: {vertOOB} face vertex indices past the vertex pool");
        Assert.True(portalRoomOOB == 0, $"{file}: {portalRoomOOB} portals reference a non-existent room");
        Assert.True(portalSame == 0, $"{file}: {portalSame} portals link a room to itself");
        Assert.True(pip2OOB == 0, $"{file}: {pip2OOB} faces have portal_index_plus_2 past the portal table");
        Assert.True(subParentOOB == 0 && subChildOOB == 0,
            $"{file}: subroom-list index out of range (parent={subParentOOB}, child={subChildOOB})");
        Assert.True(surfRoomOOB == 0, $"{file}: {surfRoomOOB} surfaces reference a non-existent room");

        // Every probed world face must be enclosed by its stored room (100% — measured on both RED and GED).
        Assert.True(notContained == 0,
            $"{file}: {notContained}/{probed} world faces tagged to a room whose AABB does not enclose them");
    }

    // ---------------------------------------------------------------- independence + invariants

    private static Geometry ParseWithGed(byte[] bytes)
    {
        RflFile rfl = RflFile.Load(bytes);
        rfl.ParseAllKnownSections();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                return gs.Geometry;
            }
        }

        return new Geometry();
    }

    private static string IndependenceCheck(RawGeo raw, Geometry parsed)
    {
        var sb = new StringBuilder();
        void Line(string name, object a, object b) =>
            sb.AppendLine($"  {name,-22} raw={a} ged={b} {(a.ToString() == b.ToString() ? "OK" : "*** MISMATCH ***")}");

        Line("rooms", raw.Rooms.Count, parsed.Rooms.Count);
        Line("subroomLists", raw.SubroomLists.Count, parsed.SubroomLists.Count);
        Line("portals", raw.Portals.Count, parsed.Portals.Count);
        Line("vertices", raw.Vertices.Count, parsed.Vertices.Count);
        Line("faces", raw.Faces.Count, parsed.Faces.Count);
        Line("surfaces", raw.Surfaces.Count, parsed.Surfaces.Count);
        Line("textures", raw.Textures.Count, parsed.Textures.Count);

        // Spot-check a scattering of faces field-by-field (the fields RF.exe reads).
        int mism = 0;
        int n = Math.Min(raw.Faces.Count, parsed.Faces.Count);
        for (int i = 0; i < n; i++)
        {
            RawFace rf = raw.Faces[i];
            Face gf = parsed.Faces[i];
            if (rf.Texture != gf.Texture || rf.SurfaceIndex != gf.SurfaceIndex || rf.FaceId != gf.FaceId ||
                rf.PortalIndexPlus2 != gf.PortalIndexPlus2 || rf.Flags != gf.Flags ||
                rf.SmoothingGroups != gf.SmoothingGroups || rf.RoomIndex != gf.RoomIndex ||
                rf.Verts.Count != gf.Vertices.Count)
            {
                if (mism < 5)
                {
                    sb.AppendLine($"    face#{i} FIELD MISMATCH raw(tex={rf.Texture},surf={rf.SurfaceIndex},fid={rf.FaceId},pip2={rf.PortalIndexPlus2},flags={rf.Flags},sg={rf.SmoothingGroups},room={rf.RoomIndex},nv={rf.Verts.Count}) ged(tex={gf.Texture},surf={gf.SurfaceIndex},fid={gf.FaceId},pip2={gf.PortalIndexPlus2},flags={gf.Flags},sg={gf.SmoothingGroups},room={gf.RoomIndex},nv={gf.Vertices.Count})");
                }

                mism++;
            }
        }

        // Rooms field-by-field.
        int rmism = 0;
        int rn = Math.Min(raw.Rooms.Count, parsed.Rooms.Count);
        for (int i = 0; i < rn; i++)
        {
            RawRoom rr = raw.Rooms[i];
            Room gr = parsed.Rooms[i];
            bool ok = rr.Id == gr.Id && rr.IsLiquidRoom == gr.IsLiquidRoom && rr.IsSubroom == gr.IsSubroom &&
                      rr.IsSkyroom == gr.IsSkyroom && Approx(rr.AabbMin, (gr.Aabb.P1.X, gr.Aabb.P1.Y, gr.Aabb.P1.Z)) &&
                      Approx(rr.AabbMax, (gr.Aabb.P2.X, gr.Aabb.P2.Y, gr.Aabb.P2.Z));
            if (!ok)
            {
                if (rmism < 5)
                {
                    sb.AppendLine($"    room#{i} MISMATCH raw(id=0x{rr.Id:X},liq={rr.IsLiquidRoom},sub={rr.IsSubroom},min={V(rr.AabbMin)},max={V(rr.AabbMax)}) ged(id=0x{gr.Id:X},liq={gr.IsLiquidRoom},sub={gr.IsSubroom},min={gr.Aabb.P1},max={gr.Aabb.P2})");
                }

                rmism++;
            }
        }

        sb.AppendLine($"  face field mismatches: {mism}/{n}; room field mismatches: {rmism}/{rn}");
        sb.AppendLine(mism == 0 && rmism == 0
            ? "  => INDEPENDENCE CONFIRMED: raw decoder and Ged.Core parser agree field-for-field."
            : "  => DIVERGENCE: raw decoder and Ged.Core parser DISAGREE (investigate reader).");
        return sb.ToString();
    }

    private static string Invariants(RawGeo g)
    {
        var sb = new StringBuilder();
        int nFaces = g.Faces.Count;
        int roomOOB = 0, roomNeg = 0;
        int portalRoomOOB = 0, portalSame = 0;
        int vertOOB = 0;
        int subroomListRoomOOB = 0, subroomIdxOOB = 0;
        int surfRoomOOB = 0;
        foreach (RawFace f in g.Faces)
        {
            if (f.RoomIndex < 0)
            {
                roomNeg++;
            }
            else if (f.RoomIndex >= g.Rooms.Count)
            {
                roomOOB++;
            }

            foreach (RawVert v in f.Verts)
            {
                if (v.Index < 0 || v.Index >= g.Vertices.Count)
                {
                    vertOOB++;
                }
            }
        }

        foreach (RawPortal p in g.Portals)
        {
            if (p.Room1 < 0 || p.Room1 >= g.Rooms.Count || p.Room2 < 0 || p.Room2 >= g.Rooms.Count)
            {
                portalRoomOOB++;
            }
            else if (p.Room1 == p.Room2)
            {
                portalSame++;
            }
        }

        foreach (RawSubroomList sl in g.SubroomLists)
        {
            if (sl.RoomIndex < 0 || sl.RoomIndex >= g.Rooms.Count)
            {
                subroomListRoomOOB++;
            }

            foreach (int si in sl.SubIndices)
            {
                if (si < 0 || si >= g.Rooms.Count)
                {
                    subroomIdxOOB++;
                }
            }
        }

        foreach (RawSurface s in g.Surfaces)
        {
            if (s.RoomIndex >= g.Rooms.Count)
            {
                surfRoomOOB++;
            }
        }

        sb.AppendLine($"  faces={nFaces}; face.room_index: negative(-1 movers/unassigned)={roomNeg}  OUT-OF-RANGE={roomOOB}");
        sb.AppendLine($"  face vertex indices OUT-OF-RANGE={vertOOB}");
        sb.AppendLine($"  portals: room-index OUT-OF-RANGE={portalRoomOOB}  same-room(room1==room2)={portalSame}");
        sb.AppendLine($"  subroom-lists: parent OUT-OF-RANGE={subroomListRoomOOB}  child-index OUT-OF-RANGE={subroomIdxOOB}");
        sb.AppendLine($"  surfaces: room-index OUT-OF-RANGE={surfRoomOOB}");
        return sb.ToString();
    }

    /// <summary>
    /// For every non-portal world face with a real texture: is the WRITTEN room_index the room whose
    /// AABB smallest-contains the face centroid? RF.exe locates the camera by smallest-volume room and
    /// draws that room's faces; if a face's stored room_index is NOT its containing room, the face
    /// renders/culls under the wrong PVS cell — the "things disappearing" defect. Reports the fraction
    /// that agree AND the fraction whose stored room does not even geometrically CONTAIN the centroid.
    /// </summary>
    private static string RoomIndexConsistency(RawGeo g)
    {
        var sb = new StringBuilder();
        int probed = 0, agreeSmallest = 0, containsStored = 0, storedIsMain = 0;
        var roomAabb = g.Rooms.Select(r => (r.AabbMin, r.AabbMax, Vol(r.AabbMin, r.AabbMax))).ToArray();
        foreach (RawFace f in g.Faces)
        {
            if (f.Texture < 0 || f.Verts.Count < 3 || f.PortalIndexPlus2 >= 2)
            {
                continue; // portal / textureless faces excluded
            }

            if (f.RoomIndex < 0 || f.RoomIndex >= g.Rooms.Count)
            {
                probed++;
                continue; // stored room out of range: counts as disagreement
            }

            (float X, float Y, float Z) c = Centroid(g, f);
            int smallest = SmallestContaining(roomAabb, c);
            probed++;
            if (smallest == f.RoomIndex)
            {
                agreeSmallest++;
            }

            if (Contains(roomAabb[f.RoomIndex].AabbMin, roomAabb[f.RoomIndex].AabbMax, c))
            {
                containsStored++;
            }

            if (g.Rooms[f.RoomIndex].IsSubroom == 0)
            {
                storedIsMain++;
            }
        }

        double agree = probed == 0 ? 1 : agreeSmallest / (double)probed;
        double cont = probed == 0 ? 1 : containsStored / (double)probed;
        sb.AppendLine($"  probed world faces={probed}");
        sb.AppendLine($"  stored room == smallest-containing room: {agreeSmallest} ({agree:P1})");
        sb.AppendLine($"  stored room geometrically CONTAINS centroid: {containsStored} ({cont:P1})  [<100% => faces tagged to a room that does not enclose them]");
        sb.AppendLine($"  stored room is a MAIN room (not subroom): {storedIsMain}");
        return sb.ToString();
    }

    private static int CountDegeneratePortals(RawGeo g)
    {
        int n = 0;
        foreach (RawPortal p in g.Portals)
        {
            float dx = Math.Abs(p.P1.X - p.P2.X), dy = Math.Abs(p.P1.Y - p.P2.Y), dz = Math.Abs(p.P1.Z - p.P2.Z);
            int zeroAxes = (dx < 1e-4f ? 1 : 0) + (dy < 1e-4f ? 1 : 0) + (dz < 1e-4f ? 1 : 0);
            if (zeroAxes >= 2)
            {
                n++; // a portal box collapsed to a line/point cannot cull anything
            }
        }

        return n;
    }

    private static string PortalGeometrySummary(string tag, RawGeo g)
    {
        if (g.Portals.Count == 0)
        {
            return $"  {tag}: (no portals)\n";
        }

        double avgVol = g.Portals.Average(p => (double)Math.Abs(p.P2.X - p.P1.X) * Math.Abs(p.P2.Y - p.P1.Y) * Math.Abs(p.P2.Z - p.P1.Z));
        double avgDiag = g.Portals.Average(p =>
        {
            float dx = p.P2.X - p.P1.X, dy = p.P2.Y - p.P1.Y, dz = p.P2.Z - p.P1.Z;
            return Math.Sqrt((double)dx * dx + dy * dy + dz * dz);
        });
        return $"  {tag}: avg portal box diagonal={avgDiag:F3} m, avg box volume={avgVol:F3}\n";
    }

    private static string PortalMarkerSummary(string tag, RawGeo g)
    {
        int pip2Faces = g.Faces.Count(f => f.PortalIndexPlus2 >= 2);
        int texNegFaces = g.Faces.Count(f => f.Texture < 0);
        int pip2OOB = g.Faces.Count(f => f.PortalIndexPlus2 >= 2 && (f.PortalIndexPlus2 - 2) >= g.Portals.Count);
        return $"  {tag}: faces with portal_index_plus_2>=2: {pip2Faces} (portal-index OUT-OF-RANGE: {pip2OOB}); faces with texture<0: {texNegFaces}\n";
    }

    private static string FlagsHistogram(string tag, RawGeo g)
    {
        var hist = new SortedDictionary<ushort, int>();
        foreach (RawFace f in g.Faces)
        {
            hist.TryGetValue(f.Flags, out int c);
            hist[f.Flags] = c + 1;
        }

        var sb = new StringBuilder();
        sb.Append($"  {tag}: ");
        foreach (var kv in hist)
        {
            sb.Append($"0x{kv.Key:X4}:{kv.Value}  ");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Matches RED portals to GED portals by box centre and reports the projected opening size (the two
    /// largest box axes) for each, plus the aggregate GED/RED ratio. A ratio well under 1.0 on the projected
    /// axes = GED's culling window undershoots RED's = over-culling.
    /// </summary>
    private static string PortalBoxCompare(RawGeo red, RawGeo ged)
    {
        var sb = new StringBuilder();
        double sumRedProj = 0, sumGedProj = 0;
        int matched = 0;
        double worstRatio = double.MaxValue;
        string worst = "";
        var used = new bool[ged.Portals.Count];
        foreach (RawPortal rp in red.Portals)
        {
            (float X, float Y, float Z) rc = Mid(rp.P1, rp.P2);
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < ged.Portals.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                (float X, float Y, float Z) gc = Mid(ged.Portals[i].P1, ged.Portals[i].P2);
                double d = Dist2(rc, gc);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            if (best < 0 || bestD > 25f)
            {
                continue; // no GED portal within 5 m of this RED portal's centre
            }

            used[best] = true;
            matched++;
            (float w, float h) rproj = ProjExtent(rp.P1, rp.P2);
            (float w, float h) gproj = ProjExtent(ged.Portals[best].P1, ged.Portals[best].P2);
            double rArea = (double)rproj.w * rproj.h;
            double gArea = (double)gproj.w * gproj.h;
            sumRedProj += rArea;
            sumGedProj += gArea;
            double ratio = rArea <= 0 ? 1 : gArea / rArea;
            if (ratio < worstRatio)
            {
                worstRatio = ratio;
                worst = $"RED proj {rproj.w:F1}x{rproj.h:F1} vs GED proj {gproj.w:F1}x{gproj.h:F1} at ({rc.X:F1},{rc.Y:F1},{rc.Z:F1})";
            }
        }

        double aggRatio = sumRedProj <= 0 ? 1 : sumGedProj / sumRedProj;
        sb.AppendLine($"  matched {matched}/{red.Portals.Count} RED portals; aggregate GED/RED projected-opening-area ratio = {aggRatio:P0}");
        sb.AppendLine($"  worst single portal: ratio {(worstRatio == double.MaxValue ? 0 : worstRatio):P0}  [{worst}]");
        sb.AppendLine("  (ratio < 100% => GED culling window is smaller than RED's => over-culls the room beyond)");
        return sb.ToString();
    }

    private static (float X, float Y, float Z) Mid((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
        ((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f);

    private static double Dist2((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The two largest of the box's three axis extents — the doorway width/height that projects to screen.</summary>
    private static (float W, float H) ProjExtent((float X, float Y, float Z) p1, (float X, float Y, float Z) p2)
    {
        float a = Math.Abs(p2.X - p1.X), b = Math.Abs(p2.Y - p1.Y), c = Math.Abs(p2.Z - p1.Z);
        // drop the smallest (the ~normal thickness axis)
        float min = Math.Min(a, Math.Min(b, c));
        float max = Math.Max(a, Math.Max(b, c));
        float mid = a + b + c - min - max;
        return (max, mid);
    }

    // ---------------------------------------------------------------- geometry helpers

    private static (float X, float Y, float Z) Centroid(RawGeo g, RawFace f)
    {
        float x = 0, y = 0, z = 0;
        int n = 0;
        foreach (RawVert v in f.Verts)
        {
            if (v.Index >= 0 && v.Index < g.Vertices.Count)
            {
                var p = g.Vertices[v.Index];
                x += p.X;
                y += p.Y;
                z += p.Z;
                n++;
            }
        }

        return n == 0 ? (0, 0, 0) : (x / n, y / n, z / n);
    }

    private static int SmallestContaining((( float X, float Y, float Z) Min, (float X, float Y, float Z) Max, double Vol)[] rooms, (float X, float Y, float Z) p)
    {
        int best = -1;
        double bestVol = double.MaxValue;
        for (int i = 0; i < rooms.Length; i++)
        {
            if (Contains(rooms[i].Min, rooms[i].Max, p) && rooms[i].Vol < bestVol)
            {
                bestVol = rooms[i].Vol;
                best = i;
            }
        }

        return best;
    }

    private static bool Contains((float X, float Y, float Z) mn, (float X, float Y, float Z) mx, (float X, float Y, float Z) p)
    {
        const float e = 0.1f;
        return p.X >= mn.X - e && p.X <= mx.X + e && p.Y >= mn.Y - e && p.Y <= mx.Y + e && p.Z >= mn.Z - e && p.Z <= mx.Z + e;
    }

    private static double Vol((float X, float Y, float Z) mn, (float X, float Y, float Z) mx) =>
        Math.Abs((double)(mx.X - mn.X) * (mx.Y - mn.Y) * (mx.Z - mn.Z));

    private static bool Approx((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
        Math.Abs(a.X - b.X) < 1e-3f && Math.Abs(a.Y - b.Y) < 1e-3f && Math.Abs(a.Z - b.Z) < 1e-3f;

    private static string V((float X, float Y, float Z) v) => $"({v.X:F1},{v.Y:F1},{v.Z:F1})";

    private static void WriteArtifact(string name, string text)
    {
        DirectoryInfo? dir = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name), text);
    }

    private static DirectoryInfo? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        return dir;
    }

    // ================================================================ INDEPENDENT RAW DECODER
    // A from-scratch little-endian decoder of the RFL container + static_geometry (0x100) body.
    // Deliberately does NOT use RfReader / Geometry.Parse. Layout per RF.exe FUN_004ed520 + rfl.ksy.

    internal readonly record struct RawVert(int Index, float U, float V, float Lu, float Lv, bool HasLm);

    internal sealed class RawFace
    {
        public (float X, float Y, float Z) Normal;
        public float PlaneOffset;
        public int Texture;
        public int SurfaceIndex;
        public int FaceId;
        public int Res1A;
        public int Res1B;
        public int PortalIndexPlus2;
        public ushort Flags;
        public ushort Reserved2;
        public uint SmoothingGroups;
        public int RoomIndex;
        public List<RawVert> Verts = new();
    }

    internal sealed class RawRoom
    {
        public int Id;
        public (float X, float Y, float Z) AabbMin;
        public (float X, float Y, float Z) AabbMax;
        public byte IsSkyroom, IsCold, IsOutside, IsAirlock, IsLiquidRoom, HasAmbientLight, IsSubroom, HasAlpha;
        public float Life;
        public string? Eax;
    }

    internal readonly record struct RawPortal(int Room1, int Room2, (float X, float Y, float Z) P1, (float X, float Y, float Z) P2);

    internal sealed class RawSubroomList
    {
        public int RoomIndex;
        public List<int> SubIndices = new();
    }

    internal sealed class RawSurface
    {
        public int LightmapIndex;
        public int RoomIndex;
    }

    internal sealed class RawGeo
    {
        public int Version;
        public List<string> Textures = new();
        public List<RawRoom> Rooms = new();
        public List<RawSubroomList> SubroomLists = new();
        public List<RawPortal> Portals = new();
        public List<(float X, float Y, float Z)> Vertices = new();
        public List<RawFace> Faces = new();
        public List<RawSurface> Surfaces = new();

        public static RawGeo Decode(byte[] data)
        {
            var c = new Cursor(data);
            uint magic = c.U32();
            if (magic != 0xD4BADA55)
            {
                throw new InvalidDataException($"bad magic 0x{magic:X8}");
            }

            int version = c.I32();
            c.U32(); // timestamp
            c.I32(); // player_start_offset
            c.I32(); // level_info_offset
            c.I32(); // num_sections
            c.I32(); // sections_total_size
            c.VString(); // level_name
            bool hasModName = version >= 0xB2 && version != 0x127;
            if (hasModName)
            {
                c.VString(); // mod_name
            }

            // Walk sections to the static_geometry (0x100).
            int geoOffset = -1, geoLen = 0;
            while (c.Pos + 8 <= data.Length)
            {
                int start = c.Pos;
                uint type = c.U32();
                int len = c.I32();
                if (len < 0 || start + 8 + len > data.Length)
                {
                    break;
                }

                if (type == 0x00000100)
                {
                    geoOffset = c.Pos;
                    geoLen = len;
                }

                c.Skip(len);
            }

            if (geoOffset < 0)
            {
                throw new InvalidDataException("no static_geometry (0x100) section");
            }

            var g = new RawGeo { Version = version };
            var b = new Cursor(data) { Pos = geoOffset };
            int geoEnd = geoOffset + geoLen;

            bool newMod = version >= 0xC8;
            bool faceScroll = version >= 0xB4;
            bool legacyScroll = version <= 0xB4;
            bool eax = version >= 0xB4;

            if (newMod)
            {
                b.U32(); // unknown1
                b.U32(); // modifiability
                b.VString(); // name
            }
            else
            {
                b.VString(); // name
                b.U32(); // modifiability_old
            }

            int numTex = b.I32();
            for (int i = 0; i < numTex; i++)
            {
                g.Textures.Add(b.VString());
            }

            if (faceScroll)
            {
                int n = b.I32();
                for (int i = 0; i < n; i++)
                {
                    b.I32();
                    b.F32();
                    b.F32();
                }
            }
            else
            {
                int n = b.I32();
                b.Skip(n * 0x29);
            }

            int numRooms = b.I32();
            for (int i = 0; i < numRooms; i++)
            {
                var r = new RawRoom
                {
                    Id = b.I32(),
                    AabbMin = b.Vec3(),
                    AabbMax = b.Vec3(),
                    IsSkyroom = b.U8(),
                    IsCold = b.U8(),
                    IsOutside = b.U8(),
                    IsAirlock = b.U8(),
                    IsLiquidRoom = b.U8(),
                    HasAmbientLight = b.U8(),
                    IsSubroom = b.U8(),
                    HasAlpha = b.U8(),
                    Life = b.F32(),
                };
                if (eax)
                {
                    r.Eax = b.VString();
                }

                if (r.IsLiquidRoom != 0)
                {
                    // room_liquid_properties
                    b.F32();            // depth
                    b.U32();            // color
                    b.VString();        // surface_texture
                    b.F32();            // visibility
                    b.I32();            // liquid_type
                    b.I32();            // liquid_alpha
                    b.U8();             // contains_plankton
                    b.I32();            // ppm_u
                    b.I32();            // ppm_v
                    b.F32();            // angle
                    b.I32();            // waveform
                    b.F32();            // scroll u
                    b.F32();            // scroll v
                }

                if (r.HasAmbientLight != 0)
                {
                    b.U32();            // ambient color
                }

                g.Rooms.Add(r);
            }

            int numSub = b.I32();
            for (int i = 0; i < numSub; i++)
            {
                var sl = new RawSubroomList { RoomIndex = b.I32() };
                int ns = b.I32();
                for (int j = 0; j < ns; j++)
                {
                    sl.SubIndices.Add(b.I32());
                }

                g.SubroomLists.Add(sl);
            }

            int numPortals = b.I32();
            for (int i = 0; i < numPortals; i++)
            {
                g.Portals.Add(new RawPortal(b.I32(), b.I32(), b.Vec3(), b.Vec3()));
            }

            int numVerts = b.I32();
            for (int i = 0; i < numVerts; i++)
            {
                g.Vertices.Add(b.Vec3());
            }

            int numFaces = b.I32();
            for (int i = 0; i < numFaces; i++)
            {
                var f = new RawFace
                {
                    Normal = b.Vec3(),
                    PlaneOffset = b.F32(),
                    Texture = b.I32(),
                    SurfaceIndex = b.I32(),
                    FaceId = b.I32(),
                    Res1A = b.I32(),
                    Res1B = b.I32(),
                    PortalIndexPlus2 = b.I32(),
                    Flags = b.U16(),
                    Reserved2 = b.U16(),
                    SmoothingGroups = b.U32(),
                    RoomIndex = b.I32(),
                };
                int nv = b.I32();
                bool hasLm = (f.SurfaceIndex & 0xFFFF) != 0xFFFF;
                for (int v = 0; v < nv; v++)
                {
                    int idx = b.I32();
                    float u = b.F32(), vv = b.F32();
                    float lu = 0, lv = 0;
                    if (hasLm)
                    {
                        lu = b.F32();
                        lv = b.F32();
                    }

                    f.Verts.Add(new RawVert(idx, u, vv, lu, lv, hasLm));
                }

                g.Faces.Add(f);
            }

            int numSurf = b.I32();
            for (int i = 0; i < numSurf; i++)
            {
                var s = new RawSurface { LightmapIndex = b.I32() };
                b.U8(); b.U8(); b.U8(); b.U8();          // x,y,w,h
                b.F32(); b.F32();                        // x/y ppm
                b.Vec3(); b.Vec3();                      // bbox
                b.Vec3(); b.F32();                       // plane
                b.I32();                                 // should_smooth
                b.I32();                                 // unknown_zero
                b.I32();                                 // dropped_coeff
                b.I32();                                 // u_coeff
                b.I32();                                 // v_coeff
                b.F32(); b.F32();                        // uv_add
                b.F32(); b.F32();                        // uv_scale
                s.RoomIndex = b.I32();
                g.Surfaces.Add(s);
            }

            // (legacy face-scroll after surfaces for version <= 0xB4 — not needed for the audit)
            _ = geoEnd;
            _ = legacyScroll;
            return g;
        }
    }

    private sealed class Cursor
    {
        private readonly byte[] _d;

        public Cursor(byte[] d) => _d = d;

        public int Pos { get; set; }

        public void Skip(int n) => Pos += n;

        public byte U8() => _d[Pos++];

        public ushort U16()
        {
            ushort v = (ushort)(_d[Pos] | (_d[Pos + 1] << 8));
            Pos += 2;
            return v;
        }

        public uint U32()
        {
            uint v = (uint)(_d[Pos] | (_d[Pos + 1] << 8) | (_d[Pos + 2] << 16) | (_d[Pos + 3] << 24));
            Pos += 4;
            return v;
        }

        public int I32() => unchecked((int)U32());

        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public (float X, float Y, float Z) Vec3() => (F32(), F32(), F32());

        public string VString()
        {
            int len = U16();
            string s = Encoding.Latin1.GetString(_d, Pos, len);
            Pos += len;
            return s;
        }
    }
}
