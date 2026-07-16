using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// RED-style SHARED face splitting (compiler-parity-notes.md phase-3
/// <c>FUN_004a8220</c> → <c>FUN_0048bec0</c>): a face is cut along the planes of the
/// brushes it meets, and every cut vertex is identified by the THREE planes through
/// it (this face's plane, the edge's other plane, and the cutter), so two faces that
/// cut along the same three planes receive the byte-identical point from
/// <see cref="PlaneRegistry"/>. That is how adjacent/coincident faces end up with
/// coincident vertices "by construction", which is what makes RED's builds
/// watertight where GED's independent exact splitter left sub-mm-to-cm seams.
/// </summary>
internal static class CsgSharedSplit
{
    private const float Band = CsgPlane.OnPlaneEpsilon;

    /// <summary>DIAGNOSTIC-ONLY hook (flagship 33 seam tracing): when set, invoked for every interned cut
    /// vertex with (reg, pos, facePlane, edgePlane, cutter, aVid, bVid, resultVid, t, path). Null in production.</summary>
    internal static Action<PlaneRegistry, Vec3, int, int, int, int, int, int, float, string>? SeamTrace;

    /// <summary>DIAGNOSTIC-ONLY route tag (flagship 34): the cut site currently executing, reported to
    /// <see cref="SeamTrace"/> in place of "intern" so a phantom birth can be attributed to its route
    /// (cap / world-sibling / brush-clip). Null in production and when SeamTrace is null.</summary>
    [ThreadStatic]
    internal static string? Route;

    /// <summary>A polygon corner during shared splitting: position, UV, and the sorted
    /// ids of every registry plane through it (always includes the owning face plane).</summary>
    internal readonly struct PsVert
    {
        public PsVert(Vec3 pos, Uv uv, int[] planes)
            : this(pos, uv, planes, -1)
        {
        }

        public PsVert(Vec3 pos, Uv uv, int[] planes, int vid)
        {
            Pos = pos;
            Uv = uv;
            Planes = planes;
            VId = vid;
        }

        public Vec3 Pos { get; }

        public Uv Uv { get; }

        /// <summary>Sorted, distinct canonical plane ids passing through this point.</summary>
        public int[] Planes { get; }

        /// <summary>Shared vertex identity for the EdgeLerpSplit path (flagship 19), or -1 when the fold
        /// carries no identity (every other path). Two flanking faces that share an edge carry the same
        /// endpoint ids, so a cut of that edge is interned once and referenced by both.</summary>
        public int VId { get; }

        /// <summary>This corner with a new shared vertex id (position/UV/planes unchanged).</summary>
        public PsVert WithId(int vid) => new(Pos, Uv, Planes, vid);
    }

    /// <summary>
    /// Splits polygon <paramref name="poly"/> (on face plane id <paramref name="facePlane"/>)
    /// successively by each cutter, returning the convex fragments. Cut vertices get
    /// canonical positions from <paramref name="reg"/>; a fragment cap guards pathological
    /// fan-out (matching the legacy splitter's <c>MaxFragmentsPerFace</c>).
    /// </summary>
    public static List<List<PsVert>> Split(
        PlaneRegistry reg, int facePlane, List<PsVert> poly,
        IReadOnlyList<(CsgPlane Geom, int Id)> cutters, int maxFragments, out bool capped)
    {
        capped = false;
        var current = new List<List<PsVert>> { poly };
        foreach ((CsgPlane geom, int id) in cutters)
        {
            var next = new List<List<PsVert>>(current.Count + 4);
            foreach (List<PsVert> frag in current)
            {
                SplitOne(reg, facePlane, frag, geom, id, next);
            }

            current = next;
            if (current.Count > maxFragments)
            {
                capped = true;
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// Splits <paramref name="poly"/> by a plane into the front piece (signed distance &gt; band) and the
    /// back piece (≤ band, i.e. inside/on the outward plane). Either may be null when the polygon lies
    /// wholly on one side. On-plane vertices join both pieces (shared cut edge).
    /// </summary>
    internal static void SplitOneSeparate(
        PlaneRegistry reg, int facePlane, List<PsVert> poly, CsgPlane plane, int cutterId,
        out List<PsVert>? front, out List<PsVert>? back)
    {
        int n = poly.Count;
        Span<float> d = n <= 64 ? stackalloc float[n] : new float[n];
        int nf = 0, nb = 0;
        for (int i = 0; i < n; i++)
        {
            d[i] = plane.Distance(poly[i].Pos);
            if (d[i] > Band)
            {
                nf++;
            }
            else if (d[i] < -Band)
            {
                nb++;
            }
        }

        if (nf == 0)
        {
            front = null;
            back = poly; // wholly inside/on
            return;
        }

        if (nb == 0)
        {
            front = poly; // wholly outside
            back = null;
            return;
        }

        var fv = new List<PsVert>();
        var bv = new List<PsVert>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            PsVert vi = poly[i];
            if (d[i] >= -Band)
            {
                fv.Add(vi);
            }

            if (d[i] <= Band)
            {
                bv.Add(vi);
            }

            bool crosses = (d[i] > Band && d[j] < -Band) || (d[i] < -Band && d[j] > Band);
            if (crosses)
            {
                PsVert cut = CutVertex(reg, facePlane, vi, poly[j], d[i], d[j], cutterId);
                fv.Add(cut);
                bv.Add(cut);
            }
        }

        front = fv.Count >= 3 ? fv : null;
        back = bv.Count >= 3 ? bv : null;
    }

    /// <summary>
    /// A per-brush solid BSP over shared-plane polygons — RED's phase-0 brush volume
    /// (FUN_004a6b90 → FUN_004af580: node planes taken from the brush's own face planes). Built once
    /// per brush; clipping a foreign face down it partitions the face into the pieces INSIDE the brush
    /// (behind a back-leaf) and OUTSIDE it (in front of a front-leaf), with node-plane cut vertices
    /// shared through the <see cref="PlaneRegistry"/>. Correct for convex AND concave brushes (a bumpy
    /// terrain shell), unlike a plain behind-all-planes convex clip. Iterative (explicit stacks) so a
    /// deep tree over a many-faced terrain brush cannot overflow the call stack.
    /// </summary>
    internal sealed class PsBsp
    {
        private CsgPlane _plane;
        private int _planeId = -1;
        private bool _hasPlane;
        private PsBsp? _front;
        private PsBsp? _back;

        /// <summary>Builds the brush's solid BSP from its outward-facing boundary faces (plane + id + polygon).</summary>
        public static PsBsp Build(IReadOnlyList<(CsgPlane Plane, int Id, List<PsVert> Poly)> faces) =>
            Build(faces, int.MaxValue, int.MaxValue, out _)!;

        /// <summary>
        /// Budgeted build. Returns null when the node count or accumulated split-work exceeds the caps —
        /// the guard that keeps a giant / pathological non-convex shell (the 11.6 GB terrain lesson) from
        /// exploding the tree; the caller then falls back to the crossing-face cutter for that brush.
        /// <paramref name="work"/> counts polygon copies routed to children (a memory proxy).
        /// </summary>
        public static PsBsp? Build(
            IReadOnlyList<(CsgPlane Plane, int Id, List<PsVert> Poly)> faces,
            int maxNodes, int maxWork, out int work)
        {
            work = 0;
            int nodes = 1;
            var root = new PsBsp();
            var stack = new Stack<(PsBsp Node, List<(CsgPlane Plane, int Id, List<PsVert> Poly)> Faces)>();
            stack.Push((root, new List<(CsgPlane, int, List<PsVert>)>(faces)));
            while (stack.Count > 0)
            {
                (PsBsp node, List<(CsgPlane Plane, int Id, List<PsVert> Poly)> polys) = stack.Pop();
                if (polys.Count == 0)
                {
                    continue;
                }

                if (nodes > maxNodes || work > maxWork)
                {
                    return null;
                }

                if (!node._hasPlane)
                {
                    node._plane = polys[0].Plane;
                    node._planeId = polys[0].Id;
                    node._hasPlane = true;
                }

                var front = new List<(CsgPlane, int, List<PsVert>)>();
                var back = new List<(CsgPlane, int, List<PsVert>)>();
                foreach ((CsgPlane pl, int id, List<PsVert> poly) in polys)
                {
                    // Coplanar with the node plane ⇒ consumed here (does not add structure); avoids
                    // re-selecting the same plane forever.
                    if (Coplanar(node._plane, poly))
                    {
                        continue;
                    }

                    SplitOneSeparate(null!, -1, poly, node._plane, -1, out List<PsVert>? f, out List<PsVert>? b);
                    if (f is not null)
                    {
                        front.Add((pl, id, f));
                    }

                    if (b is not null)
                    {
                        back.Add((pl, id, b));
                    }
                }

                work += front.Count + back.Count;
                if (front.Count > 0)
                {
                    if (node._front is null)
                    {
                        node._front = new PsBsp();
                        nodes++;
                    }

                    stack.Push((node._front, front));
                }

                if (back.Count > 0)
                {
                    if (node._back is null)
                    {
                        node._back = new PsBsp();
                        nodes++;
                    }

                    stack.Push((node._back, back));
                }
            }

            return root;
        }

        /// <summary>Clips <paramref name="poly"/> down the tree, collecting inside- and outside-brush fragments.
        /// The node planes are the convex hull's faces (front-of-any face ⇒ outside the hull, always), so this path
        /// is NOT extent-gated: a convex hull has no unbounded plane-extension over-cut to suppress (measured
        /// flagship 35 — gating it is net-negative). The extent gate applies only to the concave-cell
        /// <see cref="ConvexClip"/> path.</summary>
        public void Clip(
            PlaneRegistry reg, int facePlane, List<PsVert> poly,
            List<List<PsVert>> inside, List<List<PsVert>> outside)
        {
            var work = new Stack<(PsBsp Node, List<PsVert> Poly)>();
            work.Push((this, poly));
            while (work.Count > 0)
            {
                (PsBsp node, List<PsVert> p) = work.Pop();
                if (!node._hasPlane)
                {
                    outside.Add(p);
                    continue;
                }

                SplitOneSeparate(reg, facePlane, p, node._plane, node._planeId, out List<PsVert>? front, out List<PsVert>? back);

                if (front is not null)
                {
                    if (node._front is not null)
                    {
                        work.Push((node._front, front));
                    }
                    else
                    {
                        outside.Add(front); // in front of a leaf plane ⇒ outside the brush
                    }
                }

                if (back is not null)
                {
                    if (node._back is not null)
                    {
                        work.Push((node._back, back));
                    }
                    else
                    {
                        inside.Add(back); // behind a leaf plane with no interior ⇒ inside the brush
                    }
                }
            }
        }

        private static bool Coplanar(CsgPlane node, List<PsVert> poly)
        {
            foreach (PsVert v in poly)
            {
                if (MathF.Abs(node.Distance(v.Pos)) > Band)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Enumerates the convex INSIDE cells (the brush's convex decomposition) as oriented half-space
        /// constraint lists: each cell is <c>{ plane : Distance(p) &lt;= 0 }</c> over the path from the root
        /// to a back-leaf (a node whose back side has no child). Back branches keep the node plane as-is
        /// (inside = behind); front branches flip it (inside-subregion = in front). Returns null when the
        /// cell count exceeds <paramref name="maxCells"/> (the piece budget — caller falls back).
        /// </summary>
        public List<(CsgPlane Plane, int Id)[]>? CollectCells(int maxCells)
        {
            var cells = new List<(CsgPlane, int)[]>();
            var stack = new Stack<(PsBsp Node, List<(CsgPlane, int)> Path)>();
            stack.Push((this, new List<(CsgPlane, int)>()));
            while (stack.Count > 0)
            {
                (PsBsp node, List<(CsgPlane, int)> path) = stack.Pop();
                if (!node._hasPlane)
                {
                    continue; // empty region ⇒ no inside cell here
                }

                var backPath = new List<(CsgPlane, int)>(path) { (node._plane, node._planeId) };
                if (node._back is null)
                {
                    if (cells.Count >= maxCells)
                    {
                        return null; // over budget
                    }

                    cells.Add(backPath.ToArray());
                }
                else
                {
                    stack.Push((node._back, backPath));
                }

                if (node._front is not null)
                {
                    var frontPath = new List<(CsgPlane, int)>(path) { (node._plane.Flipped(), node._planeId) };
                    stack.Push((node._front, frontPath));
                }
            }

            return cells;
        }
    }

    /// <summary>
    /// Clips <paramref name="poly"/> against ONE convex cell given as oriented half-space constraints
    /// (inside = behind every plane, Distance ≤ 0): the part behind all planes is the single INSIDE
    /// fragment, everything cut off in front of some plane is OUTSIDE. Cut vertices are canonical
    /// three-plane points via <paramref name="reg"/>, so the silhouette cut is shared with the brush's own
    /// coincident face — watertight by construction, exactly as for a convex brush (each cell IS a small
    /// convex brush). This is the per-piece convex clip that localizes cuts to the cell, unlike a monolithic
    /// concave BSP whose plane extensions would cut distant open-space faces.
    /// </summary>
    public static void ConvexClip(
        PlaneRegistry reg, int facePlane, List<PsVert> poly,
        IReadOnlyList<(CsgPlane Plane, int Id)> constraints,
        List<List<PsVert>> inside, List<List<PsVert>> outside)
        => ConvexClip(reg, facePlane, poly, constraints, null, 0f, inside, outside);

    /// <summary>
    /// As the four-list <see cref="ConvexClip"/> but extent-gated (BoundedVolumeClip, flagship 35). When
    /// <paramref name="extents"/> is non-null (aligned 1:1 with <paramref name="constraints"/>), a piece WHOLLY
    /// beyond a constraint's bounded face extent (expanded by <paramref name="extEps"/>) is not cut by that plane —
    /// the plane is a phantom there — and descends by the plane's half-space instead (RED's "polygon outside the
    /// node extent" case: front ⇒ outside the cell; behind ⇒ carry uncut to the next constraint). MEASURED
    /// NET-NEGATIVE (flagship 35): the per-face extent is too fine — a large foreign face is trivially "wholly
    /// outside" a small terrain triangle's AABB, so the gate suppresses the legitimate classification cut and tears
    /// holes (bvc_corpus.txt: better=0 worse=10, 3 zeros broken), closing none of dm04's six. Retained default OFF
    /// (extents null ⇒ this reduces to the exact four-list clip) as the reproducible measurement.
    /// </summary>
    public static void ConvexClip(
        PlaneRegistry reg, int facePlane, List<PsVert> poly,
        IReadOnlyList<(CsgPlane Plane, int Id)> constraints,
        IReadOnlyList<(Vec3 Min, Vec3 Max)>? extents, float extEps,
        List<List<PsVert>> inside, List<List<PsVert>> outside)
    {
        List<PsVert> current = poly;
        for (int i = 0; i < constraints.Count; i++)
        {
            (CsgPlane plane, int id) = constraints[i];

            // BoundedVolumeClip: a piece WHOLLY beyond this cell face's real extent is not bounded by that face —
            // the plane is a phantom there (RED skips the node). Descend by the plane's half-space WITHOUT cutting:
            // in front ⇒ outside the cell; behind ⇒ still an inside-candidate, tested by the faces it does reach.
            if (extents is not null && AabbWhollyOutside(current, extents[i].Min, extents[i].Max, extEps))
            {
                if (plane.Distance(Centroid(current)) > 0f)
                {
                    outside.Add(current);
                    return;
                }

                continue; // behind ⇒ carry the whole (uncut) piece to the next constraint
            }

            SplitOneSeparate(reg, facePlane, current, plane, id, out List<PsVert>? front, out List<PsVert>? back);
            if (front is not null)
            {
                outside.Add(front); // in front of a bounding plane ⇒ outside the cell
            }

            if (back is null)
            {
                return; // nothing behind this plane ⇒ the whole polygon is outside the cell
            }

            current = back;
        }

        inside.Add(current); // behind every constraint ⇒ inside the cell
    }

    /// <summary>True iff <paramref name="poly"/>'s AABB does not overlap the face extent
    /// [<paramref name="min"/>,<paramref name="max"/>] expanded by <paramref name="eps"/> — i.e. the polygon lies
    /// WHOLLY beyond the bounded supporting face (RED's "polygon outside the node extent" case: descend, no cut).</summary>
    private static bool AabbWhollyOutside(List<PsVert> poly, Vec3 min, Vec3 max, float eps)
    {
        var pmn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var pmx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (PsVert v in poly)
        {
            Vec3 p = v.Pos;
            pmn = new Vec3(MathF.Min(pmn.X, p.X), MathF.Min(pmn.Y, p.Y), MathF.Min(pmn.Z, p.Z));
            pmx = new Vec3(MathF.Max(pmx.X, p.X), MathF.Max(pmx.Y, p.Y), MathF.Max(pmx.Z, p.Z));
        }

        return pmx.X + eps < min.X || pmn.X - eps > max.X ||
               pmx.Y + eps < min.Y || pmn.Y - eps > max.Y ||
               pmx.Z + eps < min.Z || pmn.Z - eps > max.Z;
    }

    private static Vec3 Centroid(List<PsVert> poly)
    {
        var s = new Vec3(0f, 0f, 0f);
        foreach (PsVert v in poly)
        {
            s = s.Add(v.Pos);
        }

        return s.Scale(1f / poly.Count);
    }

    /// <summary>Splits one polygon by one plane, appending front/back fragments to <paramref name="output"/>.</summary>
    private static void SplitOne(
        PlaneRegistry reg, int facePlane, List<PsVert> poly, CsgPlane plane, int cutterId, List<List<PsVert>> output)
    {
        int n = poly.Count;
        Span<float> d = n <= 64 ? stackalloc float[n] : new float[n];
        int front = 0, back = 0;
        for (int i = 0; i < n; i++)
        {
            d[i] = plane.Distance(poly[i].Pos);
            if (d[i] > Band)
            {
                front++;
            }
            else if (d[i] < -Band)
            {
                back++;
            }
        }

        if (front == 0 || back == 0)
        {
            output.Add(poly); // does not straddle
            return;
        }

        var fv = new List<PsVert>();
        var bv = new List<PsVert>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            PsVert vi = poly[i];
            if (d[i] >= -Band)
            {
                fv.Add(vi);
            }

            if (d[i] <= Band)
            {
                bv.Add(vi);
            }

            bool crosses = (d[i] > Band && d[j] < -Band) || (d[i] < -Band && d[j] > Band);
            if (crosses)
            {
                PsVert cut = CutVertex(reg, facePlane, vi, poly[j], d[i], d[j], cutterId);
                fv.Add(cut);
                bv.Add(cut);
            }
        }

        if (fv.Count >= 3)
        {
            output.Add(fv);
        }

        if (bv.Count >= 3)
        {
            output.Add(bv);
        }
    }

    /// <summary>The registry triple point is only trusted within this distance of the edge's own geometric
    /// intersection (the lerp). The registry folds planes within 2e-3 offset / 0.99997 normal-dot, so an
    /// ILL-CONDITIONED triple (a near-parallel member, e.g. two terrain triangles a fraction of a degree
    /// apart) amplifies that fold by 1/sin(angle) — the "exact" point can land centimetres from the edge
    /// actually being cut (measured 6 cm on dm04's rock), tearing extent-divergent holes no weld may bridge.
    /// RED never produces this: its cut points are computed ON the edge (jittered bisection, FUN_0048e240),
    /// so divergence stays at float noise and its 1e-4 t-joint fixer closes it. Bounding the triple's
    /// displacement to the weld scale keeps bit-identical shared corners where triples agree (the common,
    /// well-conditioned case) and degrades to RED-style local geometry — reconcilable by the 1e-3
    /// sealer — where they don't.</summary>
    private const float MaxTripleDeviation = 1e-3f;

    /// <summary>
    /// The cut point where the edge (a→b) crosses the cutter. Its identity is the
    /// triple {facePlane, edgePlane, cutter}; the position comes from the registry's
    /// cached exact triple intersection so it is byte-identical to any other face
    /// cutting along the same three planes. UV interpolates linearly (RED's rule).
    /// Falls back to a lerp position when the triple is ill-conditioned or lands
    /// beyond <see cref="MaxTripleDeviation"/> of the edge's own intersection.
    /// </summary>
    internal static PsVert CutVertex(PlaneRegistry reg, int facePlane, PsVert a, PsVert b, float da, float db, int cutterId)
    {
        float t = da / (da - db);
        var uv = new Uv(a.Uv.U + ((b.Uv.U - a.Uv.U) * t), a.Uv.V + ((b.Uv.V - a.Uv.V) * t));

        int edgePlane = CommonPlaneExcept(a.Planes, b.Planes, facePlane);

        // EdgeLerpSplit (flagship 19): the cut point and its identity come from the EDGE being cut, not the
        // plane triple. Two flanking faces sharing this edge carry the same endpoint ids, so the cut is
        // interned ONCE (byte-identical point + shared id) and the on-edge lerp keeps ill-conditioned
        // near-parallel edges at float noise instead of amplifying the registry fold. The plane-set is
        // retained only for downstream classification.
        if (reg?.EdgeStore is { } store && a.VId >= 0 && b.VId >= 0 && cutterId >= 0)
        {
            (int vid, Vec3 sharedPos) = store.InternCut(a.VId, b.VId, cutterId, a.Pos, b.Pos, t);
            int[] p = edgePlane >= 0 ? Sorted3(facePlane, edgePlane, cutterId) : Sorted2(facePlane, cutterId);
            if (SeamTrace is not null)
            {
                SeamTrace(reg, sharedPos, facePlane, edgePlane, cutterId, a.VId, b.VId, vid, t, Route ?? "intern");
            }

            return new PsVert(sharedPos, uv, p, vid);
        }

        Vec3? exact = reg is not null && edgePlane >= 0 && facePlane >= 0 && cutterId >= 0
            ? reg.Intersect(facePlane, edgePlane, cutterId)
            : null;
        // The deviation bound is a PER-PATH policy carried on the registry (see PlaneRegistry.BoundTripleDeviation):
        // the per-brush accumulator bounds it (ill-conditioned snaps tear unweldable seams); leaf extraction does
        // not (bit-identity of shared triples across portals IS its watertightness; one-sided rejection breaks it).
        Vec3 lerp = Vec3Math.Lerp(a.Pos, b.Pos, t);
        Vec3 pos = exact is Vec3 e
            && (!reg!.BoundTripleDeviation || e.Sub(lerp).LengthSquared() <= MaxTripleDeviation * MaxTripleDeviation)
            ? e
            : lerp;

        int[] planes = edgePlane >= 0
            ? Sorted3(facePlane, edgePlane, cutterId)
            : Sorted2(facePlane, cutterId);
        return new PsVert(pos, uv, planes);
    }

    /// <summary>The smallest plane id common to both vertices other than <paramref name="except"/>.</summary>
    private static int CommonPlaneExcept(int[] a, int[] b, int except)
    {
        int best = int.MaxValue;
        foreach (int p in a)
        {
            if (p == except)
            {
                continue;
            }

            foreach (int q in b)
            {
                if (q == p)
                {
                    if (p < best)
                    {
                        best = p;
                    }

                    break;
                }
            }
        }

        return best == int.MaxValue ? -1 : best;
    }

    private static int[] Sorted3(int a, int b, int c)
    {
        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (b > c)
        {
            (b, c) = (c, b);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        return new[] { a, b, c };
    }

    private static int[] Sorted2(int a, int b) => a <= b ? new[] { a, b } : new[] { b, a };
}
