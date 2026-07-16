using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>Severity of a build message.</summary>
public enum BuildSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A single build diagnostic. <see cref="Location"/> is an optional world point
/// the editor can jump the camera to (leaks, invalid brushes, steam-jet
/// warnings).
/// </summary>
public sealed record BuildMessage(BuildSeverity Severity, string Text, Vec3? Location = null, int BrushUid = 0);

/// <summary>
/// Stats and diagnostics produced by a geometry build, mirroring RED's
/// generate-report figures plus the modern structured warning list surfaced in
/// the Build Output panel.
/// </summary>
public sealed class BuildReport
{
    public int Brushes { get; set; }

    public int Rooms { get; set; }

    public int Subrooms { get; set; }

    public int Portals { get; set; }

    public int Faces { get; set; }

    public int FaceVertices { get; set; }

    public int Vertices { get; set; }

    public int Surfaces { get; set; }

    public int LightmapPages { get; set; }

    public int Uids { get; set; }

    /// <summary>Wall-clock build time in milliseconds.</summary>
    public double ElapsedMs { get; set; }

    /// <summary>Concave brushes clipped via their convex-decomposition BSP (vs the crossing-face fallback).</summary>
    public int DecomposedBrushes { get; set; }

    /// <summary>Eligible-looking concave brushes that fell back (budget exceeded / pathological).</summary>
    public int DecompFallbackBrushes { get; set; }

    /// <summary>Total convex pieces across all decomposed brushes.</summary>
    public int DecompTotalPieces { get; set; }

    /// <summary>Largest convex-piece count of any single decomposed brush.</summary>
    public int DecompMaxPieces { get; set; }

    // ---- World-BSP accumulator instrumentation (compiler-parity-notes.md, "the last construction") ----

    /// <summary>True when the CSG ran on RED's single accumulated world BSP (vs the per-brush accumulator).</summary>
    public bool WorldBspUsed { get; set; }

    /// <summary>True when the world-BSP build exceeded its node/work budget and fell back to the per-brush path.</summary>
    public bool WorldBspBudgetExceeded { get; set; }

    /// <summary>World-BSP internal node count (partition planes).</summary>
    public int WorldBspNodes { get; set; }

    /// <summary>World-BSP convex leaf count.</summary>
    public int WorldBspLeaves { get; set; }

    /// <summary>Total leaf fragments produced routing every boundary face through the world tree.</summary>
    public long WorldBspFragments { get; set; }

    // ---- Leaf-based boundary extraction (compiler-parity-notes.md — RED's watertight realisation) ----

    /// <summary>True when the CSG ran on the leaf-based boundary extraction (vs route-faces / per-brush).</summary>
    public bool LeafExtractionUsed { get; set; }

    /// <summary>True when the CSG ran on the fused-partition path (flagship 18 — global partition + leaf contents).</summary>
    public bool FusedPartitionUsed { get; set; }

    // ---- Incremental boundary accumulator (compiler-parity-notes.md — RED's compile architecture, flagship 11) ----

    /// <summary>True when the CSG ran on RED's incremental boundary accumulator (vs per-brush / extraction).</summary>
    public bool IncrementalUsed { get; set; }

    /// <summary>
    /// True when the CSG ran on RED's authentic single accumulated SHARED BSP (flagship 31 — the DEFAULT
    /// build method after the flip). The shared-BSP path is built on the incremental accumulator, so
    /// <see cref="IncrementalUsed"/> is also true when this is; test the two together to distinguish the
    /// shared-BSP default from the plain Incremental fold.
    /// </summary>
    public bool SharedBspUsed { get; set; }

    /// <summary>True when the incremental fold ran with EdgeLerpSplit shared vertex identity (flagship 19).</summary>
    public bool EdgeLerpSplitUsed { get; set; }

    /// <summary>Distinct shared vertex ids issued by the EdgeLerpSplit store.</summary>
    public int EdgeSharedVertices { get; set; }

    /// <summary>Coincident authored/cut corners merged to a shared id under EdgeLerpSplit.</summary>
    public int EdgeCornerMerges { get; set; }

    /// <summary>Boundary faces in the accumulated world list at the end of the incremental fold.</summary>
    public int IncWorldFaces { get; set; }

    /// <summary>World-face fragments dissolved in place by a later brush during the incremental fold.</summary>
    public int IncDissolved { get; set; }

    /// <summary>Coplanar kin fragments removed by the output-stage merge (flagship 22 instrumentation).</summary>
    public int CoplanarMerged { get; set; }

    /// <summary>Detail faces dropped by the RED-faithful output vertex cleanup (collapsed below 3 verts).</summary>
    public int OutputFacesCleaned { get; set; }

    /// <summary>Cap fragments re-cut by a flanking world plane during the B-rep pass (flagship 14 instrumentation).</summary>
    public int BRepCapCuts { get; set; }

    /// <summary>Open|solid boundary portals emitted by the leaf extraction.</summary>
    public int ExtractedPortals { get; set; }

    /// <summary>Source-face emission counts: verbatim (un-crossed), subdivided (crossed), dropped.</summary>
    public int SfVerbatim { get; set; }

    public int SfCrossed { get; set; }

    public int SfDropped { get; set; }

    /// <summary>Emitted portals whose covering source face was found by extent containment (the fidelity path).</summary>
    public int AttributedByContainment { get; set; }

    /// <summary>Emitted portals attributed to the nearest same-plane source face (no exact cover — ambiguity fallback).</summary>
    public int AttributedByNearest { get; set; }

    /// <summary>Emitted portals with no same-plane source face at all (should be ~0; a fidelity anomaly if not).</summary>
    public int Unattributed { get; set; }

    /// <summary>Degenerate/sub-resolution leaves (thinner than the vertex-resolution band; classified best-effort, collapse).</summary>
    public int LeafDegenerate { get; set; }

    /// <summary>Deepest leaf constraint count (BSP path length) — the extraction interior-point enumeration cost driver.</summary>
    public int LeafMaxCons { get; set; }

    /// <summary>Leaves whose constraint set exceeded the enumeration cap (candidate generation pruned; feasibility still full).</summary>
    public int LeafOverCap { get; set; }

    /// <summary>Bytes allocated (GC churn) across the CSG solve — the memory proxy for the world-BSP budget.</summary>
    public long SolveAllocBytes { get; set; }

    /// <summary>Managed heap size (bytes) sampled right after the CSG solve.</summary>
    public long SolvePeakHeapBytes { get; set; }

    /// <summary>Wall-clock milliseconds of the CSG solve alone (subset of <see cref="ElapsedMs"/>).</summary>
    public double SolveMs { get; set; }

    /// <summary>True when a hole/leak (room reaching the void) was detected.</summary>
    public bool HasLeak { get; set; }

    public List<BuildMessage> Messages { get; } = new();

    public bool HasErrors
    {
        get
        {
            foreach (BuildMessage m in Messages)
            {
                if (m.Severity == BuildSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Add(BuildSeverity severity, string text, Vec3? location = null, int brushUid = 0) =>
        Messages.Add(new BuildMessage(severity, text, location, brushUid));
}
