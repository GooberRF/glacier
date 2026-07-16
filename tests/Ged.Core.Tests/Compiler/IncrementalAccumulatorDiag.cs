using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// DIAGNOSTIC (flagship 11): measures the INCREMENTAL BOUNDARY ACCUMULATOR against the per-brush default and
/// the node-portal leaf-extraction path on the gate levels + the three organic-surface regression levels
/// (dm02 / dm05 / ctfwlpro). Reports non-detail non-liquid open edges (HoleDetector), rooms(sub)/portals, and
/// solve/total timing so the flip ledger has a full per-level table. Writes tests/artifacts/incremental_measure.txt.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class IncrementalAccumulatorDiag
{
    private readonly ITestOutputHelper _out;

    public IncrementalAccumulatorDiag(ITestOutputHelper output) => _out = output;

    private static readonly string[] Levels =
    {
        "dm01.rfl", "dm02.rfl", "dm04.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
        "ctf01.rfl", "ctf02.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl",
        "ctf07.rfl", "dmwarzoneclassicb1.rfl",
    };

    [Fact]
    public void Measure_Incremental_Vs_PerBrush_And_NodePortal()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Incremental boundary accumulator vs per-brush (pb) vs node-portal extraction (ext).");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail non-liquid open edges (HoleDetector).");
        sb.AppendLine("orig = RED-built original geometry in the RFL. rooms shown as total(sub)/portals.");
        sb.AppendLine();
        sb.AppendLine($"{"level",-24} {"orig",4} | {"pb",4} {"ext",4} {"inc",4} | {"incRooms",10} {"incPort",4} | {"pbMs",6} {"extMs",6} {"incMs",6} | {"incFaces",8}");
        sb.AppendLine(new string('-', 108));

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                RflFile rfl = RflFile.Load(path);
                rfl.ParseAllKnownSections();
                Geometry? orig = null;
                BrushesSection? bs = null;
                RoomEffectsSection? es = null;
                foreach (RflSection s in rfl.Sections)
                {
                    if (s.Content is GeometrySection gs)
                    {
                        orig ??= gs.Geometry;
                    }
                    else if (s.Content is BrushesSection b)
                    {
                        bs ??= b;
                    }
                    else if (s.Content is RoomEffectsSection e)
                    {
                        es ??= e;
                    }
                }

                if (bs is null)
                {
                    continue;
                }

                int origHoles = orig is null ? -1 : HoleDetector.Detect(orig).Count;
                List<Brush> brs = bs.Brushes.ToList();
                List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();

                (int h, int rooms, int sub, int port, long ms, int faces) Run(CompileOptions o)
                {
                    var sw = Stopwatch.StartNew();
                    CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                    sw.Stop();
                    return (HoleDetector.Detect(c.Geometry).Count, c.Report.Rooms, c.Report.Subrooms,
                        c.Report.Portals, sw.ElapsedMilliseconds, c.Geometry.Faces.Count);
                }

                // Post-SharedBsp-flip: the DEFAULT options now select the shared BSP (dispatched before the
                // incremental/per-brush branches), so each labelled path is forced explicitly — pb needs BOTH
                // SharedBsp and IncrementalAccumulator off; inc needs SharedBsp off.
                var pb = Run(new CompileOptions { BuildSurfaces = false, SharedBsp = false, IncrementalAccumulator = false });
                var ext = Run(new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
                var inc = Run(new CompileOptions { BuildSurfaces = false, SharedBsp = false, IncrementalAccumulator = true });

                sb.AppendLine($"{name,-24} {origHoles,4} | {pb.h,4} {ext.h,4} {inc.h,4} | " +
                    $"{$"{inc.rooms}({inc.sub})",10} {inc.port,4} | {pb.ms,6} {ext.ms,6} {inc.ms,6} | {inc.faces,8}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-24} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        // dm04 uid 1/11/14 trace fixture on all three paths (overlap detector = z-fighting).
        sb.AppendLine();
        sb.AppendLine("dm04 uid 1/11/14 trace scene (overlap pairs = z-fighting; maxArea m2):");
        List<Brush>? scene = LoadTraceScene();
        if (scene is not null && scene.Count == 3)
        {
            foreach ((string label, CompileOptions o) in new[]
            {
                ("pb", new CompileOptions { BuildSurfaces = false, SharedBsp = false, IncrementalAccumulator = false }),
                ("ext", new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true }),
                ("inc", new CompileOptions { BuildSurfaces = false, SharedBsp = false, IncrementalAccumulator = true }),
            })
            {
                CompiledLevel c = GeometryCompiler.Compile(scene, null, o);
                int holes = HoleDetector.Detect(c.Geometry).Count;
                List<(int A, int B, float Area)> ov = TraceDm04Diag.FindOverlaps(c.Geometry);
                float maxArea = ov.Count == 0 ? 0f : ov.Max(o2 => o2.Area);
                sb.AppendLine($"  {label,-4} faces={c.Geometry.Faces.Count,4} holes={holes,3} overlapPairs={ov.Count,3} maxOverlapArea={maxArea:F5}");
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("incremental_measure.txt", report);
    }

    private static List<Brush>? LoadTraceScene()
    {
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

        return bs?.Brushes.Where(b => b.Uid is 1 or 11 or 14).ToList();
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
