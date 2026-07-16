using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// TEST ORACLE — a clean-room reimplementation of Alpine Faction's <b>runtime material-debris
/// shatter</b>, the ONLY code path that emits the console warning
/// "[CapFace] Ear clip stuck: remaining=N of M" Goober saw digging a breakable brush. Built FROM
/// THE SPEC in <c>game_patch/misc/destruction.cpp</c> (<c>do_material_debris_shatter</c> →
/// <c>split_faces_by_plane</c> → <c>find_boundary_loops</c> → <c>add_cap_faces_from_loop</c> →
/// <c>ear_clip_triangulate</c>) — MPL source read for BEHAVIOUR ONLY, never copied.
/// <para>
/// Where <see cref="CapFaceEarClip"/> probes each compiled face's own loop (necessary but not
/// sufficient), this harness reproduces the RUNTIME loops: when a non-glass breakable detail room
/// is destroyed the game extracts its whole face set into a solid, repeatedly bisects it along the
/// longest bounding-box axis, and after every cut re-detects the open boundary (half-edges with no
/// mate, keyed by <b>shared vertex identity</b>) and caps it via ear clipping. A cap loop that the
/// game's ear clip cannot triangulate is exactly the stall.
/// </para>
/// <para>
/// KEY PROPERTY (measured, documented in parity notes): the bisection planes are chosen purely from
/// each chunk's bounding box — <b>independent of the dig sphere's position or radius</b>. The shatter,
/// and therefore every cap loop and every stall, is a deterministic function of the room's compiled
/// geometry alone. "Fuzzing dig positions" collapses to "enumerate every reachable room and shatter
/// it", which is what <see cref="ShatterRoom"/> does.
/// </para>
/// </summary>
internal static class CarveSimulation
{
    // Per-material DebrisConfig — every non-glass material in k_material_configs uses {0.5, 8, 3}.
    private const float MinBsphereRadius = 0.5f;   // stop subdividing below this bounding-sphere radius
    private const int MaxSubdivisions = 8;         // max boolean cuts
    private const int MinFacesToSplit = 3;         // min face count to attempt a split
    private const float SplitEps = 0.001f;         // split_faces_by_plane on-plane epsilon

    public readonly record struct StuckLoop(
        int RoomIndex, int RoomUid, int Cut, int Vertices, int Remaining, IReadOnlyList<Vec3> Loop);

    public sealed class Result
    {
        public int Rooms;
        public int Loops;
        public int Stuck;
        public int Degenerate;
        public int MaxRemaining;
        public readonly List<StuckLoop> Examples = new();
    }

    // ---- working solid model -------------------------------------------------------------------
    // Vertices are shared BY IDENTITY across a room's faces (the game shares GVertex*). We model
    // identity as an index into a shared pool: two faces meeting at a welded corner reference the
    // SAME id, which is exactly what lets find_boundary_loops chain edges across faces.

    private sealed class Pool
    {
        public readonly List<Vec3> Pos = new();
        private readonly Dictionary<int, int> _globalToLocal = new();

        public int FromGlobal(IReadOnlyList<Vec3> global, int gi)
        {
            if (_globalToLocal.TryGetValue(gi, out int li))
            {
                return li;
            }

            li = Pos.Count;
            Pos.Add(global[gi]);
            _globalToLocal[gi] = li;
            return li;
        }

        public int NewVertex(Vec3 p)
        {
            int id = Pos.Count;
            Pos.Add(p);
            return id;
        }
    }

    private sealed class SimFace
    {
        public readonly List<int> Verts;
        public Vec3 Normal;
        public float Offset;
        public int Group;

        public SimFace(List<int> verts, Vec3 normal, float offset)
        {
            Verts = verts;
            Normal = normal;
            Offset = offset;
        }
    }

    private sealed class SimSolid
    {
        public readonly Pool Pool;
        public List<SimFace> Faces = new();
        public Vec3 BBoxMin, BBoxMax, SphereCenter;
        public float SphereRadius;

        public SimSolid(Pool pool) => Pool = pool;
    }

    /// <summary>Shatter one room's compiled faces and return per-loop cap outcomes accumulated into
    /// <paramref name="result"/>. Faithful to <c>do_material_debris_shatter</c>'s bisection loop.</summary>
    public static void ShatterRoom(Geometry g, int roomIndex, Result result, int exampleCap = 8)
    {
        int roomUid = (roomIndex >= 0 && roomIndex < g.Rooms.Count) ? g.Rooms[roomIndex].Id : -1;
        var pool = new Pool();
        var root = new SimSolid(pool);

        foreach (Face f in g.Faces)
        {
            if (f.RoomIndex != roomIndex || f.IsPortalFace || f.Vertices.Count < 3)
            {
                continue;
            }

            var verts = new List<int>(f.Vertices.Count);
            foreach (FaceVertex fv in f.Vertices)
            {
                if (fv.Index >= 0 && fv.Index < g.Vertices.Count)
                {
                    verts.Add(pool.FromGlobal(g.Vertices, fv.Index));
                }
            }

            if (verts.Count < 3)
            {
                continue;
            }

            var n = new Vec3(f.Plane.Normal.X, f.Plane.Normal.Y, f.Plane.Normal.Z);
            root.Faces.Add(new SimFace(verts, n, f.Plane.Offset));
        }

        if (root.Faces.Count == 0)
        {
            return;
        }

        result.Rooms++;
        ComputeSolidBounds(root);

        var queue = new Queue<SimSolid>();
        queue.Enqueue(root);
        int totalCuts = 0;

        int groupCounter = 0;

        void Cap(SimSolid s, int cutIndex)
        {
            foreach (List<int> loop in FindBoundaryLoops(s))
            {
                var pts = loop.Select(id => s.Pool.Pos[id]).ToList();
                CapFaceEarClip.Probe p = CapFaceEarClip.ProbeLoop(pts);
                result.Loops++;
                if (p.Outcome == CapFaceEarClip.Outcome.Stuck)
                {
                    result.Stuck++;
                    result.MaxRemaining = Math.Max(result.MaxRemaining, p.Remaining);
                    if (result.Examples.Count < exampleCap)
                    {
                        result.Examples.Add(new StuckLoop(roomIndex, roomUid, cutIndex, p.Vertices, p.Remaining, pts));
                    }
                }
                else if (p.Outcome == CapFaceEarClip.Outcome.Degenerate)
                {
                    result.Degenerate++;
                }
            }
        }

        while (queue.Count > 0 && totalCuts < MaxSubdivisions)
        {
            SimSolid chunk = queue.Dequeue();

            if (chunk.SphereRadius <= MinBsphereRadius || chunk.Faces.Count < MinFacesToSplit)
            {
                continue; // final piece
            }

            Vec3 extents = chunk.BBoxMax.Sub(chunk.BBoxMin);
            Vec3 normal = extents.X >= extents.Y && extents.X >= extents.Z
                ? new Vec3(1f, 0f, 0f)
                : extents.Y >= extents.X && extents.Y >= extents.Z
                    ? new Vec3(0f, 1f, 0f)
                    : new Vec3(0f, 0f, 1f);
            float offset = -normal.Dot(chunk.SphereCenter);

            int posGroup = groupCounter++;
            int negGroup = groupCounter++;
            (int posCount, int negCount) = SplitFacesByPlane(chunk, normal, offset, posGroup, negGroup);

            if (posCount == 0 || negCount == 0)
            {
                continue; // couldn't split — final piece
            }

            SimSolid piece = ExtractByGroup(chunk, negGroup);
            totalCuts++;

            if (piece.Faces.Count > 0)
            {
                Cap(piece, totalCuts);
                ComputeSolidBounds(piece);
                queue.Enqueue(piece);
            }

            Cap(chunk, totalCuts);      // pos-side faces remain in chunk
            ComputeSolidBounds(chunk);
            queue.Enqueue(chunk);
        }
    }

    private static void ComputeSolidBounds(SimSolid s)
    {
        var vmin = new Vec3(1e18f, 1e18f, 1e18f);
        var vmax = new Vec3(-1e18f, -1e18f, -1e18f);
        foreach (SimFace f in s.Faces)
        {
            foreach (int id in f.Verts)
            {
                Vec3 p = s.Pool.Pos[id];
                vmin = Vec3Math.Min(vmin, p);
                vmax = Vec3Math.Max(vmax, p);
            }
        }

        s.BBoxMin = vmin;
        s.BBoxMax = vmax;
        s.SphereCenter = vmin.Add(vmax).Scale(0.5f);
        Vec3 d = vmax.Sub(vmin);
        s.SphereRadius = MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z)) * 0.5f;
    }

    /// <summary>Faithful port of <c>split_faces_by_plane</c>: faces wholly on one side get a group tag;
    /// straddling faces are clipped into two group-tagged sub-faces sharing cached intersection
    /// vertices (by endpoint identity) with the removed original.</summary>
    private static (int Pos, int Neg) SplitFacesByPlane(
        SimSolid solid, Vec3 planeNormal, float planeOffset, int posGroup, int negGroup)
    {
        int posCount = 0, negCount = 0;
        var intersectionCache = new Dictionary<(int, int), int>();
        List<SimFace> snapshot = solid.Faces.ToList();
        var added = new List<SimFace>();
        var removed = new HashSet<SimFace>();

        foreach (SimFace face in snapshot)
        {
            int n = face.Verts.Count;
            var dist = new float[n];
            bool hasPos = false, hasNeg = false;
            for (int i = 0; i < n; i++)
            {
                float dd = planeNormal.Dot(solid.Pool.Pos[face.Verts[i]]) + planeOffset;
                dist[i] = dd;
                if (dd > SplitEps)
                {
                    hasPos = true;
                }

                if (dd < -SplitEps)
                {
                    hasNeg = true;
                }
            }

            if (!hasNeg)
            {
                face.Group = posGroup;
                posCount++;
                continue;
            }

            if (!hasPos)
            {
                face.Group = negGroup;
                negCount++;
                continue;
            }

            var posVerts = new List<int>();
            var negVerts = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int vi = face.Verts[i];
                int vj = face.Verts[j];

                if (dist[i] >= -SplitEps)
                {
                    posVerts.Add(vi);
                }

                if (dist[i] <= SplitEps)
                {
                    negVerts.Add(vi);
                }

                bool crosses = (dist[i] > SplitEps && dist[j] < -SplitEps) ||
                               (dist[i] < -SplitEps && dist[j] > SplitEps);
                if (crosses)
                {
                    float t = dist[i] / (dist[i] - dist[j]);
                    (int, int) key = vi < vj ? (vi, vj) : (vj, vi);
                    if (!intersectionCache.TryGetValue(key, out int shared))
                    {
                        Vec3 pi = solid.Pool.Pos[vi];
                        Vec3 pj = solid.Pool.Pos[vj];
                        shared = solid.Pool.NewVertex(Vec3Math.Lerp(pi, pj, t));
                        intersectionCache[key] = shared;
                    }

                    posVerts.Add(shared);
                    negVerts.Add(shared);
                }
            }

            if (posVerts.Count >= 3)
            {
                added.Add(new SimFace(posVerts, face.Normal, face.Offset) { Group = posGroup });
                posCount++;
            }

            if (negVerts.Count >= 3)
            {
                added.Add(new SimFace(negVerts, face.Normal, face.Offset) { Group = negGroup });
                negCount++;
            }

            removed.Add(face);
        }

        if (removed.Count > 0 || added.Count > 0)
        {
            var next = new List<SimFace>(solid.Faces.Count - removed.Count + added.Count);
            foreach (SimFace f in solid.Faces)
            {
                if (!removed.Contains(f))
                {
                    next.Add(f);
                }
            }

            next.AddRange(added);
            solid.Faces = next;
        }

        return (posCount, negCount);
    }

    private static SimSolid ExtractByGroup(SimSolid parent, int group)
    {
        var piece = new SimSolid(parent.Pool);
        var kept = new List<SimFace>();
        foreach (SimFace f in parent.Faces)
        {
            if (f.Group == group)
            {
                piece.Faces.Add(f);
            }
            else
            {
                kept.Add(f);
            }
        }

        parent.Faces = kept;
        return piece;
    }

    /// <summary>Faithful port of <c>find_boundary_loops</c>: directed half-edges (by vertex identity),
    /// a boundary half-edge is one whose reverse is absent, chained into closed loops. Loop START is
    /// picked in sorted-id order so the harness is deterministic (the game's unordered_map start order
    /// does not change the set of loops nor any ear-clip stall).</summary>
    private static List<List<int>> FindBoundaryLoops(SimSolid solid)
    {
        var halfEdges = new HashSet<(int, int)>();
        foreach (SimFace f in solid.Faces)
        {
            int n = f.Verts.Count;
            for (int i = 0; i < n; i++)
            {
                halfEdges.Add((f.Verts[i], f.Verts[(i + 1) % n]));
            }
        }

        var nextInBoundary = new Dictionary<int, int>();
        foreach ((int a, int b) in halfEdges.OrderBy(e => e.Item1).ThenBy(e => e.Item2))
        {
            if (!halfEdges.Contains((b, a)))
            {
                nextInBoundary[a] = b; // sorted iteration ⇒ deterministic last-write-wins
            }
        }

        var visited = new HashSet<int>();
        var loops = new List<List<int>>();
        foreach (int startV in nextInBoundary.Keys.OrderBy(k => k))
        {
            if (visited.Contains(startV))
            {
                continue;
            }

            var loop = new List<int>();
            int curr = startV;
            while (true)
            {
                if (visited.Contains(curr))
                {
                    break;
                }

                visited.Add(curr);
                loop.Add(curr);
                if (!nextInBoundary.TryGetValue(curr, out int nxt))
                {
                    break;
                }

                curr = nxt;
                if (curr == startV)
                {
                    break;
                }
            }

            if (loop.Count >= 3)
            {
                loops.Add(loop);
            }
        }

        return loops;
    }
}
