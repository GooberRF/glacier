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
/// DIAGNOSTIC (flagship 9): establishes the acceptance target set — every corpus level whose
/// RED-BUILT ORIGINAL geometry measures ZERO holes (HoleDetector: is_detail + LiquidSurface excluded).
/// For each such level, measures the per-brush default and the leaf-extraction path so we can see, at a
/// glance, which levels the shipping default must build clean and where extraction still leaks.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class CleanSetHoleMeasureTests
{
    private readonly ITestOutputHelper _out;

    public CleanSetHoleMeasureTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Measure_CleanSet_And_Both_Paths()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("RED-original clean-set + GED path holes. holes = non-detail non-liquid open edges (HoleDetector).");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. orig = RED-built original geometry in the RFL.");
        sb.AppendLine("compile columns measured only where orig==0 (the acceptance clean set) or a known gate level.");
        sb.AppendLine();
        sb.AppendLine($"{"level",-30} {"brushes",7} {"origHoles",9} | {"pbHoles",7} {"extHoles",8} | {"pbMs",7} {"extMs",7}");
        sb.AppendLine(new string('-', 92));

        var gateLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dm01.rfl", "dm04.rfl", "dm06.rfl", "glass_house.rfl",
            "ctf01.rfl", "ctf02.rfl", "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl",
        };

        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
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

                int origHoles = orig is null ? -1 : HoleDetector.Detect(orig).Count;
                int brushes = bs?.Brushes.Count ?? 0;

                bool measureCompile = bs is not null && (origHoles == 0 || gateLevels.Contains(name));
                string pbH = "-", extH = "-", pbMs = "-", extMs = "-";
                if (measureCompile)
                {
                    List<Brush> brs = bs!.Brushes.ToList();
                    List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();

                    var sw = Stopwatch.StartNew();
                    CompiledLevel pb = GeometryCompiler.Compile(
                        brs, eff, new CompileOptions { BuildSurfaces = false });
                    sw.Stop();
                    pbH = HoleDetector.Detect(pb.Geometry).Count.ToString();
                    pbMs = sw.ElapsedMilliseconds.ToString();

                    sw.Restart();
                    CompiledLevel ex = GeometryCompiler.Compile(
                        brs, eff, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
                    sw.Stop();
                    extH = HoleDetector.Detect(ex.Geometry).Count.ToString();
                    extMs = sw.ElapsedMilliseconds.ToString();
                }

                sb.AppendLine($"{name,-30} {brushes,7} {origHoles,9} | {pbH,7} {extH,8} | {pbMs,7} {extMs,7}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-30} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("clean_set_holes.txt", report);
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
