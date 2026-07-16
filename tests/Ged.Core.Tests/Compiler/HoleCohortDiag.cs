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
/// DIAGNOSTIC (flagship 9): categorises the extraction-path open-edge cohort on the levels where
/// extraction leaks (dm02/dm06/ctf02 regressions vs a clean per-brush; dm04 residual). For each open
/// edge it finds the nearest COLLINEAR partner open edge (an opposite-direction edge on the same line)
/// and reports the endpoint gap + perpendicular offset, so we can tell apart:
///   - T-junction (partner edge present, endpoints project interior, perp &lt; mm) -> a fixer miss
///   - near-pair station divergence (endpoints ~1-3 mm apart) -> the line-weld cohort
///   - genuine extent divergence / missing boundary (no partner, or &gt;1 cm) -> tessellation floor
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class HoleCohortDiag
{
    private readonly ITestOutputHelper _out;

    public HoleCohortDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Categorise_Extraction_Holes()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (string name in new[] { "dm02.rfl", "dm05.rfl", "ctfwlpro.rfl", "dm06.rfl", "ctf02.rfl", "dm04.rfl", "dmabruptdecayrc2a27.rfl", "ctf01.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
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

            if (bs is null)
            {
                continue;
            }

            List<Brush> brs = bs.Brushes.ToList();
            List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();
            CompiledLevel ex = GeometryCompiler.Compile(
                brs, eff, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
            Geometry g = ex.Geometry;

            // Collect every open (unpaired) non-detail edge as a directed segment (a,b vertex indices).
            var edgeCount = new Dictionary<(int, int), int>();
            var openEdges = new List<(int A, int B)>();
            foreach (Face f in g.Faces)
            {
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
                    int a = f.Vertices[i].Index, b = f.Vertices[(i + 1) % n].Index;
                    if (a == b)
                    {
                        continue;
                    }

                    var key = a < b ? (a, b) : (b, a);
                    edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
                }
            }

            foreach (((int a, int b) key, int c) in edgeCount)
            {
                if (c == 1)
                {
                    openEdges.Add(key);
                }
            }

            // Categorise each open edge against the nearest other open edge.
            int tjunc = 0, nearPair = 0, extentDiv = 0, noPartner = 0;
            var buckets = new int[6]; // <=1mm, <=3mm, <=1cm, <=3cm, <=10cm, none
            var samples = new List<string>();
            for (int i = 0; i < openEdges.Count; i++)
            {
                Vec3 a0 = g.Vertices[openEdges[i].A], a1 = g.Vertices[openEdges[i].B];
                Vec3 dir = a1.Sub(a0);
                float len = dir.Length();
                if (len < 1e-5f)
                {
                    continue;
                }

                Vec3 u = dir.Scale(1f / len);
                // nearest partner: another open edge whose midpoint-region overlaps this line, opposite dir.
                float bestPerp = float.MaxValue;
                float bestEndGap = float.MaxValue;
                bool collinearPartner = false;
                for (int j = 0; j < openEdges.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    Vec3 b0 = g.Vertices[openEdges[j].A], b1 = g.Vertices[openEdges[j].B];
                    // perpendicular distance of b0,b1 to this line
                    float p0 = b0.Sub(a0).Sub(u.Scale(b0.Sub(a0).Dot(u))).Length();
                    float p1 = b1.Sub(a0).Sub(u.Scale(b1.Sub(a0).Dot(u))).Length();
                    float perp = MathF.Max(p0, p1);
                    if (perp < 0.02f)
                    {
                        collinearPartner = true;
                        // endpoint gap: nearest of the 4 endpoint pairings
                        float g00 = a0.Sub(b0).Length(), g01 = a0.Sub(b1).Length();
                        float g10 = a1.Sub(b0).Length(), g11 = a1.Sub(b1).Length();
                        float endGap = MathF.Min(MathF.Min(g00, g01), MathF.Min(g10, g11));
                        if (perp < bestPerp)
                        {
                            bestPerp = perp;
                        }

                        if (endGap < bestEndGap)
                        {
                            bestEndGap = endGap;
                        }
                    }
                }

                float nearest = collinearPartner ? bestEndGap : float.MaxValue;
                if (!collinearPartner)
                {
                    noPartner++;
                    buckets[5]++;
                }
                else if (nearest <= 1e-3f)
                {
                    tjunc++;
                    buckets[0]++;
                }
                else if (nearest <= 3e-3f)
                {
                    nearPair++;
                    buckets[1]++;
                }
                else if (nearest <= 1e-2f)
                {
                    extentDiv++;
                    buckets[2]++;
                }
                else if (nearest <= 3e-2f)
                {
                    buckets[3]++;
                }
                else if (nearest <= 1e-1f)
                {
                    buckets[4]++;
                }
                else
                {
                    buckets[5]++;
                }

                if (samples.Count < 14)
                {
                    samples.Add($"    edge ({a0.X:F3},{a0.Y:F3},{a0.Z:F3})->({a1.X:F3},{a1.Y:F3},{a1.Z:F3}) len={len:F4} " +
                        (collinearPartner ? $"collinearPartner endGap={bestEndGap * 1000:F2}mm perp={bestPerp * 1000:F2}mm" : "NO collinear partner"));
                }
            }

            sb.AppendLine($"=== {name}: extraction openEdges={openEdges.Count}");
            sb.AppendLine($"    endGap buckets: <=1mm={buckets[0]} <=3mm={buckets[1]} <=1cm={buckets[2]} <=3cm={buckets[3]} <=10cm={buckets[4]} none/far={buckets[5]}");
            foreach (string s in samples)
            {
                sb.AppendLine(s);
            }

            sb.AppendLine();
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("hole_cohort_diag.txt", report);
    }

    // flagship 12: same categorisation on the INCREMENTAL path (the residual terrain-floor cohort).
    [Fact]
    public void Categorise_Incremental_Holes()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (string name in new[] { "dm04.rfl", "dmabruptdecayrc2a27.rfl", "ctf01.rfl", "ctf02.rfl", "ctfwlpro.rfl", "dmwarzoneclassicb1.rfl", "ctf07.rfl" })
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
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

            if (bs is null)
            {
                continue;
            }

            List<Brush> brs = bs.Brushes.ToList();
            List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();
            CompiledLevel ex = GeometryCompiler.Compile(
                brs, eff, new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = true });
            Geometry g = ex.Geometry;

            var edgeCount = new Dictionary<(int, int), int>();
            foreach (Face f in g.Faces)
            {
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
                    int a = f.Vertices[i].Index, b = f.Vertices[(i + 1) % n].Index;
                    if (a == b)
                    {
                        continue;
                    }

                    var key = a < b ? (a, b) : (b, a);
                    edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
                }
            }

            var openEdges = new List<(int A, int B)>();
            foreach (((int a, int b) key, int c) in edgeCount)
            {
                if (c == 1)
                {
                    openEdges.Add(key);
                }
            }

            var buckets = new int[6];
            var samples = new List<string>();
            for (int i = 0; i < openEdges.Count; i++)
            {
                Vec3 a0 = g.Vertices[openEdges[i].A], a1 = g.Vertices[openEdges[i].B];
                Vec3 dir = a1.Sub(a0);
                float len = dir.Length();
                if (len < 1e-5f)
                {
                    continue;
                }

                Vec3 u = dir.Scale(1f / len);
                float bestPerp = float.MaxValue;
                float bestEndGap = float.MaxValue;
                bool collinearPartner = false;
                for (int j = 0; j < openEdges.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    Vec3 b0 = g.Vertices[openEdges[j].A], b1 = g.Vertices[openEdges[j].B];
                    float p0 = b0.Sub(a0).Sub(u.Scale(b0.Sub(a0).Dot(u))).Length();
                    float p1 = b1.Sub(a0).Sub(u.Scale(b1.Sub(a0).Dot(u))).Length();
                    float perp = MathF.Max(p0, p1);
                    if (perp < 0.02f)
                    {
                        collinearPartner = true;
                        float g00 = a0.Sub(b0).Length(), g01 = a0.Sub(b1).Length();
                        float g10 = a1.Sub(b0).Length(), g11 = a1.Sub(b1).Length();
                        float endGap = MathF.Min(MathF.Min(g00, g01), MathF.Min(g10, g11));
                        if (perp < bestPerp)
                        {
                            bestPerp = perp;
                        }

                        if (endGap < bestEndGap)
                        {
                            bestEndGap = endGap;
                        }
                    }
                }

                float nearest = collinearPartner ? bestEndGap : float.MaxValue;
                if (!collinearPartner)
                {
                    buckets[5]++;
                }
                else if (nearest <= 1e-3f)
                {
                    buckets[0]++;
                }
                else if (nearest <= 3e-3f)
                {
                    buckets[1]++;
                }
                else if (nearest <= 1e-2f)
                {
                    buckets[2]++;
                }
                else if (nearest <= 3e-2f)
                {
                    buckets[3]++;
                }
                else if (nearest <= 1e-1f)
                {
                    buckets[4]++;
                }
                else
                {
                    buckets[5]++;
                }

                if (samples.Count < 30)
                {
                    samples.Add($"    edge ({a0.X:F3},{a0.Y:F3},{a0.Z:F3})->({a1.X:F3},{a1.Y:F3},{a1.Z:F3}) len={len:F4} " +
                        (collinearPartner ? $"partnerEndGap={bestEndGap * 1000:F2}mm perp={bestPerp * 1000:F2}mm" : "NO collinear partner"));
                }
            }

            sb.AppendLine($"=== {name}: incremental openEdges={openEdges.Count}");
            sb.AppendLine($"    endGap buckets: <=1mm={buckets[0]} <=3mm={buckets[1]} <=1cm={buckets[2]} <=3cm={buckets[3]} <=10cm={buckets[4]} none/far={buckets[5]}");
            foreach (string s in samples)
            {
                sb.AppendLine(s);
            }

            sb.AppendLine();
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("hole_cohort_inc_diag.txt", report);
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
