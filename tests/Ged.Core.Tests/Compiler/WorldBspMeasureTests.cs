using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// RED's SINGLE ACCUMULATED WORLD BSP (<see cref="CompileOptions.UseWorldBsp"/> / <see cref="WorldBsp"/>) —
/// the fixtures and the corpus measurement behind the flip/keep decision (compiler-parity-notes.md,
/// "the last construction").
/// <para>
/// <b>Isolated fixtures</b> pin that the world tree is watertight by construction on simple geometry
/// (air/solid pair, an embedded solid, two abutting rooms) — coincident cuts share the registry triple
/// point. <b>The corpus measurement</b> records holes / faces / solve ms / GC alloc / node-leaf-fragment
/// counts under BOTH paths and asserts the mission's MEMORY/PERF budgets (peak alloc &lt; 1.5 GB) — the
/// world tree is bounded (the 11.6 GB terrain fear does NOT materialise). The measurement documents that
/// routing original faces through the global partition OVER-SPLITS and regresses the hole count on complex
/// levels (T-junctions on edge-adjacent neighbours the fixer cannot all reconcile), so the per-brush
/// accumulator stays the sole production path; the watertight realisation needs leaf-based boundary
/// extraction (characterised in the notes), not the route-faces clip.
/// </para>
/// </summary>
public sealed class WorldBspMeasureTests
{
    private readonly ITestOutputHelper _out;

    public WorldBspMeasureTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Levels =
    {
        "dm01.rfl", "dm04.rfl", "dm06.rfl", "glass_house.rfl",
        "ctf01.rfl", "ctf02.rfl", "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl",
    };

    [Fact]
    public void WorldBsp_Is_Watertight_On_Isolated_Fixtures()
    {
        // Canonical air/solid pair (air room + air panel + coincident solid).
        var pair = new List<Brush>
        {
            CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 20, 20, 20, BrushFlags.Air, "roomtex"),
            CompilerTestBrushes.MakeBox(2, new Vec3(0, 0, 0), 6, 6, 6, BrushFlags.Air, "airtex"),
            CompilerTestBrushes.MakeBox(3, new Vec3(0, 0, 0), 6, 6, 6, BrushFlags.None, "solidtex"),
        };

        // A solid block sunk into an air room floor (an extent / overhang case).
        var embed = new List<Brush>
        {
            CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 40, 20, 40, BrushFlags.Air, "room"),
            CompilerTestBrushes.MakeBox(2, new Vec3(0, -8, 0), 10, 10, 10, BrushFlags.None, "block"),
        };

        // Two abutting air rooms sharing an interior wall (the coincident-wall watertightness case).
        var twoRooms = new List<Brush>
        {
            CompilerTestBrushes.MakeBox(1, new Vec3(-10, 0, 0), 20, 20, 20, BrushFlags.Air, "r1"),
            CompilerTestBrushes.MakeBox(2, new Vec3(10, 0, 0), 20, 20, 20, BrushFlags.Air, "r2"),
        };

        foreach ((string label, List<Brush> scene) in new[]
                 {
                     ("air/solid pair", pair), ("embedded block", embed), ("two abutting rooms", twoRooms),
                 })
        {
            CompiledLevel c = GeometryCompiler.Compile(
                scene, null, new CompileOptions { BuildSurfaces = false, UseWorldBsp = true });
            int holes = HoleDetector.Detect(c.Geometry).Count;
            _out.WriteLine($"{label,-22} holes={holes} faces={c.Geometry.Faces.Count} worldUsed={c.Report.WorldBspUsed} nodes={c.Report.WorldBspNodes} leaves={c.Report.WorldBspLeaves}");

            Assert.True(c.Report.WorldBspUsed, $"{label}: world BSP should be active");
            Assert.True(c.Report.WorldBspNodes > 0, $"{label}: world BSP should have nodes");
            Assert.Empty(HoleDetector.Detect(c.Geometry)); // watertight by construction in isolation
        }
    }

    // Load-sensitive GC.GetTotalAllocatedBytes budget assert (peak alloc < 1.5 GB).
    // Quarantined out of normal passes; run once serially per publish (docs/internal/TESTING-PROTOCOL.md).
    [Trait("Category", "Perf")]
    [Fact]
    public void Measure_WorldBsp_Vs_PerBrush_Within_Budget()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("RED single accumulated world BSP (UseWorldBsp) vs the per-brush accumulator — measured, NOT adopted.");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}; the route-faces clip over-splits and regresses holes on");
        sb.AppendLine("complex levels (T-junctions on edge-adjacent neighbours); memory/perf stay within budget.");
        sb.AppendLine();
        sb.AppendLine("level                          | path      holes  faces  solveMs  allocMB heapMB | nodes   leaves   frags     budget");
        sb.AppendLine(new string('-', 118));

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path) || !Load(path, out List<Brush> brushes, out List<RoomEffect> effects))
            {
                continue;
            }

            foreach (bool world in new[] { false, true })
            {
                BuildReport? r = Row(sb, name, world, brushes, effects);

                // Mission budget: peak alloc <= 1.5 GB. The world tree is bounded — the 11.6 GB terrain
                // fear (per-brush BSPs over 1886 shells) does not recur for a single fewest-split partition.
                if (r is not null)
                {
                    Assert.True(r.SolveAllocBytes < 1_500_000_000L,
                        $"{name} ({(world ? "world" : "perbrush")}): CSG solve allocated {r.SolveAllocBytes / 1048576.0:F0} MB, over the 1.5 GB budget");
                }
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("worldbsp_measure.txt", report);
    }

    [Trait("Category", "DeepGate")] // heavy corpus leaf-extraction sweep; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
    [Fact]
    public void Measure_LeafExtraction_Vs_PerBrush()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("RED's watertight realisation — leaf-based boundary EXTRACTION (UseLeafExtraction) vs the");
        sb.AppendLine($"per-brush accumulator. generated {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail open edges");
        sb.AppendLine("(HoleDetector) on the FULL compile (t-joints + seam sealer on, surfaces off).");
        sb.AppendLine();
        sb.AppendLine("level                          | path      holes  faces rooms(sub) prtl  solveMs totalMs allocMB | emit byExtent byNear unattr degen maxCons overCap");
        sb.AppendLine(new string('-', 145));

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path) || !Load(path, out List<Brush> brushes, out List<RoomEffect> effects))
            {
                continue;
            }

            ExtractRow(sb, name, false, brushes, effects);
            BuildReport? r = ExtractRow(sb, name, true, brushes, effects);

            // Every emitted wall inherits a real source face (texture fidelity). Memory is reported in the
            // artifact (measured ≤95 MB, well within the 1.5 GB budget) but not asserted here: the alloc proxy
            // is GC.GetTotalAllocatedBytes, a process-wide counter unreliable under xUnit's parallel classes.
            if (r is not null && r.LeafExtractionUsed)
            {
                Assert.Equal(0, r.Unattributed);
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("leaf_extraction_measure.txt", report);
    }

    private BuildReport? ExtractRow(StringBuilder sb, string name, bool extract, List<Brush> brushes, List<RoomEffect> effects)
    {
        try
        {
            // Baseline (extract == false) is the pre-flip incremental default; SharedBsp is now the shipping
            // default and is dispatched before the incremental fold, so this measurement forces it off to
            // compare leaf EXTRACTION against the incremental accumulator baseline.
            CompiledLevel c = GeometryCompiler.Compile(
                brushes, effects, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = extract, SharedBsp = false });
            BuildReport r = c.Report;
            int holes = HoleDetector.Detect(c.Geometry).Count;
            int faces = c.Geometry.Faces.Count(f => f.Texture >= 0 && f.Vertices.Count >= 3
                && ((FaceFlags)f.Flags & FaceFlags.IsDetail) == 0);
            string tag = extract ? (r.LeafExtractionUsed ? "extract" : "extr*fb") : "incremental";
            sb.AppendLine(
                $"{name,-30} | {tag,-9} {holes,5} {faces,6} {r.Rooms,4}({r.Subrooms,3}) {r.Portals,4} {r.SolveMs,8:F0} {r.ElapsedMs,7:F0} {r.SolveAllocBytes / 1048576.0,7:F0} | {r.ExtractedPortals,5} {r.AttributedByContainment,8} {r.AttributedByNearest,6} {r.Unattributed,6} {r.LeafDegenerate,5} {r.LeafMaxCons,7} {r.LeafOverCap,7}");
            return r;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{name,-30} | {(extract ? "extract" : "perbrush"),-9} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static BuildReport? Row(StringBuilder sb, string name, bool world, List<Brush> brushes, List<RoomEffect> effects)
    {
        try
        {
            // Baseline (world == false) is the pre-flip incremental default; SharedBsp is now the shipping
            // default and is dispatched before both UseWorldBsp's fallback and the incremental fold, so this
            // measurement forces it off to compare the WORLD BSP against the incremental accumulator baseline.
            CompiledLevel c = GeometryCompiler.Compile(
                brushes, effects, new CompileOptions { BuildSurfaces = false, UseWorldBsp = world, SharedBsp = false });
            BuildReport r = c.Report;
            int holes = HoleDetector.Detect(c.Geometry).Count;
            int faces = c.Geometry.Faces.Count(f => f.Texture >= 0 && f.Vertices.Count >= 3
                && ((FaceFlags)f.Flags & FaceFlags.IsDetail) == 0);
            string tag = world ? (r.WorldBspUsed ? "world" : "world*fb") : "incremental";
            sb.AppendLine(
                $"{name,-30} | {tag,-9} {holes,5} {faces,6} {r.SolveMs,8:F0} {r.SolveAllocBytes / 1048576.0,7:F0} {r.SolvePeakHeapBytes / 1048576.0,6:F0} | {r.WorldBspNodes,7} {r.WorldBspLeaves,8} {r.WorldBspFragments,9} {(r.WorldBspBudgetExceeded ? "EXCEEDED" : "-"),8}");
            return r;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{name,-30} | {(world ? "world" : "perbrush"),-9} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool Load(string path, out List<Brush> brushes, out List<RoomEffect> effects)
    {
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? b = null;
        RoomEffectsSection? e = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                b ??= bs;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                e ??= es;
            }
        }

        if (b is null)
        {
            return false;
        }

        brushes = b.Brushes.ToList();
        effects = e?.Effects.ToList() ?? new List<RoomEffect>();
        return true;
    }

    private static void Artifact(string file, string content)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, file), content);
    }
}
