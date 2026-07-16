using System;
using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Blocker 1 (compiler-parity-notes.md): the leaf-extraction path classifies every convex BSP leaf at a
/// guaranteed-interior point. These tests pin that the <see cref="ChebyshevCenter"/> LP that replaced the
/// O(n³) vertex enumeration produces a genuinely strictly-interior point — margin-strict against every
/// constraint — and agrees with the vertex-enumeration oracle it replaced (same interior ⇒ same open/solid
/// verdict, so holes cannot regress). The enumeration remains callable as the ground-truth oracle.
/// </summary>
public sealed class ChebyshevCenterTests
{
    private static readonly Vec3 Wc = new(0, 0, 0);
    private const float Half = 1000f;

    /// <summary>Inside-⟺-Distance≤0 half-spaces of an axis-aligned box centred at <paramref name="c"/>.</summary>
    private static CsgPlane[] Box(Vec3 c, float hx, float hy, float hz) => new[]
    {
        new CsgPlane(new Vec3(1, 0, 0), -(c.X + hx)),
        new CsgPlane(new Vec3(-1, 0, 0), c.X - hx),
        new CsgPlane(new Vec3(0, 1, 0), -(c.Y + hy)),
        new CsgPlane(new Vec3(0, -1, 0), c.Y - hy),
        new CsgPlane(new Vec3(0, 0, 1), -(c.Z + hz)),
        new CsgPlane(new Vec3(0, 0, -1), c.Z - hz),
    };

    [Fact]
    public void Cube_Center_Is_Exact()
    {
        CsgPlane[] cell = Box(new Vec3(5, -3, 7), 4, 4, 4);
        Assert.True(ChebyshevCenter.Solve(cell, cell.Length, Wc, Half, out Vec3 c, out float r));
        Assert.True(c.Sub(new Vec3(5, -3, 7)).Length() < 1e-3f, $"centre {c} should be the cube centre");
        Assert.True(MathF.Abs(r - 4f) < 1e-3f, $"radius {r} should be the half-extent 4");
    }

    [Fact]
    public void Non_Cubic_Box_Radius_Is_Smallest_Half_Extent()
    {
        // A non-cubic box's Chebyshev centre is NOT unique (the max-radius set is a segment at y=0), so the
        // radius is what's determined — the thinnest half-extent — and the returned point must be margin-strict.
        CsgPlane[] cell = Box(new Vec3(0, 0, 0), 10, 2, 6); // thinnest axis is y (half 2)
        Assert.True(ChebyshevCenter.Solve(cell, cell.Length, Wc, Half, out Vec3 c, out float r));
        Assert.True(MathF.Abs(r - 2f) < 1e-3f, $"radius {r} should equal the thinnest half-extent 2");
        foreach (CsgPlane pl in cell)
        {
            Assert.True(pl.Distance(c) <= -r + 1e-3f, $"centre must be ≥ r inside every face; got {pl.Distance(c)}");
        }
    }

    [Fact]
    public void Slanted_And_Boxed_Cell_Point_Is_Margin_Strict()
    {
        // A tetrahedron-ish cell: four slanted planes plus the world box, at large coordinates.
        var cell = new List<CsgPlane>(Box(new Vec3(120, -80, 60), 900, 900, 900));
        cell.Add(new CsgPlane(new Vec3(1, 1, 1).Normalized(), -(new Vec3(1, 1, 1).Normalized().Dot(new Vec3(150, -60, 90)))));
        cell.Add(new CsgPlane(new Vec3(-1, 0.3f, -0.2f).Normalized(), -(new Vec3(-1, 0.3f, -0.2f).Normalized().Dot(new Vec3(80, -100, 40)))));
        cell.Add(new CsgPlane(new Vec3(0.1f, -1, 0.4f).Normalized(), -(new Vec3(0.1f, -1, 0.4f).Normalized().Dot(new Vec3(110, -120, 55)))));

        CsgPlane[] arr = cell.ToArray();
        Assert.True(ChebyshevCenter.Solve(arr, arr.Length, Wc, Half, out Vec3 c, out float r), "cell has an interior");
        Assert.True(r > 1e-2f, $"radius {r} should be comfortably positive");

        // Margin-strict: every constraint's signed distance must be ≤ −r (inside by at least the radius).
        foreach (CsgPlane pl in arr)
        {
            Assert.True(pl.Distance(c) <= -r + 1e-3f, $"centre must be ≥ r inside plane; got {pl.Distance(c)} vs -{r}");
        }
    }

    [Fact]
    public void Agrees_With_Enumeration_Oracle_On_Random_Cells()
    {
        // Random bounded convex cells (box + a few random cutting planes through the box). The LP centre and
        // the enumeration centroid must both be strictly interior (so any open/solid probe would agree), which
        // is the property that keeps the hole count identical between the two constructions.
        var rng = new Random(1234);
        int checkedCells = 0;
        for (int t = 0; t < 400; t++)
        {
            Vec3 ctr = new((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
            var cell = new List<CsgPlane>(Box(ctr, 30, 30, 30));
            int extra = rng.Next(0, 4);
            for (int k = 0; k < extra; k++)
            {
                var n = new Vec3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                if (n.Length() < 0.2f)
                {
                    continue;
                }

                n = n.Normalized();
                // Offset so the plane passes somewhere through the box interior (keeps a nonempty cell).
                Vec3 through = ctr.Add(n.Scale((float)(rng.NextDouble() * 20 - 10)));
                cell.Add(new CsgPlane(n, -n.Dot(through)));
            }

            CsgPlane[] arr = cell.ToArray();
            bool lp = ChebyshevCenter.Solve(arr, arr.Length, Wc, Half, out Vec3 lpc, out float r);
            bool enu = WorldBsp.LeafInteriorPointEnumerate(arr, arr.Length, Wc, out Vec3 enc);
            if (!enu)
            {
                continue; // oracle itself found a degenerate cell — skip
            }

            Assert.True(lp && r > 1e-3f, $"LP should resolve the same nonempty cell (enum found interior at {enc})");

            // Both points strictly interior ⇒ both satisfy every constraint (Distance < 0).
            foreach (CsgPlane pl in arr)
            {
                Assert.True(pl.Distance(lpc) < 1e-3f, "LP centre inside every constraint");
                Assert.True(pl.Distance(enc) < 1e-2f, "enumeration centroid inside every constraint");
            }

            checkedCells++;
        }

        Assert.True(checkedCells > 300, "should have exercised many nonempty random cells");
    }

    [Fact]
    public void Empty_Cell_Is_Reported()
    {
        // Two opposed half-spaces with no common interior: x ≤ -5 and x ≥ 5.
        var cell = new List<CsgPlane>(Box(new Vec3(0, 0, 0), 900, 900, 900))
        {
            new(new Vec3(1, 0, 0), 5),   // x + 5 ≤ 0 ⇒ x ≤ -5
            new(new Vec3(-1, 0, 0), 5),  // -x + 5 ≤ 0 ⇒ x ≥ 5
        };
        CsgPlane[] arr = cell.ToArray();
        bool ok = ChebyshevCenter.Solve(arr, arr.Length, Wc, Half, out _, out float r);
        Assert.False(ok && r > 0f, "an empty cell must not report a positive-radius interior");
    }
}
