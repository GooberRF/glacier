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
/// Flagship 22 TARGET 2 / flagship 23B — dm04 cluster A, now CLOSED. Was a residual open floor patch at world
/// (~31.7, −60.18, 1.7): four unpartnered edges bounding a ~2.8×1.6 m quad on the y=−60.18 plane; now 0.
/// <para>
/// TRUE ROOT CAUSE (fold instrumentation trace, flagship 23B — corrects the flagship-22 hand-trace): the patch
/// sits at a terrain junction of registry-folded near-coincident floors — solid <c>uid=11</c>/<c>uid=16</c> at
/// y=−60.181 and the air terrain <c>uid=14</c> at y=−60.180 (1 mm apart, one folded plane id at the 2 mm
/// OffsetTol). In time order: uid=11's solid floor reads BURIED near the patch when added (no air above yet) and
/// is dropped; uid=14's air floor fills in; then uid=16's solid floor (later) DISSOLVES the coincident air floor
/// via the covered-branch survival table (class 4, solid beats air — CORRECT), but uid=16's OWN replacement floor
/// is never emitted there because the fold's step-(b) room-above probe (ray-parity over the 1 mm-coincident bumpy
/// terrain) misreads the open room as not-open ⇒ buried ⇒ dropped. Air floor gone + solid cap not emitted ⇒ the
/// open quad. So the coincidence VERDICT is right; the hole is the winner's replacement failing to emit.
/// </para>
/// <para>
/// FIX (flagship 23B, <c>CompileOptions.RegionWiseCoincidence</c>): where the coincident WINNER is a solid
/// replacing a world AIR wall, keep the resolved region IN PLACE carrying the winner face's attributes (RED's
/// coincident winner survives over the overlap), instead of dropping and relying on the fold's step-(b)
/// ray-parity probe. Cluster A: 4 open edges → 0, dm04 total 13 → 9, no corpus regression. Held at ≤2.
/// </para>
/// </summary>
public sealed class Dm04ClusterADiag
{
    private readonly ITestOutputHelper _out;

    public Dm04ClusterADiag(ITestOutputHelper output) => _out = output;

    // Patch AABB (world): the four open-edge corners span x[30.7,33.4] z[1.3,2.9] at y≈−60.18.
    private const float X0 = 29f, X1 = 35f, Z0 = -1f, Z1 = 5f, Ylo = -60.6f, Yhi = -59.8f;
    private static readonly Vec3 Patch = new(31.7f, -60.18f, 1.7f);

    [Fact]
    public void Trace_Cluster_A()
    {
        if (!Corpus.Available)
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

        Assert.NotNull(bs);
        List<Brush> brushes = bs!.Brushes.ToList();
        List<RoomEffect> effects = es?.Effects.ToList() ?? new List<RoomEffect>();

        var sb = new StringBuilder();
        sb.AppendLine("dm04 CLUSTER A trace — patch ~(31.7,-60.18,1.7)");
        sb.AppendLine();

        // ---- (1) SOURCE brush faces near the patch: any face whose AABB overlaps the patch region. ----
        sb.AppendLine("== SOURCE brush faces overlapping the patch region (x[29,35] z[-1,5] y[-60.6,-59.8]) ==");
        foreach (Brush b in brushes)
        {
            List<CsgFace> wf = BrushWorld.ToWorldFaces(b, 0, out _);
            foreach (CsgFace f in wf)
            {
                float mnx = float.MaxValue, mxx = float.MinValue, mny = float.MaxValue, mxy = float.MinValue, mnz = float.MaxValue, mxz = float.MinValue;
                foreach (CsgVertex v in f.Vertices)
                {
                    mnx = MathF.Min(mnx, v.Position.X); mxx = MathF.Max(mxx, v.Position.X);
                    mny = MathF.Min(mny, v.Position.Y); mxy = MathF.Max(mxy, v.Position.Y);
                    mnz = MathF.Min(mnz, v.Position.Z); mxz = MathF.Max(mxz, v.Position.Z);
                }

                bool overlaps = mxx > X0 && mnx < X1 && mxz > Z0 && mnz < Z1 && mxy > Ylo && mny < Yhi;
                bool horizontal = MathF.Abs(f.Plane.Normal.Y) > 0.85f;
                if (overlaps && horizontal)
                {
                    var flags = (BrushFlags)b.Flags;
                    sb.AppendLine($"  uid={b.Uid} time?={brushes.IndexOf(b)} {flags} n=({f.Plane.Normal.X:F3},{f.Plane.Normal.Y:F3},{f.Plane.Normal.Z:F3}) off={f.Plane.Offset:F3} y[{mny:F3},{mxy:F3}] x[{mnx:F2},{mxx:F2}] z[{mnz:F2},{mxz:F2}] tex={f.Texture}");
                }
            }
        }

        // ---- (2) Compiled output floor faces near the patch (default path). ----
        CompiledLevel c = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false });
        Geometry g = c.Geometry;
        sb.AppendLine();
        sb.AppendLine($"== default path: incUsed={c.Report.IncrementalUsed} rooms={g.Rooms.Count} faces={g.Faces.Count} ==");
        sb.AppendLine("== OUTPUT horizontal faces near patch ==");
        for (int i = 0; i < g.Faces.Count; i++)
        {
            Face f = g.Faces[i];
            if (f.Vertices.Count < 3 || MathF.Abs(f.Plane.Normal.Y) < 0.85f)
            {
                continue;
            }

            float mnx = float.MaxValue, mxx = float.MinValue, mny = float.MaxValue, mxy = float.MinValue, mnz = float.MaxValue, mxz = float.MinValue;
            foreach (FaceVertex v in f.Vertices)
            {
                Vec3 p = g.Vertices[v.Index];
                mnx = MathF.Min(mnx, p.X); mxx = MathF.Max(mxx, p.X);
                mny = MathF.Min(mny, p.Y); mxy = MathF.Max(mxy, p.Y);
                mnz = MathF.Min(mnz, p.Z); mxz = MathF.Max(mxz, p.Z);
            }

            if (mxx > X0 && mnx < X1 && mxz > Z0 && mnz < Z1 && mxy > Ylo && mny < Yhi)
            {
                var fsb = new StringBuilder($"  face[{i}] n=({f.Plane.Normal.X:F3},{f.Plane.Normal.Y:F3},{f.Plane.Normal.Z:F3}) off={f.Plane.Offset:F3} room={f.RoomIndex} flags=0x{f.Flags:X} verts=");
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    fsb.Append($"({p.X:F2},{p.Y:F2},{p.Z:F2}) ");
                }

                sb.AppendLine(fsb.ToString());
            }
        }

        // ---- (3) Holes near the patch. ----
        List<Vec3> holes = HoleDetector.Detect(g);
        var near = holes.Where(h => h.Sub(Patch).LengthSquared() < 9f).ToList();
        sb.AppendLine();
        sb.AppendLine($"== holes total={holes.Count}, within 3 m of patch={near.Count} ==");
        foreach (Vec3 h in near.Take(20))
        {
            sb.AppendLine($"  HOLE ({h.X:F3},{h.Y:F3},{h.Z:F3})");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("dm04_clusterA_trace.txt", report);

        // PIN the locus: cluster A is now CLOSED (flagship 23B — region-wise coincidence + winner-in-place;
        // the coincident air floor's dissolve is correct, but the winning solid's replacement floor is kept
        // IN PLACE over the resolved region rather than relying on the fold's ray-parity room-above probe,
        // which misread the 1 mm-coincident bumpy terrain as not-open). Was 4 open edges, now 0. Held at ≤2
        // (the watertight-region convention) so any reopening of the −60.18 floor patch trips this gate.
        Assert.True(near.Count <= 2,
            $"dm04 cluster A regressed: {near.Count} open edges within 3 m of {Patch} (closed floor 0, ceiling 2)");
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
