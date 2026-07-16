using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Output-stage coplanar merge (flagship 22). The incremental fold splits each authored face in place at
/// every accumulated boundary, so a flat wall reaches the output as many coplanar convex slivers that all
/// share one source-face id — dmabrupt emits +42% faces vs RED, inflating every room's per-room face count
/// (RF batches faces per room, and a runaway per-room count is a prime suspect for the in-game
/// "things disappearing"). This pass re-merges each source face's coplanar kin back into maximal convex
/// faces.
/// <para>
/// Run AFTER the t-joint / seam passes and the room flood, and keyed on the SHARED VERTEX-POOL INDICES the
/// faces already reference (exactly the identity <see cref="HoleDetector"/> uses). Two kin faces union only
/// across an edge (a,b)/(b,a) that both reference by pool index; cancelling it removes an edge used by exactly
/// two faces, so no single-use (open) edge count can change — the merge is watertight by construction (verified
/// hole-neutral corpus-wide). No vertex is created or moved; the merged loop reuses the existing pool indices.
/// Only same-id kin in the same room union (RED keeps distinct source faces as distinct records, and one
/// source face has one UV basis so texture continuity is preserved); portal and liquid faces are left
/// untouched (portal records/markers; the water surface is merged separately).
/// </para>
/// </summary>
internal static class OutputFaceMerger
{
    private const float ConvexEps = 1e-3f;

    /// <summary>Merges coplanar kin in place, rewriting <paramref name="faces"/> and
    /// <paramref name="poolIndices"/> (parallel) to the merged set. Returns the number of faces removed.</summary>
    public static int Merge(List<CsgFace> faces, List<int[]> poolIndices, List<Vec3> pool)
    {
        // Global undirected edge histogram over the faces HoleDetector counts (non-portal, non-detail,
        // non-liquid). A shared edge may be cancelled ONLY when its count here is exactly 2 — i.e. it is a
        // clean manifold seam between the two kin, not a spot where a third face also references the edge
        // (a T-junction / coincidence). Cancelling a count-2 edge takes it to 0, so it can never leave a
        // single-use (open) edge: the merge is watertight by construction. Faces HoleDetector ignores (detail)
        // are not in this histogram and merge freely.
        var edgeHist = new Dictionary<(int, int), int>();
        foreach (int fi2 in System.Linq.Enumerable.Range(0, faces.Count))
        {
            CsgFace f = faces[fi2];
            if (!HoleCounted(f))
            {
                continue;
            }

            int[] idx = poolIndices[fi2];
            for (int i = 0; i < idx.Length; i++)
            {
                int a = idx[i];
                int b = idx[(i + 1) % idx.Length];
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                edgeHist[key] = edgeHist.GetValueOrDefault(key) + 1;
            }
        }

        // Partition: carry portal / liquid / mover faces through untouched; group the rest by
        // (source-face id, room) — kin of one authored face in one room.
        var carriedF = new List<CsgFace>();
        var carriedI = new List<int[]>();
        var groups = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < faces.Count; i++)
        {
            CsgFace f = faces[i];
            bool mergeable = !f.IsPortal
                && f.PortalIndexPlus2 < 2
                && (f.Flags & (ushort)FaceFlags.LiquidSurface) == 0
                && f.RoomIndex >= 0
                && f.Vertices.Count >= 3;
            if (!mergeable)
            {
                carriedF.Add(f);
                carriedI.Add(poolIndices[i]);
                continue;
            }

            (int, int) key = (f.FaceId, f.RoomIndex);
            if (!groups.TryGetValue(key, out List<int>? list))
            {
                groups[key] = list = new List<int>();
            }

            list.Add(i);
        }

        var outF = new List<CsgFace>(faces.Count);
        var outI = new List<int[]>(faces.Count);
        outF.AddRange(carriedF);
        outI.AddRange(carriedI);

        foreach (List<int> members in groups.Values)
        {
            MergeGroup(faces, poolIndices, pool, members, edgeHist, outF, outI);
        }

        int removed = faces.Count - outF.Count;
        faces.Clear();
        faces.AddRange(outF);
        poolIndices.Clear();
        poolIndices.AddRange(outI);
        return removed;
    }

    private static void MergeGroup(
        List<CsgFace> faces, List<int[]> poolIndices, List<Vec3> pool, List<int> members,
        Dictionary<(int, int), int> edgeHist, List<CsgFace> outF, List<int[]> outI)
    {
        if (members.Count == 1)
        {
            int only = members[0];
            outF.Add(faces[only]);
            outI.Add(poolIndices[only]);
            return;
        }

        CsgFace proto = faces[members[0]];
        Vec3 n = proto.Plane.Normal;

        // Solid (hole-counted) kin cancel only clean manifold seams (edgeHist == 2); detail kin, which
        // HoleDetector ignores, union freely.
        bool guarded = HoleCounted(proto);

        // Each polygon as a ring of (poolIndex, CsgVertex).
        var polys = new List<List<(int Idx, CsgVertex V)>?>(members.Count);
        foreach (int m in members)
        {
            int[] idx = poolIndices[m];
            List<CsgVertex> vs = faces[m].Vertices;
            if (idx.Length != vs.Count || vs.Count < 3)
            {
                // Defensive: keep as-is if the parallel arrays disagree.
                outF.Add(faces[m]);
                outI.Add(idx);
                continue;
            }

            var ring = new List<(int, CsgVertex)>(vs.Count);
            for (int i = 0; i < vs.Count; i++)
            {
                ring.Add((idx[i], vs[i]));
            }

            polys.Add(ring);
        }

        // Greedy union via shared pool-index edge cancellation, driven by an edge→owner index + work queue.
        var edgeOwner = new Dictionary<(int, int), int>();
        for (int i = 0; i < polys.Count; i++)
        {
            if (polys[i] is { } p)
            {
                AddEdges(edgeOwner, p, i);
            }
        }

        var queue = new Queue<int>();
        for (int i = 0; i < polys.Count; i++)
        {
            queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            List<(int Idx, CsgVertex V)>? pi = polys[i];
            if (pi is null)
            {
                continue;
            }

            bool mergedAny = false;
            for (int k = 0; k < pi.Count; k++)
            {
                var rev = (pi[(k + 1) % pi.Count].Idx, pi[k].Idx);
                if (!edgeOwner.TryGetValue(rev, out int j) || j == i || polys[j] is null)
                {
                    continue;
                }

                if (!TryUnion(pi, polys[j]!, pool, n, edgeHist, guarded, out List<(int, CsgVertex)>? merged))
                {
                    continue;
                }

                RemoveEdges(edgeOwner, pi, i);
                RemoveEdges(edgeOwner, polys[j]!, j);
                polys[j] = null;
                polys[i] = pi = merged!;
                AddEdges(edgeOwner, pi, i);
                mergedAny = true;
                k = -1;
            }

            if (mergedAny)
            {
                // Re-queue current neighbours so a piece blocked by a since-consumed neighbour is retried.
                for (int k = 0; k < pi.Count; k++)
                {
                    var rev = (pi[(k + 1) % pi.Count].Idx, pi[k].Idx);
                    if (edgeOwner.TryGetValue(rev, out int j) && polys[j] is not null)
                    {
                        queue.Enqueue(j);
                    }
                }
            }
        }

        foreach (List<(int Idx, CsgVertex V)>? poly in polys)
        {
            if (poly is null)
            {
                continue;
            }

            CsgFace f = proto.CloneAttributes();
            var vs = new List<CsgVertex>(poly.Count);
            var idx = new int[poly.Count];
            for (int i = 0; i < poly.Count; i++)
            {
                idx[i] = poly[i].Idx;
                vs.Add(poly[i].V);
            }

            f.Vertices = vs;
            outF.Add(f);
            outI.Add(idx);
        }
    }

    private static bool TryUnion(
        List<(int Idx, CsgVertex V)> p, List<(int Idx, CsgVertex V)> q, List<Vec3> pool, Vec3 n,
        Dictionary<(int, int), int> edgeHist, bool guarded,
        out List<(int, CsgVertex)>? merged)
    {
        merged = null;

        var rep = new Dictionary<int, CsgVertex>();
        foreach ((int Idx, CsgVertex V) e in p)
        {
            rep[e.Idx] = e.V;
        }

        foreach ((int Idx, CsgVertex V) e in q)
        {
            rep.TryAdd(e.Idx, e.V);
        }

        List<(int A, int B)> pe = Edges(p);
        List<(int A, int B)> qe = Edges(q);
        var qset = new HashSet<(int, int)>();
        foreach ((int A, int B) e in qe)
        {
            qset.Add((e.A, e.B));
        }

        var cancelledQ = new HashSet<(int, int)>();
        var survivors = new List<(int A, int B)>(pe.Count + qe.Count);
        int shared = 0;
        foreach ((int A, int B) e in pe)
        {
            var revv = (e.B, e.A);
            // Only cancel a clean manifold seam: the undirected edge must be used by exactly two hole-counted
            // faces globally (this p and this q). If a third face also references it (a T-junction / coincidence
            // spot), cancelling would strand that face's edge as a new open edge — so keep the pieces apart.
            var undirected = e.A < e.B ? (e.A, e.B) : (e.B, e.A);
            bool clean = !guarded || edgeHist.GetValueOrDefault(undirected) == 2;
            if (qset.Contains(revv) && clean)
            {
                cancelledQ.Add(revv);
                shared++;
            }
            else
            {
                survivors.Add(e);
            }
        }

        if (shared == 0)
        {
            return false;
        }

        foreach ((int A, int B) e in qe)
        {
            if (!cancelledQ.Contains((e.A, e.B)))
            {
                survivors.Add(e);
            }
        }

        if (survivors.Count < 3)
        {
            return false;
        }

        var startMap = new Dictionary<int, int>(survivors.Count);
        foreach ((int A, int B) e in survivors)
        {
            if (!startMap.TryAdd(e.A, e.B))
            {
                return false; // a vertex with two outgoing survivor edges ⇒ pinch / non-simple
            }
        }

        var loop = new List<int>(survivors.Count);
        int start = survivors[0].A;
        int cur = start;
        int guard = 0;
        do
        {
            loop.Add(cur);
            if (!startMap.TryGetValue(cur, out int nxt))
            {
                return false;
            }

            cur = nxt;
            if (++guard > survivors.Count + 1)
            {
                return false;
            }
        }
        while (cur != start);

        if (loop.Count != survivors.Count)
        {
            return false; // multiple disjoint loops ⇒ the union wraps a hole
        }

        var seen = new HashSet<int>(loop.Count);
        var verts = new List<(int, CsgVertex)>(loop.Count);
        foreach (int id in loop)
        {
            if (!seen.Add(id))
            {
                return false; // repeated vertex ⇒ self-touching
            }

            verts.Add((id, rep[id]));
        }

        if (verts.Count < 3 || !IsConvex(verts, pool, n))
        {
            return false;
        }

        merged = verts;
        return true;
    }

    /// <summary>Mirrors <see cref="HoleDetector"/>'s wall predicate: a face whose edges count toward the
    /// open-edge (watertightness) tally — a textured, non-portal, non-detail, non-liquid wall.</summary>
    private static bool HoleCounted(CsgFace f) =>
        !f.IsPortal
        && f.PortalIndexPlus2 < 2
        && !string.IsNullOrEmpty(f.Texture)
        && (f.Flags & (ushort)(FaceFlags.IsDetail | FaceFlags.LiquidSurface)) == 0;

    private static bool IsConvex(List<(int Idx, CsgVertex V)> verts, List<Vec3> pool, Vec3 n)
    {
        int m = verts.Count;
        for (int i = 0; i < m; i++)
        {
            Vec3 prev = verts[(i + m - 1) % m].V.Position;
            Vec3 cur = verts[i].V.Position;
            Vec3 next = verts[(i + 1) % m].V.Position;
            float turn = cur.Sub(prev).Cross(next.Sub(cur)).Dot(n);
            if (turn < -ConvexEps)
            {
                return false;
            }
        }

        return true;
    }

    private static List<(int A, int B)> Edges(List<(int Idx, CsgVertex V)> poly)
    {
        int m = poly.Count;
        var e = new List<(int, int)>(m);
        for (int i = 0; i < m; i++)
        {
            e.Add((poly[i].Idx, poly[(i + 1) % m].Idx));
        }

        return e;
    }

    private static void AddEdges(Dictionary<(int, int), int> map, List<(int Idx, CsgVertex V)> poly, int owner)
    {
        int m = poly.Count;
        for (int i = 0; i < m; i++)
        {
            map[(poly[i].Idx, poly[(i + 1) % m].Idx)] = owner;
        }
    }

    private static void RemoveEdges(Dictionary<(int, int), int> map, List<(int Idx, CsgVertex V)> poly, int owner)
    {
        int m = poly.Count;
        for (int i = 0; i < m; i++)
        {
            var e = (poly[i].Idx, poly[(i + 1) % m].Idx);
            if (map.TryGetValue(e, out int o) && o == owner)
            {
                map.Remove(e);
            }
        }
    }
}
