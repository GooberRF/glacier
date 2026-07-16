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
/// DIAGNOSTIC (flagship 16 — the convergence pass): measures THE GLOBAL ACCUMULATED PARTITION
/// (<see cref="CompileOptions.GlobalPartition"/>) against the shipping incremental default and the flagship-15
/// PartitionClip, on the full corpus. Runs the three FIRST PROOFS: (a) the dm04 16 mm stub, (b) the
/// ctf04/ctfstockintrade community regressions PartitionClip opened, (c) dm06 stays 0 (the flagship-5 storm
/// canary). Writes tests/artifacts/global_partition_measure.txt.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class GlobalPartitionDiag
{
    private readonly ITestOutputHelper _out;

    public GlobalPartitionDiag(ITestOutputHelper output) => _out = output;

    private static readonly string[] GateLevels =
    {
        "dm01.rfl", "dm02.rfl", "dm04.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
        "ctf01.rfl", "ctf02.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl",
        "ctf07.rfl", "dmwarzoneclassicb1.rfl", "ctf04.rfl", "ctfstockintradeb1.rfl",
    };

    /// <summary>Pins the flagship-16 result: on every measured level GlobalPartition holds at or below its
    /// achieved floor, the watertight zeros hold, the storm canary (dm06) stays 0 (proof c), and the two
    /// community levels PartitionClip regressed do NOT regress under GlobalPartition (proof b — in fact both
    /// improve). A future regression fails here. Proof (a) — the dm04 stub — is NOT closed (extent divergence,
    /// documented in the parity notes) and is deliberately not asserted absent.</summary>
    [Fact]
    public void GlobalPartition_Floors_And_Proofs()
    {
        if (!Corpus.Available)
        {
            return;
        }

        // level -> GlobalPartition ceiling (measured floor; zeros are hard). Proof (b)/(c) live in here too:
        // dm06 = 0 (storm canary), ctf04 <= 13 and ctfstockintrade <= 3 (the PartitionClip community regressions
        // must not recur — measured 10 and 3, headroom to the inc floor 13 / 3).
        var ceilings = new (string Level, int Ceil)[]
        {
            ("dm01.rfl", 0), ("dm02.rfl", 0), ("dm04.rfl", 14), ("dm05.rfl", 0), ("dm06.rfl", 0),
            ("glass_house.rfl", 0), ("ctf01.rfl", 17), ("ctf02.rfl", 5), ("ctfwlpro.rfl", 6),
            ("dmabruptdecayrc2a27.rfl", 5), ("kothcowb1~.rfl", 0), ("ctf07.rfl", 58),
            ("dmwarzoneclassicb1.rfl", 17), ("dm07.rfl", 20), ("dm08.rfl", 3),
            ("ctf04.rfl", 13), ("ctfstockintradeb1.rfl", 3), ("dmedgeofdespairb1a1.rfl", 0),
        };

        foreach ((string level, int ceil) in ceilings)
        {
            string path = Path.Combine(Corpus.Directory!, level);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            int glob = OpenEdges(GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, SharedBsp = false, GlobalPartition = true }).Geometry).Count;
            Assert.True(glob <= ceil, $"{level}: GlobalPartition {glob} exceeds flagship-16 floor {ceil}");
        }
    }

    /// <summary>
    /// Census-predicted sealer-tightening sweep (flagship 16, coordinator directive): RED's t-joint fixer runs
    /// at 1e-4 to a fixpoint with NO late weld/snap; GED's SeamSealer runs at 3 mm on the fold path as
    /// compensation for per-brush station divergence. Measures GlobalPartition holes with the sealer OFF /
    /// 1e-4 (RED's) / 1e-3 / 3e-3 (current) to see whether the partition's cap-side station coincidence lets
    /// the compensation be tightened or retired. Writes global_partition_seal_sweep.txt.
    /// </summary>
    [Fact]
    public void Seal_Tolerance_Sweep_On_GlobalPartition()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"GlobalPartition seal-tolerance sweep. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
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
            int H(float? tol, bool fix = true) => OpenEdges(GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, SharedBsp = false, GlobalPartition = true, FixTJoints = fix, SealTolerance = tol }).Geometry).Count;
            sb.AppendLine($"{name,-24} {H(3e-3f),9} {H(1e-3f),6} {H(1e-4f),9} {H(0f),8}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("global_partition_seal_sweep.txt", report);
    }

    [Fact]
    public void FullCorpus_Global_Vs_Inc_Vs_Part()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"FULL CORPUS inc vs partclip vs globalpartition holes. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"holes = non-detail non-liquid open edges. faces/ms for inc and glob.");
        sb.AppendLine($"{"level",-30} {"br",5} {"inc",5} {"part",5} {"glob",5} {"dPart",6} {"dGlob",6} {"incF",7} {"globF",7} {"incMs",6} {"globMs",6}");
        int gRegr = 0, gImpr = 0, pRegr = 0, pImpr = 0;
        var gRegressed = new List<string>();
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            try
            {
                (List<Brush> brs, List<RoomEffect> eff) = Load(path);
                if (brs.Count == 0)
                {
                    continue;
                }

                (int h, int f, long ms) Run(CompileOptions o)
                {
                    var sw = Stopwatch.StartNew();
                    CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                    sw.Stop();
                    return (OpenEdges(c.Geometry).Count, c.Geometry.Faces.Count, sw.ElapsedMilliseconds);
                }

                var inc = Run(new CompileOptions { BuildSurfaces = false, SharedBsp = false });
                var part = Run(new CompileOptions { BuildSurfaces = false, SharedBsp = false, PartitionClip = true });
                var glob = Run(new CompileOptions { BuildSurfaces = false, SharedBsp = false, GlobalPartition = true });
                int dPart = part.h - inc.h;
                int dGlob = glob.h - inc.h;
                if (dGlob > 0) { gRegr++; gRegressed.Add($"{name} {inc.h}->{glob.h}"); }
                else if (dGlob < 0) { gImpr++; }
                if (dPart > 0) { pRegr++; }
                else if (dPart < 0) { pImpr++; }

                sb.AppendLine($"{name,-30} {brs.Count,5} {inc.h,5} {part.h,5} {glob.h,5} {dPart,6} {dGlob,6} {inc.f,7} {glob.f,7} {inc.ms,6} {glob.ms,6}"
                    + (dGlob > 0 ? "  <== GLOB REGRESSION" : string.Empty));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-30} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"GLOBAL vs inc: improvements={gImpr} regressions={gRegr}");
        sb.AppendLine($"PART   vs inc: improvements={pImpr} regressions={pRegr}");
        foreach (string r in gRegressed)
        {
            sb.AppendLine($"  GLOB REGRESSED {r}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("global_partition_measure.txt", report);
    }

    /// <summary>Reports rooms(sub)/portals for inc vs glob on the gate levels and asserts dm04 is held
    /// (24(15)/10 — glob = inc there, so the room graph must not shift). Writes global_partition_rooms.txt.</summary>
    [Fact]
    public void Rooms_Portals_Held()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"rooms(sub)/portals inc vs glob. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{"level",-24} | {"inc rooms(sub)/port",22} | {"glob rooms(sub)/port",22}");
        foreach (string name in new[] { "dm04.rfl", "ctf01.rfl", "ctf04.rfl", "ctf07.rfl", "ctfwlpro.rfl", "dmwarzoneclassicb1.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            var i = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = false }).Report;
            var g = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = false, GlobalPartition = true }).Report;
            sb.AppendLine($"{name,-24} | {$"{i.Rooms}({i.Subrooms})/{i.Portals}",22} | {$"{g.Rooms}({g.Subrooms})/{g.Portals}",22}");
            if (name == "dm04.rfl")
            {
                Assert.Equal(i.Rooms, g.Rooms);
                Assert.Equal(i.Portals, g.Portals);
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("global_partition_rooms.txt", report);
    }

    [Fact]
    public void Proofs_Stub_And_Community_And_Storm()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"FIRST PROOFS (flagship 16). generated {DateTime.Now:yyyy-MM-dd HH:mm}");

        // Proof (a): dm04 16 mm Xa->Xb stub.
        string dm04 = Path.Combine(Corpus.Directory!, "dm04.rfl");
        if (File.Exists(dm04))
        {
            (List<Brush> brs, List<RoomEffect> eff) = Load(dm04);
            var pa = new Vec3(-37.742f, -65.162f, -9.939f);
            var pb = new Vec3(-37.726f, -65.159f, -9.936f);
            sb.AppendLine();
            sb.AppendLine("PROOF (a) dm04 stub:");
            foreach ((string label, CompileOptions o) in Variants())
            {
                CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
                bool present = StubPresent(c.Geometry, pa, pb, out int total);
                sb.AppendLine($"  {label,-6}: open-edges={total} stubPresent={present}");
            }
        }

        // Proof (b): ctf04 <= 13, ctfstockintrade <= 3 (the PartitionClip community regressions must not occur).
        sb.AppendLine();
        sb.AppendLine("PROOF (b) community levels (PartitionClip regressed these):");
        foreach (string name in new[] { "ctf04.rfl", "ctfstockintradeb1.rfl" })
        {
            string p = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(p))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(p);
            sb.Append($"  {name,-24}");
            foreach ((string label, CompileOptions o) in Variants())
            {
                sb.Append($" {label}={OpenEdges(GeometryCompiler.Compile(brs, eff, o).Geometry).Count}");
            }

            sb.AppendLine();
        }

        // Proof (c): dm06 storm canary.
        sb.AppendLine();
        sb.AppendLine("PROOF (c) dm06 storm canary (must stay 0):");
        string dm06 = Path.Combine(Corpus.Directory!, "dm06.rfl");
        if (File.Exists(dm06))
        {
            (List<Brush> brs, List<RoomEffect> eff) = Load(dm06);
            sb.Append("  dm06.rfl                ");
            foreach ((string label, CompileOptions o) in Variants())
            {
                sb.Append($" {label}={OpenEdges(GeometryCompiler.Compile(brs, eff, o).Geometry).Count}");
            }

            sb.AppendLine();
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("global_partition_proofs.txt", report);
    }

    private static IEnumerable<(string, CompileOptions)> Variants()
    {
        yield return ("inc", new CompileOptions { BuildSurfaces = false, SharedBsp = false });
        yield return ("part", new CompileOptions { BuildSurfaces = false, SharedBsp = false, PartitionClip = true });
        yield return ("glob", new CompileOptions { BuildSurfaces = false, SharedBsp = false, GlobalPartition = true });
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
