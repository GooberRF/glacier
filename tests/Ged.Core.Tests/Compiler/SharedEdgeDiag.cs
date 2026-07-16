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
/// DIAGNOSTIC (flagship 13): dumps every open edge (HoleDetector's single-use non-detail edge) on the
/// incremental default path for a level, and for each searches all faces for a COLLINEAR would-be partner
/// (a face edge lying on the same line that overlaps the open edge). Classifies each open edge as a fixable
/// T-junction (a partner exists on the SAME line, just subdivided differently) vs a genuine tessellation
/// sliver (the nearest opposing structure is on a DIFFERENT near-parallel line — no shared vertex can seal it).
/// </summary>
public sealed class SharedEdgeDiag
{
    private readonly ITestOutputHelper _out;

    public SharedEdgeDiag(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dm04.rfl")]
    [InlineData("ctf07.rfl")]
    [InlineData("ctf01.rfl")]
    public void Characterise_Open_Edges(string level)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        List<Brush> brushes = bs!.Brushes.ToList();
        List<RoomEffect> effects = es?.Effects.ToList() ?? new List<RoomEffect>();
        CompiledLevel c = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false });
        Geometry g = c.Geometry;

        var sb = new StringBuilder();
        sb.AppendLine($"{level}: faces={g.Faces.Count} incUsed={c.Report.IncrementalUsed}");

        // Rebuild the open-edge set (HoleDetector semantics: single-use non-detail non-liquid edges).
        var count = new Dictionary<(int, int), int>();
        var owner = new Dictionary<(int, int), (int Face, int I)>();
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
                    owner[key] = (fi, i);
                }
            }
        }

        var open = count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        sb.AppendLine($"open edges: {open.Count}");

        int fixableTjoint = 0, sliver = 0, other = 0;
        foreach ((int ia, int ib) in open)
        {
            Vec3 pa = g.Vertices[ia], pb = g.Vertices[ib];
            Vec3 dir = pb.Sub(pa);
            float len = dir.Length();
            Vec3 mid = Vec3Math.Lerp(pa, pb, 0.5f);
            (int fowner, _) = owner[(ia, ib)];
            Face of = g.Faces[fowner];

            // Search for the best would-be partner: a face edge that is (near-)collinear with this open edge
            // and overlaps it, on a DIFFERENT face. Report the closest approach + whether an endpoint of ours
            // lands mid-partner-edge (classic T-junction) or the partner is a parallel offset line (sliver).
            float bestPerp = float.MaxValue;
            float bestParallelGap = float.MaxValue;
            string bestDesc = "none";
            for (int pf = 0; pf < g.Faces.Count; pf++)
            {
                if (pf == fowner)
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

                    // Direction must be (anti)parallel.
                    float dot = MathF.Abs(dir.Dot(qd) / (len * qlen));
                    if (dot < 0.999f)
                    {
                        continue;
                    }

                    // Perpendicular distance of our midpoint to the partner's infinite line.
                    Vec3 w = mid.Sub(qa);
                    float t = w.Dot(qd) / (qlen * qlen);
                    Vec3 proj = qa.Add(qd.Scale(t));
                    float perp = proj.Distance(mid);
                    if (perp > 0.05f)
                    {
                        continue; // > 5 cm off: unrelated
                    }

                    // Longitudinal overlap of [pa,pb] with [qa,qb] along our dir.
                    float ta0 = 0, ta1 = len;
                    float tb0 = qa.Sub(pa).Dot(dir) / len;
                    float tb1 = qb.Sub(pa).Dot(dir) / len;
                    if (tb0 > tb1)
                    {
                        (tb0, tb1) = (tb1, tb0);
                    }

                    float ov = MathF.Min(ta1, tb1) - MathF.Max(ta0, tb0);
                    if (ov <= 0.001f)
                    {
                        continue; // no longitudinal overlap
                    }

                    if (perp < bestPerp)
                    {
                        bestPerp = perp;
                        bestParallelGap = perp;
                        bool endpointOnPartner = (tb0 < -0.001f && tb1 > 0.001f) || (tb0 < len - 0.001f && tb1 > len + 0.001f);
                        Vec3 pn = f2.Plane.Normal;
                        bestDesc = $"face{pf} n=({pn.X:F3},{pn.Y:F3},{pn.Z:F3}) perp={perp * 1000:F2}mm overlap={ov * 1000:F1}mm partnerSpan=[{tb0 * 1000:F1},{tb1 * 1000:F1}]mm {(endpointOnPartner ? "T-JOINT" : "aligned")}";
                    }
                }
            }

            Vec3 on = of.Plane.Normal;
            string ownerN = $"n=({on.X:F3},{on.Y:F3},{on.Z:F3})";

            string cls;
            if (bestPerp < 0.5e-3f)
            {
                cls = "TJOINT(collinear)"; // partner exactly on our line — a pure T-junction
                fixableTjoint++;
            }
            else if (bestPerp < 0.05f)
            {
                cls = "SLIVER(parallel-offset)"; // partner on a parallel line a few mm off — asymmetric tessellation
                sliver++;
            }
            else
            {
                cls = "OTHER(no partner)";
                other++;
            }

            sb.AppendLine($"  open ({pa.X:F3},{pa.Y:F3},{pa.Z:F3})->({pb.X:F3},{pb.Y:F3},{pb.Z:F3}) len={len * 1000:F1}mm  [{cls}]  owner {ownerN}  best: {bestDesc}");
        }

        sb.AppendLine($"summary: TJOINT={fixableTjoint} SLIVER={sliver} OTHER={other}");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact($"shared_edge_diag_{level}.txt", report);
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
