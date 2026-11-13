using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// The eleven counters Alpine RED's build repair prints to the editor log after every build
/// (<c>editor_patch/build_repair.cpp:1507-1519</c>, Alpine master 8db7d90b), in that order.
/// All zero (and <see cref="Ran"/> false) when repair is off.
/// </summary>
public sealed class BuildRepairStats
{
    /// <summary>True when the hole repair ran on this build (a sealed build with <see cref="CompileOptions.AlpineRepair"/> on).</summary>
    public bool Ran { get; internal set; }

    /// <summary>Holes (groups of open edges sharing a vertex, per room) right before the hole repair.</summary>
    public int HolesAfterRooms { get; internal set; }

    public int PortalPlanesUnified { get; internal set; }

    public int PortalCopiesMoved { get; internal set; }

    public int SplitRoomsJoined { get; internal set; }

    public int VerticesWelded { get; internal set; }

    public int EdgesSplit { get; internal set; }

    public int CollapsedFacesRemoved { get; internal set; }

    public int PortalStripsRemoved { get; internal set; }

    public int PortalGapsFilled { get; internal set; }

    public int WallGapsFilled { get; internal set; }

    public int UnplacedPortalFacesRemoved { get; internal set; }

    /// <summary>The counters as (Alpine log label, value) pairs, in Alpine's log order.</summary>
    public IReadOnlyList<(string Name, int Value)> Counters() => new[]
    {
        ("Holes after building rooms", HolesAfterRooms),
        ("Portal planes unified", PortalPlanesUnified),
        ("Portal copies moved", PortalCopiesMoved),
        ("Split rooms joined", SplitRoomsJoined),
        ("Vertices welded", VerticesWelded),
        ("Edges split", EdgesSplit),
        ("Collapsed faces removed", CollapsedFacesRemoved),
        ("Portal strips removed", PortalStripsRemoved),
        ("Portal gaps filled", PortalGapsFilled),
        ("Wall gaps filled", WallGapsFilled),
        ("Unplaced portal faces removed", UnplacedPortalFacesRemoved),
    };
}

/// <summary>
/// Alpine RED's build repair (passes A, C, D and E of <c>docs/internal/ALPINE-COMPAT-SPEC.md</c> §3.1),
/// reimplemented from the behaviour of <c>editor_patch/build_repair.cpp</c> (Alpine master 8db7d90b; MPL
/// source, not copied — line citations are to that file). Constants and all geometry math are double, as
/// Alpine's are (<c>:27-39</c>). Pass B (the coplanar seam tolerance) lives in <see cref="CsgSolver"/>.
/// <para>Hook points, mirroring Alpine's: A = <see cref="BeforeRooms"/> (before the room builder,
/// <c>:1613-1626</c>); C = <see cref="AfterFlood"/> (inside the room builder once every face, portal copies
/// included, has its room — <c>:1436-1464</c>); D = <see cref="CountUnplacedPortals"/> (portal linking's
/// deletions, <c>:1628-1661</c>); E = <see cref="RepairHoles"/> (after the t-joint pass, right before the hole
/// count, <c>:1466-1523</c>).</para>
/// <para>Never touches detail faces or detail rooms; movers are not part of the static solid. Filled wall gaps
/// carry <see cref="CsgFace.RepairFill"/> so the surface stage gives them no lightmap (Alpine's surface −1).
/// Two Glacier-specific notes: (1) Glacier's liquid surface is single-sided where RED's is double-sided and so
/// closed liquid-to-liquid, so liquid faces never act as open-edge sources here (they never do in RED);
/// (2) Glacier does not emulate stock RED's &gt;16-corner face split (<c>FUN_004a6950</c>, called at the end of
/// the stock t-joint pass), so the repair's re-run of it (<c>:1494-1497</c>) has no counterpart.</para>
/// </summary>
public sealed class BuildRepair
{
    // build_repair.cpp:27-36
    private const double RepairTolerance = 1e-3;
    private const double RedEpsilon = 1e-4;
    private const double ParallelCosine = 0.999;
    private const double MaxFillArea = 1.0;
    private const int MaxFaceCorners = 16;
    private const int MaxRepairRounds = 8;

    private readonly BuildRepairStats _stats;

    /// <summary>Repair passes, for the diagnostic skip mask (<see cref="CompileOptions.RepairSkip"/>).</summary>
    [Flags]
    internal enum Pass
    {
        None = 0,
        UnifyPlanes = 1,
        Twins = 2,
        MergeRooms = 4,
        LineFaces = 8,
        PortalStrips = 16,
        Weld = 32,
        Split = 64,
        PortalFill = 128,
        WallFill = 256,
    }

    /// <summary>DIAGNOSTIC: passes to skip.</summary>
    internal Pass Skip { get; init; }

    private bool On(Pass p) => (Skip & p) == 0;

    /// <summary>Portal faces as the room builder's flood left them (<c>g_flood_portals</c>, :311).</summary>
    private readonly List<CsgFace> _floodPortals = new();

    /// <summary>Every portal of the build, linked or not, so the wall fill leaves doorways open (:313).</summary>
    private readonly List<PortalBox> _portalBoxes = new();

    internal BuildRepair(BuildRepairStats stats)
    {
        _stats = stats;
    }

    // ───────────────────────────── hooks ─────────────────────────────

    /// <summary>Pass A (:1613-1626): unify coplanar portal pieces' planes, judged by RED's own side test.</summary>
    internal void BeforeRooms(List<CsgFace> faces, List<int[]> poolIndices, IReadOnlyList<Vec3> pool)
    {
        if (!On(Pass.UnifyPlanes))
        {
            return;
        }

        var mesh = Mesh.Over(faces, poolIndices, pool, roomOf: null);
        _stats.PortalPlanesUnified += UnifyPortalPlanes(mesh, beforeRooms: true);
    }

    /// <summary>
    /// Pass C (:1436-1464): record the portal faces, then move misplaced portal copies to the room in front and
    /// join rooms the flood split. Updates <see cref="CsgFace.RoomIndex"/>, <see cref="RoomBuildResult.FaceRoom"/>
    /// and the room list (an absorbed room is left empty as a zero-box detail room, its index preserved).
    /// </summary>
    /// <returns>True when any face changed room (the caller's room lookups are then stale).</returns>
    internal bool AfterFlood(List<CsgFace> faces, List<int[]> poolIndices, IReadOnlyList<Vec3> pool, RoomBuildResult rooms)
    {
        var mesh = Mesh.Over(faces, poolIndices, pool, rooms.FaceRoom);
        mesh.Rooms = rooms.Rooms;
        _floodPortals.Clear();
        foreach (int f in mesh.PortalFaces())
        {
            _floodPortals.Add(faces[f]);
        }

        int moved = On(Pass.Twins) ? FixPortalTwins(mesh) : 0;
        int joined = On(Pass.MergeRooms) ? MergeFragmentRooms(mesh) : 0;
        _stats.PortalCopiesMoved += moved;
        _stats.SplitRoomsJoined += joined;

        for (int i = 0; i < faces.Count; i++)
        {
            faces[i].RoomIndex = mesh.Faces[i].Room;
            rooms.FaceRoom[i] = mesh.Faces[i].Room;
        }

        return moved + joined > 0;
    }

    /// <summary>
    /// Pass D (:1628-1661): after portal linking (Glacier's untagged-portal compaction), count the flood's portal
    /// faces it removed, and keep every flood portal's plane and box for the wall fill's doorway test.
    /// </summary>
    internal void CountUnplacedPortals(IReadOnlyList<CsgFace> survivors)
    {
        var left = new HashSet<CsgFace>(survivors, ReferenceEqualityComparer.Instance);
        foreach (CsgFace face in _floodPortals)
        {
            _portalBoxes.Add(PortalBox.Of(face));
            if (!left.Contains(face))
            {
                _stats.UnplacedPortalFacesRemoved++;
            }
        }

        _floodPortals.Clear();
    }

    /// <summary>
    /// Pass E, <c>repair_holes</c> (:1466-1523): runs on the finished solid right before the hole count. Rewrites
    /// <paramref name="faces"/> / <paramref name="poolIndices"/> in place (removed faces dropped, fills appended)
    /// and links any refilled portal copy into <paramref name="portals"/>.
    /// </summary>
    internal void RepairHoles(List<CsgFace> faces, List<int[]> poolIndices, List<Vec3> pool, RoomBuildResult rooms, List<Portal> portals)
    {
        var roomOf = new int[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            roomOf[i] = faces[i].RoomIndex;
        }

        var mesh = Mesh.Over(faces, poolIndices, pool, roomOf);
        mesh.Rooms = rooms.Rooms;
        mesh.Portals = portals;
        var touched = new HashSet<int>();

        _stats.Ran = true;
        _stats.HolesAfterRooms = CountHoles(OpenEdges(mesh));
        _stats.PortalPlanesUnified += On(Pass.UnifyPlanes) ? UnifyPortalPlanes(mesh, beforeRooms: false) : 0;
        _stats.CollapsedFacesRemoved += On(Pass.LineFaces) ? RemoveLineFaces(mesh, touched) : 0;
        _stats.PortalStripsRemoved += On(Pass.PortalStrips) ? RemovePortalStrips(mesh, touched) : 0;
        for (int round = 0; round < MaxRepairRounds; round++)
        {
            int welds = On(Pass.Weld) ? WeldCracks(mesh, touched) : 0;
            int splits = On(Pass.Split) ? SplitTJunctions(mesh, touched) : 0;
            _stats.VerticesWelded += welds;
            _stats.EdgesSplit += splits;
            if (welds == 0 && splits == 0)
            {
                break;
            }
        }

        _stats.PortalGapsFilled += On(Pass.PortalFill) ? FillPortalGaps(mesh, touched) : 0;
        _stats.WallGapsFilled += On(Pass.WallFill) ? FillWallGaps(mesh, touched) : 0;

        // :1494-1506. Stock RED's >16-corner split (FUN_004a6950) is not emulated by Glacier (see the summary).
        if (On(Pass.UnifyPlanes))
        {
            UnifyPortalPlanes(mesh, beforeRooms: false);
        }

        mesh.WriteBack(faces, poolIndices);
        foreach (int room in touched)
        {
            RecomputeRoomBox(faces, rooms.Rooms, room);
        }
    }

    // ───────────────────────────── mesh ─────────────────────────────

    /// <summary>A face of the repair's working solid: a ring of pool indices plus its corner UVs.</summary>
    private sealed class MFace
    {
        public MFace(CsgFace src, List<int> ring, List<Uv> uvs, int room)
        {
            Src = src;
            Ring = ring;
            Uvs = uvs;
            Room = room;
        }

        public CsgFace Src { get; }

        public List<int> Ring { get; }

        public List<Uv> Uvs { get; }

        public int Room { get; set; }

        public bool Alive { get; set; } = true;

        /// <summary>Ring changed: the source face's corners are rewritten from the pool on write-back.</summary>
        public bool Edited { get; set; }

        public bool HasBox { get; set; }

        public Vec3 Min { get; set; }

        public Vec3 Max { get; set; }

        public CsgPlane Plane
        {
            get => Src.Plane;
            set => Src.Plane = value;
        }

        public bool IsPortal => Src.IsPortal;

        public bool IsDetail => (Src.Flags & (ushort)FaceFlags.IsDetail) != 0;

        public bool IsLiquid => (Src.Flags & (ushort)FaceFlags.LiquidSurface) != 0;

        /// <summary>As the room builder's regular face (FUN_0043cc90, :97-101): not liquid, detail or portal.</summary>
        public bool IsRegular => !IsLiquid && !IsDetail && !IsPortal;
    }

    private sealed class Mesh
    {
        private Mesh(List<Vec3> pool)
        {
            Pool = pool;
            Refs = new List<List<int>>(pool.Count);
            for (int i = 0; i < pool.Count; i++)
            {
                Refs.Add(new List<int>(4));
            }
        }

        public List<MFace> Faces { get; } = new();

        public List<Vec3> Pool { get; }

        /// <summary>Per pool vertex, the live faces referencing it (GVertex::face_refs).</summary>
        public List<List<int>> Refs { get; }

        public List<Room> Rooms { get; set; } = new();

        public List<Portal>? Portals { get; set; }

        public static Mesh Over(List<CsgFace> faces, List<int[]> poolIndices, IReadOnlyList<Vec3> pool, int[]? roomOf)
        {
            var mesh = new Mesh(pool as List<Vec3> ?? new List<Vec3>(pool));
            for (int i = 0; i < faces.Count; i++)
            {
                CsgFace f = faces[i];
                int[] idx = poolIndices[i];
                var ring = new List<int>(idx);
                var uvs = new List<Uv>(idx.Length);
                for (int k = 0; k < idx.Length; k++)
                {
                    uvs.Add(k < f.Vertices.Count ? f.Vertices[k].Uv : default);
                }

                int room = roomOf is null ? f.RoomIndex : roomOf[i];
                mesh.Faces.Add(new MFace(f, ring, uvs, room));
                mesh.AddRefs(i);
            }

            return mesh;
        }

        public Vec3 Pos(int v) => Pool[v];

        public bool HasRef(int v, int face) => Refs[v].Contains(face);

        public bool RoomIsDetail(int room) => room < 0 || room >= Rooms.Count || Rooms[room].IsSubroom != 0;

        private void AddRefs(int f)
        {
            foreach (int v in Faces[f].Ring)
            {
                if (!Refs[v].Contains(f))
                {
                    Refs[v].Add(f);
                }
            }
        }

        public void AddRef(int v, int f)
        {
            if (!Refs[v].Contains(f))
            {
                Refs[v].Add(f);
            }
        }

        /// <summary>RED's remove_face + GFace::destroy: unlink the face from its vertices.</summary>
        public void Remove(int f)
        {
            MFace face = Faces[f];
            face.Alive = false;
            foreach (int v in face.Ring)
            {
                Refs[v].Remove(f);
            }
        }

        public int Add(MFace face)
        {
            int f = Faces.Count;
            Faces.Add(face);
            AddRefs(f);
            return f;
        }

        /// <summary>The live, non-detail portal faces (portal_faces, :202-212), in face order.</summary>
        public List<int> PortalFaces()
        {
            var list = new List<int>();
            for (int f = 0; f < Faces.Count; f++)
            {
                MFace face = Faces[f];
                if (face.Alive && face.IsPortal && !face.IsDetail)
                {
                    list.Add(f);
                }
            }

            return list;
        }

        /// <summary>Faces per room, in face order (GRoom face lists).</summary>
        public List<int>[] RoomFaces()
        {
            var lists = new List<int>[Rooms.Count];
            for (int r = 0; r < lists.Length; r++)
            {
                lists[r] = new List<int>();
            }

            for (int f = 0; f < Faces.Count; f++)
            {
                MFace face = Faces[f];
                if (face.Alive && face.Room >= 0 && face.Room < lists.Length)
                {
                    lists[face.Room].Add(f);
                }
            }

            return lists;
        }

        public void Box(int f, out Vec3 min, out Vec3 max)
        {
            MFace face = Faces[f];
            if (!face.HasBox)
            {
                var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                foreach (int v in face.Ring)
                {
                    mn = Vec3Math.Min(mn, Pool[v]);
                    mx = Vec3Math.Max(mx, Pool[v]);
                }

                face.Min = mn;
                face.Max = mx;
                face.HasBox = true;
            }

            min = face.Min;
            max = face.Max;
        }

        /// <summary>
        /// FUN_0048a8b0 with a null plane: Newell plane through the ring (offset through the centroid) and a fresh
        /// box. False for fewer than three corners or zero area.
        /// </summary>
        public bool ComputePlaneAndBox(int f)
        {
            MFace face = Faces[f];
            face.HasBox = false;
            List<int> ring = face.Ring;
            if (ring.Count < 3)
            {
                return false;
            }

            D n = default;
            D c = default;
            for (int i = 0; i < ring.Count; i++)
            {
                D a = D.Of(Pool[ring[i]]);
                D b = D.Of(Pool[ring[(i + 1) % ring.Count]]);
                n = n + new D((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                c = c + a;
            }

            double len = n.Length;
            if (!(len > 0.0))
            {
                return false;
            }

            D unit = n * (1.0 / len);
            c = c * (1.0 / ring.Count);
            face.Plane = new CsgPlane(unit.ToVec3(), (float)-D.Dot(unit, c));
            return true;
        }

        /// <summary>Rewrites the compiler's parallel face lists from the working solid (live faces, face order).</summary>
        public void WriteBack(List<CsgFace> faces, List<int[]> poolIndices)
        {
            faces.Clear();
            poolIndices.Clear();
            foreach (MFace face in Faces)
            {
                if (!face.Alive)
                {
                    continue;
                }

                face.Src.RoomIndex = face.Room;
                if (face.Edited || face.Src.Vertices.Count != face.Ring.Count)
                {
                    var verts = new List<CsgVertex>(face.Ring.Count);
                    for (int k = 0; k < face.Ring.Count; k++)
                    {
                        verts.Add(new CsgVertex(Pool[face.Ring[k]], face.Uvs[k]));
                    }

                    face.Src.Vertices = verts;
                }

                faces.Add(face.Src);
                poolIndices.Add(face.Ring.ToArray());
            }
        }
    }

    /// <summary>A double-precision vector (Alpine's <c>Vec</c>, :40-80).</summary>
    private readonly record struct D(double X, double Y, double Z)
    {
        public static D Of(Vec3 v) => new(v.X, v.Y, v.Z);

        public static D operator +(D a, D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static D operator -(D a, D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static D operator *(D a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(D a, D b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

        public static D Cross(D a, D b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

        public double Length => Math.Sqrt(Dot(this, this));

        public Vec3 ToVec3() => new((float)X, (float)Y, (float)Z);
    }

    private readonly record struct PortalBox(CsgPlane Plane, Vec3 Min, Vec3 Max)
    {
        public static PortalBox Of(CsgFace face)
        {
            var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            face.GrowAabb(ref mn, ref mx);
            return new PortalBox(face.Plane, mn, mx);
        }
    }

    private static double PlaneDistance(CsgPlane plane, Vec3 p) => D.Dot(D.Of(plane.Normal), D.Of(p)) + plane.Offset;

    /// <summary>Twice the area along the winding normal (Newell, :138-150).</summary>
    private static D Newell(Mesh mesh, IReadOnlyList<int> ring)
    {
        D n = default;
        for (int i = 0; i < ring.Count; i++)
        {
            D a = D.Of(mesh.Pos(ring[i]));
            D b = D.Of(mesh.Pos(ring[(i + 1) % ring.Count]));
            n = n + new D((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        return n;
    }

    private static bool IsEdge(MFace face, int a, int b)
    {
        List<int> r = face.Ring;
        for (int i = 0; i < r.Count; i++)
        {
            int p = r[i];
            int q = r[(i + 1) % r.Count];
            if ((p == a && q == b) || (p == b && q == a))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NearBox(Vec3 v, Vec3 min, Vec3 max) =>
        v.X >= min.X - RepairTolerance && v.X <= max.X + RepairTolerance
        && v.Y >= min.Y - RepairTolerance && v.Y <= max.Y + RepairTolerance
        && v.Z >= min.Z - RepairTolerance && v.Z <= max.Z + RepairTolerance;

    private static bool NoDetailFaces(Mesh mesh, int v)
    {
        foreach (int f in mesh.Refs[v])
        {
            if (mesh.Faces[f].IsDetail)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>RED's plane side test FUN_0048a790 at its 1e-4 band: 1 front, 2 on, 3 back.</summary>
    private static int SideOf(CsgPlane plane, Vec3 p)
    {
        double d = PlaneDistance(plane, p);
        if (d < -RedEpsilon)
        {
            return 3;
        }

        return d <= RedEpsilon ? 2 : 1;
    }

    /// <summary>
    /// FUN_004861d0 (GFace::in_front_of, Alpine level.h:330): more corners in front of the portal's plane than
    /// behind, or all on it with the normals agreeing.
    /// </summary>
    private static bool InFrontOf(Mesh mesh, MFace face, MFace portal)
    {
        int front = 0;
        int back = 0;
        int on = 0;
        foreach (int v in face.Ring)
        {
            switch (SideOf(portal.Plane, mesh.Pos(v)))
            {
                case 1:
                    front++;
                    break;
                case 2:
                    on++;
                    break;
                default:
                    back++;
                    break;
            }
        }

        if (on != face.Ring.Count)
        {
            return back < front;
        }

        return D.Dot(D.Of(face.Plane.Normal), D.Of(portal.Plane.Normal)) >= 0.0;
    }

    /// <summary>Union-find with first-seen ordering so groups come out deterministic (DisjointSets, :214-261).</summary>
    private sealed class DisjointSets
    {
        private readonly Dictionary<int, int> _parent = new();
        private readonly List<int> _order = new();

        public int Find(int x)
        {
            if (!_parent.ContainsKey(x))
            {
                _parent[x] = x;
                _order.Add(x);
            }

            int root = x;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            while (_parent[x] != root)
            {
                int next = _parent[x];
                _parent[x] = root;
                x = next;
            }

            return root;
        }

        public void Join(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra != rb)
            {
                _parent[rb] = ra;
            }
        }

        /// <summary>Sets of two or more, in first-seen order.</summary>
        public List<List<int>> Groups()
        {
            var output = new List<List<int>>();
            var slot = new Dictionary<int, int>();
            foreach (int x in _order)
            {
                int root = Find(x);
                if (!slot.TryGetValue(root, out int s))
                {
                    s = output.Count;
                    slot[root] = s;
                    output.Add(new List<int>());
                }

                output[s].Add(x);
            }

            output.RemoveAll(static g => g.Count < 2);
            return output;
        }
    }

    // ───────────────────────────── portal planes (:320-377) ─────────────────────────────

    /// <summary>
    /// Each coplanar group of portal pieces takes its largest piece's plane (or its negation, by winding), so a
    /// sliver's tilted plane cannot mislead the side test. Before the room builder coplanar is RED's own side
    /// test; afterwards within 1e-3. Planes only — no vertex moves. Returns the planes changed.
    /// </summary>
    private static int UnifyPortalPlanes(Mesh mesh, bool beforeRooms)
    {
        var portals = new List<(double Area, int Face)>();
        foreach (int f in mesh.PortalFaces())
        {
            portals.Add((Newell(mesh, mesh.Faces[f].Ring).Length, f));
        }

        // stable sort by area, descending
        var order = new int[portals.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (x, y) =>
        {
            int c = portals[y].Area.CompareTo(portals[x].Area);
            return c != 0 ? c : x.CompareTo(y);
        });

        int changed = 0;
        var done = new HashSet<int>();
        var queue = new List<int>();
        foreach (int o in order)
        {
            int seed = portals[o].Face;
            if (done.Contains(seed))
            {
                continue;
            }

            CsgPlane reference = mesh.Faces[seed].Plane;
            D refNormal = D.Of(reference.Normal);
            done.Add(seed);
            queue.Clear();
            queue.Add(seed);
            while (queue.Count > 0)
            {
                int f = queue[^1];
                queue.RemoveAt(queue.Count - 1);
                MFace face = mesh.Faces[f];

                // The winding gives the facing even under a tilted plane, unless the piece is too thin for it.
                D winding = Newell(mesh, face.Ring);
                double facing = D.Dot(winding, refNormal);
                bool same = Math.Abs(facing) > 1e-6 * winding.Length
                    ? facing > 0.0
                    : D.Dot(D.Of(face.Plane.Normal), refNormal) >= 0.0;
                CsgPlane plane = same ? reference : reference.Flipped();
                if (!BitEqual(face.Plane, plane))
                {
                    face.Plane = plane;
                    changed++;
                }

                foreach (int v in face.Ring)
                {
                    foreach (int other in mesh.Refs[v])
                    {
                        MFace o2 = mesh.Faces[other];
                        if (o2.IsPortal && !o2.IsDetail && !done.Contains(other) && InPlane(mesh, o2, reference, beforeRooms))
                        {
                            done.Add(other);
                            queue.Add(other);
                        }
                    }
                }
            }
        }

        return changed;
    }

    private static bool InPlane(Mesh mesh, MFace face, CsgPlane reference, bool beforeRooms)
    {
        foreach (int v in face.Ring)
        {
            bool inside = beforeRooms
                ? SideOf(reference, mesh.Pos(v)) == 2
                : Math.Abs(PlaneDistance(reference, mesh.Pos(v))) <= RepairTolerance;
            if (!inside)
            {
                return false;
            }
        }

        return true;
    }

    private static bool BitEqual(CsgPlane a, CsgPlane b) =>
        BitConverter.SingleToInt32Bits(a.Normal.X) == BitConverter.SingleToInt32Bits(b.Normal.X)
        && BitConverter.SingleToInt32Bits(a.Normal.Y) == BitConverter.SingleToInt32Bits(b.Normal.Y)
        && BitConverter.SingleToInt32Bits(a.Normal.Z) == BitConverter.SingleToInt32Bits(b.Normal.Z)
        && BitConverter.SingleToInt32Bits(a.Offset) == BitConverter.SingleToInt32Bits(b.Offset);

    // ───────────────────────────── after the flood (:379-568) ─────────────────────────────

    /// <summary>Null (−1) unless exactly one room's bordering regular faces are in front; edges vote before corners (:381-407).</summary>
    private static int RoomInFront(Mesh mesh, int portal)
    {
        MFace p = mesh.Faces[portal];
        (int Room, bool Ambiguous) Vote(bool alongEdges)
        {
            int found = -1;
            bool ambiguous = false;
            var seen = new HashSet<int>();
            List<int> ring = p.Ring;
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i];
                int b = ring[(i + 1) % ring.Count];
                foreach (int other in mesh.Refs[a])
                {
                    MFace o = mesh.Faces[other];
                    if (other == portal || !o.IsRegular || mesh.RoomIsDetail(o.Room)
                        || (alongEdges && !IsEdge(o, a, b)) || !seen.Add(other) || !InFrontOf(mesh, o, p))
                    {
                        continue;
                    }

                    ambiguous = ambiguous || (found >= 0 && found != o.Room);
                    found = o.Room;
                }
            }

            return (found, ambiguous);
        }

        (int room, bool amb) = Vote(true);
        if (room < 0 && !amb)
        {
            (room, amb) = Vote(false);
        }

        return amb ? -1 : room;
    }

    /// <summary>A portal copy the flood put in the wrong room leaves the other room open (fix_portal_twins, :409-446).</summary>
    private static int FixPortalTwins(Mesh mesh)
    {
        var slot = new Dictionary<string, int>();
        var pairs = new List<List<int>>();
        foreach (int f in mesh.PortalFaces())
        {
            string key = SortedKey(mesh.Faces[f].Ring);
            if (!slot.TryGetValue(key, out int s))
            {
                s = pairs.Count;
                slot[key] = s;
                pairs.Add(new List<int>());
            }

            pairs[s].Add(f);
        }

        int moved = 0;
        foreach (List<int> pair in pairs)
        {
            if (pair.Count != 2)
            {
                continue;
            }

            MFace a = mesh.Faces[pair[0]];
            MFace b = mesh.Faces[pair[1]];
            if (D.Dot(D.Of(a.Plane.Normal), D.Of(b.Plane.Normal)) >= 0.0)
            {
                continue;
            }

            if (a.Room >= 0 && b.Room >= 0 && a.Room != b.Room)
            {
                continue;
            }

            int roomA = RoomInFront(mesh, pair[0]);
            int roomB = RoomInFront(mesh, pair[1]);

            // one room in front of both is a portal inside one room, which portal linking removes
            if (roomA < 0 || roomB < 0 || roomA == roomB)
            {
                continue;
            }

            if (a.Room != roomA)
            {
                a.Room = roomA;
                moved++;
            }

            if (b.Room != roomB)
            {
                b.Room = roomB;
                moved++;
            }
        }

        return moved;
    }

    private static string SortedKey(List<int> ring)
    {
        var sorted = new List<int>(ring);
        sorted.Sort();
        return string.Join(",", sorted);
    }

    /// <summary>An edge along a portal's boundary — corners or extents, within 1e-3 of its plane (:448-469).</summary>
    private static bool OnPortal(Mesh mesh, int a, int b, List<PortalBox> portals)
    {
        Vec3 pa = mesh.Pos(a);
        Vec3 pb = mesh.Pos(b);
        bool InPlane(CsgPlane plane) =>
            Math.Abs(PlaneDistance(plane, pa)) <= RepairTolerance && Math.Abs(PlaneDistance(plane, pb)) <= RepairTolerance;

        foreach (int v in new[] { a, b })
        {
            foreach (int f in mesh.Refs[v])
            {
                MFace face = mesh.Faces[f];
                if (face.IsPortal && !face.IsDetail && InPlane(face.Plane))
                {
                    return true;
                }
            }
        }

        foreach (PortalBox p in portals)
        {
            if ((NearBox(pa, p.Min, p.Max) || NearBox(pb, p.Min, p.Max)) && InPlane(p.Plane))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A coplanar, opposed, overlapping portal pair between the two rooms (:471-505).</summary>
    private static bool PortalBetween(Mesh mesh, List<int> inA, List<int> inB)
    {
        foreach (int pf in inA)
        {
            MFace p = mesh.Faces[pf];
            if (!p.IsPortal)
            {
                continue;
            }

            mesh.Box(pf, out Vec3 pmin, out Vec3 pmax);
            foreach (int qf in inB)
            {
                MFace q = mesh.Faces[qf];
                if (!q.IsPortal)
                {
                    continue;
                }

                mesh.Box(qf, out Vec3 qmin, out Vec3 qmax);
                bool overlap = pmin.X <= qmax.X + RepairTolerance && qmin.X <= pmax.X + RepairTolerance
                    && pmin.Y <= qmax.Y + RepairTolerance && qmin.Y <= pmax.Y + RepairTolerance
                    && pmin.Z <= qmax.Z + RepairTolerance && qmin.Z <= pmax.Z + RepairTolerance;
                if (!overlap || D.Dot(D.Of(p.Plane.Normal), D.Of(q.Plane.Normal)) > -ParallelCosine)
                {
                    continue;
                }

                bool coplanar = true;
                foreach (int v in q.Ring)
                {
                    coplanar = coplanar && Math.Abs(PlaneDistance(p.Plane, mesh.Pos(v))) <= RepairTolerance;
                }

                if (coplanar)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Rooms with no portal between them that share a full edge away from every portal are one space the flood
    /// split (merge_fragment_rooms, :507-568). The room with the most faces absorbs the rest; an absorbed room is
    /// left empty with a zero box as a detail room, its index preserved.
    /// </summary>
    private static int MergeFragmentRooms(Mesh mesh)
    {
        var portals = new List<PortalBox>();
        foreach (int f in mesh.PortalFaces())
        {
            mesh.Box(f, out Vec3 mn, out Vec3 mx);
            portals.Add(new PortalBox(mesh.Faces[f].Plane, mn, mx));
        }

        List<int>[] roomFaces = mesh.RoomFaces();
        var sets = new DisjointSets();
        var separated = new Dictionary<(int, int), bool>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            MFace face = mesh.Faces[f];
            int room = face.Room;
            if (!face.Alive || !face.IsRegular || mesh.RoomIsDetail(room))
            {
                continue;
            }

            List<int> ring = face.Ring;
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i];
                int b = ring[(i + 1) % ring.Count];
                foreach (int other in mesh.Refs[a])
                {
                    MFace o = mesh.Faces[other];
                    int otherRoom = o.Room;
                    if (other == f || !o.IsRegular || mesh.RoomIsDetail(otherRoom)
                        || sets.Find(otherRoom) == sets.Find(room) || !mesh.HasRef(b, other)
                        || OnPortal(mesh, a, b, portals))
                    {
                        continue;
                    }

                    var key = room < otherRoom ? (room, otherRoom) : (otherRoom, room);
                    if (!separated.TryGetValue(key, out bool sep))
                    {
                        sep = PortalBetween(mesh, roomFaces[room], roomFaces[otherRoom]);
                        separated[key] = sep;
                    }

                    if (!sep)
                    {
                        sets.Join(room, otherRoom);
                    }
                }
            }
        }

        int merged = 0;
        foreach (List<int> group in sets.Groups())
        {
            int primary = group[0];
            foreach (int r in group)
            {
                if (roomFaces[r].Count > roomFaces[primary].Count)
                {
                    primary = r;
                }
            }

            foreach (int r in group)
            {
                if (r == primary)
                {
                    continue;
                }

                foreach (int f in roomFaces[r])
                {
                    mesh.Faces[f].Room = primary;
                }

                Room absorbed = mesh.Rooms[r];
                absorbed.Aabb = new Aabb(Vec3.Zero, Vec3.Zero);
                absorbed.IsSubroom = 1;
                merged++;
            }

            roomFaces[primary].Clear();
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                if (mesh.Faces[f].Alive && mesh.Faces[f].Room == primary)
                {
                    roomFaces[primary].Add(f);
                }
            }

            RecomputeRoomBox(mesh, primary);
        }

        return merged;
    }

    // ───────────────────────────── open edges (:570-637) ─────────────────────────────

    private readonly record struct OpenEdge(int Face, int A, int B);

    /// <summary>
    /// CheckForHoles' rule (0x0043c420, :581-604): an edge is open unless another face of its room references
    /// both ends. Rooms that are detail or empty are skipped. Liquid faces are never edge sources (see summary).
    /// </summary>
    private static List<(int Room, List<OpenEdge> Edges)> OpenEdges(Mesh mesh)
    {
        var output = new List<(int, List<OpenEdge>)>();
        List<int>[] roomFaces = mesh.RoomFaces();
        for (int room = 0; room < roomFaces.Length; room++)
        {
            if (mesh.RoomIsDetail(room) || roomFaces[room].Count == 0)
            {
                continue;
            }

            var list = new List<OpenEdge>();
            foreach (int f in roomFaces[room])
            {
                MFace face = mesh.Faces[f];
                if (face.IsLiquid)
                {
                    continue;
                }

                List<int> ring = face.Ring;
                for (int i = 0; i < ring.Count; i++)
                {
                    int a = ring[i];
                    int b = ring[(i + 1) % ring.Count];
                    bool closed = false;
                    foreach (int other in mesh.Refs[a])
                    {
                        if (other != f && mesh.Faces[other].Room == room && mesh.HasRef(b, other))
                        {
                            closed = true;
                            break;
                        }
                    }

                    if (!closed)
                    {
                        list.Add(new OpenEdge(f, a, b));
                    }
                }
            }

            if (list.Count > 0)
            {
                output.Add((room, list));
            }
        }

        return output;
    }

    /// <summary>Open-edge endpoints, unique, ordered by position (x, then y, then z).</summary>
    private static List<int> OpenEdgeEnds(Mesh mesh, List<OpenEdge> list)
    {
        var set = new SortedSet<int>();
        foreach (OpenEdge e in list)
        {
            set.Add(e.A);
            set.Add(e.B);
        }

        var ends = new List<int>(set);

        // stable by position (List.Sort is unstable, so break ties by the prior order — the vertex index)
        ends.Sort((x, y) =>
        {
            Vec3 p = mesh.Pos(x);
            Vec3 q = mesh.Pos(y);
            int c = p.X.CompareTo(q.X);
            if (c == 0)
            {
                c = p.Y.CompareTo(q.Y);
            }

            if (c == 0)
            {
                c = p.Z.CompareTo(q.Z);
            }

            return c != 0 ? c : x.CompareTo(y);
        });
        return ends;
    }

    /// <summary>Open edges sharing a vertex make one hole, as CheckForHoles counts them (:621-637).</summary>
    private static int CountHoles(List<(int Room, List<OpenEdge> Edges)> edges)
    {
        int holes = 0;
        foreach ((int _, List<OpenEdge> list) in edges)
        {
            var sets = new DisjointSets();
            foreach (OpenEdge e in list)
            {
                sets.Join(e.A, e.B);
            }

            var roots = new HashSet<int>();
            foreach (OpenEdge e in list)
            {
                roots.Add(sets.Find(e.A));
            }

            holes += roots.Count;
        }

        return holes;
    }

    // ───────────────────────────── spatial grid (:675-782) ─────────────────────────────

    /// <summary>Faces bucketed on a 1-unit grid; faces spanning over 512 cells are kept apart and box-tested.</summary>
    private sealed class FaceGrid
    {
        private const long MaxCellsPerFace = 512;
        private const double MaxCell = 1e6;
        private readonly Dictionary<long, List<int>> _cells = new();
        private readonly List<int> _large = new();
        private readonly Mesh _mesh;

        public FaceGrid(Mesh mesh, List<int> faces)
        {
            _mesh = mesh;
            foreach (int f in faces)
            {
                mesh.Box(f, out Vec3 min, out Vec3 max);
                (int lx, int ly, int lz) = CellOf(min);
                (int hx, int hy, int hz) = CellOf(max);
                long count = ((long)hx - lx + 1) * ((long)hy - ly + 1) * ((long)hz - lz + 1);
                if (count > MaxCellsPerFace)
                {
                    _large.Add(f);
                    continue;
                }

                for (int x = lx; x <= hx; x++)
                {
                    for (int y = ly; y <= hy; y++)
                    {
                        for (int z = lz; z <= hz; z++)
                        {
                            long k = Key(x, y, z);
                            if (!_cells.TryGetValue(k, out List<int>? list))
                            {
                                list = new List<int>();
                                _cells[k] = list;
                            }

                            list.Add(f);
                        }
                    }
                }
            }
        }

        public List<int> Around(Vec3 p)
        {
            var output = new List<int>();
            foreach (int f in _large)
            {
                _mesh.Box(f, out Vec3 min, out Vec3 max);
                if (NearBox(p, min, max))
                {
                    output.Add(f);
                }
            }

            float pad = (float)RepairTolerance;
            (int lx, int ly, int lz) = CellOf(new Vec3(p.X - pad, p.Y - pad, p.Z - pad));
            (int hx, int hy, int hz) = CellOf(new Vec3(p.X + pad, p.Y + pad, p.Z + pad));
            var seen = new HashSet<int>();
            for (int x = lx; x <= hx; x++)
            {
                for (int y = ly; y <= hy; y++)
                {
                    for (int z = lz; z <= hz; z++)
                    {
                        if (!_cells.TryGetValue(Key(x, y, z), out List<int>? list))
                        {
                            continue;
                        }

                        foreach (int f in list)
                        {
                            if (seen.Add(f))
                            {
                                output.Add(f);
                            }
                        }
                    }
                }
            }

            return output;
        }

        private static (int, int, int) CellOf(Vec3 v) => (Cell(v.X), Cell(v.Y), Cell(v.Z));

        private static int Cell(float c)
        {
            double f = Math.Floor((double)c);
            return (int)(f >= -MaxCell && f <= MaxCell ? f : 0.0);
        }

        private static long Key(int x, int y, int z) =>
            ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }

    // ───────────────────────────── cracks (:784-1112) ─────────────────────────────

    /// <summary>Drops a corner repeated by its successor; false if fewer than three corners remain (:786-808).</summary>
    private static bool DropRepeatedCorners(MFace face)
    {
        List<int> ring = face.Ring;
        List<Uv> uvs = face.Uvs;
        int i = 0;
        int unchanged = 0;
        while (ring.Count > 1 && unchanged < ring.Count)
        {
            int j = (i + 1) % ring.Count;
            if (ring[i] != ring[j])
            {
                i = j;
                unchanged++;
                continue;
            }

            ring.RemoveAt(j);
            uvs.RemoveAt(j);
            face.Edited = true;
            if (j == 0)
            {
                // the loop's head went: the corner before it becomes the head
                i--;
                Rotate(ring, i);
                Rotate(uvs, i);
                i = 0;
            }
            else if (j < i)
            {
                i--;
            }

            unchanged = 0;
        }

        return ring.Count >= 3;
    }

    private static void Rotate<T>(List<T> list, int start)
    {
        if (start <= 0 || start >= list.Count)
        {
            return;
        }

        var head = list.GetRange(start, list.Count - start);
        head.AddRange(list.GetRange(0, start));
        list.Clear();
        list.AddRange(head);
    }

    /// <summary>Each corner's offset from the line through the two corners farthest apart (:816-845).</summary>
    private static List<D>? LineOffsets(Mesh mesh, MFace face, out D dir)
    {
        dir = default;
        List<int> ring = face.Ring;
        D a = default;
        D b = default;
        double longest = 0.0;
        for (int i = 0; i < ring.Count; i++)
        {
            for (int j = i + 1; j < ring.Count; j++)
            {
                D p = D.Of(mesh.Pos(ring[i]));
                D q = D.Of(mesh.Pos(ring[j]));
                double l = (q - p).Length;
                if (l > longest)
                {
                    longest = l;
                    a = p;
                    b = q;
                }
            }
        }

        if (!(longest > 0.0))
        {
            return null;
        }

        dir = (b - a) * (1.0 / longest);
        var offsets = new List<D>(ring.Count);
        foreach (int v in ring)
        {
            D d = D.Of(mesh.Pos(v)) - a;
            offsets.Add(d - (dir * D.Dot(d, dir)));
        }

        return offsets;
    }

    private static bool IsLine(Mesh mesh, MFace face, double tolerance)
    {
        List<D>? offsets = LineOffsets(mesh, face, out _);
        if (offsets is null)
        {
            return true; // all corners coincide: std::all_of over no offsets
        }

        foreach (D o in offsets)
        {
            if (!(o.Length <= tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStrip(Mesh mesh, MFace face, double tolerance)
    {
        List<D>? offsets = LineOffsets(mesh, face, out D dir);
        if (offsets is null)
        {
            return true;
        }

        D side = D.Cross(D.Of(face.Plane.Normal), dir);
        double low = 0.0;
        double high = 0.0;
        foreach (D o in offsets)
        {
            if (!(o.Length <= tolerance))
            {
                return false;
            }

            low = Math.Min(low, D.Dot(o, side));
            high = Math.Max(high, D.Dot(o, side));
        }

        return high - low <= tolerance;
    }

    /// <summary>
    /// A zero-area regular face along a seam whose long edge counts as a hole goes, only when every edge it
    /// closes for another face of the room is closed by a third face too, and never a room's last face (:874-922).
    /// </summary>
    private static int RemoveLineFaces(Mesh mesh, HashSet<int> touched)
    {
        int removed = 0;
        foreach ((int room, List<OpenEdge> list) in OpenEdges(mesh))
        {
            var candidates = new List<int>();
            foreach (OpenEdge e in list)
            {
                if (candidates.Count == 0 || candidates[^1] != e.Face)
                {
                    candidates.Add(e.Face);
                }
            }

            foreach (int f in candidates)
            {
                MFace face = mesh.Faces[f];
                if (!face.Alive || !face.IsRegular || !IsLine(mesh, face, RedEpsilon) || RoomFaceCount(mesh, room) <= 1)
                {
                    continue;
                }

                bool safe = true;
                foreach (int a in face.Ring)
                {
                    foreach (int other in mesh.Refs[a])
                    {
                        MFace o = mesh.Faces[other];
                        if (other == f || o.Room != room)
                        {
                            continue;
                        }

                        List<int> oring = o.Ring;
                        for (int k = 0; k < oring.Count; k++)
                        {
                            int p = oring[k];
                            int q = oring[(k + 1) % oring.Count];
                            if (p != a || !mesh.HasRef(q, f))
                            {
                                continue;
                            }

                            bool closed = false;
                            foreach (int third in mesh.Refs[p])
                            {
                                if (third != f && third != other && mesh.Faces[third].Room == room && mesh.HasRef(q, third))
                                {
                                    closed = true;
                                    break;
                                }
                            }

                            safe = safe && closed;
                        }
                    }
                }

                if (!safe)
                {
                    continue;
                }

                touched.Add(room);
                mesh.Remove(f);
                removed++;
            }
        }

        return removed;
    }

    private static int RoomFaceCount(Mesh mesh, int room)
    {
        int n = 0;
        foreach (MFace f in mesh.Faces)
        {
            if (f.Alive && f.Room == room)
            {
                n++;
                if (n > 1)
                {
                    return n;
                }
            }
        }

        return n;
    }

    /// <summary>
    /// Zero-width (≤ 1e-3) non-detail portal pieces reached from open-edge endpoints, transitively, are removed
    /// (:924-971). Vertices only the strips used are dropped from the solid (in Glacier the pool keeps them unreferenced).
    /// </summary>
    private static int RemovePortalStrips(Mesh mesh, HashSet<int> touched)
    {
        var strips = new List<int>();
        var found = new HashSet<int>();
        void Visit(int v)
        {
            foreach (int f in mesh.Refs[v])
            {
                MFace face = mesh.Faces[f];
                if (face.IsPortal && !face.IsDetail && !found.Contains(f) && IsStrip(mesh, face, RepairTolerance))
                {
                    found.Add(f);
                    strips.Add(f);
                }
            }
        }

        foreach ((int _, List<OpenEdge> list) in OpenEdges(mesh))
        {
            foreach (int v in OpenEdgeEnds(mesh, list))
            {
                Visit(v);
            }
        }

        for (int i = 0; i < strips.Count; i++)
        {
            foreach (int v in mesh.Faces[strips[i]].Ring.ToArray())
            {
                Visit(v);
            }
        }

        foreach (int f in strips)
        {
            if (mesh.Faces[f].Room >= 0)
            {
                touched.Add(mesh.Faces[f].Room);
            }
        }

        foreach (int f in strips)
        {
            mesh.Remove(f);
        }

        return strips.Count;
    }

    /// <summary>
    /// Clusters same-room open-edge endpoints within 1e-3 that touch no detail face and redirects each cluster to
    /// its member with the most face references — no averaging. A cluster is skipped when a face would lose two
    /// corners that are not neighbours. Faces left under three corners (or with zero area) are removed (:973-1049).
    /// </summary>
    private int WeldCracks(Mesh mesh, HashSet<int> touched)
    {
        var sets = new DisjointSets();
        foreach ((int _, List<OpenEdge> list) in OpenEdges(mesh))
        {
            List<int> ends = OpenEdgeEnds(mesh, list);
            for (int i = 0; i < ends.Count; i++)
            {
                Vec3 pi = mesh.Pos(ends[i]);
                for (int j = i + 1; j < ends.Count && (double)(mesh.Pos(ends[j]).X - pi.X) <= RepairTolerance; j++)
                {
                    if ((D.Of(pi) - D.Of(mesh.Pos(ends[j]))).Length <= RepairTolerance
                        && NoDetailFaces(mesh, ends[i]) && NoDetailFaces(mesh, ends[j]))
                    {
                        sets.Join(ends[i], ends[j]);
                    }
                }
            }
        }

        int welds = 0;
        foreach (List<int> members in sets.Groups())
        {
            var memberSet = new HashSet<int>(members);
            var faces = new List<int>();
            var seen = new HashSet<int>();
            foreach (int v in members)
            {
                foreach (int f in mesh.Refs[v])
                {
                    if (seen.Add(f))
                    {
                        faces.Add(f);
                    }
                }
            }

            bool safe = true;
            foreach (int f in faces)
            {
                List<int> ring = mesh.Faces[f].Ring;
                int count = 0;
                int runs = 0;
                for (int k = 0; k < ring.Count; k++)
                {
                    bool inside = memberSet.Contains(ring[k]);
                    bool prevIn = memberSet.Contains(ring[(k + ring.Count - 1) % ring.Count]);
                    count += inside ? 1 : 0;
                    runs += inside && !prevIn ? 1 : 0;
                }

                if (count > 1 && runs != 1 && count != ring.Count)
                {
                    safe = false;
                }
            }

            if (!safe)
            {
                continue;
            }

            foreach (int f in faces)
            {
                if (mesh.Faces[f].Room >= 0)
                {
                    touched.Add(mesh.Faces[f].Room);
                }
            }

            int keep = members[0];
            foreach (int v in members)
            {
                if (mesh.Refs[v].Count > mesh.Refs[keep].Count)
                {
                    keep = v;
                }
            }

            foreach (int v in members)
            {
                if (v == keep)
                {
                    continue;
                }

                foreach (int f in mesh.Refs[v].ToArray())
                {
                    List<int> ring = mesh.Faces[f].Ring;
                    for (int k = 0; k < ring.Count; k++)
                    {
                        if (ring[k] == v)
                        {
                            ring[k] = keep;
                        }
                    }

                    mesh.Faces[f].Edited = true;
                    mesh.AddRef(keep, f);
                }

                mesh.Refs[v].Clear();
                welds++;
            }

            foreach (int f in faces)
            {
                if (!mesh.Faces[f].Alive)
                {
                    continue;
                }

                if (!DropRepeatedCorners(mesh.Faces[f]) || !mesh.ComputePlaneAndBox(f))
                {
                    mesh.Remove(f);
                    _stats.CollapsedFacesRemoved++;
                }
            }
        }

        return welds;
    }

    private static double SegmentDistance(D p, D a, D b, out double along)
    {
        D ab = b - a;
        double len = ab.Length;
        if (len <= 0.0)
        {
            along = 0.0;
            return (p - a).Length;
        }

        along = D.Dot(p - a, ab) / len;
        return (p - (a + (ab * (along / len)))).Length;
    }

    /// <summary>
    /// For each open-edge endpoint touching no detail face, the first non-detail face of its room with an edge
    /// within 1e-3 of it and strictly interior (more than 1e-3 from both ends) has the endpoint inserted into that
    /// edge, in every face sharing it (stock insert-vertex-on-edge FUN_00496770) (:1063-1112).
    /// </summary>
    private static int SplitTJunctions(Mesh mesh, HashSet<int> touched)
    {
        int splits = 0;
        List<int>[] roomFaces = mesh.RoomFaces();
        foreach ((int room, List<OpenEdge> list) in OpenEdges(mesh))
        {
            var grid = new FaceGrid(mesh, roomFaces[room]);
            foreach (int c in OpenEdgeEnds(mesh, list))
            {
                if (!NoDetailFaces(mesh, c))
                {
                    continue;
                }

                D p = D.Of(mesh.Pos(c));
                int edgeA = -1;
                int edgeB = -1;
                foreach (int f in grid.Around(mesh.Pos(c)))
                {
                    if (edgeA >= 0)
                    {
                        break;
                    }

                    MFace face = mesh.Faces[f];
                    if (!face.Alive || face.IsDetail || mesh.HasRef(c, f))
                    {
                        continue;
                    }

                    List<int> ring = face.Ring;
                    for (int k = 0; k < ring.Count; k++)
                    {
                        int a = ring[k];
                        int b = ring[(k + 1) % ring.Count];
                        double len = (D.Of(mesh.Pos(b)) - D.Of(mesh.Pos(a))).Length;
                        if (edgeA < 0
                            && SegmentDistance(p, D.Of(mesh.Pos(a)), D.Of(mesh.Pos(b)), out double along) <= RepairTolerance
                            && along > RepairTolerance && along < len - RepairTolerance)
                        {
                            edgeA = a;
                            edgeB = b;
                        }
                    }
                }

                if (edgeA < 0)
                {
                    continue;
                }

                var sharing = new List<int>();
                foreach (int f in mesh.Refs[edgeA])
                {
                    if (mesh.HasRef(edgeB, f))
                    {
                        sharing.Add(f);
                    }
                }

                foreach (int f in sharing)
                {
                    if (mesh.Faces[f].Room >= 0)
                    {
                        touched.Add(mesh.Faces[f].Room);
                    }
                }

                InsertVertexOnEdge(mesh, sharing, edgeA, edgeB, c);

                // the vertex can sit up to the tolerance off the edge, past the faces' boxes
                foreach (int f in sharing)
                {
                    if (mesh.Faces[f].Alive && !mesh.Faces[f].IsDetail)
                    {
                        mesh.ComputePlaneAndBox(f);
                    }
                }

                splits++;
            }
        }

        return splits;
    }

    /// <summary>
    /// FUN_00496770 / FUN_00496550: inserts <paramref name="vertex"/> between <paramref name="a"/> and
    /// <paramref name="b"/> in every non-detail face that runs a→b or b→a and does not already use it, its UV
    /// interpolated along the edge by distance (|v−a| / |b−a|, 0 / 1 inside RED's 1e-4).
    /// </summary>
    private static void InsertVertexOnEdge(Mesh mesh, List<int> faces, int a, int b, int vertex)
    {
        Vec3 pa = mesh.Pos(a);
        float dva = mesh.Pos(vertex).Sub(pa).Length();
        float dba = mesh.Pos(b).Sub(pa).Length();
        float t = dva < (float)RedEpsilon ? 0f : dba < (float)RedEpsilon ? 1f : dva / dba;
        foreach (int f in faces)
        {
            MFace face = mesh.Faces[f];
            if (!face.Alive || face.IsDetail || face.Ring.Contains(vertex))
            {
                continue;
            }

            List<int> ring = face.Ring;
            for (int k = 0; k < ring.Count; k++)
            {
                int cur = ring[k];
                int next = ring[(k + 1) % ring.Count];
                bool forward = cur == a && next == b;
                bool backward = cur == b && next == a;
                if (!forward && !backward)
                {
                    continue;
                }

                float s = forward ? t : 1f - t;
                Uv u0 = face.Uvs[k];
                Uv u1 = face.Uvs[(k + 1) % ring.Count];
                ring.Insert(k + 1, vertex);
                face.Uvs.Insert(k + 1, new Uv(((u1.U - u0.U) * s) + u0.U, ((u1.V - u0.V) * s) + u0.V));
                face.Edited = true;
                mesh.AddRef(vertex, f);
                k++;
            }
        }
    }

    // ───────────────────────────── fills (:1114-1308) ─────────────────────────────

    private sealed class Loop
    {
        public List<int> Vertices { get; } = new();

        public List<int> Faces { get; } = new();
    }

    /// <summary>Closed simple loops of open edges, each wound as a face filling it would be (:1122-1157).</summary>
    private static List<Loop> OpenLoops(List<OpenEdge> list)
    {
        var from = new Dictionary<int, List<int>>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!from.TryGetValue(list[i].B, out List<int>? l))
            {
                l = new List<int>();
                from[list[i].B] = l;
            }

            l.Add(i);
        }

        var used = new HashSet<int>();
        var loops = new List<Loop>();
        for (int start = 0; start < list.Count; start++)
        {
            if (used.Contains(start))
            {
                continue;
            }

            var loop = new Loop();
            int e = start;
            bool closed = false;
            for (int guard = 0; guard <= list.Count; guard++)
            {
                used.Add(e);
                loop.Vertices.Add(list[e].B);
                loop.Faces.Add(list[e].Face);
                if (!from.TryGetValue(list[e].A, out List<int>? next) || next.Count != 1)
                {
                    break;
                }

                e = next[0];
                if (e == start)
                {
                    closed = true;
                    break;
                }

                if (used.Contains(e))
                {
                    break;
                }
            }

            if (closed && loop.Vertices.Count >= 3)
            {
                loops.Add(loop);
            }
        }

        return loops;
    }

    /// <summary>
    /// Portal linking deletes a copy whose neighbours it can't place; a closed open-edge loop whose vertex set is
    /// exactly one surviving portal face of another non-detail room gets a reversed copy of it, linked as that
    /// portal's twin (:1159-1201).
    /// </summary>
    private static int FillPortalGaps(Mesh mesh, HashSet<int> touched)
    {
        var portals = new Dictionary<string, List<int>>();
        foreach (int f in mesh.PortalFaces())
        {
            if (mesh.Faces[f].Room < 0)
            {
                continue;
            }

            string key = SortedKey(mesh.Faces[f].Ring);
            if (!portals.TryGetValue(key, out List<int>? l))
            {
                l = new List<int>();
                portals[key] = l;
            }

            l.Add(f);
        }

        int fills = 0;
        foreach ((int room, List<OpenEdge> list) in OpenEdges(mesh))
        {
            foreach (Loop loop in OpenLoops(list))
            {
                if (!portals.TryGetValue(SortedKey(loop.Vertices), out List<int>? match) || match.Count != 1)
                {
                    continue;
                }

                int source = match[0];
                MFace src = mesh.Faces[source];
                int other = src.Room;
                if (other == room || mesh.RoomIsDetail(other)
                    || D.Dot(Newell(mesh, src.Ring), Newell(mesh, loop.Vertices)) >= 0.0)
                {
                    continue;
                }

                touched.Add(room);
                CsgFace copy = src.Src.CloneAttributes();
                var ring = new List<int>(src.Ring);
                var uvs = new List<Uv>(src.Uvs);
                ring.Reverse();
                uvs.Reverse();
                var face = new MFace(copy, ring, uvs, room) { Edited = true };
                int added = mesh.Add(face);
                if (!mesh.ComputePlaneAndBox(added))
                {
                    mesh.Remove(added);
                    continue;
                }

                LinkPortalFace(mesh, face, room, other);
                fills++;
            }
        }

        return fills;
    }

    /// <summary>FUN_004affa0: links a portal face between two rooms, creating their portal record if needed.</summary>
    private static void LinkPortalFace(Mesh mesh, MFace face, int room, int other)
    {
        List<Portal>? portals = mesh.Portals;
        face.Src.IsPortal = true;
        face.Src.Texture = string.Empty;
        if (portals is null)
        {
            return;
        }

        int rec = -1;
        for (int i = 0; i < portals.Count; i++)
        {
            Portal p = portals[i];
            if ((p.RoomIndex1 == room && p.RoomIndex2 == other) || (p.RoomIndex1 == other && p.RoomIndex2 == room))
            {
                rec = i;
                break;
            }
        }

        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (int v in face.Ring)
        {
            mn = Vec3Math.Min(mn, mesh.Pos(v));
            mx = Vec3Math.Max(mx, mesh.Pos(v));
        }

        if (rec < 0)
        {
            rec = portals.Count;
            portals.Add(new Portal { RoomIndex1 = room, RoomIndex2 = other, Point1 = mn, Point2 = mx });
        }
        else
        {
            Portal p = portals[rec];
            p.Point1 = Vec3Math.Min(p.Point1, mn);
            p.Point2 = Vec3Math.Max(p.Point2, mx);
        }

        face.Src.PortalIndexPlus2 = rec + 2;
    }

    /// <summary>A lost portal leaves a doorway, not a wall gap (:1203-1212).</summary>
    private bool InDoorway(Mesh mesh, Loop loop)
    {
        for (int k = 0; k < loop.Vertices.Count; k++)
        {
            if (OnPortal(mesh, loop.Vertices[k], loop.Vertices[(k + 1) % loop.Vertices.Count], _portalBoxes))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fills a closed open-edge loop with a new face only if it has at most 16 corners, every bordering face is
    /// regular, 0 &lt; area ≤ 1 unit², a bordering face is coplanar with it (dot ≥ 0.999, every corner within
    /// 1e-3), it is convex, and no loop edge lies on a portal. The new face copies the wall face's attributes, no
    /// portal, no lightmap surface, UVs by the affine map of three wall corners, plane forced to the wall's (:1214-1308).
    /// </summary>
    private int FillWallGaps(Mesh mesh, HashSet<int> touched)
    {
        int fills = 0;
        foreach ((int room, List<OpenEdge> list) in OpenEdges(mesh))
        {
            foreach (Loop loop in OpenLoops(list))
            {
                bool regular = true;
                foreach (int f in loop.Faces)
                {
                    regular = regular && mesh.Faces[f].IsRegular;
                }

                if (loop.Vertices.Count > MaxFaceCorners || !regular)
                {
                    continue;
                }

                var points = new List<D>(loop.Vertices.Count);
                foreach (int v in loop.Vertices)
                {
                    points.Add(D.Of(mesh.Pos(v)));
                }

                D n = Newell(mesh, loop.Vertices);
                double twiceArea = n.Length;
                if (!(twiceArea > 0.0) || twiceArea > 2.0 * MaxFillArea)
                {
                    continue;
                }

                MFace? wall = null;
                foreach (int f in loop.Faces)
                {
                    MFace candidate = mesh.Faces[f];
                    bool inPlane = D.Dot(n, D.Of(candidate.Plane.Normal)) >= ParallelCosine * twiceArea;
                    foreach (int v in loop.Vertices)
                    {
                        inPlane = inPlane && Math.Abs(PlaneDistance(candidate.Plane, mesh.Pos(v))) <= RepairTolerance;
                    }

                    if (inPlane)
                    {
                        wall = candidate;
                        break;
                    }
                }

                bool flat = wall is not null;
                D wallNormal = flat ? D.Of(wall!.Plane.Normal) : n * (1.0 / twiceArea);

                // corners on a straight line count as convex
                bool convex = true;
                for (int k = 0; k < points.Count; k++)
                {
                    D p0 = points[(k + points.Count - 1) % points.Count];
                    D p1 = points[k];
                    D p2 = points[(k + 1) % points.Count];
                    convex = convex && D.Dot(D.Cross(p1 - p0, p2 - p1), wallNormal) >= -1e-9;
                }

                if (!flat || !convex || InDoorway(mesh, loop))
                {
                    continue;
                }

                List<int> wring = wall!.Ring;
                if (wring.Count < 3)
                {
                    continue;
                }

                D origin = D.Of(mesh.Pos(wring[0]));
                D e1 = D.Of(mesh.Pos(wring[1])) - origin;
                double e1Length = e1.Length;
                if (!(e1Length > 0.0))
                {
                    continue;
                }

                D axisU = e1 * (1.0 / e1Length);
                D axisV = D.Cross(wallNormal, axisU);
                int third = 2;
                double best = 0.0;
                for (int k = 2; k < wring.Count; k++)
                {
                    double a = Math.Abs(D.Dot(D.Cross(e1, D.Of(mesh.Pos(wring[k])) - origin), wallNormal));
                    if (a > best)
                    {
                        best = a;
                        third = k;
                    }
                }

                (double S, double T) St(D p) => (D.Dot(p - origin, axisU), D.Dot(p - origin, axisV));
                (double s1, double t1) = St(D.Of(mesh.Pos(wring[1])));
                (double s2, double t2) = St(D.Of(mesh.Pos(wring[third])));
                double det = (s1 * t2) - (s2 * t1);
                if (!(Math.Abs(det) > 1e-12))
                {
                    continue;
                }

                float Mapped(double c0, double c1, double c2, D p)
                {
                    double ds = (((c1 - c0) * t2) - ((c2 - c0) * t1)) / det;
                    double dt = ((s1 * (c2 - c0)) - (s2 * (c1 - c0))) / det;
                    (double s, double t) = St(p);
                    return (float)(c0 + (ds * s) + (dt * t));
                }

                touched.Add(room);
                CsgFace copy = wall.Src.CloneAttributes();
                copy.IsPortal = false;
                copy.PortalIndexPlus2 = 0;
                copy.RepairFill = true;
                Uv w0 = wall.Uvs[0];
                Uv w1 = wall.Uvs[1];
                Uv w2 = wall.Uvs[third];
                var ring = new List<int>(loop.Vertices);
                var uvs = new List<Uv>(ring.Count);
                for (int k = 0; k < ring.Count; k++)
                {
                    uvs.Add(new Uv(Mapped(w0.U, w1.U, w2.U, points[k]), Mapped(w0.V, w1.V, w2.V, points[k])));
                }

                var face = new MFace(copy, ring, uvs, room) { Edited = true };
                int added = mesh.Add(face);
                if (!mesh.ComputePlaneAndBox(added))
                {
                    mesh.Remove(added);
                    continue;
                }

                face.Plane = wall.Plane;
                fills++;
            }
        }

        return fills;
    }

    // ───────────────────────────── room boxes ─────────────────────────────

    /// <summary>A room's box from its member faces (Glacier's convention: portal faces excluded).</summary>
    private static void RecomputeRoomBox(Mesh mesh, int room)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            MFace face = mesh.Faces[f];
            if (!face.Alive || face.Room != room || face.IsPortal)
            {
                continue;
            }

            mesh.Box(f, out Vec3 a, out Vec3 b);
            mn = Vec3Math.Min(mn, a);
            mx = Vec3Math.Max(mx, b);
            any = true;
        }

        if (any)
        {
            mesh.Rooms[room].Aabb = new Aabb(mn, mx);
        }
    }

    private static void RecomputeRoomBox(List<CsgFace> faces, List<Room> rooms, int room)
    {
        if (room < 0 || room >= rooms.Count)
        {
            return;
        }

        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        foreach (CsgFace f in faces)
        {
            if (f.RoomIndex != room || f.IsPortal)
            {
                continue;
            }

            f.GrowAabb(ref mn, ref mx);
            any = true;
        }

        if (any)
        {
            rooms[room].Aabb = new Aabb(mn, mx);
        }
    }
}
