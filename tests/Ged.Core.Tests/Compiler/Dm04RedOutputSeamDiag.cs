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
/// GROUND-TRUTH per-seam closure pass. Reads RED's OWN compiled dm04 output (the GeometrySection in
/// dm04.rfl) and dumps every face whose polygon lies within ~0.5 m of each of the 9 residual seams,
/// with plane / texture / room / vertex-loop (pool index + position). Then does the same on GED's
/// SharedBsp output for direct comparison. The point is difference-elimination at the ARTIFACT level:
/// read RED's OUTPUT, not RED's process. Set GED_REDSEAM=1 to run.
/// </summary>
public sealed class Dm04RedOutputSeamDiag
{
    private readonly ITestOutputHelper _out;

    public Dm04RedOutputSeamDiag(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("GED_REDSEAM") == "1";

    // The 9 residual seams (shared_bsp_dm04_perseam.txt, def/shared config). Each is a segment.
    private static readonly (string Name, Vec3 A, Vec3 B)[] Seams =
    {
        ("A_cl1_sliver1.17", new Vec3(-36.522f, -65.156f, -9.754f), new Vec3(-37.726f, -65.159f, -9.936f)),
        ("B_cl1_stub16",     new Vec3(-37.742f, -65.162f, -9.939f), new Vec3(-37.726f, -65.159f, -9.936f)),
        ("C_cl2_stub22",     new Vec3(-11.843f, -65.161f, 33.832f), new Vec3(-11.864f, -65.160f, 33.840f)),
        ("D_cl2_sliver1.75", new Vec3(-12.353f, -65.229f, 34.057f), new Vec3(-11.864f, -65.160f, 33.840f)),
        ("E_cl3_stub12",     new Vec3(-14.510f, -60.063f, -4.135f), new Vec3(-14.512f, -60.064f, -4.124f)),
        ("F_cl3_other2680",  new Vec3(-14.510f, -60.063f, -4.135f), new Vec3(-17.027f, -60.059f, -5.058f)),
        ("G_cl3_sliver2.65", new Vec3(-18.004f, -60.064f, -5.059f), new Vec3(-17.027f, -60.059f, -5.058f)),
        ("H_cl3_tjoint0.00", new Vec3(-14.519f, -60.064f, -4.088f), new Vec3(-14.512f, -60.064f, -4.124f)),
        ("I_cl3_tjoint0.09", new Vec3(-18.004f, -60.064f, -5.059f), new Vec3(-14.519f, -60.064f, -4.088f)),
    };

    private const float Radius = 0.5f;

    [Fact]
    public void Dump_Red_And_Ged_At_Seams()
    {
        if (!Corpus.Available || !Enabled)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();

        Geometry? red = null;
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                red = gs.Geometry;
            }

            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        Assert.NotNull(red);
        var sb = new StringBuilder();
        sb.AppendLine($"dm04 GROUND-TRUTH per-seam. RED compiled output vs GED SharedBsp. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        // --- RED's own output totals + open-edge census (index identity, like HoleDetector) ---
        sb.AppendLine($"RED output: verts={red!.Vertices.Count} faces={red.Faces.Count} rooms={red.Rooms.Count} portals={red.Portals.Count}");
        (List<(int A, int B)> redOpen, int redWallEdges) = OpenEdges(red);
        sb.AppendLine($"RED wall-manifold open edges (non-detail/liquid/portal, index identity): {redOpen.Count} / {redWallEdges} total wall edges");
        sb.AppendLine();

        DumpGeometry(sb, "RED", red, redOpen);

        // --- GED SharedBsp output ---
        List<Brush> brs = bs?.Brushes.ToList() ?? new List<Brush>();
        List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();
        Geometry ged = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = true }).Geometry;
        (List<(int A, int B)> gedOpen, int gedWallEdges) = OpenEdges(ged);
        sb.AppendLine();
        sb.AppendLine($"GED SharedBsp output: verts={ged.Vertices.Count} faces={ged.Faces.Count} rooms={ged.Rooms.Count} portals={ged.Portals.Count}");
        sb.AppendLine($"GED wall-manifold open edges: {gedOpen.Count} / {gedWallEdges} total wall edges");
        sb.AppendLine();
        DumpGeometry(sb, "GED-SharedBsp", ged, gedOpen);

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("dm04_redoutput_perseam.txt", report);
    }

    private static void DumpGeometry(StringBuilder sb, string tag, Geometry g, List<(int A, int B)> open)
    {
        // Distinct floor planes (n ~ +/-Y) anywhere in cluster-3 box, to answer "one floor plane or two".
        sb.AppendLine($"===== {tag}: distinct near-horizontal (|ny|>0.95) planes in cluster-3 box (x[-19,-13] y[-61,-59] z[-6,-3]) =====");
        var floorPlanes = new List<(Vec3 N, float Off, int Count)>();
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            if (f.IsPortalFace || f.Vertices.Count < 3)
            {
                continue;
            }

            Vec3 n = f.Plane.Normal;
            if (MathF.Abs(n.Y) < 0.95f)
            {
                continue;
            }

            // any vertex inside cluster-3 box?
            bool inBox = false;
            foreach (FaceVertex v in f.Vertices)
            {
                Vec3 p = g.Vertices[v.Index];
                if (p.X > -19f && p.X < -13f && p.Y > -61f && p.Y < -59f && p.Z > -6f && p.Z < -3f)
                {
                    inBox = true;
                    break;
                }
            }

            if (!inBox)
            {
                continue;
            }

            int match = floorPlanes.FindIndex(pl => MathF.Abs(pl.N.Dot(n)) > 0.99995f && MathF.Abs(MathF.Abs(pl.Off) - MathF.Abs(f.Plane.Offset)) < 2e-3f);
            if (match < 0)
            {
                floorPlanes.Add((n, f.Plane.Offset, 1));
            }
            else
            {
                floorPlanes[match] = (floorPlanes[match].N, floorPlanes[match].Off, floorPlanes[match].Count + 1);
            }
        }

        foreach ((Vec3 n, float off, int c) in floorPlanes)
        {
            sb.AppendLine($"   plane n=({n.X:F5},{n.Y:F5},{n.Z:F5}) off={off:F5}  x{c} faces");
        }

        sb.AppendLine();

        foreach ((string name, Vec3 a, Vec3 b) in Seams)
        {
            sb.AppendLine($"--- {tag} seam {name}: ({a.X:F3},{a.Y:F3},{a.Z:F3})->({b.X:F3},{b.Y:F3},{b.Z:F3}) ---");

            // Open edges (this geometry) within radius of the seam segment.
            int openNear = 0;
            foreach ((int ia, int ib) in open)
            {
                Vec3 mid = Vec3Math.Lerp(g.Vertices[ia], g.Vertices[ib], 0.5f);
                if (SegDist(mid, a, b) < Radius)
                {
                    openNear++;
                    if (openNear <= 8)
                    {
                        sb.AppendLine($"    OPEN edge idx({ia},{ib}) ({g.Vertices[ia].X:F3},{g.Vertices[ia].Y:F3},{g.Vertices[ia].Z:F3})->({g.Vertices[ib].X:F3},{g.Vertices[ib].Y:F3},{g.Vertices[ib].Z:F3})");
                    }
                }
            }

            if (openNear == 0)
            {
                sb.AppendLine("    (no open edges within 0.5 m — sealed)");
            }

            // Faces near the seam.
            var near = new List<int>();
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Vertices.Count < 3 || f.IsPortalFace)
                {
                    continue;
                }

                float best = float.MaxValue;
                foreach (FaceVertex v in f.Vertices)
                {
                    best = MathF.Min(best, SegDist(g.Vertices[v.Index], a, b));
                }

                if (best < Radius)
                {
                    near.Add(fi);
                }
            }

            foreach (int fi in near)
            {
                Face f = g.Faces[fi];
                Vec3 n = f.Plane.Normal;
                var fsb = new StringBuilder();
                fsb.Append($"    face{fi} tex={f.Texture} room={f.RoomIndex} fl=0x{f.Flags:X} n=({n.X:F4},{n.Y:F4},{n.Z:F4}) off={f.Plane.Offset:F4} loop=");
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    fsb.Append($"#{v.Index}({p.X:F3},{p.Y:F3},{p.Z:F3}) ");
                }

                sb.AppendLine(fsb.ToString());
            }

            sb.AppendLine();
        }
    }

    [Fact]
    public void Dump_OpenEdge_Partners()
    {
        if (!Corpus.Available || !Enabled)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        List<Brush> brs = bs?.Brushes.ToList() ?? new List<Brush>();
        List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();
        Geometry g = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = true }).Geometry;
        (List<(int A, int B)> open, _) = OpenEdges(g);

        // Which faces (wall, non-portal) use each vertex, and how many times each edge is used.
        var vtxFaces = new Dictionary<int, List<int>>();
        var edgeCount = new Dictionary<(int, int), int>();
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            if (f.Texture < 0 || f.PortalIndexPlus2 >= 2 || ((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
            {
                continue;
            }

            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                if (!vtxFaces.TryGetValue(a, out List<int>? lst))
                {
                    vtxFaces[a] = lst = new List<int>();
                }

                if (!lst.Contains(fi))
                {
                    lst.Add(fi);
                }

                int b = f.Vertices[(i + 1) % n].Index;
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
            }
        }

        var openVerts = new HashSet<int>();
        foreach ((int a, int b) in open)
        {
            openVerts.Add(a);
            openVerts.Add(b);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"GED SharedBsp open-edge partner analysis. {open.Count} open edges. generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach ((int a, int b) in open.OrderBy(e => e.A).ThenBy(e => e.B))
        {
            Vec3 pa = g.Vertices[a], pb = g.Vertices[b];
            sb.AppendLine($"OPEN #{a}({pa.X:F6},{pa.Y:F6},{pa.Z:F6}) -> #{b}({pb.X:F6},{pb.Y:F6},{pb.Z:F6}) len={pb.Sub(pa).Length() * 1000:F2}mm faces[#{a}]={string.Join(",", vtxFaces.GetValueOrDefault(a) ?? new())} faces[#{b}]={string.Join(",", vtxFaces.GetValueOrDefault(b) ?? new())}");
            foreach (int endp in new[] { a, b })
            {
                Vec3 pe = g.Vertices[endp];
                // Nearest OTHER pool vertex (different index) used by a wall face.
                int bestI = -1;
                float bestD = float.MaxValue;
                foreach (int other in vtxFaces.Keys)
                {
                    if (other == endp)
                    {
                        continue;
                    }

                    float d = g.Vertices[other].Sub(pe).Length();
                    if (d < bestD)
                    {
                        bestD = d;
                        bestI = other;
                    }
                }

                if (bestI >= 0)
                {
                    string kind = openVerts.Contains(bestI) ? "OPEN" : "manifold";
                    sb.AppendLine($"    #{endp} nearest foreign vtx #{bestI} d={bestD * 1000:F3}mm [{kind}] faces={string.Join(",", vtxFaces.GetValueOrDefault(bestI) ?? new())}");
                }
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("dm04_open_partners.txt", report);
    }

    /// <summary>Wall-manifold open edges by pool-index identity (mirrors HoleDetector's edge census).</summary>
    private static (List<(int, int)> Open, int Total) OpenEdges(Geometry g)
    {
        var count = new Dictionary<(int, int), int>();
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

        var open = count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        return (open, count.Count);
    }

    private static float SegDist(Vec3 p, Vec3 a, Vec3 b)
    {
        Vec3 ab = b.Sub(a);
        float len2 = ab.LengthSquared();
        if (len2 < 1e-9f)
        {
            return p.Sub(a).Length();
        }

        float t = MathF.Max(0f, MathF.Min(1f, p.Sub(a).Dot(ab) / len2));
        return p.Sub(a.Add(ab.Scale(t))).Length();
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
