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
/// DIAGNOSTIC: measures the on-edge cut arithmetic at construction time (edge-lerp split) + shared
/// vertex identity against the incremental default. Reports holes (HoleDetector), rooms/portals, corner
/// merges, and perf across a corner-merge tolerance sweep, plus the per-cohort open-edge classification on
/// the target terrain levels (dm04 / dmabrupt) and the dm04 trace-scene 0/0 check. Writes
/// tests/artifacts/edge_lerp_measure.txt.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class FoldArithmeticDiag
{
    private readonly ITestOutputHelper _out;

    public FoldArithmeticDiag(ITestOutputHelper output) => _out = output;

    private static readonly string[] Levels =
    {
        "dm01.rfl", "dm02.rfl", "dm04.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
        "dm07.rfl", "dm13.rfl", "dm17.rfl",
        "ctf01.rfl", "ctf02.rfl", "ctf04.rfl", "ctf07.rfl", "ctfwlpro.rfl",
        "dmabruptdecayrc2a27.rfl", "geddmabruptdecayrc2a27.rfl",
        "dmwarzoneclassicb1.rfl", "kothcowb1~.rfl",
    };

    private static readonly float[] Tolerances = { 0f, 1e-3f, 2e-3f, 3e-3f };

    [Fact]
    public void Measure_EdgeLerp_Vs_Incremental()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("EdgeLerpSplit (flagship 19) vs incremental default (inc). holes = non-detail non-liquid open edges.");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. el@T = EdgeLerpSplit at corner-merge tolerance T mm.");
        sb.AppendLine("rooms shown total(sub)/portals for inc | el@2mm. merges = coincident corners collapsed at 2mm.");
        sb.AppendLine();
        string hdr = $"{"level",-26} {"orig",4} {"inc",4}";
        foreach (float t in Tolerances)
        {
            hdr += $" {"el@" + (t * 1000).ToString("0.#"),6}";
        }

        hdr += $" | {"incRP",12} {"elRP",12} | {"incMs",6} {"elMs",6} {"merges",7}";
        sb.AppendLine(hdr);
        sb.AppendLine(new string('-', hdr.Length));

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (!Load(path, out Geometry? orig, out List<Brush> brs, out List<RoomEffect> eff))
                {
                    continue;
                }

                int origHoles = orig is null ? -1 : HoleDetector.Detect(orig).Count;

                (int h, int rooms, int sub, int port, long ms, int merges) Run(CompileOptions o)
                {
                    var sw = Stopwatch.StartNew();
                    CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                    sw.Stop();
                    return (HoleDetector.Detect(c.Geometry).Count, c.Report.Rooms, c.Report.Subrooms,
                        c.Report.Portals, sw.ElapsedMilliseconds, c.Report.EdgeCornerMerges);
                }

                var inc = Run(new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false });
                var line = new StringBuilder($"{name,-26} {origHoles,4} {inc.h,4}");
                (int h, int rooms, int sub, int port, long ms, int merges) el2 = default;
                foreach (float t in Tolerances)
                {
                    var el = Run(new CompileOptions
                    {
                        BuildSurfaces = false,
                        EdgeLerpSplit = true,
                        EdgeMergeTolerance = t,
                    });
                    line.Append($" {el.h,6}");
                    if (Math.Abs(t - 2e-3f) < 1e-9f)
                    {
                        el2 = el;
                    }
                }

                line.Append($" | {$"{inc.rooms}({inc.sub})/{inc.port}",12} {$"{el2.rooms}({el2.sub})/{el2.port}",12}");
                line.Append($" | {inc.ms,6} {el2.ms,6} {el2.merges,7}");
                sb.AppendLine(line.ToString());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-26} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Per-cohort open-edge classification on the target terrain levels (baseline inc vs el@2mm).
        sb.AppendLine();
        sb.AppendLine("Per-cohort open-edge classification (SharedEdgeDiag semantics: TJOINT collinear / SLIVER parallel-offset / OTHER no-partner):");
        foreach (string name in new[] { "dm04.rfl", "dmabruptdecayrc2a27.rfl", "ctf01.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path) || !Load(path, out _, out List<Brush> brs, out List<RoomEffect> eff))
            {
                continue;
            }

            Geometry incG = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false }).Geometry;
            Geometry elG = GeometryCompiler.Compile(brs, eff, new CompileOptions
            {
                BuildSurfaces = false, EdgeLerpSplit = true, EdgeMergeTolerance = 2e-3f,
            }).Geometry;
            sb.AppendLine($"  {name}: inc {Classify(incG)}  ->  el@2mm {Classify(elG)}");
        }

        // dm04 trace-scene 0/0 (the permanent fixture scene) on inc vs el@2mm.
        sb.AppendLine();
        List<Brush>? scene = LoadTraceScene();
        if (scene is not null && scene.Count == 3)
        {
            foreach ((string label, CompileOptions o) in new[]
            {
                ("inc", new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false }),
                ("el@2mm", new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = true, EdgeMergeTolerance = 2e-3f }),
            })
            {
                CompiledLevel c = GeometryCompiler.Compile(scene, null, o);
                int holes = HoleDetector.Detect(c.Geometry).Count;
                List<(int A, int B, float Area)> ov = TraceDm04Diag.FindOverlaps(c.Geometry);
                float maxArea = ov.Count == 0 ? 0f : ov.Max(o2 => o2.Area);
                sb.AppendLine($"  trace {label,-6} faces={c.Geometry.Faces.Count,4} holes={holes,3} overlapPairs={ov.Count,3} maxOverlapArea={maxArea:F5} edgeLerpUsed={c.Report.EdgeLerpSplitUsed}");
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("edge_lerp_measure.txt", report);
    }

    [Fact]
    public void FullCorpus_EqualOrBetter_At_Default_Tolerance()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Full-corpus holes: inc default vs EdgeLerpSplit @ 2mm (the flip gate = equal-or-better on EVERY level).");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}.");
        sb.AppendLine($"{"level",-28} {"orig",4} {"inc",4} {"el2",4} {"delta",5} {"note"}");
        sb.AppendLine(new string('-', 60));
        int better = 0, worse = 0, equal = 0;
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.Contains("autosave", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!Load(path, out Geometry? orig, out List<Brush> brs, out List<RoomEffect> eff))
                {
                    continue;
                }

                int origHoles = orig is null ? -1 : HoleDetector.Detect(orig).Count;
                int incH = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false }).Geometry).Count;
                int elH = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff, new CompileOptions
                {
                    BuildSurfaces = false, EdgeLerpSplit = true,
                }).Geometry).Count;
                int d = elH - incH;
                string note = d < 0 ? "BETTER" : d > 0 ? "WORSE" : "";
                if (d < 0)
                {
                    better++;
                }
                else if (d > 0)
                {
                    worse++;
                }
                else
                {
                    equal++;
                }

                sb.AppendLine($"{name,-28} {origHoles,4} {incH,4} {elH,4} {d,5} {note}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-28} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"summary: BETTER={better} EQUAL={equal} WORSE={worse}");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("edge_lerp_fullcorpus.txt", report);
    }

    [Fact]
    public void Sealer_Sweep_Under_Flag()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("SeamSealer sweep under EdgeLerpSplit @ 2mm (census: does on-edge arithmetic let the tight 1e-4 end-state hold?).");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. columns = seal tolerance mm; inc = incremental default @ its 3mm sealer.");
        float[] seals = { 0f, 1e-4f, 1e-3f, 3e-3f };
        string hdr = $"{"level",-26} {"inc",4}";
        foreach (float s in seals)
        {
            hdr += $" {"seal" + (s * 1000).ToString("0.###"),9}";
        }

        sb.AppendLine(hdr);
        sb.AppendLine(new string('-', hdr.Length));
        foreach (string name in new[] { "dm04.rfl", "dm06.rfl", "ctf01.rfl", "ctf02.rfl", "ctf07.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path) || !Load(path, out _, out List<Brush> brs, out List<RoomEffect> eff))
            {
                continue;
            }

            int incH = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false }).Geometry).Count;
            var line = new StringBuilder($"{name,-26} {incH,4}");
            foreach (float s in seals)
            {
                int h = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff, new CompileOptions
                {
                    BuildSurfaces = false, EdgeLerpSplit = true, EdgeMergeTolerance = 2e-3f, SealTolerance = s,
                }).Geometry).Count;
                line.Append($" {h,9}");
            }

            sb.AppendLine(line.ToString());
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("edge_lerp_seal_sweep.txt", report);
    }

    [Fact]
    public void Regression_Boundary_Probe()
    {
        if (!Corpus.Available)
        {
            return;
        }

        float[] tols = { 0f, 2.5e-4f, 5e-4f, 7.5e-4f, 1e-3f, 1.5e-3f, 2e-3f };
        var sb = new StringBuilder();
        sb.AppendLine("Tolerance probe: find the corner-merge tolerance with benefits held and zero regressions.");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. columns = corner-merge tolerance mm.");
        string hdr = $"{"level",-26} {"inc",4}";
        foreach (float t in tols)
        {
            hdr += $" {(t * 1000).ToString("0.###"),6}";
        }

        sb.AppendLine(hdr);
        sb.AppendLine(new string('-', hdr.Length));
        foreach (string name in new[]
        {
            "ctf05.rfl", "ctf02.rfl", "ctf04.rfl", "ctfstockintradeb1.rfl", "dm08.rfl", "dm15.rfl",
            "dm04.rfl", "ctf01.rfl", "ctf07.rfl", "ctf06.rfl", "dmedgeofdespairb1a1.rfl",
        })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path) || !Load(path, out _, out List<Brush> brs, out List<RoomEffect> eff))
            {
                continue;
            }

            int incH = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false }).Geometry).Count;
            var line = new StringBuilder($"{name,-26} {incH,4}");
            foreach (float t in tols)
            {
                int h = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff, new CompileOptions
                {
                    BuildSurfaces = false, EdgeLerpSplit = true, EdgeMergeTolerance = t,
                }).Geometry).Count;
                line.Append($" {h,6}");
            }

            sb.AppendLine(line.ToString());
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("edge_lerp_tol_probe.txt", report);
    }

    [Fact]
    public void Perf_And_RoomsPortals_At_Default()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Perf (min of 3 solve+build ms) and rooms/portals: inc default vs EdgeLerpSplit @ 1mm default.");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}. ratio = elMs/incMs (perf gate ≤1.1x).");
        sb.AppendLine($"{"level",-26} {"incMs",6} {"elMs",6} {"ratio",6} | {"incRP",13} {"elRP",13} {"merges",7}");
        sb.AppendLine(new string('-', 78));
        foreach (string name in new[]
        {
            "dm04.rfl", "ctf01.rfl", "ctf07.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl",
            "geddmabruptdecayrc2a27.rfl", "kothcowb1~.rfl", "dm06.rfl", "dm17.rfl",
        })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path) || !Load(path, out _, out List<Brush> brs, out List<RoomEffect> eff))
            {
                continue;
            }

            (long ms, int r, int s, int p) Best(CompileOptions o)
            {
                long best = long.MaxValue;
                int r = 0, s = 0, p = 0;
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                    sw.Stop();
                    best = Math.Min(best, sw.ElapsedMilliseconds);
                    r = c.Report.Rooms;
                    s = c.Report.Subrooms;
                    p = c.Report.Portals;
                }

                return (best, r, s, p);
            }

            var inc = Best(new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = false });
            var el = Best(new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = true });
            int merges = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, EdgeLerpSplit = true }).Report.EdgeCornerMerges;
            double ratio = inc.ms == 0 ? 0 : (double)el.ms / inc.ms;
            sb.AppendLine($"{name,-26} {inc.ms,6} {el.ms,6} {ratio,6:F2} | {$"{inc.r}({inc.s})/{inc.p}",13} {$"{el.r}({el.s})/{el.p}",13} {merges,7}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("edge_lerp_perf.txt", report);
    }

    private static string Classify(Geometry g)
    {
        var count = new Dictionary<(int, int), int>();
        var owner = new Dictionary<(int, int), int>();
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
                if (!owner.ContainsKey(key))
                {
                    owner[key] = fi;
                }
            }
        }

        var open = count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        int tjoint = 0, sliver = 0, other = 0;
        foreach ((int ia, int ib) in open)
        {
            Vec3 pa = g.Vertices[ia], pb = g.Vertices[ib];
            Vec3 dir = pb.Sub(pa);
            float len = dir.Length();
            Vec3 mid = Vec3Math.Lerp(pa, pb, 0.5f);
            float bestPerp = float.MaxValue;
            for (int pf = 0; pf < g.Faces.Count; pf++)
            {
                if (pf == owner[(ia, ib)])
                {
                    continue;
                }

                Face f2 = g.Faces[pf];
                if (f2.Texture < 0 || f2.Vertices.Count < 3 || f2.PortalIndexPlus2 >= 2)
                {
                    continue;
                }

                int n2 = f2.Vertices.Count;
                for (int j = 0; j < n2; j++)
                {
                    Vec3 qa = g.Vertices[f2.Vertices[j].Index];
                    Vec3 qb = g.Vertices[f2.Vertices[(j + 1) % n2].Index];
                    Vec3 qd = qb.Sub(qa);
                    float qlen = qd.Length();
                    if (qlen < 1e-4f)
                    {
                        continue;
                    }

                    if (MathF.Abs(dir.Dot(qd) / (len * qlen)) < 0.999f)
                    {
                        continue;
                    }

                    Vec3 w = mid.Sub(qa);
                    float tt = w.Dot(qd) / (qlen * qlen);
                    float perp = qa.Add(qd.Scale(tt)).Distance(mid);
                    if (perp > 0.05f)
                    {
                        continue;
                    }

                    float tb0 = qa.Sub(pa).Dot(dir) / len;
                    float tb1 = qb.Sub(pa).Dot(dir) / len;
                    if (tb0 > tb1)
                    {
                        (tb0, tb1) = (tb1, tb0);
                    }

                    float ov = MathF.Min(len, tb1) - MathF.Max(0f, tb0);
                    if (ov <= 0.001f)
                    {
                        continue;
                    }

                    if (perp < bestPerp)
                    {
                        bestPerp = perp;
                    }
                }
            }

            if (bestPerp < 0.5e-3f)
            {
                tjoint++;
            }
            else if (bestPerp < 0.05f)
            {
                sliver++;
            }
            else
            {
                other++;
            }
        }

        return $"holes={open.Count} [TJOINT={tjoint} SLIVER={sliver} OTHER={other}]";
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

    private static bool Load(string path, out Geometry? orig, out List<Brush> brushes, out List<RoomEffect> effects)
    {
        orig = null;
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? b = null;
        RoomEffectsSection? e = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                orig ??= gs.Geometry;
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
        File.WriteAllText(Path.Combine(outDir, file.Replace("~", string.Empty)), content);
    }
}
