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
/// DIAGNOSTIC (flagship 14): measures the construction-time B-rep cap re-cut
/// (<see cref="CompileOptions.BRepBoundary"/>) against the shipping incremental default on the gate corpus.
/// Reports non-detail non-liquid open edges (HoleDetector), faces, rooms(sub)/portals, and solve timing so
/// the flip ledger has a full per-level before→after table. Also runs the dm04 stub-impossibility proof:
/// the 16 mm no-partner edge (-37.742,-65.162,-9.939)→(-37.726,-65.159,-9.936) (SharedEdgeDiag) must be
/// absent from the open-edge set once B-rep is on. Writes tests/artifacts/brep_measure.txt.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class BRepBoundaryDiag
{
    private readonly ITestOutputHelper _out;

    public BRepBoundaryDiag(ITestOutputHelper output) => _out = output;

    private static readonly string[] Levels =
    {
        "dm01.rfl", "dm02.rfl", "dm04.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
        "ctf01.rfl", "ctf02.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl",
        "ctf07.rfl", "dmwarzoneclassicb1.rfl",
    };

    [Fact]
    public void Measure_BRep_Vs_Incremental()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("B-rep cap re-cut (brep) + partition-clip (part) vs incremental default (inc).");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail non-liquid open edges.");
        sb.AppendLine($"{"level",-24} | {"inc",4} {"brep",4} {"part",4} | {"incF",7} {"partF",7} | {"rooms",9} {"port",4} | {"incMs",6} {"partMs",6} {"capCuts",7}");
        sb.AppendLine(new string('-', 100));

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                (List<Brush> brs, List<RoomEffect> eff) = Load(path);
                if (brs.Count == 0)
                {
                    continue;
                }

                (int h, int rooms, int sub, int port, long ms, int faces, int capCuts) Run(CompileOptions o)
                {
                    var sw = Stopwatch.StartNew();
                    CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                    sw.Stop();
                    return (HoleDetector.Detect(c.Geometry).Count, c.Report.Rooms, c.Report.Subrooms,
                        c.Report.Portals, sw.ElapsedMilliseconds, c.Geometry.Faces.Count, c.Report.BRepCapCuts);
                }

                var inc = Run(new CompileOptions { BuildSurfaces = false });
                var brep = Run(new CompileOptions { BuildSurfaces = false, BRepBoundary = true });
                var part = Run(new CompileOptions { BuildSurfaces = false, PartitionClip = true });

                sb.AppendLine($"{name,-24} | {inc.h,4} {brep.h,4} {part.h,4} | {inc.faces,7} {part.faces,7} | " +
                    $"{$"{part.rooms}({part.sub})",9} {part.port,4} | {inc.ms,6} {part.ms,6} {part.capCuts,7}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-24} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Stub-impossibility proof (dm04): the flagship-12/13 16 mm no-partner edge must vanish under B-rep.
        sb.AppendLine();
        string dm04 = Path.Combine(Corpus.Directory!, "dm04.rfl");
        if (File.Exists(dm04))
        {
            (List<Brush> brs, List<RoomEffect> eff) = Load(dm04);
            var pa = new Vec3(-37.742f, -65.162f, -9.939f);
            var pb = new Vec3(-37.726f, -65.159f, -9.936f);
            foreach ((string label, CompileOptions o) in new[]
            {
                ("inc", new CompileOptions { BuildSurfaces = false }),
                ("brep", new CompileOptions { BuildSurfaces = false, BRepBoundary = true }),
                ("part", new CompileOptions { BuildSurfaces = false, PartitionClip = true }),
            })
            {
                CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                bool present = StubPresent(c.Geometry, pa, pb, out int total);
                sb.AppendLine($"dm04 stub ({label}): open-edges={total} stubPresent={present}");
                DumpStubNeighbourhood(sb, c.Geometry, pa, pb);
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("brep_measure.txt", report);
    }

    /// <summary>True when an open edge exists whose endpoints match the stub within 1 mm (either order).</summary>
    private static bool StubPresent(Geometry g, Vec3 pa, Vec3 pb, out int totalOpen)
    {
        var count = new Dictionary<(int, int), int>();
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            if (f.Texture < 0 || f.PortalIndexPlus2 >= 2)
            {
                continue;
            }

            if (((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
            {
                continue;
            }

            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int b = f.Vertices[(i + 1) % n].Index;
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        }

        totalOpen = 0;
        bool found = false;
        const float Tol = 1e-3f;
        foreach ((var key, int c) in count)
        {
            if (c != 1)
            {
                continue;
            }

            totalOpen++;
            Vec3 va = g.Vertices[key.Item1], vb = g.Vertices[key.Item2];
            bool m1 = va.Distance(pa) < Tol && vb.Distance(pb) < Tol;
            bool m2 = va.Distance(pb) < Tol && vb.Distance(pa) < Tol;
            if (m1 || m2)
            {
                found = true;
            }
        }

        return found;
    }

    /// <summary>Dumps every face carrying a vertex within 5 mm of either stub endpoint (plane normal, how many
    /// of its own vertices land near Xa / Xb), so we can see whether the matching cap fragment survived.</summary>
    private static void DumpStubNeighbourhood(StringBuilder sb, Geometry g, Vec3 pa, Vec3 pb)
    {
        const float R = 5e-3f;
        int shown = 0;
        for (int fi = 0; fi < g.Faces.Count && shown < 14; fi++)
        {
            Face f = g.Faces[fi];
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            int nearA = 0, nearB = 0;
            foreach (FaceVertex v in f.Vertices)
            {
                Vec3 p = g.Vertices[v.Index];
                if (p.Distance(pa) < R)
                {
                    nearA++;
                }

                if (p.Distance(pb) < R)
                {
                    nearB++;
                }
            }

            if (nearA == 0 && nearB == 0)
            {
                continue;
            }

            Vec3 nrm = f.Plane.Normal;
            sb.AppendLine($"    face{fi} tex={f.Texture} flags=0x{f.Flags:X} n=({nrm.X:F3},{nrm.Y:F3},{nrm.Z:F3}) " +
                $"verts={f.Vertices.Count} nearXa={nearA} nearXb={nearB}");
            shown++;
        }
    }

    private static (List<Brush>, List<RoomEffect>) Load(string path)
    {
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        return (bs?.Brushes.ToList() ?? new List<Brush>(), es?.Effects.ToList() ?? new List<RoomEffect>());
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
