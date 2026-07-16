using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Merges the coplanar leaf-boundary portals of ONE source brush face back into maximal convex faces
/// (qbsp's <c>MergeFaceList</c>). Leaf-based extraction routes every boundary through the single world
/// partition, which subdivides each original face at every node plane that crosses it — so a flat wall
/// comes out as many coplanar convex slivers. Left unmerged that is a 2–3× face-count over-split, and
/// the internal split edges are collinear T-junctions the hole detector flags (and the sliver drop turns
/// into gaps). Merging two coplanar convex pieces of the SAME source face that share an edge, whenever the
/// union stays convex, restores near-original faces: it removes the internal seams entirely (they become
/// interior to one polygon) and keeps every real CSG boundary (a piece only merges with its own kin, never
/// across a genuine survival boundary). Pieces of DIFFERENT source faces are left distinct, matching RED's
/// per-face records. Any collinear vertex a neighbour still needs is re-inserted by <see cref="TJointFixer"/>.
/// </summary>
internal static class CoplanarMerger
{
    private const float PosEps = 1e-3f;    // shared-vertex match (extracted corners are registry-exact)
    private const float ConvexEps = 1e-3f; // a merged join may not turn back on itself beyond this

    public static List<CsgFace> Merge(List<CsgFace> faces)
    {
        var groups = new Dictionary<(int, int, int, int), List<CsgFace>>();
        foreach (CsgFace f in faces)
        {
            (int, int, int, int) key = Key(f);
            if (!groups.TryGetValue(key, out List<CsgFace>? list))
            {
                groups[key] = list = new List<CsgFace>();
            }

            list.Add(f);
        }

        var result = new List<CsgFace>(faces.Count);
        foreach (List<CsgFace> group in groups.Values)
        {
            MergeGroup(group, result);
        }

        return result;
    }

    /// <summary>Grouping key: same source face id and same emitted plane orientation ⇒ mergeable kin.</summary>
    private static (int, int, int, int) Key(CsgFace f)
    {
        Vec3 n = f.Plane.Normal;
        return (
            f.FaceId,
            (int)MathF.Round(n.X * 512f),
            (int)MathF.Round(n.Y * 512f),
            (int)MathF.Round(n.Z * 512f));
    }

    private static void MergeGroup(List<CsgFace> group, List<CsgFace> result)
    {
        if (group.Count == 1)
        {
            result.Add(group[0]);
            return;
        }

        var polys = new List<CsgFace>(group);
        bool changed = true;
        while (changed && polys.Count > 1)
        {
            changed = false;
            for (int i = 0; i < polys.Count && !changed; i++)
            {
                for (int j = i + 1; j < polys.Count; j++)
                {
                    if (TryMergePair(polys[i], polys[j], out CsgFace? merged))
                    {
                        polys[i] = merged!;
                        polys.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }
        }

        // Collinear vertices on a merged face's boundary are intentionally KEPT: a perpendicular neighbour's
        // corner sits at such a point, so keeping it makes the shared edge match by construction (removing it
        // would create a T-junction and depend on TJointFixer to re-add it). Merging already removed the
        // INTERNAL seams (they are interior to one polygon now); the outer boundary must stay vertex-complete.
        result.AddRange(polys);
    }

    /// <summary>
    /// Merges two coplanar convex faces sharing an edge into one, iff the union is convex. Finds the shared
    /// edge (a→b in <paramref name="p"/>, b→a in <paramref name="q"/>), splices <paramref name="q"/>'s far
    /// vertices in place of that edge, then verifies convexity of the result.
    /// </summary>
    private static bool TryMergePair(CsgFace p, CsgFace q, out CsgFace? merged)
    {
        merged = null;
        List<CsgVertex> pv = p.Vertices;
        List<CsgVertex> qv = q.Vertices;
        int pn = pv.Count, qm = qv.Count;
        if (pn < 3 || qm < 3)
        {
            return false;
        }

        for (int i = 0; i < pn; i++)
        {
            Vec3 a = pv[i].Position;
            Vec3 b = pv[(i + 1) % pn].Position;
            for (int j = 0; j < qm; j++)
            {
                Vec3 c = qv[j].Position;
                Vec3 d = qv[(j + 1) % qm].Position;
                if (!Approx(a, d) || !Approx(b, c))
                {
                    continue; // not the shared edge (must be reversed: q edge is b→a)
                }

                var verts = new List<CsgVertex>(pn + qm - 2);
                for (int k = 1; k <= pn; k++)
                {
                    verts.Add(pv[(i + k) % pn]); // b … a  (all of p, shared edge last)
                }

                for (int k = 2; k < qm; k++)
                {
                    verts.Add(qv[(j + k) % qm]); // q vertices strictly between a and b
                }

                if (!IsConvex(verts, p.Plane.Normal))
                {
                    return false; // union would be concave — keep the pieces separate
                }

                if (HasRepeatedVertex(verts))
                {
                    // A simple polygon never repeats a position. A repeat means the union closes around a
                    // hole / touches itself through a zero-width bridge (exactly-collinear doubling-back
                    // passes the convexity turn test with zero cross products) — merging would emit a
                    // self-overlapping monster polygon (measured on dm04's y=−60.18 floor: a ~200-vertex
                    // face z-fighting its own kin). Keep the pieces separate.
                    return false;
                }

                CsgFace f = p.CloneAttributes();
                f.Vertices = verts;
                merged = f;
                return true;
            }
        }

        return false;
    }

    /// <summary>True when every turn of the loop bends the same way (convex) w.r.t. the face normal.</summary>
    private static bool IsConvex(List<CsgVertex> verts, Vec3 n)
    {
        int m = verts.Count;
        if (m < 3)
        {
            return false;
        }

        for (int i = 0; i < m; i++)
        {
            Vec3 prev = verts[(i + m - 1) % m].Position;
            Vec3 cur = verts[i].Position;
            Vec3 next = verts[(i + 1) % m].Position;
            float turn = cur.Sub(prev).Cross(next.Sub(cur)).Dot(n);
            if (turn < -ConvexEps)
            {
                return false; // a right turn on a CCW loop ⇒ reflex vertex ⇒ concave
            }
        }

        return true;
    }

    /// <summary>True when any two vertices of the loop share a (quantized) position — a self-touching union.</summary>
    private static bool HasRepeatedVertex(List<CsgVertex> verts)
    {
        var seen = new HashSet<(int, int, int)>(verts.Count);
        foreach (CsgVertex v in verts)
        {
            Vec3 p = v.Position;
            var key = (
                (int)MathF.Round(p.X / PosEps),
                (int)MathF.Round(p.Y / PosEps),
                (int)MathF.Round(p.Z / PosEps));
            if (!seen.Add(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Approx(Vec3 a, Vec3 b) => a.Sub(b).LengthSquared() <= PosEps * PosEps;

    // ---- Robust (T-junction-tolerant) coplanar union -----------------------------------------------
    // The exact-edge Merge above only joins two pieces that share a whole edge (same two endpoints).
    // Leaf extraction / surface clipping route a face through the world partition, so its coplanar
    // fragments meet at T-junctions (one piece's vertex sits mid-edge on its neighbour) and never
    // share a whole edge — so the exact merger leaves them split (the liquid surface: 693 slivers per
    // side merge only to ~164). MergeRobust first makes every piece vertex-complete along its
    // boundary (insert each neighbour's on-edge vertex), turning every adjacency into exact
    // anti-parallel edge pairs, then unions two pieces by CANCELLING their shared boundary and
    // re-tracing the surviving edges into one loop — accepted only when that loop is a single simple
    // CONVEX polygon (never a hole, a pinch, or a reflex L). Same FaceId grouping as Merge, so a piece
    // only ever unions with its own source-face kin (RED keeps distinct source faces as distinct
    // records) and the shared UV basis stays consistent.

    private const float QStep = 1e-4f;         // 0.1 mm vertex-identity grid for edge cancellation
    private const float ColinearPerp = 6e-4f;  // max perpendicular distance to treat a vertex as on an edge

    /// <param name="insertTJunctions">When true, first make every piece vertex-complete along its boundary
    /// so T-junction-adjacent pieces can union (needed for the surface-clip slivers, which meet at
    /// T-junctions). When false, only pieces that already share a whole edge union, and no boundary vertex
    /// is ever moved — the safe setting for solid geometry, where a moved boundary vertex could open a seam
    /// against an external (different-FaceId) neighbour.</param>
    public static List<CsgFace> MergeRobust(List<CsgFace> faces, bool insertTJunctions = true)
    {
        var groups = new Dictionary<(int, int, int, int), List<CsgFace>>();
        foreach (CsgFace f in faces)
        {
            (int, int, int, int) key = Key(f);
            if (!groups.TryGetValue(key, out List<CsgFace>? list))
            {
                groups[key] = list = new List<CsgFace>();
            }

            list.Add(f);
        }

        var result = new List<CsgFace>(faces.Count);
        foreach (List<CsgFace> group in groups.Values)
        {
            MergeGroupRobust(group, result, insertTJunctions);
        }

        return result;
    }

    private static void MergeGroupRobust(List<CsgFace> group, List<CsgFace> result, bool insertTJunctions)
    {
        if (group.Count == 1)
        {
            result.Add(group[0]);
            return;
        }

        Vec3 n = group[0].Plane.Normal;

        // Phase 1 — collect all distinct group vertex positions into a coarse spatial grid, then make
        // every polygon vertex-complete: insert each on-edge (collinear, interior) group vertex into
        // its boundary. The grid keeps the insertion near-linear (each edge tests only nearby points),
        // so a several-thousand-fragment liquid group stays fast.
        var grid = new Dictionary<(int, int, int), List<Vec3>>();
        var seenPos = new HashSet<(long, long, long)>();
        foreach (CsgFace f in group)
        {
            foreach (CsgVertex v in f.Vertices)
            {
                if (seenPos.Add(QKey(v.Position)))
                {
                    (int, int, int) cell = Cell(v.Position);
                    if (!grid.TryGetValue(cell, out List<Vec3>? bucket))
                    {
                        grid[cell] = bucket = new List<Vec3>();
                    }

                    bucket.Add(v.Position);
                }
            }
        }

        var polys = new List<List<CsgVertex>?>(group.Count);
        foreach (CsgFace f in group)
        {
            if (f.Vertices.Count >= 3)
            {
                List<CsgVertex> pc = insertTJunctions ? InsertColinear(f.Vertices, grid) : new List<CsgVertex>(f.Vertices);
                if (pc.Count >= 3)
                {
                    polys.Add(pc);
                }
            }
        }

        // Phase 2 — union via shared-edge cancellation, driven by an edge→owner index and a work queue
        // (near-linear: a merge only touches the two incident pieces, and each piece is re-queued only
        // after it actually grows).
        var edgeOwner = new Dictionary<((long, long, long), (long, long, long)), int>();
        for (int i = 0; i < polys.Count; i++)
        {
            AddEdges(edgeOwner, polys[i]!, i);
        }

        var queue = new Queue<int>();
        var queued = new bool[polys.Count];
        for (int i = 0; i < polys.Count; i++)
        {
            queue.Enqueue(i);
            queued[i] = true;
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            queued[i] = false;
            List<CsgVertex>? pi = polys[i];
            if (pi is null)
            {
                continue;
            }

            int m = pi.Count;
            bool mergedAny = false;
            for (int k = 0; k < m; k++)
            {
                var rev = (QKey(pi[(k + 1) % m].Position), QKey(pi[k].Position));
                if (!edgeOwner.TryGetValue(rev, out int j) || j == i || polys[j] is null)
                {
                    continue;
                }

                if (!TryUnion(pi, polys[j]!, n, out List<CsgVertex>? merged))
                {
                    continue;
                }

                RemoveEdges(edgeOwner, pi, i);
                RemoveEdges(edgeOwner, polys[j]!, j);
                polys[j] = null;
                polys[i] = pi = merged!;
                AddEdges(edgeOwner, pi, i);
                m = pi.Count;
                mergedAny = true;
                k = -1; // restart the edge scan on the grown polygon
            }

            // Growing i changed the neighbour that owns each of i's boundary edges; a piece that could
            // not merge with the smaller i (or was blocked by a since-consumed neighbour) may now form a
            // convex union with it. Re-queue i's current neighbours so no merge is missed.
            if (mergedAny)
            {
                int mm = pi.Count;
                for (int k = 0; k < mm; k++)
                {
                    var rev = (QKey(pi[(k + 1) % mm].Position), QKey(pi[k].Position));
                    if (edgeOwner.TryGetValue(rev, out int j) && polys[j] is not null && !queued[j])
                    {
                        queued[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }
        }

        CsgFace proto = group[0];
        foreach (List<CsgVertex>? poly in polys)
        {
            if (poly is null)
            {
                continue;
            }

            CsgFace f = proto.CloneAttributes();
            f.Vertices = poly;
            result.Add(f);
        }
    }

    private static void AddEdges(
        Dictionary<((long, long, long), (long, long, long)), int> map, List<CsgVertex> poly, int owner)
    {
        int m = poly.Count;
        for (int i = 0; i < m; i++)
        {
            map[(QKey(poly[i].Position), QKey(poly[(i + 1) % m].Position))] = owner;
        }
    }

    private static void RemoveEdges(
        Dictionary<((long, long, long), (long, long, long)), int> map, List<CsgVertex> poly, int owner)
    {
        int m = poly.Count;
        for (int i = 0; i < m; i++)
        {
            var e = (QKey(poly[i].Position), QKey(poly[(i + 1) % m].Position));
            if (map.TryGetValue(e, out int o) && o == owner)
            {
                map.Remove(e);
            }
        }
    }

    private static (int, int, int) Cell(Vec3 p) => (
        (int)MathF.Floor(p.X * 2f),
        (int)MathF.Floor(p.Y * 2f),
        (int)MathF.Floor(p.Z * 2f));

    /// <summary>Inserts every group vertex that lies strictly interior to a boundary edge (collinear
    /// within <see cref="ColinearPerp"/>), so a neighbour's mid-edge corner becomes a shared vertex.
    /// Candidate positions are pulled from the coarse grid cells the edge's AABB overlaps.</summary>
    private static List<CsgVertex> InsertColinear(List<CsgVertex> verts, Dictionary<(int, int, int), List<Vec3>> grid)
    {
        int n = verts.Count;
        var outv = new List<CsgVertex>(n + 4);
        for (int i = 0; i < n; i++)
        {
            CsgVertex a = verts[i];
            CsgVertex b = verts[(i + 1) % n];
            outv.Add(a);

            Vec3 ab = b.Position.Sub(a.Position);
            float abLen2 = ab.LengthSquared();
            if (abLen2 < 1e-12f)
            {
                continue;
            }

            List<(float T, Vec3 P)>? hits = null;
            int cx0 = (int)MathF.Floor(MathF.Min(a.Position.X, b.Position.X) * 2f) - 1;
            int cx1 = (int)MathF.Floor(MathF.Max(a.Position.X, b.Position.X) * 2f) + 1;
            int cy0 = (int)MathF.Floor(MathF.Min(a.Position.Y, b.Position.Y) * 2f) - 1;
            int cy1 = (int)MathF.Floor(MathF.Max(a.Position.Y, b.Position.Y) * 2f) + 1;
            int cz0 = (int)MathF.Floor(MathF.Min(a.Position.Z, b.Position.Z) * 2f) - 1;
            int cz1 = (int)MathF.Floor(MathF.Max(a.Position.Z, b.Position.Z) * 2f) + 1;
            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        if (!grid.TryGetValue((cx, cy, cz), out List<Vec3>? bucket))
                        {
                            continue;
                        }

                        foreach (Vec3 p in bucket)
                        {
                            Vec3 ap = p.Sub(a.Position);
                            float t = ap.Dot(ab) / abLen2;
                            if (t <= 1e-3f || t >= 1f - 1e-3f)
                            {
                                continue; // at or past an endpoint
                            }

                            Vec3 proj = a.Position.Add(ab.Scale(t));
                            if (proj.Sub(p).LengthSquared() > ColinearPerp * ColinearPerp)
                            {
                                continue; // off the edge line
                            }

                            (hits ??= new List<(float, Vec3)>()).Add((t, p));
                        }
                    }
                }
            }

            if (hits is null)
            {
                continue;
            }

            hits.Sort(static (x, y) => x.T.CompareTo(y.T));
            foreach ((float t, Vec3 p) in hits)
            {
                // Snap the corner onto the neighbour's exact position (identity for cancellation) but
                // interpolate its UV along this edge (same texture basis ⇒ affine ⇒ exact).
                Uv uv = CsgVertex.Lerp(a, b, t).Uv;
                outv.Add(new CsgVertex(p, uv));
            }
        }

        // Drop any consecutive duplicate corners the snap produced.
        var dedup = new List<CsgVertex>(outv.Count);
        for (int i = 0; i < outv.Count; i++)
        {
            if (dedup.Count > 0 && QKey(dedup[^1].Position).Equals(QKey(outv[i].Position)))
            {
                continue;
            }

            dedup.Add(outv[i]);
        }

        if (dedup.Count > 1 && QKey(dedup[0].Position).Equals(QKey(dedup[^1].Position)))
        {
            dedup.RemoveAt(dedup.Count - 1);
        }

        return dedup;
    }

    /// <summary>Unions two coplanar polygons that share one or more anti-parallel boundary edges,
    /// by cancelling the shared edges and re-tracing the surviving boundary into a single simple
    /// convex loop. Returns false (keep separate) if no edge is shared, or the union is disconnected,
    /// self-touching, or non-convex.</summary>
    private static bool TryUnion(List<CsgVertex> p, List<CsgVertex> q, Vec3 n, out List<CsgVertex>? merged)
    {
        merged = null;

        var rep = new Dictionary<(long, long, long), CsgVertex>();
        foreach (CsgVertex v in p)
        {
            rep[QKey(v.Position)] = v;
        }

        foreach (CsgVertex v in q)
        {
            rep.TryAdd(QKey(v.Position), v);
        }

        List<((long, long, long) A, (long, long, long) B)> pe = Edges(p);
        List<((long, long, long) A, (long, long, long) B)> qe = Edges(q);
        var qset = new HashSet<((long, long, long), (long, long, long))>();
        foreach (var e in qe)
        {
            qset.Add((e.A, e.B));
        }

        var cancelledQ = new HashSet<((long, long, long), (long, long, long))>();
        var survivors = new List<((long, long, long) A, (long, long, long) B)>(pe.Count + qe.Count);
        int shared = 0;
        foreach (var e in pe)
        {
            var rev = (e.B, e.A);
            if (qset.Contains(rev))
            {
                cancelledQ.Add(rev);
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

        foreach (var e in qe)
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

        // Re-trace: each vertex must have exactly one outgoing survivor edge (else the union is a
        // pinch or has a hole).
        var startMap = new Dictionary<(long, long, long), (long, long, long)>(survivors.Count);
        foreach (var e in survivors)
        {
            if (!startMap.TryAdd(e.A, e.B))
            {
                return false;
            }
        }

        var loop = new List<(long, long, long)>(survivors.Count);
        (long, long, long) start = survivors[0].A;
        (long, long, long) cur = start;
        int guard = 0;
        do
        {
            loop.Add(cur);
            if (!startMap.TryGetValue(cur, out (long, long, long) nxt))
            {
                return false;
            }

            cur = nxt;
            if (++guard > survivors.Count + 1)
            {
                return false;
            }
        }
        while (!cur.Equals(start));

        if (loop.Count != survivors.Count)
        {
            return false; // multiple disjoint loops ⇒ the union wraps a hole
        }

        var verts = new List<CsgVertex>(loop.Count);
        var seen = new HashSet<(long, long, long)>(loop.Count);
        foreach ((long, long, long) id in loop)
        {
            if (!seen.Add(id))
            {
                return false; // repeated vertex ⇒ self-touching
            }

            verts.Add(rep[id]);
        }

        if (verts.Count < 3 || !IsConvexSimple(verts, n))
        {
            return false;
        }

        merged = verts;
        return true;
    }

    /// <summary>
    /// Scale-independent convex-AND-simple test: every turn bends the SAME way (normalised by edge length,
    /// so a genuine reflex angle at a SHORT edge is not swallowed by an absolute epsilon as <see cref="IsConvex"/>
    /// does) AND the total turning is exactly one loop (±2π). The absolute-epsilon <see cref="IsConvex"/> let
    /// small reflex angles at the dense T-junction vertices of the liquid surface accumulate into a
    /// self-intersecting spiral whose vertices all turn the same way but wind more than 360° — a face whose
    /// fan area far exceeds its true (shoelace) area and renders as a gaping hole. This rejects such unions,
    /// keeping the merged liquid surface a clean convex decomposition like RED's compiled surface.
    /// </summary>
    private static bool IsConvexSimple(List<CsgVertex> verts, Vec3 n)
    {
        int m = verts.Count;
        if (m < 3)
        {
            return false;
        }

        double total = 0;
        int sign = 0;
        for (int i = 0; i < m; i++)
        {
            Vec3 prev = verts[(i + m - 1) % m].Position;
            Vec3 cur = verts[i].Position;
            Vec3 next = verts[(i + 1) % m].Position;
            Vec3 e0 = cur.Sub(prev);
            Vec3 e1 = next.Sub(cur);
            float l0 = e0.Length(), l1 = e1.Length();
            if (l0 < 1e-6f || l1 < 1e-6f)
            {
                continue; // degenerate edge — ignore
            }

            float sin = e0.Cross(e1).Dot(n) / (l0 * l1); // ~ sin(exterior angle)
            float cos = e0.Dot(e1) / (l0 * l1);           // ~ cos(exterior angle)
            double ang = System.Math.Atan2(sin, cos);
            if (System.Math.Abs(ang) < 1e-4)
            {
                continue; // straight-through (collinear) vertex
            }

            int s = ang > 0 ? 1 : -1;
            if (sign == 0)
            {
                sign = s;
            }
            else if (s != sign)
            {
                return false; // a turn reversal — the union is non-convex
            }

            total += ang;
        }

        return System.Math.Abs(System.Math.Abs(total) - (2 * System.Math.PI)) < 1e-2; // exactly one convex loop
    }

    private static List<((long, long, long), (long, long, long))> Edges(List<CsgVertex> poly)
    {
        int m = poly.Count;
        var e = new List<((long, long, long), (long, long, long))>(m);
        for (int i = 0; i < m; i++)
        {
            e.Add((QKey(poly[i].Position), QKey(poly[(i + 1) % m].Position)));
        }

        return e;
    }

    private static (long, long, long) QKey(Vec3 p) => (
        (long)MathF.Round(p.X / QStep),
        (long)MathF.Round(p.Y / QStep),
        (long)MathF.Round(p.Z / QStep));
}
