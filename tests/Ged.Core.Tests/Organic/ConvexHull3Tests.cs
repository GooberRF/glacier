using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing.Organic;
using Xunit;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// The exact convex hull: canonical output, exact handling of coplanar / collinear / interior points,
/// coplanar merge and fan split, sliver simplification, and the flat-input failure.
/// </summary>
public sealed class ConvexHull3Tests
{
    private static Vec3d P(double x, double y, double z) => new(x, y, z);

    private static List<Vec3d> Cube(double s = 2) => new()
    {
        P(0, 0, 0), P(s, 0, 0), P(0, s, 0), P(s, s, 0), P(0, 0, s), P(s, 0, s), P(0, s, s), P(s, s, s),
    };

    private static HullResult Hull(IReadOnlyList<Vec3d> pts, HullOptions? o = null)
    {
        HullResult? r = ConvexHull3.Build(pts, o ?? new HullOptions(), out string? error);
        Assert.True(r is not null, error);
        return r!;
    }

    /// <summary>The faces as sets of corner positions (independent of indices and corner rotation).</summary>
    private static HashSet<string> FaceSet(HullResult r) =>
        r.Faces.Select(f => string.Join(";", f.Corners.Select(c => r.Vertices[c].ToString()).OrderBy(s => s, StringComparer.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Structural invariants every hull must satisfy, checked exactly on grid inputs.</summary>
    private static void AssertValid(HullResult r, IReadOnlyList<Vec3d> input, double tol = 1e-9)
    {
        // Closed, consistently wound (every edge once per direction), Euler characteristic 2.
        var edges = new HashSet<(int, int)>();
        foreach (HullFace f in r.Faces)
        {
            Assert.True(f.Corners.Length >= 3);
            Assert.True(f.Corners.Length <= 16);
            Assert.Equal(f.Corners.Min(), f.Corners[0]);
            for (int i = 0; i < f.Corners.Length; i++)
            {
                Assert.True(edges.Add((f.Corners[i], f.Corners[(i + 1) % f.Corners.Length])), "an edge is used twice in one direction");
            }
        }

        Assert.All(edges, e => Assert.Contains((e.Item2, e.Item1), edges));

        // Every input point is behind or on every face plane; every corner is on its plane (exact on the grid).
        foreach (HullFace f in r.Faces)
        {
            Assert.InRange(Math.Abs(f.Normal.Length - 1), 0, 1e-12);
            foreach (int c in f.Corners)
            {
                Assert.InRange(Math.Abs(f.Normal.Dot(r.Vertices[c]) + f.Offset), 0, tol);
            }

            foreach (Vec3d p in input)
            {
                Assert.True(f.Normal.Dot(p) + f.Offset <= tol, $"point {p} is in front of a face");
            }
        }

        // Every hull vertex is a true corner: it lies on at least three faces with independent normals.
        for (int v = 0; v < r.Vertices.Count; v++)
        {
            var normals = r.Faces.Where(f => f.Corners.Contains(v)).Select(f => f.Normal).ToList();
            bool independent = false;
            for (int a = 0; a < normals.Count && !independent; a++)
            {
                for (int b = a + 1; b < normals.Count && !independent; b++)
                {
                    for (int c = b + 1; c < normals.Count && !independent; c++)
                    {
                        independent = Math.Abs(normals[a].Cross(normals[b]).Dot(normals[c])) > 1e-9;
                    }
                }
            }

            Assert.True(independent, $"vertex {r.Vertices[v]} is not a corner of the hull");
        }

        // Canonical order: vertices by ascending source index; faces sorted by corner sequence.
        Assert.Equal(r.SourceIndex.OrderBy(i => i), r.SourceIndex);
        for (int i = 1; i < r.Faces.Count; i++)
        {
            Assert.True(Compare(r.Faces[i - 1].Corners, r.Faces[i].Corners) < 0);
        }
    }

    private static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    [Fact]
    public void Cube_Has_Six_Quads()
    {
        HullResult r = Hull(Cube());
        Assert.Equal(8, r.Vertices.Count);
        Assert.Equal(6, r.Faces.Count);
        Assert.All(r.Faces, f => Assert.Equal(4, f.Corners.Length));
        Assert.All(r.Faces, f => Assert.Equal(1, Math.Abs(f.Normal.X) + Math.Abs(f.Normal.Y) + Math.Abs(f.Normal.Z)));
        Assert.Equal(8, r.Volume, 12);
        Assert.Equal(0, r.InteriorPoints);
        Assert.Equal(0, r.Simplified);
        AssertValid(r, Cube());
    }

    [Fact]
    public void Interior_Face_And_Edge_Points_Are_Not_Corners()
    {
        List<Vec3d> pts = Cube();
        pts.Add(P(1, 1, 1)); // interior
        HullResult r = Hull(pts);
        Assert.Equal(1, r.InteriorPoints);
        Assert.Equal(8, r.Vertices.Count);

        // A face centre, an edge midpoint and a duplicate are inside-or-on too — inserted FIRST, so the
        // builder must also remove points that later points make non-extreme.
        var tricky = new List<Vec3d> { P(1, 0, 1), P(1, 0, 0), P(0, 0, 0) };
        tricky.AddRange(Cube());
        HullResult t = Hull(tricky);
        Assert.Equal(8, t.Vertices.Count);
        Assert.Equal(6, t.Faces.Count);
        Assert.Equal(3, t.InteriorPoints);
        AssertValid(t, tricky);
    }

    [Fact]
    public void Collinear_Extensions_Drop_Middle_Points()
    {
        var pts = new List<Vec3d> { P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(0, 0, 1), P(2, 0, 0), P(3, 0, 0), P(0, 2, 0), P(0, 0, 3) };
        HullResult r = Hull(pts);
        Assert.Equal(4, r.Vertices.Count);
        Assert.Equal(4, r.Faces.Count);
        Assert.Equal(new[] { 0, 5, 6, 7 }, r.SourceIndex);
        AssertValid(r, pts);
    }

    [Fact]
    public void A_Shuffled_Lattice_Hulls_To_Its_Bounding_Cube()
    {
        var pts = new List<Vec3d>();
        for (int x = 0; x < 5; x++)
        {
            for (int y = 0; y < 5; y++)
            {
                for (int z = 0; z < 5; z++)
                {
                    pts.Add(P(x, y, z));
                }
            }
        }

        var rng = new OrganicRandom(7);
        List<Vec3d> shuffled = pts.OrderBy(_ => rng.NextUInt64()).ToList();
        HullResult r = Hull(shuffled);
        Assert.Equal(8, r.Vertices.Count);
        Assert.Equal(6, r.Faces.Count);
        Assert.Equal(125 - 8, r.InteriorPoints);
        AssertValid(r, shuffled);
    }

    [Fact]
    public void Degenerate_Lattice_Clouds_Are_Always_Valid()
    {
        // Small integer lattices are full of coplanar and collinear quadruples: the exact predicates must
        // produce a valid canonical hull every time, independent of the insertion order.
        for (int seed = 1; seed <= 300; seed++)
        {
            var rng = new OrganicRandom(seed, 99);
            int n = 6 + (int)(rng.NextDouble() * 30);
            var pts = new List<Vec3d>();
            for (int i = 0; i < n; i++)
            {
                pts.Add(P(Math.Floor(rng.Range(0, 4)), Math.Floor(rng.Range(0, 4)), Math.Floor(rng.Range(0, 4))));
            }

            HullResult? r = ConvexHull3.Build(pts, new HullOptions(), out string? error);
            if (r is null)
            {
                Assert.True(error!.Contains("coplanar", StringComparison.Ordinal) || error.Contains("distinct", StringComparison.Ordinal), $"seed {seed}: {error}");
                continue;
            }

            AssertValid(r, pts);
            List<Vec3d> reversed = Enumerable.Reverse(pts).ToList();
            HullResult back = Hull(reversed);
            Assert.Equal(FaceSet(r), FaceSet(back));
            Assert.Equal(r.Volume, back.Volume, 12);
        }
    }

    [Fact]
    public void Seventeen_Gon_Prism_Caps_Split_Sixteen_Plus_Three()
    {
        var pts = new List<Vec3d>();
        for (int i = 0; i < 17; i++)
        {
            double a = 2 * Math.PI * i / 17;
            double x = Math.Round(10 * Math.Cos(a) * 1024) / 1024, z = Math.Round(10 * Math.Sin(a) * 1024) / 1024;
            pts.Add(P(x, 0, z));
            pts.Add(P(x, 3, z));
        }

        HullResult r = Hull(pts);
        Assert.Equal(34, r.Vertices.Count);
        var caps = r.Faces.Where(f => Math.Abs(f.Normal.Y) == 1).ToList();
        Assert.Equal(4, caps.Count);
        Assert.Equal(new[] { 3, 3, 16, 16 }, caps.Select(f => f.Corners.Length).OrderBy(n => n));
        Assert.Equal(17, r.Faces.Count(f => f.Normal.Y == 0));
        AssertValid(r, pts);
    }

    [Fact]
    public void Permuting_The_Input_Keeps_The_Face_Set()
    {
        var rng = new OrganicRandom(3);
        var pts = Enumerable.Range(0, 30).Select(_ => P(Math.Round(rng.Range(-20, 20) * 1024) / 1024,
            Math.Round(rng.Range(-20, 20) * 1024) / 1024, Math.Round(rng.Range(-20, 20) * 1024) / 1024)).ToList();
        HullResult a = Hull(pts);
        for (int k = 0; k < 5; k++)
        {
            var perm = new OrganicRandom(k + 10);
            HullResult b = Hull(pts.OrderBy(_ => perm.NextUInt64()).ToList());
            Assert.Equal(FaceSet(a), FaceSet(b));
            Assert.Equal(a.Volume, b.Volume, 12);
            Assert.Equal(a.InteriorPoints, b.InteriorPoints);
        }
    }

    [Fact]
    public void Same_Input_Gives_Identical_Output()
    {
        var rng = new OrganicRandom(5);
        var pts = Enumerable.Range(0, 25).Select(_ => P(Math.Round(rng.Range(-8, 8) * 1024) / 1024,
            Math.Round(rng.Range(-8, 8) * 1024) / 1024, Math.Round(rng.Range(-8, 8) * 1024) / 1024)).ToList();
        HullResult a = Hull(pts), b = Hull(pts);
        Assert.Equal(a.Vertices, b.Vertices);
        Assert.Equal(a.SourceIndex, b.SourceIndex);
        Assert.Equal(a.Faces.Count, b.Faces.Count);
        for (int i = 0; i < a.Faces.Count; i++)
        {
            Assert.Equal(a.Faces[i].Corners, b.Faces[i].Corners);
            Assert.Equal(a.Faces[i].Normal, b.Faces[i].Normal);
            Assert.Equal(a.Faces[i].Offset, b.Faces[i].Offset);
        }
    }

    /// <summary>A 2 m box whose +X/+Y edge is chamfered by one grid step: the chamfer face is a ~1.4 mm sliver.</summary>
    private static List<Vec3d> ChamferedBox()
    {
        const double d = 1.0 / 1024;
        return new List<Vec3d>
        {
            P(0, 0, 0), P(2, 0, 0), P(0, 2, 0), P(0, 0, 2), P(2, 0, 2), P(0, 2, 2),
            P(2, 2 - d, 0), P(2 - d, 2, 0), P(2, 2 - d, 2), P(2 - d, 2, 2),
        };
    }

    [Fact]
    public void A_One_Millimetre_Sliver_Is_Simplified()
    {
        HullResult r = Hull(ChamferedBox());
        Assert.True(r.Simplified >= 1);
        Assert.Equal(r.Simplified, r.SimplifiedSources.Count);
        Assert.Equal(10, r.Vertices.Count + r.Simplified + r.InteriorPoints);
        Assert.All(r.Faces, f => Assert.True(MinAltitude(r, f) >= 0.002, $"face altitude {MinAltitude(r, f)}"));
        AssertValid(r, r.Vertices);
    }

    private static double MinAltitude(HullResult r, HullFace f)
    {
        double best = double.PositiveInfinity;
        for (int e = 0; e < f.Corners.Length; e++)
        {
            Vec3d a = r.Vertices[f.Corners[e]], dir = r.Vertices[f.Corners[(e + 1) % f.Corners.Length]].Sub(a);
            best = Math.Min(best, f.Corners.Max(c => r.Vertices[c].Sub(a).Cross(dir).Length / dir.Length));
        }

        return best;
    }

    [Fact]
    public void A_Sliver_Is_An_Error_Without_Simplify()
    {
        HullResult? r = ConvexHull3.Build(ChamferedBox(), new HullOptions { Simplify = false }, out string? error);
        Assert.Null(r);
        Assert.NotNull(error);
        Assert.Contains("sliver", error, StringComparison.Ordinal);
        Assert.Matches(@"points \d+, \d+ and \d+ make a sliver face", error);
    }

    [Fact]
    public void Coplanar_Input_Fails()
    {
        HullResult? flat = ConvexHull3.Build(new[] { P(0, 0, 0), P(4, 0, 0), P(0, 0, 4), P(4, 0, 4), P(2, 0, 2) }, new HullOptions(), out string? e1);
        Assert.Null(flat);
        Assert.Contains("coplanar", e1, StringComparison.Ordinal);

        // A tilted plane (x + y + z = 3): every AABB extent is large, but there is no volume.
        HullResult? tilted = ConvexHull3.Build(new[] { P(3, 0, 0), P(0, 3, 0), P(0, 0, 3), P(1, 1, 1), P(2, 1, 0) }, new HullOptions(), out string? e2);
        Assert.Null(tilted);
        Assert.Contains("coplanar", e2, StringComparison.Ordinal);

        HullResult? few = ConvexHull3.Build(new[] { P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(1, 0, 0) }, new HullOptions(), out string? e3);
        Assert.Null(few);
        Assert.Contains("distinct", e3, StringComparison.Ordinal);

        HullResult? offGrid = ConvexHull3.Build(new[] { P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(0, 0, 0.3) }, new HullOptions(), out string? e4);
        Assert.Null(offGrid);
        Assert.Contains("grid", e4, StringComparison.Ordinal);
    }

    [Fact]
    public void Near_Coplanar_Facets_Merge_Within_Tolerance()
    {
        // A 1 m block whose top corners (in grid steps) are (0,0,0), (1024,0,0), (0,1,1000) and (1024,1,1001):
        // the fourth lies 1/1000 grid step (≈ 9.8e-7 m) off the plane of the other three, so the exact hull
        // has two top triangles that the 1e-6 m coplanar merge joins into one quad. A one-step bend is
        // ~1 mm and stays two faces.
        const double g = 1.0 / 1024;
        var pts = new List<Vec3d>
        {
            P(0, 0, 0), P(1, 0, 0), P(0, g, 1000 * g), P(1, g, 1001 * g),
            P(0, -1, 0), P(1, -1, 0), P(0, -1, 1000 * g), P(1, -1, 1001 * g),
        };
        HullResult merged = Hull(pts);
        Assert.Equal(6, merged.Faces.Count);
        Assert.Equal(1, merged.Faces.Count(f => f.Normal.Y > 0.9));
        AssertValid(merged, pts, 1e-6);

        HullResult exact = Hull(pts, new HullOptions { CoplanarTolerance = 0 });
        Assert.Equal(7, exact.Faces.Count);
        Assert.Equal(2, exact.Faces.Count(f => f.Normal.Y > 0.9));
        AssertValid(exact, pts);

        var bent = new List<Vec3d>
        {
            P(0, 0, 0), P(4, 0, 0), P(0, 0, 1), P(4, 0, 1),
            P(0, 1, 0), P(4, 1, 0), P(0, 1, 1), P(4, 1, 1), P(2, 1 + g, 0), P(2, 1 + g, 1),
        };
        Assert.Equal(2, Hull(bent).Faces.Count(f => f.Normal.Y > 0.9));
    }
}
