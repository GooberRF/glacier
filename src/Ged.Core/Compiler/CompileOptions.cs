using System;
using System.Collections.Generic;
using System.Threading;
using Ged.Core.Lighting;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>Progress callback payload: current stage text and an n/m counter.</summary>
public readonly record struct CompileProgress(string Stage, int Current, int Total);

/// <summary>
/// Tunables for a geometry build. Defaults reproduce a stock RED compile; the
/// Alpine flag enables geoable/breakable room isolation and the room↔brush uid
/// map used by the alpine_level_properties chunk.
/// </summary>
public sealed class CompileOptions
{
    /// <summary>Enable Alpine geoable/breakable brush room isolation + uid mapping.</summary>
    public bool Alpine { get; set; }

    /// <summary>
    /// [ALPINE] Brush UIDs (geoable ∪ breakable) that must each isolate into their own
    /// compiled detail room — the source of truth is <c>alpine_level_properties</c>, not
    /// a per-brush flag. Populated from the alpine props at build time so the compiler
    /// isolates geoable brushes even when they carry infinite life and no editor flag,
    /// and so their compiled room UIDs can be matched back to the (brush_uid → room_uid)
    /// tables the game reads for geomod / breakable materials.
    /// </summary>
    public IReadOnlyCollection<int> IsolatedBrushUids { get; set; } = System.Array.Empty<int>();

    /// <summary>
    /// Use RED's SINGLE ACCUMULATED WORLD BSP (<see cref="WorldBsp"/>) for the CSG split instead of the
    /// per-brush accumulator: every boundary face is routed through ONE partition over all brush face
    /// planes, so coincident cuts are bit-identical and the residual seams the per-brush clip cannot
    /// close (its faces are cut by DIFFERENT trees) vanish by construction. Dual-path while measured;
    /// a level whose world tree exceeds the build budget falls back to the per-brush path automatically.
    /// See compiler-parity-notes.md ("the last construction").
    /// </summary>
    public bool UseWorldBsp { get; set; }

    /// <summary>
    /// RED's actual watertight realisation (compiler-parity-notes.md — "leaf-based boundary extraction"):
    /// build the single accumulated world BSP (as <see cref="UseWorldBsp"/>) but EXTRACT the boundary
    /// face set from the tree instead of routing original faces through it. For every leaf-boundary portal
    /// between an OPEN and a SOLID leaf (or the void) a face is emitted — the node splitting plane clipped
    /// to its leaf region (qbsp portal construction) — attributed to the original brush face sharing that
    /// plane and covering its extent (texture/UV re-projected, flags/smoothing/face_id inherited). For a
    /// sealed level every open leaf is a bounded convex cell, so all portal corners are registry-exact
    /// three-plane points: coincident cuts are bit-identical and the boundary is watertight by construction
    /// (the collinear T-junctions between differently-subdivided neighbours are exact-on-edge, so the
    /// t-joint fixer closes them cleanly — the sub-mm float seams the per-brush clip leaves never arise).
    /// Dual-path while measured; a level whose world tree exceeds the build budget falls back to the
    /// per-brush accumulator automatically.
    /// </summary>
    public bool UseLeafExtraction { get; set; }

    /// <summary>
    /// SOURCE-FACE emission for the leaf-extraction path (flagship 10 — RED's actual face semantics,
    /// binary-verified: RED never re-derives a face from leaf boundaries; its accumulated world solid is a
    /// boundary-rep of oriented AUTHORED polygons that are only ever SPLIT IN PLACE by the planes that cross
    /// them — <c>FUN_004a8220</c> processes only class-0 spanning faces, <c>FUN_0048bec0</c> splits each at
    /// the partition planes it straddles, un-crossed faces pass verbatim). When set (with
    /// <see cref="UseLeafExtraction"/>), the boundary geometry comes from routing each SOURCE face polygon
    /// through the world partition (fragments are pieces of the authored polygon, shared registry cuts,
    /// coplanar kin re-merged so tangent cuts vanish) and survival is the world-BSP leaf contents — instead of
    /// emitting re-tessellated node-plane leaf portals. Preserves organic/curved non-convex faces verbatim
    /// exactly as the per-brush path does, removing the re-tessellation floor. Off = the node-portal extraction.
    /// </summary>
    public bool SourceFaceEmission { get; set; }

    /// <summary>
    /// RED's actual compile architecture — the INCREMENTAL BOUNDARY ACCUMULATOR (flagship 11,
    /// compiler-parity-notes.md). Starts from an empty world boundary and folds each brush in strict
    /// linear time order into a PERSISTENT list of oriented boundary faces: the new brush both (a) SPLITS
    /// the already-accumulated world faces IN PLACE where its volume crosses them — dissolving the pieces
    /// whose far side flips open↔solid — and (b) contributes its own faces where they border the opposite
    /// state, split against the earlier-brush partition. Every cut is a shared <see cref="PlaneRegistry"/>
    /// triple, so coincident cuts coincide bit-identically AND un-crossed authored faces pass through
    /// verbatim — RED's two watertightness properties TOGETHER, which the per-brush accumulator (shared
    /// cuts, but independent per-face survival → coincident z-fighting) and the leaf-extraction path
    /// (shared cuts, but re-tessellated organic faces) each have only half of. Coincident/coplanar pairs
    /// are resolved by the survival table (<c>ResolveCoincident</c>); the RED 1e-4 t-joint fixer + room
    /// flood run unchanged downstream. FLIPPED to the DEFAULT compile path (flagship 12): equal-or-better
    /// than the per-brush accumulator on 34/35 corpus levels (watertight on 20 vs 10; dm04 170→14,
    /// dmabrupt 138→6). The per-brush, world-BSP, and leaf-extraction paths stay flag-gated and selectable:
    /// set <see cref="UseLeafExtraction"/> or <see cref="UseWorldBsp"/> to pick them (they take precedence
    /// over this default), or set <c>IncrementalAccumulator = false</c> for the per-brush accumulator.
    /// </summary>
    public bool IncrementalAccumulator { get; set; } = true;

    /// <summary>
    /// CONSTRUCTION-TIME shared-vertex reconciliation for the incremental fold (flagship 14). Binary
    /// verification (RED.exe 1.20na, ghidraRF) established RED's world boundary is NOT an auto-sewing
    /// shared-EDGE B-rep: it is a per-face circular vertex loop (head <c>face+0x40</c>, node
    /// <c>{vertexId, x, y, z, w, next, prev}</c>) referencing SHARED VERTEX IDS, reconciled by a BOUNDED
    /// (1e-4) t-joint fixer (<c>FUN_004972e0</c>→<c>FUN_00496770</c>→<c>FUN_00496550</c>, which splices a node
    /// into ONE face's loop and is looped over every flanking face). RED's watertightness at a divergent
    /// organic corner comes instead from clipping BOTH the world faces AND the operand's own cap faces down
    /// the SAME global accumulated partition (<c>FUN_0048bec0</c>) — which contains BOTH near-parallel terrain
    /// planes as nodes — so their cut vertices coincide by construction. GED's fold clips a cap against the
    /// earlier brush's convex-decomposition VOLUME, whose local boundary in the sub-region is a SINGLE plane,
    /// so the cap misses the other near-parallel cut and a stub opens (dm04 face 35 / 1312·1313 / cutter 1315,
    /// the 16 mm Xa→Xb no-partner edge). When set, step (a) records the registry planes that produced each
    /// cut vertex on brush b's own face planes, and step (b) re-cuts b's cap faces by exactly those planes —
    /// so the cap acquires the SAME registry-triple vertices the flanking world faces got, making the stub
    /// impossible by construction. Default OFF (flag-gated while measured); the incremental accumulator is
    /// otherwise unchanged.
    /// </summary>
    public bool BRepBoundary { get; set; }

    /// <summary>
    /// PARTITION-CONSISTENT operand clipping for the incremental fold (flagship 15) — RED's cap-from-cut-CHORD
    /// semantics (<c>FUN_0048bec0</c>: the operand's cap is clipped down the SAME partition that cut the world
    /// faces, so it inherits the world's cut chord). The flagship-14 <see cref="BRepBoundary"/> re-cut a cap by
    /// EVERY recorded plane whenever a near-parallel pair was present, which over-cut some caps and opened fresh
    /// slivers (ctfwlpro 20→22). This flag instead records the world-cut vertex POSITIONS on each brush face
    /// plane during step (a) and re-cuts the cap in step (b) ONLY by planes carrying a REAL 2-corner world edge
    /// (≥2 recorded corners ≥5 mm apart — a genuine chord such as dm04's terrain plane 35 with Xa+Xb), rejecting
    /// planes touched at a single grazing corner (the near-parallel siblings whose infinite extent over-cuts).
    /// A corpus dot-sweep proved the harmful folds are inseparable from the beneficial ones by pair angle; the
    /// real-edge (2-corner) span is the discriminator. Strictly superior to <see cref="BRepBoundary"/>: no
    /// ctfwlpro regression, fewer holes (ctf01 6 vs 10), ~half the face inflation. Default OFF (flag-gated while
    /// measured — two non-gate community levels, ctf04/ctfstockintrade, still regress by the per-brush-vs-global
    /// partition T-junction limit); the incremental accumulator is otherwise byte-unchanged.
    /// </summary>
    public bool PartitionClip { get; set; }

    /// <summary>
    /// THE GLOBAL ACCUMULATED PARTITION for the incremental fold (flagship 16 — the convergence pass). RED
    /// (RED.exe 1.20na, ghidraRF) clips EVERY face — the accumulated world faces AND each new brush's OWN cap
    /// faces — down ONE global accumulated partition (<c>FUN_0048bec0</c> walk; node children <c>+0x20/+0x24</c>;
    /// per-plane split <c>FUN_0048e2c0</c> keeps the CLOSEST valid straddling plane, 1e-4 tie band; bbox straddle
    /// gate <c>FUN_0048e4f0</c>/<c>FUN_004c9af0</c>; fragments are literal pieces of the source polygon; depth cap
    /// 0x10). Because both members of a near-parallel plane pair are partition NODES, every face crossing that
    /// region is cut at the SAME stations — the extent-divergence stub (dm04 16 mm Xa→Xb) and the asymmetric
    /// T-junctions PartitionClip opened (ctf04/ctfstockintrade) become impossible by construction, rather than
    /// re-cut after the fact. When set, the incremental fold accumulates the brushes' face planes (registry ids,
    /// 2e-3 fold — a coincident family is ONE node, a near-parallel pair stays two DISTINCT nodes, which is the
    /// point) and routes both the world faces (step a) and every incoming cap (step b) down that SAME partition;
    /// coincident (own-node) faces resolve by the survival-table fold logic, never re-split. Supersedes
    /// <see cref="BRepBoundary"/> and <see cref="PartitionClip"/> conceptually (the cap re-cut / cap-from-chord
    /// were bounded approximations of this). Default OFF (flag-gated while measured); the incremental accumulator
    /// is otherwise byte-unchanged.
    /// </summary>
    public bool GlobalPartition { get; set; }

    /// <summary>
    /// THE FUSION (flagship 18 — the convergence of GED's two proven halves). Routes EVERY source face
    /// (world and brush alike) down ONE global partition of the level's brush face planes (registry ids,
    /// 2e-3 fold: a coincident family is ONE node, a near-parallel pair stays two DISTINCT nodes), splitting
    /// each authored polygon only where it genuinely straddles a partition plane (bbox-straddle gate then the
    /// 1e-4 tie band, the binary-verified <c>FUN_0048bec0</c>/<c>FUN_0048e2c0</c> semantics), at the
    /// byte-identical <see cref="PlaneRegistry"/> triple. Because ALL faces are cut on the SAME partition,
    /// adjacent faces share cut stations bit-identically — no asymmetric T-junctions, no extent divergence,
    /// by construction (the flagship-10 source-face path leaked because it cut each face only by the planes
    /// that physically crossed it, so a plane crossing one face but stopping at a shared edge left a T-junction;
    /// the global partition cuts BOTH sides).
    /// <para>
    /// Fragment SURVIVAL is the world-level convex-leaf CONTENTS (<see cref="WorldBsp.ClassifyAllLeaves"/> +
    /// Chebyshev-centre interior points, the boolean CONTENT of the world after the linear time-order fold —
    /// later-air-carves-earlier-solid, via <see cref="CsgSolver.OpenAt"/>): a fragment is kept iff the leaf
    /// contents differ across its two sides (open|solid, oriented into open); coplanar pairs are resolved by
    /// the survival table <c>DAT_0057cc48</c> in <c>ResolveCoincident</c>. Because survival is a global leaf
    /// verdict — NOT a per-fragment/per-brush probe — the extra cuts of the full partition CANNOT storm
    /// (flagship 5's and flagship 16's failure mode was per-fragment/per-brush survival, not the cutting): two
    /// coplanar fragments of a spurious cut get the same verdict and <see cref="CoplanarMerger"/> re-merges them.
    /// </para>
    /// Dual-path while measured (default OFF); a level whose world tree exceeds the build budget falls back to
    /// the incremental accumulator. See compiler-parity-notes.md flagship 18.
    /// </summary>
    public bool FusedPartition { get; set; }

    /// <summary>
    /// CONSTRUCTION-TIME on-edge cut arithmetic + shared vertex identity for the incremental fold
    /// (flagship 19 — the surgical arithmetic swap). Binary verification (RED.exe 1.20na) established RED
    /// computes every cut vertex ON the edge being cut (<c>t = -((edgeStart·N)+d)/(edgeDir·N)</c>,
    /// <c>point = edgeStart + t·edgeDir</c>) with adjacent faces referencing SHARED vertex ids, so a shared
    /// edge cut by one plane yields the byte-identical point in both flanking faces automatically, and every
    /// unavoidable T-junction lands EXACTLY on a neighbour's edge where the 1e-4 fixer closes it. GED's
    /// default derives cut vertices from PLANE-TRIPLE intersections (<see cref="PlaneRegistry"/>), identical
    /// across faces only when the same triple is used and physically off the real edge on ill-conditioned
    /// near-parallel terrain — the residual 0.1–3 mm station cohort. When set, the fold assigns each authored
    /// corner a shared id (coincident corners collapse to one id + one canonical position), and every cut is
    /// computed by on-edge lerp on the stored endpoints and interned ONCE by (endpoint ids, cutter), so both
    /// flanking faces reference the byte-identical shared cut vertex. FLIPPED to DEFAULT ON (flagship 19):
    /// measured equal-or-better than the plane-triple arithmetic on EVERY one of the 35 corpus levels
    /// (BETTER=3: dm04 14→13, ctf01 11→8, ctf07 90→74; EQUAL=32 including all 20+ watertight zeros;
    /// WORSE=0 — the first zero-regression construction of the campaign), rooms/portals byte-identical,
    /// perf ≤1.05× (several levels faster). Set false for the plane-triple arithmetic (kept path, no
    /// deletion); the incremental fold is byte-unchanged with it off (a cut vertex carries VId = -1).
    /// </summary>
    public bool EdgeLerpSplit { get; set; } = true;

    /// <summary>
    /// RED's AUTHENTIC SINGLE ACCUMULATED SHARED BSP (the commissioned endgame — compiler-parity-notes.md
    /// flagship 31). The incremental persistent shared boundary (as <see cref="IncrementalAccumulator"/>) but
    /// with BOTH the accumulated world faces (step a) AND every incoming brush cap (step b) routed down ONE
    /// accumulated partition of the brushes' face planes, SYMMETRICALLY — so where two differently-tessellated
    /// terrain/floor surfaces meet, they are subdivided by the SAME partition stations and their shared corners
    /// are bit-identical by construction. This is the construction flagship 30 identified as the requirement for
    /// dm04's 9 residual seams ("every terrain face AND every cap clipped down the SAME partition"): the
    /// GlobalPartition path (flagship 16) routed only the CAP side (world faces stayed volume-clipped, an
    /// asymmetry that traded seams); the FusedPartition path (flagship 18) routed both but as INDEPENDENT source
    /// faces with leaf-contents survival (which over-cut and stormed). SharedBsp keeps the persistent shared
    /// boundary — faces split IN PLACE, shared vertex ids (<see cref="EdgeLerpSplit"/>), un-crossed faces passed
    /// verbatim — and the incremental fold's per-fragment far-side-flip + region-wise survival table (NOT
    /// leaf-contents), which is precisely what makes the full symmetric routing safe: a shared cut is inserted
    /// once and both flanks reference the same station. FLIPPED to the DEFAULT compile path (owner decision —
    /// compiler-parity-notes.md flip ledger): measured parity-or-better than the incremental accumulator on the
    /// whole corpus (SharedBspDiag — better=2/worse=0/equal=33; ctf07 74→42, dmedgeofdespair 4→0; dm04 6==6;
    /// rooms/portals byte-identical on every checked level). The IncrementalAccumulator/GlobalPartition/leaf/
    /// per-brush paths stay selectable — because this branch is dispatched BEFORE them (CsgSolver.Solve), a caller
    /// selecting one of those paths must ALSO set <c>SharedBsp = false</c>. Set false for GED's Incremental
    /// accumulator (kept path, no deletion).
    /// </summary>
    public bool SharedBsp { get; set; } = true;

    /// <summary>
    /// EXTENT-GATED brush volume-classification clip (the commissioned RED-authentic close-out — compiler-parity-
    /// notes.md flagship 35). Route-attribution (flagship 34) proved dm04's residual seams are born in the brush
    /// VOLUME CLIP (<c>SplitPolyByBrushClassified</c> / the hybrid cap's <c>ClipAgainstEarlierBrushes</c> →
    /// <c>SplitPolyByBrush</c> → <c>PsBsp.Clip</c>/<c>ConvexClip</c>): GED cut foreign faces by a brush's UNBOUNDED
    /// solid-BSP node planes, so a terrain/wall plane extended past its supporting FACE slices a foreign rock/floor
    /// far beyond where the brush actually reaches, spawning phantom slivers RED never generates. RED's cut is
    /// extent-gated everywhere (binary-verified: <c>FUN_0048e4f0</c> → <c>FUN_004c9af0</c>, a segment-vs-AABB gate
    /// that skips any BSP node whose bounded extent the clipped geometry misses). When set, each convex hull/cell
    /// face carries its bounded AABB extent and a node/constraint plane cuts a foreign polygon only where the
    /// crossing overlaps that extent; a straddle whose crossing lies wholly beyond the extent is a phantom — the
    /// whole (uncut) polygon is classified OUTSIDE (RED's plane-half-space verdict with no geometric split). Correct
    /// by construction: crossing a bounded cell face is the ONLY way a foreign polygon enters the convex region.
    /// Applied on the <see cref="SharedBsp"/> path (concave-cell <c>ConvexClip</c> only; the convex-hull
    /// <c>PsBsp.Clip</c> has no phantom to gate, front-of-any-face being always outside).
    /// <para>
    /// MEASURED DECISIVELY NET-NEGATIVE (flagship 35 — tests/artifacts/bvc_corpus.txt): bvc vs shared
    /// <b>better=0 worse=10 equal=25 zeros-broken=3</b> (ctf05/dm17/dmedification 0→broken; dm04 6→14; dmwarzone
    /// 17→57), and it closes NONE of dm04's residual six (they persist byte-identically). The per-cell-face extent
    /// is the wrong granularity: a large foreign face is trivially "wholly outside" a small terrain triangle's AABB,
    /// so the polygon-level gate suppresses the many legitimate classification cuts, while the crossing-level gate
    /// that DOES target the slivers over-suppresses worse still (dm04 6→63). This empirically CONFIRMS flagship 34's
    /// thesis — no local extent gate separates RED's few over-cuts from the dense field of legitimate near-parallel
    /// terrain cuts; the six need RED's GLOBAL tessellation, not a bounded-face clip. Kept selectable + default OFF
    /// as the reproducible measurement (as <see cref="FusedPartition"/> keeps its stormed form); the room/portal
    /// graph HELD on all 11 checked levels. Production is byte-identical with the flag off. NOT flipped.
    /// </para>
    /// </summary>
    public bool BoundedVolumeClip { get; set; }

    /// <summary>
    /// REGION-WISE coincident-face resolution for the incremental fold (flagship 23B). Binary verification
    /// (RED.exe 1.20na, ghidraRF: phase-3 <c>FUN_004a8220</c> → <c>FUN_0048bec0</c> clips every class-0
    /// spanning/coplanar-pending face down the OTHER solid's BSP partition, splitting it at each straddled
    /// node plane before <c>FUN_004a7480</c> classifies + applies the survival table <c>DAT_0057cc48</c>
    /// PER FRAGMENT) established that RED resolves a PARTIALLY overlapping coincident pair region-wise: the
    /// table verdict applies only to the 2D OVERLAP region; each face's non-overlapping remainder survives
    /// independently with its OWN identity/texture. GED's per-brush fold resolved coincidence FACE-WISE — the
    /// covered-coincident branch dissolved the WHOLE world fragment on a single verdict at the covering brush
    /// face's centroid, even where that brush's coincident face only partially covers the fragment. When set,
    /// the covered-branch table dissolve is CLIPPED to the covering brush faces: only the covered part is
    /// dropped; the uncovered remainder is kept (RED's ground truth — dmabrupt brush 86 keeps its
    /// non-overlap wall strip's own texture rather than losing it to the later coincident brush 108). Cut
    /// vertices go through the same <see cref="CsgSharedSplit"/> on-edge machinery, preserving the fold's
    /// vertex identity. Default ON (the fix); set false for the prior face-wise behaviour.
    /// </summary>
    public bool RegionWiseCoincidence { get; set; } = true;

    /// <summary>
    /// Coincident-corner merge tolerance for <see cref="EdgeLerpSplit"/> (metres). Coincident authored/cut
    /// corners within this distance collapse to one shared vertex id + canonical position, which is what lets
    /// two divergent-triple flanking edges become the SAME edge. Null uses the fold's measured default; 0
    /// merges only bit-identical corners (the pure no-weld baseline). Used only when EdgeLerpSplit is set.
    /// </summary>
    public float? EdgeMergeTolerance { get; set; }

    /// <summary>
    /// RED's PORTAL-SIDE ROOM CLASSIFICATION by majority FACE-VOTE (flagship 24 — the water-room fix).
    /// Binary verification (RED.exe 1.20na, ghidraRF): the room-creation driver <c>FUN_00485990</c>
    /// classifies which side of a membrane a ROOM lies on with <c>FUN_004861d0</c> — it walks the room's
    /// face loop (<c>room+0x40</c>) and, per face, calls <c>FUN_0048a790</c> (signed distance at the ±1e-4
    /// band, <c>_DAT_0055c804 = −1e-4</c> / <c>_DAT_00554714 = +1e-4</c>) to tally front(1)/on(2)/back(3),
    /// returning FRONT iff <c>front &gt; back</c> (area tiebreak <c>_DAT_0055470c = 0</c> when every face is
    /// coplanar). It is a TOPOLOGICAL majority vote of the room's own faces against the membrane plane, NOT a
    /// geometric point-probe. GED's default per-fragment vertical-ray / smallest-containing-AABB probe
    /// (<see cref="RoomBuilder.ResolvePortalSide"/>) resolves the LIQUID room on BOTH sides of the near-
    /// horizontal water membrane (its pool walls reach y≈−2.0, inside the y≈−2.25 membrane band), starving
    /// the water-surface portal to edge slivers (dmabrupt: RED's ONE 28.8×11 m ≈317 m² liquid↔air portal
    /// becomes ~5 m² of slivers → both-ways PVS collapse). When set, each membrane's two sides are the
    /// adjacent MAIN rooms whose face-vote lands on opposite sides of its plane; the per-fragment geometric
    /// probe is the fallback where the vote is inconclusive. Default on.
    /// </summary>
    public bool PortalFaceVote { get; set; } = true;

    /// <summary>Build the lightmap surfaces + atlas pages (off for a fast CSG-only preview).</summary>
    public bool BuildSurfaces { get; set; } = true;

    /// <summary>Run the t-joint fixing pass (off for a fast preview).</summary>
    public bool FixTJoints { get; set; } = true;

    /// <summary>
    /// Flagship 22 (output-stage coplanar merge): after the incremental fold, re-merge each source
    /// face's coplanar kin fragments into maximal convex faces (<see cref="CoplanarMerger.MergeRobust"/>).
    /// The incremental fold splits authored faces in place at every accumulated boundary, leaving a flat
    /// wall as many coplanar convex slivers (dmabrupt +42% faces vs RED); re-merging kin restores near-
    /// original faces and cuts the per-room face count toward RED's, without crossing any real CSG
    /// boundary (kin = one source face id) so watertightness and UV continuity are preserved. Default on.
    /// </summary>
    public bool MergeCoplanarOutput { get; set; } = true;

    /// <summary>
    /// RED's per-output-face vertex cleanup (BuildFinalRenderSolid <c>FUN_00496150</c>): drop repeated and
    /// redundant-collinear vertices from every detail (geoable/breakable) face, so the compiled surfaces the
    /// in-game geomod cap triangulator processes are clean. Without it, authored/near-coincident duplicate
    /// corners survive verbatim and Alpine's <c>ear_clip_triangulate</c> stalls ("[CapFace] Ear clip stuck"),
    /// leaving dug geoable/breakable brushes uncapped. Load-bearing T-junction corners are always kept, so the
    /// pass is watertight by construction. Default on. See <see cref="OutputFaceCleanup"/>.
    /// </summary>
    public bool CleanOutputFaces { get; set; } = true;

    /// <summary>
    /// EXPERIMENTAL measurement override for the <see cref="SeamSealer"/> weld/stitch tolerance (flagship 16 —
    /// the census-predicted sealer-tightening sweep). When null the compiler picks the per-path default
    /// (RED's tight 1 mm on the per-brush path; 3 mm on the incremental/leaf-extraction paths, which carry a
    /// wider divergent-triple station cohort). Set to RED's 1e-4 fixpoint tolerance (or 0 to disable the weld)
    /// to measure whether a station-coincident partition path lets the compensation be tightened/retired. Not
    /// used by any shipping build; the default paths keep their measured-optimal tolerances.
    /// </summary>
    public float? SealTolerance { get; set; }

    /// <summary>
    /// Merge coplanar, co-room, edge-adjacent faces into one lightmap surface
    /// (RED's final-build grouping — fewer atlas pages). Off = one surface per face
    /// (RED's live-preview behaviour).
    /// </summary>
    public bool GroupSurfaces { get; set; } = true;

    /// <summary>
    /// Item 6 (amendment): "High-Resolution Lightmaps" — raise the lightmap texel density above
    /// RED's stock ceiling (8 px/m) so a projection cookie / gobo can actually resolve. Verified
    /// format-safe against RF.exe (FUN_004ed1c0 reads page w/h from the file and allocates
    /// dynamically; surface x/y/w/h are u8 ≤255): 256×256 atlas pages and fragments up to 255,
    /// ppm scaled ×4 (up to 32 px/m). Stock (false) keeps 128 pages / 64-texel fragments / 8 px/m
    /// so the parity gates stay byte-identical.
    /// </summary>
    public bool HighResLightmaps { get; set; }

    /// <summary>
    /// Bake real lighting into the atlas (Calculate Lighting). Opt-in so the
    /// default build keeps the byte-identity round-trip invariants; when off the
    /// surfaces are seeded with ambient only.
    /// </summary>
    public bool BakeLighting { get; set; }

    /// <summary>Bake tunables (shadows, clamp, quality) when <see cref="BakeLighting"/> is on.</summary>
    public LightingOptions Lighting { get; set; } = new();

    /// <summary>
    /// The level's mover brushes (elevators, doors, lifts). Supplied so the lighting bake can include
    /// them as shadow occluders at their rest pose when <see cref="LightingOptions.MoverShadows"/> is on
    /// ("Movers cast shadows"). RED itself excludes mover geometry from the shadow occluder set
    /// (RED.exe 1.20na <c>FUN_004ae360</c>: faces whose moving-group type is 4/5/7 are rejected via
    /// <c>FUN_004bcc60</c>, and a surface never self-shadows against its own solid via the +0x36 owner
    /// check) — a moving object cannot bake a fixed shadow — so the RED-matching / byte-parity state is
    /// OFF and this list is unused there. Populated by <see cref="GeometryBuildService"/>.
    /// </summary>
    public IReadOnlyList<Brush> Movers { get; set; } = Array.Empty<Brush>();

    /// <summary>Level lights (from the lights section) contributing to the game bake.</summary>
    public IReadOnlyList<Light> Lights { get; set; } = Array.Empty<Light>();

    /// <summary>Editor-only lights — excluded from the game bake, available for preview.</summary>
    public IReadOnlyList<Light> EditorOnlyLights { get; set; } = Array.Empty<Light>();

    /// <summary>Level ambient colour (level_properties); when null a neutral white ambient is used.</summary>
    public RfColor? LevelAmbient { get; set; }

    /// <summary>
    /// Resolves a texture name to its content-derived face traits (invisible /
    /// alpha / holes). RED derives these from the texture's alpha channel at
    /// compile time; the app supplies a VFS-backed provider. When null (or the
    /// provider returns null for a name) a name-based fallback detects RF's
    /// *_invisible* wall textures.
    /// </summary>
    public Func<string, TextureTraits?>? TextureTraits { get; set; }

    /// <summary>Optional progress sink (stage + n/m), invoked on the compiling thread.</summary>
    public Action<CompileProgress>? Progress { get; set; }

    /// <summary>Cancellation token; the compiler checks it between brushes and stages.</summary>
    public CancellationToken Cancellation { get; set; } = CancellationToken.None;
}
