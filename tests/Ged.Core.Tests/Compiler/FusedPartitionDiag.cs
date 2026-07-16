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
/// DIAGNOSTIC (flagship 18 — THE FUSION): measures the fused-partition path
/// (<see cref="CompileOptions.FusedPartition"/>) against the shipping incremental default across the whole
/// corpus. Runs the FIRST PROOFS: (a) the dm04 16 mm Xa->Xb stub, (b) dm06 stays 0 (the flagship-5 storm
/// canary), (c) dm04/dmabrupt to literal 0. Writes tests/artifacts/fused_partition_*.txt.
/// </summary>
public sealed class FusedPartitionDiag
{
    private readonly ITestOutputHelper _out;

    public FusedPartitionDiag(ITestOutputHelper output) => _out = output;

    // The fused path is a MEASURED NET-NEGATIVE (flagship 18) and pathologically slow on some organic levels
    // (geddmabrupt ~91 s, dmabrupt ~20 s under the full global-partition routing + Chebyshev classification), so
    // the heavy corpus measurements are opt-in (GED_FUSED_MEASURE=1) — they pin the finding for reproducibility
    // without bloating the default suite. The cheap storm-canary invariant below always runs.
    private static bool Measure => Environment.GetEnvironmentVariable("GED_FUSED_MEASURE") == "1";

    /// <summary>Cheap always-on invariant: the fused path runs (does not fall back) AND holds the flagship-5
    /// storm canary dm06 = 0 (proof c) — the one first-proof the construction cleanly achieves. A regression that
    /// broke dm06 or the fused dispatch fails here without running the pathological levels.</summary>
    [Fact]
    public void Fused_Storm_Canary_Holds()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string dm06 = Path.Combine(Corpus.Directory!, "dm06.rfl");
        if (!File.Exists(dm06))
        {
            return;
        }

        (List<Brush> brs, List<RoomEffect> eff) = Load(dm06);
        CompiledLevel c = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, FusedPartition = true });
        Assert.True(c.Report.FusedPartitionUsed, "dm06: fused path fell back instead of running");
        Assert.Empty(OpenEdges(c.Geometry));
    }

    [Fact]
    public void FullCorpus_Fused_Vs_Inc()
    {
        if (!Corpus.Available || !Measure)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"FULL CORPUS inc vs fused holes. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("holes = non-detail non-liquid open edges. faces/ms for inc and fused.");
        sb.AppendLine($"{"level",-30} {"br",5} {"inc",5} {"fused",5} {"d",5} {"incF",7} {"fusF",7} {"incMs",6} {"fusMs",6} {"fbk",4}");
        int regr = 0, impr = 0;
        var regressed = new List<string>();
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".autosave.rfl", StringComparison.OrdinalIgnoreCase))
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

                (int h, int f, long ms, bool used) Run(CompileOptions o)
                {
                    var sw = Stopwatch.StartNew();
                    CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                    sw.Stop();
                    return (OpenEdges(c.Geometry).Count, c.Geometry.Faces.Count, sw.ElapsedMilliseconds, c.Report.FusedPartitionUsed);
                }

                var inc = Run(new CompileOptions { BuildSurfaces = false });
                var fus = Run(new CompileOptions { BuildSurfaces = false, FusedPartition = true });
                int d = fus.h - inc.h;
                if (d > 0) { regr++; regressed.Add($"{name} {inc.h}->{fus.h}"); }
                else if (d < 0) { impr++; }

                sb.AppendLine($"{name,-30} {brs.Count,5} {inc.h,5} {fus.h,5} {d,5} {inc.f,7} {fus.f,7} {inc.ms,6} {fus.ms,6} {(fus.used ? "" : "FBK"),4}"
                    + (d > 0 ? "  <== FUSED REGRESSION" : d < 0 ? "  <== fused better" : string.Empty));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-30} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"FUSED vs inc: improvements={impr} regressions={regr}");
        foreach (string r in regressed)
        {
            sb.AppendLine($"  FUSED REGRESSED {r}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("fused_partition_measure.txt", report);
    }

    [Fact]
    public void Proofs_Stub_Storm_Named()
    {
        if (!Corpus.Available || !Measure)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"FIRST PROOFS (flagship 18). generated {DateTime.Now:yyyy-MM-dd HH:mm}");

        // Proof (a): dm04 16 mm Xa->Xb stub. (b): dm04 -> 0 (Goober's level).
        string dm04 = Path.Combine(Corpus.Directory!, "dm04.rfl");
        if (File.Exists(dm04))
        {
            (List<Brush> brs, List<RoomEffect> eff) = Load(dm04);
            var pa = new Vec3(-37.742f, -65.162f, -9.939f);
            var pb = new Vec3(-37.726f, -65.159f, -9.936f);
            sb.AppendLine();
            sb.AppendLine("PROOF (a)/(c) dm04 stub + count:");
            foreach ((string label, CompileOptions o) in Variants())
            {
                CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                bool present = StubPresent(c.Geometry, pa, pb, out int total);
                sb.AppendLine($"  {label,-6}: open-edges={total} stubPresent={present}");
            }
        }

        // Proof (b): dm06 storm canary (must stay 0). Plus dmabrupt (Goober's named level).
        sb.AppendLine();
        sb.AppendLine("PROOF (b) storm canary + named levels:");
        foreach (string name in new[] { "dm06.rfl", "dmabruptdecayrc2a27.rfl", "ctf01.rfl", "ctf02.rfl", "ctfwlpro.rfl", "dmwarzoneclassicb1.rfl", "ctf07.rfl" })
        {
            string p = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(p))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(p);
            sb.Append($"  {name,-26}");
            foreach ((string label, CompileOptions o) in Variants())
            {
                sb.Append($" {label}={OpenEdges(GeometryCompiler.Compile(brs, eff, o).Geometry).Count}");
            }

            sb.AppendLine();
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("fused_partition_proofs.txt", report);
    }

    [Fact]
    public void Rooms_Portals()
    {
        if (!Corpus.Available || !Measure)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"rooms(sub)/portals inc vs fused. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{"level",-24} | {"inc rooms(sub)/port",22} | {"fused rooms(sub)/port",22}");
        foreach (string name in new[] { "dm04.rfl", "dm06.rfl", "ctf01.rfl", "ctf07.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl", "dmwarzoneclassicb1.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            var i = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false }).Report;
            var g = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, FusedPartition = true }).Report;
            sb.AppendLine($"{name,-24} | {$"{i.Rooms}({i.Subrooms})/{i.Portals}",22} | {$"{g.Rooms}({g.Subrooms})/{g.Portals}",22}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("fused_partition_rooms.txt", report);
    }

    [Fact]
    public void Seal_Tolerance_Sweep()
    {
        if (!Corpus.Available || !Measure)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Fused seal-tolerance sweep. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("holes = non-detail non-liquid open edges. sealOff = FixTJoints on but weld disabled.");
        sb.AppendLine($"{"level",-24} {"3e-3(cur)",9} {"1e-3",6} {"1e-4(RED)",9} {"sealOff",8}");
        foreach (string name in new[]
        {
            "dm04.rfl", "ctf01.rfl", "ctf02.rfl", "ctf04.rfl", "ctf07.rfl", "ctfwlpro.rfl",
            "dmabruptdecayrc2a27.rfl", "dm06.rfl", "dm07.rfl", "dmwarzoneclassicb1.rfl",
        })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            int H(float? tol) => OpenEdges(GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, FusedPartition = true, SealTolerance = tol }).Geometry).Count;
            sb.AppendLine($"{name,-24} {H(3e-3f),9} {H(1e-3f),6} {H(1e-4f),9} {H(0f),8}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("fused_partition_seal_sweep.txt", report);
    }

    [Fact]
    public void Categorise_Fused_Open_Edges()
    {
        if (!Corpus.Available || !Measure)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Fused open-edge categorisation. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("TJOINT=<0.5mm collinear partner; SLIVER=<50mm parallel-offset partner; OTHER=no partner.");
        foreach (string name in new[] { "dm04.rfl", "dmabruptdecayrc2a27.rfl", "ctf01.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            Geometry g = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, FusedPartition = true }).Geometry;
            var count = new Dictionary<(int, int), int>();
            var owner = new Dictionary<(int, int), int>();
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Texture < 0 || f.PortalIndexPlus2 >= 2 ||
                    ((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
                {
                    continue;
                }

                int n = f.Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    int a = f.Vertices[i].Index, b = f.Vertices[(i + 1) % n].Index;
                    if (a == b) { continue; }
                    var key = a < b ? (a, b) : (b, a);
                    count[key] = count.GetValueOrDefault(key) + 1;
                    if (!owner.ContainsKey(key)) { owner[key] = fi; }
                }
            }

            int tj = 0, sl = 0, ot = 0;
            var lenBuckets = new int[5]; // <1mm,<1cm,<10cm,<1m,>=1m
            foreach ((var key, int c) in count)
            {
                if (c != 1) { continue; }
                Vec3 pa = g.Vertices[key.Item1], pb = g.Vertices[key.Item2];
                Vec3 dir = pb.Sub(pa);
                float len = dir.Length();
                lenBuckets[len < 1e-3f ? 0 : len < 1e-2f ? 1 : len < 0.1f ? 2 : len < 1f ? 3 : 4]++;
                Vec3 mid = Vec3Math.Lerp(pa, pb, 0.5f);
                float bestPerp = float.MaxValue;
                for (int pf = 0; pf < g.Faces.Count; pf++)
                {
                    if (pf == owner[key]) { continue; }
                    Face f2 = g.Faces[pf];
                    if (f2.Texture < 0 || f2.Vertices.Count < 3 || f2.PortalIndexPlus2 >= 2) { continue; }
                    int n2 = f2.Vertices.Count;
                    for (int j = 0; j < n2; j++)
                    {
                        Vec3 qa = g.Vertices[f2.Vertices[j].Index], qb = g.Vertices[f2.Vertices[(j + 1) % n2].Index];
                        Vec3 qd = qb.Sub(qa);
                        float qlen = qd.Length();
                        if (qlen < 1e-4f) { continue; }
                        if (MathF.Abs(dir.Dot(qd) / (len * qlen)) < 0.999f) { continue; }
                        Vec3 w = mid.Sub(qa);
                        float t = w.Dot(qd) / (qlen * qlen);
                        float perp = qa.Add(qd.Scale(t)).Distance(mid);
                        if (perp > 0.05f) { continue; }
                        float tb0 = qa.Sub(pa).Dot(dir) / len, tb1 = qb.Sub(pa).Dot(dir) / len;
                        if (tb0 > tb1) { (tb0, tb1) = (tb1, tb0); }
                        if (MathF.Min(len, tb1) - MathF.Max(0f, tb0) <= 0.001f) { continue; }
                        if (perp < bestPerp) { bestPerp = perp; }
                    }
                }

                if (bestPerp < 0.5e-3f) { tj++; }
                else if (bestPerp < 0.05f) { sl++; }
                else { ot++; }
            }

            sb.AppendLine($"{name,-24} open={tj + sl + ot} TJOINT={tj} SLIVER={sl} OTHER={ot}  len[<1mm,<1cm,<10cm,<1m,>=1m]=[{string.Join(",", lenBuckets)}]");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("fused_partition_categorise.txt", report);
    }

    private static IEnumerable<(string, CompileOptions)> Variants()
    {
        yield return ("inc", new CompileOptions { BuildSurfaces = false });
        yield return ("fused", new CompileOptions { BuildSurfaces = false, FusedPartition = true });
    }

    private static bool StubPresent(Geometry g, Vec3 pa, Vec3 pb, out int totalOpen)
    {
        var count = new Dictionary<(int, int), int>();
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || f.PortalIndexPlus2 >= 2 ||
                ((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
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
            if ((va.Distance(pa) < Tol && vb.Distance(pb) < Tol) || (va.Distance(pb) < Tol && vb.Distance(pa) < Tol))
            {
                found = true;
            }
        }

        return found;
    }

    private static List<(Vec3 A, Vec3 B, Vec3 N)> OpenEdges(Geometry g)
    {
        var count = new Dictionary<(int, int), int>();
        var owner = new Dictionary<(int, int), int>();
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            if (f.Texture < 0 || f.PortalIndexPlus2 >= 2 ||
                ((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
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

        var result = new List<(Vec3, Vec3, Vec3)>();
        foreach ((var key, int c) in count)
        {
            if (c == 1)
            {
                result.Add((g.Vertices[key.Item1], g.Vertices[key.Item2], g.Faces[owner[key]].Plane.Normal));
            }
        }

        return result;
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
        File.WriteAllText(Path.Combine(outDir, file.Replace("~", string.Empty)), content);
    }
}
