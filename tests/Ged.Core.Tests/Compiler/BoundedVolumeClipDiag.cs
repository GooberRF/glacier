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
/// FLAGSHIP 35 — the EXTENT-GATED brush volume clip (<see cref="CompileOptions.BoundedVolumeClip"/>). Route
/// attribution (flagship 34) proved dm04's residual seams are born in the brush VOLUME CLIP cutting foreign faces
/// by UNBOUNDED solid-BSP node planes; this measures the extent-gated form (a node plane cuts only where the
/// crossing overlaps its bounded supporting face) against the SharedBsp control. Staging discipline: storm
/// canaries FIRST (dm06 must stay 0; dm02/dm05/dm17/dm20 zeros; ctf02), then dm04 target, then the full corpus and
/// the room/portal graph. Opt-in behind GED_BVC_MEASURE=1. Writes tests/artifacts/bvc_*.txt.
/// </summary>
public sealed class BoundedVolumeClipDiag
{
    private readonly ITestOutputHelper _out;

    public BoundedVolumeClipDiag(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("GED_BVC_MEASURE") == "1";

    private static CompileOptions Shared() => new() { BuildSurfaces = false, SharedBsp = true };

    private static CompileOptions SharedBvc() => new() { BuildSurfaces = false, SharedBsp = true, BoundedVolumeClip = true };

    // Storm canaries measured across flagships 31-34 — the levels an over-cutting clip tears first.
    private static readonly string[] Canaries =
    {
        "dm06.rfl", "dm02.rfl", "dm05.rfl", "dm17.rfl", "dm20.rfl", "ctf02.rfl", "dm04.rfl",
    };

    [Fact]
    public void Canaries_First()
    {
        if (!Corpus.Available || !Enabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"BVC storm canaries. {DateTime.Now:yyyy-MM-dd HH:mm}. shared vs shared+bvc (holes/faces).");
        bool anyZeroBroken = false;
        foreach (string name in Canaries)
        {
            string path = Path.Combine(Corpus.Directory!, name);
            if (!File.Exists(path))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            (int sh, int shf, _) = Measure(brs, eff, Shared());
            (int bvc, int bvcf, double ms) = Measure(brs, eff, SharedBvc());
            string note = bvc < sh ? "BETTER" : bvc > sh ? (sh == 0 ? "ZERO-BROKEN" : "worse") : string.Empty;
            if (bvc > sh && sh == 0)
            {
                anyZeroBroken = true;
            }

            sb.AppendLine($"{name.Replace("~", string.Empty),-26} shared={sh,-4}({shf}) bvc={bvc,-4}({bvcf}) {ms,7:F0}ms {note}");
        }

        sb.AppendLine();
        sb.AppendLine(anyZeroBroken ? "RESULT: a watertight ZERO was BROKEN — revert" : "RESULT: no watertight zero broken");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("bvc_canaries.txt", report);
    }

    [Fact]
    public void FullCorpus_Shared_vs_Bvc()
    {
        if (!Corpus.Available || !Enabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"BVC full corpus. {DateTime.Now:yyyy-MM-dd HH:mm}. holes = non-detail non-liquid non-portal");
        sb.AppendLine("single-use edges. def=shipping default; shared=SharedBsp; bvc=SharedBsp+BoundedVolumeClip.");
        sb.AppendLine();
        sb.AppendLine($"{"level",-26} {"def",12} {"shared",12} {"bvc",12} {"shMs",8} {"bvcMs",8} {"note",-12}");
        sb.AppendLine(new string('-', 100));

        int better = 0, worse = 0, equal = 0, zerosBroken = 0;
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.Contains(".autosave.", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dmabruptdecayrc2a27~.rfl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (List<Brush> brs, List<RoomEffect> eff) = Load(path);
            if (brs.Count == 0)
            {
                continue;
            }

            (int def, int deff, _) = Measure(brs, eff, new CompileOptions { BuildSurfaces = false });
            (int sh, int shf, double shMs) = Measure(brs, eff, Shared());
            (int bvc, int bvcf, double bvcMs) = Measure(brs, eff, SharedBvc());

            string note;
            if (bvc < sh)
            {
                better++;
                note = "BETTER";
            }
            else if (bvc > sh)
            {
                worse++;
                note = "worse";
                if (sh == 0)
                {
                    zerosBroken++;
                    note = "ZERO-BROKEN";
                }
            }
            else
            {
                equal++;
                note = string.Empty;
            }

            sb.AppendLine($"{name.Replace("~", string.Empty),-26} {def + "(" + deff + ")",12} {sh + "(" + shf + ")",12} {bvc + "(" + bvcf + ")",12} {shMs,8:F0} {bvcMs,8:F0} {note,-12}");
        }

        sb.AppendLine();
        sb.AppendLine($"bvc vs shared: better={better} worse={worse} equal={equal} zeros-broken={zerosBroken}");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("bvc_corpus.txt", report);
    }

    [Fact]
    public void Dm04_PerSeam_Bvc()
    {
        if (!Corpus.Available || !Enabled)
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
        sb.AppendLine($"dm04 PER-SEAM: shared vs shared+bvc. {DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach ((string tag, CompileOptions o) in new[] { ("shared", Shared()), ("bvc", SharedBvc()) })
        {
            Geometry g = GeometryCompiler.Compile(brs, eff, o).Geometry;
            sb.AppendLine();
            sb.AppendLine($"=== {tag}: faces={g.Faces.Count} ===");
            sb.Append(PerSeam(g));
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("bvc_dm04_perseam.txt", report);
    }

    [Fact]
    public void RoomsPortals_Bvc_vs_Shared()
    {
        if (!Corpus.Available || !Enabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"BVC rooms/portals: shared vs shared+bvc. {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("The room/portal graph is byte-exact today (ctf07 158/97, dm04 24/10) and must HOLD.");
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
            Geometry gs = GeometryCompiler.Compile(brs, eff, Shared()).Geometry;
            Geometry gb = GeometryCompiler.Compile(brs, eff, SharedBvc()).Geometry;
            bool held = gs.Rooms.Count == gb.Rooms.Count && gs.Portals.Count == gb.Portals.Count;
            if (!held)
            {
                anyDisturbed = true;
            }

            sb.AppendLine($"{name.Replace("~", string.Empty),-26} shared rooms={gs.Rooms.Count} portals={gs.Portals.Count} | bvc rooms={gb.Rooms.Count} portals={gb.Portals.Count}{(held ? string.Empty : "  <== DISTURBED")}");
        }

        sb.AppendLine();
        sb.AppendLine(anyDisturbed ? "RESULT: at least one graph DISTURBED — revert" : "RESULT: all room/portal graphs HELD");
        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("bvc_rooms.txt", report);
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
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || f.PortalIndexPlus2 >= 2 ||
                ((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
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

    // Lists the open-edge seams with midpoint coordinate + length, so a per-seam closure (A-D slivers gone,
    // F/G seam whole) can be read directly against dm04_redoutput_perseam.txt.
    private static string PerSeam(Geometry g)
    {
        var count = new Dictionary<(int, int), int>();
        var owner = new Dictionary<(int, int), int>();
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            if (f.Texture < 0 || f.PortalIndexPlus2 >= 2 ||
                ((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
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
        foreach ((int ia, int ib) in open.OrderBy(k => k.Item1))
        {
            Vec3 pa = g.Vertices[ia], pb = g.Vertices[ib];
            Vec3 mid = Vec3Math.Lerp(pa, pb, 0.5f);
            Vec3 on = g.Faces[owner[(ia, ib)]].Plane.Normal;
            sb.AppendLine($"  mid=({mid.X:F3},{mid.Y:F3},{mid.Z:F3}) len={pb.Sub(pa).Length() * 1000:F1}mm owner n=({on.X:F3},{on.Y:F3},{on.Z:F3})");
        }

        sb.AppendLine($"  total open edges: {open.Count}");
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
        File.WriteAllText(Path.Combine(outDir, file), content);
    }
}
