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
/// Flagship 22 — OUTPUT-STAGE MERGE measurement. Compiles a level with shipping defaults and reports the
/// face-count shape vs RED (total / liquid / portal / detail), a per-room batch-pressure estimate (face count
/// and distinct-texture group count per room — the quantity RF.exe's per-room render is sensitive to), the
/// liquid-surface shape, and the open-edge (hole) count. Pure diagnostic; no asserts. Serialised into the
/// corpus-sweep collection so its compiles do not inflate the parallel memory-proxy tests.
/// </summary>
[Collection("CorpusSweep")]
public sealed class OutputMergeDiag
{
    private readonly ITestOutputHelper _out;

    public OutputMergeDiag(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Measure(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        Geometry red = ParseInput(bytes);

        RflFile rfl = RflFile.Load(bytes);
        rfl.ParseAllKnownSections();
        var options = new CompileOptions { Alpine = rfl.Context.IsAlpine, BuildSurfaces = false, FixTJoints = true };
        CompiledLevel c = GeometryBuildService.Build(rfl, options);
        Geometry ged = c.Geometry;
        int gedHoles = HoleDetector.Detect(ged).Count;

        var sb = new StringBuilder();
        sb.AppendLine($"OUTPUT-MERGE MEASURE — {file}");
        sb.AppendLine(Summary("RED", red));
        sb.AppendLine($"{Summary("GED", ged)} holes={gedHoles} coplanarMerged={c.Report.CoplanarMerged}");
        sb.AppendLine(RoomBatch("RED", red));
        sb.AppendLine(RoomBatch("GED", ged));
        sb.AppendLine(LiquidShape("RED", red));
        sb.AppendLine(LiquidShape("GED", ged));
        _out.WriteLine(sb.ToString());

        string dir = Path.Combine(FindRepo(), "tests", "artifacts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"output_merge_{Path.GetFileNameWithoutExtension(file)}.txt"), sb.ToString());
    }

    private static string Summary(string tag, Geometry g)
    {
        int liquid = g.Faces.Count(f => (f.Flags & (ushort)FaceFlags.LiquidSurface) != 0);
        int portal = g.Faces.Count(f => f.IsPortalFace);
        int detail = g.Faces.Count(f => (f.Flags & (ushort)FaceFlags.IsDetail) != 0);
        return $"  {tag}: faces={g.Faces.Count} liquid={liquid} portal={portal} detail={detail} rooms={g.Rooms.Count} verts={g.Vertices.Count}";
    }

    private static string RoomBatch(string tag, Geometry g)
    {
        var faceCount = new int[g.Rooms.Count];
        var texGroups = new HashSet<int>[g.Rooms.Count];
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            texGroups[i] = new HashSet<int>();
        }

        foreach (Face f in g.Faces)
        {
            if (f.RoomIndex < 0 || f.RoomIndex >= g.Rooms.Count)
            {
                continue;
            }

            faceCount[f.RoomIndex]++;
            texGroups[f.RoomIndex].Add(f.Texture);
        }

        int maxFaces = faceCount.DefaultIfEmpty(0).Max();
        int maxTex = texGroups.Select(h => h.Count).DefaultIfEmpty(0).Max();
        var top = Enumerable.Range(0, g.Rooms.Count)
            .OrderByDescending(i => faceCount[i]).Take(5)
            .Select(i => $"room{i}(faces={faceCount[i]},tex={texGroups[i].Count})");
        return $"  {tag} per-room: maxFaces={maxFaces} maxTexGroups={maxTex}\n    top: {string.Join(" ", top)}";
    }

    private static string LiquidShape(string tag, Geometry g)
    {
        var liq = g.Faces.Where(f => (f.Flags & (ushort)FaceFlags.LiquidSurface) != 0).ToList();
        if (liq.Count == 0)
        {
            return $"  {tag} liquid: none";
        }

        var up = liq.Where(f => f.Plane.Normal.Y > 0.5f).ToList();
        var vcHist = up.GroupBy(f => f.Vertices.Count).OrderBy(x => x.Key).Select(x => $"{x.Key}v:{x.Count()}");
        return $"  {tag} liquid up={up.Count} vhist={string.Join(",", vcHist)}";
    }

    private static Geometry ParseInput(byte[] bytes)
    {
        RflFile rfl = RflFile.Load(bytes);
        rfl.ParseAllKnownSections();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                return gs.Geometry;
            }
        }

        return new Geometry();
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
