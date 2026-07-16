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
/// Seam trace: locate exactly where the residual-6 dm04 phantom cut vertices are born in the SharedBsp
/// fold. Hooks CsgSharedSplit.SeamTrace and captures every interned cut within a small radius of the
/// phantom coordinates (#1267 cluster-3, #240 cluster-1, #377 cluster-2). Set GED_SEAMTRACE=1 to run.
/// </summary>
public sealed class SeamTraceDiag
{
    private readonly ITestOutputHelper _out;

    public SeamTraceDiag(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("GED_SEAMTRACE") == "1";

    private static readonly (string Name, Vec3 P)[] Targets =
    {
        ("c3_1267", new Vec3(-17.027f, -60.059f, -5.058f)),
        ("c3_1700", new Vec3(-17.025f, -60.064f, -5.059f)),
        ("c1_240", new Vec3(-37.726f, -65.159f, -9.936f)),
        ("c1_236", new Vec3(-36.522f, -65.156f, -9.754f)),
        ("c2_377", new Vec3(-11.864f, -65.160f, 33.840f)),
        ("c2_376", new Vec3(-12.353f, -65.229f, 34.057f)),
    };

    [Fact]
    public void Trace_Phantom_Cut_Births()
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

        List<Brush> brs = bs!.Brushes.ToList();
        List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();

        var hits = new List<string>();
        var gate = new object();
        CsgSharedSplit.SeamTrace = (reg, pos, fp, ep, cut, av, bv, rv, t, kind) =>
        {
            foreach ((string name, Vec3 p) in Targets)
            {
                if (pos.Sub(p).Length() < 0.006f)
                {
                    string fpg = PlaneStr(reg, fp), epg = PlaneStr(reg, ep), cutg = PlaneStr(reg, cut);
                    lock (gate)
                    {
                        hits.Add($"{name} route={kind} pos=({pos.X:F5},{pos.Y:F5},{pos.Z:F5}) face={fp}{fpg} edge={ep}{epg} cut={cut}{cutg} aVid={av} bVid={bv} => vid={rv} t={t:F4}");
                    }

                    break;
                }
            }
        };

        try
        {
            GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, SharedBsp = true });
        }
        finally
        {
            CsgSharedSplit.SeamTrace = null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"seam-trace. {hits.Count} phantom cut births. {DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach (string h in hits)
        {
            sb.AppendLine(h);
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("seamtrace.txt", report);
    }

    private static string PlaneStr(PlaneRegistry reg, int id)
    {
        if (id < 0 || !reg.TryGetPlane(id, out CsgPlane pl))
        {
            return "[-]";
        }

        Vec3 n = pl.Normal;
        return $"[n=({n.X:F3},{n.Y:F3},{n.Z:F3}) o={pl.Offset:F3}]";
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
