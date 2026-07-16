using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// The core CSG: computes the open/solid boundary faces from the air and solid
/// brushes, in RED's incremental, robust style — no global boolean. Each brush
/// face is split only where another brush's boundary actually crosses it, then a
/// fragment survives iff open space is on exactly one side of it (evaluated as a
/// time-ordered fold: the last brush containing a point decides open vs solid).
/// Surviving fragments are oriented so the normal points into open space, which
/// is RF's stored convention. This avoids the fragmentation and numerical
/// collapse of repeated BSP booleans on complex, non-convex real brushes.
/// </summary>
public sealed class CsgSolver
{
    /// <summary>Offset used to sample just off a fragment's front/back for the open test.</summary>
    private const float SampleEps = 0.02f;
    private const float Band = CsgPlane.OnPlaneEpsilon;

    /// <summary>Safety cap on fragments produced from one source face (prevents pathological explosion).</summary>
    private const int MaxFragmentsPerFace = 2048;

    /// <summary>Max distance an original corner may be snapped to its plane-triple intersection (sub-mm; never an authored feature).</summary>
    private const float CornerSnap = 1e-3f;

    /// <summary>Default coincident-corner merge tolerance for the EdgeLerpSplit path (flagship 19). Measured-optimal:
    /// the corpus tolerance probe (edge_lerp_tol_probe.txt) shows the benefit band [0.5 mm, 1.5 mm] holds every
    /// improvement (dm04 14→13, ctf01 11→8, ctf07 90→74) with ZERO regressions — below 0.5 mm pure-lerp abandons
    /// the registry-triple sharing before enough corners merge (dm04 regresses to 16); at 2 mm the merge over-reaches
    /// and re-opens ctf05 (0→4). 1 mm sits mid-band, far below any authored feature (distinct walls/lips ≥2 cm).</summary>
    private const float EdgeLerpDefaultMergeTol = 1e-3f;

    // ---- Convex decomposition of concave brushes (compiler-parity-notes.md, the CSG watertightness cohort) ----
    // A concave brush's own face planes, extended as one solid-leaf BSP, partition its (closed) volume into
    // convex inside-cells — the standard exact convex decomposition. We build that BSP (budgeted) for
    // eligible closed-ish concave solids and clip foreign faces down it exactly like a convex brush, so the
    // brush silhouette is cut with shared registry vertices (watertight by construction). Ineligible /
    // oversized / pathological brushes fall back to the crossing-face cutter unchanged (never crash, never
    // regress). Budgets guard the 11.6 GB terrain-shell explosion; tuned from the corpus sweep.
    private const int DecompMinFaces = 5;
    private const int DecompMaxFaces = 1200;
    private const int DecompMaxNodes = 6000;
    private const int DecompMaxWork = 400_000;

    // Convex-piece budget per brush. Decomposition is applied ONLY to SOLID concave brushes and capped to
    // COMPACT ones, because GED clips per-brush rather than against RED's single accumulated BSP: the cells'
    // shared internal planes are not globally shared with the neighbour faces, so a large decomposition
    // OVER-splits (its internal-plane cuts land on surviving faces the neighbour is not cut along, churning
    // the open-edge topology). For a SOLID brush the cells are BURIED, so the union side that survives is the
    // exterior and the internal cuts stay under the wall; capping to few cells keeps the exterior churn below
    // the watertightness it buys back at the real silhouette (measured net-positive, no per-level regression).
    // AIR brushes are excluded entirely — their cells are OPEN space, so the internal cuts land on surviving
    // faces and explode the count (dm06 0→312, ctf02 36→728); those keep the crossing-face fallback.
    // See compiler-parity-notes.md (the CSG watertightness cohort) for the full before/after and rationale.
    private const int DecompMaxCells = 20;

    private int _cappedFaces;
    private int _decomposed;
    private int _decompFallback;
    private int _decompCells;
    private int _decompMaxCells;

    // ---- World-BSP accumulator (RED's single accumulated world BSP — see WorldBsp / compiler-parity-notes.md) ----
    // Node planes drawn from ALL brush faces (bounded set); fewest-split heuristic keeps it from exploding.
    // Budgets are the 11.6 GB terrain lesson: a level that exceeds them falls back to the per-brush path.
    private const int WorldBspMaxNodes = 2_000_000;
    private const long WorldBspMaxWork = 60_000_000L;
    private const int WorldBspMaxCandidates = 48;

    private bool _useWorldBsp;
    private WorldBsp? _worldBsp;
    private long _worldFragments;

    // ---- Leaf-based boundary extraction (RED's watertight realisation — see WorldBsp.Extract) ----
    // Safety cap on extracted portals: a well-formed level is ~15k; far above ⇒ pathological fan-out,
    // fall back to the per-brush accumulator (a partial extraction would tear holes).
    private const int MaxExtractPortals = 2_000_000;

    private bool _useLeafExtraction;
    private bool _sourceFaceEmission;
    private bool _incremental;
    private bool _brepBoundary;
    private bool _partitionClip;
    private bool _globalPartition;
    private bool _sharedBsp;
    private bool _fusedPartition;

    // Shared-BSP MATCHED-EDGE cap gate (flagship 31): a near-parallel-SIBLING cap cutter is kept only where a
    // surviving world face on the cutter's plane carries a real boundary EDGE along the cap∩cutter seam line, so
    // the cap's sibling cut pairs with a world edge by construction (the precise form of flagship 16's "the AABB
    // gate is too coarse" fix — measured: it keeps the ctf07/ctfwlpro/dmedge sibling wins and drops dm07's
    // unmatched sibling over-cut, 20→16). Set true only on the shared-BSP path; the world faces by registry plane
    // id are snapshot after step (a). Real crossings are ungated (they must always cut where the world is cut).
    private bool _capEdgeMatchGate;
    private Dictionary<int, List<WFace>>? _worldFacesByPlane;

    // HYBRID cap (flagship 31 — the ctf01 fix + the clean-corpus form): the cap is volume-clipped against the
    // earlier brushes FIRST (the incremental cap that keeps stepped-channel geometry from over-cutting), then
    // each volume-clipped fragment is routed down the accumulated partition (real crossings + matched-edge
    // siblings) to ADD the terrain-pair / membrane stations the volume clip misses. Measured: equal-or-better
    // than the incremental default on EVERY corpus level (ctf07 74->42, dmedge 4->0; ctf01 8->8 held — the pure
    // partition cap over-cut it to 17), 0 regressions, 0 watertight zeros broken. Mirrors RED's own phase-0
    // (brush BSP volume) + phase-3 (spanning-face clip down the partition) two-phase structure. Set on the
    // shared-BSP path.
    private bool _capHybrid;
    private bool _edgeLerpSplit;
    private bool _regionWise = true;
    private float? _edgeMergeTol;
    private int _incWorldFaces;
    private int _incDissolved;

    // ---- Global accumulated partition (flagship 16, CompileOptions.GlobalPartition) ----
    // Per brush: the DISTINCT (registry-folded) non-portal face planes with their supporting-face AABBs — the
    // node planes this brush contributes to the accumulated partition. A cap/world face is routed down the
    // partition by splitting it at every earlier-brush node plane whose AABB straddles it (the bbox gate,
    // RED's FUN_0048e4f0), so near-parallel siblings (distinct registry ids ≥ the 2e-3 fold) BOTH cut it and
    // coincident cuts land on the byte-identical registry triple. Built lazily by the global-partition fold.
    private (CsgPlane Geom, int Id, Vec3 Min, Vec3 Max)[][] _partFaces = Array.Empty<(CsgPlane, int, Vec3, Vec3)[]>();

    // Snapshot (per fold step) of the accumulated world faces' AABBs keyed by registry plane id — the
    // "is this sibling plane a REAL surviving world boundary near the cap" gate. A cap's near-parallel sibling
    // cut is only safe (matched) where the adjacent world face carries that station; adding it where no world
    // face survives on that plane opens an unmatched T-junction (the ctf01/dm07 cohort). Rebuilt each brush.
    private Dictionary<int, List<(Vec3 Min, Vec3 Max)>>? _worldByPlane;

    // ---- B-rep cap re-cut (flagship 14, CompileOptions.BRepBoundary) ----
    // Per-brush map: brush b's face plane id -> the set of OTHER registry plane ids that produced a cut
    // vertex ON that plane while step (a) split the accumulated world faces by b. Step (b) then re-cuts b's
    // cap face on that plane by exactly those planes, so the cap acquires the SAME registry-triple vertices
    // the flanking world faces already carry (RED's single-partition coincidence, realised surgically —
    // NOT an auto-sewing shared-edge mesh, which the binary shows RED does not maintain). Reset per brush.
    private Dictionary<int, HashSet<int>>? _capCut;

    // Partition-clip (flagship 15): per b face plane -> per OTHER registry plane -> the world-cut vertex
    // POSITIONS that landed on b's plane via that other plane in step (a). A plane carrying >=2 well-separated
    // positions is a REAL world edge crossing the cap (a 2-corner chord, e.g. dm04's terrain plane 35 with
    // Xa+Xb); a plane with a single grazing corner (the near-parallel siblings 1312/1313) is NOT an edge and
    // re-cutting the cap by its infinite extent only opens spurious slivers. PartitionClip cuts the cap by the
    // real-edge planes only — RED's "the cap is the operand's own face clipped down the partition that carries
    // the world's cut chord", realised from the recorded chord itself. Null unless PartitionClip is set.
    private Dictionary<int, Dictionary<int, List<Vec3>>>? _capCutPos;
    private int _brepCapCuts;

    // Guard: a cap plane touched by more than this many distinct world planes falls back to the plain clip
    // (the cavity contact is bounded in practice; the cap only receives cuts where the world was actually
    // split, so this trips only on pathological fan-out).
    private const int MaxCapCutPlanes = 96;

    /// <summary>
    /// Probe offset for the incremental fold's volume/fold-state verdicts. 2e-3 (the registry coincidence
    /// scale), NOT the per-brush path's 2 cm: the fold probes a fragment's two sides against real volumes,
    /// and a 2 cm probe overshoots any feature thinner than 2 cm — measured decisively: dm01's "real 2 cm
    /// lip" cohort (10 open edges) was a probe artifact (0 at 2e-3), dm04 133→32, kothcow 18→0, dm05 18→0.
    /// Finer is NOT better: below ~1e-3 the probe lands inside ray-parity's confusion band next to
    /// near-coincident organic surfaces (dm02 0→12 at 1e-3). Swept 3e-4…5e-3; 2e-3 is the corpus optimum.
    /// </summary>
    private float _incEps = 2e-3f;

    // BoundedVolumeClip (flagship 35): extent-gate the concave-cell volume-classification clip (ConvexClip) so a
    // cell plane cuts a foreign polygon only where the polygon overlaps the bounded FACE that supplied the plane
    // (RED's FUN_0048e4f0/FUN_004c9af0 extent gate mirrored INSIDE the volume walk). Wired on the SharedBsp step (a)
    // SplitPolyByBrushClassified and step (b) hybrid-cap ClipAgainstEarlierBrushes → SplitPolyByBrush. MEASURED
    // DECISIVELY NET-NEGATIVE (flagship 35, bvc_corpus.txt): better=0 worse=10 zeros-broken=3, and it closes NONE
    // of dm04's residual six — the per-cell-face extent is the wrong granularity (a large foreign face is trivially
    // "wholly outside" a small terrain triangle's AABB, so the gate suppresses legitimate classification cuts). Kept
    // selectable + default OFF as the reproducible measurement (as FusedPartition kept its stormed form); production
    // is byte-identical with the flag off. The convex-hull PsBsp.Clip path is intentionally NOT gated (front-of-any
    // face is always outside a convex hull, so there is no phantom to suppress — gating it measured worse still).
    private bool _boundedVolumeClip;
    private const float BvcExtentEps = 1e-3f;

    // Classification-only convex decomposition (incremental fold): per-brush oriented cells used for EXACT
    // point containment — never for cutting, so the measured air-cell cut explosion (dm06 0→312) does not
    // apply. Replaces ray-parity Contains for fold verdicts: the parity cavity-fill heuristic misfires a few
    // mm from step/notch corners (all perturbed escape rays graze adjacent faces — measured on ctf01's
    // uid=11166 stepped channel room, where it read an open notch as air and dropped a 20 m wall strip).
    private const int ClassCellCap = 96;
    private ConvexCell[]?[] _classCells = Array.Empty<ConvexCell[]?>();

    // Per brush → per face: +1 the authored plane points OUT of the brush, -1 it points in, 0 unknown.
    // Computed once (convex: sign at a guaranteed-interior point, the vertex average; closed non-convex:
    // the combinatorial manifold orientation). Step (b) then never probes the brush's OWN interior — the
    // interior state IS the brush's kind — which removes the near-cap probe fragility (a hex post whose
    // authored bottom cap is a shallow cone defeated the convex ±2 mm containment by the tilt epsilon,
    // reading room air behind its own boundary and dropping the cap: the dm12/dm09/dm19/ctf03 12-edge rings).
    private sbyte[][] _faceOut = Array.Empty<sbyte[]>();

    /// <summary>Exact containment for fold verdicts: oriented classification cells when available, else the
    /// brush volume's convex-plane / ray-parity test.</summary>
    private bool IncContains(int bi, Vec3 p)
    {
        ConvexCell[]? cells = bi < _classCells.Length ? _classCells[bi] : null;
        if (cells is null)
        {
            return _volumes[bi].Contains(p);
        }

        foreach (ConvexCell cell in cells)
        {
            if (p.X < cell.Min.X - Band || p.X > cell.Max.X + Band ||
                p.Y < cell.Min.Y - Band || p.Y > cell.Max.Y + Band ||
                p.Z < cell.Min.Z - Band || p.Z > cell.Max.Z + Band)
            {
                continue;
            }

            bool inside = true;
            foreach ((CsgPlane plane, int _) in cell.Planes)
            {
                if (plane.Distance(p) > Band)
                {
                    inside = false;
                    break;
                }
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a non-convex brush's classification cells: solid-leaf BSP over its OUTWARD-oriented face
    /// planes (authored windings are unreliable — each plane's side is probed against the volume), inside
    /// leaves collected as convex half-space sets. Null (fall back to ray parity) for open shells, oversized
    /// brushes, or budget hits.
    /// </summary>
    /// <summary>Per-face outward orientation of brush <paramref name="bi"/> (see <see cref="_faceOut"/>).</summary>
    private sbyte[] BuildFaceOrientations(int bi)
    {
        List<CsgFace> faces = _brushFaces[bi];
        var result = new sbyte[faces.Count];
        if (_volumes[bi].IsConvexVolume)
        {
            // A convex brush's vertex average is strictly interior; the authored plane is outward iff the
            // interior point lies behind it.
            var sum = new Vec3(0, 0, 0);
            int count = 0;
            foreach (CsgFace f in faces)
            {
                foreach (CsgVertex v in f.Vertices)
                {
                    sum = sum.Add(v.Position);
                    count++;
                }
            }

            if (count == 0)
            {
                return result;
            }

            Vec3 interior = sum.Scale(1f / count);
            for (int fi = 0; fi < faces.Count; fi++)
            {
                if (faces[fi].IsPortal || faces[fi].Vertices.Count < 3)
                {
                    continue;
                }

                float d = faces[fi].Plane.Distance(interior);
                result[fi] = d < -1e-3f ? (sbyte)1 : d > 1e-3f ? (sbyte)-1 : (sbyte)0;
            }

            return result;
        }

        // Closed non-convex: the combinatorial manifold orientation (no probes).
        var polys = new List<List<CsgSharedSplit.PsVert>>();
        var fiMap = new List<int>();
        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace f = faces[fi];
            if (f.IsPortal || f.Vertices.Count < 3)
            {
                continue;
            }

            var poly = new List<CsgSharedSplit.PsVert>(f.Vertices.Count);
            foreach (CsgVertex v in f.Vertices)
            {
                poly.Add(new CsgSharedSplit.PsVert(v.Position, v.Uv, Array.Empty<int>()));
            }

            polys.Add(poly);
            fiMap.Add(fi);
        }

        if (polys.Count < 4)
        {
            return result;
        }

        bool[]? flips = TryManifoldFlips(polys);
        if (flips is null)
        {
            return result; // unorientable — step (b) falls back to the probe verdicts
        }

        for (int i = 0; i < fiMap.Count; i++)
        {
            result[fiMap[i]] = flips[i] ? (sbyte)-1 : (sbyte)1;
        }

        return result;
    }

    private ConvexCell[]? BuildClassificationCells(int bi)
    {
        List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)> raw = CollectBspFaces(bi);

        // STRICT gates: the shell must be an exactly closed 2-manifold with a consistent COMBINATORIAL
        // orientation (flood-fill across shared edges; global sign by signed volume — no containment probes,
        // which are exactly what misfires near step corners). A cell set from a leaky/ambiguous shell is
        // subtly wrong in ways the ray-parity fallback is not (measured: unguarded cells regressed dm05 0→8,
        // ctf01 39→43); a clean stepped/notched functional brush — the case parity's cavity-fill heuristic
        // misfires on — passes.
        if (raw.Count < 4 || raw.Count > DecompMaxFaces || !IsExactlyClosed(raw))
        {
            return null;
        }

        List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)>? input = OrientByManifold(raw);
        if (input is null)
        {
            return null;
        }

        CsgSharedSplit.PsBsp? tree = CsgSharedSplit.PsBsp.Build(input, DecompMaxNodes, DecompMaxWork, out _);
        List<(CsgPlane Plane, int Id)[]>? constraints = tree?.CollectCells(ClassCellCap);
        if (constraints is null)
        {
            return null;
        }

        BrushVolume vol = _volumes[bi];
        var cells = new List<ConvexCell>(constraints.Count);
        foreach ((CsgPlane Plane, int Id)[] planes in constraints)
        {
            if (TryCellAabb(planes, vol.Min, vol.Max, out Vec3 cmin, out Vec3 cmax))
            {
                cells.Add(new ConvexCell(planes, cmin, cmax));
            }
        }

        return cells.Count > 0 ? cells.ToArray() : null;
    }

    private int _sfVerbatim;
    private int _sfCrossed;
    private int _sfDropped;
    private int _extractedPortals;
    private int _attrContainment;
    private int _attrNearest;
    private int _unattributed;
    private int _leafDegenerate;
    private int _leafMaxCons;
    private int _leafOverCap;

    /// <summary>Enables RED's single accumulated world BSP for the split (vs the per-brush accumulator).</summary>
    public bool UseWorldBsp
    {
        get => _useWorldBsp;
        set => _useWorldBsp = value;
    }

    /// <summary>Enables RED's leaf-based boundary EXTRACTION (vs route-faces / per-brush). Takes precedence.</summary>
    public bool UseLeafExtraction
    {
        get => _useLeafExtraction;
        set => _useLeafExtraction = value;
    }

    /// <summary>Under <see cref="UseLeafExtraction"/>, emit boundary geometry from the SOURCE face polygons
    /// (RED's binary-verified face semantics) instead of re-tessellated node-plane leaf portals.</summary>
    public bool SourceFaceEmission
    {
        get => _sourceFaceEmission;
        set => _sourceFaceEmission = value;
    }

    /// <summary>RED's INCREMENTAL BOUNDARY ACCUMULATOR (flagship 11) — the DEFAULT CSG path since the flip.
    /// Runs unless an explicit opt-in (<see cref="UseLeafExtraction"/> / <see cref="UseWorldBsp"/>) claims the
    /// build; set to false to select the per-brush accumulator.</summary>
    public bool IncrementalAccumulator
    {
        get => _incremental;
        set => _incremental = value;
    }

    /// <summary>Construction-time B-rep cap re-cut on the incremental fold (flagship 14). See
    /// <see cref="CompileOptions.BRepBoundary"/>. Default OFF.</summary>
    public bool BRepBoundary
    {
        get => _brepBoundary;
        set => _brepBoundary = value;
    }

    /// <summary>Partition-consistent operand clipping on the incremental fold (flagship 15). See
    /// <see cref="CompileOptions.PartitionClip"/>. Supersedes <see cref="BRepBoundary"/>. Default OFF.</summary>
    public bool PartitionClip
    {
        get => _partitionClip;
        set => _partitionClip = value;
    }

    /// <summary>THE GLOBAL ACCUMULATED PARTITION on the incremental fold (flagship 16). See
    /// <see cref="CompileOptions.GlobalPartition"/>. Supersedes <see cref="BRepBoundary"/> /
    /// <see cref="PartitionClip"/> conceptually. Default OFF.</summary>
    public bool GlobalPartition
    {
        get => _globalPartition;
        set => _globalPartition = value;
    }

    /// <summary>RED's AUTHENTIC SINGLE ACCUMULATED SHARED BSP (flagship 31) — the persistent shared boundary
    /// with BOTH world faces and caps routed down ONE accumulated partition symmetrically. See
    /// <see cref="CompileOptions.SharedBsp"/>. Takes precedence over the plain incremental/global-partition
    /// folds when set. Default OFF while measured.</summary>
    public bool SharedBsp
    {
        get => _sharedBsp;
        set => _sharedBsp = value;
    }

    /// <summary>True after <see cref="Solve"/> when the shared-BSP fold produced the boundary.</summary>
    public bool SharedBspActive { get; private set; }

    /// <summary>Extent-gate the brush volume-classification clip (flagship 35). See
    /// <see cref="CompileOptions.BoundedVolumeClip"/>. Applies inside the SharedBsp step-(a)/(b) volume clip; a
    /// brush plane cuts a foreign polygon only where the crossing overlaps the bounded supporting face. Default OFF.</summary>
    public bool BoundedVolumeClip
    {
        get => _boundedVolumeClip;
        set => _boundedVolumeClip = value;
    }

    /// <summary>THE FUSION (flagship 18): every source face routed down ONE global partition, survival from the
    /// world-level convex-leaf contents. See <see cref="CompileOptions.FusedPartition"/>. Explicit opt-in that
    /// takes precedence over the incremental default; falls back to the incremental accumulator when the world
    /// tree is over budget. Default OFF.</summary>
    public bool FusedPartition
    {
        get => _fusedPartition;
        set => _fusedPartition = value;
    }

    /// <summary>True after <see cref="Solve"/> when the fused-partition path produced the boundary.</summary>
    public bool FusedPartitionActive { get; private set; }

    /// <summary>CONSTRUCTION-TIME on-edge cut arithmetic + shared vertex identity on the incremental fold
    /// (flagship 19). See <see cref="CompileOptions.EdgeLerpSplit"/>. Default OFF.</summary>
    public bool EdgeLerpSplit
    {
        get => _edgeLerpSplit;
        set => _edgeLerpSplit = value;
    }

    /// <summary>Coincident-corner merge tolerance for <see cref="EdgeLerpSplit"/> (metres); null = fold default.</summary>
    public float? EdgeMergeTolerance
    {
        get => _edgeMergeTol;
        set => _edgeMergeTol = value;
    }

    /// <summary>REGION-WISE coincident-face resolution (flagship 23B). See
    /// <see cref="CompileOptions.RegionWiseCoincidence"/>. Default ON.</summary>
    public bool RegionWiseCoincidence
    {
        get => _regionWise;
        set => _regionWise = value;
    }

    /// <summary>True after <see cref="Solve"/> when the incremental fold ran with EdgeLerpSplit identity.</summary>
    public bool EdgeLerpSplitActive { get; private set; }

    /// <summary>Distinct shared vertex ids issued by the EdgeLerpSplit store (instrumentation).</summary>
    public int EdgeSharedVertices { get; private set; }

    /// <summary>Coincident authored/cut corners merged to a shared id under EdgeLerpSplit (instrumentation).</summary>
    public int EdgeCornerMerges { get; private set; }

    /// <summary>Cap fragments re-cut by a flanking world plane during the B-rep pass (instrumentation).</summary>
    public int BRepCapCuts => _brepCapCuts;

    /// <summary>True after <see cref="Solve"/> when the incremental accumulator produced the boundary.</summary>
    public bool IncrementalActive { get; private set; }

    /// <summary>True after <see cref="Solve"/> when the global-partition fold produced the boundary.</summary>
    public bool GlobalPartitionActive { get; private set; }

    /// <summary>Boundary faces in the accumulated world list at the end of the incremental fold (pre-resolve).</summary>
    public int IncWorldFaces => _incWorldFaces;

    /// <summary>World-face fragments dissolved in place by a later brush during the incremental fold.</summary>
    public int IncDissolved => _incDissolved;

    /// <summary>True after <see cref="Solve"/> when the boundary was extracted from the leaf tree (not a fallback).</summary>
    public bool LeafExtractionActive { get; private set; }

    /// <summary>Source-face emission: faces emitted verbatim (un-crossed), subdivided (crossed), and dropped.</summary>
    public int SfVerbatim => _sfVerbatim;

    public int SfCrossed => _sfCrossed;

    public int SfDropped => _sfDropped;

    /// <summary>Open|solid boundary portals emitted by the leaf extraction.</summary>
    public int ExtractedPortals => _extractedPortals;

    /// <summary>Portals attributed to a covering same-plane source face (the fidelity path).</summary>
    public int AttributedByContainment => _attrContainment;

    /// <summary>Portals attributed to the nearest same-plane source face (no exact cover — ambiguity fallback).</summary>
    public int AttributedByNearest => _attrNearest;

    /// <summary>Portals with no same-plane source face (should be ~0; a fidelity anomaly if not).</summary>
    public int Unattributed => _unattributed;

    /// <summary>Degenerate/sub-resolution leaves classified best-effort (collapse cohort).</summary>
    public int LeafDegenerate => _leafDegenerate;

    /// <summary>Deepest leaf constraint count (BSP path length) reached during classification.</summary>
    public int LeafMaxCons => _leafMaxCons;

    /// <summary>Leaves whose constraint set exceeded the enumeration cap (candidate generation pruned).</summary>
    public int LeafOverCap => _leafOverCap;

    /// <summary>True after <see cref="Solve"/> when the split actually ran on the world BSP (not the fallback).</summary>
    public bool WorldBspActive { get; private set; }

    /// <summary>True when the world-BSP build exceeded its budget and the solve fell back to the per-brush path.</summary>
    public bool WorldBspBudgetExceeded { get; private set; }

    /// <summary>World-BSP node / leaf counts (0 unless <see cref="UseWorldBsp"/>).</summary>
    public int WorldBspNodes { get; private set; }

    public int WorldBspLeaves { get; private set; }

    /// <summary>Total leaf fragments produced routing every boundary face through the world tree.</summary>
    public long WorldBspFragments => _worldFragments;

    /// <summary>Number of source faces that hit the fragment cap (a build-quality warning signal).</summary>
    public int CappedFaces => _cappedFaces;

    /// <summary>Concave brushes clipped via their convex-decomposition BSP (vs the crossing-face fallback).</summary>
    public int DecomposedBrushes => _decomposed;

    /// <summary>Eligible-looking concave brushes that fell back (budget exceeded / pathological).</summary>
    public int DecompFallbackBrushes => _decompFallback;

    /// <summary>Total convex pieces across all decomposed brushes.</summary>
    public int DecompTotalPieces => _decompCells;

    /// <summary>Largest convex-piece count of any single decomposed brush.</summary>
    public int DecompMaxPieces => _decompMaxCells;

    private const float GridCell = 12f;

    private readonly List<BrushVolume> _volumes = new();
    private readonly List<List<CsgFace>> _brushFaces = new();
    private readonly List<Vec3[]> _faceAabbMin = new();
    private readonly List<Vec3[]> _faceAabbMax = new();

    // Shared-split state: a level-wide plane registry plus, per brush face, its canonical face-plane id
    // and per-vertex plane-set (see CsgSharedSplit / PlaneRegistry) — the substrate for RED-style
    // shared-plane splitting so adjacent/coincident faces cut along identical lines.
    private readonly PlaneRegistry _registry = new();
    private readonly List<int[]> _facePlaneId = new();       // per brush → per face → plane id
    private readonly List<int[][][]> _vertexPlanes = new();  // per brush → per face → per vertex → plane-set

    // Per brush → its solid BSP volume (RED's phase-0 brush volume, node planes from its own face
    // planes), the operand the accumulator clips other faces against. Built once, before the parallel
    // solve, so the clip is read-only and thread-safe. Null for a brush with no usable faces.
    // Populated for CONVEX brushes only (a convex brush's BSP is a trivially-correct linear chain).
    private CsgSharedSplit.PsBsp?[] _brushBsp = Array.Empty<CsgSharedSplit.PsBsp?>();

    // Per brush → its convex decomposition (a closed-ish CONCAVE brush's inside cells, each a small
    // convex sub-volume with its own AABB). Populated only for eligible concave brushes; null otherwise
    // (those keep the crossing-face fallback). A foreign face is clipped against each cell it penetrates
    // (per-cell AABB gate), so the silhouette is cut with shared vertices while distant open-space faces
    // stay whole — the fix for the concave-brush open edges.
    private ConvexCell[]?[] _brushCells = Array.Empty<ConvexCell[]?>();

    /// <summary>One convex piece of a decomposed concave brush: its inside=behind half-spaces and AABB.</summary>
    private sealed class ConvexCell
    {
        public ConvexCell((CsgPlane Plane, int Id)[] planes, Vec3 min, Vec3 max, (Vec3 Min, Vec3 Max)[]? extents = null)
        {
            Planes = planes;
            Min = min;
            Max = max;
            Extents = extents;
        }

        public (CsgPlane Plane, int Id)[] Planes { get; }

        public Vec3 Min { get; }

        public Vec3 Max { get; }

        /// <summary>BoundedVolumeClip (flagship 35): the AABB of the brush face(s) supporting each constraint plane,
        /// aligned 1:1 with <see cref="Planes"/>. Null when not built (extent gate inactive). Passed to
        /// <see cref="CsgSharedSplit.ConvexClip"/> so a cell plane cuts only where its bounded face reaches.</summary>
        public (Vec3 Min, Vec3 Max)[]? Extents { get; }
    }

    // Brush spatial grid: cell → brush indices; oversized brushes always checked.
    private readonly Dictionary<(int, int, int), List<int>> _cells = new();
    private readonly List<int> _large = new();

    /// <summary>
    /// Adds a brush's world faces as one CSG operand (time index = call order).
    /// Air brushes union open space, solids subtract it.
    /// </summary>
    public void AddBrush(bool isAir, List<CsgFace> worldFaces)
    {
        int ti = _volumes.Count;
        _volumes.Add(BrushVolume.From(ti, isAir, worldFaces));
        foreach (CsgFace f in worldFaces)
        {
            f.BrushTime = ti; // strict CSG time order — decides world (earlier) vs brush (later)
        }

        _brushFaces.Add(worldFaces);

        var mins = new Vec3[worldFaces.Count];
        var maxs = new Vec3[worldFaces.Count];
        for (int i = 0; i < worldFaces.Count; i++)
        {
            var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            worldFaces[i].GrowAabb(ref mn, ref mx);
            mins[i] = mn;
            maxs[i] = mx;
        }

        _faceAabbMin.Add(mins);
        _faceAabbMax.Add(maxs);

        BuildPlaneData(worldFaces);
    }

    /// <summary>
    /// Interns each face's plane and derives every original vertex's plane-set (the
    /// owning face plane plus every same-brush face plane through that corner), so the
    /// shared splitter can identify each cut point by its three planes.
    /// </summary>
    private void BuildPlaneData(List<CsgFace> worldFaces)
    {
        const float OnFace = 1e-3f;
        int n = worldFaces.Count;
        var faceIds = new int[n];
        for (int i = 0; i < n; i++)
        {
            faceIds[i] = _registry.Intern(worldFaces[i].Plane);
        }

        var vplanes = new int[n][][];
        for (int i = 0; i < n; i++)
        {
            CsgFace f = worldFaces[i];
            var perVert = new int[f.Vertices.Count][];
            for (int v = 0; v < f.Vertices.Count; v++)
            {
                Vec3 p = f.Vertices[v].Position;
                var set = new SortedSet<int> { faceIds[i] };
                for (int g = 0; g < n; g++)
                {
                    if (g != i && faceIds[g] >= 0 && MathF.Abs(worldFaces[g].Plane.Distance(p)) < OnFace)
                    {
                        set.Add(faceIds[g]);
                    }
                }

                var arr = new int[set.Count];
                set.CopyTo(arr);
                perVert[v] = arr;
            }

            vplanes[i] = perVert;
        }

        _facePlaneId.Add(faceIds);
        _vertexPlanes.Add(vplanes);
    }

    /// <summary>Collects brush <paramref name="bi"/>'s non-portal boundary faces as shared-plane BSP input.</summary>
    private List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)> CollectBspFaces(int bi)
    {
        List<CsgFace> faces = _brushFaces[bi];
        int[] ids = _facePlaneId[bi];
        int[][][] vplanes = _vertexPlanes[bi];
        var input = new List<(CsgPlane, int, List<CsgSharedSplit.PsVert>)>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            CsgFace f = faces[i];
            if (f.IsPortal || f.Vertices.Count < 3 || ids[i] < 0)
            {
                continue;
            }

            int[][] fvp = vplanes[i];
            int[] fpset = { ids[i] };
            var poly = new List<CsgSharedSplit.PsVert>(f.Vertices.Count);
            for (int v = 0; v < f.Vertices.Count; v++)
            {
                int[] planes = v < fvp.Length ? fvp[v] : fpset;
                poly.Add(new CsgSharedSplit.PsVert(f.Vertices[v].Position, f.Vertices[v].Uv, planes));
            }

            input.Add((f.Plane, ids[i], poly));
        }

        return input;
    }

    /// <summary>Builds a CONVEX brush's solid BSP (a linear chain — the trivially-correct convex clip).</summary>
    private CsgSharedSplit.PsBsp? BuildBrushBsp(int bi)
    {
        List<(CsgPlane, int, List<CsgSharedSplit.PsVert>)> input = CollectBspFaces(bi);
        return input.Count >= 4 ? CsgSharedSplit.PsBsp.Build(input) : null;
    }

    /// <summary>
    /// Convex decomposition of a CONCAVE brush (compiler-parity-notes.md — the CSG watertightness cohort).
    /// The brush's solid-leaf BSP over its own face planes partitions its closed volume into convex inside
    /// cells (the standard exact decomposition = RED's per-brush BSP inside leaves). Each cell is kept as a
    /// small convex sub-volume (half-space constraints + AABB) and clipped per-piece, so a foreign face is
    /// cut only at the silhouette it actually penetrates — watertight by construction, without a monolithic
    /// concave BSP's spurious open-space plane-extension cuts. Built only for a closed-ish solid within the
    /// face/node/work/piece budgets (the 11.6 GB terrain lesson); otherwise returns null (crossing-face
    /// fallback). Union membership stays the ray-cast <see cref="BrushVolume.Contains"/>, unchanged.
    /// </summary>
    private ConvexCell[]? BuildDecompositionCells(int bi)
    {
        if (_volumes[bi].IsAir)
        {
            return null; // air cells are OPEN space — decomposing them over-splits surviving faces (see above)
        }

        List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)> input = CollectBspFaces(bi);
        if (input.Count < DecompMinFaces || input.Count > DecompMaxFaces || !IsClosedish(input))
        {
            return null; // not a decomposition target (flat sheet / open shell / oversized) — fall back, uncounted
        }

        CsgSharedSplit.PsBsp? tree = CsgSharedSplit.PsBsp.Build(input, DecompMaxNodes, DecompMaxWork, out _);
        List<(CsgPlane Plane, int Id)[]>? constraints = tree?.CollectCells(DecompMaxCells);
        if (tree is null || constraints is null)
        {
            _decompFallback++; // eligible-looking but exceeded a budget — fall back, never crash
            return null;
        }

        BrushVolume vol = _volumes[bi];

        // BoundedVolumeClip (flagship 35): the bounded face extent per registry plane id — the union AABB of this
        // brush's own boundary faces on that plane. Each cell constraint plane's extent gates its cut in ConvexClip.
        Dictionary<int, (Vec3 Min, Vec3 Max)>? faceExt = null;
        if (_boundedVolumeClip)
        {
            faceExt = new Dictionary<int, (Vec3, Vec3)>(input.Count);
            foreach ((CsgPlane _, int id, List<CsgSharedSplit.PsVert> poly) in input)
            {
                var pmn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                var pmx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                foreach (CsgSharedSplit.PsVert pv in poly)
                {
                    pmn = new Vec3(MathF.Min(pmn.X, pv.Pos.X), MathF.Min(pmn.Y, pv.Pos.Y), MathF.Min(pmn.Z, pv.Pos.Z));
                    pmx = new Vec3(MathF.Max(pmx.X, pv.Pos.X), MathF.Max(pmx.Y, pv.Pos.Y), MathF.Max(pmx.Z, pv.Pos.Z));
                }

                if (faceExt.TryGetValue(id, out (Vec3 Min, Vec3 Max) cur))
                {
                    pmn = new Vec3(MathF.Min(pmn.X, cur.Min.X), MathF.Min(pmn.Y, cur.Min.Y), MathF.Min(pmn.Z, cur.Min.Z));
                    pmx = new Vec3(MathF.Max(pmx.X, cur.Max.X), MathF.Max(pmx.Y, cur.Max.Y), MathF.Max(pmx.Z, cur.Max.Z));
                }

                faceExt[id] = (pmn, pmx);
            }
        }

        var cells = new List<ConvexCell>(constraints.Count);
        foreach ((CsgPlane Plane, int Id)[] planes in constraints)
        {
            if (TryCellAabb(planes, vol.Min, vol.Max, out Vec3 cmin, out Vec3 cmax))
            {
                (Vec3 Min, Vec3 Max)[]? extents = null;
                if (faceExt is not null)
                {
                    extents = new (Vec3, Vec3)[planes.Length];
                    for (int k = 0; k < planes.Length; k++)
                    {
                        extents[k] = faceExt.TryGetValue(planes[k].Id, out (Vec3 Min, Vec3 Max) e)
                            ? e
                            : (new Vec3(float.MinValue, float.MinValue, float.MinValue), new Vec3(float.MaxValue, float.MaxValue, float.MaxValue));
                    }
                }

                cells.Add(new ConvexCell(planes, cmin, cmax, extents));
            }
        }

        if (cells.Count == 0)
        {
            _decompFallback++;
            return null;
        }

        _decomposed++;
        _decompCells += cells.Count;
        _decompMaxCells = System.Math.Max(_decompMaxCells, cells.Count);
        return cells.ToArray();
    }

    /// <summary>
    /// Bounding box of a convex cell {p : Distance(planeᵢ) ≤ 0}, computed by enumerating the cell's
    /// vertices as triple-plane intersections (via the registry's exact cached solve) that satisfy every
    /// constraint. Clamped to the brush AABB. Falls back to the brush AABB (conservative — over-gates
    /// nothing) when the constraint set is too large to enumerate or too few vertices resolve.
    /// </summary>
    private bool TryCellAabb((CsgPlane Plane, int Id)[] planes, Vec3 bmin, Vec3 bmax, out Vec3 cmin, out Vec3 cmax)
    {
        cmin = bmin;
        cmax = bmax;
        int n = planes.Length;
        if (n < 3 || n > 48)
        {
            return true; // conservative brush-AABB bound (still correct; just gates less)
        }

        const float Feasible = 1e-2f; // 1 cm slack so on-boundary vertices survive float noise
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        int found = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                for (int k = j + 1; k < n; k++)
                {
                    if (_registry.Intersect(planes[i].Id, planes[j].Id, planes[k].Id) is not { } p)
                    {
                        continue;
                    }

                    bool feasible = true;
                    for (int m = 0; m < n; m++)
                    {
                        if (planes[m].Plane.Distance(p) > Feasible)
                        {
                            feasible = false;
                            break;
                        }
                    }

                    if (feasible)
                    {
                        mn = Vec3Math.Min(mn, p);
                        mx = Vec3Math.Max(mx, p);
                        found++;
                    }
                }
            }
        }

        if (found < 4)
        {
            return true; // could not resolve the cell — keep the brush AABB
        }

        // Clamp to the brush AABB (guards a stray ill-conditioned intersection) and keep.
        cmin = Vec3Math.Max(mn, bmin);
        cmax = Vec3Math.Min(mx, bmax);
        return true;
    }

    /// <summary>
    /// True when the brush faces form a closed-ish 2-manifold (a decomposition target). A small fraction
    /// of open / non-manifold edges is tolerated: a slightly-off decomposition only over-splits foreign
    /// faces (survival stays the ray-cast union test), it cannot flip a survival verdict — so the gate is
    /// about avoiding wasted work / spurious cuts on genuinely-open flat sheets, not correctness.
    /// </summary>
    private static bool IsClosedish(List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)> faces)
    {
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        foreach ((CsgPlane _, int _, List<CsgSharedSplit.PsVert> poly) in faces)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                (int, int, int) a = VKey(poly[i].Pos);
                (int, int, int) b = VKey(poly[(i + 1) % n].Pos);
                ((int, int, int), (int, int, int)) e = Cmp(a, b) <= 0 ? (a, b) : (b, a);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        }

        if (edges.Count < 6)
        {
            return false;
        }

        int open = 0, nonManifold = 0;
        foreach (int c in edges.Values)
        {
            if (c == 1)
            {
                open++;
            }
            else if (c > 2)
            {
                nonManifold++;
            }
        }

        return open <= System.Math.Max(2, edges.Count * 0.06) &&
               nonManifold <= System.Math.Max(1, edges.Count * 0.04);
    }

    /// <summary>
    /// Consistently orients a closed 2-manifold face set OUTWARD, combinatorially: flood-fill across shared
    /// edges (the two faces sharing an edge must traverse it in opposite directions — flip the neighbour if
    /// not), then fix the global sign by the shell's signed volume (positive = outward normals). No
    /// containment probes — authored windings and ray-parity heuristics never enter. Returns null when the
    /// mesh is not consistently orientable (a mismatched edge after flood-fill) or the volume is degenerate.
    /// Polygons are re-wound to match the flipped planes so downstream splitting stays consistent.
    /// </summary>
    private static List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)>? OrientByManifold(
        List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)> faces)
    {
        var polys = new List<List<CsgSharedSplit.PsVert>>(faces.Count);
        foreach ((CsgPlane _, int _, List<CsgSharedSplit.PsVert> poly) in faces)
        {
            polys.Add(poly);
        }

        bool[]? flips = TryManifoldFlips(polys);
        if (flips is null)
        {
            return null;
        }

        var result = new List<(CsgPlane, int, List<CsgSharedSplit.PsVert>)>(faces.Count);
        for (int fi = 0; fi < faces.Count; fi++)
        {
            if (!flips[fi])
            {
                result.Add(faces[fi]);
                continue;
            }

            var rev = new List<CsgSharedSplit.PsVert>(faces[fi].Poly);
            rev.Reverse();
            result.Add((faces[fi].Plane.Flipped(), faces[fi].Id, rev));
        }

        return result;
    }

    /// <summary>
    /// Core of the combinatorial outward orientation: per-polygon flip flags for a strictly closed,
    /// consistently orientable 2-manifold (flood-fill across shared edges + signed-volume global sign);
    /// null when the mesh is open, non-manifold, non-orientable, disconnected, or volume-degenerate.
    /// </summary>
    private static bool[]? TryManifoldFlips(List<List<CsgSharedSplit.PsVert>> faces)
    {
        int n = faces.Count;
        var edgeOwners = new Dictionary<((int, int, int), (int, int, int)), List<(int Face, bool Forward)>>();
        for (int fi = 0; fi < n; fi++)
        {
            List<CsgSharedSplit.PsVert> poly = faces[fi];
            int m = poly.Count;
            for (int i = 0; i < m; i++)
            {
                (int, int, int) a = VKey(poly[i].Pos);
                (int, int, int) b = VKey(poly[(i + 1) % m].Pos);
                if (Cmp(a, b) == 0)
                {
                    continue;
                }

                bool forward = Cmp(a, b) < 0;
                ((int, int, int), (int, int, int)) key = forward ? (a, b) : (b, a);
                if (!edgeOwners.TryGetValue(key, out List<(int, bool)>? owners))
                {
                    edgeOwners[key] = owners = new List<(int, bool)>(2);
                }

                owners.Add((fi, forward));
            }
        }

        // Flood-fill consistent orientation: flipped[fi] = whether face fi must be reversed.
        var flipped = new bool[n];
        var visited = new bool[n];
        var queue = new Queue<int>();
        visited[0] = true;
        queue.Enqueue(0);
        int seen = 1;
        var adj = new Dictionary<int, List<(int Other, bool SameDir)>>();
        foreach (List<(int Face, bool Forward)> owners in edgeOwners.Values)
        {
            if (owners.Count != 2)
            {
                return null; // not exactly closed (guarded by the caller, but be safe)
            }

            (int f0, bool d0) = owners[0];
            (int f1, bool d1) = owners[1];
            if (!adj.TryGetValue(f0, out List<(int, bool)>? l0))
            {
                adj[f0] = l0 = new List<(int, bool)>();
            }

            if (!adj.TryGetValue(f1, out List<(int, bool)>? l1))
            {
                adj[f1] = l1 = new List<(int, bool)>();
            }

            // Consistent manifold orientation ⇒ the two faces traverse the shared edge OPPOSITELY.
            l0.Add((f1, d0 == d1));
            l1.Add((f0, d0 == d1));
        }

        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            if (!adj.TryGetValue(f, out List<(int Other, bool SameDir)>? neighbours))
            {
                continue;
            }

            foreach ((int other, bool sameDir) in neighbours)
            {
                // sameDir ⇒ the neighbour traverses the edge the SAME way ⇒ exactly one of the two must flip.
                bool needFlip = sameDir ? !flipped[f] : flipped[f];
                if (!visited[other])
                {
                    visited[other] = true;
                    flipped[other] = needFlip;
                    seen++;
                    queue.Enqueue(other);
                }
                else if (flipped[other] != needFlip)
                {
                    return null; // non-orientable / inconsistent mesh
                }
            }
        }

        if (seen != n)
        {
            return null; // disconnected shell
        }

        // Global sign: signed volume over origin-based tetrahedra must be positive for outward normals
        // (divergence theorem; each face fan-triangulated in its oriented winding).
        double vol6 = 0;
        for (int fi = 0; fi < n; fi++)
        {
            List<CsgSharedSplit.PsVert> poly = faces[fi];
            for (int i = 1; i + 1 < poly.Count; i++)
            {
                Vec3 a = poly[0].Pos;
                Vec3 b = poly[flipped[fi] ? i + 1 : i].Pos;
                Vec3 c = poly[flipped[fi] ? i : i + 1].Pos;
                vol6 += ((double)a.X * (((double)b.Y * c.Z) - ((double)b.Z * c.Y)))
                      - ((double)a.Y * (((double)b.X * c.Z) - ((double)b.Z * c.X)))
                      + ((double)a.Z * (((double)b.X * c.Y) - ((double)b.Y * c.X)));
            }
        }

        if (Math.Abs(vol6) < 1e-9)
        {
            return null; // degenerate volume
        }

        if (vol6 < 0)
        {
            for (int fi = 0; fi < n; fi++)
            {
                flipped[fi] = !flipped[fi];
            }
        }

        return flipped;
    }

    /// <summary>Strictly closed 2-manifold: every edge shared by exactly two faces (no open, no non-manifold).</summary>
    private static bool IsExactlyClosed(List<(CsgPlane Plane, int Id, List<CsgSharedSplit.PsVert> Poly)> faces)
    {
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        foreach ((CsgPlane _, int _, List<CsgSharedSplit.PsVert> poly) in faces)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                (int, int, int) a = VKey(poly[i].Pos);
                (int, int, int) b = VKey(poly[(i + 1) % n].Pos);
                if (Cmp(a, b) == 0)
                {
                    continue;
                }

                ((int, int, int), (int, int, int)) e = Cmp(a, b) <= 0 ? (a, b) : (b, a);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        }

        if (edges.Count < 6)
        {
            return false;
        }

        foreach (int c in edges.Values)
        {
            if (c != 2)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Coarse (~4 mm) quantized vertex key — merges near-coincident corners for the manifold test.</summary>
    private static (int, int, int) VKey(Vec3 p) =>
        ((int)MathF.Round(p.X * 256f), (int)MathF.Round(p.Y * 256f), (int)MathF.Round(p.Z * 256f));

    private static int Cmp((int, int, int) a, (int, int, int) b)
    {
        if (a.Item1 != b.Item1)
        {
            return a.Item1 < b.Item1 ? -1 : 1;
        }

        if (a.Item2 != b.Item2)
        {
            return a.Item2 < b.Item2 ? -1 : 1;
        }

        return a.Item3 == b.Item3 ? 0 : (a.Item3 < b.Item3 ? -1 : 1);
    }

    /// <summary>Runs the survival solve and returns the surviving, open-facing boundary faces.</summary>
    public List<CsgFace> Solve()
    {
        BuildGrid();

        // Path dispatch. The explicit experimental opt-ins (leaf extraction, world BSP) are checked FIRST so
        // that setting one still selects its path even though the incremental accumulator is now the DEFAULT
        // (flagship 12 flip: CompileOptions.IncrementalAccumulator defaults true). With no explicit opt-in the
        // incremental fold runs; set IncrementalAccumulator=false to fall through to the per-brush accumulator.

        // THE FUSION (flagship 18): route EVERY source face down ONE global partition (all brush face planes,
        // registry-folded), survival from the world-level convex-leaf contents. Combines the extraction path's
        // exact global in/out classification with the source-face path's authored-polygon geometry, cut on the
        // SAME partition so adjacent faces share stations bit-identically. Explicit opt-in, checked first; a
        // world tree over budget returns null and falls through to the incremental default below.
        if (_fusedPartition)
        {
            List<CsgFace>? fused = SolveFusedPartition();
            if (fused is not null)
            {
                return fused;
            }
        }

        // RED's watertight realisation: EXTRACT the boundary face set from the leaf tree (portals between
        // open and solid leaves), attributed back to the original brush faces. Watertight by construction on
        // sealed geometry; needs no coincident-face resolution (each boundary is emitted exactly once). A tree
        // over budget or a pathological extraction falls back to the per-brush accumulator below (NOT the
        // incremental default — this is the documented leaf-extraction fallback).
        if (_useLeafExtraction)
        {
            List<CsgFace>? extracted = _sourceFaceEmission ? ExtractBoundarySourceFaces() : ExtractBoundary();
            if (extracted is not null)
            {
                return extracted;
            }
        }

        // RED's single accumulated world BSP: build ONE partition over all brush face planes so every
        // boundary face is routed through the SAME tree (coincident cuts bit-identical). Built once,
        // single-threaded, read-only during the parallel solve. A level whose tree exceeds the budget
        // falls back to the per-brush accumulator (the 11.6 GB terrain lesson).
        else if (_useWorldBsp)
        {
            _worldBsp = BuildWorldBsp();
            WorldBspActive = _worldBsp is not null;
        }

        // RED's AUTHENTIC SINGLE ACCUMULATED SHARED BSP (flagship 31 — the commissioned endgame): the
        // persistent incremental shared boundary, but BOTH the accumulated world faces (step a) AND every
        // incoming cap (step b) route down ONE accumulated partition SYMMETRICALLY, so two differently-
        // tessellated terrain surfaces subdivide at the same partition stations and their shared corners are
        // bit-identical by construction. Takes precedence over the plain incremental/global-partition folds.
        else if (_sharedBsp)
        {
            return SolveSharedBsp();
        }

        // THE GLOBAL ACCUMULATED PARTITION (flagship 16): the incremental fold, but both the world faces and
        // every incoming cap route down ONE accumulated partition of the brushes' node planes (registry-folded),
        // so near-parallel siblings cut both sides at the same stations. Explicit opt-in; supersedes the
        // BRepBoundary/PartitionClip cap re-cuts conceptually. Default OFF.
        else if (_globalPartition)
        {
            return SolveGlobalPartition();
        }

        // RED's actual compile architecture (flagship 11): fold every brush in strict time order into a
        // persistent, in-place-split world boundary. Shared registry cuts + verbatim un-crossed faces —
        // both watertightness properties at once. The DEFAULT CSG path since the flip; reached only when no
        // explicit opt-in above claimed the build, and skipped (per-brush accumulator below) when
        // IncrementalAccumulator is explicitly off.
        else if (_incremental)
        {
            return SolveIncremental();
        }

        // Pre-build each CONVEX brush's solid BSP volume single-threaded, so the parallel accumulate
        // reads them without contention (RED's phase-0 per-brush BSP, node planes from face planes).
        // Non-convex brushes clip via crossing-face cutters (their infinite-plane BSP would make
        // spurious reflex cuts), so building their BSP would be wasted work on big terrain shells.
        // Convex brushes get a linear-chain BSP (clipped directly). Closed-ish concave brushes get a convex
        // decomposition (inside cells, each clipped per-piece). Both are read-only during the parallel solve.
        // Ineligible/oversized concave brushes keep neither and fall back to crossing-face cutters.
        // Skipped entirely when the world BSP is active (it handles every face uniformly).
        _brushBsp = new CsgSharedSplit.PsBsp?[_brushFaces.Count];
        _brushCells = new ConvexCell[]?[_brushFaces.Count];
        if (!WorldBspActive)
        {
            for (int bi = 0; bi < _brushFaces.Count; bi++)
            {
                if (_volumes[bi].IsConvexVolume)
                {
                    _brushBsp[bi] = BuildBrushBsp(bi);
                }
                else
                {
                    _brushCells[bi] = BuildDecompositionCells(bi);
                }
            }
        }

        // Process brushes in parallel (volumes are immutable once built); collect
        // per-partition survivors then concatenate in brush order for determinism.
        var partial = new List<CsgFace>[_brushFaces.Count];
        Parallel.For(0, _brushFaces.Count, bi =>
        {
            var local = new List<CsgFace>();
            List<CsgFace> faces = _brushFaces[bi];
            for (int fi = 0; fi < faces.Count; fi++)
            {
                CsgFace bf = faces[fi];
                if (bf.Vertices.Count < 3 || bf.IsPortal)
                {
                    continue;
                }

                List<CsgFace> fragments = WorldBspActive
                    ? SplitFaceWorldBsp(bi, fi, bf, out bool capped)
                    : SplitFaceAccumulate(bi, fi, bf, out capped);
                if (capped)
                {
                    System.Threading.Interlocked.Increment(ref _cappedFaces);
                }
                foreach (CsgFace frag in fragments)
                {
                    if (frag.Vertices.Count < 3 || frag.Area() < 1e-6f)
                    {
                        continue;
                    }

                    if (TrySurvive(frag))
                    {
                        local.Add(frag);
                    }
                }
            }

            partial[bi] = local;
        });

        var survivors = new List<CsgFace>();
        foreach (List<CsgFace>? p in partial)
        {
            if (p is not null)
            {
                survivors.AddRange(p);
            }
        }

        return ResolveCoincident(survivors);
    }

    // ================= Incremental boundary accumulator (RED's compile architecture, flagship 11) =================
    //
    // Maintains ONE persistent list of oriented boundary faces (normal into open — RF convention), folding
    // each brush in strict time order. A brush b does two things to that list:
    //   (a) SPLITS the already-accumulated world faces IN PLACE where b's volume crosses them (shared registry
    //       cuts), and DISSOLVES the fragments whose far side flips state: an AIR b opens the solid behind a
    //       world face → it becomes open|open → gone; a SOLID b fills the open in front of a world face →
    //       solid|solid → gone. Un-crossed world faces pass through verbatim (RED property #1).
    //   (b) Adds b's OWN faces, split against the earlier-brush partition so each fragment is uniformly
    //       classified, kept where b's interior state differs from the prior exterior state, oriented to open.
    // Because every cut — world-vs-b and b-vs-earlier — is a shared PlaneRegistry triple, coincident cuts are
    // bit-identical (RED property #2). Coincident/coplanar survivors are then resolved by the survival table.
    //
    // A world face carried through the fold keeps its PsVert plane-sets so a LATER brush cuts it on the same
    // shared triples (that is what makes the accumulation "in place" rather than re-derived).

    /// <summary>One face in the accumulated world boundary: its shared-plane polygon, canonical face-plane
    /// id, oriented plane (normal into open), source attributes, and cached AABB.</summary>
    private sealed class WFace
    {
        public WFace(List<CsgSharedSplit.PsVert> poly, int facePlaneId, CsgPlane plane, CsgFace src)
        {
            Poly = poly;
            FacePlaneId = facePlaneId;
            Plane = plane;
            Src = src;
            ComputeAabb();
        }

        public List<CsgSharedSplit.PsVert> Poly { get; }

        public int FacePlaneId { get; }

        public CsgPlane Plane { get; }

        /// <summary>Attribute template (texture/flags/FromAir/BrushTime/…); geometry lives in <see cref="Poly"/>.</summary>
        public CsgFace Src { get; }

        public Vec3 Min { get; private set; }

        public Vec3 Max { get; private set; }

        /// <summary>A fragment of this face after a later-brush split — same attributes/plane, new polygon.</summary>
        public WFace With(List<CsgSharedSplit.PsVert> poly) => new(poly, FacePlaneId, Plane, Src);

        public CsgFace ToCsgFace()
        {
            var verts = new List<CsgVertex>(Poly.Count);
            foreach (CsgSharedSplit.PsVert pv in Poly)
            {
                verts.Add(new CsgVertex(pv.Pos, pv.Uv));
            }

            CsgFace f = Src.CloneAttributes();
            f.Plane = Plane;
            f.Vertices = verts;
            f.IsPortal = false;
            f.RoomIndex = -1;
            f.PortalIndexPlus2 = 0;
            return f;
        }

        private void ComputeAabb()
        {
            var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            foreach (CsgSharedSplit.PsVert v in Poly)
            {
                mn = Vec3Math.Min(mn, v.Pos);
                mx = Vec3Math.Max(mx, v.Pos);
            }

            Min = mn;
            Max = mx;
        }
    }

    /// <summary>
    /// RED's incremental boundary accumulator. Builds the per-brush clip volumes (convex BSP / concave
    /// cells / crossing-cutter fallback — the same machinery the per-brush path uses), then folds every
    /// brush in time order into the persistent world boundary and resolves coincident survivors.
    /// </summary>
    private List<CsgFace> SolveIncremental()
    {
        // EdgeLerpSplit (flagship 19): construction-time shared vertex identity. Assign the store to the
        // registry BEFORE the fold so BuildInitialPoly stamps corner ids and CutVertex interns each edge cut
        // once (on-edge lerp on the stored endpoints). The fold is single-threaded, so a plain-dictionary
        // store is safe. When the flag is off the store stays null and every cut vertex carries VId = -1 —
        // the fold is byte-identical to before.
        EdgeLerpSplitActive = _edgeLerpSplit;
        if (_edgeLerpSplit)
        {
            _registry.EdgeStore = new EdgeStore(_edgeMergeTol ?? EdgeLerpDefaultMergeTol);
        }

        // The deviation bound STAYS ON (the per-brush policy, PlaneRegistry default): the incremental fold
        // splits AUTHORED faces in place — like the per-brush path, not like extraction's re-derivation — so
        // an ill-conditioned registry triple (near-parallel organic planes) must fall back to the edge's own
        // lerp. Measured decisively on dm02's rck_coal01 rock: with raw triples the displaced cut vertices
        // put fragments' centroids far off the face and the whole flat rock wall mis-classified away
        // (26 open edges); with the bound dm02 compiles watertight (0).

        // Per-brush clip volumes, single-threaded (read-only during the fold). Convex → linear-chain BSP;
        // closed-ish concave solid → convex-decomposition cells (CUTTING only — see
        // SplitPolyByBrushClassified); else the crossing-face cutter fallback. A full per-op BSP for
        // non-convex operands (RED-literal FUN_004a6b90) was measured NET NEGATIVE (reflex-plane-extension
        // over-cut: dm04 30→42/+64% faces when applied to shells without cells, dm04 32→37/ctf01 39→49
        // applied to all) and is not built.
        _brushBsp = new CsgSharedSplit.PsBsp?[_brushFaces.Count];
        _brushCells = new ConvexCell[]?[_brushFaces.Count];
        _classCells = new ConvexCell[]?[_brushFaces.Count];
        _faceOut = new sbyte[_brushFaces.Count][];
        for (int bi = 0; bi < _brushFaces.Count; bi++)
        {
            _faceOut[bi] = BuildFaceOrientations(bi);
            if (_volumes[bi].IsConvexVolume)
            {
                _brushBsp[bi] = BuildBrushBsp(bi);
            }
            else
            {
                _brushCells[bi] = BuildDecompositionCells(bi);
                _classCells[bi] = BuildClassificationCells(bi);
            }
        }

        var world = new List<WFace>();
        for (int b = 0; b < _brushFaces.Count; b++)
        {
            BrushVolume bvol = _volumes[b];
            bool bIsAir = bvol.IsAir;

            // Brush b's authored faces by registry plane id — the coincidence lookup for the survival table
            // (RED resolves coincident/coplanar pairs BEFORE the spanning-face phase; here that is fold step (c)).
            Dictionary<int, List<CsgFace>> bFacesByPlane = BrushFacesByPlane(b);

            // Surviving world faces on b's face planes (filled during step (a)) — step (b)'s duplicate gate:
            // where the WORLD face won the coincidence, b's own coincident fragment must not be added over it.
            var worldOnBPlanes = new Dictionary<int, List<WFace>>();

            // B-rep cap re-cut (flagship 14): record, per b face plane, which OTHER world planes cut a
            // vertex onto it in step (a); step (b) re-cuts b's cap on that plane by exactly those planes.
            _capCut = _brepBoundary || _partitionClip ? new Dictionary<int, HashSet<int>>() : null;
            _capCutPos = _partitionClip ? new Dictionary<int, Dictionary<int, List<Vec3>>>() : null;

            // (a) Split the accumulated world faces in place by brush b, dissolving fragments whose far side
            // flips state, and resolving fragments COINCIDENT with one of b's face planes by the survival table.
            var next = new List<WFace>(world.Count);
            foreach (WFace w in world)
            {
                if (!AabbOverlapWide(w.Min, w.Max, bvol.Min, bvol.Max))
                {
                    next.Add(w); // b nowhere near this face — carried verbatim
                    continue;
                }

                // Split w by b, keeping the clip's own INSIDE|OUTSIDE|UNKNOWN classification: for a
                // BSP/cell-clipped brush the verdict is exact (RED's in/out stamping down the partition,
                // FUN_0048bec0 class 1/2) — no probe, no near-tangent 2 cm overshoot. Only the
                // crossing-cutter fallback (organic non-convex operands) still needs the volume probe.
                var insideF = new List<List<CsgSharedSplit.PsVert>>();
                var outsideF = new List<List<CsgSharedSplit.PsVert>>();
                var unknownF = new List<List<CsgSharedSplit.PsVert>>();
                bool capped = false;
                SplitPolyByBrushClassified(b, w.FacePlaneId, w.Plane, w.Poly, insideF, outsideF, unknownF, ref capped);
                Vec3 n = w.Plane.Normal; // into open (front = open, back = solid) — the fold invariant
                for (int cls = 0; cls < 3; cls++)
                {
                    List<List<CsgSharedSplit.PsVert>> bucket = cls == 0 ? insideF : cls == 1 ? outsideF : unknownF;
                    foreach (List<CsgSharedSplit.PsVert> frag in bucket)
                {
                    if (frag.Count < 3)
                    {
                        continue;
                    }

                    Vec3 c = PolyCentroid(frag);

                    // Fold step (c): a fragment on one of b's own face planes under its authored polygon is a
                    // COINCIDENT pair (a coplanar fragment classifies down the back path, so the in/out verdict
                    // must not decide it). Order matters: b's volume swallowing w's other side dissolves the wall
                    // REGARDLESS of the table (back-to-back air rooms union away their shared wall; a solid
                    // filling the open in front buries it) — only then does RED's survival table decide the
                    // remaining texture-claim case (DAT_0057cc48; class 3 = interiors same side, 4 = opposed).
                    bool dissolve;
                    bool covTable = false;
                    List<CsgFace>? coincidentKin = null;
                    CsgFace? coincidentKinFace = null;
                    if (bFacesByPlane.TryGetValue(w.FacePlaneId, out List<CsgFace>? bkin) &&
                        CoveringFace(bkin, c) is { } kin)
                    {
                        coincidentKin = bkin;
                        coincidentKinFace = kin;

                        // Probe at the COVERING kin face's centroid, not the fragment's: the kin face is b's
                        // own boundary on this plane, so b's side is uniform across its whole extent — one
                        // stable verdict per authored face. Per-fragment probes inside a sloppy authored
                        // shell gave parity-noise verdicts that flip-flopped across adjacent fragments,
                        // tearing a keep/dissolve checkerboard rim (dmwarzone's building floor ring).
                        Vec3 kc = kin.Centroid();
                        bool bFront = IncContains(b, kc.Add(n.Scale(_incEps)));
                        bool bBack = IncContains(b, kc.Sub(n.Scale(_incEps)));
                        if (bFront == bBack)
                        {
                            dissolve = false; // b's side unresolvable (sub-probe-thin operand) — keep the wall
                        }
                        else if (bIsAir ? bBack : bFront)
                        {
                            dissolve = true; // air opens w's solid back / solid fills w's open front — wall gone
                        }
                        else
                        {
                            // Texture claim: both faces bound the same wall; the table picks the operand.
                            int wSide = w.Src.FromAir ? +1 : -1;
                            int bSide = bFront ? +1 : -1;
                            int cls34 = wSide == bSide ? 3 : 4;
                            int mode = bIsAir ? 3 : 1;
                            dissolve = TableAction(1, mode, cls34) < TableAction(0, mode, cls34);
                            covTable = dissolve; // region-wise applies to this texture-claim dissolve
                        }
                    }
                    else if (cls == 0)
                    {
                        dissolve = true; // strictly inside b ⇒ both sides take b's state ⇒ no boundary here
                    }
                    else if (cls == 1)
                    {
                        dissolve = false; // strictly outside b ⇒ b changes nothing here
                    }
                    else
                    {
                        // Fallback operand (no in/out classification): the volume probe.
                        // AIR b opens the solid behind → open|open; SOLID b fills the open in front → solid|solid.
                        dissolve = bIsAir
                            ? IncContains(b, c.Sub(n.Scale(_incEps)))
                            : IncContains(b, c.Add(n.Scale(_incEps)));
                    }

                    if (dissolve)
                    {
                        _incDissolved++;

                        // REGION-WISE + winner-in-place (flagship 23B). RED clips every coincident face down the
                        // OTHER brush's partition and applies the survival table PER FRAGMENT, so the coincident
                        // WINNER's face survives over the resolved 2D overlap and each face's non-overlapping
                        // remainder survives with its own identity. Two consequences the per-brush face-wise
                        // dissolve missed:
                        //  (1) Remainder: the covered-branch table verdict was applied to the WHOLE fragment;
                        //      clip it to the covering kin faces and keep the uncovered remainder (loser's texture).
                        //  (2) Winner-in-place: where the winner is a SOLID replacing a coincident world AIR wall
                        //      (air-loses-to-solid), the fold's step-(b) emission of the winner's own cap can MISS
                        //      over near-coincident bumpy terrain — its room-above probe misreads a 1 mm-coincident
                        //      floor as not-open, so BOTH the air wall (dissolved here) and the solid cap (buried in
                        //      step b) vanish and a hole opens (dm04 cluster A) even though the dissolve verdict is
                        //      correct. Keep the COVERED region in place carrying the winner face's attributes,
                        //      oriented into open as the air wall was, so the winner's surface is guaranteed present;
                        //      the step-(b) duplicate gate then suppresses the winner's own cap there (no doubling).
                        //      Same-kind (air/air) keeps the plain drop — the later air's own cap emits reliably.
                        if (_regionWise && covTable && coincidentKin is not null)
                        {
                            var covered = new List<List<CsgSharedSplit.PsVert>>();
                            var uncovered = new List<List<CsgSharedSplit.PsVert>>();
                            SplitByKinCoverage(frag, coincidentKin, n, w.FacePlaneId, covered, uncovered);

                            foreach (List<CsgSharedSplit.PsVert> piece in uncovered)
                            {
                                KeepWorldFragment(w, piece, next, worldOnBPlanes, bFacesByPlane); // remainder keeps loser's texture
                            }

                            if (!bIsAir && coincidentKinFace is { } winner)
                            {
                                foreach (List<CsgSharedSplit.PsVert> piece in covered)
                                {
                                    KeepWorldFragmentAs(w, winner, piece, next, worldOnBPlanes, bFacesByPlane);
                                }
                            }
                        }
                    }
                    else
                    {
                        KeepWorldFragment(w, frag, next, worldOnBPlanes, bFacesByPlane);
                    }
                }
                }
            }

            world = next;

            // (b) Add brush b's own faces where the state differs across them (skipping fragments a surviving
            // coincident world face already covers — the world operand won the survival table there).
            AddBrushFacesIncremental(b, bvol, worldOnBPlanes, world);
        }

        var result = new List<CsgFace>(world.Count);
        foreach (WFace w in world)
        {
            if (w.Poly.Count >= 3)
            {
                result.Add(w.ToCsgFace());
            }
        }

        _incWorldFaces = result.Count;
        IncrementalActive = true;
        if (_registry.EdgeStore is { } es)
        {
            EdgeSharedVertices = es.VertexCount;
            EdgeCornerMerges = es.CornerMerges;
            _registry.EdgeStore = null; // release; no path after the fold uses it
        }

        // No post-hoc ResolveCoincident: coincidence is resolved IN the fold (step (c) above), which is
        // RED's semantics. The post-hoc air-vs-solid pass is actively wrong here — it assumes the backing
        // solid's own face survived the CSG, but in the incremental fold a solid face buried at its own add
        // time is gone, and the AIR brush's face is the authored replacement wall (measured: it tore the
        // dm01 corridor walls out over the pillar fronts).
        return result;
    }

    // ================= Global accumulated partition (flagship 16 — the convergence pass) =================
    //
    // The incremental fold, but instead of clipping each incoming cap against the EARLIER brushes' convex
    // VOLUMES (which see only the local boundary plane and miss a near-parallel sibling — the dm04 16 mm stub),
    // every cap is routed down ONE accumulated PARTITION: split at every earlier-brush node plane (registry-
    // folded, so a coincident family is one node but a near-parallel pair stays two DISTINCT nodes) whose
    // supporting-face AABB straddles it, splitting only where the polygon genuinely straddles (SplitOne's
    // 1e-4 band) and cutting at the byte-identical PlaneRegistry triple. Step (a) is UNCHANGED from the
    // incremental fold (the world faces already split in place there, and the exact convex-BSP in/out
    // classification is the fold's robustness — see the flagship-11 verdict campaign); the change is entirely
    // the cap side (step b), which is the deficient side the whole campaign traced the stub to (BRepBoundary /
    // PartitionClip were bounded cap re-cuts approximating exactly this). Survival is the same per-fragment fold
    // verdict; coincident (own-node) caps still resolve via the survival-table duplicate gate, never re-split.

    /// <summary>
    /// The global-partition fold (flagship 16). Identical setup + step (a) to <see cref="SolveIncremental"/>;
    /// step (b) routes each cap down the accumulated partition of earlier brushes' node planes instead of
    /// clipping it against their volumes, so near-parallel siblings cut the cap at the same stations the
    /// flanking world faces carry.
    /// </summary>
    private List<CsgFace> SolveGlobalPartition()
    {
        _brushBsp = new CsgSharedSplit.PsBsp?[_brushFaces.Count];
        _brushCells = new ConvexCell[]?[_brushFaces.Count];
        _classCells = new ConvexCell[]?[_brushFaces.Count];
        _faceOut = new sbyte[_brushFaces.Count][];
        _partFaces = new (CsgPlane, int, Vec3, Vec3)[_brushFaces.Count][];
        for (int bi = 0; bi < _brushFaces.Count; bi++)
        {
            _faceOut[bi] = BuildFaceOrientations(bi);
            _partFaces[bi] = BuildPartitionFaces(bi);
            if (_volumes[bi].IsConvexVolume)
            {
                _brushBsp[bi] = BuildBrushBsp(bi);
            }
            else
            {
                _brushCells[bi] = BuildDecompositionCells(bi);
                _classCells[bi] = BuildClassificationCells(bi);
            }
        }

        var world = new List<WFace>();
        for (int b = 0; b < _brushFaces.Count; b++)
        {
            BrushVolume bvol = _volumes[b];
            bool bIsAir = bvol.IsAir;
            Dictionary<int, List<CsgFace>> bFacesByPlane = BrushFacesByPlane(b);
            var worldOnBPlanes = new Dictionary<int, List<WFace>>();
            _capCut = null;
            _capCutPos = null;

            // (a) Split the accumulated world faces in place by brush b (UNCHANGED from the incremental fold).
            var next = new List<WFace>(world.Count);
            foreach (WFace w in world)
            {
                if (!AabbOverlapWide(w.Min, w.Max, bvol.Min, bvol.Max))
                {
                    next.Add(w);
                    continue;
                }

                var insideF = new List<List<CsgSharedSplit.PsVert>>();
                var outsideF = new List<List<CsgSharedSplit.PsVert>>();
                var unknownF = new List<List<CsgSharedSplit.PsVert>>();
                bool capped = false;
                SplitPolyByBrushClassified(b, w.FacePlaneId, w.Plane, w.Poly, insideF, outsideF, unknownF, ref capped);
                Vec3 n = w.Plane.Normal;
                for (int cls = 0; cls < 3; cls++)
                {
                    List<List<CsgSharedSplit.PsVert>> bucket = cls == 0 ? insideF : cls == 1 ? outsideF : unknownF;
                    foreach (List<CsgSharedSplit.PsVert> frag in bucket)
                    {
                        if (frag.Count < 3)
                        {
                            continue;
                        }

                        Vec3 c = PolyCentroid(frag);
                        bool dissolve;
                        if (bFacesByPlane.TryGetValue(w.FacePlaneId, out List<CsgFace>? bkin) &&
                            CoveringFace(bkin, c) is { } kin)
                        {
                            Vec3 kc = kin.Centroid();
                            bool bFront = IncContains(b, kc.Add(n.Scale(_incEps)));
                            bool bBack = IncContains(b, kc.Sub(n.Scale(_incEps)));
                            if (bFront == bBack)
                            {
                                dissolve = false;
                            }
                            else if (bIsAir ? bBack : bFront)
                            {
                                dissolve = true;
                            }
                            else
                            {
                                int wSide = w.Src.FromAir ? +1 : -1;
                                int bSide = bFront ? +1 : -1;
                                int cls34 = wSide == bSide ? 3 : 4;
                                int mode = bIsAir ? 3 : 1;
                                dissolve = TableAction(1, mode, cls34) < TableAction(0, mode, cls34);
                            }
                        }
                        else if (cls == 0)
                        {
                            dissolve = true;
                        }
                        else if (cls == 1)
                        {
                            dissolve = false;
                        }
                        else
                        {
                            dissolve = bIsAir
                                ? IncContains(b, c.Sub(n.Scale(_incEps)))
                                : IncContains(b, c.Add(n.Scale(_incEps)));
                        }

                        if (dissolve)
                        {
                            _incDissolved++;
                        }
                        else
                        {
                            WFace kept = w.With(frag);
                            next.Add(kept);
                            if (bFacesByPlane.ContainsKey(w.FacePlaneId))
                            {
                                if (!worldOnBPlanes.TryGetValue(w.FacePlaneId, out List<WFace>? list))
                                {
                                    worldOnBPlanes[w.FacePlaneId] = list = new List<WFace>();
                                }

                                list.Add(kept);
                            }
                        }
                    }
                }
            }

            world = next;

            // Snapshot the surviving world faces by registry plane id (the sibling-cut safety gate). Built
            // AFTER step (a) so it reflects what b actually left standing.
            _worldByPlane = new Dictionary<int, List<(Vec3, Vec3)>>();
            foreach (WFace w in world)
            {
                if (!_worldByPlane.TryGetValue(w.FacePlaneId, out List<(Vec3, Vec3)>? list))
                {
                    _worldByPlane[w.FacePlaneId] = list = new List<(Vec3, Vec3)>();
                }

                list.Add((w.Min, w.Max));
            }

            // (b) Add brush b's own faces — routed down the accumulated partition (the global-partition change).
            AddBrushFacesGlobalPartition(b, bvol, worldOnBPlanes, world);
        }

        var result = new List<CsgFace>(world.Count);
        foreach (WFace w in world)
        {
            if (w.Poly.Count >= 3)
            {
                result.Add(w.ToCsgFace());
            }
        }

        _incWorldFaces = result.Count;
        IncrementalActive = true;
        GlobalPartitionActive = true;
        return result;
    }

    /// <summary>
    /// Adds brush <paramref name="b"/>'s boundary faces to the accumulated world (global-partition step b).
    /// Identical to <see cref="AddBrushFacesIncremental"/> except the cap is ROUTED DOWN THE PARTITION
    /// (<see cref="RouteCapThroughPartition"/>) — split at every earlier-brush node plane that straddles it —
    /// rather than clipped against the earlier brushes' volumes + the flag-gated cap re-cut. Survival, the
    /// duplicate gate, and orientation are unchanged.
    /// </summary>
    private void AddBrushFacesGlobalPartition(
        int b, BrushVolume bvol, Dictionary<int, List<WFace>> worldOnBPlanes, List<WFace> world)
    {
        bool bIsAir = bvol.IsAir;
        List<CsgFace> faces = _brushFaces[b];
        int[] ids = _facePlaneId[b];
        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace bf = faces[fi];
            if (bf.IsPortal || bf.Vertices.Count < 3 || ids[fi] < 0)
            {
                continue;
            }

            int facePlane = ids[fi];
            CsgPlane faceGeom = bf.Plane;
            List<CsgSharedSplit.PsVert> poly0 = BuildInitialPoly(b, fi, bf);
            List<List<CsgSharedSplit.PsVert>> pieces;
            if (_capHybrid)
            {
                // HYBRID cap (shared-BSP ctf01 fix): volume-clip the cap against the earlier brushes FIRST
                // (the incremental cap that keeps stepped-channel geometry from over-cutting), then route each
                // volume-clipped fragment down the accumulated partition (real crossings + matched-edge siblings)
                // to add the terrain-pair / membrane stations the volume clip misses. The base cut is the
                // incremental one, so the partition routing can only ADD matched cuts, never replace the whole cap.
                List<List<CsgSharedSplit.PsVert>> volPieces = ClipAgainstEarlierBrushes(
                    b, facePlane, faceGeom, poly0, _faceAabbMin[b][fi], _faceAabbMax[b][fi]);
                pieces = new List<List<CsgSharedSplit.PsVert>>(volPieces.Count);
                foreach (List<CsgSharedSplit.PsVert> vp in volPieces)
                {
                    if (vp.Count < 3)
                    {
                        continue;
                    }

                    PieceAabb(vp, out Vec3 pmin, out Vec3 pmax);
                    pieces.AddRange(RouteCapThroughPartition(b, fi, bf, facePlane, vp, pmin, pmax));
                }
            }
            else
            {
                pieces = RouteCapThroughPartition(
                    b, fi, bf, facePlane, poly0, _faceAabbMin[b][fi], _faceAabbMax[b][fi]);
            }

            worldOnBPlanes.TryGetValue(facePlane, out List<WFace>? coincidentWorld);

            Vec3 n = faceGeom.Normal;
            foreach (List<CsgSharedSplit.PsVert> frag in pieces)
            {
                if (frag.Count < 3)
                {
                    continue;
                }

                Vec3 c = PolyCentroid(frag);
                Vec3 pf = c.Add(n.Scale(_incEps));
                Vec3 pk = c.Sub(n.Scale(_incEps));

                sbyte outward = _faceOut[b][fi];
                bool frontOpen;
                bool backOpen;
                if (outward > 0)
                {
                    frontOpen = OpenAtBefore(pf, b);
                    backOpen = bIsAir;
                }
                else if (outward < 0)
                {
                    frontOpen = bIsAir;
                    backOpen = OpenAtBefore(pk, b);
                }
                else
                {
                    frontOpen = IncContains(b, pf) ? bIsAir : OpenAtBefore(pf, b);
                    backOpen = IncContains(b, pk) ? bIsAir : OpenAtBefore(pk, b);
                }

                bool duplicate = frontOpen != backOpen &&
                    coincidentWorld is not null && CoveredByAnyWorld(coincidentWorld, c);
                if (duplicate)
                {
                    continue;
                }

                if (frontOpen == backOpen)
                {
                    continue;
                }

                List<CsgSharedSplit.PsVert> oriented = frag;
                CsgPlane plane = faceGeom;
                if (backOpen)
                {
                    oriented = new List<CsgSharedSplit.PsVert>(frag);
                    oriented.Reverse();
                    plane = faceGeom.Flipped();
                }

                world.Add(new WFace(oriented, facePlane, plane, bf));
            }
        }
    }

    // ================= RED's AUTHENTIC SINGLE ACCUMULATED SHARED BSP (flagship 31 — the endgame) =================
    //
    // The persistent incremental shared boundary (as SolveIncremental), but the routing is SYMMETRIC across the
    // two sides of every cut: when a brush b arrives, the already-accumulated WORLD faces (step a) AND b's own
    // CAPS (step b) are BOTH routed down ONE accumulated partition of the brushes' face planes — including the
    // near-parallel SIBLINGS of every real crossing. The global-partition fold (flagship 16) gave the sibling
    // stations to the CAP side only (world faces stayed volume-clipped), so a near-parallel terrain pair was cut
    // on one side but not the other — an asymmetry that closed some seams and reopened others (dm04 9→14). Here a
    // near-parallel pair {Pw (a surviving world plane), Pc (b's plane)} is cut on BOTH sides: step (a) cuts the
    // world face on Pw at the sibling Pc, and step (b) cuts b's cap on Pc at the sibling Pw — so both surfaces
    // subdivide at the same Pw∩Pc stations and their shared corners are bit-identical by construction (with the
    // EdgeLerpSplit store the cut is interned once and referenced by both flanks). Survival stays the incremental
    // fold's per-fragment far-side-flip + region-wise table (NOT FusedPartition's leaf-contents — that over-cut
    // INDEPENDENT source faces and stormed; the persistent shared boundary, splitting in place and passing
    // un-crossed faces verbatim, is precisely what keeps the full symmetric routing bounded).

    /// <summary>
    /// The authentic single accumulated shared-BSP fold (flagship 31). Identical setup + survival to
    /// <see cref="SolveGlobalPartition"/>; the addition is the SYMMETRIC world-side sibling routing in step (a)
    /// (<see cref="RouteWorldFragmentSiblings"/>) that mirrors the cap-side partition routing, so a near-parallel
    /// terrain/floor pair is subdivided identically on both surfaces.
    /// </summary>
    private List<CsgFace> SolveSharedBsp()
    {
        EdgeLerpSplitActive = _edgeLerpSplit;
        if (_edgeLerpSplit)
        {
            _registry.EdgeStore = new EdgeStore(_edgeMergeTol ?? EdgeLerpDefaultMergeTol);
        }

        _capEdgeMatchGate = true;
        _capHybrid = true;
        _brushBsp = new CsgSharedSplit.PsBsp?[_brushFaces.Count];
        _brushCells = new ConvexCell[]?[_brushFaces.Count];
        _classCells = new ConvexCell[]?[_brushFaces.Count];
        _faceOut = new sbyte[_brushFaces.Count][];
        _partFaces = new (CsgPlane, int, Vec3, Vec3)[_brushFaces.Count][];
        for (int bi = 0; bi < _brushFaces.Count; bi++)
        {
            _faceOut[bi] = BuildFaceOrientations(bi);
            _partFaces[bi] = BuildPartitionFaces(bi);
            if (_volumes[bi].IsConvexVolume)
            {
                _brushBsp[bi] = BuildBrushBsp(bi);
            }
            else
            {
                _brushCells[bi] = BuildDecompositionCells(bi);
                _classCells[bi] = BuildClassificationCells(bi);
            }
        }

        var world = new List<WFace>();
        for (int b = 0; b < _brushFaces.Count; b++)
        {
            BrushVolume bvol = _volumes[b];
            bool bIsAir = bvol.IsAir;
            Dictionary<int, List<CsgFace>> bFacesByPlane = BrushFacesByPlane(b);
            var worldOnBPlanes = new Dictionary<int, List<WFace>>();
            _capCut = null;
            _capCutPos = null;

            // (a) Split the accumulated world faces in place by brush b (volume-classified dissolve, region-wise
            // coincidence — flagship 11/23B robustness), then route each KEPT fragment down b's near-parallel
            // SIBLING planes so the world side acquires the same stations the cap side gets in step (b).
            var next = new List<WFace>(world.Count);
            foreach (WFace w in world)
            {
                if (!AabbOverlapWide(w.Min, w.Max, bvol.Min, bvol.Max))
                {
                    next.Add(w);
                    continue;
                }

                var insideF = new List<List<CsgSharedSplit.PsVert>>();
                var outsideF = new List<List<CsgSharedSplit.PsVert>>();
                var unknownF = new List<List<CsgSharedSplit.PsVert>>();
                bool capped = false;
                SplitPolyByBrushClassified(b, w.FacePlaneId, w.Plane, w.Poly, insideF, outsideF, unknownF, ref capped);
                Vec3 n = w.Plane.Normal;
                for (int cls = 0; cls < 3; cls++)
                {
                    List<List<CsgSharedSplit.PsVert>> bucket = cls == 0 ? insideF : cls == 1 ? outsideF : unknownF;
                    foreach (List<CsgSharedSplit.PsVert> frag in bucket)
                    {
                        if (frag.Count < 3)
                        {
                            continue;
                        }

                        Vec3 c = PolyCentroid(frag);
                        bool dissolve;
                        bool covTable = false;
                        List<CsgFace>? coincidentKin = null;
                        CsgFace? coincidentKinFace = null;
                        if (bFacesByPlane.TryGetValue(w.FacePlaneId, out List<CsgFace>? bkin) &&
                            CoveringFace(bkin, c) is { } kin)
                        {
                            coincidentKin = bkin;
                            coincidentKinFace = kin;
                            Vec3 kc = kin.Centroid();
                            bool bFront = IncContains(b, kc.Add(n.Scale(_incEps)));
                            bool bBack = IncContains(b, kc.Sub(n.Scale(_incEps)));
                            if (bFront == bBack)
                            {
                                dissolve = false;
                            }
                            else if (bIsAir ? bBack : bFront)
                            {
                                dissolve = true;
                            }
                            else
                            {
                                int wSide = w.Src.FromAir ? +1 : -1;
                                int bSide = bFront ? +1 : -1;
                                int cls34 = wSide == bSide ? 3 : 4;
                                int mode = bIsAir ? 3 : 1;
                                dissolve = TableAction(1, mode, cls34) < TableAction(0, mode, cls34);
                                covTable = dissolve;
                            }
                        }
                        else if (cls == 0)
                        {
                            dissolve = true;
                        }
                        else if (cls == 1)
                        {
                            dissolve = false;
                        }
                        else
                        {
                            dissolve = bIsAir
                                ? IncContains(b, c.Sub(n.Scale(_incEps)))
                                : IncContains(b, c.Add(n.Scale(_incEps)));
                        }

                        if (dissolve)
                        {
                            _incDissolved++;
                            if (_regionWise && covTable && coincidentKin is not null)
                            {
                                var covered = new List<List<CsgSharedSplit.PsVert>>();
                                var uncovered = new List<List<CsgSharedSplit.PsVert>>();
                                SplitByKinCoverage(frag, coincidentKin, n, w.FacePlaneId, covered, uncovered);
                                foreach (List<CsgSharedSplit.PsVert> piece in uncovered)
                                {
                                    KeepWorldFragmentShared(b, w, piece, next, worldOnBPlanes, bFacesByPlane);
                                }

                                if (!bIsAir && coincidentKinFace is { } winner)
                                {
                                    foreach (List<CsgSharedSplit.PsVert> piece in covered)
                                    {
                                        KeepWorldFragmentAsShared(b, w, winner, piece, next, worldOnBPlanes, bFacesByPlane);
                                    }
                                }
                            }
                        }
                        else
                        {
                            KeepWorldFragmentShared(b, w, frag, next, worldOnBPlanes, bFacesByPlane);
                        }
                    }
                }
            }

            world = next;

            // Snapshot surviving world faces by registry plane id: AABBs for the sibling-cut safety gate, and
            // the full WFace list for the shared-BSP matched-edge cap gate (CapCutterMatched).
            _worldByPlane = new Dictionary<int, List<(Vec3, Vec3)>>();
            _worldFacesByPlane = _capEdgeMatchGate ? new Dictionary<int, List<WFace>>() : null;
            foreach (WFace w in world)
            {
                if (!_worldByPlane.TryGetValue(w.FacePlaneId, out List<(Vec3, Vec3)>? list))
                {
                    _worldByPlane[w.FacePlaneId] = list = new List<(Vec3, Vec3)>();
                }

                list.Add((w.Min, w.Max));

                if (_worldFacesByPlane is not null)
                {
                    if (!_worldFacesByPlane.TryGetValue(w.FacePlaneId, out List<WFace>? faces))
                    {
                        _worldFacesByPlane[w.FacePlaneId] = faces = new List<WFace>();
                    }

                    faces.Add(w);
                }
            }

            // (b) Add brush b's own faces — routed down the accumulated partition (real crossings ungated;
            // near-parallel siblings gated by the matched-edge test CapCutterMatched).
            AddBrushFacesGlobalPartition(b, bvol, worldOnBPlanes, world);
        }

        var result = new List<CsgFace>(world.Count);
        foreach (WFace w in world)
        {
            if (w.Poly.Count >= 3)
            {
                result.Add(w.ToCsgFace());
            }
        }

        _incWorldFaces = result.Count;
        IncrementalActive = true;
        GlobalPartitionActive = true;
        SharedBspActive = true;
        if (_registry.EdgeStore is { } es)
        {
            EdgeSharedVertices = es.VertexCount;
            EdgeCornerMerges = es.CornerMerges;
            _registry.EdgeStore = null;
        }

        return result;
    }

    /// <summary>
    /// Keeps a surviving world fragment on the shared-BSP path: routes it down brush <paramref name="b"/>'s
    /// near-parallel SIBLING planes (<see cref="RouteWorldFragmentSiblings"/>) so it acquires the same stations
    /// the cap side gets, then adds each sub-fragment to <paramref name="next"/> (recording it on b's coincident
    /// planes for the step-(b) duplicate gate). The symmetric mirror of the cap-side partition routing.
    /// </summary>
    private void KeepWorldFragmentShared(
        int b, WFace w, List<CsgSharedSplit.PsVert> frag, List<WFace> next,
        Dictionary<int, List<WFace>> worldOnBPlanes, Dictionary<int, List<CsgFace>> bFacesByPlane)
    {
        foreach (List<CsgSharedSplit.PsVert> piece in RouteWorldFragmentSiblings(b, w.FacePlaneId, w.Plane, frag))
        {
            if (piece.Count < 3)
            {
                continue;
            }

            WFace kept = w.With(piece);
            next.Add(kept);
            if (bFacesByPlane.ContainsKey(w.FacePlaneId))
            {
                if (!worldOnBPlanes.TryGetValue(w.FacePlaneId, out List<WFace>? list))
                {
                    worldOnBPlanes[w.FacePlaneId] = list = new List<WFace>();
                }

                list.Add(kept);
            }
        }
    }

    /// <summary>As <see cref="KeepWorldFragmentShared"/> but re-attributing the fragment to the coincident WINNER
    /// face (region-wise winner-in-place, flagship 23B). Oriented into open as the loser wall was.</summary>
    private void KeepWorldFragmentAsShared(
        int b, WFace w, CsgFace winner, List<CsgSharedSplit.PsVert> frag, List<WFace> next,
        Dictionary<int, List<WFace>> worldOnBPlanes, Dictionary<int, List<CsgFace>> bFacesByPlane)
    {
        var template = new WFace(frag, w.FacePlaneId, w.Plane, winner);
        foreach (List<CsgSharedSplit.PsVert> piece in RouteWorldFragmentSiblings(b, w.FacePlaneId, w.Plane, frag))
        {
            if (piece.Count < 3)
            {
                continue;
            }

            WFace kept = template.With(piece);
            next.Add(kept);
            if (bFacesByPlane.ContainsKey(w.FacePlaneId))
            {
                if (!worldOnBPlanes.TryGetValue(w.FacePlaneId, out List<WFace>? list))
                {
                    worldOnBPlanes[w.FacePlaneId] = list = new List<WFace>();
                }

                list.Add(kept);
            }
        }
    }

    /// <summary>
    /// SYMMETRIC world-side sibling routing (the shared-BSP addition over GlobalPartition). Splits a kept world
    /// fragment on plane <paramref name="faceGeom"/> at each of brush <paramref name="b"/>'s partition planes
    /// that is near-parallel to the fragment's own plane (a distinct registry id ≥ the 2e-3 fold — the terrain
    /// stub signature), genuinely straddles the fragment, and is backed by one of b's authored faces overlapping
    /// the fragment (so the cut pairs with b's own cap emitted there in step (b) — matched by construction, the
    /// mirror of the cap-side <see cref="SiblingBackedByWorld"/> gate). This is the world half of "both surfaces
    /// clipped down the same partition": the terrain pair is now cut on BOTH sides at the same stations.
    /// </summary>
    private List<List<CsgSharedSplit.PsVert>> RouteWorldFragmentSiblings(
        int b, int facePlane, CsgPlane faceGeom, List<CsgSharedSplit.PsVert> frag)
    {
        (CsgPlane Geom, int Id, Vec3 Min, Vec3 Max)[] parts = _partFaces[b];
        if (parts.Length == 0)
        {
            return new List<List<CsgSharedSplit.PsVert>> { frag };
        }

        Vec3 wn = faceGeom.Normal;
        Vec3 amin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 amax = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgSharedSplit.PsVert v in frag)
        {
            amin = Vec3Math.Min(amin, v.Pos);
            amax = Vec3Math.Max(amax, v.Pos);
        }

        List<(CsgPlane Geom, int Id)>? cutters = null;
        foreach ((CsgPlane geom, int id, Vec3 mn, Vec3 mx) in parts)
        {
            if (id == facePlane || !AabbOverlap(amin, amax, mn, mx))
            {
                continue;
            }

            // Near-parallel to the world fragment's own plane (the terrain-pair signature), but not the SAME
            // plane the registry already folded: a distinct id at dot > the sibling tolerance.
            float dot = MathF.Abs(geom.Normal.Dot(wn));
            if (dot <= GlobalSiblingDot)
            {
                continue;
            }

            if (!SpansPoly(frag, geom))
            {
                continue;
            }

            (cutters ??= new List<(CsgPlane, int)>()).Add((geom, id));
        }

        if (cutters is null)
        {
            return new List<List<CsgSharedSplit.PsVert>> { frag };
        }

        CsgSharedSplit.Route = "worldsib";
        List<List<CsgSharedSplit.PsVert>> wsr = CsgSharedSplit.Split(_registry, facePlane, frag, cutters, MaxFragmentsPerFace, out _);
        CsgSharedSplit.Route = null;
        return wsr;
    }

    /// <summary>The AABB of a shared-split polygon.</summary>
    private static void PieceAabb(List<CsgSharedSplit.PsVert> poly, out Vec3 min, out Vec3 max)
    {
        min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            min = Vec3Math.Min(min, v.Pos);
            max = Vec3Math.Max(max, v.Pos);
        }
    }

    /// <summary>True when polygon <paramref name="poly"/> genuinely straddles <paramref name="plane"/> (some
    /// vertices on each side, beyond the on-plane band) — the shared-split straddle gate for a PsVert polygon.</summary>
    private static bool SpansPoly(List<CsgSharedSplit.PsVert> poly, CsgPlane plane)
    {
        bool front = false, back = false;
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            float d = plane.Distance(v.Pos);
            if (d > Band)
            {
                front = true;
            }
            else if (d < -Band)
            {
                back = true;
            }

            if (front && back)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The distinct (registry-folded) non-portal face planes brush <paramref name="bi"/> contributes to the
    /// accumulated partition, each with its supporting-face AABB (the bbox straddle gate — RED's
    /// <c>FUN_0048e4f0</c>). Registry folding means a coincident plane family reduces to one node while a
    /// near-parallel pair (distinct ids ≥ the 2e-3 fold) stays two nodes — the property that lets both members
    /// cut a crossing cap.
    /// </summary>
    private (CsgPlane Geom, int Id, Vec3 Min, Vec3 Max)[] BuildPartitionFaces(int bi)
    {
        List<CsgFace> faces = _brushFaces[bi];
        int[] ids = _facePlaneId[bi];
        var byId = new Dictionary<int, (CsgPlane Geom, Vec3 Min, Vec3 Max)>();
        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace f = faces[fi];
            int id = ids[fi];
            if (f.IsPortal || f.Vertices.Count < 3 || id < 0)
            {
                continue;
            }

            Vec3 mn = _faceAabbMin[bi][fi];
            Vec3 mx = _faceAabbMax[bi][fi];
            if (byId.TryGetValue(id, out (CsgPlane Geom, Vec3 Min, Vec3 Max) cur))
            {
                byId[id] = (cur.Geom, Vec3Math.Min(cur.Min, mn), Vec3Math.Max(cur.Max, mx));
            }
            else
            {
                byId[id] = (f.Plane, mn, mx);
            }
        }

        var result = new (CsgPlane, int, Vec3, Vec3)[byId.Count];
        int k = 0;
        foreach ((int id, (CsgPlane Geom, Vec3 Min, Vec3 Max) v) in byId)
        {
            result[k++] = (v.Geom, id, v.Min, v.Max);
        }

        return result;
    }

    /// <summary>Near-parallel dot for the global-partition sibling gate. Set to the registry's own
    /// <c>NormalDotTol</c> (0.99997, ~0.44°): a sibling within this normal dot of a real crossing but at a
    /// distinct registry id is a parallel-OFFSET pair the registry ALMOST folded (offset ≥ the 2e-3 fold) —
    /// exactly the terrain-stub signature (dm04's 1312/1313, ~3 mm apart, 0.99997 dot). Measured equivalent to a
    /// looser 1.8° gate (0.9995) on the corpus — the ctf01/dm07 regressions are NOT angle-sensitive (they are
    /// genuine parallel-offset siblings whose cap cut is unmatched on the adjacent world face, the residual
    /// per-brush-vs-global asymmetry) — so the tighter, principled registry-tolerance value is used.</summary>
    private const float GlobalSiblingDot = 0.99997f;

    /// <summary>
    /// Routes cap polygon <paramref name="poly0"/> (on plane <paramref name="facePlane"/>) down the accumulated
    /// partition of EARLIER brushes (RED's <c>FUN_0048bec0</c> cap-clip-down-the-partition), but bounded to the
    /// planes that matter so it does not reproduce the flat straddle-everything over-cut (measured NET-NEGATIVE:
    /// dm04 14→16, ctf01 11→17 — the route-faces storm). Two cutter classes, folded by registry id:
    /// <list type="number">
    /// <item>REAL crossings — an earlier-brush face that genuinely intersects the cap (<see cref="Spans"/> +
    /// <see cref="FacesCross"/>), exactly as the world faces are cut by this brush.</item>
    /// <item>Their NEAR-PARALLEL SIBLINGS — an earlier partition plane near-parallel to a real crossing
    /// (distinct registry id ≥ the 2e-3 fold) that straddles the cap: the OTHER member of the terrain stub pair
    /// (dm04's 1313 next to the crossing 1312). Adding ONLY these gives the cap the sibling station the flanking
    /// world face carries — closing the extent-divergence stub by construction — without cutting the cap by
    /// every distant parallel plane.</item>
    /// </list>
    /// Every cut is the byte-identical <see cref="PlaneRegistry"/> triple, so the cap and the world face share
    /// stations. The cap's own plane is excluded; coincident (own-node) resolution is left to the survival table.
    /// </summary>
    private List<List<CsgSharedSplit.PsVert>> RouteCapThroughPartition(
        int b, int fi, CsgFace bf, int facePlane, List<CsgSharedSplit.PsVert> poly0, Vec3 amin, Vec3 amax)
    {
        // (1) Real crossings.
        var cutters = new List<(CsgPlane Geom, int Id)>();
        var seen = new HashSet<int> { facePlane };
        foreach (int oi in CandidatesForAabb(amin, amax))
        {
            if (oi >= b || !AabbOverlap(amin, amax, _volumes[oi].Min, _volumes[oi].Max))
            {
                continue;
            }

            List<CsgFace> of = _brushFaces[oi];
            int[] oiPlanes = _facePlaneId[oi];
            for (int ofi = 0; ofi < of.Count; ofi++)
            {
                int id = oiPlanes[ofi];
                if (id < 0 || seen.Contains(id))
                {
                    continue;
                }

                CsgFace ofFace = of[ofi];
                if (!ofFace.IsPortal && ofFace.Vertices.Count >= 3 && Spans(bf, ofFace.Plane) && FacesCross(bf, ofFace))
                {
                    cutters.Add((ofFace.Plane, id));
                    seen.Add(id);
                }
            }
        }

        // (2) Near-parallel siblings of those crossings (the stub-closer), gated against the ORIGINAL crossings
        // only (no sibling-of-sibling chaining).
        int nCross = cutters.Count;
        if (nCross > 0)
        {
            foreach (int oi in CandidatesForAabb(amin, amax))
            {
                if (oi >= b || !AabbOverlapWide(amin, amax, _volumes[oi].Min, _volumes[oi].Max))
                {
                    continue;
                }

                foreach ((CsgPlane geom, int id, Vec3 mn, Vec3 mx) in _partFaces[oi])
                {
                    if (seen.Contains(id) || !AabbOverlap(amin, amax, mn, mx))
                    {
                        continue;
                    }

                    Vec3 sn = geom.Normal;
                    bool sibling = false;
                    for (int k = 0; k < nCross; k++)
                    {
                        if (MathF.Abs(sn.Dot(cutters[k].Geom.Normal)) > GlobalSiblingDot)
                        {
                            sibling = true;
                            break;
                        }
                    }

                    // Only add the sibling where a SURVIVING world face on its plane overlaps the cap: then the
                    // cap's sibling cut pairs with that world face's edge (matched by construction — the stub
                    // case, where 1313 is a real terrain world face). Adding it with no backing world face opens
                    // an unmatched T-junction (the ctf01/dm07 cohort).
                    bool backed = _capEdgeMatchGate
                        ? CapCutterMatched(bf.Plane, id, amin, amax)
                        : SiblingBackedByWorld(id, amin, amax);
                    if (sibling && backed && Spans(bf, geom))
                    {
                        cutters.Add((geom, id));
                        seen.Add(id);
                    }
                }
            }
        }

        if (cutters.Count == 0)
        {
            return new List<List<CsgSharedSplit.PsVert>> { poly0 };
        }

        CsgSharedSplit.Route = "cap";
        List<List<CsgSharedSplit.PsVert>> cpr = CsgSharedSplit.Split(_registry, facePlane, poly0, cutters, MaxFragmentsPerFace, out _);
        CsgSharedSplit.Route = null;
        return cpr;
    }

    /// <summary>Max distance a surviving world edge may sit off the cap's plane and still count as lying on the
    /// cap∩cutter seam line (the registry-fold scale — a genuinely shared seam edge sits within it).</summary>
    private const float CapSeamTol = 2e-3f;

    /// <summary>
    /// The shared-BSP MATCHED-EDGE cap gate (flagship 31 — the precise form of flagship 16's "the AABB gate is
    /// too coarse"). A cap cutter on registry plane <paramref name="cutterId"/> is kept only where a surviving
    /// world face on that plane carries a real boundary EDGE lying on the cap's own plane
    /// (<paramref name="capPlane"/>) — i.e. along the cap∩cutter seam — overlapping the cap's extent. Then the
    /// cap's cut PAIRS with that world edge by construction (a shared T-junction the fixer closes), instead of
    /// landing where the adjacent world face (added at a different time in the fold) carries no such station and
    /// opening an unmatched T-junction (the ctf01/dm07 cohort). Where no world edge backs the cutter, the cap is
    /// left uncut there — falling back to the incremental clip's conservatism, so the shared-BSP path never
    /// over-cuts below the incremental baseline.
    /// </summary>
    private bool CapCutterMatched(CsgPlane capPlane, int cutterId, Vec3 amin, Vec3 amax)
    {
        if (_worldFacesByPlane is null)
        {
            return true;
        }

        if (!_worldFacesByPlane.TryGetValue(cutterId, out List<WFace>? faces))
        {
            return false;
        }

        foreach (WFace w in faces)
        {
            if (!AabbOverlap(amin, amax, w.Min, w.Max))
            {
                continue;
            }

            List<CsgSharedSplit.PsVert> poly = w.Poly;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Vec3 p0 = poly[i].Pos;
                Vec3 p1 = poly[(i + 1) % n].Pos;
                if (MathF.Abs(capPlane.Distance(p0)) > CapSeamTol || MathF.Abs(capPlane.Distance(p1)) > CapSeamTol)
                {
                    continue; // this edge is not on the cap∩cutter seam
                }

                if (AabbOverlap(amin, amax, Vec3Math.Min(p0, p1), Vec3Math.Max(p0, p1)))
                {
                    return true; // a real world seam edge backs this cut
                }
            }
        }

        return false;
    }

    /// <summary>True when a surviving world face on registry plane <paramref name="id"/> has an AABB overlapping
    /// the cap's [<paramref name="amin"/>,<paramref name="amax"/>] — the sibling-cut safety gate (see
    /// <see cref="_worldByPlane"/>).</summary>
    private bool SiblingBackedByWorld(int id, Vec3 amin, Vec3 amax)
    {
        if (_worldByPlane is null || !_worldByPlane.TryGetValue(id, out List<(Vec3 Min, Vec3 Max)>? list))
        {
            return false;
        }

        foreach ((Vec3 mn, Vec3 mx) in list)
        {
            if (AabbOverlap(amin, amax, mn, mx))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Brush <paramref name="b"/>'s authored non-portal faces keyed by registry plane id.</summary>
    private Dictionary<int, List<CsgFace>> BrushFacesByPlane(int b)
    {
        var map = new Dictionary<int, List<CsgFace>>();
        List<CsgFace> faces = _brushFaces[b];
        int[] ids = _facePlaneId[b];
        for (int fi = 0; fi < faces.Count; fi++)
        {
            if (faces[fi].IsPortal || faces[fi].Vertices.Count < 3 || ids[fi] < 0)
            {
                continue;
            }

            if (!map.TryGetValue(ids[fi], out List<CsgFace>? list))
            {
                map[ids[fi]] = list = new List<CsgFace>();
            }

            list.Add(faces[fi]);
        }

        return map;
    }

    /// <summary>The first coplanar face containing point <paramref name="c"/> (2D even-odd), or null.</summary>
    private static CsgFace? CoveringFace(List<CsgFace> kin, Vec3 c)
    {
        foreach (CsgFace f in kin)
        {
            if (PolyContains(f, c))
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>True when any surviving world face fragment contains point <paramref name="c"/> (2D even-odd).</summary>
    private static bool CoveredByAnyWorld(List<WFace> kin, Vec3 c)
    {
        foreach (WFace w in kin)
        {
            if (PsPolyContains(w.Poly, w.Plane.Normal, c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Even-odd point-in-polygon over a shared-split polygon, dropping the normal's dominant axis.</summary>
    private static bool PsPolyContains(List<CsgSharedSplit.PsVert> poly, Vec3 nrm, Vec3 p)
    {
        float ax = MathF.Abs(nrm.X), ay = MathF.Abs(nrm.Y), az = MathF.Abs(nrm.Z);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        float pu = Axis(p, drop, true), pv = Axis(p, drop, false);
        bool inside = false;
        int count = poly.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float ui = Axis(poly[i].Pos, drop, true), vi = Axis(poly[i].Pos, drop, false);
            float uj = Axis(poly[j].Pos, drop, true), vj = Axis(poly[j].Pos, drop, false);
            if (((vi > pv) != (vj > pv)) && (pu < ((uj - ui) * (pv - vi) / (vj - vi)) + ui))
            {
                inside = !inside;
            }
        }

        return inside;
    }


    /// <summary>
    /// Adds brush <paramref name="b"/>'s boundary faces to the accumulated world. Each face is split against
    /// the EARLIER-brush partition (so every fragment lies in one prior region), then kept iff the state on
    /// one side differs from the state on the other — each side's state is brush b's own (air = open,
    /// solid = solid) where the probe lands inside b, else the prior fold state. No winding assumption:
    /// authored faces keep their authored winding (<see cref="BrushWorld.ToWorldFaces"/>), which is not
    /// reliably outward, so orientation is decided by the probes (exactly the per-brush path's robustness)
    /// and the survivor faces into open space.
    /// </summary>
    private void AddBrushFacesIncremental(
        int b, BrushVolume bvol, Dictionary<int, List<WFace>> worldOnBPlanes, List<WFace> world)
    {
        bool bIsAir = bvol.IsAir;
        List<CsgFace> faces = _brushFaces[b];
        int[] ids = _facePlaneId[b];
        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace bf = faces[fi];
            if (bf.IsPortal || bf.Vertices.Count < 3 || ids[fi] < 0)
            {
                continue;
            }

            int facePlane = ids[fi];
            CsgPlane faceGeom = bf.Plane;
            List<CsgSharedSplit.PsVert> poly0 = BuildInitialPoly(b, fi, bf);
            List<List<CsgSharedSplit.PsVert>> pieces = ClipAgainstEarlierBrushes(
                b, facePlane, faceGeom, poly0, _faceAabbMin[b][fi], _faceAabbMax[b][fi]);

            // B-rep cap re-cut (flagship 14): give this cap the SAME registry-triple vertices the flanking
            // world faces already carry from step (a), so a near-parallel-plane divergence cannot open a stub.
            pieces = ApplyCapCuts(facePlane, pieces);

            worldOnBPlanes.TryGetValue(facePlane, out List<WFace>? coincidentWorld);

            Vec3 n = faceGeom.Normal;
            foreach (List<CsgSharedSplit.PsVert> frag in pieces)
            {
                if (frag.Count < 3)
                {
                    continue;
                }

                Vec3 c = PolyCentroid(frag);
                Vec3 pf = c.Add(n.Scale(_incEps));
                Vec3 pk = c.Sub(n.Scale(_incEps));

                // The fragment lies ON brush b's boundary, so the state on its interior side IS b's kind —
                // no probe against b's own volume (which is fragile within the authored surface's tilt
                // epsilon; see _faceOut). Only the EXTERIOR side needs the prior fold state. Faces whose
                // outward orientation could not be determined keep the two-sided probe verdicts.
                sbyte outward = _faceOut[b][fi];
                bool frontOpen;
                bool backOpen;
                if (outward > 0)
                {
                    frontOpen = OpenAtBefore(pf, b); // authored normal = outward ⇒ front is exterior
                    backOpen = bIsAir;
                }
                else if (outward < 0)
                {
                    frontOpen = bIsAir; // authored normal points inward ⇒ back is exterior
                    backOpen = OpenAtBefore(pk, b);
                }
                else
                {
                    frontOpen = IncContains(b, pf) ? bIsAir : OpenAtBefore(pf, b);
                    backOpen = IncContains(b, pk) ? bIsAir : OpenAtBefore(pk, b);
                }
                bool duplicate = frontOpen != backOpen &&
                    coincidentWorld is not null && CoveredByAnyWorld(coincidentWorld, c);
                if (duplicate)
                {
                    continue; // a surviving coincident world face won the survival table here — no duplicate
                }

                if (frontOpen == backOpen)
                {
                    continue; // interior (both open) or buried (both solid) — not a boundary
                }

                // Orient the survivor so its normal points into open space.
                List<CsgSharedSplit.PsVert> oriented = frag;
                CsgPlane plane = faceGeom;
                if (backOpen)
                {
                    oriented = new List<CsgSharedSplit.PsVert>(frag);
                    oriented.Reverse();
                    plane = faceGeom.Flipped();
                }

                world.Add(new WFace(oriented, facePlane, plane, bf));
            }
        }
    }

    /// <summary>
    /// B-rep step (a) bookkeeping (flagship 14): a kept world fragment <paramref name="frag"/> carries the
    /// registry planes through each of its vertices. Where a vertex lies on ONE of brush b's own face planes
    /// (<paramref name="bFacesByPlane"/>), the OTHER planes through it are exactly the planes that cut this
    /// vertex onto b's plane — record them so step (b) re-cuts b's cap by the same planes and lands the same
    /// registry-triple vertex.
    /// </summary>
    private void RecordCapCutPlanes(Dictionary<int, List<CsgFace>> bFacesByPlane, List<CsgSharedSplit.PsVert> frag)
    {
        foreach (CsgSharedSplit.PsVert v in frag)
        {
            int[] planes = v.Planes;
            for (int i = 0; i < planes.Length; i++)
            {
                int pb = planes[i];
                if (!bFacesByPlane.ContainsKey(pb))
                {
                    continue;
                }

                if (!_capCut!.TryGetValue(pb, out HashSet<int>? set))
                {
                    _capCut[pb] = set = new HashSet<int>();
                }

                Dictionary<int, List<Vec3>>? posByPlane = null;
                if (_capCutPos is not null && !_capCutPos.TryGetValue(pb, out posByPlane))
                {
                    _capCutPos[pb] = posByPlane = new Dictionary<int, List<Vec3>>();
                }

                for (int j = 0; j < planes.Length; j++)
                {
                    if (planes[j] != pb)
                    {
                        set.Add(planes[j]);
                        if (posByPlane is not null)
                        {
                            if (!posByPlane.TryGetValue(planes[j], out List<Vec3>? pl))
                            {
                                posByPlane[planes[j]] = pl = new List<Vec3>();
                            }

                            pl.Add(v.Pos);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// B-rep step (b) (flagship 14): re-cut brush b's cap fragments on <paramref name="facePlane"/> by the
    /// planes step (a) recorded as cutting a shared vertex onto that plane, so the cap acquires the SAME
    /// registry-triple vertices the flanking world faces carry (the dm04 stub-closer). No-op when the flag
    /// is off, no planes were recorded, or the recorded set is pathologically large. Cut vertices are shared
    /// registry triples (<see cref="CsgSharedSplit.Split"/>), so this cannot move a vertex off the shared line.
    /// </summary>
    private List<List<CsgSharedSplit.PsVert>> ApplyCapCuts(int facePlane, List<List<CsgSharedSplit.PsVert>> pieces)
    {
        if (_capCut is null || facePlane < 0 ||
            !_capCut.TryGetValue(facePlane, out HashSet<int>? planeIds) ||
            planeIds.Count == 0 || planeIds.Count > MaxCapCutPlanes)
        {
            return pieces;
        }

        List<(CsgPlane Geom, int Id)> cutters = _partitionClip
            ? RealEdgeCutters(facePlane, planeIds)
            : NearParallelGatedCutters(planeIds);
        if (cutters.Count == 0)
        {
            return pieces;
        }

        var result = new List<List<CsgSharedSplit.PsVert>>(pieces.Count);
        foreach (List<CsgSharedSplit.PsVert> piece in pieces)
        {
            int before = result.Count;
            result.AddRange(CsgSharedSplit.Split(_registry, facePlane, piece, cutters, MaxFragmentsPerFace, out _));
            if (result.Count - before > 1)
            {
                _brepCapCuts++;
            }
        }

        return result;
    }

    /// <summary>BRepBoundary (flagship 14) cutter set: every recorded plane, but only when the set contains a
    /// near-parallel PAIR (the stub signature) — caps bordering no near-parallel fold cannot open a stub, and
    /// re-cutting them only churns tessellation.</summary>
    private List<(CsgPlane Geom, int Id)> NearParallelGatedCutters(HashSet<int> planeIds)
    {
        var cutters = new List<(CsgPlane Geom, int Id)>(planeIds.Count);
        foreach (int id in planeIds)
        {
            if (_registry.TryGetPlane(id, out CsgPlane geom))
            {
                cutters.Add((geom, id));
            }
        }

        return HasNearParallelPair(cutters, CapNearParallelDot) ? cutters : new List<(CsgPlane, int)>();
    }

    /// <summary>Min separation of two recorded corners on a plane to count as a real world EDGE (a 2-corner
    /// chord) rather than a single grazing touch. Below the shortest authored feature, above float noise.</summary>
    private const float RealEdgeMinSpan = 5e-3f;

    /// <summary>
    /// Partition-clip cutter set (flagship 15): the recorded planes whose cut vertices on the cap plane span a
    /// REAL world edge — ≥2 corners at least <see cref="RealEdgeMinSpan"/> apart. Such a plane is a genuine
    /// world-face chord crossing the cap (dm04's terrain plane 35 carrying Xa+Xb), so cutting the cap by it lands
    /// the cap's bottom edge ON that chord — matched by construction. Planes touched at a SINGLE grazing corner
    /// (the near-parallel siblings 1312/1313, and the ctfwlpro spurious folds) are rejected: re-cutting the cap
    /// by their infinite extent only opens unmatched slivers. This is RED's cap-from-cut-chord without the
    /// near-parallel infinite-plane over-cut.
    /// </summary>
    private List<(CsgPlane Geom, int Id)> RealEdgeCutters(int facePlane, HashSet<int> planeIds)
    {
        var cutters = new List<(CsgPlane Geom, int Id)>();
        if (_capCutPos is null || !_capCutPos.TryGetValue(facePlane, out Dictionary<int, List<Vec3>>? byPlane))
        {
            return cutters;
        }

        // Gate on the near-parallel stub signature (as flagship 14) AND select the real-edge cutters: the
        // real-edge span discriminator alone over-cuts a few community caps whose cut line is a real edge but
        // whose infinite-plane extent slices a floor beyond the chord (ctf04/ctfstockintrade), where the fragment
        // beyond the chord does not dissolve under the per-brush verdict. Restricting to caps that ALSO border a
        // near-parallel fold keeps the terrain-stub gains (dm04/ctf01/ctf02/ctf07 all border such folds) while
        // leaving the non-terrain community caps whole.
        var geoms = new List<(CsgPlane Geom, int Id)>(planeIds.Count);
        foreach (int gid in planeIds)
        {
            if (_registry.TryGetPlane(gid, out CsgPlane g))
            {
                geoms.Add((g, gid));
            }
        }

        if (!HasNearParallelPair(geoms, CapNearParallelDot))
        {
            return cutters;
        }

        foreach ((int id, List<Vec3> positions) in byPlane)
        {
            if (positions.Count < 2 || !_registry.TryGetPlane(id, out CsgPlane geom))
            {
                continue;
            }

            // A real edge needs two corners at least RealEdgeMinSpan apart.
            bool span = false;
            for (int i = 0; i < positions.Count && !span; i++)
            {
                for (int j = i + 1; j < positions.Count && !span; j++)
                {
                    if (positions[i].Distance(positions[j]) >= RealEdgeMinSpan)
                    {
                        span = true;
                    }
                }
            }

            if (span)
            {
                cutters.Add((geom, id));
            }
        }

        return cutters;
    }

    /// <summary>Near-parallel dot threshold for the flagship-14 BRepBoundary cap re-cut gate (~1.8°). A corpus
    /// sweep (0.9995…0.999995) showed the harmful ctfwlpro fold is INSEPARABLE from the beneficial terrain folds
    /// by this angle, which is why the flagship-15 PartitionClip discriminates on real-edge span instead.</summary>
    private const float CapNearParallelDot = 0.9995f;

    /// <summary>True when the cutter set contains two DISTINCT near-parallel planes (normals within <paramref name="dot"/>,
    /// ~1.8° at 0.9995) — the shallow-angle organic-tessellation signature that opens the flagship-12 stub, where a
    /// face borders two gently-varying terrain triangles and the cap picks up only one member's cut.</summary>
    private static bool HasNearParallelPair(List<(CsgPlane Geom, int Id)> cutters, float dot)
    {
        for (int i = 0; i < cutters.Count; i++)
        {
            Vec3 ni = cutters[i].Geom.Normal;
            for (int j = i + 1; j < cutters.Count; j++)
            {
                if (MathF.Abs(ni.Dot(cutters[j].Geom.Normal)) > dot)
                {
                    return true;
                }
            }
        }

        return false;
    }


    /// <summary>
    /// Splits brush face polygon <paramref name="poly0"/> against every EARLIER (time &lt; <paramref name="b"/>)
    /// overlapping brush's volume, so each returned fragment lies wholly inside one prior region (uniformly
    /// classified by <see cref="OpenAtBefore"/>). Cuts are shared registry triples. This is the "split the new
    /// brush's faces against the accumulated partition" step (b); later brushes cut these faces in place via (a).
    /// </summary>
    private List<List<CsgSharedSplit.PsVert>> ClipAgainstEarlierBrushes(
        int b, int facePlane, CsgPlane faceGeom, List<CsgSharedSplit.PsVert> poly0, Vec3 amin, Vec3 amax)
    {
        var pieces = new List<List<CsgSharedSplit.PsVert>> { poly0 };
        bool capped = false;
        foreach (int oi in CandidatesForAabb(amin, amax))
        {
            if (oi >= b || !AabbOverlapWide(amin, amax, _volumes[oi].Min, _volumes[oi].Max))
            {
                continue;
            }

            var nextPieces = new List<List<CsgSharedSplit.PsVert>>(pieces.Count);
            foreach (List<CsgSharedSplit.PsVert> piece in pieces)
            {
                SplitPolyByBrush(oi, facePlane, faceGeom, piece, nextPieces, ref capped);
            }

            pieces = nextPieces;
            if (pieces.Count > MaxFragmentsPerFace)
            {
                break;
            }
        }

        return pieces;
    }

    /// <summary>
    /// As <see cref="SplitPolyByBrush"/> but KEEPS the clip's classification: fragments inside brush
    /// <paramref name="ob"/>'s volume go to <paramref name="inside"/>, fragments outside to
    /// <paramref name="outside"/>, and fragments from the crossing-cutter fallback (no in/out available)
    /// to <paramref name="unknown"/>. The in/out verdict is RED's exact partition stamping — the caller's
    /// dissolve logic uses it instead of a fixed-eps volume probe wherever it exists.
    /// </summary>
    private void SplitPolyByBrushClassified(
        int ob, int facePlane, CsgPlane faceGeom, List<CsgSharedSplit.PsVert> poly,
        List<List<CsgSharedSplit.PsVert>> inside, List<List<CsgSharedSplit.PsVert>> outside,
        List<List<CsgSharedSplit.PsVert>> unknown, ref bool capped)
    {
        BrushVolume vol = _volumes[ob];
        if (!PolyOverlapsAabbWide(poly, vol.Min, vol.Max))
        {
            outside.Add(poly);
            return;
        }

        CsgSharedSplit.PsBsp? bsp = _brushBsp[ob];
        ConvexCell[]? cells = _brushCells[ob];
        CsgSharedSplit.Route = "splitclass";
        if (bsp is not null)
        {
            bsp.Clip(_registry, facePlane, poly, inside, outside);
        }
        else if (cells is not null)
        {
            // The cutting decomposition is IsClosedish-gated and winding-unverified — fine for CUTTING
            // (a bogus cell only over-cuts) but NOT trustworthy as an in/out verdict (measured: a sloppy
            // authored shell whose bottom face disagrees with its walls classified the arena floor OUTSIDE
            // its footprint as "inside" and deleted it — dmwarzone's y=-2 ring). All fragments go to
            // UNKNOWN; the caller's probe verdict (strict-gated classification cells, else ray parity)
            // decides.
            var pending = new List<List<CsgSharedSplit.PsVert>> { poly };
            foreach (ConvexCell cell in cells)
            {
                if (pending.Count == 0)
                {
                    break;
                }

                var still = new List<List<CsgSharedSplit.PsVert>>(pending.Count);
                foreach (List<CsgSharedSplit.PsVert> piece in pending)
                {
                    if (!PolyOverlapsAabb(piece, cell.Min, cell.Max))
                    {
                        still.Add(piece);
                        continue;
                    }

                    CsgSharedSplit.ConvexClip(
                        _registry, facePlane, piece, cell.Planes,
                        _boundedVolumeClip ? cell.Extents : null, BvcExtentEps, unknown, still);
                }

                pending = still;
            }

            unknown.AddRange(pending);
        }
        else
        {
            var tmp = new CsgFace { Plane = faceGeom };
            var verts = new List<CsgVertex>(poly.Count);
            foreach (CsgSharedSplit.PsVert pv in poly)
            {
                verts.Add(new CsgVertex(pv.Pos, pv.Uv));
            }

            tmp.Vertices = verts;
            List<(CsgPlane, int)> cutters = CrossingCutters(ob, tmp, facePlane);
            if (cutters.Count == 0)
            {
                unknown.Add(poly);
                CsgSharedSplit.Route = null;
                return;
            }

            CsgSharedSplit.Split(_registry, facePlane, poly, cutters, MaxFragmentsPerFace, out bool c)
                .ForEach(unknown.Add);
            capped |= c;
        }

        CsgSharedSplit.Route = null;
    }

    /// <summary>
    /// Splits shared-plane polygon <paramref name="poly"/> (on face plane <paramref name="facePlane"/>, geometry
    /// <paramref name="faceGeom"/>) along the boundary of brush <paramref name="ob"/>, appending ALL fragments
    /// (inside-brush and outside-brush) to <paramref name="output"/> with shared registry cut vertices. Uses the
    /// brush's convex BSP, its convex-decomposition cells, or the crossing-face cutter fallback — the same clip
    /// machinery the per-brush accumulator uses. A polygon that does not reach the brush is emitted whole.
    /// </summary>
    private void SplitPolyByBrush(
        int ob, int facePlane, CsgPlane faceGeom, List<CsgSharedSplit.PsVert> poly,
        List<List<CsgSharedSplit.PsVert>> output, ref bool capped)
    {
        BrushVolume vol = _volumes[ob];
        if (!PolyOverlapsAabbWide(poly, vol.Min, vol.Max))
        {
            output.Add(poly);
            return;
        }

        CsgSharedSplit.PsBsp? bsp = _brushBsp[ob];
        ConvexCell[]? cells = _brushCells[ob];
        if (bsp is not null)
        {
            bsp.Clip(_registry, facePlane, poly, output, output); // inside + outside both collected
        }
        else if (cells is not null)
        {
            var outside = new List<List<CsgSharedSplit.PsVert>> { poly };
            foreach (ConvexCell cell in cells)
            {
                if (outside.Count == 0)
                {
                    break;
                }

                var stillOutside = new List<List<CsgSharedSplit.PsVert>>(outside.Count);
                foreach (List<CsgSharedSplit.PsVert> piece in outside)
                {
                    if (!PolyOverlapsAabb(piece, cell.Min, cell.Max))
                    {
                        stillOutside.Add(piece);
                        continue;
                    }

                    CsgSharedSplit.ConvexClip(
                        _registry, facePlane, piece, cell.Planes,
                        _boundedVolumeClip ? cell.Extents : null, BvcExtentEps, output, stillOutside);
                }

                outside = stillOutside;
            }

            output.AddRange(outside);
        }
        else
        {
            var tmp = new CsgFace { Plane = faceGeom };
            var verts = new List<CsgVertex>(poly.Count);
            foreach (CsgSharedSplit.PsVert pv in poly)
            {
                verts.Add(new CsgVertex(pv.Pos, pv.Uv));
            }

            tmp.Vertices = verts;
            List<(CsgPlane, int)> cutters = CrossingCutters(ob, tmp, facePlane);
            if (cutters.Count == 0)
            {
                output.Add(poly);
                return;
            }

            CsgSharedSplit.Split(_registry, facePlane, poly, cutters, MaxFragmentsPerFace, out bool c)
                .ForEach(output.Add);
            capped |= c;
        }
    }

    /// <summary>Open iff the last brush with time index &lt; <paramref name="timeCap"/> containing the point is air
    /// (containment via the exact classification cells where available — see <see cref="IncContains"/>).</summary>
    private bool OpenAtBefore(Vec3 p, int timeCap)
    {
        int best = -1;
        bool bestAir = false;
        if (_cells.TryGetValue(Cell(p), out List<int>? bucket))
        {
            foreach (int i in bucket)
            {
                BrushVolume v = _volumes[i];
                if (v.TimeIndex < timeCap && v.TimeIndex > best && IncContains(i, p))
                {
                    best = v.TimeIndex;
                    bestAir = v.IsAir;
                }
            }
        }

        foreach (int i in _large)
        {
            BrushVolume v = _volumes[i];
            if (v.TimeIndex < timeCap && v.TimeIndex > best && IncContains(i, p))
            {
                best = v.TimeIndex;
                bestAir = v.IsAir;
            }
        }

        return best >= 0 && bestAir;
    }

    /// <summary>
    /// Builds RED's single accumulated world BSP from every brush's non-portal boundary faces (shared-plane
    /// polygons via <see cref="CollectBspFaces"/>). Returns null and records the budget flag when the tree
    /// exceeds its node/work caps, so the caller falls back to the per-brush accumulator for the whole level.
    /// </summary>
    private WorldBsp? BuildWorldBsp()
    {
        // Faces carry their RAW plane geometry (not the registry's folded canonical plane): routing the tree
        // through canonical planes was measured NET-NEGATIVE (dmabrupt holes 9→16, ctf01 56→60 vs dm04 −7) —
        // the folded plane sits up to 2e-3 from the authored geometry and shifts real walls more than it
        // reconciles seams. Coincidence folding stays at the node level (same-id consumption in WorldBsp.Build).
        var faces = new List<WorldBsp.Face>();
        for (int bi = 0; bi < _brushFaces.Count; bi++)
        {
            foreach ((CsgPlane plane, int id, List<CsgSharedSplit.PsVert> poly) in CollectBspFaces(bi))
            {
                faces.Add(new WorldBsp.Face(plane, id, poly));
            }
        }

        if (faces.Count == 0)
        {
            return null;
        }

        WorldBsp? tree = WorldBsp.Build(
            faces, WorldBspMaxNodes, WorldBspMaxWork, WorldBspMaxCandidates, out WorldBsp.Stats stats);
        WorldBspNodes = stats.Nodes;
        WorldBspLeaves = stats.Leaves;
        WorldBspBudgetExceeded = stats.BudgetExceeded;
        return tree;
    }

    /// <summary>
    /// Routes brush face <paramref name="bf"/> through the single accumulated world BSP, returning the leaf
    /// fragments (RED's phase-3 clip against the world partition). Every fragment is cut at the byte-identical
    /// registry triple point of each straddled node plane, so a face and its coincident neighbour split along
    /// the identical line — the watertight-by-construction property the per-brush clip cannot reach.
    /// </summary>
    private List<CsgFace> SplitFaceWorldBsp(int brushIndex, int faceIndex, CsgFace bf, out bool capped)
    {
        List<CsgSharedSplit.PsVert> poly = BuildInitialPoly(brushIndex, faceIndex, bf);
        int facePlane = _facePlaneId[brushIndex][faceIndex];

        var pieces = new List<List<CsgSharedSplit.PsVert>>();
        _worldBsp!.Clip(_registry, facePlane, poly, pieces, MaxFragmentsPerFace, out capped);
        System.Threading.Interlocked.Add(ref _worldFragments, pieces.Count);

        var result = new List<CsgFace>(pieces.Count);
        foreach (List<CsgSharedSplit.PsVert> frag in pieces)
        {
            if (frag.Count < 3)
            {
                continue;
            }

            var verts = new List<CsgVertex>(frag.Count);
            foreach (CsgSharedSplit.PsVert pv in frag)
            {
                verts.Add(new CsgVertex(pv.Pos, pv.Uv));
            }

            result.Add(bf.With(verts));
        }

        return result;
    }

    /// <summary>
    /// RED's watertight realisation (compiler-parity-notes.md — "leaf-based boundary extraction"). Builds the
    /// single accumulated world BSP, extracts every open|solid leaf-boundary portal (<see cref="WorldBsp.Extract"/>),
    /// and attributes each to the original brush face on its plane that covers its extent — inheriting texture,
    /// flags, smoothing, face id, source uid and re-projecting UV from that face's own planar mapping (same plane
    /// ⇒ same mapping ⇒ exact texture continuity). Returns null when the tree is over budget or the extraction hits
    /// the fragment cap, so the caller falls back to the per-brush accumulator (a partial extraction would tear holes).
    /// </summary>
    private List<CsgFace>? ExtractBoundary()
    {
        var wmin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var wmax = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (BrushVolume v in _volumes)
        {
            wmin = Vec3Math.Min(wmin, v.Min);
            wmax = Vec3Math.Max(wmax, v.Max);
        }

        WorldBsp? tree = BuildWorldBsp();
        if (tree is null)
        {
            return null; // budget exceeded — fall back to per-brush
        }

        // Extraction relies on the BIT-IDENTITY of shared triple points across neighbouring portals, so the
        // per-brush deviation bound must be OFF here (see PlaneRegistry.BoundTripleDeviation). Restored on
        // every fallback path so a budget/cap fallback runs the per-brush accumulator with its own policy.
        _registry.BoundTripleDeviation = false;
        List<WorldBsp.BoundaryPolygon> portals;
        WorldBsp.ExtractStats st;
        try
        {
            portals = tree.Extract(_registry, OpenAt, wmin, wmax, MaxExtractPortals, out st);
        }
        catch
        {
            _registry.BoundTripleDeviation = true;
            throw;
        }

        _leafDegenerate = st.Degenerate;
        _leafMaxCons = st.MaxCons;
        _leafOverCap = st.OverCap;
        if (st.Capped)
        {
            _registry.BoundTripleDeviation = true;
            return null; // pathological fan-out — fall back rather than emit a partial (holed) boundary
        }

        // Plane-aware station weld: canonicalise over-determined-corner stations that the per-portal
        // triple solve landed a fraction of a millimetre apart, so faces meeting at the corner share the
        // byte-identical position (the property RED's single global partition gives for free). Gated by
        // shared registry planes, so it never bridges distinct authored features.
        StationWeld.Canonicalize(portals);

        Dictionary<int, List<CsgFace>> planeFaces = BuildPlaneFaceIndex();
        var uvCache = new Dictionary<CsgFace, UvBasis>();
        var result = new List<CsgFace>(portals.Count);
        foreach (WorldBsp.BoundaryPolygon bp in portals)
        {
            Vec3 c = PolyCentroid(bp.Poly);
            CsgPlane emittedPlane = bp.OpenOnFront ? bp.Plane : bp.Plane.Flipped();
            CsgFace? src = AttributeFace(planeFaces, bp.PlaneId, c);
            if (src is null)
            {
                continue; // no same-plane source face at all (counted in _unattributed); anomaly, drop
            }

            if (!uvCache.TryGetValue(src, out UvBasis? basis))
            {
                basis = new UvBasis(src);
                uvCache[src] = basis;
            }

            int n = bp.Poly.Count;
            var verts = new List<CsgVertex>(n);
            if (bp.OpenOnFront)
            {
                for (int i = 0; i < n; i++)
                {
                    Vec3 p = bp.Poly[i].Pos;
                    verts.Add(new CsgVertex(p, basis.Map(p)));
                }
            }
            else
            {
                for (int i = n - 1; i >= 0; i--) // open on the back ⇒ reverse winding to face into open
                {
                    Vec3 p = bp.Poly[i].Pos;
                    verts.Add(new CsgVertex(p, basis.Map(p)));
                }
            }

            CsgFace f = src.CloneAttributes();
            f.Plane = emittedPlane;
            f.Vertices = verts;
            f.IsPortal = false;
            f.RoomIndex = -1;
            f.PortalIndexPlus2 = 0;
            result.Add(f);
        }

        // Merge the coplanar leaf portals of each source face back into maximal convex faces: the world
        // partition subdivides every original face at each crossing node plane, so a flat wall extracts as
        // many coplanar slivers. Merging kin pieces removes those internal seams (else collinear T-junctions
        // / dropped-sliver gaps) and cuts the over-split back toward RED's near-original face count.
        List<CsgFace> mergedFaces = CoplanarMerger.Merge(result);
        _extractedPortals = mergedFaces.Count;
        LeafExtractionActive = true;
        return mergedFaces;
    }

    /// <summary>
    /// SOURCE-FACE extraction (flagship 10 — RED's binary-verified face-emission semantics). Where
    /// <see cref="ExtractBoundary"/> re-derives every output face from the tree's leaf-boundary node portals
    /// (which re-tessellates curved/organic non-convex source faces into leaf-cell-bounded pieces that diverge
    /// 3–16 mm), this emits the AUTHORED source polygons SPLIT IN PLACE by exactly the world-partition planes
    /// that cross them — RED's model (<c>FUN_004a8220</c>/<c>FUN_0048bec0</c>: the accumulated world solid is a
    /// boundary-rep of oriented authored faces, only ever split by straddled partition planes; un-crossed faces
    /// pass verbatim). Each face is routed through the same global partition (<see cref="SplitFaceWorldBsp"/> —
    /// pieces of the source polygon, cut at shared <see cref="PlaneRegistry"/> triples so coincident cuts are
    /// bit-identical). Survival is the world-BSP leaf-contents test (<see cref="TrySurvive"/>). Coincident
    /// air/solid walls are deduped by <see cref="ResolveCoincident"/> (leaf contents emit both, RED keeps one),
    /// and <see cref="CoplanarMerger"/> re-merges each source face's coplanar kin so the partition's tangent
    /// cuts (a plane grazing a shared edge) become interior to one polygon and vanish — leaving the authored
    /// outer boundary intact. Returns null (fall back to per-brush) when the tree is over budget.
    /// </summary>
    private List<CsgFace>? ExtractBoundarySourceFaces()
    {
        WorldBsp? tree = BuildWorldBsp();
        if (tree is null)
        {
            return null; // budget exceeded — fall back to per-brush
        }

        // The world tree drives BOTH the geometry (route each source face through it) AND survival (leaf
        // contents). BoundTripleDeviation OFF here: bit-identity of shared triples across neighbouring faces
        // IS the watertightness (a one-sided lerp rejection would break coincident cuts apart).
        _worldBsp = tree;
        WorldBspActive = true;
        _registry.BoundTripleDeviation = false;

        // Pre-classify EVERY leaf's contents once, exactly (Chebyshev-centre interior point), so the survival
        // probe below reads the stored verdict rather than re-evaluating OpenAt at the ±eps point (which
        // mis-verdicts thin sliver leaves and dropped real walls — the node-portal path's lesson).
        var wmin2 = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var wmax2 = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (BrushVolume v in _volumes)
        {
            wmin2 = Vec3Math.Min(wmin2, v.Min);
            wmax2 = Vec3Math.Max(wmax2, v.Max);
        }

        _leafDegenerate = tree.ClassifyAllLeaves(OpenAt, wmin2, wmax2, out _leafMaxCons, out _leafOverCap);

        var partial = new List<CsgFace>[_brushFaces.Count];
        Parallel.For(0, _brushFaces.Count, bi =>
        {
            var local = new List<CsgFace>();
            List<CsgFace> faces = _brushFaces[bi];
            for (int fi = 0; fi < faces.Count; fi++)
            {
                CsgFace bf = faces[fi];
                if (bf.Vertices.Count < 3 || bf.IsPortal)
                {
                    continue;
                }

                EmitSourceFace(bi, fi, bf, local);
            }

            partial[bi] = local;
        });

        var survivors = new List<CsgFace>();
        foreach (List<CsgFace>? p in partial)
        {
            if (p is not null)
            {
                survivors.AddRange(p);
            }
        }

        // Coincident air/solid + same-kind walls: leaf contents classify both coincident faces identically, so
        // both emit — ResolveCoincident keeps the one RED keeps (its survival preference). Then merge each
        // source face's coplanar kin (only genuinely-subdivided faces emit pieces).
        List<CsgFace> resolved = ResolveCoincident(survivors);
        List<CsgFace> merged = CoplanarMerger.Merge(resolved);
        _extractedPortals = merged.Count;
        LeafExtractionActive = true;
        return merged;
    }

    /// <summary>
    /// Emits source face <paramref name="bf"/> the way RED does: route the authored polygon through the world
    /// partition to find how the leaf contents subdivide it, then
    /// <list type="bullet">
    /// <item>UN-CROSSED (every leaf fragment is the SAME open|solid boundary) ⇒ emit the WHOLE authored polygon
    /// verbatim, oriented to open — no tangent grazing-cuts, bit-identical to the per-brush path (this is what
    /// preserves curved/organic non-convex faces and kills the re-tessellation floor);</item>
    /// <item>entirely interior or entirely buried ⇒ drop;</item>
    /// <item>GENUINELY CROSSED (a real open|solid transition runs through the face) ⇒ emit only the boundary
    /// leaf-fragments (pieces of the authored polygon, cut at shared registry triples).</item>
    /// </list>
    /// </summary>
    private void EmitSourceFace(int bi, int fi, CsgFace bf, List<CsgFace> output)
    {
        List<CsgSharedSplit.PsVert> poly = BuildInitialPoly(bi, fi, bf);
        int facePlane = _facePlaneId[bi][fi];

        // Clip the AUTHORED polygon by ONLY the planes that GENUINELY CROSS it (a brush face whose polygon
        // overlaps this face along the plane-intersection line — RED's FUN_0048e4f0/FacesCross gate), NOT every
        // partition plane it straddles by extent. Un-crossed faces get no cutters ⇒ a single piece = the whole
        // authored polygon (organic/curved faces preserved verbatim). Cuts are shared registry triples, so a
        // face and its coincident/adjacent neighbour cut along the identical line.
        List<(CsgPlane Geom, int Id)> cutters = GlobalCrossingCutters(bi, fi, bf, facePlane);
        List<List<CsgSharedSplit.PsVert>> pieces;
        if (cutters.Count == 0)
        {
            System.Threading.Interlocked.Increment(ref _sfVerbatim);
            pieces = new List<List<CsgSharedSplit.PsVert>> { poly };
        }
        else
        {
            System.Threading.Interlocked.Increment(ref _sfCrossed);
            pieces = CsgSharedSplit.Split(_registry, facePlane, poly, cutters, MaxFragmentsPerFace, out bool capped);
            if (capped)
            {
                System.Threading.Interlocked.Increment(ref _cappedFaces);
            }
        }

        Vec3 n = bf.Plane.Normal;
        bool emitted = false;
        foreach (List<CsgSharedSplit.PsVert> frag in pieces)
        {
            if (frag.Count < 3)
            {
                continue;
            }

            // Survival = the two convex leaves this fragment separates (world-BSP leaf contents, RED's in/out).
            Vec3 c = PolyCentroid(frag);
            bool of = _worldBsp!.ClassifyPoint(c.Add(n.Scale(SampleEps)), OpenAt);
            bool ob = _worldBsp.ClassifyPoint(c.Sub(n.Scale(SampleEps)), OpenAt);
            if (of == ob)
            {
                continue; // interior (both open) or buried (both solid/void) — not a boundary
            }

            EmitSourcePiece(bf, frag, of, output);
            emitted = true;
        }

        if (!emitted)
        {
            System.Threading.Interlocked.Increment(ref _sfDropped);
        }
    }

    /// <summary>
    /// All registry planes that GENUINELY CROSS source face <paramref name="bf"/> — a boundary face of any
    /// spatially-overlapping OTHER brush whose polygon overlaps <paramref name="bf"/> along their plane
    /// intersection (<see cref="FacesCross"/>, RED's overlap gate). Deduped by registry id. This is the
    /// crossing-cutter set RED's phase-3 clip applies; un-crossed faces get an EMPTY set (emit verbatim).
    /// </summary>
    private List<(CsgPlane, int)> GlobalCrossingCutters(int bi, int fi, CsgFace bf, int facePlane)
    {
        Vec3 bmin = _faceAabbMin[bi][fi];
        Vec3 bmax = _faceAabbMax[bi][fi];
        var cutters = new List<(CsgPlane, int)>();
        var seen = new HashSet<int> { facePlane };
        foreach (int oi in CandidatesForAabb(bmin, bmax))
        {
            if (oi == bi)
            {
                continue;
            }

            BrushVolume vol = _volumes[oi];
            if (!AabbOverlap(bmin, bmax, vol.Min, vol.Max))
            {
                continue;
            }

            List<CsgFace> of = _brushFaces[oi];
            int[] oiPlanes = _facePlaneId[oi];
            for (int ofi = 0; ofi < of.Count; ofi++)
            {
                int id = oiPlanes[ofi];
                if (id < 0 || seen.Contains(id))
                {
                    continue;
                }

                CsgFace ofFace = of[ofi];
                if (!ofFace.IsPortal && ofFace.Vertices.Count >= 3 && Spans(bf, ofFace.Plane) && FacesCross(bf, ofFace))
                {
                    cutters.Add((ofFace.Plane, id));
                    seen.Add(id);
                }
            }
        }

        return cutters;
    }

    /// <summary>Emits one boundary piece of a source face (winding/plane oriented so the normal faces open).</summary>
    private static void EmitSourcePiece(CsgFace bf, List<CsgSharedSplit.PsVert> frag, bool openOnFront, List<CsgFace> output)
    {
        var verts = new List<CsgVertex>(frag.Count);
        if (openOnFront)
        {
            foreach (CsgSharedSplit.PsVert pv in frag)
            {
                verts.Add(new CsgVertex(pv.Pos, pv.Uv));
            }
        }
        else
        {
            for (int i = frag.Count - 1; i >= 0; i--)
            {
                verts.Add(new CsgVertex(frag[i].Pos, frag[i].Uv));
            }
        }

        CsgFace f = bf.CloneAttributes();
        f.Plane = openOnFront ? bf.Plane : bf.Plane.Flipped();
        f.Vertices = verts;
        f.IsPortal = false;
        f.RoomIndex = -1;
        f.PortalIndexPlus2 = 0;
        output.Add(f);
    }

    // ================= THE FUSION (flagship 18) =================
    //
    // GED's two proven halves, joined:
    //   HALF 1 (extraction machinery) — the world-level convex-leaf CONTENTS classification: WorldBsp +
    //     ClassifyAllLeaves + Chebyshev-centre interior points give EXACT global in/out verdicts (its old
    //     weakness was face GEOMETRY — re-tessellating from leaf boundaries).
    //   HALF 2 (source-face machinery) — authored polygons split into literal fragments on shared registry
    //     triples (its old weakness was per-fragment/per-brush SURVIVAL: flagship 10's source-face path leaked
    //     shared corners because it cut each face ONLY by the planes physically crossing it — a plane crossing
    //     one face but stopping at a shared edge left a T-junction).
    // THE FUSION routes EVERY source face down ONE global partition (registry-id node planes, 2e-3 fold),
    // splitting only where genuinely straddling (Spans = the bbox-straddle gate, then SplitOne's 1e-4 tie band).
    // Because ALL faces cut on the SAME partition, adjacent faces share stations bit-identically — no asymmetric
    // T-junctions, no extent divergence. Survival is the global leaf contents (open|solid across the fragment's
    // two sides), a WORLD-level verdict, so the extra cuts CANNOT storm (flagship 5 / 16-naive stormed on
    // per-fragment survival, NOT the cutting; two coplanar fragments of a spurious cut get the same verdict and
    // CoplanarMerger re-merges them).

    /// <summary>
    /// The fused-partition solve (flagship 18). Builds the world tree for the global leaf contents (HALF 1),
    /// pre-classifies every leaf's open/solid verdict once by construction (Chebyshev centre, time-ordered fold),
    /// then routes every authored source face down the GLOBAL partition of all brush face planes (HALF 2 with a
    /// full partition instead of per-face crossing cutters), keeping the fragments whose two leaves differ
    /// open|solid. Coincident/coplanar pairs are deduped by <see cref="ResolveCoincident"/> (survival table) and
    /// each source face's coplanar kin re-merged by <see cref="CoplanarMerger"/>. Returns null (fall back to the
    /// incremental accumulator) when the world tree is over budget.
    /// </summary>
    private List<CsgFace>? SolveFusedPartition()
    {
        WorldBsp? tree = BuildWorldBsp();
        if (tree is null)
        {
            return null; // budget exceeded — fall back to the incremental default
        }

        // The world tree drives BOTH the geometry (route each source face down the global partition) AND
        // survival (leaf contents). BoundTripleDeviation OFF here (as leaf extraction / source-face): the
        // bit-identity of shared triples across neighbouring faces IS the watertightness — a one-sided lerp
        // rejection would break coincident cuts apart into new seams.
        _worldBsp = tree;
        WorldBspActive = true;
        _registry.BoundTripleDeviation = false;

        var wmin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var wmax = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (BrushVolume v in _volumes)
        {
            wmin = Vec3Math.Min(wmin, v.Min);
            wmax = Vec3Math.Max(wmax, v.Max);
        }

        // HALF 1: classify EVERY leaf's contents once, exactly (Chebyshev-centre interior point through the
        // time-ordered fold — later-air-carves-earlier-solid, OpenAt), so the survival probe below reads the
        // stored verdict rather than re-evaluating OpenAt at the ±eps point (which mis-verdicts thin sliver
        // leaves and drops real walls — the node-portal path's lesson).
        _leafDegenerate = tree.ClassifyAllLeaves(OpenAt, wmin, wmax, out _leafMaxCons, out _leafOverCap);

        // The global partition: every brush's DISTINCT (registry-folded) non-portal face planes with their
        // supporting-face AABBs. A coincident family folds to ONE node; a near-parallel pair stays two DISTINCT
        // nodes — the property that lets BOTH members cut a crossing face (the dm04 stub closer).
        _partFaces = new (CsgPlane, int, Vec3, Vec3)[_brushFaces.Count][];
        for (int bi = 0; bi < _brushFaces.Count; bi++)
        {
            _partFaces[bi] = BuildPartitionFaces(bi);
        }

        var partial = new List<CsgFace>[_brushFaces.Count];
        Parallel.For(0, _brushFaces.Count, bi =>
        {
            var local = new List<CsgFace>();
            List<CsgFace> faces = _brushFaces[bi];
            for (int fi = 0; fi < faces.Count; fi++)
            {
                CsgFace bf = faces[fi];
                if (bf.Vertices.Count < 3 || bf.IsPortal)
                {
                    continue;
                }

                EmitFusedFace(bi, fi, bf, local);
            }

            partial[bi] = local;
        });

        var survivors = new List<CsgFace>();
        foreach (List<CsgFace>? p in partial)
        {
            if (p is not null)
            {
                survivors.AddRange(p);
            }
        }

        // Coincident air/solid + same-kind walls: the global leaf contents classify both coincident faces
        // identically (both border the same open|solid transition), so both emit — ResolveCoincident keeps the
        // one RED keeps (survival table). Then merge each source face's coplanar kin, dissolving the extra cuts
        // the full partition made where both sides survived identically (the storm-proofing).
        List<CsgFace> resolved = ResolveCoincident(survivors);
        List<CsgFace> merged = CoplanarMerger.Merge(resolved);
        _extractedPortals = merged.Count;
        LeafExtractionActive = true;
        FusedPartitionActive = true;
        return merged;
    }

    /// <summary>
    /// Emits source face <paramref name="bf"/> the fused way: route the authored polygon down the GLOBAL
    /// partition (all straddling brush face planes, not just the planes that physically cross it), then keep
    /// each fragment whose two sides differ open|solid under the world-level leaf contents (oriented into open).
    /// Un-straddled faces pass verbatim (organic/curved faces preserved). Cuts are shared registry triples, so a
    /// face and its adjacent neighbour — cut by the SAME partition plane — split at the byte-identical station.
    /// </summary>
    private void EmitFusedFace(int bi, int fi, CsgFace bf, List<CsgFace> output)
    {
        List<CsgSharedSplit.PsVert> poly = BuildInitialPoly(bi, fi, bf);
        int facePlane = _facePlaneId[bi][fi];

        List<(CsgPlane Geom, int Id)> cutters = FusedPartitionCutters(bi, fi, bf, facePlane);
        List<List<CsgSharedSplit.PsVert>> pieces;
        if (cutters.Count == 0)
        {
            System.Threading.Interlocked.Increment(ref _sfVerbatim);
            pieces = new List<List<CsgSharedSplit.PsVert>> { poly };
        }
        else
        {
            System.Threading.Interlocked.Increment(ref _sfCrossed);
            pieces = CsgSharedSplit.Split(_registry, facePlane, poly, cutters, MaxFragmentsPerFace, out bool capped);
            if (capped)
            {
                System.Threading.Interlocked.Increment(ref _cappedFaces);
            }
        }

        Vec3 n = bf.Plane.Normal;
        bool emitted = false;
        foreach (List<CsgSharedSplit.PsVert> frag in pieces)
        {
            if (frag.Count < 3)
            {
                continue;
            }

            // Survival = the two convex leaves this fragment separates (world-BSP leaf contents, RED's in/out).
            Vec3 c = PolyCentroid(frag);
            bool of = _worldBsp!.ClassifyPoint(c.Add(n.Scale(SampleEps)), OpenAt);
            bool ob = _worldBsp.ClassifyPoint(c.Sub(n.Scale(SampleEps)), OpenAt);
            if (of == ob)
            {
                continue; // interior (both open) or buried (both solid/void) — not a boundary
            }

            EmitSourcePiece(bf, frag, of, output);
            emitted = true;
        }

        if (!emitted)
        {
            System.Threading.Interlocked.Increment(ref _sfDropped);
        }
    }

    /// <summary>
    /// The GLOBAL-partition cutter set for source face <paramref name="bf"/>: every OTHER brush's registry
    /// partition plane whose supporting-face AABB overlaps the face AND which the face polygon genuinely
    /// straddles (<see cref="Spans"/> — RED's bbox-vs-plane straddle gate <c>FUN_0048e4f0</c>). Unlike
    /// <see cref="GlobalCrossingCutters"/> (which additionally required the OTHER face's polygon to overlap
    /// along the intersection line, <see cref="FacesCross"/>), this cuts wherever the PLANE straddles the face —
    /// so two adjacent faces straddling the same partition plane are cut at the SAME station even if the plane's
    /// own supporting face physically stops at their shared edge (that asymmetry was the source-face path's
    /// T-junction leak). The face's OWN brush planes are excluded (a valid brush's faces meet at edges, not
    /// cross — including them would make reflex over-cuts, flagship 11's measured net-negative). Deduped by
    /// registry id (a coincident plane family is one cut). The extra cuts cannot storm because survival is a
    /// world-level leaf verdict and CoplanarMerger re-merges coplanar kin whose two sides survived identically.
    /// </summary>
    private List<(CsgPlane, int)> FusedPartitionCutters(int bi, int fi, CsgFace bf, int facePlane)
    {
        Vec3 bmin = _faceAabbMin[bi][fi];
        Vec3 bmax = _faceAabbMax[bi][fi];
        var cutters = new List<(CsgPlane, int)>();
        var seen = new HashSet<int> { facePlane };

        // THE GLOBAL PARTITION (the mission's literal construction): cut this face by EVERY other brush's
        // registry partition plane whose supporting-face AABB overlaps it and which the face polygon genuinely
        // straddles (Spans = RED's bbox-vs-plane straddle gate FUN_0048e4f0). SplitOne then splits only across
        // the 1e-4 tie band, at the shared registry triple.
        //
        // MEASURED NET-NEGATIVE (flagship 18): closes the dm04 16 mm stub (proof a — the wall is cut by BOTH
        // near-parallel terrain siblings) and holds dm06 = 0 (proof c), and improves ctf07 90→16 (the dense
        // portal-membrane geometry the route-faces path handles better). BUT it storms the organic/terrain
        // levels (dm04 14→317, dmabrupt 6→399, ctf01 11→109) — categorised as ~1 collinear T-junction, ~138
        // parallel-offset slivers and ~178 no-partner coverage edges (many > 1 m). The storm is a GEOMETRY
        // problem: routing INDEPENDENT source faces through a global partition over-cuts, and a plane that
        // straddles one face but grazes/folds away from its neighbour produces asymmetric T-junctions and
        // coverage gaps regardless of survival correctness. The global leaf contents make SURVIVAL exact (the
        // mission's premise) but do NOT reconcile the over-cut tessellation — RED avoids this only by
        // INCREMENTALLY ACCUMULATING into a persistent shared boundary (the shipping incremental default), which
        // splits the already-shared world faces IN PLACE and passes un-crossed faces verbatim, so shared cuts
        // never diverge. A bounded cutter set (FacesCross crossings + near-parallel siblings, flagship 16's cap
        // routing generalised) was also measured: it halves the storm (dm04 205, dmabrupt 237) but stays far
        // above inc AND loses the stub closure — still net-negative. Flag stays OFF; see compiler-parity-notes.md
        // flagship 18 for the full ledger.
        foreach (int oi in CandidatesForAabb(bmin, bmax))
        {
            if (oi == bi || !AabbOverlap(bmin, bmax, _volumes[oi].Min, _volumes[oi].Max))
            {
                continue;
            }

            foreach ((CsgPlane geom, int id, Vec3 mn, Vec3 mx) in _partFaces[oi])
            {
                if (seen.Contains(id) || !AabbOverlap(bmin, bmax, mn, mx))
                {
                    continue;
                }

                if (Spans(bf, geom))
                {
                    cutters.Add((geom, id));
                    seen.Add(id);
                }
            }
        }

        return cutters;
    }

    /// <summary>Indexes every non-portal source brush face by its canonical (registry) plane id, for attribution.</summary>
    private Dictionary<int, List<CsgFace>> BuildPlaneFaceIndex()
    {
        var index = new Dictionary<int, List<CsgFace>>();
        for (int bi = 0; bi < _brushFaces.Count; bi++)
        {
            List<CsgFace> faces = _brushFaces[bi];
            int[] ids = _facePlaneId[bi];
            for (int fi = 0; fi < faces.Count; fi++)
            {
                CsgFace f = faces[fi];
                int id = ids[fi];
                if (f.IsPortal || f.Vertices.Count < 3 || id < 0)
                {
                    continue;
                }

                if (!index.TryGetValue(id, out List<CsgFace>? list))
                {
                    index[id] = list = new List<CsgFace>();
                }

                list.Add(f);
            }
        }

        return index;
    }

    /// <summary>
    /// Attributes an emitted portal to a source brush face: the same-plane face whose polygon COVERS the portal
    /// centroid (extent containment — the fidelity path). Multiple covering faces (an air panel + its backing
    /// solid at the same place) are resolved by RED's survival preference — a solid beats an air, a later air beats
    /// an earlier one, an earlier solid beats a later one — so the surviving wall's texture/UV is inherited. With no
    /// covering face the nearest same-plane face wins (the documented ambiguity fallback); with no same-plane face
    /// at all the portal is unattributed (an anomaly, tracked).
    /// </summary>
    private CsgFace? AttributeFace(Dictionary<int, List<CsgFace>> planeFaces, int planeId, Vec3 centroid)
    {
        if (!planeFaces.TryGetValue(planeId, out List<CsgFace>? cands) || cands.Count == 0)
        {
            System.Threading.Interlocked.Increment(ref _unattributed);
            return null;
        }

        CsgFace? best = null;
        foreach (CsgFace f in cands)
        {
            if (PolyContains(f, centroid) && (best is null || BeatsForAttribution(f, best)))
            {
                best = f;
            }
        }

        if (best is not null)
        {
            _attrContainment++;
            return best;
        }

        float bestD = float.MaxValue;
        foreach (CsgFace f in cands)
        {
            float d = f.Centroid().Sub(centroid).LengthSquared();
            if (d < bestD)
            {
                bestD = d;
                best = f;
            }
        }

        _attrNearest++;
        return best;
    }

    /// <summary>RED's survival preference for two coincident same-plane source faces: solid beats air; between two
    /// solids the earlier (world) wins; between two airs the later wins. Decides which face's texture a wall inherits.</summary>
    private static bool BeatsForAttribution(CsgFace a, CsgFace b)
    {
        if (a.FromAir != b.FromAir)
        {
            return !a.FromAir; // a solid owns the coincident wall over an air panel
        }

        return a.FromAir ? a.BrushTime > b.BrushTime : a.BrushTime < b.BrushTime;
    }

    /// <summary>Even-odd point-in-polygon of <paramref name="p"/> against face <paramref name="f"/> (coplanar),
    /// after dropping the face normal's dominant axis.</summary>
    private static bool PolyContains(CsgFace f, Vec3 p)
    {
        Vec3 nrm = f.Plane.Normal;
        float ax = MathF.Abs(nrm.X), ay = MathF.Abs(nrm.Y), az = MathF.Abs(nrm.Z);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        float pu = Axis(p, drop, true), pv = Axis(p, drop, false);
        bool inside = false;
        List<CsgVertex> verts = f.Vertices;
        int count = verts.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float ui = Axis(verts[i].Position, drop, true), vi = Axis(verts[i].Position, drop, false);
            float uj = Axis(verts[j].Position, drop, true), vj = Axis(verts[j].Position, drop, false);
            if (((vi > pv) != (vj > pv)) && (pu < ((uj - ui) * (pv - vi) / (vj - vi)) + ui))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float Axis(Vec3 p, int drop, bool first) => drop switch
    {
        0 => first ? p.Y : p.Z,
        1 => first ? p.X : p.Z,
        _ => first ? p.X : p.Y,
    };

    private static Vec3 PolyCentroid(List<CsgSharedSplit.PsVert> poly)
    {
        var s = new Vec3(0, 0, 0);
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            s = s.Add(v.Pos);
        }

        return s.Scale(1f / poly.Count);
    }

    /// <summary>
    /// The affine world→UV mapping of a source face's own planar texture projection, recovered from three
    /// non-collinear corners. An emitted portal on the SAME plane re-projects its vertices through this map, so
    /// texture continuity is exact (RED's rule: a wall inherits its owning face's mapping function).
    /// </summary>
    private sealed class UvBasis
    {
        private readonly Vec3 _p0, _e1, _e2;
        private readonly Uv _uv0, _duv1, _duv2;
        private readonly float _m11, _m12, _m22, _invDet;
        private readonly bool _valid;

        public UvBasis(CsgFace f)
        {
            List<CsgVertex> verts = f.Vertices;
            _p0 = verts[0].Position;
            _uv0 = verts[0].Uv;
            bool haveE1 = false, haveE2 = false;
            for (int i = 1; i < verts.Count && !haveE1; i++)
            {
                Vec3 e = verts[i].Position.Sub(_p0);
                if (e.LengthSquared() > 1e-8f)
                {
                    _e1 = e;
                    _duv1 = new Uv(verts[i].Uv.U - _uv0.U, verts[i].Uv.V - _uv0.V);
                    haveE1 = true;
                }
            }

            for (int i = 1; i < verts.Count && haveE1 && !haveE2; i++)
            {
                Vec3 e = verts[i].Position.Sub(_p0);
                if (_e1.Cross(e).LengthSquared() > 1e-8f)
                {
                    _e2 = e;
                    _duv2 = new Uv(verts[i].Uv.U - _uv0.U, verts[i].Uv.V - _uv0.V);
                    haveE2 = true;
                }
            }

            _m11 = _e1.Dot(_e1);
            _m12 = _e1.Dot(_e2);
            _m22 = _e2.Dot(_e2);
            float det = (_m11 * _m22) - (_m12 * _m12);
            _valid = haveE1 && haveE2 && MathF.Abs(det) > 1e-12f;
            _invDet = _valid ? 1f / det : 0f;
        }

        public Uv Map(Vec3 p)
        {
            if (!_valid)
            {
                return _uv0;
            }

            Vec3 d = p.Sub(_p0);
            float r1 = d.Dot(_e1), r2 = d.Dot(_e2);
            float a = ((r1 * _m22) - (r2 * _m12)) * _invDet;
            float b = ((r2 * _m11) - (r1 * _m12)) * _invDet;
            return new Uv(_uv0.U + (a * _duv1.U) + (b * _duv2.U), _uv0.V + (a * _duv1.V) + (b * _duv2.V));
        }
    }

    private void BuildGrid()
    {
        for (int i = 0; i < _volumes.Count; i++)
        {
            BrushVolume v = _volumes[i];
            (int x0, int y0, int z0) = Cell(v.Min);
            (int x1, int y1, int z1) = Cell(v.Max);
            long span = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (span > 64)
            {
                _large.Add(i);
                continue;
            }

            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!_cells.TryGetValue((cx, cy, cz), out List<int>? bucket))
                        {
                            bucket = new List<int>();
                            _cells[(cx, cy, cz)] = bucket;
                        }

                        bucket.Add(i);
                    }
                }
            }
        }
    }

    private static (int, int, int) Cell(Vec3 p) =>
        ((int)MathF.Floor(p.X / GridCell), (int)MathF.Floor(p.Y / GridCell), (int)MathF.Floor(p.Z / GridCell));

    /// <summary>
    /// Clips an arbitrary face (a portal membrane, a liquid surface) to open
    /// space: splits it wherever brush boundaries cross it and keeps only the
    /// fragments that float inside open space — points just off BOTH sides of the
    /// fragment are open. Call after <see cref="Solve"/> (the grid must exist).
    /// </summary>
    public List<CsgFace> ClipToOpen(CsgFace face)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        face.GrowAabb(ref mn, ref mx);

        var cutters = new List<CsgPlane>();
        foreach (int oi in CandidatesForAabb(mn, mx))
        {
            List<CsgFace> of = _brushFaces[oi];
            for (int ofi = 0; ofi < of.Count; ofi++)
            {
                if (!AabbOverlap(mn, mx, _faceAabbMin[oi][ofi], _faceAabbMax[oi][ofi]))
                {
                    continue;
                }

                CsgPlane plane = of[ofi].Plane;
                if (Spans(face, plane) && FacesCross(face, of[ofi]) && !AlreadyHave(cutters, plane))
                {
                    cutters.Add(plane);
                }
            }
        }

        List<CsgFace> fragments = SplitByPlanes(face, cutters, out _);
        var kept = new List<CsgFace>(fragments.Count);
        foreach (CsgFace frag in fragments)
        {
            if (frag.Vertices.Count < 3 || frag.Area() < 1e-6f)
            {
                continue;
            }

            Vec3 c = frag.Centroid();
            Vec3 n = frag.Plane.Normal;
            if (OpenAt(c.Add(n.Scale(SampleEps))) && OpenAt(c.Sub(n.Scale(SampleEps))))
            {
                kept.Add(frag);
            }
        }

        return kept;
    }

    /// <summary>
    /// RED's accumulating per-brush clip-and-classify (compiler-parity-notes.md — the residual is
    /// STRUCTURAL extent divergence, not sub-mm precision). Instead of splitting <paramref name="bf"/>
    /// only where another face crosses it (which leaves overhangs and tessellation gaps uncut), the
    /// accumulator clips <paramref name="bf"/> against the VOLUME of every spatially-overlapping brush:
    /// each convex brush partitions <paramref name="bf"/> into its inside piece and the beyond-brush
    /// (outside) pieces at the brush's silhouette planes. A ceiling that overhangs past a wall, or a
    /// floor whose neighbour ends at a different plane, is thereby cut at the bounding brush plane, so
    /// the buried overhang becomes its own fragment that the open/solid survival test drops — closing
    /// the extent/overhang leaks RED's mutual clip closes. Cut vertices are canonical three-plane
    /// points (shared with the bounding brush's own faces), so the split is watertight by construction.
    /// </summary>
    private List<CsgFace> SplitFaceAccumulate(int brushIndex, int faceIndex, CsgFace bf, out bool capped)
    {
        capped = false;
        List<CsgSharedSplit.PsVert> poly = BuildInitialPoly(brushIndex, faceIndex, bf);
        int facePlane = _facePlaneId[brushIndex][faceIndex];
        Vec3 bmin = _faceAabbMin[brushIndex][faceIndex];
        Vec3 bmax = _faceAabbMax[brushIndex][faceIndex];

        var pieces = new List<List<CsgSharedSplit.PsVert>> { poly };
        foreach (int oi in CandidatesForAabb(bmin, bmax))
        {
            if (oi == brushIndex)
            {
                continue;
            }

            BrushVolume vol = _volumes[oi];
            if (!AabbOverlap(bmin, bmax, vol.Min, vol.Max))
            {
                continue;
            }

            var next = new List<List<CsgSharedSplit.PsVert>>(pieces.Count);

            CsgSharedSplit.PsBsp? bsp = _brushBsp[oi];
            ConvexCell[]? cells = _brushCells[oi];
            if (bsp is not null)
            {
                // Convex brush: clip the piece against its (single) BSP volume — inside + beyond both kept,
                // survival drops the buried part at the brush silhouette (shared cut vertices).
                foreach (List<CsgSharedSplit.PsVert> piece in pieces)
                {
                    if (!PolyOverlapsAabb(piece, vol.Min, vol.Max))
                    {
                        next.Add(piece); // cannot pass through this brush; leave whole
                        continue;
                    }

                    bsp.Clip(_registry, facePlane, piece, next, next);
                }
            }
            else if (cells is not null)
            {
                // Closed-ish concave brush: clip the piece against each convex decomposition cell it
                // PENETRATES (per-cell AABB gate). A cell's inside fragment is buried (kept, dropped by
                // survival); the outside fragments carry on to later cells. Distant open-space faces overlap
                // no cell interior and stay whole — this is what avoids the monolithic-BSP over-cut spike.
                ClipAgainstCells(cells, facePlane, pieces, next, bmin, bmax, ref capped);
            }
            else
            {
                // Non-manifold / oversized concave brush the decomposition declined: fall back to crossing-
                // face cutters (only real face crossings cut) — watertight where it can be, the residual floor.
                List<(CsgPlane Geom, int Id)> cutters = CrossingCutters(oi, bf, facePlane);
                if (cutters.Count == 0)
                {
                    continue;
                }

                foreach (List<CsgSharedSplit.PsVert> piece in pieces)
                {
                    CsgSharedSplit.Split(_registry, facePlane, piece, cutters, MaxFragmentsPerFace, out bool c)
                        .ForEach(next.Add);
                    capped |= c;
                }
            }

            pieces = next;
            if (pieces.Count > MaxFragmentsPerFace)
            {
                capped = true;
                break;
            }
        }

        var result = new List<CsgFace>(pieces.Count);
        foreach (List<CsgSharedSplit.PsVert> frag in pieces)
        {
            if (frag.Count < 3)
            {
                continue;
            }

            var verts = new List<CsgVertex>(frag.Count);
            foreach (CsgSharedSplit.PsVert pv in frag)
            {
                verts.Add(new CsgVertex(pv.Pos, pv.Uv));
            }

            result.Add(bf.With(verts));
        }

        return result;
    }

    /// <summary>
    /// Clips the current fragment set against a decomposed concave brush's convex cells and appends all
    /// resulting fragments to <paramref name="next"/>. Each fragment is clipped only against cells whose
    /// AABB it overlaps; a cell's inside part is buried (kept, dropped later by survival), its outside part
    /// carries on to the next cell. A fragment overlapping no cell interior passes through untouched, so
    /// distant open-space faces are never cut (the localization that keeps concave clipping watertight).
    /// </summary>
    private void ClipAgainstCells(
        ConvexCell[] cells, int facePlane, List<List<CsgSharedSplit.PsVert>> pieces,
        List<List<CsgSharedSplit.PsVert>> next, Vec3 bmin, Vec3 bmax, ref bool capped)
    {
        var outside = new List<List<CsgSharedSplit.PsVert>>(pieces);
        foreach (ConvexCell cell in cells)
        {
            if (outside.Count == 0)
            {
                break;
            }

            if (!AabbOverlap(bmin, bmax, cell.Min, cell.Max))
            {
                continue; // this cell is nowhere near the source face — skip entirely
            }

            var stillOutside = new List<List<CsgSharedSplit.PsVert>>(outside.Count);
            foreach (List<CsgSharedSplit.PsVert> piece in outside)
            {
                if (!PolyOverlapsAabb(piece, cell.Min, cell.Max))
                {
                    stillOutside.Add(piece); // fragment does not reach this cell
                    continue;
                }

                CsgSharedSplit.ConvexClip(_registry, facePlane, piece, cell.Planes, next, stillOutside);
            }

            outside = stillOutside;
            if (next.Count + outside.Count > MaxFragmentsPerFace)
            {
                capped = true;
                break;
            }
        }

        next.AddRange(outside);
    }

    /// <summary>Crossing cutter planes of brush <paramref name="oi"/>'s faces vs <paramref name="bf"/> (with ids).</summary>
    private List<(CsgPlane, int)> CrossingCutters(int oi, CsgFace bf, int facePlane)
    {
        var cutters = new List<(CsgPlane, int)>();
        var seen = new HashSet<int>();
        List<CsgFace> of = _brushFaces[oi];
        int[] oiPlanes = _facePlaneId[oi];
        for (int ofi = 0; ofi < of.Count; ofi++)
        {
            int id = oiPlanes[ofi];
            if (id < 0 || id == facePlane || seen.Contains(id))
            {
                continue;
            }

            CsgFace ofFace = of[ofi];
            if (!ofFace.IsPortal && ofFace.Vertices.Count >= 3 && Spans(bf, ofFace.Plane) && FacesCross(bf, ofFace))
            {
                cutters.Add((ofFace.Plane, id));
                seen.Add(id);
            }
        }

        return cutters;
    }

    /// <summary>Builds the shared-split polygon for a brush face, snapping each corner to its plane-triple.</summary>
    private List<CsgSharedSplit.PsVert> BuildInitialPoly(int brushIndex, int faceIndex, CsgFace bf)
    {
        int facePlane = _facePlaneId[brushIndex][faceIndex];
        int[][] vplanes = _vertexPlanes[brushIndex][faceIndex];
        var poly = new List<CsgSharedSplit.PsVert>(bf.Vertices.Count);
        int[] fpset = { facePlane };
        for (int i = 0; i < bf.Vertices.Count; i++)
        {
            CsgVertex v = bf.Vertices[i];
            int[] planes = i < vplanes.Length ? vplanes[i] : fpset;

            // Snap the ORIGINAL corner to the exact intersection of its three defining planes so two
            // brushes sharing a corner resolve it to the byte-identical point (RED's shared-BSP corner),
            // instead of the float noise of two independent transforms — which at 10²-m coordinates
            // exceeds the 1e-4 weld and leaves a seam. Only when well-conditioned and sub-mm.
            Vec3 pos = v.Position;
            if (planes.Length >= 3 && _registry.Intersect(planes[0], planes[1], planes[2]) is { } snapped &&
                snapped.Sub(pos).LengthSquared() < CornerSnap * CornerSnap)
            {
                pos = snapped;
            }

            // EdgeLerpSplit (flagship 19): intern the authored corner to a shared vertex id. Coincident
            // corners across brushes collapse to one id + one canonical position, so a divergent-triple snap
            // cannot re-open a seam and the two flanking faces carry the SAME edge endpoints — the property
            // that makes a later cut of that edge share bit-identically.
            if (_registry.EdgeStore is { } store)
            {
                (int vid, Vec3 canon) = store.InternCorner(pos);
                poly.Add(new CsgSharedSplit.PsVert(canon, v.Uv, planes, vid));
            }
            else
            {
                poly.Add(new CsgSharedSplit.PsVert(pos, v.Uv, planes));
            }
        }

        return poly;
    }

    /// <summary>True when the polygon's AABB overlaps the given box (with the on-plane band as slack).</summary>
    private static bool PolyOverlapsAabb(List<CsgSharedSplit.PsVert> poly, Vec3 min, Vec3 max)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            mn = Vec3Math.Min(mn, v.Pos);
            mx = Vec3Math.Max(mx, v.Pos);
        }

        return AabbOverlap(mn, mx, min, max);
    }

    /// <summary>As <see cref="PolyOverlapsAabb"/> at the coincidence scale (see <see cref="AabbOverlapWide"/>).</summary>
    private static bool PolyOverlapsAabbWide(List<CsgSharedSplit.PsVert> poly, Vec3 min, Vec3 max)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgSharedSplit.PsVert v in poly)
        {
            mn = Vec3Math.Min(mn, v.Pos);
            mx = Vec3Math.Max(mx, v.Pos);
        }

        return AabbOverlapWide(mn, mx, min, max);
    }

    /// <summary>Splits a fragment successively by each cutter plane (convex fragments out).</summary>
    private static List<CsgFace> SplitByPlanes(CsgFace face, List<CsgPlane> planes, out bool capped)
    {
        capped = false;
        var current = new List<CsgFace> { face };
        foreach (CsgPlane plane in planes)
        {
            var next = new List<CsgFace>(current.Count + 4);
            foreach (CsgFace f in current)
            {
                SplitFace(f, plane, next);
            }

            current = next;
            if (current.Count > MaxFragmentsPerFace)
            {
                capped = true;
                break; // stop splitting; remaining fragments keep mixed classification
            }
        }

        return current;
    }

    /// <summary>Splits one face by a plane, appending the front/back fragments to <paramref name="output"/>.</summary>
    internal static void SplitFace(CsgFace face, CsgPlane plane, List<CsgFace> output)
    {
        int n = face.Vertices.Count;
        Span<float> d = n <= 64 ? stackalloc float[n] : new float[n];
        int front = 0, back = 0;
        for (int i = 0; i < n; i++)
        {
            d[i] = plane.Distance(face.Vertices[i].Position);
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
            output.Add(face); // does not straddle
            return;
        }

        var fv = new List<CsgVertex>();
        var bv = new List<CsgVertex>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            CsgVertex vi = face.Vertices[i];
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
                float t = d[i] / (d[i] - d[j]);
                CsgVertex c = CsgVertex.Lerp(vi, face.Vertices[j], t);
                fv.Add(c);
                bv.Add(c);
            }
        }

        if (fv.Count >= 3)
        {
            output.Add(face.With(fv));
        }

        if (bv.Count >= 3)
        {
            output.Add(face.With(bv));
        }
    }

    /// <summary>Applies the open-space survival test, orienting the survivor toward open space.</summary>
    private bool TrySurvive(CsgFace frag)
    {
        Vec3 c = frag.Centroid();
        Vec3 nrm = frag.Plane.Normal;

        // World-BSP path: classify by the convex LEAF on each side (RED's in/out classification), memoised
        // per leaf so tiny fragments inherit their leaf's single-valued verdict instead of a fixed-eps probe
        // that lands in the wrong leaf and mis-drops a real boundary fragment (tearing a hole).
        if (WorldBspActive && _worldBsp is not null)
        {
            bool wFront = _worldBsp.ClassifyPoint(c.Add(nrm.Scale(SampleEps)), OpenAt);
            bool wBack = _worldBsp.ClassifyPoint(c.Sub(nrm.Scale(SampleEps)), OpenAt);
            if (wFront == wBack)
            {
                return false;
            }

            if (wBack)
            {
                frag.Flip();
            }

            return true;
        }

        bool openFront = OpenAt(c.Add(nrm.Scale(SampleEps)));
        bool openBack = OpenAt(c.Sub(nrm.Scale(SampleEps)));
        if (openFront == openBack)
        {
            return false; // interior (both open) or buried (both solid)
        }

        if (openBack)
        {
            frag.Flip(); // open is behind; face it that way
        }

        // Coincident-face resolution is NOT done here (a per-fragment centroid probe
        // over-fires and drops wall area the winner does not actually cover, tearing
        // holes). It runs as a whole-list pass in ResolveCoincident, which clips each
        // loser to only the region a coincident winner covers — RED's incremental
        // partial replacement.
        return true;
    }

    /// <summary>
    /// RED's coincident/coplanar survival table DAT_0057cc48 (RED.exe, verified
    /// byte-exact against the binary). Flat 80×int32 addressed as
    /// <c>Table[(mode + operand*8)*5 + classcode]</c>: operand 0 = accumulated WORLD
    /// (earlier brush), 1 = incoming BRUSH (later brush); mode 1 = op-2 solid add,
    /// mode 3 = op-1 / air add (RED.exe FUN_004399b0 dispatch); classcode 3 =
    /// coplanar-aligned normals, 4 = coplanar-opposed (FUN_0048b050 dot≥0 test).
    /// Action values: 0 defer, 1 keep as room-bounding solid wall, 2 keep as
    /// interface, 3 keep flipped. The consumer (FUN_004a7480/FUN_004a8520) emits the
    /// LOWER-action face over a coincident higher-action one, and an equal-action tie
    /// resolves to the world (earlier) operand — which is how the winner is chosen below.
    /// </summary>
    private static readonly int[] SurvivalTable =
    {
        // WORLD (operand 0): modes 0..7, each { class0, class1, class2, class3, class4 }
        0, 2, 2, 2, 2,   0, 1, 2, 1, 2,   0, 2, 1, 2, 1,   0, 1, 2, 2, 1,
        0, 1, 2, 2, 1,   0, 2, 2, 2, 2,   0, 2, 2, 2, 2,   0, 1, 2, 1, 2,
        // BRUSH (operand 1): modes 0..7
        0, 2, 2, 2, 2,   0, 3, 1, 1, 1,   0, 1, 3, 1, 1,   0, 1, 2, 1, 1,
        0, 1, 2, 1, 1,   0, 2, 1, 1, 1,   0, 2, 1, 1, 1,   0, 1, 1, 1, 1,
    };

    private static int TableAction(int operand, int mode, int classcode) =>
        SurvivalTable[(mode + (operand * 8)) * 5 + classcode];

    /// <summary>
    /// Resolves coincident/coplanar survivors the way RED does — two faces from
    /// different brushes on the same surface are the wall claimed twice, and RED keeps
    /// exactly one. Two passes:
    /// <list type="bullet">
    /// <item>Air-vs-solid (the item-1 divergence): a SOLID brush whose boundary is
    /// coincident with an AIR wall owns that wall (survival-table class 4 ⇒ solid wins,
    /// both time orders). The air fragment is clipped against the solid's footprint —
    /// robust to bumpy terrain (a volume test, not a coplanar-normal match) and to
    /// partial backing (the uncovered remainder survives, so no hole).</item>
    /// <item>Same-kind (air/air, solid/solid): resolved by the survival table on the
    /// later brush's op; the loser is dropped only where a winner FULLY covers it (a
    /// partial same-kind overlap is left whole — the wall texture is interchangeable and
    /// a post-hoc clip there would only orphan neighbour edges into leaks).</item>
    /// </list>
    /// </summary>
    private List<CsgFace> ResolveCoincident(List<CsgFace> faces)
    {
        var afterAir = new List<CsgFace>(faces.Count);
        foreach (CsgFace f in faces)
        {
            if (f.IsPortal || !f.FromAir)
            {
                afterAir.Add(f);
                continue;
            }

            ClipAgainstBackingSolids(f, afterAir);
        }

        return DedupSameKind(afterAir);
    }

    /// <summary>
    /// Appends the parts of AIR fragment <paramref name="f"/> not owned by a coincident
    /// backing SOLID to <paramref name="output"/>. A solid owns the wall where it fills
    /// immediately behind the fragment AND carries a boundary face coincident with and
    /// facing the same way as <paramref name="f"/> (survival-table class 4 ⇒ solid wins,
    /// both time orders). To stay hole-safe over non-convex/bumpy terrain, the fragment is
    /// SPLIT along the solid's own boundary faces and only the sub-pieces the solid
    /// actually fills behind are dropped — a solid that backs part of the wall replaces
    /// exactly that part, and the unbacked remainder survives.
    /// </summary>
    private void ClipAgainstBackingSolids(CsgFace f, List<CsgFace> output)
    {
        Vec3 n = f.Plane.Normal;
        var pieces = new List<CsgFace> { f };

        foreach (int oi in CandidatesForAabb(f.Centroid(), f.Centroid()))
        {
            BrushVolume v = _volumes[oi];
            if (v.IsAir || v.TimeIndex == f.BrushTime)
            {
                continue; // only a different SOLID brush can own the wall
            }

            List<CsgFace> solidFaces = _brushFaces[oi];
            if (!SolidCarriesCoincidentFace(solidFaces, n, f.Centroid()))
            {
                continue; // the solid must own a boundary coincident with this wall
            }

            var next = new List<CsgFace>();
            foreach (CsgFace p in pieces)
            {
                SplitOffSolidBackedRegion(p, solidFaces, v, n, next);
            }

            pieces = next;
            if (pieces.Count == 0)
            {
                return; // wholly owned by backing solids
            }
        }

        foreach (CsgFace p in pieces)
        {
            if (p.Vertices.Count >= 3 && p.Area() >= 1e-6f)
            {
                output.Add(p);
            }
        }
    }

    /// <summary>True when the solid carries a boundary face coincident with, and facing the same way as, the wall.</summary>
    private static bool SolidCarriesCoincidentFace(List<CsgFace> solidFaces, Vec3 wallNormal, Vec3 wallCentroid)
    {
        foreach (CsgFace g in solidFaces)
        {
            if (!g.IsPortal && g.Vertices.Count >= 3 &&
                g.Plane.Normal.Dot(wallNormal) > 0.99f && MathF.Abs(g.Plane.Distance(wallCentroid)) < 0.15f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits <paramref name="p"/> along the solid's boundary faces that cross it, then
    /// appends to <paramref name="output"/> only the sub-fragments the solid does NOT fill
    /// immediately behind (the unbacked remainder). Sub-fragments the solid backs are
    /// dropped — the solid's own coincident face is the authored wall there.
    /// </summary>
    private static void SplitOffSolidBackedRegion(
        CsgFace p, List<CsgFace> solidFaces, BrushVolume solid, Vec3 n, List<CsgFace> output)
    {
        var cutters = new List<CsgPlane>();
        foreach (CsgFace g in solidFaces)
        {
            if (g.IsPortal || g.Vertices.Count < 3)
            {
                continue;
            }

            // A face nearly parallel to the wall is the coincident cap, not a silhouette edge.
            if (MathF.Abs(g.Plane.Normal.Dot(n)) > 0.99f)
            {
                continue;
            }

            if (Spans(p, g.Plane) && FacesCross(p, g) && !AlreadyHave(cutters, g.Plane))
            {
                cutters.Add(g.Plane);
            }
        }

        List<CsgFace> fragments = SplitByPlanes(p, cutters, out _);
        foreach (CsgFace frag in fragments)
        {
            if (frag.Vertices.Count < 3 || frag.Area() < 1e-6f)
            {
                continue;
            }

            if (!solid.Contains(frag.Centroid().Sub(n.Scale(SampleEps))))
            {
                output.Add(frag); // solid does not fill behind here ⇒ the air wall survives
            }
        }
    }

    /// <summary>
    /// Removes same-kind coincident duplicates (air/air, solid/solid): a survivor is
    /// dropped when a coplanar, area-overlapping face from a different brush of the same
    /// kind beats it (survival table) and fully covers it. Partial overlaps are kept
    /// whole to avoid orphaning neighbour edges into leaks.
    /// </summary>
    private static List<CsgFace> DedupSameKind(List<CsgFace> faces)
    {
        var buckets = new Dictionary<(int, int, int, int), List<int>>();
        for (int i = 0; i < faces.Count; i++)
        {
            (int, int, int, int) key = PlaneKey(faces[i].Plane);
            if (!buckets.TryGetValue(key, out List<int>? b))
            {
                buckets[key] = b = new List<int>();
            }

            b.Add(i);
        }

        var result = new List<CsgFace>(faces.Count);
        foreach (List<int> bucket in buckets.Values)
        {
            foreach (int i in bucket)
            {
                CsgFace f = faces[i];
                if (f.IsPortal)
                {
                    result.Add(f);
                    continue;
                }

                var fmn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                var fmx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                f.GrowAabb(ref fmn, ref fmx);

                var pieces = new List<CsgFace> { f };
                foreach (int j in bucket)
                {
                    if (j == i || pieces.Count == 0)
                    {
                        continue;
                    }

                    CsgFace g = faces[j];
                    if (g.IsPortal || g.BrushTime == f.BrushTime || g.FromAir != f.FromAir ||
                        !Coplanar(f.Plane, g.Plane) || !Beats(g, f))
                    {
                        continue;
                    }

                    var gmn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var gmx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                    g.GrowAabb(ref gmn, ref gmx);
                    if (!AabbOverlap(fmn, fmx, gmn, gmx))
                    {
                        continue;
                    }

                    var next = new List<CsgFace>(pieces.Count);
                    foreach (CsgFace p in pieces)
                    {
                        SubtractCoverage(p, g, next);
                    }

                    pieces = next;
                }

                // Dropped only if fully covered; otherwise the authored fragment stays whole.
                float remaining = 0f;
                foreach (CsgFace p in pieces)
                {
                    remaining += p.Area();
                }

                if (pieces.Count > 0 && remaining > f.Area() * 0.02f)
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when coincident face <paramref name="a"/> wins over coincident face
    /// <paramref name="b"/> (so a's surface stays and b is clipped where a covers it).
    /// Applies the survival table for the later brush's operation: the smaller action
    /// wins; an equal-action tie goes to the earlier (world) brush.
    /// </summary>
    private static bool Beats(CsgFace a, CsgFace b)
    {
        // Later brush drives the op mode (air/op-1 → 3, solid op-2 → 1).
        CsgFace later = a.BrushTime > b.BrushTime ? a : b;
        CsgFace earlier = a.BrushTime > b.BrushTime ? b : a;
        int mode = later.FromAir ? 3 : 1;

        // Coplanar class from the ORIGINAL outward normals. Both survivors face into
        // the same open space (aligned here); an air face was flipped to get there, so
        // its original normal is opposed — same brush kind ⇒ aligned (3), mixed ⇒ opposed (4).
        int cls = earlier.FromAir == later.FromAir ? 3 : 4;

        int actionEarlier = TableAction(0, mode, cls); // world operand
        int actionLater = TableAction(1, mode, cls);   // brush operand
        CsgFace winner = actionLater < actionEarlier ? later : earlier; // tie ⇒ earlier (world)
        return ReferenceEquals(winner, a);
    }

    /// <summary>
    /// Appends the parts of coplanar <paramref name="loser"/> NOT covered by convex
    /// polygon <paramref name="winner"/> to <paramref name="output"/> (the loser minus
    /// the winner footprint). Splits the loser by each of the winner's in-plane edge
    /// half-planes: a piece outside any winner edge is uncovered and kept; a piece
    /// inside every winner edge lies under the winner and is dropped.
    /// </summary>
    private static void SubtractCoverage(CsgFace loser, CsgFace winner, List<CsgFace> output)
    {
        Vec3 nrm = loser.Plane.Normal;
        var inside = new List<CsgFace> { loser };
        int m = winner.Vertices.Count;
        for (int e = 0; e < m && inside.Count > 0; e++)
        {
            Vec3 a = winner.Vertices[e].Position;
            Vec3 bpt = winner.Vertices[(e + 1) % m].Position;
            Vec3 inward = nrm.Cross(bpt.Sub(a)); // points into the winner polygon
            float len = inward.Length();
            if (len < 1e-9f)
            {
                continue;
            }

            inward = inward.Scale(1f / len);
            var edge = new CsgPlane(inward, -inward.Dot(a)); // Distance > 0 ⇒ inside the winner along this edge

            var stillInside = new List<CsgFace>(inside.Count);
            foreach (CsgFace p in inside)
            {
                var split = new List<CsgFace>(2);
                SplitFace(p, edge, split);
                foreach (CsgFace piece in split)
                {
                    if (edge.Distance(piece.Centroid()) < -Band)
                    {
                        output.Add(piece); // outside the winner along this edge ⇒ uncovered
                    }
                    else
                    {
                        stillInside.Add(piece); // still inside; keep testing against remaining edges
                    }
                }
            }

            inside = stillInside;
        }

        // Whatever is inside every winner edge is covered by the winner and dropped.
    }

    /// <summary>
    /// Keeps a surviving world fragment: appends it to the accumulated world and records it on brush b's
    /// coincident plane (the step-(b) duplicate gate + B-rep cap bookkeeping). Extracted so the region-wise
    /// remainder pieces re-enter the world by exactly the same path as a whole kept fragment.
    /// </summary>
    private void KeepWorldFragment(
        WFace w, List<CsgSharedSplit.PsVert> f, List<WFace> next,
        Dictionary<int, List<WFace>> worldOnBPlanes, Dictionary<int, List<CsgFace>> bFacesByPlane)
    {
        if (f.Count < 3)
        {
            return;
        }

        WFace kept = w.With(f);
        next.Add(kept);
        if (bFacesByPlane.ContainsKey(w.FacePlaneId))
        {
            if (!worldOnBPlanes.TryGetValue(w.FacePlaneId, out List<WFace>? list))
            {
                worldOnBPlanes[w.FacePlaneId] = list = new List<WFace>();
            }

            list.Add(kept);
        }

        if (_capCut is not null)
        {
            RecordCapCutPlanes(bFacesByPlane, f);
        }
    }

    /// <summary>
    /// Keeps a surviving world fragment carrying a DIFFERENT face's attributes (the coincident WINNER's
    /// texture/flags/faceid) over the fragment geometry, oriented as the world face was into open space. Used
    /// by the region-wise winner-in-place resolution (flagship 23B) so the winner's surface is guaranteed
    /// present over the resolved overlap when the fold's step-(b) cap emission would miss it.
    /// </summary>
    private void KeepWorldFragmentAs(
        WFace w, CsgFace attributes, List<CsgSharedSplit.PsVert> f, List<WFace> next,
        Dictionary<int, List<WFace>> worldOnBPlanes, Dictionary<int, List<CsgFace>> bFacesByPlane)
    {
        if (f.Count < 3)
        {
            return;
        }

        // Re-project the winner face's own planar UV mapping onto the fragment corners (the fragment carries
        // the LOSER's UVs; the winner's texture must be mapped with the winner's basis for texel continuity —
        // RED re-projects the surviving operand's UVs). Vertex identity/positions are unchanged.
        var basis = new UvBasis(attributes);
        var reuv = new List<CsgSharedSplit.PsVert>(f.Count);
        foreach (CsgSharedSplit.PsVert pv in f)
        {
            reuv.Add(new CsgSharedSplit.PsVert(pv.Pos, basis.Map(pv.Pos), pv.Planes, pv.VId));
        }

        var kept = new WFace(reuv, w.FacePlaneId, w.Plane, attributes);
        next.Add(kept);
        if (bFacesByPlane.ContainsKey(w.FacePlaneId))
        {
            if (!worldOnBPlanes.TryGetValue(w.FacePlaneId, out List<WFace>? list))
            {
                worldOnBPlanes[w.FacePlaneId] = list = new List<WFace>();
            }

            list.Add(kept);
        }

        if (_capCut is not null)
        {
            RecordCapCutPlanes(bFacesByPlane, f);
        }
    }

    /// <summary>
    /// Region-wise coincidence (flagship 23B): partitions world fragment <paramref name="frag"/> into the part
    /// COVERED by brush b's same-facing coincident faces <paramref name="bkin"/> (the resolved overlap) and the
    /// UNCOVERED remainder RED keeps when the coincident brush only partially overlaps. The fragment is clipped
    /// (via the shared on-edge split machinery, so cut vertices stay on the edge and carry the fold's vertex
    /// identity) against each kin face's convex in-plane footprint: a piece inside a kin is covered; a piece
    /// outside ALL kins is the remainder.
    /// </summary>
    private void SplitByKinCoverage(
        List<CsgSharedSplit.PsVert> frag, List<CsgFace> bkin, Vec3 wNormal, int facePlane,
        List<List<CsgSharedSplit.PsVert>> covered, List<List<CsgSharedSplit.PsVert>> uncovered)
    {
        var pending = new List<List<CsgSharedSplit.PsVert>> { frag };
        foreach (CsgFace kin in bkin)
        {
            if (pending.Count == 0)
            {
                break;
            }

            if (kin.IsPortal || kin.Vertices.Count < 3 || kin.Plane.Normal.Dot(wNormal) < 0.99f)
            {
                continue; // only b's SAME-facing coincident boundary defines the covered region
            }

            List<(CsgPlane Plane, int Id)> edges = InwardKinConstraints(kin, wNormal);
            if (edges.Count < 3)
            {
                continue; // degenerate kin footprint — cannot clip
            }

            var still = new List<List<CsgSharedSplit.PsVert>>(pending.Count);
            foreach (List<CsgSharedSplit.PsVert> piece in pending)
            {
                CsgSharedSplit.ConvexClip(_registry, facePlane, piece, edges, covered, still);
            }

            pending = still;
        }

        uncovered.AddRange(pending);
    }

    /// <summary>
    /// The convex kin polygon as OUTWARD-facing in-plane edge constraints for <see cref="CsgSharedSplit.ConvexClip"/>
    /// (inside the cell = behind every plane). Each edge normal is oriented away from the kin centroid, so a point
    /// inside the polygon is behind all of them. cutterId is -1 (these in-plane edges are not registry node planes)
    /// — the cut point falls to the on-edge lerp, still identity-preserving for the endpoints it shares.
    /// </summary>
    private static List<(CsgPlane Plane, int Id)> InwardKinConstraints(CsgFace kin, Vec3 nrm)
    {
        var constraints = new List<(CsgPlane, int)>(kin.Vertices.Count);
        Vec3 cen = kin.Centroid();
        int m = kin.Vertices.Count;
        for (int e = 0; e < m; e++)
        {
            Vec3 a = kin.Vertices[e].Position;
            Vec3 bpt = kin.Vertices[(e + 1) % m].Position;
            Vec3 inward = nrm.Cross(bpt.Sub(a));
            float len = inward.Length();
            if (len < 1e-9f)
            {
                continue;
            }

            inward = inward.Scale(1f / len);
            if (inward.Dot(cen.Sub(a)) < 0f)
            {
                inward = inward.Scale(-1f); // robust to kin winding: point toward the interior
            }

            Vec3 outward = inward.Scale(-1f); // ConvexClip: inside = behind (Distance <= 0)
            constraints.Add((new CsgPlane(outward, -outward.Dot(a)), -1));
        }

        return constraints;
    }

    /// <summary>Coarse quantized plane key so exactly-coplanar faces share a bucket.</summary>
    private static (int, int, int, int) PlaneKey(CsgPlane p) => (
        (int)MathF.Round(p.Normal.X * 64f),
        (int)MathF.Round(p.Normal.Y * 64f),
        (int)MathF.Round(p.Normal.Z * 64f),
        (int)MathF.Round(p.Offset * 8f));

    /// <summary>True when two planes are the same surface with aligned normals.</summary>
    private static bool Coplanar(CsgPlane a, CsgPlane b) =>
        a.Normal.Dot(b.Normal) > 0.999f && MathF.Abs(a.Offset - b.Offset) < 0.03f;

    /// <summary>Open iff the last (highest time index) brush containing the point is air.</summary>
    public bool OpenAt(Vec3 p)
    {
        int best = -1;
        bool bestAir = false;
        if (_cells.TryGetValue(Cell(p), out List<int>? bucket))
        {
            foreach (int i in bucket)
            {
                BrushVolume v = _volumes[i];
                if (v.TimeIndex > best && v.Contains(p))
                {
                    best = v.TimeIndex;
                    bestAir = v.IsAir;
                }
            }
        }

        foreach (int i in _large)
        {
            BrushVolume v = _volumes[i];
            if (v.TimeIndex > best && v.Contains(p))
            {
                best = v.TimeIndex;
                bestAir = v.IsAir;
            }
        }

        return best >= 0 && bestAir;
    }

    /// <summary>Brush indices whose grid cells overlap the given AABB (plus oversized brushes).</summary>
    private IEnumerable<int> CandidatesForAabb(Vec3 min, Vec3 max)
    {
        var seen = new HashSet<int>();
        (int x0, int y0, int z0) = Cell(min);
        (int x1, int y1, int z1) = Cell(max);
        for (int cx = x0; cx <= x1; cx++)
        {
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (_cells.TryGetValue((cx, cy, cz), out List<int>? bucket))
                    {
                        foreach (int i in bucket)
                        {
                            if (seen.Add(i))
                            {
                                yield return i;
                            }
                        }
                    }
                }
            }
        }

        foreach (int i in _large)
        {
            if (seen.Add(i))
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// True when the two faces actually overlap along their plane-intersection line
    /// (RED's overlap test), so <paramref name="of"/>'s plane genuinely cuts through
    /// <paramref name="bf"/>'s polygon rather than merely crossing its plane extent.
    /// This keeps splitting local and prevents fragment explosion.
    /// </summary>
    internal static bool FacesCross(CsgFace bf, CsgFace of)
    {
        // Where does 'of' meet bf's plane? Collect the crossing/touching points.
        CsgPlane p = bf.Plane;
        Vec3 e0 = default, e1 = default;
        int count = 0;
        int n = of.Vertices.Count;
        for (int i = 0; i < n && count < 2; i++)
        {
            Vec3 u = of.Vertices[i].Position;
            Vec3 v = of.Vertices[(i + 1) % n].Position;
            float du = p.Distance(u);
            float dv = p.Distance(v);
            if (MathF.Abs(du) <= Band)
            {
                AddPoint(ref e0, ref e1, ref count, u);
            }

            if ((du > Band && dv < -Band) || (du < -Band && dv > Band))
            {
                float t = du / (du - dv);
                AddPoint(ref e0, ref e1, ref count, u.Add(v.Sub(u).Scale(t)));
            }
        }

        if (count < 2)
        {
            return false;
        }

        // Clip the segment e0->e1 to bf's convex polygon (in bf's plane).
        Vec3 dir = e1.Sub(e0);
        float lenSq = dir.LengthSquared();
        if (lenSq < 1e-10f)
        {
            return false;
        }

        float tMin = 0f, tMax = 1f;
        int m = bf.Vertices.Count;
        for (int i = 0; i < m; i++)
        {
            Vec3 a = bf.Vertices[i].Position;
            Vec3 b = bf.Vertices[(i + 1) % m].Position;
            Vec3 inward = p.Normal.Cross(b.Sub(a)); // points into a CCW polygon
            float d0 = e0.Sub(a).Dot(inward);
            float d1 = e1.Sub(a).Dot(inward);
            // keep the part with d >= 0
            if (d0 >= 0 && d1 >= 0)
            {
                continue;
            }

            if (d0 < 0 && d1 < 0)
            {
                return false; // segment entirely outside this edge
            }

            float t = d0 / (d0 - d1);
            if (d0 < 0)
            {
                tMin = MathF.Max(tMin, t);
            }
            else
            {
                tMax = MathF.Min(tMax, t);
            }
        }

        return (tMax - tMin) * MathF.Sqrt(lenSq) > Band;
    }

    private static void AddPoint(ref Vec3 e0, ref Vec3 e1, ref int count, Vec3 p)
    {
        if (count == 0)
        {
            e0 = p;
            count = 1;
        }
        else if (count == 1 && !p.ApproxEquals(e0, Band))
        {
            e1 = p;
            count = 2;
        }
    }

    private static bool Spans(CsgFace f, CsgPlane plane)
    {
        bool front = false, back = false;
        foreach (CsgVertex v in f.Vertices)
        {
            float dd = plane.Distance(v.Position);
            if (dd > Band)
            {
                front = true;
            }
            else if (dd < -Band)
            {
                back = true;
            }

            if (front && back)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AlreadyHave(List<CsgPlane> planes, CsgPlane p)
    {
        foreach (CsgPlane q in planes)
        {
            if (q.Normal.Dot(p.Normal) > 0.99995f && MathF.Abs(q.Offset - p.Offset) < 1e-3f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AabbOverlap(Vec3 amin, Vec3 amax, Vec3 bmin, Vec3 bmax) =>
        amin.X <= bmax.X + Band && amax.X >= bmin.X - Band &&
        amin.Y <= bmax.Y + Band && amax.Y >= bmin.Y - Band &&
        amin.Z <= bmax.Z + Band && amax.Z >= bmin.Z - Band;

    /// <summary>
    /// AABB overlap at the COINCIDENCE scale (2× the registry's 2e-3 plane fold), for the incremental
    /// fold's gates. The 1e-4 band is too tight there: two registry-coincident planes can sit 2e-3 apart,
    /// so a world face 3e-4 outside a brush's AABB is still that brush's coincident wall — with the tight
    /// gate the pair was never presented to the survival table and BOTH copies emitted (the dm04 y=−60.18
    /// z-fighting floor pair, 21.9 m² overlap on the trace fixture).
    /// </summary>
    private static bool AabbOverlapWide(Vec3 amin, Vec3 amax, Vec3 bmin, Vec3 bmax)
    {
        const float W = 4e-3f;
        return amin.X <= bmax.X + W && amax.X >= bmin.X - W &&
               amin.Y <= bmax.Y + W && amax.Y >= bmin.Y - W &&
               amin.Z <= bmax.Z + W && amax.Z >= bmin.Z - W;
    }
}
