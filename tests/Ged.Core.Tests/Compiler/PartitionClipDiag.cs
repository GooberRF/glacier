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
/// DIAGNOSTIC (flagship 15): diffs the open-edge set between two compile option variants, listing the edges
/// CLOSED by the variant (gains) and OPENED by the variant (regressions) with position + owner face normal,
/// so the per-cap effect of a construction can be seen edge-by-edge. Writes tests/artifacts/partition_clip_diag.txt.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class PartitionClipDiag
{
    private readonly ITestOutputHelper _out;

    public PartitionClipDiag(ITestOutputHelper output) => _out = output;

    private static readonly string[] Levels =
    {
        "dm04.rfl", "ctf02.rfl", "ctfwlpro.rfl", "ctf07.rfl", "ctf01.rfl", "dmabruptdecayrc2a27.rfl",
        "dmwarzoneclassicb1.rfl", "ctf04.rfl", "ctfstockintradeb1.rfl", "dmedgeofdespairb1a1.rfl",
    };

    /// <summary>Pins the flagship-15 win: on every GATE level PartitionClip is equal-or-better than the
    /// incremental default, the watertight zeros hold, and the named improvements are present. A future
    /// regression on a gate level (or a lost win) fails here; the two known NON-gate community regressions
    /// (ctf04, ctfstockintrade) are documented in the parity notes and deliberately not gated.
    /// <para>Flagship-30 RE-MEASURE: PartitionClip runs INSIDE the incremental fold, so it now composes with the
    /// EdgeLerpSplit (flagship 19) and RegionWiseCoincidence (flagship 23B) DEFAULTS. That composition strictly
    /// improved the flag's floors: ctf01 6→3, ctf07 58→42, dm04 10→9 (cluster A already closed by region-wise).
    /// Ceilings retightened to the re-measured floors (tests/artifacts/dm04_seam_campaign.txt). The flag stays
    /// OFF — still blocked from a default flip by the SAME ctf04 (+1) / ctfstockintrade (+3) cap-only-cut
    /// regressions (partition_clip_diag_partclip.txt: the chord PLANE cut extends ~2 mm past the recorded chord
    /// into a floor the neighbour face is NOT cut at, and EdgeLerp's shared-vertex identity cannot pair an edge
    /// that exists on only one face — its dual GlobalPartition cuts the world too but regresses dm04/ctf01/dm07
    /// instead; neither dominates, the per-brush-vs-global asymmetry) — AND by a flagship-30 NEW finding: part
    /// disturbs ctf07's RED-exact room/portal graph (158/97 → 154/93 — the 32 closed portal-adjacent seams weld
    /// doorway membranes and the room flood merges through; Dm04SeamCampaignDiag.PartitionClip_RoomsPortals_Compare).
    /// This gate holds HOLES only; rooms are the report-only compare.</para></summary>
    [Fact]
    public void PartitionClip_GateLevels_EqualOrBetter()
    {
        if (!Corpus.Available)
        {
            return;
        }

        // gate level -> partMax (re-measured floor). part must be <= inc and <= partMax.
        var gates = new (string Level, int PartCeil)[]
        {
            ("dm01.rfl", 0), ("dm02.rfl", 0), ("dm04.rfl", 9), ("dm05.rfl", 0), ("dm06.rfl", 0),
            ("glass_house.rfl", 0), ("ctf01.rfl", 3), ("ctf02.rfl", 0), ("ctfwlpro.rfl", 20),
            ("dmabruptdecayrc2a27.rfl", 6), ("kothcowb1~.rfl", 0), ("ctf07.rfl", 42),
            ("dmwarzoneclassicb1.rfl", 17),
        };

        foreach ((string level, int ceil) in gates)
        {
            string path = Path.Combine(Corpus.Directory!, level);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            int inc = OpenEdges(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = false }).Geometry).Count;
            int part = OpenEdges(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = false, PartitionClip = true }).Geometry).Count;
            Assert.True(part <= inc, $"{level}: PartitionClip {part} regressed vs inc {inc}");
            Assert.True(part <= ceil, $"{level}: PartitionClip {part} exceeds flagship-15 floor {ceil}");
        }
    }

    [Fact]
    public void FullCorpus_PartitionClip_Vs_Inc()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"FULL CORPUS inc vs partclip holes. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{"level",-30} {"brushes",7} {"inc",5} {"part",5} {"delta",6}");
        int regressions = 0, improvements = 0;
        var regressed = new List<string>();
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

                int inc = OpenEdges(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = false }).Geometry).Count;
                int part = OpenEdges(GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = false, PartitionClip = true }).Geometry).Count;
                int d = part - inc;
                if (d > 0)
                {
                    regressions++;
                    regressed.Add($"{name} {inc}->{part}");
                }
                else if (d < 0)
                {
                    improvements++;
                }

                sb.AppendLine($"{name,-30} {brs.Count,7} {inc,5} {part,5} {d,6}{(d > 0 ? "  <== REGRESSION" : string.Empty)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-30} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"improvements={improvements} regressions={regressions}");
        foreach (string r in regressed)
        {
            sb.AppendLine($"  REGRESSED {r}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("partition_clip_fullcorpus.txt", report);
    }

    [Fact]
    public void Diff_Brep_Vs_Inc()
    {
        Run("brep", o => o.BRepBoundary = true);
    }

    [Fact]
    public void Diff_PartitionClip_Vs_Inc()
    {
        Run("partclip", o => o.PartitionClip = true);
    }

    private void Run(string tag, Action<CompileOptions> apply)
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"open-edge diff: {tag} vs incremental default. generated {DateTime.Now:yyyy-MM-dd HH:mm}");

        foreach (string name in Levels)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            if (brs.Count == 0)
            {
                continue;
            }

            List<(Vec3 A, Vec3 B, Vec3 N)> baseOpen = OpenEdges(GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, SharedBsp = false }).Geometry);
            var vo = new CompileOptions { BuildSurfaces = false, SharedBsp = false };
            apply(vo);
            List<(Vec3 A, Vec3 B, Vec3 N)> varOpen = OpenEdges(GeometryCompiler.Compile(brs, eff, vo).Geometry);

            var baseKeys = baseOpen.Select(Key).ToHashSet();
            var varKeys = varOpen.Select(Key).ToHashSet();
            var closed = baseOpen.Where(e => !varKeys.Contains(Key(e))).ToList();
            var opened = varOpen.Where(e => !baseKeys.Contains(Key(e))).ToList();

            sb.AppendLine();
            sb.AppendLine($"=== {name}: inc={baseOpen.Count} {tag}={varOpen.Count}  (closed {closed.Count}, opened {opened.Count}) ===");
            foreach ((Vec3 a, Vec3 b, Vec3 n) in opened)
            {
                sb.AppendLine($"  OPENED ({a.X:F3},{a.Y:F3},{a.Z:F3})->({b.X:F3},{b.Y:F3},{b.Z:F3}) len={a.Distance(b) * 1000:F1}mm n=({n.X:F3},{n.Y:F3},{n.Z:F3})");
            }

            foreach ((Vec3 a, Vec3 b, Vec3 n) in closed.Take(24))
            {
                sb.AppendLine($"  closed ({a.X:F3},{a.Y:F3},{a.Z:F3})->({b.X:F3},{b.Y:F3},{b.Z:F3}) len={a.Distance(b) * 1000:F1}mm n=({n.X:F3},{n.Y:F3},{n.Z:F3})");
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact($"partition_clip_diag_{tag}.txt", report);
    }

    private static (int, int, int, int, int, int) Key((Vec3 A, Vec3 B, Vec3 N) e)
    {
        static (int, int, int) Q(Vec3 p) => ((int)MathF.Round(p.X * 500f), (int)MathF.Round(p.Y * 500f), (int)MathF.Round(p.Z * 500f));
        (int, int, int) a = Q(e.A);
        (int, int, int) b = Q(e.B);
        // order-independent
        if (a.CompareTo(b) > 0)
        {
            (a, b) = (b, a);
        }

        return (a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3);
    }

    private static List<(Vec3 A, Vec3 B, Vec3 N)> OpenEdges(Geometry g)
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
