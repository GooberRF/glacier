using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Watertight seam sealing — RED's t-joint fixer (<c>FUN_004975a0</c> → per-face
/// <c>FUN_00497410</c> → insertion <c>FUN_004972e0</c>), reproduced from the binary and
/// generalised only to GED's numerical scale.
/// <para><b>What the binary does (verified, RED.exe 1.20na, ghidraRF).</b> RED reaches
/// watertightness in two steps that GED's independent exact per-face splitter does not: (1)
/// phase-3 clips every spanning face against the OTHER solid's shared BSP tree
/// (<c>FUN_004a8220</c> → <c>FUN_0048bec0</c>), so adjacent faces split along the SAME node
/// planes and their vertices coincide by construction; (2) a t-joint fixer inserts a vertex
/// where a neighbour vertex lies ON a face's edge. That insertion (<c>FUN_004972e0</c>) is
/// gated by <c>FUN_0048a790 == 2</c> — the candidate must lie on the target face's plane within
/// <c>_DAT_00554714 = 0x38d1b717 = 1e-4</c> — and, for each edge, requires the candidate to
/// project strictly interior (<c>1e-4 &lt; t &lt; len − 1e-4</c>) AND to sit within <c>1e-4</c>
/// perpendicular of the edge. RED uses the SAME <c>1e-4</c> everywhere and never welds larger
/// gaps: watertightness falls out of the shared split, not out of gap-bridging.</para>
/// <para><b>What GED does here.</b> GED keeps its exact per-face splitter (robust, no BSP
/// fragment explosion), which leaves should-be-coincident vertices up to a fraction of a
/// millimetre apart on coplanar-heavy / bumpy terrain (float divergence of two exact
/// intersections computed from different plane pairs at 10²-metre coordinates). This pass is
/// RED's t-joint insertion rule — coplanar-gated, interior-margin, perpendicular test — plus a
/// coincidence weld, both run to a fixed point on the OPEN edges of the wall mesh (RED's
/// original is watertight there, so every open edge is a GED stitching artefact). The one
/// deliberate deviation from the binary is the tolerance: <see cref="Tol"/> is <b>10×</b> RED's
/// <c>1e-4</c> to absorb GED's exact-arithmetic divergence — still sub-millimetre, well below
/// any authored feature (e.g. dm01's real 2 cm wall lip is left un-welded), so it reconciles
/// GED's representation toward RED's coincident result without bridging a real gap. Closing the
/// remaining leaks needs the shared-BSP split itself (a characterised, higher-risk core change),
/// not a looser weld.</para>
/// </summary>
public static class SeamSealer
{
    /// <summary>
    /// On-edge / coincidence tolerance. RED's t-joint fixer uses <c>_DAT_00554714 = 1e-4</c>
    /// (RED.exe 0x00554714) throughout; GED relaxes to 10× that (still 1 mm ≪ any authored
    /// feature) to absorb its exact-per-face-splitter divergence where RED's shared BSP is
    /// bit-coincident. NOT a gap bridge — larger separations are left open (they need the split).
    /// </summary>
    private const float Tol = 1e-3f;

    /// <summary>Keep insertions away from an edge's own endpoints by this much.</summary>
    private const float EndEps = 5e-4f;

    /// <summary>Canonical weld tolerance — matches <see cref="VertexWelder"/> so open-edge
    /// detection sees the same identity the final pool will.</summary>
    private const float CanonEps = CsgPlane.OnPlaneEpsilon;

    private const float CanonCell = 0.02f;
    private const float SnapCell = 0.05f;
    private const int MaxPasses = 24;

    /// <summary>
    /// Seals open seams in-place across the wall faces of <paramref name="faces"/>. <paramref name="tol"/> is
    /// the weld / on-edge tolerance: the per-brush default keeps RED's <see cref="Tol"/> (1 mm = 10× the binary's
    /// 1e-4), but the leaf-extraction path passes a wider value to close the over-determined-corner near-pairs
    /// its global re-tessellation leaves (the plane weld handles the shared-plane cohort; this mops up the rest).
    /// Widening is safe because it only ever moves OPEN-edge endpoints, which sit at a leak by definition — it
    /// cannot distort watertight geometry, only bridge two open leak endpoints that are within the tolerance
    /// (authored features that must stay open, e.g. dm01's 2 cm lip, are far wider than any tolerance used).
    /// </summary>
    public static void Seal(List<CsgFace> faces, float tol = Tol)
    {
        var wall = new List<CsgFace>(faces.Count);
        foreach (CsgFace f in faces)
        {
            if (!f.IsPortal && (f.Flags & (ushort)FaceFlags.IsDetail) == 0 && f.Vertices.Count >= 3)
            {
                wall.Add(f);
            }
        }

        if (wall.Count == 0)
        {
            return;
        }

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var canon = new Canon(wall);
            bool changed = WeldOpenEndpoints(wall, canon, tol);

            // Rebuild identity after welding moved positions, then insert T-joints.
            var canon2 = changed ? new Canon(wall) : canon;
            changed |= InsertOnOpenEdges(wall, canon2, tol);

            // RED-authentic shared-corner snap: an open leak endpoint is snapped onto the
            // nearby MANIFOLD vertex the neighbour faces already share (RED's shared BSP emits
            // one pool vertex there; GED's independent exact splitter computed the same corner a
            // fraction of a millimetre off, so it fell outside the 1e-4 pool weld and stayed
            // open). Verified on dm04's floor cluster (ground-truth vs RED's compiled output):
            // the flat-floor corner sits 0.19 mm from the wall/tilted-floor corner RED shares.
            // Moves only the leak endpoint (safe by the sealer's invariant), and never onto a
            // vertex the same face already carries (that would fold a real edge to zero length).
            var canon3 = changed ? new Canon(wall) : canon2;
            changed |= SnapOpenToManifold(wall, canon3, tol);

            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>Snaps near-coincident endpoints of open edges onto one shared position.</summary>
    private static bool WeldOpenEndpoints(List<CsgFace> wall, Canon canon, float tol)
    {
        float weldEps = tol;
        (Dictionary<(int, int), int> edgeCount, _) = canon.EdgeUse(wall);

        // Canonical ids that are endpoints of at least one open edge.
        var openEnds = new HashSet<int>();
        foreach (KeyValuePair<(int, int), int> kv in edgeCount)
        {
            if (kv.Value == 1)
            {
                openEnds.Add(kv.Key.Item1);
                openEnds.Add(kv.Key.Item2);
            }
        }

        if (openEnds.Count == 0)
        {
            return false;
        }

        // Union-find near-coincident open endpoints via a spatial hash on their positions.
        var grid = new Dictionary<(int, int, int), List<int>>();
        foreach (int id in openEnds)
        {
            (int, int, int) c = Cell(canon.Pos[id], SnapCell);
            if (!grid.TryGetValue(c, out List<int>? b))
            {
                grid[c] = b = new List<int>();
            }

            b.Add(id);
        }

        int[] parent = null!;
        var idList = new List<int>(openEnds);
        var idPos = new Dictionary<int, int>(idList.Count);
        for (int i = 0; i < idList.Count; i++)
        {
            idPos[idList[i]] = i;
        }

        parent = new int[idList.Count];
        for (int i = 0; i < parent.Length; i++)
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

        bool anyUnion = false;
        foreach (int id in openEnds)
        {
            Vec3 p = canon.Pos[id];
            (int cx, int cy, int cz) = Cell(p, SnapCell);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                        {
                            continue;
                        }

                        foreach (int other in bucket)
                        {
                            if (other <= id)
                            {
                                continue;
                            }

                            if (canon.Pos[other].Sub(p).LengthSquared() <= weldEps * weldEps)
                            {
                                int ra = Find(idPos[id]);
                                int rb = Find(idPos[other]);
                                if (ra != rb)
                                {
                                    parent[ra] = rb;
                                    anyUnion = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (!anyUnion)
        {
            return false;
        }

        // Deterministic representative = min canonical id in the cluster.
        var repId = new Dictionary<int, int>();
        foreach (int id in idList)
        {
            int r = Find(idPos[id]);
            if (!repId.TryGetValue(r, out int cur) || id < cur)
            {
                repId[r] = id;
            }
        }

        // Map every clustered canonical id to the representative position.
        var moveTo = new Dictionary<int, Vec3>();
        foreach (int id in idList)
        {
            int r = Find(idPos[id]);
            int rep = repId[r];
            if (rep != id)
            {
                moveTo[id] = canon.Pos[rep];
            }
        }

        if (moveTo.Count == 0)
        {
            return false;
        }

        // Apply: any wall-face vertex at a moved canonical position snaps to the rep position.
        bool moved = false;
        foreach (CsgFace f in wall)
        {
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                int cid = canon.IdAt(f.Vertices[i].Position);
                if (cid >= 0 && moveTo.TryGetValue(cid, out Vec3 to))
                {
                    CsgVertex v = f.Vertices[i];
                    if (!v.Position.ApproxEquals(to, CanonEps))
                    {
                        f.Vertices[i] = new CsgVertex(to, v.Uv);
                        moved = true;
                    }
                }
            }
        }

        return moved;
    }

    /// <summary>Inserts open-edge endpoints onto collinear neighbour edges that pass through them.</summary>
    private static bool InsertOnOpenEdges(List<CsgFace> wall, Canon canon, float perpEps)
    {
        (Dictionary<(int, int), int> edgeCount, _) = canon.EdgeUse(wall);

        // Positions that are endpoints of an open edge (candidates to insert into other edges).
        var openEndPos = new List<Vec3>();
        var seenPos = new HashSet<int>();
        foreach (KeyValuePair<(int, int), int> kv in edgeCount)
        {
            if (kv.Value != 1)
            {
                continue;
            }

            if (seenPos.Add(kv.Key.Item1))
            {
                openEndPos.Add(canon.Pos[kv.Key.Item1]);
            }

            if (seenPos.Add(kv.Key.Item2))
            {
                openEndPos.Add(canon.Pos[kv.Key.Item2]);
            }
        }

        if (openEndPos.Count == 0)
        {
            return false;
        }

        // Spatial hash of candidate positions.
        var grid = new Dictionary<(int, int, int), List<Vec3>>();
        foreach (Vec3 p in openEndPos)
        {
            (int, int, int) c = Cell(p, SnapCell);
            if (!grid.TryGetValue(c, out List<Vec3>? b))
            {
                grid[c] = b = new List<Vec3>();
            }

            b.Add(p);
        }

        // For each face, if an OPEN edge passes near a candidate, insert it.
        bool changed = false;
        foreach (CsgFace f in wall)
        {
            List<CsgVertex> verts = f.Vertices;
            int n = verts.Count;
            var result = new List<CsgVertex>(n + 4);
            bool faceChanged = false;
            CsgPlane plane = f.Plane; // RED's FUN_0048a790==2 gate: candidate must lie ON this plane

            for (int i = 0; i < n; i++)
            {
                CsgVertex a = verts[i];
                CsgVertex b = verts[(i + 1) % n];
                result.Add(a);

                int ca = canon.IdAt(a.Position);
                int cb = canon.IdAt(b.Position);
                bool isOpen = ca >= 0 && cb >= 0 && edgeCount.TryGetValue(Key(ca, cb), out int uc) && uc == 1;
                if (!isOpen)
                {
                    continue;
                }

                Vec3 pa = a.Position, pb = b.Position;
                Vec3 dir = pb.Sub(pa);
                float lenSq = dir.LengthSquared();
                if (lenSq < 1e-9f)
                {
                    continue;
                }

                float len = MathF.Sqrt(lenSq);
                var hits = new List<(float T, Vec3 P)>();
                CollectOnEdge(grid, plane, pa, pb, dir, lenSq, len, perpEps, hits);
                if (hits.Count == 0)
                {
                    continue;
                }

                hits.Sort(static (x, y) => x.T.CompareTo(y.T));
                float lastT = 0f;
                foreach ((float t, Vec3 p) in hits)
                {
                    if (t - lastT < EndEps / len)
                    {
                        continue;
                    }

                    result.Add(new CsgVertex(p, LerpUv(a, b, t)));
                    lastT = t;
                    faceChanged = true;
                }
            }

            if (faceChanged)
            {
                f.Vertices = result;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Snaps each OPEN-edge endpoint onto the nearest MANIFOLD (non-open) wall vertex within
    /// <paramref name="tol"/> that no face sharing the endpoint already carries. This closes the
    /// RED-shared-corner cohort: where RED's shared BSP emits one pool vertex, GED's independent
    /// exact per-face splitter can compute the same corner a fraction of a millimetre off on one of
    /// the incident faces, so the duplicate falls outside the 1e-4 pool weld and its edges read open
    /// while the neighbour's identical corner is fully manifold. Only the open leak endpoint moves.
    /// </summary>
    private static bool SnapOpenToManifold(List<CsgFace> wall, Canon canon, float tol)
    {
        (Dictionary<(int, int), int> edgeCount, _) = canon.EdgeUse(wall);
        var openEnds = new HashSet<int>();
        foreach (KeyValuePair<(int, int), int> kv in edgeCount)
        {
            if (kv.Value == 1)
            {
                openEnds.Add(kv.Key.Item1);
                openEnds.Add(kv.Key.Item2);
            }
        }

        if (openEnds.Count == 0)
        {
            return false;
        }

        // Face membership per canonical id (to forbid snapping onto a vertex the same face carries).
        var idFaces = new Dictionary<int, HashSet<int>>();
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < canon.Pos.Count; i++)
        {
            (int, int, int) c = Cell(canon.Pos[i], SnapCell);
            if (!grid.TryGetValue(c, out List<int>? b))
            {
                grid[c] = b = new List<int>();
            }

            b.Add(i);
        }

        for (int fi = 0; fi < wall.Count; fi++)
        {
            foreach (CsgVertex v in wall[fi].Vertices)
            {
                int id = canon.IdAt(v.Position);
                if (id < 0)
                {
                    continue;
                }

                if (!idFaces.TryGetValue(id, out HashSet<int>? set))
                {
                    idFaces[id] = set = new HashSet<int>();
                }

                set.Add(fi);
            }
        }

        float weldEps2 = tol * tol;
        var moveTo = new Dictionary<int, Vec3>();
        foreach (int o in openEnds)
        {
            Vec3 p = canon.Pos[o];
            (int cx, int cy, int cz) = Cell(p, SnapCell);
            int best = -1;
            float bestD2 = weldEps2;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                        {
                            continue;
                        }

                        foreach (int t in bucket)
                        {
                            if (t == o || openEnds.Contains(t))
                            {
                                continue; // snap targets must be manifold anchors, never other leaks
                            }

                            // Forbid targets any face incident to o already carries (would fold an edge to zero).
                            if (idFaces.TryGetValue(o, out HashSet<int>? of) && idFaces.TryGetValue(t, out HashSet<int>? tf) && of.Overlaps(tf))
                            {
                                continue;
                            }

                            float d2 = canon.Pos[t].Sub(p).LengthSquared();
                            if (d2 < bestD2 || (d2 <= bestD2 && (best < 0 || t < best)))
                            {
                                bestD2 = d2;
                                best = t;
                            }
                        }
                    }
                }
            }

            if (best >= 0)
            {
                moveTo[o] = canon.Pos[best];
            }
        }

        if (moveTo.Count == 0)
        {
            return false;
        }

        bool moved = false;
        foreach (CsgFace f in wall)
        {
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                int cid = canon.IdAt(f.Vertices[i].Position);
                if (cid >= 0 && moveTo.TryGetValue(cid, out Vec3 to))
                {
                    CsgVertex v = f.Vertices[i];
                    if (!v.Position.ApproxEquals(to, CanonEps))
                    {
                        f.Vertices[i] = new CsgVertex(to, v.Uv);
                        moved = true;
                    }
                }
            }
        }

        return moved;
    }

    private static void CollectOnEdge(
        Dictionary<(int, int, int), List<Vec3>> grid, CsgPlane plane, Vec3 pa, Vec3 pb, Vec3 dir, float lenSq,
        float len, float perpEps, List<(float, Vec3)> hits)
    {
        Vec3 min = Vec3Math.Min(pa, pb);
        Vec3 max = Vec3Math.Max(pa, pb);
        (int x0, int y0, int z0) = Cell(min, SnapCell);
        (int x1, int y1, int z1) = Cell(max, SnapCell);
        var seen = new HashSet<(int, int, int)>();

        for (int cx = x0 - 1; cx <= x1 + 1; cx++)
        {
            for (int cy = y0 - 1; cy <= y1 + 1; cy++)
            {
                for (int cz = z0 - 1; cz <= z1 + 1; cz++)
                {
                    if (!grid.TryGetValue((cx, cy, cz), out List<Vec3>? bucket))
                    {
                        continue;
                    }

                    foreach (Vec3 p in bucket)
                    {
                        // RED's FUN_0048a790==2 gate: the candidate must lie ON the target face's
                        // plane. Without it, a perpendicular face's vertex could be inserted onto
                        // this edge, distorting the face off-plane.
                        if (MathF.Abs(plane.Distance(p)) > perpEps)
                        {
                            continue;
                        }

                        float t = p.Sub(pa).Dot(dir) / lenSq;
                        if (t * len <= EndEps || (1f - t) * len <= EndEps)
                        {
                            continue; // at or beyond an endpoint
                        }

                        Vec3 proj = pa.Add(dir.Scale(t));
                        if (proj.Distance(p) <= perpEps && seen.Add(Quantize(p)))
                        {
                            hits.Add((t, p));
                        }
                    }
                }
            }
        }
    }

    private static Uv LerpUv(CsgVertex a, CsgVertex b, float t) =>
        new(a.Uv.U + ((b.Uv.U - a.Uv.U) * t), a.Uv.V + ((b.Uv.V - a.Uv.V) * t));

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    private static (int, int, int) Cell(Vec3 p, float size) =>
        ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size));

    private static (int, int, int) Quantize(Vec3 p) =>
        ((int)MathF.Round(p.X * 4096f), (int)MathF.Round(p.Y * 4096f), (int)MathF.Round(p.Z * 4096f));

    /// <summary>
    /// A canonical vertex identity over a set of faces, welding positions within
    /// <see cref="CanonEps"/> (matching the final <see cref="VertexWelder"/>), so an edge is
    /// counted the same way <see cref="HoleDetector"/> will count it on the compiled pool.
    /// </summary>
    private sealed class Canon
    {
        private readonly Dictionary<(int, int, int), List<int>> _grid = new();

        public List<Vec3> Pos { get; } = new();

        public Canon(List<CsgFace> wall)
        {
            foreach (CsgFace f in wall)
            {
                foreach (CsgVertex v in f.Vertices)
                {
                    Intern(v.Position);
                }
            }
        }

        public int Intern(Vec3 p)
        {
            (int cx, int cy, int cz) = Cell(p, CanonCell);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (_grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                        {
                            foreach (int i in bucket)
                            {
                                if (Pos[i].ApproxEquals(p, CanonEps))
                                {
                                    return i;
                                }
                            }
                        }
                    }
                }
            }

            int idx = Pos.Count;
            Pos.Add(p);
            if (!_grid.TryGetValue((cx, cy, cz), out List<int>? cell))
            {
                _grid[(cx, cy, cz)] = cell = new List<int>();
            }

            cell.Add(idx);
            return idx;
        }

        /// <summary>Canonical id for an existing position, or -1 if unseen.</summary>
        public int IdAt(Vec3 p)
        {
            (int cx, int cy, int cz) = Cell(p, CanonCell);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (_grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                        {
                            foreach (int i in bucket)
                            {
                                if (Pos[i].ApproxEquals(p, CanonEps))
                                {
                                    return i;
                                }
                            }
                        }
                    }
                }
            }

            return -1;
        }

        /// <summary>Edge-use counts over the wall faces, keyed by canonical id pair.</summary>
        public (Dictionary<(int, int), int> Count, int OpenCount) EdgeUse(List<CsgFace> wall)
        {
            var count = new Dictionary<(int, int), int>();
            foreach (CsgFace f in wall)
            {
                int n = f.Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    int a = IdAt(f.Vertices[i].Position);
                    int b = IdAt(f.Vertices[(i + 1) % n].Position);
                    if (a < 0 || b < 0 || a == b)
                    {
                        continue;
                    }

                    (int, int) k = a < b ? (a, b) : (b, a);
                    count[k] = count.GetValueOrDefault(k) + 1;
                }
            }

            int open = 0;
            foreach (int c in count.Values)
            {
                if (c == 1)
                {
                    open++;
                }
            }

            return (count, open);
        }
    }
}
