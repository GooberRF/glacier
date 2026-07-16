using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// RED's SINGLE ACCUMULATED WORLD BSP (compiler-parity-notes.md — "the last construction").
/// Where the per-brush accumulator clips each face against every overlapping brush's OWN tree
/// (so a face and its coincident neighbour are cut by DIFFERENT partitions and their split
/// lines can diverge), this builds ONE partition over the whole level's brush face planes and
/// routes EVERY boundary face fragment through the SAME tree. Two faces that straddle the same
/// node plane are cut at the byte-identical <see cref="PlaneRegistry"/> triple point, so all
/// coincident cuts are bit-identical and beyond-boundary fragments die by the same in/out
/// survival test — watertight by construction, which is how RED reaches 0 open edges.
/// <para>
/// RED (RED.exe 1.20na, ghidraRF): the world solid at <c>builder_ctx+0x84</c> starts empty and
/// accumulates each brush's faces (linked list at face <c>+0x58</c>, appended by
/// <c>FUN_0048e630</c>); every op allocates a partition (<c>FUN_004af580</c>, node children
/// <c>+0x20/+0x24</c>) and homes each face's <c>+0x48</c> to it (<c>FUN_004a6b90</c>, compiler
/// path <c>DAT_00e13b8c != 0</c>); phase-3 (<c>FUN_004a8220</c> → <c>FUN_0048bec0</c>) clips the
/// spanning faces of BOTH operands down that partition, walking node planes and splitting at each
/// straddled one (RED jitters the split 0.9753→−1.0 to dodge degeneracies; GED uses the exact
/// registry triple point, allowed by the parity target). Node planes come from actual brush faces
/// (a bounded set) and the tree is chosen fewest-split, so it does not explode the way a naive
/// global BSP does. The <c>1e-4</c> on-plane band is RED's <c>_DAT_00554714 = 0x38d1b717</c>.
/// </para>
/// <para>
/// Leaf solidity is NOT stored in the tree: the survivor test stays GED's time-ordered open/solid
/// fold (<see cref="CsgSolver.OpenAt"/>), so the tree's sole job is to supply the shared cut lines.
/// Iterative (explicit stacks) so a deep tree cannot overflow the call stack; budgeted so a
/// pathological level falls back to the per-brush path (the 11.6 GB terrain lesson).
/// </para>
/// </summary>
internal sealed class WorldBsp
{
    private const float Band = CsgPlane.OnPlaneEpsilon;

    private CsgPlane _plane;
    private int _planeId = -1;
    private bool _hasPlane;
    private WorldBsp? _front;
    private WorldBsp? _back;

    // Leaf solidity cache: a convex leaf is uniformly open or solid under the time-ordered fold (every
    // brush face plane is a candidate node, so no boundary passes through a leaf interior). Memoised by
    // first-touch so all fragments landing in a leaf get the SAME verdict — RED's "beyond-boundary
    // fragments die by in/out classification", replacing the fixed-eps probe that mis-drops tiny fragments.
    private bool _classified;
    private bool _open;

    /// <summary>One boundary face fed to the build: its registry plane id, geometric plane and polygon.</summary>
    internal readonly struct Face
    {
        public Face(CsgPlane plane, int id, List<CsgSharedSplit.PsVert> poly)
        {
            Plane = plane;
            Id = id;
            Poly = poly;
        }

        public CsgPlane Plane { get; }

        public int Id { get; }

        public List<CsgSharedSplit.PsVert> Poly { get; }
    }

    /// <summary>Build statistics for the memory/perf report (nodes, leaves, split-work).</summary>
    internal readonly record struct Stats(int Nodes, int Leaves, long Work, bool BudgetExceeded);

    /// <summary>
    /// Builds the world partition from the level's boundary faces with a fewest-split heuristic
    /// (Quake-lineage <c>SelectPartition</c>: minimise straddled faces, prefer axial, balance the
    /// split). Node planes carry their registry id so <see cref="Clip"/> emits shared cut vertices.
    /// Returns null when a budget is exceeded (<paramref name="stats"/>.BudgetExceeded set), so the
    /// caller falls back to the per-brush accumulator for the whole level.
    /// </summary>
    public static WorldBsp? Build(
        IReadOnlyList<Face> faces, int maxNodes, long maxWork, int maxCandidates, out Stats stats)
    {
        int nodes = 1;
        int leaves = 0;
        long work = 0;
        var root = new WorldBsp();

        // Explicit stack of (node, faces-in-this-region). Faces are consumed as the tree descends.
        var stack = new Stack<(WorldBsp Node, List<Face> Faces)>();
        stack.Push((root, new List<Face>(faces)));
        while (stack.Count > 0)
        {
            (WorldBsp node, List<Face> region) = stack.Pop();
            if (region.Count == 0)
            {
                leaves++;
                continue; // empty convex leaf
            }

            if (nodes > maxNodes || work > maxWork)
            {
                stats = new Stats(nodes, leaves, work, true);
                return null;
            }

            int pivot = SelectPartition(region, maxCandidates);
            node._plane = region[pivot].Plane;
            node._planeId = region[pivot].Id;
            node._hasPlane = true;

            var front = new List<Face>();
            var back = new List<Face>();
            foreach (Face f in region)
            {
                // Coplanar with the node plane ⇒ consumed here (bounds this leaf face; does not recurse),
                // which also prevents re-selecting the same plane forever. COINCIDENT means the REGISTRY fold
                // (same interned id — the project's one coincidence definition, offset tol 2e-3), not merely the
                // geometric 1e-4 band: community brushwork authors the same wall a few 1e-4s apart (dm04's
                // y=−60.18 floor pair is 3e-4 apart), and consuming only within 1e-4 let the second copy be
                // re-selected as a SECOND parallel node plane — a sub-mm sliver leaf between them whose
                // classification is unstable, emitting the wall on both planes (overlapping z-fighting faces,
                // the trace-case symptom) with rim holes. RED's boolean gets one surface there (the later op
                // carves the earlier face wherever the brush contains it); one shared node plane per registry
                // id is the extraction equivalent.
                if ((f.Id >= 0 && f.Id == node._planeId) || Coplanar(node._plane, f.Poly))
                {
                    continue;
                }

                CsgSharedSplit.SplitOneSeparate(
                    null!, -1, f.Poly, node._plane, -1, out List<CsgSharedSplit.PsVert>? ff, out List<CsgSharedSplit.PsVert>? bb);
                if (ff is not null)
                {
                    front.Add(new Face(f.Plane, f.Id, ff));
                }

                if (bb is not null)
                {
                    back.Add(new Face(f.Plane, f.Id, bb));
                }
            }

            work += front.Count + back.Count;

            node._front = new WorldBsp();
            node._back = new WorldBsp();
            nodes += 2;
            stack.Push((node._front, front));
            stack.Push((node._back, back));
        }

        stats = new Stats(nodes, leaves, work, false);
        return root;
    }

    /// <summary>
    /// Clips <paramref name="poly"/> (on face plane id <paramref name="facePlane"/>) down the world
    /// tree, appending the leaf fragments to <paramref name="output"/>. Cut vertices at each straddled
    /// node come from the registry's exact three-plane point, so any other face straddling the same
    /// node is cut at the byte-identical position — the shared-cut / watertight-by-construction
    /// property. A fragment cap guards pathological fan-out.
    /// </summary>
    public void Clip(
        PlaneRegistry reg, int facePlane, List<CsgSharedSplit.PsVert> poly,
        List<List<CsgSharedSplit.PsVert>> output, int maxFragments, out bool capped)
    {
        capped = false;
        var work = new Stack<(WorldBsp Node, List<CsgSharedSplit.PsVert> Poly)>();
        work.Push((this, poly));
        while (work.Count > 0)
        {
            (WorldBsp node, List<CsgSharedSplit.PsVert> p) = work.Pop();
            if (!node._hasPlane)
            {
                output.Add(p); // reached a convex leaf
                if (output.Count > maxFragments)
                {
                    capped = true;
                    return;
                }

                continue;
            }

            CsgSharedSplit.SplitOneSeparate(
                reg, facePlane, p, node._plane, node._planeId,
                out List<CsgSharedSplit.PsVert>? front, out List<CsgSharedSplit.PsVert>? back);

            // On-plane fragments follow the back side (inside/on) — the choice is immaterial to the
            // final fragment set (both children partition the remaining space) but keeps a coplanar
            // face routed consistently to one leaf.
            if (front is not null && node._front is not null)
            {
                work.Push((node._front, front));
            }

            if (back is not null && node._back is not null)
            {
                work.Push((node._back, back));
            }

            if (work.Count > maxFragments)
            {
                capped = true;
                return;
            }
        }
    }

    /// <summary>Feasibility slack (1 mm) when enumerating a leaf cell's vertices for its interior point. Double-
    /// precision triple solves land a true cell vertex within float noise (~1e-4 at 10²-m coords); 1 mm captures
    /// them without admitting far points. A leaf too thin to resolve ≥4 vertices is sub-resolution (RED's 1e-4
    /// build collapses it too) and is classified best-effort at its resolved centroid.</summary>
    private const float InteriorSlack = 1e-3f;


    /// <summary>One extracted open|solid boundary polygon: the portal loop, the node plane it lies on
    /// (normal pointing to the front / Distance&gt;0 side) with its registry id, and which side is open.</summary>
    internal readonly struct BoundaryPolygon
    {
        public BoundaryPolygon(List<CsgSharedSplit.PsVert> poly, CsgPlane plane, int planeId, bool openOnFront)
        {
            Poly = poly;
            Plane = plane;
            PlaneId = planeId;
            OpenOnFront = openOnFront;
        }

        public List<CsgSharedSplit.PsVert> Poly { get; }

        /// <summary>Node plane; its normal points to the front (Distance &gt; 0) side.</summary>
        public CsgPlane Plane { get; }

        public int PlaneId { get; }

        /// <summary>True ⇒ open on the +normal side (emit with this normal); false ⇒ open on the back (flip).</summary>
        public bool OpenOnFront { get; }
    }

    /// <summary>Extraction statistics for the report (portals emitted, leaves classified, degenerate/collapsed
    /// leaves, max/over-cap leaf constraint depth, cap hit).</summary>
    internal readonly record struct ExtractStats(int Portals, long Leaves, int Degenerate, int MaxCons, int OverCap, bool Capped);

    /// <summary>
    /// RED's watertight realisation (compiler-parity-notes.md — "leaf-based boundary extraction"): EXTRACTS
    /// the open|solid boundary face set from the tree instead of routing original faces through it. For every
    /// internal node the splitting plane is clipped to the node's convex cell (the node portal), split by BOTH
    /// child subtrees into leaf-atomic pieces, and each piece separating an OPEN leaf from a SOLID leaf (or the
    /// void) is emitted — the region of the node plane between exactly two leaves. Cut vertices are the
    /// registry's exact three-plane points, so a boundary and its coincident neighbour share bit-identical
    /// vertices: on a sealed level every open leaf is a bounded convex cell whose portal corners are all real
    /// three-plane brush corners, so the emitted set is watertight by construction (the only residual seams are
    /// exact-on-edge collinear T-junctions where two neighbours are subdivided differently — the t-joint fixer
    /// closes those cleanly, unlike the per-brush path's sub-mm float divergences). Iterative (explicit stacks)
    /// for stack safety on deep trees; a global fragment cap guards pathological fan-out.
    /// </summary>
    public List<BoundaryPolygon> Extract(
        PlaneRegistry reg, Func<Vec3, bool> isOpen, Vec3 wmin, Vec3 wmax, int maxFragments, out ExtractStats stats)
    {
        var output = new List<BoundaryPolygon>();
        Vec3 wc = wmin.Add(wmax).Scale(0.5f);
        float half = (MathF.Max(wmax.Sub(wmin).Length(), 1f) * 1.5f) + 16f; // quad half-extent that covers any plane

        // CONTENTS-CARRYING (the mission's construction). RED never probes: its accumulated solid carries
        // solidity through construction (each brush op re-clips and re-stamps the boundary faces it overlaps,
        // time-ordered — FUN_004a8220/FUN_0048bec0, no per-leaf contents field). GED's equivalent on this tree:
        // classify every convex leaf's open/solid state ONCE, by construction, at a GUARANTEED-INTERIOR point
        // (the vertex-enumeration centroid of the leaf cell) through the time-ordered fold (isOpen = "last brush
        // containing the point is air"). This is EXACT — the partition places every brush face plane as a
        // candidate node, so no brush boundary crosses a leaf interior and every interior point gives the same
        // verdict — and it replaces (a) the per-portal 1 mm probe that overshot thin leaves and dropped real
        // walls, and (b) the OpenAt-per-portal call that dominated solve time on non-convex operands. Extraction
        // now reads leaf states directly: a portal is emitted iff its two leaves differ (open|solid).
        CsgPlane[] bound = BoundPlanes(wc, half);
        long leafCount = 0;
        int degenerate = ClassifyLeaves(isOpen, bound, wc, half, ref leafCount, out int maxCons, out int overCap);

        var nodeStack = new Stack<(WorldBsp Node, List<(CsgPlane Plane, int Id, bool KeepFront)> Path)>();
        nodeStack.Push((this, new List<(CsgPlane, int, bool)>()));
        while (nodeStack.Count > 0)
        {
            (WorldBsp node, List<(CsgPlane Plane, int Id, bool KeepFront)> path) = nodeStack.Pop();
            if (node._hasPlane)
            {
                List<CsgSharedSplit.PsVert>? portal = BuildNodePortal(reg, node._plane, node._planeId, wc, half, path);
                if (portal is not null && portal.Count >= 3)
                {
                    // Split by the front subtree, then each piece by the back subtree, so every final piece
                    // lies between exactly one front leaf and one back leaf; carry each atom's leaf verdict.
                    var frontAtoms = new List<(bool Open, List<CsgSharedSplit.PsVert> Poly)>();
                    SplitBySubtree(reg, node._planeId, node._front, portal, frontAtoms);
                    foreach ((bool fOpen, List<CsgSharedSplit.PsVert> fa) in frontAtoms)
                    {
                        var atoms = new List<(bool Open, List<CsgSharedSplit.PsVert> Poly)>();
                        SplitBySubtree(reg, node._planeId, node._back, fa, atoms);
                        foreach ((bool bOpen, List<CsgSharedSplit.PsVert> ba) in atoms)
                        {
                            if (ba.Count < 3)
                            {
                                continue;
                            }

                            if (fOpen == bOpen)
                            {
                                continue; // interior (both open) or buried (both solid/void) — not a boundary
                            }

                            output.Add(new BoundaryPolygon(ba, node._plane, node._planeId, fOpen));
                            if (output.Count > maxFragments)
                            {
                                stats = new ExtractStats(output.Count, leafCount, degenerate, maxCons, overCap, true);
                                return output;
                            }
                        }
                    }
                }

                if (node._front is not null)
                {
                    nodeStack.Push((node._front, new List<(CsgPlane, int, bool)>(path) { (node._plane, node._planeId, true) }));
                }

                if (node._back is not null)
                {
                    nodeStack.Push((node._back, new List<(CsgPlane, int, bool)>(path) { (node._plane, node._planeId, false) }));
                }
            }
        }

        stats = new ExtractStats(output.Count, leafCount, degenerate, maxCons, overCap, false);
        return output;
    }

    /// <summary>
    /// Sets every convex leaf's open/solid verdict ONCE, by construction (the mission's contents-carrying).
    /// Walks the tree carrying the root→leaf bounding half-spaces; at each leaf computes a guaranteed-interior
    /// point (<see cref="LeafInteriorPoint"/>) and stores <c>isOpen(interior)</c> — the time-ordered fold = the
    /// last brush containing the leaf is air. Single-threaded (runs before the extract node walk). Returns the
    /// count of degenerate/sub-resolution leaves (thinner than the vertex-resolution band — RED's 1e-4 build
    /// collapses these; GED classifies them best-effort at their resolved centroid, and their sub-1e-4 boundary
    /// slivers are dropped by the downstream min-area filter — a documented collapse, not a leak).
    /// </summary>
    private int ClassifyLeaves(Func<Vec3, bool> isOpen, CsgPlane[] bound, Vec3 wc, float half, ref long leafCount, out int maxCons, out int overCap)
    {
        // Recursive DFS with ONE shared path (push before, pop after each child) and ONE reused constraint
        // buffer — no per-node list allocation (that copy was O(nodes·depth) GC churn). Depth = tree height
        // (observed ≤ ~50 across the corpus), well within the call stack.
        var path = new List<CsgPlane>(64);
        var buf = new CsgPlane[bound.Length + 64];
        var ws = new ChebyshevWorkspace(bound.Length + 64); // one reusable LP scratch for all leaves (single-threaded)
        int degenerate = 0;
        int maxc = 0;
        int over = 0;
        long leaves = 0;

        void Recurse(WorldBsp node)
        {
            if (!node._hasPlane)
            {
                leaves++;
                int nc = path.Count;
                if (nc > maxc)
                {
                    maxc = nc;
                }

                int total = nc + bound.Length;
                if (buf.Length < total)
                {
                    buf = new CsgPlane[total];
                }

                for (int i = 0; i < nc; i++)
                {
                    buf[i] = path[i];
                }

                for (int i = 0; i < bound.Length; i++)
                {
                    buf[nc + i] = bound[i];
                }

                if (!LeafInteriorPoint(buf, total, wc, half, ws, out Vec3 ip, out bool enumerated))
                {
                    degenerate++;
                }

                if (enumerated)
                {
                    over++; // fell back to vertex enumeration (the LP found no margin-strict interior)
                }

                node._open = isOpen(ip);
                node._classified = true;
                return;
            }

            // Inside the FRONT child = Distance(plane) ≥ 0 ⟺ Distance(flip) ≤ 0; inside the BACK child =
            // Distance(plane) ≤ 0. Constraint convention: inside the cell ⟺ Distance(constraint) ≤ 0.
            path.Add(node._plane.Flipped());
            Recurse(node._front!);
            path[path.Count - 1] = node._plane;
            Recurse(node._back!);
            path.RemoveAt(path.Count - 1);
        }

        Recurse(this);
        leafCount += leaves;
        maxCons = maxc;
        overCap = over;

        return degenerate;
    }

    /// <summary>Minimum Chebyshev radius for a leaf centre to count as strictly interior: the RED geometry band
    /// (1e-4). A centre this far from every bounding plane is unambiguously classified by <see cref="BrushVolume.Contains"/>
    /// (same tolerance), so its open/solid verdict is the true leaf contents. A cell whose deepest point is thinner
    /// is sub-resolution (RED's 1e-4 build collapses it too) and falls back to vertex enumeration.</summary>
    private const float MinRadius = CsgPlane.OnPlaneEpsilon;

    /// <summary>
    /// Guaranteed-interior point of the convex leaf cell {p : Distance(consᵢ) ≤ 0} ∩ world-bound box. Primary
    /// path is the <see cref="ChebyshevCenter"/> LP (blocker 1) — the exact deepest interior point in O(constraints)
    /// instead of the O(n³) vertex enumeration. When the LP returns a margin ≥ <see cref="MinRadius"/> that centre
    /// is the answer (<paramref name="enumerated"/> = false). A thinner/empty result falls back to
    /// <see cref="LeafInteriorPointEnumerate"/> — the exact vertex-enumeration oracle, kept both for the rare
    /// sub-resolution cell and as the test verification oracle. Returns false for a degenerate/sub-resolution cell.
    /// </summary>
    private static bool LeafInteriorPoint(CsgPlane[] all, int total, Vec3 wc, float half, ChebyshevWorkspace ws, out Vec3 point, out bool enumerated)
    {
        if (ChebyshevCenter.Solve(all, total, wc, half, ws, out Vec3 c, out float r) && r >= MinRadius)
        {
            point = c;
            enumerated = false;
            return true;
        }

        enumerated = true;
        return LeafInteriorPointEnumerate(all, total, wc, out point);
    }

    /// <summary>
    /// Exact vertex-enumeration interior point (the pre-LP construction, kept as the fallback + test oracle):
    /// the average of the cell's corner vertices (triple-plane intersections satisfying every constraint) is
    /// strictly interior for a bounded convex cell, so its open/solid verdict is the true leaf contents. Returns
    /// false for a degenerate/sub-resolution cell (&lt;4 vertices resolve); <paramref name="point"/> is then the
    /// best available estimate (resolved-vertex centroid, or the world centre).
    /// </summary>
    internal static bool LeafInteriorPointEnumerate(CsgPlane[] all, int total, Vec3 wc, out Vec3 point)
    {
        // Feasibility is tested against ALL constraints, so every kept vertex is a genuine cell corner. The full
        // set (not an early-stopped subset — a clustered subset averages to a near-boundary point and mis-classes)
        // is required for the interior guarantee.
        var sum = new Vec3(0, 0, 0);
        int found = 0;
        for (int a = 0; a < total; a++)
        {
            CsgPlane pa = all[a];
            for (int b = a + 1; b < total; b++)
            {
                CsgPlane pb = all[b];
                for (int c = b + 1; c < total; c++)
                {
                    if (!Solve3(pa, pb, all[c], out Vec3 p))
                    {
                        continue;
                    }

                    bool feasible = true;
                    for (int m = 0; m < total; m++)
                    {
                        if (all[m].Distance(p) > InteriorSlack)
                        {
                            feasible = false;
                            break;
                        }
                    }

                    if (feasible)
                    {
                        sum = sum.Add(p);
                        found++;
                    }
                }
            }
        }

        if (found >= 4)
        {
            point = sum.Scale(1f / found);
            return true;
        }

        // Degenerate / sub-resolution cell (thinner than the resolution band): best-effort at the resolved
        // centroid, else the world centre. Its sub-1e-4 boundary slivers are dropped downstream — a documented
        // collapse (RED's 1e-4 build collapses the same cells).
        point = found > 0 ? sum.Scale(1f / found) : wc;
        return false;
    }

    /// <summary>The six axis-aligned world-bound planes of the cube centred at <paramref name="wc"/> with the
    /// given half-extent (inside ⟺ Distance ≤ 0), so every leaf — including those open to the void — is a
    /// bounded cell for vertex enumeration.</summary>
    private static CsgPlane[] BoundPlanes(Vec3 wc, float half) => new[]
    {
        new CsgPlane(new Vec3(1, 0, 0), -(wc.X + half)),
        new CsgPlane(new Vec3(-1, 0, 0), wc.X - half),
        new CsgPlane(new Vec3(0, 1, 0), -(wc.Y + half)),
        new CsgPlane(new Vec3(0, -1, 0), wc.Y - half),
        new CsgPlane(new Vec3(0, 0, 1), -(wc.Z + half)),
        new CsgPlane(new Vec3(0, 0, -1), wc.Z - half),
    };

    /// <summary>Double-precision Cramer's-rule intersection of three planes (n·x + offset = 0). Returns false
    /// when near-parallel / ill-conditioned. Position accuracy is sufficient for an interior-point centroid;
    /// exact shared cut vertices come from <see cref="PlaneRegistry"/> elsewhere.</summary>
    private static bool Solve3(CsgPlane p, CsgPlane q, CsgPlane r, out Vec3 point)
    {
        double a11 = p.Normal.X, a12 = p.Normal.Y, a13 = p.Normal.Z;
        double a21 = q.Normal.X, a22 = q.Normal.Y, a23 = q.Normal.Z;
        double a31 = r.Normal.X, a32 = r.Normal.Y, a33 = r.Normal.Z;
        double det =
            (a11 * ((a22 * a33) - (a23 * a32))) -
            (a12 * ((a21 * a33) - (a23 * a31))) +
            (a13 * ((a21 * a32) - (a22 * a31)));
        if (Math.Abs(det) < 1e-9)
        {
            point = default;
            return false;
        }

        double b1 = -p.Offset, b2 = -q.Offset, b3 = -r.Offset;
        double x =
            ((b1 * ((a22 * a33) - (a23 * a32))) - (a12 * ((b2 * a33) - (a23 * b3))) + (a13 * ((b2 * a32) - (a22 * b3)))) / det;
        double y =
            ((a11 * ((b2 * a33) - (a23 * b3))) - (b1 * ((a21 * a33) - (a23 * a31))) + (a13 * ((a21 * b3) - (b2 * a31)))) / det;
        double z =
            ((a11 * ((a22 * b3) - (b2 * a32))) - (a12 * ((a21 * b3) - (b2 * a31))) + (b1 * ((a21 * a32) - (a22 * a31)))) / det;
        point = new Vec3((float)x, (float)y, (float)z);
        return true;
    }

    /// <summary>Builds the node portal: the splitting plane's world quad clipped to the node's convex cell
    /// (successively by every ancestor half-space). Returns null when the plane misses the cell.</summary>
    private static List<CsgSharedSplit.PsVert>? BuildNodePortal(
        PlaneRegistry reg, CsgPlane plane, int planeId, Vec3 wc, float half,
        List<(CsgPlane Plane, int Id, bool KeepFront)> path)
    {
        List<CsgSharedSplit.PsVert> poly = BuildQuad(plane, planeId, wc, half);
        foreach ((CsgPlane ap, int aid, bool keepFront) in path)
        {
            CsgSharedSplit.SplitOneSeparate(
                reg, planeId, poly, ap, aid,
                out List<CsgSharedSplit.PsVert>? f, out List<CsgSharedSplit.PsVert>? b);
            List<CsgSharedSplit.PsVert>? kept = keepFront ? f : b;
            if (kept is null || kept.Count < 3)
            {
                return null;
            }

            poly = kept;
        }

        return poly;
    }

    /// <summary>A large CCW quad on <paramref name="plane"/> centred at the world centre's projection, sized
    /// to cover the whole world on this plane. Corners carry only the plane id so later cuts tag exact triples.</summary>
    private static List<CsgSharedSplit.PsVert> BuildQuad(CsgPlane plane, int planeId, Vec3 wc, float half)
    {
        Vec3 n = plane.Normal;
        Vec3 p0 = wc.Sub(n.Scale(plane.Distance(wc)));
        Vec3 a = MathF.Abs(n.X) < 0.9f ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        Vec3 u = n.Cross(a).Normalized();
        Vec3 v = n.Cross(u); // u × v = n ⇒ CCW winding w.r.t. the front normal
        var planes = new[] { planeId };
        var uv = default(Uv);
        return new List<CsgSharedSplit.PsVert>(4)
        {
            new(p0.Sub(u.Scale(half)).Sub(v.Scale(half)), uv, planes),
            new(p0.Add(u.Scale(half)).Sub(v.Scale(half)), uv, planes),
            new(p0.Add(u.Scale(half)).Add(v.Scale(half)), uv, planes),
            new(p0.Sub(u.Scale(half)).Add(v.Scale(half)), uv, planes),
        };
    }

    /// <summary>Splits <paramref name="poly"/> down a subtree, appending each leaf-atomic piece to
    /// <paramref name="output"/> tagged with that leaf's precomputed open/solid verdict (each piece lies wholly
    /// within one leaf of the subtree). A missing child (never built for an internal node) is treated as
    /// void = solid.</summary>
    private static void SplitBySubtree(
        PlaneRegistry reg, int facePlane, WorldBsp? subtree, List<CsgSharedSplit.PsVert> poly,
        List<(bool Open, List<CsgSharedSplit.PsVert> Poly)> output)
    {
        var stack = new Stack<(WorldBsp? Node, List<CsgSharedSplit.PsVert> Poly)>();
        stack.Push((subtree, poly));
        while (stack.Count > 0)
        {
            (WorldBsp? nd, List<CsgSharedSplit.PsVert> p) = stack.Pop();
            if (nd is null || !nd._hasPlane)
            {
                output.Add((nd?._open ?? false, p)); // leaf verdict (void ⇒ solid)
                continue;
            }

            CsgSharedSplit.SplitOneSeparate(
                reg, facePlane, p, nd._plane, nd._planeId,
                out List<CsgSharedSplit.PsVert>? f, out List<CsgSharedSplit.PsVert>? b);
            if (f is not null)
            {
                stack.Push((nd._front, f));
            }

            if (b is not null)
            {
                stack.Push((nd._back, b));
            }
        }
    }

    /// <summary>
    /// Pre-classifies EVERY convex leaf's open/solid verdict by construction (the contents-carrying pass), at a
    /// guaranteed-interior Chebyshev-centre point (exact, not the ±eps probe). Call once before reading verdicts
    /// with <see cref="ClassifyPoint"/> so its navigation returns the stored contents (matching the node-portal
    /// extraction's classification exactly, instead of re-probing <c>isOpen</c> at the ±eps point which
    /// mis-verdicts thin sliver leaves). Returns the degenerate/sub-resolution leaf count.
    /// </summary>
    public int ClassifyAllLeaves(Func<Vec3, bool> isOpen, Vec3 wmin, Vec3 wmax, out int maxCons, out int overCap)
    {
        Vec3 wc = wmin.Add(wmax).Scale(0.5f);
        float half = (MathF.Max(wmax.Sub(wmin).Length(), 1f) * 1.5f) + 16f;
        CsgPlane[] bound = BoundPlanes(wc, half);
        long leafCount = 0;
        return ClassifyLeaves(isOpen, bound, wc, half, ref leafCount, out maxCons, out overCap);
    }

    /// <summary>
    /// Classifies the convex leaf containing <paramref name="p"/> as open/solid (memoised per leaf via the
    /// time-ordered fold <paramref name="isOpen"/>), the RED-faithful in/out survival used instead of the
    /// fixed-eps probe. Thread-safe: the first probe to reach a leaf sets its verdict under a lock; a leaf's
    /// solidity is single-valued (convex, no boundary through its interior) so first-touch is consistent.
    /// After <see cref="ClassifyAllLeaves"/> every leaf is already classified, so this just returns the stored
    /// verdict (the exact contents-carrying value, not a re-probe at <paramref name="p"/>).
    /// </summary>
    public bool ClassifyPoint(Vec3 p, Func<Vec3, bool> isOpen)
    {
        WorldBsp node = this;
        while (node._hasPlane)
        {
            node = node._plane.Distance(p) > 0f ? node._front! : node._back!;
        }

        if (!node._classified)
        {
            lock (node)
            {
                if (!node._classified)
                {
                    node._open = isOpen(p);
                    node._classified = true;
                }
            }
        }

        return node._open;
    }

    /// <summary>
    /// Picks the best split face for a region: for a sampled subset of candidate planes, count how
    /// many region faces each straddles and how it balances the front/back partition, and choose the
    /// lowest score (<c>splits*8 + |front − back|</c>), with an axial bonus. Sampling bounds the build
    /// at O(candidates·faces) per node rather than O(faces²).
    /// </summary>
    private static int SelectPartition(List<Face> region, int maxCandidates)
    {
        int n = region.Count;
        int stride = n <= maxCandidates ? 1 : n / maxCandidates;
        int bestIndex = 0;
        long bestScore = long.MaxValue;

        for (int ci = 0; ci < n; ci += stride)
        {
            CsgPlane cand = region[ci].Plane;
            int splits = 0, front = 0, back = 0;
            for (int fi = 0; fi < n; fi++)
            {
                if (fi == ci)
                {
                    continue;
                }

                Classify(cand, region[fi].Poly, out bool hasFront, out bool hasBack);
                if (hasFront && hasBack)
                {
                    splits++;
                    front++;
                    back++;
                }
                else if (hasFront)
                {
                    front++;
                }
                else if (hasBack)
                {
                    back++;
                }
            }

            long score = (splits * 8L) + Math.Abs(front - back);
            if (IsAxial(cand.Normal))
            {
                score -= 4; // a small preference for axis-aligned splits (fewer downstream slivers)
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = ci;
            }
        }

        return bestIndex;
    }

    private static void Classify(CsgPlane plane, List<CsgSharedSplit.PsVert> poly, out bool hasFront, out bool hasBack)
    {
        hasFront = false;
        hasBack = false;
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            float d = plane.Distance(v.Pos);
            if (d > Band)
            {
                hasFront = true;
            }
            else if (d < -Band)
            {
                hasBack = true;
            }

            if (hasFront && hasBack)
            {
                return;
            }
        }
    }

    private static bool IsAxial(Vec3 n) =>
        MathF.Abs(n.X) > 0.99999f || MathF.Abs(n.Y) > 0.99999f || MathF.Abs(n.Z) > 0.99999f;

    private static bool Coplanar(CsgPlane node, List<CsgSharedSplit.PsVert> poly)
    {
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            if (MathF.Abs(node.Distance(v.Pos)) > Band)
            {
                return false;
            }
        }

        return true;
    }
}
