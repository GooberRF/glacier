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
/// DIAGNOSTIC (flagship 10): fast per-level hole table for the SOURCE-FACE extraction vs the node-portal
/// extraction vs per-brush, on the priority levels (the three regressions dm02/dm05/ctfwlpro, Goober's
/// dm04/dmabrupt, and the held zeros). Not an asserted gate — an iteration harness.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class SourceFaceExtractionDiag
{
    private readonly ITestOutputHelper _out;

    public SourceFaceExtractionDiag(ITestOutputHelper output) => _out = output;

    // Priority + hold set (the levels the mission tracks). NOTE: dmabruptdecay + kothcowb1~ are omitted from
    // this fast diagnostic because the SOURCE-FACE path's O(faces^2) global crossing-cutter collection hits a
    // perf cliff on their 12k+ organic faces (dmabrupt ~194 s, kothcow ~69 s vs node-portal ~6 s) — a decisive
    // ">2x perf" flip-blocker on its own, recorded in the parity notes; no need to re-run it every suite pass.
    private static readonly string[] Levels =
    {
        "dm02.rfl", "dm05.rfl", "ctfwlpro.rfl",              // the three blocking regressions (pb 0 / 20 / 71)
        "dm04.rfl",                                          // Goober's named level (dmabrupt is a perf cliff, see note)
        "dm06.rfl", "ctf02.rfl", "ctf01.rfl", "dm01.rfl",    // held zeros / gates
        "glass_house.rfl",
    };

    [Fact]
    public void Measure_SourceFace_Vs_NodePortal_Vs_PerBrush()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"source-face extraction diag. generated {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail non-liquid open edges.");
        sb.AppendLine($"{"level",-28} {"orig",5} | {"pb",5} {"node",5} {"src",5} | {"pbMs",6} {"nodeMs",6} {"srcMs",6} | {"srcRooms(sub)/port",18}");
        sb.AppendLine(new string('-', 108));

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                sb.AppendLine($"{name,-28} (missing)");
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
                    sb.AppendLine($"{name,-28} (no brushes)");
                    continue;
                }

                int origHoles = orig is null ? -1 : HoleDetector.Detect(orig).Count;
                List<Brush> brs = bs.Brushes.ToList();
                List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();

                (int h, long ms, string rp, string sf) pb = Run(brs, eff, new CompileOptions { BuildSurfaces = false });
                (int h, long ms, string rp, string sf) node = Run(brs, eff, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
                (int h, long ms, string rp, string sf) src = Run(brs, eff, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true, SourceFaceEmission = true });

                sb.AppendLine($"{name,-28} {origHoles,5} | {pb.h,5} {node.h,5} {src.h,5} | {pb.ms,6} {node.ms,6} {src.ms,6} | {src.rp,16} | vcd {src.sf}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-28} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("source_face_extraction.txt", report);
    }

    private static (int holes, long ms, string roomsPortals, string sf) Run(List<Brush> brs, List<RoomEffect> eff, CompileOptions opt)
    {
        var sw = Stopwatch.StartNew();
        CompiledLevel c = GeometryCompiler.Compile(brs, eff, opt);
        sw.Stop();
        int holes = HoleDetector.Detect(c.Geometry).Count;
        int rooms = c.Geometry.Rooms.Count;
        int sub = c.Geometry.Rooms.Count(r => r.IsSubroom != 0);
        int main = rooms - sub;
        int portals = c.Geometry.Portals.Count;
        string sf = $"{c.Report.SfVerbatim}/{c.Report.SfCrossed}/{c.Report.SfDropped}";
        return (holes, sw.ElapsedMilliseconds, $"{rooms}({sub})m{main}/{portals}", sf);
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
