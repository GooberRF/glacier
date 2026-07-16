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
/// FLAGSHIP dm04-seam-closure RE-MEASUREMENT. The prior per-configuration hole numbers (PartitionClip
/// flagship 15, GlobalPartition flagship 16, leaf extraction) predate the EdgeLerpSplit (flagship 19) and
/// RegionWiseCoincidence (flagship 23B) defaults. PartitionClip/BRepBoundary run INSIDE SolveIncremental so
/// they now inherit those; GlobalPartition/LeafExtraction are separate paths. This sweep re-measures the
/// whole landscape under today's machinery: dm04 + full corpus holes under every flag path, plus dm04's
/// per-seam classification under the promising configs. Writes tests/artifacts/dm04_seam_campaign.txt.
/// </summary>
public sealed class Dm04SeamCampaignDiag
{
    private readonly ITestOutputHelper _out;

    public Dm04SeamCampaignDiag(ITestOutputHelper output) => _out = output;

    // Heavy full-corpus × all-paths sweep (leaf extraction on some levels is slow); opt-in like DmWarzoneDiag /
    // FusedPartitionDiag so the default parallel suite stays fast. Set GED_SEAM_MEASURE=1 to regenerate the
    // tests/artifacts/dm04_seam_campaign.txt + dm04_perseam_byconfig.txt tables.
    private static bool MeasureEnabled => Environment.GetEnvironmentVariable("GED_SEAM_MEASURE") == "1";

    // Configs that compose inside the incremental fold (fast, ~incremental speed) or are separate paths.
    private static readonly (string Tag, Func<CompileOptions> Make)[] Configs =
    {
        ("def",  () => new CompileOptions { BuildSurfaces = false }),
        ("part", () => new CompileOptions { BuildSurfaces = false, PartitionClip = true }),
        ("glob", () => new CompileOptions { BuildSurfaces = false, GlobalPartition = true }),
        ("brep", () => new CompileOptions { BuildSurfaces = false, BRepBoundary = true }),
        ("pb",   () => new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = false }),
    };

    // Leaf extraction is slow on some levels; run it on the gate + named + interesting levels only.
    private static readonly string[] LeafLevels =
    {
        "dm01.rfl", "dm02.rfl", "dm04.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
        "ctf01.rfl", "ctf02.rfl", "ctf07.rfl", "ctfwlpro.rfl", "ctf04.rfl", "ctfstockintradeb1.rfl",
        "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl", "dmwarzoneclassicb1.rfl", "dm15.rfl", "dm07.rfl", "dm08.rfl",
    };

    private static bool Canonical(string name) =>
        !name.Contains(".autosave.", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("dmabruptdecayrc2a27~.rfl", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void FullCorpus_All_Paths()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"dm04 seam-campaign RE-MEASURE. generated {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail");
        sb.AppendLine("non-liquid non-portal single-use edges (HoleDetector). def=inc+edgelerp+regionwise (SHIPPING");
        sb.AppendLine("DEFAULT); part=+PartitionClip; glob=+GlobalPartition; brep=+BRepBoundary; pb=per-brush");
        sb.AppendLine("(IncrementalAccumulator=false); leaf=UseLeafExtraction. faces in parens after each hole count.");
        sb.AppendLine();
        sb.AppendLine($"{"level",-28} {"def",10} {"part",10} {"glob",10} {"brep",10} {"pb",10} {"leaf",10}");
        sb.AppendLine(new string('-', 100));

        var totals = new Dictionary<string, (int Better, int Worse, int Zero)>();
        foreach (string tag in new[] { "part", "glob", "brep", "pb", "leaf" })
        {
            totals[tag] = (0, 0, 0);
        }

        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (!Canonical(name))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            if (brs.Count == 0)
            {
                continue;
            }

            var cell = new Dictionary<string, string>();
            int def = -1;
            foreach ((string tag, Func<CompileOptions> make) in Configs)
            {
                (int holes, int faces, _) = Measure(brs, eff, make());
                cell[tag] = $"{holes}({faces})";
                if (tag == "def")
                {
                    def = holes;
                }
                else if (def >= 0)
                {
                    (int b, int w, int z) = totals[tag];
                    if (holes < def)
                    {
                        b++;
                    }
                    else if (holes > def)
                    {
                        w++;
                    }

                    if (holes == 0)
                    {
                        z++;
                    }

                    totals[tag] = (b, w, z);
                }
            }

            string leafCell = "-";
            if (LeafLevels.Contains(name))
            {
                (int lh, int lf, bool lUsed) = Measure(brs, eff, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
                leafCell = lUsed ? $"{lh}({lf})" : $"FB:{lh}";
                if (lUsed && def >= 0)
                {
                    (int b, int w, int z) = totals["leaf"];
                    if (lh < def)
                    {
                        b++;
                    }
                    else if (lh > def)
                    {
                        w++;
                    }

                    if (lh == 0)
                    {
                        z++;
                    }

                    totals["leaf"] = (b, w, z);
                }
            }

            sb.AppendLine($"{name.Replace("~", string.Empty),-28} {cell["def"],10} {cell["part"],10} {cell["glob"],10} {cell["brep"],10} {cell["pb"],10} {leafCell,10}");
        }

        sb.AppendLine();
        sb.AppendLine("vs def (better/worse/zeros; leaf only over its measured subset):");
        foreach ((string tag, (int b, int w, int z)) in totals)
        {
            sb.AppendLine($"  {tag,-5} better={b} worse={w} zeros={z}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("dm04_seam_campaign.txt", report);
    }

    [Fact]
    public void Dm04_PerSeam_ByConfig()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        (List<Brush> brs, List<RoomEffect> eff) = Load(path);
        var sb = new StringBuilder();
        sb.AppendLine($"dm04 PER-SEAM classification by config. generated {DateTime.Now:yyyy-MM-dd HH:mm}");

        var configs = new (string Tag, Func<CompileOptions> Make)[]
        {
            ("def",  () => new CompileOptions { BuildSurfaces = false }),
            ("part", () => new CompileOptions { BuildSurfaces = false, PartitionClip = true }),
            ("glob", () => new CompileOptions { BuildSurfaces = false, GlobalPartition = true }),
            ("brep", () => new CompileOptions { BuildSurfaces = false, BRepBoundary = true }),
            ("leaf", () => new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true }),
        };

        foreach ((string tag, Func<CompileOptions> make) in configs)
        {
            Geometry g = GeometryCompiler.Compile(brs, eff, make()).Geometry;
            sb.AppendLine();
            sb.AppendLine($"=== {tag}: faces={g.Faces.Count} ===");
            sb.Append(Classify(g));
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("dm04_perseam_byconfig.txt", report);
    }

    /// <summary>Decision-option 1 support (REPORT-ONLY): does PartitionClip disturb the room/portal graph
    /// under the EdgeLerp+RegionWise defaults? MEASURED FINDING (flagship 30): YES on ctf07 — def is RED-exact
    /// (158 rooms / 97 portals, the flagship-20 win) but part merges rooms through the doorway membranes its
    /// 32 closed portal-adjacent seams weld shut (154 / 93, AWAY from RED) — the same watertight-flood/room-merge
    /// interaction the leaf-extraction path hit on dm04 (9→5 main rooms). A NEW flip-blocker for the PartitionClip
    /// option beyond the ctf04/ctfstockintrade hole regressions; recorded in the parity notes. dm04 and the other
    /// five measured levels hold byte-identical. Report-only (the finding is the deliverable; the default-path
    /// room/portal gates live in RedRoomStructureDiffTests).</summary>
    [Fact]
    public void PartitionClip_RoomsPortals_Compare()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"PartitionClip rooms/portals vs def. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach (string name in new[] { "dm04.rfl", "ctf04.rfl", "ctfstockintradeb1.rfl", "ctf01.rfl", "ctf02.rfl", "ctf07.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            Geometry gd = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false }).Geometry;
            Geometry gp = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, PartitionClip = true }).Geometry;
            int dSub = gd.Rooms.Count(r => r.IsSubroom != 0), pSub = gp.Rooms.Count(r => r.IsSubroom != 0);
            bool held = gd.Rooms.Count == gp.Rooms.Count && gd.Portals.Count == gp.Portals.Count;
            sb.AppendLine($"{name,-24} def rooms={gd.Rooms.Count}({dSub}) portals={gd.Portals.Count} | part rooms={gp.Rooms.Count}({pSub}) portals={gp.Portals.Count}{(held ? string.Empty : "  <== DISTURBED")}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("dm04_partclip_rooms.txt", report);
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
        var sb = new StringBuilder();
        int tj = 0, sl = 0, ot = 0;
        foreach ((int ia, int ib) in open)
        {
            Vec3 pa = g.Vertices[ia], pb = g.Vertices[ib];
            Vec3 dir = pb.Sub(pa);
            float len = dir.Length();
            Vec3 mid = Vec3Math.Lerp(pa, pb, 0.5f);
            Face of = g.Faces[owner[(ia, ib)]];
            float bestPerp = float.MaxValue;
            string bestDesc = "none";
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
                    float t = w.Dot(qd) / (qlen * qlen);
                    float perp = qa.Add(qd.Scale(t)).Distance(mid);
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

                    float ov = MathF.Min(len, tb1) - MathF.Max(0, tb0);
                    if (ov <= 0.001f)
                    {
                        continue;
                    }

                    if (perp < bestPerp)
                    {
                        bestPerp = perp;
                        bool tjoint = (tb0 < -0.001f && tb1 > 0.001f) || (tb0 < len - 0.001f && tb1 > len + 0.001f);
                        Vec3 pn = f2.Plane.Normal;
                        bestDesc = $"face{pf} n=({pn.X:F3},{pn.Y:F3},{pn.Z:F3}) perp={perp * 1000:F2}mm overlap={ov * 1000:F1}mm {(tjoint ? "T-JOINT" : "aligned")}";
                    }
                }
            }

            string cls = bestPerp < 0.5e-3f ? "TJOINT(collinear)" : bestPerp < 0.05f ? "SLIVER(parallel-offset)" : "OTHER(no partner)";
            if (bestPerp < 0.5e-3f)
            {
                tj++;
            }
            else if (bestPerp < 0.05f)
            {
                sl++;
            }
            else
            {
                ot++;
            }

            Vec3 on = of.Plane.Normal;
            sb.AppendLine($"  ({pa.X:F3},{pa.Y:F3},{pa.Z:F3})->({pb.X:F3},{pb.Y:F3},{pb.Z:F3}) len={len * 1000:F1}mm [{cls}] owner n=({on.X:F3},{on.Y:F3},{on.Z:F3}) best: {bestDesc}");
        }

        sb.AppendLine($"  summary: TJOINT={tj} SLIVER={sl} OTHER={ot} total={open.Count}");
        return sb.ToString();
    }

    private static (int Holes, int Faces, bool PathUsed) Measure(List<Brush> brs, List<RoomEffect> eff, CompileOptions o)
    {
        CompiledLevel c = GeometryCompiler.Compile(brs, eff, o);
        Geometry g = c.Geometry;
        bool pathUsed = !o.UseLeafExtraction || c.Report.LeafExtractionUsed;
        return (OpenEdges(g), g.Faces.Count, pathUsed);
    }

    private static int OpenEdges(Geometry g)
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

        return count.Count(kv => kv.Value == 1);
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
