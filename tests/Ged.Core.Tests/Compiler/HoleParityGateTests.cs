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
/// Camera-independent HOLE-PARITY gate (item 1a). Counts open, single-use non-portal,
/// NON-DETAIL edges (<see cref="HoleDetector"/>) on GED's recompiled geometry (via the DEFAULT
/// compile path — now RED's authentic SHARED BSP, flagship 31, the owner-approved flip) and on
/// the level's ORIGINAL RED-compiled geometry. Detail sheets (glass, gratings, flat
/// panels) never close a manifold loop, so they are excluded on both sides — with them
/// gone RED's original geometry is watertight (~0 open edges) across the corpus, which
/// makes this gate sharp.
/// <para>
/// The pixel-parity gates use two fixed camera viewpoints and MISSED the item-1
/// air-drop regression (holes are only visible from inside the leaking room); this gate
/// would have caught it — the <c>SolidOwnsCoincidentFace</c> air-fragment over-drop spiked
/// dmabruptdecay from a watertight-baseline to ~1476 open edges. GED's exact-arithmetic
/// CSG splitter leaves a per-level residual leak floor on bumpy, coplanar-heavy terrain
/// that RED's shared BSP splitter re-stitches away; <see cref="SeamSealer"/> reproduces RED's
/// t-joint fixer (FUN_004972e0, tolerance 1e-4, binary-verified) at GED's numerical scale to
/// close the near-coincident half of that floor, and the coincident-face resolution is
/// leak-neutral, so each level's count sits at the reduced floor. The per-level ceilings pin
/// the floor: a coincident-resolution or seam regression spikes far past them, while the fully
/// watertight levels hold the exact RED bound.
/// </para>
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class HoleParityGateTests
{
    private readonly ITestOutputHelper _out;

    public HoleParityGateTests(ITestOutputHelper output) => _out = output;

    // Per-level ceiling on GED's non-detail, non-liquid open-edge count under the DEFAULT compile path,
    // which is now RED's authentic SHARED BSP (flagship 31 — CompileOptions.SharedBsp defaults true, the
    // owner-approved flip). RED (with detail AND liquid excluded) is ~0 for the indoor levels; GED's value is
    // the shared-BSP fold's achieved residual floor AFTER SeamSealer plus a little headroom (+2), so a
    // coincident-resolution or seam regression on the shipping path spikes far past the ceiling.
    // RE-MEASURED under the new default (SharedBspDiag — shared_bsp_corpus.txt — and a dedicated flip-day
    // measurement through THIS gate's exact path, mover-excluded brushes + default CompileOptions): the shared
    // BSP is parity-or-better than the Incremental method on the whole corpus (better=2/worse=0/equal=33). On
    // the 11 originally-gated levels shared == the prior Incremental floor, so those ceilings are UNCHANGED:
    // dm04 6, ctf01 8, ctf02 5, ctfwlpro 8, dmabrupt 6, and dm01/dm02/dm05/dm06/glass_house/kothcow watertight
    // (floor 0, held hard at ≤2). GATE EXPANDED at the flip: the flagship key levels (dm07/ctf07/ctf04/dm15/
    // dm08/dmedgeofdespair) join so the flip's two WINS — ctf07 74→42 and dmedgeofdespair →0 (GED beats RED's
    // own 36, the kothcow pattern) — are pinned at their new tighter floors. The shared BSP carries both RED
    // watertightness properties at once (shared registry cuts + verbatim un-crossed authored faces) with both
    // world faces and caps routed down ONE symmetric partition; the SeamSealer still reconciles the sub-mm
    // divergent-triple station cohort (measured NOT removable — see compiler-parity-notes.md, the flip ledger).
    // Floor + 2 headroom; zeros held hard at ≤2 (the watertight convention). dm04/ctf01/ctfwlpro floors reflect
    // the flagship-32 RED-shared-corner snap (dm04 9→6, ctfwlpro 20→8) and flagship-19 EdgeLerpSplit (ctf01 →8).
    private static readonly IReadOnlyDictionary<string, int> Ceiling = new Dictionary<string, int>
    {
        ["dm01.rfl"] = 2,   // floor 0 (watertight)
        ["dm02.rfl"] = 2,   // floor 0 (watertight)
        ["dm04.rfl"] = 8,   // floor 6 (flagship 32: RED-shared-corner snap closes seams E/H + cluster-3 duplicate; was 11)
        ["dm05.rfl"] = 2,   // floor 0 (watertight)
        ["dm06.rfl"] = 2,   // floor 0 (watertight)
        ["glass_house.rfl"] = 2, // floor 0 (watertight)
        ["ctf01.rfl"] = 10, // el floor 8 (flagship 19 flip; was inc 11)
        ["ctf02.rfl"] = 7,  // floor 5
        ["ctfwlpro.rfl"] = 10, // floor 8 (flagship 32: RED-shared-corner snap closes 12 seams; was floor 20 / ceiling 22)
        ["dmabruptdecayrc2a27.rfl"] = 8, // floor 6
        ["kothcowb1~.rfl"] = 2, // floor 0 (watertight — beats RED's own 8)
        ["dm07.rfl"] = 16,  // floor 14 (SharedBsp-flip measurement; RED's own baseline is 8 here)
        ["ctf07.rfl"] = 44, // floor 42 (the flip WIN: 74→42 under SharedBsp; rooms/portals RED-exact 158/97)
        ["ctf04.rfl"] = 15, // floor 13
        ["dm15.rfl"] = 18,  // floor 16
        ["dm08.rfl"] = 7,   // floor 5
        ["dmedgeofdespairb1a1.rfl"] = 2, // floor 0 (the flip WIN: →0 watertight — beats RED's own 36)
    };

    // Levels GED compiles fully watertight on the default (shared-BSP) path — held to the exact RED bound
    // (orig + tiny). kothcow and dmedgeofdespair also compile watertight (shared floor 0) but their RED
    // baselines are NOT watertight (8 / 36 genuine open edges), so they are gated by the hard ≤2 Ceiling
    // above, not this RED-parity bound.
    private static readonly HashSet<string> Watertight = new()
    {
        "dm01.rfl", "dm02.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
    };

    // RED-baseline non-detail open-edge sanity bound (guards the corpus file changing under us). RED's own
    // geometry is watertight (~0) on the indoor levels; kothcow (outdoor terrain, measured 8), dm07 (measured
    // 8), and dmedgeofdespair (community outdoor, measured 36) carry genuine open sky edges, so their baseline
    // bounds are higher. GED's shared-BSP default still seals kothcow and dmedgeofdespair to 0.
    private static int RedBaselineCeiling(string fileName) => fileName switch
    {
        "kothcowb1~.rfl" => 10,
        "dm07.rfl" => 10,
        "dmedgeofdespairb1a1.rfl" => 40,
        _ => 2,
    };

    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm02.rfl")]
    [InlineData("dm04.rfl")]
    [InlineData("dm05.rfl")]
    [InlineData("dm06.rfl")]
    [InlineData("glass_house.rfl")]
    [InlineData("ctf01.rfl")]
    [InlineData("ctf02.rfl")]
    [InlineData("ctfwlpro.rfl")]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("kothcowb1~.rfl")]
    [InlineData("dm07.rfl")]
    [InlineData("ctf07.rfl")]
    [InlineData("ctf04.rfl")]
    [InlineData("dm15.rfl")]
    [InlineData("dm08.rfl")]
    [InlineData("dmedgeofdespairb1a1.rfl")]
    public void Recompiled_Geometry_Holes_Stay_At_Or_Below_The_Level_Floor(string fileName)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        if (!Load(path, out Geometry orig, out var brushes, out var effects))
        {
            return;
        }

        Geometry mine = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;

        int origHoles = HoleDetector.Detect(orig).Count;
        int gedHoles = HoleDetector.Detect(mine).Count;
        _out.WriteLine($"{fileName}: origHoles(non-detail)={origHoles} gedHoles={gedHoles} ceiling={Ceiling[fileName]}");

        // RED's own geometry is watertight once detail sheets are excluded (outdoor levels carry a few
        // genuine open sky edges — kothcow's 8 — so the baseline bound is per-level).
        Assert.True(origHoles <= RedBaselineCeiling(fileName),
            $"{fileName}: RED baseline unexpectedly has {origHoles} non-detail open edges");

        // GED never exceeds the per-level floor (a coincident-resolution regression blows past it).
        Assert.True(gedHoles <= Ceiling[fileName],
            $"{fileName}: {gedHoles} open edges exceeds the {Ceiling[fileName]} floor — a hole regression");

        // Where GED is fully watertight, hold the exact RED-parity bound.
        if (Watertight.Contains(fileName))
        {
            Assert.True(gedHoles <= origHoles + 2,
                $"{fileName}: {gedHoles} open edges vs RED's {origHoles} (watertight level regressed)");
        }
    }

    private static bool Load(string path, out Geometry orig, out List<Brush> brushes, out List<RoomEffect> effects)
    {
        orig = null!;
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? o = null;
        BrushesSection? b = null;
        RoomEffectsSection? e = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                o ??= gs.Geometry;
            }
            else if (s.Content is BrushesSection bs)
            {
                b ??= bs;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                e ??= es;
            }
        }

        if (o is null || b is null)
        {
            return false;
        }

        orig = o;
        // Match RED's static fold: exclude mover-owned brushes (they animate from the movers section).
        brushes = MoverBrushes.ExcludeMovers(b.Brushes, MoverBrushes.CollectMoverUids(rfl));
        effects = e?.Effects.ToList() ?? new List<RoomEffect>();
        return true;
    }
}
