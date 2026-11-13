using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// Deformed slab pieces through the compiler (ORGANIC-GEOMETRY-SPEC §2.1 / W4): twisted, tapered and bent
/// columns with pieces 1, 4 and 12 as structural solids in a room, plus a twisted structural AIR shaft
/// whose pieces abut on bit-identical faces. SEALED builds under both build methods must be watertight,
/// lint clean, one undo node with an exact undo; no two pieces may form a near-coplanar face pair that
/// is not simply two neighbours meeting along a shared edge (§4.2 band).
/// </summary>
public sealed class GeoDeformCompileTests
{
    private const string Room = "level.brush{shape = 'box', pos = {0, 5, 0}, size = {40, 10, 40}, air = true}\n";

    private static string Col(double x, double z) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"level.brush{{shape = 'box', pos = {{{x}, 2.5, {z}}}, size = {{1.5, 5, 1.5}}}}");

    [Fact]
    public void Twisted_Tapered_And_Bent_Columns_In_A_Room_Are_Watertight()
    {
        KitRun run = Run(
            Room +
            "geo.twist{brushes = " + Col(-12, -12) + ", degrees = 90}\n" +
            "geo.twist{brushes = " + Col(-6, -12) + ", degrees = 90, pieces = 4}\n" +
            "geo.twist{brushes = " + Col(0, -12) + ", degrees = 90, pieces = 12}\n" +
            "geo.taper{brushes = " + Col(6, -12) + ", scale = 0.4, pieces = 4}\n" +
            "geo.taper{brushes = " + Col(12, -12) + ", scale = 1.6, pieces = 12}\n" +
            "geo.bend{brushes = " + Col(-8, 8) + ", axis = 'y', toward = 'x', degrees = 40, pieces = 4}\n" +
            "geo.jitter{brushes = " + Col(8, 8) + ", amount = {0.2, 0, 0.2}, seed = 2}\n");
        Assert.DoesNotContain(run.Log.Entries, e => e.Level == Ged.Core.Scripting.ScriptLogLevel.Warning);
        List<Brush> generated = new BrushEditor(run.Doc).Brushes.Skip(1).ToList();
        Assert.Equal(1 + 4 + 12 + 4 + 12 + 4 + 1, generated.Count);
        generated.ForEach(ScriptGeoEditTests.AssertHull);
        Assert.Empty(NearCoplanarConflicts(generated));
        AssertAccepted(run);
    }

    [Fact]
    public void A_Twisted_Air_Shaft_Of_Abutting_Pieces_Is_Watertight()
    {
        KitRun run = Run(
            Room +
            "local shaft = level.brush{shape = 'box', pos = {0, 12, 0}, size = {3, 6, 3}, air = true}\n" +
            "local q = geo.twist{brushes = shaft, degrees = 60, pieces = 4}\n" +
            "assert(q.count == 4)");
        List<Brush> pieces = new BrushEditor(run.Doc).Brushes.Skip(1).ToList();
        pieces.ForEach(ScriptGeoEditTests.AssertHull);
        Assert.Empty(NearCoplanarConflicts(pieces));
        AssertAccepted(run);
    }

    [Fact]
    public void Random_Hulls_Twisted_Tapered_And_Bent_Stay_Valid_And_Their_Caps_Are_Exact()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var rng = new OrganicRandom(77, seed);
            var pts = new List<Vec3d>();
            int n = 8 + (int)(rng.NextDouble() * 12);
            for (int i = 0; i < n; i++)
            {
                pts.Add(new Vec3d(rng.Range(-3, 3), rng.Range(0, 8), rng.Range(-3, 3)));
            }

            Assert.True(HullBrush.TryBuild(pts, new HullBrushParams(), 1, out Brush? b, out _).Success);
            List<Vec3d> world = HullDeform.WorldVertices(b!);
            (Vec3d min, Vec3d max) = HullDeform.WorldBounds(world);
            var centre = new Vec3d((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            foreach (DeformMath def in new[]
            {
                DeformMath.Twist(1, min.Y, max.Y - min.Y, centre, 120),
                DeformMath.Taper(1, min.Y, max.Y - min.Y, centre, 0.3),
                DeformMath.Bend(1, 0, min.Y, max.Y - min.Y, centre, 40),
            })
            {
                List<List<Vec3d>>? slabs = HullDeform.Slice(b!, def, 2 + (seed % 9), out List<GridPlane> joints, out GeoEditError? error);
                Assert.True(slabs is not null, error?.Message);
                var built = new List<Brush>();
                foreach (List<Vec3d> slab in slabs!)
                {
                    Brush piece = Ged.Core.Editing.GeometryClone.Deep(b!);
                    OpResult r = HullBrush.TryRebuild(piece, slab, 4, HullBrush.Grid);
                    Assert.True(r.Success, $"seed {seed}: {r.Message}");
                    ScriptGeoEditTests.AssertHull(piece);
                    built.Add(piece);
                }

                Assert.Equal(built.Count - 1, joints.Count);
                for (int k = 0; k + 1 < built.Count; k++)
                {
                    GridPlane joint = joints[k];
                    var a = HullDeform.WorldVertices(built[k]).Where(p => joint.Side(p) == 0).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
                    var c = HullDeform.WorldVertices(built[k + 1]).Where(p => joint.Side(p) == 0).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
                    Assert.True(a.Count >= 3 && a.SequenceEqual(c), $"seed {seed} {def.Kind}: pieces {k + 1}/{k + 2} caps differ on joint {joint}: [{string.Join(" ", a)}] vs [{string.Join(" ", c)}]");
                }
            }
        }
    }

    public static IEnumerable<object[]> BendSweepFast() =>
        new[] { (1, 2), (5, 16), (10, 16), (20, 32), (90, 32), (-40, 4) }.Select(c => new object[] { c.Item1, c.Item2 });

    public static IEnumerable<object[]> BendSweepDeep() =>
        new[] { 1, 2, 4, 5, 10, 20, 40, 90, 135, -15, -60 }.SelectMany(d => new[] { 2, 3, 4, 8, 12, 16, 24, 32 }.Select(n => new object[] { d, n }));

    /// <summary>A structural column bent by <paramref name="degrees"/> in <paramref name="pieces"/> pieces in an air room: every
    /// pair of neighbouring pieces abuts on one identical cap, and sealed builds under both methods are watertight; a split
    /// finer than the near-flat fold limit (under 0.5° per piece) is refused with the largest piece count that fits.</summary>
    [Theory]
    [MemberData(nameof(BendSweepFast))]
    public void A_Bent_Column_Abuts_Exactly_And_Is_Watertight(int degrees, int pieces) => BendCase(degrees, pieces);

    [Theory]
    [Trait("Category", "DeepGate")]
    [MemberData(nameof(BendSweepDeep))]
    public void A_Bent_Column_Abuts_Exactly_And_Is_Watertight_Sweep(int degrees, int pieces) => BendCase(degrees, pieces);

    private static void BendCase(int degrees, int pieces)
    {
        string script = Room + $"geo.bend{{brushes = {Col(0, 0)}, axis = 'y', toward = 'x', degrees = {degrees}, pieces = {pieces}}}";
        if (Math.Abs(degrees) / (double)pieces < HullAudit.FoldDegrees)
        {
            // Pieces that would fold by less than the near-flat limit are refused, naming the most that fit.
            ScriptDiagnostic e = Fail(script);
            Assert.Contains("turns each piece by only", e.Message, StringComparison.Ordinal);
            Assert.Contains($"Use pieces = {(int)Math.Floor(Math.Abs(degrees) / HullAudit.FoldDegrees)} or fewer", e.Hint ?? string.Empty, StringComparison.Ordinal);
            return;
        }

        KitRun run = Run(script);
        Assert.DoesNotContain(run.Log.Entries, e => e.Level == Ged.Core.Scripting.ScriptLogLevel.Warning);
        List<Brush> generated = new BrushEditor(run.Doc).Brushes.Skip(1).ToList();
        Assert.Equal(pieces, generated.Count);
        generated.ForEach(ScriptGeoEditTests.AssertHull);
        for (int k = 0; k + 1 < generated.Count; k++)
        {
            var a = HullDeform.WorldVertices(generated[k]).ToHashSet();
            int shared = HullDeform.WorldVertices(generated[k + 1]).Count(a.Contains);
            AuditShape cap = AuditShape.FromBrush(generated[k], "a");
            Assert.True(shared >= 3 && cap.Faces.Any(f => f.Points.Length == shared && f.Points.All(a.Contains) && f.Points.All(p => HullDeform.WorldVertices(generated[k + 1]).Contains(p))),
                $"pieces {k + 1}/{k + 2} share {shared} corners but no whole face");
        }

        Assert.Empty(NearCoplanarConflicts(generated));
        AssertAccepted(run);
    }

    /// <summary>
    /// Face pairs of different brushes in the §4.2 near-coplanar band (normals within 0.5°, largest vertex
    /// distance between 1e-6 and 5 mm) that are NOT neighbours meeting along a shared edge (two common
    /// world vertices) — the configuration that creates duplicate coplanar coverage.
    /// </summary>
    internal static List<string> NearCoplanarConflicts(IReadOnlyList<Brush> brushes)
    {
        var faces = brushes.SelectMany(b => b.Geometry.Faces.Select(f =>
        {
            var pts = f.Vertices.Select(v => HullDeform.World(b, b.Geometry.Vertices[v.Index])).ToList();
            (Vec3d n, Vec3d c) = ConvexHull3.NewellPlane(pts, Enumerable.Range(0, pts.Count).ToArray());
            return (Uid: b.Uid, Pts: pts, N: n, C: c);
        })).ToList();

        double cos = Math.Cos(0.5 * Math.PI / 180);
        var conflicts = new List<string>();
        for (int i = 0; i < faces.Count; i++)
        {
            for (int j = i + 1; j < faces.Count; j++)
            {
                var a = faces[i];
                var b = faces[j];
                if (a.Uid == b.Uid || Math.Abs(a.N.Dot(b.N)) < cos)
                {
                    continue;
                }

                double delta = Math.Max(a.Pts.Max(p => Math.Abs(b.N.Dot(p.Sub(b.C)))), b.Pts.Max(p => Math.Abs(a.N.Dot(p.Sub(a.C)))));
                if (delta <= 1e-6 || delta >= 0.005 || a.Pts.Count(p => b.Pts.Contains(p)) >= 2 || !Overlap(a.Pts, b.Pts, a.N))
                {
                    continue;
                }

                conflicts.Add($"brush {a.Uid} / {b.Uid}: Δ = {delta:0.#######} m");
            }
        }

        return conflicts;
    }

    /// <summary>True when two convex faces, projected along their (shared) dominant normal axis, overlap with positive area (separating-axis test).</summary>
    private static bool Overlap(List<Vec3d> a, List<Vec3d> b, Vec3d n)
    {
        int drop = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
        (int u, int w) = drop == 0 ? (1, 2) : drop == 1 ? (2, 0) : (0, 1);
        foreach (List<Vec3d> poly in new[] { a, b })
        {
            for (int i = 0; i < poly.Count; i++)
            {
                Vec3d p = poly[i], q = poly[(i + 1) % poly.Count];
                double ax = -(q[w] - p[w]), ay = q[u] - p[u];
                double len = Math.Sqrt((ax * ax) + (ay * ay));
                if (len < 1e-12)
                {
                    continue;
                }

                (double lo1, double hi1) = Project(a, ax / len, ay / len, u, w);
                (double lo2, double hi2) = Project(b, ax / len, ay / len, u, w);
                if (hi1 <= lo2 + 1e-6 || hi2 <= lo1 + 1e-6)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (double, double) Project(List<Vec3d> poly, double ax, double ay, int u, int w)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (Vec3d p in poly)
        {
            double d = (p[u] * ax) + (p[w] * ay);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }

        return (lo, hi);
    }
}
