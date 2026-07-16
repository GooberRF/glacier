using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// PERMANENT fixture for the dm04 trace case (coordinator directive): brushes uid 1 / 11 / 14 — two air
/// terrain brushes + one solid — the scene where the user's screenshot showed BOTH residual symptoms at
/// once: a gaping hole and overlapping coexisting z-fighting faces (neither happens in RED).
/// <para>
/// Root causes found and fixed on the leaf-extraction path (compiler-parity-notes.md):
/// (1) near-coincident planes (3e-4 apart) became two parallel BSP node planes whose sliver leaf
/// classified unstably — the wall was emitted on BOTH planes (the overlap) with rim holes; fixed by
/// consuming faces at a node by REGISTRY id (the 2e-3 coincidence fold). (2) CoplanarMerger built
/// self-touching monster polygons which z-fought their own kin and bridged rooms; fixed by the
/// repeated-vertex merge guard. (3) ill-conditioned registry triples displaced cut vertices centimetres
/// off the edge; bounded per-path (per-brush bounds to the weld scale, extraction keeps raw triples).
/// </para>
/// <para>
/// The OVERLAP DETECTOR (coplanar, overlapping-area fragment pairs) is the assertion the pixel gates
/// cannot express: z-fighting duplicates occupy the same screen area. The extraction path is held to NO
/// VISIBLE overlap (&gt; 0.01 m²; measured max 0.002 m² — sliver-scale); the per-brush path demonstrably
/// STILL HAS the defect (4 pairs, up to 2.2 m² — the screenshot's z-fighting), so it is pinned at its
/// measured floor to catch further regression. Post-flip (flagship 12): the DEFAULT path is the incremental
/// accumulator, which closes BOTH symptoms (0 holes / 0 overlaps) — the Incremental fact uses default options
/// as the default-path gate. The per-brush and extraction paths stay flag-gated and are pinned via their
/// explicit flags (kept compilable for acceptance comparison; deletion is post-acceptance).
/// </para>
/// </summary>
public sealed class Dm04TraceCaseFixtureTests
{
    private readonly ITestOutputHelper _out;

    public Dm04TraceCaseFixtureTests(ITestOutputHelper output) => _out = output;

    /// <summary>Overlap area above which a coplanar duplicate is a VISIBLE z-fighting defect (m²).</summary>
    private const float VisibleOverlapArea = 0.01f;

    private static List<Brush>? LoadScene()
    {
        if (!Corpus.Available)
        {
            return null;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        if (!File.Exists(path))
        {
            return null;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection b)
            {
                bs = b;
                break;
            }
        }

        // Document (time) order preserved: uid1 (air terrain), uid11 (solid), uid14 (air terrain).
        return bs?.Brushes.Where(b => b.Uid is 1 or 11 or 14).ToList();
    }

    [Fact]
    public void Extraction_Has_No_Visible_Overlapping_Faces_And_Bounded_Holes()
    {
        List<Brush>? scene = LoadScene();
        if (scene is null || scene.Count != 3)
        {
            return;
        }

        CompiledLevel c = GeometryCompiler.Compile(
            scene, null, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
        Assert.True(c.Report.LeafExtractionUsed, "leaf extraction should be active");

        int holes = HoleDetector.Detect(c.Geometry).Count;
        List<(int A, int B, float Area)> overlaps = TraceDm04Diag.FindOverlaps(c.Geometry);
        float maxArea = overlaps.Count == 0 ? 0f : overlaps.Max(o => o.Area);
        _out.WriteLine($"extract: holes={holes} overlapPairs={overlaps.Count} maxOverlapArea={maxArea:F5}");

        // Symptom (b) — overlapping coexisting faces: FIXED on extraction. No visible z-fighting pair.
        Assert.True(maxArea <= VisibleOverlapArea,
            $"extraction emitted a visible overlapping face pair ({maxArea:F4} m² > {VisibleOverlapArea} m²) — the trace-case z-fighting regressed");

        // Symptom (a) — holes: the isolated 3-brush scene has boundary openings where the level's other
        // brushes would close the manifold, so absolute zero is not reachable here; the ceiling pins the
        // achieved floor (measured 47). The FULL-level gate lives in the corpus measurements.
        Assert.True(holes <= 60, $"extraction trace-case holes {holes} exceed the 60 ceiling (measured floor 47)");
    }

    /// <summary>
    /// Flagship 31 flip: RED's AUTHENTIC SINGLE ACCUMULATED SHARED BSP — now the DEFAULT compile path (the
    /// owner-approved flip) — compiles the trace scene with ZERO holes and ZERO overlapping face pairs, both
    /// screenshot symptoms fully closed at once (extraction: 0 overlaps but 20 holes; per-brush: 14 holes + 4
    /// visible overlap pairs). This fact uses the DEFAULT options (no path flag), so it doubles as the flip
    /// assertion: c.Report.SharedBspUsed proves the default path is the shared BSP. The shared BSP is built on the
    /// incremental accumulator, so IncrementalUsed is also true here; SharedBspUsed is the discriminator that the
    /// default is the shared-BSP path, not the plain Incremental fold.
    /// </summary>
    [Fact]
    public void Default_SharedBsp_Has_Zero_Holes_And_Zero_Overlaps()
    {
        List<Brush>? scene = LoadScene();
        if (scene is null || scene.Count != 3)
        {
            return;
        }

        // Default options — the flip makes the shared BSP the default; asserting SharedBspUsed below verifies
        // that (no explicit SharedBsp = true needed).
        CompiledLevel c = GeometryCompiler.Compile(
            scene, null, new CompileOptions { BuildSurfaces = false });
        Assert.True(c.Report.SharedBspUsed, "shared BSP should be the default path after the flip");
        Assert.True(c.Report.EdgeLerpSplitUsed,
            "EdgeLerpSplit (flagship 19 on-edge cut arithmetic + shared vertex identity) should default ON after the flip");

        int holes = HoleDetector.Detect(c.Geometry).Count;
        List<(int A, int B, float Area)> overlaps = TraceDm04Diag.FindOverlaps(c.Geometry);
        float maxArea = overlaps.Count == 0 ? 0f : overlaps.Max(o => o.Area);
        _out.WriteLine($"shared-bsp: holes={holes} overlapPairs={overlaps.Count} maxOverlapArea={maxArea:F5}");

        Assert.True(holes == 0, $"shared-bsp trace-case holes {holes} != 0");
        Assert.True(maxArea <= VisibleOverlapArea,
            $"shared-bsp emitted a visible overlapping face pair ({maxArea:F4} m² > {VisibleOverlapArea} m²)");
    }

    [Fact]
    public void PerBrush_Trace_Case_Defect_Floor_Is_Pinned()
    {
        List<Brush>? scene = LoadScene();
        if (scene is null || scene.Count != 3)
        {
            return;
        }

        // The default path is now the shared BSP (the flip), which is dispatched BEFORE the incremental /
        // per-brush branches, so this fact must FORCE the per-brush accumulator explicitly — SharedBsp = false
        // (so the shared-BSP branch is skipped) AND IncrementalAccumulator = false (so the incremental branch is
        // skipped too, falling through to the per-brush accumulator). It pins the KEPT per-brush path's measured
        // defect floor, not the default. The per-brush path stays flag-gated and compilable (Goober compares
        // paths during acceptance; deletion is post-acceptance).
        CompiledLevel c = GeometryCompiler.Compile(
            scene, null, new CompileOptions { BuildSurfaces = false, SharedBsp = false, IncrementalAccumulator = false });
        Assert.False(c.Report.IncrementalUsed, "per-brush path should be active (incremental forced off)");
        int holes = HoleDetector.Detect(c.Geometry).Count;
        List<(int A, int B, float Area)> overlaps = TraceDm04Diag.FindOverlaps(c.Geometry);
        _out.WriteLine($"perbrush: holes={holes} overlapPairs={overlaps.Count}");

        // The per-brush path STILL exhibits the screenshot's z-fighting (4 coplanar pairs, up to 2.2 m²)
        // and 14 open edges on this scene — the extraction path is the fix. Pin the floor so any further
        // regression is caught; these pins are deleted with the path at the flip.
        Assert.True(overlaps.Count <= 6, $"per-brush overlap pairs {overlaps.Count} exceed the pinned floor 4 (+2 headroom)");
        Assert.True(holes <= 25, $"per-brush trace-case holes {holes} exceed the 25 ceiling (measured floor 14)");
    }
}
