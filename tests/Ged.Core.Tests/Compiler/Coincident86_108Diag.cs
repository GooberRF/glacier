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
/// DIAGNOSTIC (item 2): the dmabrupt brush 86 vs 108 coincident wall at x=25.5. Goober's RED ground
/// truth: brush 86's texture renders; GED shows 108's. Dumps both brushes' document (time) order,
/// flags, and their +X faces at x=25.5, plus what RED's original geometry and GED's recompile carry
/// on that plane — to locate the first divergent survival decision.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class Coincident86_108Diag
{
    private readonly ITestOutputHelper _out;

    public Coincident86_108Diag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_86_108()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                red ??= g.Geometry;
            }
            else if (s.Content is BrushesSection b)
            {
                bs ??= b;
            }
            else if (s.Content is RoomEffectsSection e)
            {
                es ??= e;
            }
        }

        if (red is null || bs is null)
        {
            return;
        }

        var sb = new StringBuilder();
        var brushes = bs.Brushes.ToList();
        for (int i = 0; i < brushes.Count; i++)
        {
            Brush b = brushes[i];
            if (b.Uid is not (86 or 108))
            {
                continue;
            }

            var flags = (BrushFlags)b.Flags;
            sb.AppendLine($"brush uid={b.Uid} docIndex(time)={i} flags={flags} (raw 0x{b.Flags:X}) life={b.Life} faces={b.Geometry.Faces.Count}");
            // Faces near x=25.5 with a +/-X normal (the shared wall plane).
            for (int fi = 0; fi < b.Geometry.Faces.Count; fi++)
            {
                Face f = b.Geometry.Faces[fi];
                Vec3 n = new(f.Plane.Normal.X, f.Plane.Normal.Y, f.Plane.Normal.Z);
                if (MathF.Abs(n.X) < 0.99f)
                {
                    continue;
                }

                Vec3 c = WorldCentroid(b, f);
                if (MathF.Abs(c.X - 25.5f) > 0.6f)
                {
                    continue;
                }

                string tex = f.Texture >= 0 && f.Texture < b.Geometry.Textures.Count ? b.Geometry.Textures[f.Texture] : "(none)";
                sb.AppendLine($"    face#{fi} n=({n.X:F2},{n.Y:F2},{n.Z:F2}) worldCentroid=({c.X:F2},{c.Y:F2},{c.Z:F2}) tex={tex}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("RED original faces on x=25.5 (|nx|>0.99):");
        DumpWall(sb, red, 25.5f);

        Geometry ged = GeometryCompiler.Compile(brushes, es?.Effects, new CompileOptions { BuildSurfaces = false }).Geometry;
        sb.AppendLine();
        sb.AppendLine("GED recompile faces on x=25.5 (|nx|>0.99):");
        DumpWall(sb, ged, 25.5f);

        _out.WriteLine(sb.ToString());
        WriteArtifact("coincident_86_108.txt", sb.ToString());
    }

    private static void DumpWall(StringBuilder sb, Geometry g, float x)
    {
        var byTex = new Dictionary<string, (float Area, int Count, float NxSum)>();
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || MathF.Abs(f.Plane.Normal.X) < 0.99f)
            {
                continue;
            }

            Vec3 c = Centroid(g, f);
            if (MathF.Abs(c.X - x) > 0.3f)
            {
                continue;
            }

            string tex = f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : "(portal/none)";
            (float a, int n, float nx) = byTex.GetValueOrDefault(tex);
            byTex[tex] = (a + Area(g, f), n + 1, nx + f.Plane.Normal.X);
        }

        foreach ((string tex, (float area, int count, float nxSum)) in byTex.OrderByDescending(kv => kv.Value.Area))
        {
            sb.AppendLine($"    tex={tex} area={area:F1} faces={count} avgNx={nxSum / count:F2}");
        }
    }

    private static Vec3 WorldCentroid(Brush b, Face f)
    {
        var c = new Vec3(0, 0, 0);
        int n = 0;
        foreach (FaceVertex v in f.Vertices)
        {
            if (v.Index >= 0 && v.Index < b.Geometry.Vertices.Count)
            {
                c = c.Add(b.Geometry.Vertices[v.Index]);
                n++;
            }
        }

        if (n > 0)
        {
            c = c.Scale(1f / n);
        }

        // Local -> world: world = pos + x*Right + y*Up + z*Forward (RF's brush convention).
        Mat3 m = b.Rotation;
        return b.Position
            .Add(m.Right.Scale(c.X))
            .Add(m.Up.Scale(c.Y))
            .Add(m.Forward.Scale(c.Z));
    }

    private static Vec3 Centroid(Geometry g, Face f)
    {
        var c = new Vec3(0, 0, 0);
        int n = 0;
        foreach (FaceVertex v in f.Vertices)
        {
            if (v.Index >= 0 && v.Index < g.Vertices.Count)
            {
                c = c.Add(g.Vertices[v.Index]);
                n++;
            }
        }

        return n == 0 ? c : c.Scale(1f / n);
    }

    private static float Area(Geometry g, Face f)
    {
        Vec3 c = Centroid(g, f);
        float area = 0;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            int ia = f.Vertices[i].Index, ib = f.Vertices[(i + 1) % f.Vertices.Count].Index;
            if (ia < 0 || ia >= g.Vertices.Count || ib < 0 || ib >= g.Vertices.Count)
            {
                return area;
            }

            area += g.Vertices[ia].Sub(c).Cross(g.Vertices[ib].Sub(c)).Length() * 0.5f;
        }

        return area;
    }

    private static void WriteArtifact(string name, string text)
    {
        string? envRoot = Environment.GetEnvironmentVariable("GED_REPO_ROOT");
        var dir = envRoot is not null && Directory.Exists(envRoot) ? new DirectoryInfo(envRoot) : new DirectoryInfo(AppContext.BaseDirectory);
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
        File.WriteAllText(Path.Combine(outDir, name), text);
    }
}
