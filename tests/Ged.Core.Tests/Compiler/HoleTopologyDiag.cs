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
/// DIAGNOSTIC (flagship 9): for the extraction path on dm02, dumps the FULL topology around the first
/// residual open edges — every face carrying a vertex near either endpoint, with its whole vertex loop,
/// winding, flags, and whether the shared edge is same- or opposite-wound. Reveals why the seam sealer /
/// t-joint fixer cannot pair them (divergent station, T-junction stem not open, same-winding overlap, ...).
/// </summary>
public sealed class HoleTopologyDiag
{
    private readonly ITestOutputHelper _out;

    public HoleTopologyDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_Dm02_Open_Edge_Topology()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm02.rfl");
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        List<Brush> brs = bs!.Brushes.ToList();
        List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();
        CompiledLevel ex = GeometryCompiler.Compile(
            brs, eff, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
        Geometry g = ex.Geometry;

        // Open (unpaired) non-detail edges.
        var edgeUse = new Dictionary<(int, int), int>();
        var edgeFace = new Dictionary<(int, int), List<int>>();
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
                int a = f.Vertices[i].Index, b = f.Vertices[(i + 1) % n].Index;
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                if (!edgeFace.TryGetValue(key, out List<int>? l))
                {
                    edgeFace[key] = l = new List<int>();
                }

                l.Add(fi);
            }
        }

        var open = edgeUse.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"dm02 extraction: faces={g.Faces.Count} verts={g.Vertices.Count} openEdges={open.Count}");

        for (int idx = 0; idx < Math.Min(6, open.Count); idx++)
        {
            (int a, int b) = open[idx];
            Vec3 pa = g.Vertices[a], pb = g.Vertices[b];
            sb.AppendLine();
            sb.AppendLine($"--- OPEN EDGE #{idx}: v{a}({pa.X:F4},{pa.Y:F4},{pa.Z:F4}) -> v{b}({pb.X:F4},{pb.Y:F4},{pb.Z:F4})  ownerFaces={string.Join(",", edgeFace[(a, b)])}");

            // Every face with a vertex within 3mm of either endpoint.
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Vertices.Count < 3)
                {
                    continue;
                }

                bool near = false;
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    if (p.Sub(pa).Length() < 3e-3f || p.Sub(pb).Length() < 3e-3f)
                    {
                        near = true;
                        break;
                    }
                }

                if (!near)
                {
                    continue;
                }

                var fsb = new StringBuilder($"    face[{fi}] tex={f.Texture} flags=0x{f.Flags:X} portal={f.PortalIndexPlus2} n=({f.Plane.Normal.X:F3},{f.Plane.Normal.Y:F3},{f.Plane.Normal.Z:F3}) off={f.Plane.Offset:F3} loop=");
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    fsb.Append($"v{v.Index}({p.X:F4},{p.Y:F4},{p.Z:F4}) ");
                }

                sb.AppendLine(fsb.ToString());
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("hole_topology_dm02.txt", report);
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
