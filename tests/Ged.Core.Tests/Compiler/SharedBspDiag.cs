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
/// FLAGSHIP 31 — RED's AUTHENTIC SINGLE ACCUMULATED SHARED BSP (<see cref="CompileOptions.SharedBsp"/>): the
/// persistent shared boundary with BOTH world faces and caps routed down ONE accumulated partition symmetrically.
/// This sweep measures the shared-BSP path against the shipping default (def) and the global-partition fold
/// (glob) across the corpus (holes / faces / solve ms) and per-seam on dm04, so the flip/keep decision rests on
/// measurement. Opt-in behind GED_SHAREDBSP_MEASURE=1 (the shared-BSP path is heavier than the incremental fold,
/// so it does not bloat the default parallel suite). Writes tests/artifacts/shared_bsp_*.txt.
/// </summary>
public sealed class SharedBspDiag
{
    private readonly ITestOutputHelper _out;

    public SharedBspDiag(ITestOutputHelper output) => _out = output;

    private static bool MeasureEnabled => Environment.GetEnvironmentVariable("GED_SHAREDBSP_MEASURE") == "1";

    private static readonly (string Tag, Func<CompileOptions> Make)[] Configs =
    {
        ("def",    () => new CompileOptions { BuildSurfaces = false }),
        ("glob",   () => new CompileOptions { BuildSurfaces = false, GlobalPartition = true }),
        ("shared", () => new CompileOptions { BuildSurfaces = false, SharedBsp = true }),
    };

    private static bool Canonical(string name) =>
        !name.Contains(".autosave.", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("dmabruptdecayrc2a27~.rfl", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void FullCorpus_Def_Glob_Shared()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SharedBsp corpus sweep. generated {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail");
        sb.AppendLine("non-liquid non-portal single-use edges. def=shipping default; glob=GlobalPartition;");
        sb.AppendLine("shared=SharedBsp (flagship 31). faces + solve ms in parens/columns.");
        sb.AppendLine();
        sb.AppendLine($"{"level",-26} {"def",14} {"glob",14} {"shared",14} {"defMs",8} {"shMs",8} {"note",-8}");
        sb.AppendLine(new string('-', 100));

        int better = 0, worse = 0, equal = 0, zerosBroken = 0;
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
            int def = -1, sh = -1;
            double defMs = 0, shMs = 0;
            foreach ((string tag, Func<CompileOptions> make) in Configs)
            {
                (int holes, int faces, double ms) = Measure(brs, eff, make());
                cell[tag] = $"{holes}({faces})";
                if (tag == "def")
                {
                    def = holes;
                    defMs = ms;
                }
                else if (tag == "shared")
                {
                    sh = holes;
                    shMs = ms;
                }
            }

            string note = string.Empty;
            if (sh < def)
            {
                better++;
                note = "BETTER";
            }
            else if (sh > def)
            {
                worse++;
                note = "worse";
                if (def == 0)
                {
                    zerosBroken++;
                    note = "ZERO-BROKEN";
                }
            }
            else
            {
                equal++;
            }

            sb.AppendLine($"{name.Replace("~", string.Empty),-26} {cell["def"],14} {cell["glob"],14} {cell["shared"],14} {defMs,8:F0} {shMs,8:F0} {note,-8}");
        }

        sb.AppendLine();
        sb.AppendLine($"shared vs def: better={better} worse={worse} equal={equal} zeros-broken={zerosBroken}");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("shared_bsp_corpus.txt", report);
    }

    [Fact]
    public void KeyLevels_Shared()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SharedBsp key-levels probe. {DateTime.Now:HH:mm:ss}");
        foreach (string name in new[]
                 {
                     "dm04.rfl", "ctf01.rfl", "dm07.rfl", "ctf07.rfl", "ctfwlpro.rfl", "ctf04.rfl",
                     "dmedgeofdespairb1a1.rfl", "dm08.rfl", "dm15.rfl", "dmwarzoneclassicb1.rfl", "ctf02.rfl", "dm06.rfl",
                 })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            (int def, _, _) = Measure(brs, eff, new CompileOptions { BuildSurfaces = false });
            (int sh, int shf, double shMs) = Measure(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = true });
            string note = sh < def ? "BETTER" : sh > def ? (def == 0 ? "ZERO-BROKEN" : "worse") : string.Empty;
            sb.AppendLine($"{name.Replace("~", string.Empty),-26} def={def,-4} shared={sh,-4} faces={shf,-6} {shMs,7:F0}ms {note}");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("shared_bsp_keylevels.txt", report);
    }

    [Fact]
    public void RoomsPortals_Shared_vs_Def()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SharedBsp rooms/portals vs def. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("ctf07 def is RED-exact 158/97 (flagship 20). SharedBsp must HOLD the room/portal graph.");
        sb.AppendLine();
        bool anyDisturbed = false;
        foreach (string name in new[]
                 {
                     "ctf07.rfl", "dm04.rfl", "ctf04.rfl", "ctfwlpro.rfl", "ctf01.rfl", "dm07.rfl",
                     "dmabruptdecayrc2a27.rfl", "dmedgeofdespairb1a1.rfl", "dm08.rfl", "ctf02.rfl", "dm06.rfl",
                 })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            Geometry gd = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false }).Geometry;
            Geometry gs = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = true }).Geometry;
            int dSub = gd.Rooms.Count(r => r.IsSubroom != 0), sSub = gs.Rooms.Count(r => r.IsSubroom != 0);
            bool held = gd.Rooms.Count == gs.Rooms.Count && gd.Portals.Count == gs.Portals.Count;
            if (!held)
            {
                anyDisturbed = true;
            }

            sb.AppendLine($"{name.Replace("~", string.Empty),-26} def rooms={gd.Rooms.Count}({dSub}) portals={gd.Portals.Count} | shared rooms={gs.Rooms.Count}({sSub}) portals={gs.Portals.Count}{(held ? string.Empty : "  <== DISTURBED")}");
        }

        sb.AppendLine();
        sb.AppendLine(anyDisturbed ? "RESULT: at least one level DISTURBED" : "RESULT: all room/portal graphs HELD");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("shared_bsp_rooms.txt", report);
    }

    [Fact]
    public void Dm04_PerSeam_Shared()
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
        sb.AppendLine($"dm04 PER-SEAM under SharedBsp. generated {DateTime.Now:yyyy-MM-dd HH:mm}");

        foreach ((string tag, Func<CompileOptions> make) in Configs)
        {
            Geometry g = GeometryCompiler.Compile(brs, eff, make()).Geometry;
            sb.AppendLine();
            sb.AppendLine($"=== {tag}: faces={g.Faces.Count} ===");
            sb.Append(Classify(g));
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("shared_bsp_dm04_perseam.txt", report);
    }

    private static (int Holes, int Faces, double Ms) Measure(List<Brush> brs, List<RoomEffect> eff, CompileOptions o)
    {
        var sw = Stopwatch.StartNew();
        Geometry g = GeometryCompiler.Compile(brs, eff, o).Geometry;
        sw.Stop();
        return (OpenEdges(g), g.Faces.Count, sw.Elapsed.TotalMilliseconds);
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

    // Per-seam classifier, mirrors Dm04SeamCampaignDiag.Classify.
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
