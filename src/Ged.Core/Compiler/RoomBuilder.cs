using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>Outcome of room building: the compiled rooms plus per-brush detail-room lookup.</summary>
public sealed class RoomBuildResult
{
    public List<Room> Rooms { get; } = new();

    /// <summary>Parent→children subroom lists (detail rooms attached to their container).</summary>
    public List<SubroomList> SubroomLists { get; } = new();

    /// <summary>Room index each face was assigned to (parallel to the compiler's face list).</summary>
    public int[] FaceRoom { get; set; } = Array.Empty<int>();

    /// <summary>Room index a detail brush isolated into, keyed by brush uid (Alpine mapping).</summary>
    public Dictionary<int, int> BrushRoom { get; } = new();

    /// <summary>Locates the room nearest a world point (portal-aware, face-based).</summary>
    public RoomLocator? Locator { get; set; }
}

/// <summary>
/// Groups compiled faces into rooms with RED's mode-4 portal semantics: world
/// faces have already been chopped at portal membranes (no face spans a doorway
/// sheet), so a room is an edge-adjacency connected component of the open
/// boundary where adjacency is BLOCKED across edges lying on a membrane sheet —
/// the two sides of a doorway flood into separate rooms exactly at the portal,
/// with no action at a distance from the membrane's infinite plane. Detail-gated
/// adjacency keeps detail brushes in their own subrooms (touching ones merge,
/// Alpine isolates geoable/breakable). Room AABBs are the union of member-face
/// AABBs; ids come from a containing room-effect (else synthetic); life from the
/// source brush.
/// </summary>
public sealed class RoomBuilder
{
    private readonly BuildReport _report;

    /// <summary>DIAGNOSTIC (test-only, off by default): capture the face-adjacency joins the flood used —
    /// (both faces' centroids, edge midpoint, kind 0=exact-manifold 1=collinear-overlap) — so a test can
    /// trace the exact edges connecting two rooms that should be separate. Static: set before Compile.
    /// Adds are lock-guarded so a concurrent unrelated compile (parallel test classes) cannot corrupt the
    /// list while a diagnosis is capturing.</summary>
    internal static bool CaptureJoins;

    /// <summary>DIAGNOSTIC (test-only): force EVERY membrane through the face-vote branch (bypass the
    /// geometric-consistent fast path), to measure the pure face-vote result against the hybrid + probe.</summary>
    internal static bool ForceAlwaysVote;

    private static readonly object CaptureLock = new();

    /// <summary>The captured joins of the LAST flood when <see cref="CaptureJoins"/> is set.
    /// Face indices are in the room-builder's face list (pre-compaction).</summary>
    internal static List<(int A, int B, Vec3 CentroidA, Vec3 CentroidB, Vec3 Mid, int Kind)>? CapturedJoins;

    /// <summary>Membrane side assignments of the LAST build when <see cref="CaptureJoins"/> is set:
    /// (brush uid, probe point, front room, back room, authored-opening polygon or null).</summary>
    internal static List<(int BrushUid, Vec3 Probe, int FrontRoom, int BackRoom, List<Vec3>? Opening)>? CapturedMembranes;

    /// <summary>Per-membrane resolution detail of the LAST build (when <see cref="CaptureJoins"/> is set):
    /// (brush uid, plane normal, plane offset, footprint min/max, fragment count, area, drop-area,
    /// needed-vote, group id, resolved front/back room).</summary>
    internal static List<(int Uid, Vec3 Normal, float Offset, Vec3 FpMin, Vec3 FpMax, int Frags, float Area, float DropArea, bool Voted, int Group, int Front, int Back)>? CapturedMembraneDetail;

    public RoomBuilder(BuildReport report)
    {
        _report = report;
    }

    public RoomBuildResult Build(
        List<CsgFace> faces,
        List<int[]> facePoolIndices,
        IReadOnlyList<Vec3> pool,
        int openCount,
        IReadOnlyList<(Brush Brush, bool IsAir, List<CsgFace> Faces)> worldBrushes,
        IReadOnlyList<(Brush Brush, List<CsgFace> Faces)> detailBrushes,
        IReadOnlyList<PortalMembrane> membranes,
        IReadOnlyList<RoomEffect> effects,
        bool alpine,
        bool portalFaceVote = true)
    {
        int n = faces.Count;
        if (CaptureJoins)
        {
            CapturedJoins = new List<(int, int, Vec3, Vec3, Vec3, int)>();
            CapturedMembranes = new List<(int, Vec3, int, int, List<Vec3>?)>();
            CapturedMembraneDetail = new List<(int, Vec3, float, Vec3, Vec3, int, float, float, bool, int, int, int)>();
        }

        var result = new RoomBuildResult { FaceRoom = new int[n] };
        Array.Fill(result.FaceRoom, -1);

        var isolatedUids = new HashSet<int>();
        var brushLife = new Dictionary<int, float>();
        foreach ((Brush b, bool _, List<CsgFace> _) in worldBrushes)
        {
            brushLife[b.Uid] = b.Life;
        }

        foreach ((Brush b, List<CsgFace> _) in detailBrushes)
        {
            brushLife[b.Uid] = b.Life;
            if (alpine && IsIsolated(b))
            {
                isolatedUids.Add(b.Uid);
            }
        }

        var isDetail = new bool[n];
        var isolated = new bool[n];
        for (int i = 0; i < n; i++)
        {
            isDetail[i] = i >= openCount;
            isolated[i] = isDetail[i] && isolatedUids.Contains(faces[i].SourceBrushUid);
        }

        // --- Membrane sheets (block adjacency through the doorway) ---
        var sheets = new List<MembraneSheet>(membranes.Count);
        foreach (PortalMembrane m in membranes)
        {
            sheets.Add(new MembraneSheet(m));
        }

        // --- Flood fill: connected components with sheet-blocked adjacency ---
        int[] comp = FloodFill(faces, facePoolIndices, pool, isDetail, isolated, sheets, out int _);

        // --- Rooms per component (world components first-face order, detail as subrooms) ---
        var roomOfComp = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            if (faces[i].IsPortal || comp[i] < 0)
            {
                continue;
            }

            if (!roomOfComp.TryGetValue(comp[i], out int room))
            {
                room = result.Rooms.Count;
                roomOfComp[comp[i]] = room;
                result.Rooms.Add(new Room
                {
                    IsSubroom = (byte)(isDetail[i] ? 1 : 0),
                    Life = brushLife.GetValueOrDefault(faces[i].SourceBrushUid, -1f),
                });
            }

            faces[i].RoomIndex = room;
            result.FaceRoom[i] = room;
            if (isDetail[i])
            {
                result.BrushRoom[faces[i].SourceBrushUid] = room;
            }
        }

        // --- Isolated-brush interior merge (Alpine: merge_geoable_interior_rooms) -------
        // A geoable/breakable brush's faces can flood into SEVERAL disconnected components: a
        // concave brush (whose inner and outer shells are not edge-connected) always does, and even
        // a convex brush does when a face coincident with an adjacent wall welds its corners onto
        // the wall's shared pool vertices, severing the exact manifold edge it had with its own box.
        // Each fragment becomes its own detail room, but the alpine_level_properties table records
        // only ONE room per brush, so in-game the boolean carve (destruction.cpp: a face carves iff
        // face->which_room == the target geoable room) reaches only the faces in that one room — the
        // classic "only the top / only one side geomods". RED consolidates every room owning a given
        // isolated brush's faces into one primary room (editor_patch/level.cpp
        // merge_geoable_interior_rooms) so the whole brush shares a single geoable room. GED mirrors
        // that: reassign all of a brush's isolated faces to its lowest-indexed room, then drop the
        // emptied fragments. MUST run before the AABB/locator/attach stages so they see one room.
        MergeIsolatedBrushRooms(faces, result, isolated);

        // --- Junk-room merge (item 5) --------------------------------------------------
        // Community levels leave millimetre gaps between nearly-flush brushes; the CSG
        // emits the exposed gap ribbons as real (tiny) faces, and when a ribbon sits on a
        // portal membrane plane the sheet gate correctly blocks its edge adjacency — but
        // then it floods into a portal-less singleton "room" of a few cm². RED attaches
        // such fragments to the surrounding room. Emitting them as rooms is what breaks
        // the level in-game: RF's smallest-volume point-in-room lookup can land the camera
        // in one, and the portal flood then renders nothing (dmabruptdecay: 34 junk rooms
        // ⇒ the reported vanishing brushwork). Merge every main room whose total face area
        // is below a threshold no real room can be under into the room a probe just off
        // its largest face lands in.
        MergeJunkRooms(faces, result);

        // --- Room AABBs from member faces (portal faces added by BuildRecords) ---
        // Computed BEFORE membrane side-resolution so the portal side probe can use RF's own
        // point-in-room rule (smallest-volume containing room) against the member-face extents.
        var min = new Vec3[result.Rooms.Count];
        var max = new Vec3[result.Rooms.Count];
        var seeded = new bool[result.Rooms.Count];
        for (int i = 0; i < n; i++)
        {
            int room = result.FaceRoom[i];
            if (room < 0 || faces[i].IsPortal)
            {
                continue;
            }

            GrowRoom(min, max, seeded, room, faces[i]);
        }

        for (int r = 0; r < result.Rooms.Count; r++)
        {
            result.Rooms[r].Aabb = seeded[r] ? new Aabb(min[r], max[r]) : default;
        }

        // --- Locator over the world faces (nearest-face room lookup) ---
        result.Locator = new RoomLocator(faces, result.FaceRoom);

        // A portal connects two MAIN rooms; a detail brush (subroom) merely sits inside a main room,
        // often right in front of a doorway. A vertical-ray probe just off the membrane can land on
        // the detail subroom's floor (resolving that side to a SUBROOM, which BuildRecords cannot use)
        // or, at a doorway above a nested lower room, on the lower room's ceiling — stranding the link.
        // RED assigns a portal's side from the ADJACENT world room (FUN_00485850), i.e. the enclosing
        // MAIN room. A main-room-only locator (subroom faces masked out) is the vertical-ray fallback.
        var mainFaceRoom = (int[])result.FaceRoom.Clone();
        for (int i = 0; i < n; i++)
        {
            if (mainFaceRoom[i] >= 0 && result.Rooms[mainFaceRoom[i]].IsSubroom != 0)
            {
                mainFaceRoom[i] = -1;
            }
        }

        var mainLocator = new RoomLocator(faces, mainFaceRoom);

        // --- Membrane faces: room from a probe just off each side of the sheet, PER FRAGMENT ---
        // A single per-membrane probe (the old m.FrontFaces[0]) mis-handles a TALL doorway whose
        // fragments border DIFFERENT rooms at different heights: dmabrupt's water pool (y[-9..-2])
        // and the air room above it share one doorway sheet, so one probe picked a single pair and
        // the other side got no portal — the water room fell to 2 portal faces where RED has 28. RED
        // votes room adjacency per membrane REGION (FUN_004861d0), so a sheet bordering N room pairs
        // emits N portals. Probing each fragment off its own centroid reproduces that and is identical
        // for a normal single-pair doorway (all fragments resolve the same pair, deduped downstream).
        // Each side resolves to the SMALLEST-volume MAIN room whose member-face AABB contains the
        // offset point — RF's own runtime point-in-room rule — so the portal graph is consistent with
        // how the game will pick the camera's room; a vertical ray (main-only) is the fallback when
        // the offset point lies outside every room extent. This fixes the two failure modes a bare
        // vertical ray cannot: a HORIZONTAL ceiling/floor opening (both offsets share one column, so
        // the ray returns the lower room for both — hub #2 <-> sky #1) and a doorway whose far side
        // sits above a nested lower room (the ray hits the lower room's surface — the hub niche links).
        if (portalFaceVote)
        {
            ResolveMembraneSidesByFaceVote(faces, result, membranes, mainLocator);
        }
        else
        {
            ResolveMembraneSidesByProbe(faces, result, membranes, mainLocator);
        }

        for (int i = 0; i < n; i++)
        {
            if (faces[i].IsPortal)
            {
                result.FaceRoom[i] = faces[i].RoomIndex;
            }
        }

        AssignRoomEffects(result, effects);
        RollUpAlpha(faces, result);
        BuildSubroomLists(faces, result, mainLocator);
        return result;
    }

    /// <summary>
    /// Sets each room's <see cref="Room.HasAlpha"/> from its faces: a room is an alpha room iff at
    /// least one of its non-portal faces carries the texture-derived <see cref="FaceFlags.HasAlpha"/>
    /// (0x40) bit. This is RED's build-time rollup — RF's renderer reads room.has_alpha to schedule the
    /// room in the alpha pass, which is what draws the glass in an encased window frame. Binary-exact:
    /// on dm04 + dmabrupt, all 211 compiled rooms satisfy room.has_alpha == (room owns an alpha face),
    /// with zero exceptions either direction (the alpha rooms are the detail-glass subrooms).
    /// </summary>
    private static void RollUpAlpha(List<CsgFace> faces, RoomBuildResult result)
    {
        for (int i = 0; i < faces.Count; i++)
        {
            int room = result.FaceRoom[i];
            if (room < 0 || room >= result.Rooms.Count || faces[i].IsPortal)
            {
                continue;
            }

            if ((faces[i].Flags & (ushort)FaceFlags.HasAlpha) != 0)
            {
                result.Rooms[room].HasAlpha = 1;
            }
        }
    }

    private static bool IsIsolated(Brush b)
    {
        var flags = (BrushFlags)b.Flags;
        return (flags & BrushFlags.Geoable) != 0 || b.Life >= 0;
    }

    /// <summary>A main room under this much total face area (m²) is CSG gap-ribbon junk, not a room.</summary>
    private const float JunkRoomArea = 0.25f;

    /// <summary>
    /// Merges sub-threshold main rooms into the room a probe just off their largest face
    /// resolves to, then compacts the room list (indices remapped everywhere they exist at
    /// this stage: face RoomIndex, FaceRoom, BrushRoom). Junk faces are masked out of the
    /// probe locator so one ribbon can never adopt another.
    /// </summary>
    private void MergeJunkRooms(List<CsgFace> faces, RoomBuildResult result)
    {
        int roomCount = result.Rooms.Count;
        if (roomCount == 0)
        {
            return;
        }

        var area = new float[roomCount];
        var largest = new int[roomCount];
        var allInvisible = new bool[roomCount];
        Array.Fill(largest, -1);
        Array.Fill(allInvisible, true);
        for (int i = 0; i < faces.Count; i++)
        {
            int r = result.FaceRoom[i];
            if (r < 0 || faces[i].IsPortal)
            {
                continue;
            }

            float a = faces[i].Area();
            area[r] += a;
            if (largest[r] < 0 || a > faces[largest[r]].Area())
            {
                largest[r] = i;
            }

            if ((faces[i].Flags & (ushort)FaceFlags.IsInvisible) == 0)
            {
                allInvisible[r] = false;
            }
        }

        var junk = new bool[roomCount];
        bool anyJunk = false;
        for (int r = 0; r < roomCount; r++)
        {
            if (result.Rooms[r].IsSubroom != 0 || largest[r] < 0)
            {
                continue;
            }

            // Sub-threshold CSG gap ribbons are junk. So is a fully INVISIBLE main room (all faces
            // mtl_invisible*) nested inside a larger main room: these are invisible AIR-brush clip
            // pockets (dmabrupt uid 10336/10338, 2.0x6.0x0.8 boxes) that GED floods as their own
            // sealed portal-less room but RED assigns to the SURROUNDING room (verified: RED puts the
            // same mtl_invisible02 faces in room 2 / room 1, not a distinct room). Left standing they
            // are 2 spurious main rooms RF's smallest-volume point-in-room can trap the camera in.
            // A fully INVISIBLE main room is a sealed invisible AIR-brush clip pocket; merge it only
            // when a real surrounding room hosts it (the merge below finds the host via a probe /
            // nearest non-junk main face and keeps the room untouched if none is found — so a genuine
            // free-standing invisible structure is never dissolved into a distant room).
            bool ribbon = area[r] < JunkRoomArea;
            if (ribbon || allInvisible[r])
            {
                junk[r] = true;
                anyJunk = true;
            }
        }

        if (!anyJunk)
        {
            return;
        }

        int junkTotal = 0;
        for (int r = 0; r < roomCount; r++)
        {
            if (junk[r])
            {
                junkTotal++;
            }
        }

        // Probe locator that ignores junk-room faces (mask them to -1).
        var masked = (int[])result.FaceRoom.Clone();
        for (int i = 0; i < faces.Count; i++)
        {
            if (masked[i] >= 0 && junk[masked[i]])
            {
                masked[i] = -1;
            }
        }

        var probeLocator = new RoomLocator(faces, masked);

        // Resolve each junk room to a host room (probe both sides of its largest face).
        var target = new int[roomCount];
        for (int r = 0; r < roomCount; r++)
        {
            target[r] = r;
            if (!junk[r])
            {
                continue;
            }

            CsgFace f = faces[largest[r]];
            Vec3 c = f.Centroid();
            Vec3 n = f.Plane.Normal;
            int host = probeLocator.Locate(c.Add(n.Scale(0.05f)));
            if (host < 0 || junk[host])
            {
                host = probeLocator.Locate(c.Sub(n.Scale(0.05f)));
            }

            if (host < 0 || junk[host] || result.Rooms[host].IsSubroom != result.Rooms[r].IsSubroom)
            {
                // A ribbon on a rim edge can defeat the vertical-ray probe (the probe sits
                // exactly on the neighbouring polygons' shared boundary), and the probe can
                // land on a detail subroom. Fall back to the room of the nearest non-junk
                // face whose room KIND matches (main hosts main).
                host = NearestHostRoom(faces, masked, junk, result.Rooms, result.Rooms[r].IsSubroom, c);
            }

            if (host >= 0 && !junk[host] && result.Rooms[host].IsSubroom == result.Rooms[r].IsSubroom)
            {
                target[r] = host;
            }
            else
            {
                junk[r] = false; // no host found: keep the room rather than corrupt anything
            }
        }

        // Compact rooms, dropping merged junk; remap all indices assigned so far.
        var remap = new int[roomCount];
        var kept = new List<Room>(roomCount);
        for (int r = 0; r < roomCount; r++)
        {
            if (junk[r])
            {
                remap[r] = -2; // resolved via target after keepers get their slots
            }
            else
            {
                remap[r] = kept.Count;
                kept.Add(result.Rooms[r]);
            }
        }

        for (int r = 0; r < roomCount; r++)
        {
            if (remap[r] == -2)
            {
                remap[r] = remap[target[r]];
            }
        }

        result.Rooms.Clear();
        result.Rooms.AddRange(kept);

        for (int i = 0; i < faces.Count; i++)
        {
            int r = result.FaceRoom[i];
            if (r >= 0)
            {
                result.FaceRoom[i] = remap[r];
                faces[i].RoomIndex = remap[r];
            }
        }

        foreach (int uid in new List<int>(result.BrushRoom.Keys))
        {
            result.BrushRoom[uid] = remap[result.BrushRoom[uid]];
        }

        int merged = roomCount - result.Rooms.Count;
        if (merged > 0 || junkTotal > 0)
        {
            _report.Add(BuildSeverity.Info,
                $"Junk-room cleanup: merged {merged} gap-ribbon room(s) into their neighbours ({junkTotal - merged} kept — no host).");
        }
    }

    /// <summary>
    /// Consolidates the detail rooms an isolated geoable/breakable brush's faces flooded into
    /// (RED's <c>merge_geoable_interior_rooms</c>). Each isolated flood component is single-brush
    /// (<see cref="CanJoin"/> only pairs same-uid isolated faces), so a brush whose faces span more
    /// than one component owns more than one detail room; the game's per-face room-membership carve
    /// only reaches the ONE room the alpine table names, so the other faces never geomod. This
    /// reassigns every one of a brush's isolated faces to that brush's lowest-indexed room and drops
    /// the emptied fragment rooms, remapping face rooms and the brush→room map in lockstep. Nothing
    /// happens when every isolated brush already occupies a single room.
    /// </summary>
    private void MergeIsolatedBrushRooms(List<CsgFace> faces, RoomBuildResult result, bool[] isolated)
    {
        // Rooms owning each isolated brush's faces (kept ascending — first is the primary).
        var roomsOfBrush = new Dictionary<int, List<int>>();
        for (int i = 0; i < faces.Count; i++)
        {
            if (!isolated[i])
            {
                continue;
            }

            int room = result.FaceRoom[i];
            if (room < 0)
            {
                continue;
            }

            int uid = faces[i].SourceBrushUid;
            if (!roomsOfBrush.TryGetValue(uid, out List<int>? list))
            {
                roomsOfBrush[uid] = list = new List<int>();
            }

            if (!list.Contains(room))
            {
                list.Add(room);
            }
        }

        // Fold each multi-room brush's fragment rooms into its lowest-indexed room.
        var absorbedInto = new int[result.Rooms.Count];
        for (int r = 0; r < absorbedInto.Length; r++)
        {
            absorbedInto[r] = r;
        }

        var dead = new bool[result.Rooms.Count];
        int fragments = 0;
        int brushesMerged = 0;
        foreach (List<int> rooms in roomsOfBrush.Values)
        {
            if (rooms.Count < 2)
            {
                continue;
            }

            rooms.Sort();
            int primary = rooms[0];
            brushesMerged++;
            for (int k = 1; k < rooms.Count; k++)
            {
                absorbedInto[rooms[k]] = primary;
                dead[rooms[k]] = true;
                fragments++;
            }
        }

        if (fragments == 0)
        {
            return;
        }

        // Reassign the absorbed rooms' faces to their brush's primary room.
        for (int i = 0; i < faces.Count; i++)
        {
            int r = result.FaceRoom[i];
            if (r >= 0 && absorbedInto[r] != r)
            {
                result.FaceRoom[i] = absorbedInto[r];
                faces[i].RoomIndex = absorbedInto[r];
            }
        }

        // Compact: drop the now-empty fragment rooms, remapping every stored index.
        var remap = new int[result.Rooms.Count];
        var kept = new List<Room>(result.Rooms.Count);
        for (int r = 0; r < result.Rooms.Count; r++)
        {
            if (dead[r])
            {
                remap[r] = -1;
            }
            else
            {
                remap[r] = kept.Count;
                kept.Add(result.Rooms[r]);
            }
        }

        result.Rooms.Clear();
        result.Rooms.AddRange(kept);

        for (int i = 0; i < faces.Count; i++)
        {
            int r = result.FaceRoom[i];
            if (r >= 0)
            {
                result.FaceRoom[i] = remap[r];
                faces[i].RoomIndex = remap[r];
            }
        }

        foreach (int uid in new List<int>(result.BrushRoom.Keys))
        {
            int r = result.BrushRoom[uid];
            if (r >= 0)
            {
                result.BrushRoom[uid] = remap[absorbedInto[r]];
            }
        }

        _report.Add(BuildSeverity.Info,
            $"Geoable interior merge: folded {fragments} fragment room(s) of {brushesMerged} isolated brush(es) into their primary detail room.");
    }

    /// <summary>Room of the nearest non-junk, non-portal, kind-matching face by squared centroid distance, or -1.</summary>
    private static int NearestHostRoom(
        List<CsgFace> faces, int[] maskedRoom, bool[] junk, List<Room> rooms, byte isSubroom, Vec3 p)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < faces.Count; i++)
        {
            int r = maskedRoom[i];
            if (r < 0 || junk[r] || faces[i].IsPortal || rooms[r].IsSubroom != isSubroom)
            {
                continue;
            }

            float d = faces[i].Centroid().Sub(p).LengthSquared();
            if (d < bestD)
            {
                bestD = d;
                best = r;
            }
        }

        return best;
    }

    /// <summary>
    /// Connected components via manifold edge adjacency, gated by detail-ness /
    /// isolation, with adjacency blocked across edges that lie on a portal
    /// membrane sheet (the doorway divider). Edges that find no EXACT manifold
    /// partner get a second, geometric pass: collinear-overlap matching (item 5) —
    /// RED's own adjacency (FUN_004861d0) is a geometric edge-vs-plane vote, not an
    /// exact-vertex-pair test, so fragments left by CSG over-splitting (residual
    /// T-junctions) must still connect. Without it the flood strands parts of the
    /// playfield in portal-less singleton rooms which RF's portal-flood renderer
    /// never draws (dmabruptdecay: 34 orphan main rooms — the reported in-game
    /// "missing brushwork").
    /// </summary>
    private static int[] FloodFill(
        List<CsgFace> faces,
        List<int[]> facePoolIndices,
        IReadOnlyList<Vec3> pool,
        bool[] isDetail,
        bool[] isolated,
        List<MembraneSheet> sheets,
        out int count)
    {
        int n = faces.Count;
        var comp = new int[n];
        Array.Fill(comp, -1);

        var edgeMap = new Dictionary<(int, int), List<(int Face, int A, int B)>>();
        for (int fi = 0; fi < n; fi++)
        {
            if (faces[fi].IsPortal)
            {
                continue; // membranes take no part in base connectivity
            }

            int[] idx = facePoolIndices[fi];
            int m = idx.Length;
            for (int e = 0; e < m; e++)
            {
                int a = idx[e], b = idx[(e + 1) % m];
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                if (!edgeMap.TryGetValue(key, out List<(int, int, int)>? list))
                {
                    edgeMap[key] = list = new List<(int, int, int)>();
                }

                list.Add((fi, a, b));
            }
        }

        var adj = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            adj[i] = new List<int>();
        }

        bool OnSheet(Vec3 mid)
        {
            foreach (MembraneSheet sheet in sheets)
            {
                if (sheet.Contains(mid))
                {
                    return true;
                }
            }

            return false;
        }

        // Pass 1: exact manifold pairing (fast path). Track which edge INSTANCES paired
        // so the geometric pass only has to consider the true leftovers (a key where two
        // of three faces pair must still surface the stranded third).
        var pairedInstances = new HashSet<(int Face, int A, int B)>();
        foreach (KeyValuePair<(int, int), List<(int Face, int A, int B)>> entry in edgeMap)
        {
            List<(int Face, int A, int B)> shares = entry.Value;

            // An edge lying on a membrane sheet is the doorway boundary: the faces
            // meeting there belong to different rooms, so it connects nothing (and
            // must not be revived by the geometric pass either).
            if (sheets.Count > 0)
            {
                (int a, int b) = entry.Key;
                if (OnSheet(Vec3Math.Lerp(pool[a], pool[b], 0.5f)))
                {
                    foreach ((int Face, int A, int B) share in shares)
                    {
                        pairedInstances.Add(share);
                    }

                    continue;
                }
            }

            if (shares.Count < 2)
            {
                continue;
            }

            for (int i = 0; i < shares.Count; i++)
            {
                for (int j = i + 1; j < shares.Count; j++)
                {
                    if (shares[i].A != shares[j].B || shares[i].B != shares[j].A)
                    {
                        continue; // require opposite traversal (same-cell manifold pairing)
                    }

                    int fi = shares[i].Face, fj = shares[j].Face;
                    pairedInstances.Add(shares[i]);
                    pairedInstances.Add(shares[j]);
                    if (CanJoin(faces, isDetail, isolated, fi, fj))
                    {
                        adj[fi].Add(fj);
                        adj[fj].Add(fi);
                        if (CaptureJoins)
                        {
                            (int a, int b) = entry.Key;
                            lock (CaptureLock)
                            {
                                (CapturedJoins ??= new List<(int, int, Vec3, Vec3, Vec3, int)>())
                                    .Add((fi, fj, faces[fi].Centroid(), faces[fj].Centroid(), Vec3Math.Lerp(pool[a], pool[b], 0.5f), 0));
                            }
                        }
                    }
                }
            }
        }

        // Pass 2 (item 5): geometric collinear-overlap adjacency for OPEN edges — an
        // edge with no exact partner still connects to a fragment whose boundary runs
        // along the same line and overlaps it (the T-junction the fixer missed / the
        // partial edge left by over-splitting). Same membrane + detail gates apply.
        JoinOpenEdgesByOverlap(faces, pool, edgeMap, pairedInstances, adj, isDetail, isolated, OnSheet);

        int next = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < n; start++)
        {
            if (comp[start] != -1 || faces[start].IsPortal)
            {
                continue;
            }

            comp[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                foreach (int g in adj[f])
                {
                    if (comp[g] == -1)
                    {
                        comp[g] = next;
                        stack.Push(g);
                    }
                }
            }

            next++;
        }

        count = next;
        return comp;
    }

    /// <summary>Collinearity distance and minimum shared length for the geometric edge pass.</summary>
    private const float OverlapEps = 5e-3f;
    private const float MinOverlap = 5e-3f;
    private const float OverlapCell = 4f;

    /// <summary>
    /// Joins faces whose UNPAIRED edges are collinear and overlap another face's edge
    /// (open × ALL matching, spatial-hashed by edge AABB) — the stranded face's neighbour
    /// may already be exactly paired with a third face at the same boundary (a 3-way edge),
    /// so restricting to open×open would miss it. Overlap midpoints on a membrane sheet
    /// stay blocked, and the detail/isolation join gate applies — this only restores
    /// connectivity the exact-pair pass lost to fragmentation, never across doorways.
    /// </summary>
    private static void JoinOpenEdgesByOverlap(
        List<CsgFace> faces,
        IReadOnlyList<Vec3> pool,
        Dictionary<(int, int), List<(int Face, int A, int B)>> edgeMap,
        HashSet<(int Face, int A, int B)> pairedInstances,
        List<int>[] adj,
        bool[] isDetail,
        bool[] isolated,
        Func<Vec3, bool> onSheet)
    {
        // All edge instances, remembering which are open (unpaired by the exact pass).
        var all = new List<(int Face, Vec3 P0, Vec3 P1, bool Open)>();
        int openCount = 0;
        foreach (KeyValuePair<(int, int), List<(int Face, int A, int B)>> entry in edgeMap)
        {
            foreach ((int Face, int A, int B) share in entry.Value)
            {
                bool open = !pairedInstances.Contains(share);
                if (open)
                {
                    openCount++;
                }

                all.Add((share.Face, pool[share.A], pool[share.B], open));
            }
        }

        if (openCount == 0)
        {
            return;
        }

        // Spatial hash by edge AABB cells.
        var cells = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < all.Count; i++)
        {
            (Vec3 p0, Vec3 p1) = (all[i].P0, all[i].P1);
            (int x0, int y0, int z0) = OverlapCellOf(Vec3Math.Min(p0, p1));
            (int x1, int y1, int z1) = OverlapCellOf(Vec3Math.Max(p0, p1));
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!cells.TryGetValue((cx, cy, cz), out List<int>? bucket))
                        {
                            cells[(cx, cy, cz)] = bucket = new List<int>();
                        }

                        bucket.Add(i);
                    }
                }
            }
        }

        var tested = new HashSet<(int, int)>();
        foreach (List<int> bucket in cells.Values)
        {
            for (int bi = 0; bi < bucket.Count; bi++)
            {
                for (int bj = bi + 1; bj < bucket.Count; bj++)
                {
                    int i = bucket[bi], j = bucket[bj];
                    (int Face, Vec3 P0, Vec3 P1, bool Open) a = all[i];
                    (int Face, Vec3 P0, Vec3 P1, bool Open) b = all[j];
                    if ((!a.Open && !b.Open) || a.Face == b.Face || !tested.Add(i < j ? (i, j) : (j, i)))
                    {
                        continue;
                    }

                    if (!CanJoin(faces, isDetail, isolated, a.Face, b.Face))
                    {
                        continue;
                    }

                    if (EdgeOverlapMid(a.P0, a.P1, b.P0, b.P1) is not Vec3 mid || onSheet(mid))
                    {
                        continue;
                    }

                    adj[a.Face].Add(b.Face);
                    adj[b.Face].Add(a.Face);
                    if (CaptureJoins)
                    {
                        lock (CaptureLock)
                        {
                            (CapturedJoins ??= new List<(int, int, Vec3, Vec3, Vec3, int)>())
                                .Add((a.Face, b.Face, faces[a.Face].Centroid(), faces[b.Face].Centroid(), mid, 1));
                        }
                    }
                }
            }
        }
    }

    private static (int, int, int) OverlapCellOf(Vec3 p) => (
        (int)MathF.Floor(p.X / OverlapCell),
        (int)MathF.Floor(p.Y / OverlapCell),
        (int)MathF.Floor(p.Z / OverlapCell));

    /// <summary>
    /// If segments (a0,a1) and (b0,b1) are collinear (within <see cref="OverlapEps"/>) and share
    /// more than <see cref="MinOverlap"/> of length, returns the midpoint of the shared span.
    /// </summary>
    internal static Vec3? EdgeOverlapMid(Vec3 a0, Vec3 a1, Vec3 b0, Vec3 b1)
    {
        Vec3 dir = a1.Sub(a0);
        float len = dir.Length();
        if (len < MinOverlap)
        {
            return null;
        }

        dir = dir.Scale(1f / len);
        if (dir.Cross(b0.Sub(a0)).Length() > OverlapEps || dir.Cross(b1.Sub(a0)).Length() > OverlapEps)
        {
            return null; // not on a's line
        }

        float t0 = b0.Sub(a0).Dot(dir);
        float t1 = b1.Sub(a0).Dot(dir);
        float lo = MathF.Max(0f, MathF.Min(t0, t1));
        float hi = MathF.Min(len, MathF.Max(t0, t1));
        if (hi - lo <= MinOverlap)
        {
            return null; // touching endpoints only (a corner), not a shared boundary
        }

        return a0.Add(dir.Scale((lo + hi) * 0.5f));
    }

    private static bool CanJoin(List<CsgFace> faces, bool[] isDetail, bool[] isolated, int i, int j)
    {
        if (isDetail[i] != isDetail[j])
        {
            return false;
        }

        if (!isDetail[i])
        {
            return true;
        }

        if (isolated[i] || isolated[j])
        {
            return faces[i].SourceBrushUid == faces[j].SourceBrushUid;
        }

        return true;
    }

    /// <summary>
    /// RED's portal-side room classification by majority FACE-VOTE (flagship 24). For each membrane the two
    /// sides are the adjacent MAIN rooms whose faces vote onto OPPOSITE sides of the membrane plane —
    /// binary-faithful to RED's <c>FUN_004861d0</c> (walk a room's faces, tally front/on/back at the ±1e-4
    /// band via <c>FUN_0048a790</c>, FRONT iff <c>front &gt; back</c>). This replaces the per-fragment
    /// geometric probe, which resolves the LIQUID room on both sides of the near-horizontal water membrane
    /// (its pool walls reach into the membrane's y-band), starving the whole water-surface portal to slivers.
    /// A membrane's candidate rooms are the main rooms with a member face within a normal-band of its
    /// fragment footprint; the strongest-adjacency room on each side wins, and the geometric probe is the
    /// fallback for any side the vote leaves empty. All fragments of a membrane resolve to the SAME pair, so
    /// the record dedup unions them into ONE portal covering the full opening (RED's single 28.8×11 m water
    /// portal, not GED's edge slivers).
    /// </summary>
    private void ResolveMembraneSidesByFaceVote(
        List<CsgFace> faces, RoomBuildResult result,
        IReadOnlyList<PortalMembrane> membranes, RoomLocator mainLocator)
    {
        int roomCount = result.Rooms.Count;
        int n = faces.Count;

        // Per-face geometry for main-room member faces (portals + subrooms excluded from the vote).
        var fmin = new Vec3[n];
        var fmax = new Vec3[n];
        var fcen = new Vec3[n];
        var roomFaces = new List<int>[roomCount];
        for (int r = 0; r < roomCount; r++)
        {
            roomFaces[r] = new List<int>();
        }

        for (int i = 0; i < n; i++)
        {
            int r = result.FaceRoom[i];
            if (r < 0 || faces[i].IsPortal || result.Rooms[r].IsSubroom != 0)
            {
                continue;
            }

            var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            faces[i].GrowAabb(ref mn, ref mx);
            fmin[i] = mn;
            fmax[i] = mx;
            fcen[i] = faces[i].Centroid();
            roomFaces[r].Add(i);
        }

        // --- Per-membrane state: geometric probe + footprint, and whether the probe FAILED. ---
        var mem = new List<PortalMembrane>(membranes.Count);
        foreach (PortalMembrane m in membranes)
        {
            if (m.FrontFaces.Count > 0)
            {
                mem.Add(m);
            }
        }

        int mc = mem.Count;
        var probeF = new int[mc][];
        var probeB = new int[mc][];
        var needsVote = new bool[mc];
        var fpMin = new Vec3[mc];
        var fpMax = new Vec3[mc];
        var memArea = new float[mc];
        var memDropArea = new float[mc];
        var memRooms = new HashSet<int>[mc];
        for (int mi = 0; mi < mc; mi++)
        {
            PortalMembrane m = mem[mi];
            Vec3 nrm = m.Plane.Normal;

            // Flagship-20 geometric per-fragment probe: a normal doorway resolves each fragment to its two
            // flanking main rooms cleanly (those wins are kept verbatim). The probe FAILS on the near-
            // horizontal water membrane — the bulk of its fragments have BOTH sides land in the SAME room
            // (both fall inside the liquid room's y-extent) so they drop, and the few that survive are the
            // rim slivers. The vote engages when a MAJORITY of a membrane's area drops that way — the
            // signature of a sheet the ray cannot slice — not when a doorway merely has a few odd fragments.
            var f = new int[m.FrontFaces.Count];
            var b = new int[m.FrontFaces.Count];
            var bmn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var bmx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            var rooms = new HashSet<int>();
            float area = 0f, dropArea = 0f;
            for (int fi = 0; fi < m.FrontFaces.Count; fi++)
            {
                m.FrontFaces[fi].GrowAabb(ref bmn, ref bmx);
                float fa = m.FrontFaces[fi].Area();
                area += fa;
                Vec3 c = m.FrontFaces[fi].Centroid();
                f[fi] = ResolvePortalSide(result.Rooms, mainLocator, c.Add(nrm.Scale(0.05f)));
                b[fi] = ResolvePortalSide(result.Rooms, mainLocator, c.Sub(nrm.Scale(0.05f)));
                if (f[fi] >= 0)
                {
                    rooms.Add(f[fi]);
                }

                if (b[fi] >= 0)
                {
                    rooms.Add(b[fi]);
                }

                if (f[fi] >= 0 && b[fi] >= 0 && f[fi] == b[fi])
                {
                    dropArea += fa;
                }
            }

            probeF[mi] = f;
            probeB[mi] = b;
            fpMin[mi] = bmn;
            fpMax[mi] = bmx;
            memArea[mi] = area;
            memDropArea[mi] = dropArea;
            memRooms[mi] = rooms;

            // Majority of the membrane's area could not be sliced by the ray (both sides one room).
            bool majorityDrop = area > 0f && dropArea > 0.5f * area;
            needsVote[mi] = majorityDrop || ForceAlwaysVote;
        }

        // --- Group COPLANAR + footprint-adjacent membranes (union-find). RED emits ONE portal per portal
        // brush; GED can tile a wide opening (the whole water surface) with several coplanar membranes that
        // must resolve to the SAME room pair. When ANY member of a coplanar group needs the vote, the whole
        // group is voted together so the surface unifies into one 28.8×11 m record instead of splitting into
        // the room1 edge sliver + the room2 body. Vertical door membranes (different normal) never join a
        // horizontal water group; distinct openings on one plane stay apart unless their footprints touch. ---
        var parent = new int[mc];
        for (int i = 0; i < mc; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        const float GroupSlack = 0.5f;
        for (int i = 0; i < mc; i++)
        {
            for (int j = i + 1; j < mc; j++)
            {
                CsgPlane pi = mem[i].Plane, pj = mem[j].Plane;
                float align = pi.Normal.Dot(pj.Normal);
                if (MathF.Abs(align) < 0.999f)
                {
                    continue; // not coplanar (different orientation)
                }

                // Parallel-offset tolerance: a near-HORIZONTAL opening (a water surface) can be tiled by
                // membranes at slightly different heights — its rim ledge sits a few dm above the surface —
                // so horizontal planes group across a wider vertical gap than vertical doorways, whose two
                // leaves are always exactly coplanar. This only ever pulls a probe-clean rim membrane into a
                // group whose water-surface member already FAILED the probe (drop/multipair); a group with no
                // failing member stays on the probe untouched, so the wider band cannot disturb clean doorways.
                bool horizontal = MathF.Abs(pi.Normal.Y) > 0.7f && MathF.Abs(pj.Normal.Y) > 0.7f;
                float offTol = horizontal ? 0.75f : 0.05f;
                float offDiff = MathF.Abs(pi.Offset - (align >= 0 ? pj.Offset : -pj.Offset));
                if (offDiff > offTol)
                {
                    continue; // parallel but too far apart to be the same opening
                }

                bool footprintAdjacent =
                    fpMin[i].X - GroupSlack <= fpMax[j].X && fpMax[i].X + GroupSlack >= fpMin[j].X &&
                    fpMin[i].Y - GroupSlack <= fpMax[j].Y && fpMax[i].Y + GroupSlack >= fpMin[j].Y &&
                    fpMin[i].Z - GroupSlack <= fpMax[j].Z && fpMax[i].Z + GroupSlack >= fpMin[j].Z;

                // A near-horizontal water surface can be split by CSG into disjoint pieces (a +X body and a
                // separated -X rim patch) with a dead gap between them where the whole membrane dropped — so
                // footprint-adjacency alone leaves them ungrouped. Two coplanar horizontal membranes that
                // SHARE a bordering room (the liquid room) are pieces of the same surface: group them so they
                // vote together and their records dedup into RED's single full-width window. Vertical doorways
                // keep the strict footprint-adjacency test (they never share this signature spuriously).
                bool shareRoom = horizontal && SharesRoom(memRooms[i], memRooms[j]);
                if (!footprintAdjacent && !shareRoom)
                {
                    continue;
                }

                parent[Find(i)] = Find(j);
            }
        }

        // --- Resolve each group. ---
        var roomNear = new int[roomCount];
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < mc; i++)
        {
            int g = Find(i);
            if (!groups.TryGetValue(g, out List<int>? list))
            {
                groups[g] = list = new List<int>();
            }

            list.Add(i);
        }

        foreach (List<int> group in groups.Values)
        {
            bool vote = false;
            foreach (int mi in group)
            {
                vote |= needsVote[mi];
            }

            int groupId = group[0];
            if (!vote)
            {
                // Every member's probe was clean and consistent — keep the per-fragment assignment verbatim.
                foreach (int mi in group)
                {
                    PortalMembrane m = mem[mi];
                    for (int fi = 0; fi < m.FrontFaces.Count; fi++)
                    {
                        if (probeF[mi][fi] >= 0)
                        {
                            m.FrontFaces[fi].RoomIndex = probeF[mi][fi];
                        }

                        if (probeB[mi][fi] >= 0 && fi < m.BackFaces.Count)
                        {
                            m.BackFaces[fi].RoomIndex = probeB[mi][fi];
                        }
                    }

                    CaptureMembrane(m, probeF[mi][0], probeB[mi][0]);
                    CaptureDetail(mem[mi], fpMin[mi], fpMax[mi], memArea[mi], memDropArea[mi], groupId, false, probeF[mi][0], probeB[mi][0]);
                }

                continue;
            }

            // Face-vote the whole group to ONE pair (RED's FUN_004861d0 room classification). Candidate rooms
            // are the main rooms with a member face within a normal-band of the group's combined footprint;
            // the strongest-adjacency room on each side of the plane wins (tie: larger face-vote margin).
            CsgPlane plane = mem[group[0]].Plane;
            Vec3 pn = plane.Normal;
            var gmn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var gmx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            foreach (int mi in group)
            {
                gmn = Vec3Math.Min(gmn, fpMin[mi]);
                gmx = Vec3Math.Max(gmx, fpMax[mi]);
            }

            const float LatSlack = 0.1f;
            const float NormalBand = 1.0f;
            var expand = new Vec3(
                LatSlack + (NormalBand * MathF.Abs(pn.X)),
                LatSlack + (NormalBand * MathF.Abs(pn.Y)),
                LatSlack + (NormalBand * MathF.Abs(pn.Z)));
            Vec3 bmn = gmn.Sub(expand);
            Vec3 bmx = gmx.Add(expand);

            Array.Clear(roomNear, 0, roomCount);
            for (int i = 0; i < n; i++)
            {
                int r = result.FaceRoom[i];
                if (r < 0 || faces[i].IsPortal || result.Rooms[r].IsSubroom != 0)
                {
                    continue;
                }

                if (fmin[i].X <= bmx.X && fmax[i].X >= bmn.X &&
                    fmin[i].Y <= bmx.Y && fmax[i].Y >= bmn.Y &&
                    fmin[i].Z <= bmx.Z && fmax[i].Z >= bmn.Z)
                {
                    roomNear[r]++;
                }
            }

            int frontRoom = -1, backRoom = -1;
            int frontScore = 0, backScore = 0, frontMargin = int.MinValue, backMargin = int.MinValue;
            for (int r = 0; r < roomCount; r++)
            {
                if (roomNear[r] == 0)
                {
                    continue;
                }

                int side = RoomSide(roomFaces[r], fcen, plane, out int margin);
                if (side > 0)
                {
                    if (roomNear[r] > frontScore || (roomNear[r] == frontScore && margin > frontMargin))
                    {
                        frontScore = roomNear[r];
                        frontMargin = margin;
                        frontRoom = r;
                    }
                }
                else if (roomNear[r] > backScore || (roomNear[r] == backScore && margin > backMargin))
                {
                    backScore = roomNear[r];
                    backMargin = margin;
                    backRoom = r;
                }
            }

            // Geometric fallback for any side the vote left empty (keep whatever the probe found there).
            if (frontRoom < 0 || backRoom < 0)
            {
                Vec3 c = mem[group[0]].FrontFaces[0].Centroid();
                if (frontRoom < 0)
                {
                    frontRoom = ResolvePortalSide(result.Rooms, mainLocator, c.Add(pn.Scale(0.05f)));
                }

                if (backRoom < 0)
                {
                    backRoom = ResolvePortalSide(result.Rooms, mainLocator, c.Sub(pn.Scale(0.05f)));
                }
            }

            // Assign every fragment of every group member to the group pair. A member whose own normal is
            // anti-parallel to the group plane has its front/back sides swapped so it still faces its room.
            foreach (int mi in group)
            {
                PortalMembrane m = mem[mi];
                bool aligned = m.Plane.Normal.Dot(pn) >= 0;
                int fRoom = aligned ? frontRoom : backRoom;
                int bRoom = aligned ? backRoom : frontRoom;
                for (int fi = 0; fi < m.FrontFaces.Count; fi++)
                {
                    if (fRoom >= 0)
                    {
                        m.FrontFaces[fi].RoomIndex = fRoom;
                    }

                    if (bRoom >= 0 && fi < m.BackFaces.Count)
                    {
                        m.BackFaces[fi].RoomIndex = bRoom;
                    }
                }

                CaptureMembrane(m, fRoom, bRoom);
                CaptureDetail(mem[mi], fpMin[mi], fpMax[mi], memArea[mi], memDropArea[mi], groupId, true, fRoom, bRoom);
            }
        }
    }

    private static bool SharesRoom(HashSet<int> a, HashSet<int> b)
    {
        HashSet<int> small = a.Count <= b.Count ? a : b;
        HashSet<int> large = a.Count <= b.Count ? b : a;
        foreach (int r in small)
        {
            if (large.Contains(r))
            {
                return true;
            }
        }

        return false;
    }

    private static void CaptureDetail(PortalMembrane m, Vec3 fpMin, Vec3 fpMax, float area, float dropArea, int group, bool voted, int front, int back)
    {
        if (!CaptureJoins)
        {
            return;
        }

        lock (CaptureLock)
        {
            (CapturedMembraneDetail ??= new List<(int, Vec3, float, Vec3, Vec3, int, float, float, bool, int, int, int)>())
                .Add((m.BrushUid, m.Plane.Normal, m.Plane.Offset, fpMin, fpMax, m.FrontFaces.Count, area, dropArea, voted, group, front, back));
        }
    }

    private void CaptureMembrane(PortalMembrane m, int frontRoom, int backRoom)
    {
        if (!CaptureJoins)
        {
            return;
        }

        List<Vec3>? opening = CaptureOpening(m);
        lock (CaptureLock)
        {
            CapturedMembranes?.Add((m.BrushUid, m.FrontFaces[0].Centroid(), frontRoom, backRoom, opening));
        }
    }

    /// <summary>
    /// Legacy per-fragment geometric side resolution (vertical-ray + smallest-containing-AABB). Kept as the
    /// <c>PortalFaceVote = false</c> path for A/B measurement; superseded by the face-vote above.
    /// </summary>
    private void ResolveMembraneSidesByProbe(
        List<CsgFace> faces, RoomBuildResult result,
        IReadOnlyList<PortalMembrane> membranes, RoomLocator mainLocator)
    {
        foreach (PortalMembrane m in membranes)
        {
            if (m.FrontFaces.Count == 0)
            {
                continue;
            }

            int probeFront = -1, probeBack = -1;
            for (int fi = 0; fi < m.FrontFaces.Count; fi++)
            {
                Vec3 c = m.FrontFaces[fi].Centroid();
                int frontRoom = ResolvePortalSide(result.Rooms, mainLocator, c.Add(m.Plane.Normal.Scale(0.05f)));
                int backRoom = ResolvePortalSide(result.Rooms, mainLocator, c.Sub(m.Plane.Normal.Scale(0.05f)));
                if (fi == 0)
                {
                    probeFront = frontRoom;
                    probeBack = backRoom;
                }

                if (frontRoom >= 0)
                {
                    m.FrontFaces[fi].RoomIndex = frontRoom;
                }

                if (backRoom >= 0 && fi < m.BackFaces.Count)
                {
                    m.BackFaces[fi].RoomIndex = backRoom;
                }
            }

            if (CaptureJoins)
            {
                List<Vec3>? opening = CaptureOpening(m);
                lock (CaptureLock)
                {
                    CapturedMembranes?.Add((m.BrushUid, m.FrontFaces[0].Centroid(), probeFront, probeBack, opening));
                }
            }
        }
    }

    private static List<Vec3>? CaptureOpening(PortalMembrane m)
    {
        if (m.Opening is null)
        {
            return null;
        }

        var opening = new List<Vec3>();
        foreach (CsgVertex v in m.Opening.Vertices)
        {
            opening.Add(v.Position);
        }

        return opening;
    }

    /// <summary>
    /// RED's <c>FUN_004861d0</c> room-vs-plane classification: tally the room's member faces front/on/back of
    /// <paramref name="p"/> at the ±1e-4 band (<c>FUN_0048a790</c>) using each face's centroid, and return
    /// +1 (FRONT) iff <c>front &gt; back</c>, else −1 (BACK). When every face is coplanar RED's area tiebreak
    /// (<c>_DAT_0055470c = 0</c>) defaults to FRONT. <paramref name="margin"/> = front − back (adjacency tiebreak).
    /// </summary>
    private static int RoomSide(List<int> memberFaces, Vec3[] centroid, CsgPlane p, out int margin)
    {
        int front = 0, back = 0;
        foreach (int i in memberFaces)
        {
            float d = p.Distance(centroid[i]);
            if (d > CsgPlane.OnPlaneEpsilon)
            {
                front++;
            }
            else if (d < -CsgPlane.OnPlaneEpsilon)
            {
                back++;
            }
        }

        margin = front - back;
        if (front == 0 && back == 0)
        {
            return 1; // all faces coplanar: RED's area tiebreak defaults to the front side
        }

        return front > back ? 1 : -1;
    }

    /// <summary>
    /// Resolves the MAIN room on one side of a portal membrane at <paramref name="p"/>: the
    /// smallest-volume main room whose member-face AABB contains the point (RF's runtime
    /// point-in-room rule — keeps the portal graph consistent with the game's camera-room lookup),
    /// falling back to a main-only vertical ray when the point lies outside every room extent.
    /// </summary>
    private static int ResolvePortalSide(List<Room> rooms, RoomLocator mainLocator, Vec3 p)
    {
        // The vertical ray is authoritative for a same-level doorway (it finds THIS side's floor even
        // where AABBs overlap), but only when its room actually CONTAINS the probe: if the ray had to
        // reach a distant surface (a nested lower room's ceiling, or the single shared column of a
        // horizontal ceiling/floor opening) the returned room does not contain the point, and RF's
        // smallest-containing-room rule is the correct side instead.
        int vr = mainLocator.Locate(p);
        if (vr >= 0 && rooms[vr].IsSubroom == 0 && AabbContains(rooms[vr].Aabb, p, 0.01f))
        {
            return vr;
        }

        int sc = SmallestContainingMainRoom(rooms, p);
        if (sc >= 0)
        {
            return sc;
        }

        return vr >= 0 && rooms[vr].IsSubroom == 0 ? vr : -1;
    }

    private static bool AabbContains(Aabb a, Vec3 p, float tol) =>
        p.X >= a.P1.X - tol && p.X <= a.P2.X + tol &&
        p.Y >= a.P1.Y - tol && p.Y <= a.P2.Y + tol &&
        p.Z >= a.P1.Z - tol && p.Z <= a.P2.Z + tol;

    /// <summary>Smallest-volume MAIN (non-subroom) room whose member-face AABB contains the point, or -1.
    /// The tolerance is deliberately below the 0.05 probe offset so a probe just past a room's face
    /// plane (e.g. above a ceiling opening) is NOT counted as inside that room.</summary>
    private static int SmallestContainingMainRoom(List<Room> rooms, Vec3 p)
    {
        const float Tol = 0.01f;
        int best = -1;
        float bestVol = float.MaxValue;
        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].IsSubroom != 0)
            {
                continue;
            }

            Aabb a = rooms[i].Aabb;
            if (p.X < a.P1.X - Tol || p.X > a.P2.X + Tol ||
                p.Y < a.P1.Y - Tol || p.Y > a.P2.Y + Tol ||
                p.Z < a.P1.Z - Tol || p.Z > a.P2.Z + Tol)
            {
                continue;
            }

            Vec3 d = a.P2.Sub(a.P1);
            float vol = MathF.Abs(d.X * d.Y * d.Z);
            if (vol < bestVol)
            {
                bestVol = vol;
                best = i;
            }
        }

        return best;
    }

    private static void GrowRoom(Vec3[] min, Vec3[] max, bool[] seeded, int room, CsgFace f)
    {
        if (!seeded[room])
        {
            min[room] = f.Vertices[0].Position;
            max[room] = f.Vertices[0].Position;
            seeded[room] = true;
        }

        Vec3 mn = min[room], mx = max[room];
        f.GrowAabb(ref mn, ref mx);
        min[room] = mn;
        max[room] = mx;
    }

    private void AssignRoomEffects(RoomBuildResult result, IReadOnlyList<RoomEffect> effects)
    {
        var claimed = new bool[result.Rooms.Count];
        foreach (RoomEffect effect in effects)
        {
            int room = result.Locator?.Locate(effect.Header.Position) ?? -1;
            if (room < 0 || claimed[room] || result.Rooms[room].IsSubroom != 0)
            {
                room = FindContainingRoom(result.Rooms, effect.Header.Position, claimed);
            }

            if (room < 0)
            {
                continue;
            }

            claimed[room] = true;
            Room r = result.Rooms[room];
            r.Id = effect.Header.Uid;
            r.IsCold = effect.RoomIsCold;
            r.IsOutside = effect.RoomIsOutside;
            r.IsAirlock = effect.RoomIsAirLock;

            switch (effect.EffectType)
            {
                case RoomEffectsSection.EffectSkyRoom:
                    r.IsSkyroom = 1;
                    break;
                case RoomEffectsSection.EffectLiquidRoom:
                    ApplyLiquid(r, effect);
                    break;
                case RoomEffectsSection.EffectAmbientLight:
                    r.HasAmbientLight = 1;
                    r.AmbientColor = effect.AmbientLightColor;
                    break;
            }
        }

        for (int i = 0; i < result.Rooms.Count; i++)
        {
            if (result.Rooms[i].Id == 0)
            {
                result.Rooms[i].Id = GeometryCompiler.SyntheticRoomId(i);
            }
        }
    }

    private static void ApplyLiquid(Room r, RoomEffect effect)
    {
        RoomEffectLiquidProperties src = effect.LiquidProperties!;
        r.IsLiquidRoom = 1;
        r.LiquidProperties = new RoomLiquidProperties
        {
            Depth = src.Depth,
            Color = src.LiquidColor,
            SurfaceTexture = src.SurfaceTexture,
            Visibility = src.Visibility,
            LiquidType = src.LiquidType,
            LiquidAlpha = 255,
            ContainsPlankton = src.ContainsPlankton,
            TexturePixelsPerMeterU = src.TexturePixelsPerMeterU,
            TexturePixelsPerMeterV = src.TexturePixelsPerMeterV,
            TextureAngleRadians = src.TextureAngleDegrees * (MathF.PI / 180f),
            Waveform = src.Waveform - 1,
            TextureScrollRate = src.TextureScrollRate,
        };
    }

    private static int FindContainingRoom(List<Room> rooms, Vec3 p, bool[] claimed)
    {
        int best = -1;
        float bestVol = float.MaxValue;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantMain = pass == 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                Room r = rooms[i];
                if (claimed[i] || (wantMain && r.IsSubroom != 0) || (!wantMain && r.IsSubroom == 0) || !Contains(r.Aabb, p))
                {
                    continue;
                }

                Vec3 d = r.Aabb.P2.Sub(r.Aabb.P1);
                float vol = MathF.Abs(d.X * d.Y * d.Z);
                if (vol < bestVol)
                {
                    bestVol = vol;
                    best = i;
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return best;
    }

    private static bool Contains(Aabb box, Vec3 p) =>
        p.X >= box.P1.X - 0.05f && p.X <= box.P2.X + 0.05f &&
        p.Y >= box.P1.Y - 0.05f && p.Y <= box.P2.Y + 0.05f &&
        p.Z >= box.P1.Z - 0.05f && p.Z <= box.P2.Z + 0.05f;

    /// <summary>Probe offset (m) off a detail face to find the MAIN room its surface borders.
    /// Matches the 0.05 probe offset the portal-side resolution uses.</summary>
    private const float DetailProbeOffset = 0.05f;

    /// <summary>Max vertical-ray distance (m) from a detail's outward face probe to the main-room surface
    /// it rests against. 10 m clears a normal room's wall-mounted detail (floor within a storey or two)
    /// yet rejects the tens-of-metres reach to an outer/sky shell that RED never lists as a parent.
    /// Measured knee on dmabrupt: child-entries 161 vs RED 164, multi-parent 25 vs RED 20, under-attach 7
    /// (baseline single-parent left 32 details missing a RED parent); unbounded would recover 1 more at
    /// the cost of 6 spurious sky-room attaches.</summary>
    private const float DetailAttachMaxDist = 10f;

    private void BuildSubroomLists(List<CsgFace> faces, RoomBuildResult result, RoomLocator mainLocator)
    {
        // RED writes a DENSE subroom-list array — one entry per room, indexed by room (idxEqPos
        // verified: dmabrupt 170 lists for 170 rooms, dm04 24 for 24), most empty. RF resolves each
        // entry's children by its explicit RoomIndex, so a sparse array also loads, but matching RED's
        // dense per-room layout keeps the file structurally identical and is safe for any consumer that
        // assumes room i's subrooms live at subroom_list[i].
        //
        // MULTI-PARENT ATTACH (RED FUN_00485990 detail loop, flagship 26). RF renders a detail room's
        // faces when ANY of its PARENT (container) rooms is in the portal-visible set. RED walks each
        // detail room's FACES and attaches the room to every MAIN room its faces border (FUN_004850c0
        // gates "is detail", FUN_0043cc90 gates a candidate to "is main"); a detail bordering N main
        // rooms lists under all N. GED's old rule attached each detail to the SINGLE smallest-volume
        // main room CONTAINING its centre — which both (a) misses the extra parents (RED had 20 rooms
        // with >1 parent on dmabrupt; GED 0) and (b) picks the WRONG single parent for a thin panel at
        // a room boundary whose centre falls in a neighbouring room's AABB. Both make the detail vanish
        // at camera angles where a non-parent room is the one seen through the portal chain — Goober's
        // angle-dependent brush drop. Ground truth (RED's own subroom lists) confirms every RED parent
        // is a main room whose AABB OVERLAPS the detail (100% recall) but only ~48% of overlapping rooms
        // are parents — the face-border probe is that ~48% refinement.
        int count = result.Rooms.Count;
        var lists = new SubroomList[count];
        for (int i = 0; i < count; i++)
        {
            lists[i] = new SubroomList { RoomIndex = i };
        }

        // Group each detail subroom's member (non-portal) faces.
        var subroomFaces = new Dictionary<int, List<int>>();
        for (int i = 0; i < faces.Count; i++)
        {
            int room = result.FaceRoom[i];
            if (room < 0 || faces[i].IsPortal || result.Rooms[room].IsSubroom == 0)
            {
                continue;
            }

            if (!subroomFaces.TryGetValue(room, out var l))
            {
                l = new List<int>();
                subroomFaces[room] = l;
            }

            l.Add(i);
        }

        for (int s = 0; s < count; s++)
        {
            if (result.Rooms[s].IsSubroom == 0)
            {
                continue;
            }

            var parents = FindParentRooms(faces, result.Rooms, mainLocator, subroomFaces.GetValueOrDefault(s), s);
            foreach (int parent in parents)
            {
                lists[parent].SubroomIndices.Add(s);
            }
        }

        result.SubroomLists.AddRange(lists);
    }

    /// <summary>
    /// RED's detail-room parent set: every MAIN room whose open space a face of the detail room rests
    /// against. Probes just off the OUTWARD side of each detail face and takes the main room the vertical
    /// ray lands on within <see cref="DetailAttachMaxDist"/> — a wall-flush face's outward probe finds the
    /// surrounding room, a detail straddling a doorway resolves both bordering rooms, and a plate's probe
    /// cannot ray through the slab to bind the room beyond. Falls back to the single smallest-volume main
    /// room containing the detail's centre when no face borders any room (a fully nested detail), so every
    /// subroom still attaches somewhere the way RED's containment guarantees.
    /// </summary>
    private static List<int> FindParentRooms(
        List<CsgFace> faces, List<Room> rooms, RoomLocator mainLocator, List<int>? memberFaces, int subIndex)
    {
        var parents = new List<int>();
        if (memberFaces is not null)
        {
            foreach (int fi in memberFaces)
            {
                CsgFace f = faces[fi];
                Vec3 c = f.Centroid();
                Vec3 nrm = f.Plane.Normal;
                // Which MAIN room's open cell does this detail face border? The vertical-ray locator
                // (main-room floors/ceilings only) answers "which room is a point standing in", which room
                // AABBs cannot when they overlap (a big room's member-face bbox swallows a detail sitting in
                // a smaller neighbour). Probe just off the OUTWARD (open) side of each face; the bounded ray
                // keeps a probe whose column has only a distant outer/sky shell from binding that shell, and
                // taking only the outward side stops a floor/ceiling plate from binding the room on the far
                // side of the slab it caps.
                AddParent(parents, mainLocator.Locate(c.Add(nrm.Scale(DetailProbeOffset)), DetailAttachMaxDist));
            }
        }

        if (parents.Count == 0)
        {
            AddParent(parents, FindParentRoom(rooms, subIndex));
        }

        return parents;
    }

    private static void AddParent(List<int> parents, int room)
    {
        if (room >= 0 && !parents.Contains(room))
        {
            parents.Add(room);
        }
    }

    /// <summary>Single smallest-volume MAIN room whose AABB contains the detail's centre (legacy fallback).</summary>
    private static int FindParentRoom(List<Room> rooms, int subIndex)
    {
        Aabb sub = rooms[subIndex].Aabb;
        var center = new Vec3(
            (sub.P1.X + sub.P2.X) * 0.5f,
            (sub.P1.Y + sub.P2.Y) * 0.5f,
            (sub.P1.Z + sub.P2.Z) * 0.5f);
        int best = -1;
        float bestVol = float.MaxValue;
        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].IsSubroom != 0 || !Contains(rooms[i].Aabb, center))
            {
                continue;
            }

            Vec3 d = rooms[i].Aabb.P2.Sub(rooms[i].Aabb.P1);
            float vol = MathF.Abs(d.X * d.Y * d.Z);
            if (vol < bestVol)
            {
                bestVol = vol;
                best = i;
            }
        }

        return best;
    }

    /// <summary>A portal membrane's doorway sheet: plane band + near-polygon test.</summary>
    internal sealed class MembraneSheet
    {
        private const float PlaneBand = 0.01f;
        private const float PolygonSlack = 0.05f;

        private readonly CsgPlane _plane;
        private readonly List<CsgFace> _polygons;
        private readonly Vec3 _min;
        private readonly Vec3 _max;
        private readonly float _slack;

        public MembraneSheet(PortalMembrane m)
        {
            _plane = m.Plane;
            _polygons = m.FrontFaces;

            // Both paths block with the same fragment-anchored slack; the extraction-path divider differs
            // only in the CHOP test (PortalBuilder.StraddleOverlapsFragment — the FacesCross overlap
            // semantic with the clip against the convex fragment, robust to degenerate world-face loops).
            // A wider block (0.25) or the full authored slab were measured to over-divide (dm04 25/11,
            // dmabrupt rooms 161→229 — the gap-ribbon pathology the bounded sheet gate exists to avoid).
            _slack = PolygonSlack;
            var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            foreach (CsgFace f in _polygons)
            {
                f.GrowAabb(ref mn, ref mx);
            }

            _min = mn;
            _max = mx;
        }

        /// <summary>True when the point lies on the membrane sheet (within the polygon, on the plane).</summary>
        public bool Contains(Vec3 p)
        {
            if (MathF.Abs(_plane.Distance(p)) > PlaneBand)
            {
                return false;
            }

            if (p.X < _min.X - _slack || p.X > _max.X + _slack ||
                p.Y < _min.Y - _slack || p.Y > _max.Y + _slack ||
                p.Z < _min.Z - _slack || p.Z > _max.Z + _slack)
            {
                return false;
            }

            foreach (CsgFace poly in _polygons)
            {
                if (NearPolygon(poly, p, _slack))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NearPolygon(CsgFace poly, Vec3 p, float slack)
        {
            // Project onto the dominant axis plane and test containment or
            // proximity (within slack) to any boundary edge.
            Vec3 n = poly.Plane.Normal;
            float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
            int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);

            float pu = Comp(p, drop, true), pv = Comp(p, drop, false);
            bool inside = false;
            int count = poly.Vertices.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                float ui = Comp(poly.Vertices[i].Position, drop, true);
                float vi = Comp(poly.Vertices[i].Position, drop, false);
                float uj = Comp(poly.Vertices[j].Position, drop, true);
                float vj = Comp(poly.Vertices[j].Position, drop, false);
                if (((vi > pv) != (vj > pv)) && (pu < ((uj - ui) * (pv - vi) / (vj - vi)) + ui))
                {
                    inside = !inside;
                }

                // Proximity to the edge segment (2D).
                float du = uj - ui, dv = vj - vi;
                float lenSq = (du * du) + (dv * dv);
                if (lenSq > 1e-12f)
                {
                    float t = Math.Clamp((((pu - ui) * du) + ((pv - vi) * dv)) / lenSq, 0f, 1f);
                    float qu = ui + (t * du), qv = vi + (t * dv);
                    float distSq = ((pu - qu) * (pu - qu)) + ((pv - qv) * (pv - qv));
                    if (distSq <= slack * slack)
                    {
                        return true;
                    }
                }
            }

            return inside;
        }

        private static float Comp(Vec3 p, int drop, bool first) => drop switch
        {
            0 => first ? p.Y : p.Z,
            1 => first ? p.X : p.Z,
            _ => first ? p.X : p.Y,
        };
    }
}
