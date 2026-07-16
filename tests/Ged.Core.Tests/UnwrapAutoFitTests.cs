using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Items 10 + 11 — the UV Unwrap editor's Auto Unwrap (planar projection + shelf packing of
/// face islands into the base tile) and Fit (scale+centre the selection to fill [0,1]).
/// Pure ops over the window's working set, tested island-geometry-first.
/// </summary>
public sealed class UnwrapAutoFitTests
{
    // ---- Item 11: Fit ---------------------------------------------------------

    [Fact]
    public void FitToTile_Fills_The_Tile_Preserving_Aspect()
    {
        // A 2:1 landscape rectangle away from the origin.
        var uvs = new List<Uv> { new(10f, 5f), new(14f, 5f), new(14f, 7f), new(10f, 7f) };
        UnwrapOps.FitToTile(uvs, new[] { 0, 1, 2, 3 });

        float minU = uvs.Min(p => p.U), maxU = uvs.Max(p => p.U);
        float minV = uvs.Min(p => p.V), maxV = uvs.Max(p => p.V);

        // Long axis fills [0,1]; short axis keeps the 2:1 aspect (0.5 tall) and is centred.
        Assert.Equal(0f, minU, 3);
        Assert.Equal(1f, maxU, 3);
        Assert.Equal(0.5f, maxV - minV, 3);
        Assert.Equal(0.25f, minV, 3);
        Assert.Equal(0.75f, maxV, 3);
    }

    [Fact]
    public void FitToTile_Without_Aspect_Stretches_Both_Axes()
    {
        var uvs = new List<Uv> { new(3f, 3f), new(5f, 3f), new(5f, 4f), new(3f, 4f) };
        UnwrapOps.FitToTile(uvs, new[] { 0, 1, 2, 3 }, preserveAspect: false);

        Assert.Equal(0f, uvs.Min(p => p.U), 3);
        Assert.Equal(1f, uvs.Max(p => p.U), 3);
        Assert.Equal(0f, uvs.Min(p => p.V), 3);
        Assert.Equal(1f, uvs.Max(p => p.V), 3);
    }

    [Fact]
    public void FitToTile_Handles_A_Degenerate_Axis()
    {
        // All corners share one V — a horizontal line: U fits, V centres at 0.5.
        var uvs = new List<Uv> { new(2f, 9f), new(6f, 9f) };
        UnwrapOps.FitToTile(uvs, new[] { 0, 1 });
        Assert.Equal(0.5f, uvs[0].V, 3);
        Assert.Equal(0.5f, uvs[1].V, 3);
        Assert.True(uvs.Max(p => p.U) - uvs.Min(p => p.U) is > 0.99f and <= 1.001f);
    }

    // ---- Item 10: Auto Unwrap --------------------------------------------------

    [Fact]
    public void AutoUnwrap_Packs_Cube_Faces_Into_The_Tile_Without_Overlap()
    {
        // A unit cube: 6 quad faces, 4 corners each. Corner index = face*4 + i.
        (Vec3[] positions, Vec3[] normals, List<IReadOnlyList<int>> rings) = Cube(size: 2f);
        var uvs = new List<Uv>(Enumerable.Repeat(default(Uv), 24));

        UnwrapOps.AutoUnwrap(uvs, rings, i => positions[i], f => normals[f]);

        // Every UV inside the base tile.
        Assert.All(uvs, p => Assert.True(p.U is >= 0f and <= 1f && p.V is >= 0f and <= 1f, $"({p.U},{p.V}) outside tile"));

        // Islands are axis-aligned squares here: no two face bounds may overlap.
        var bounds = rings.Select(r =>
            (MinU: r.Min(i => uvs[i].U), MaxU: r.Max(i => uvs[i].U),
             MinV: r.Min(i => uvs[i].V), MaxV: r.Max(i => uvs[i].V))).ToList();
        for (int a = 0; a < bounds.Count; a++)
        {
            for (int b = a + 1; b < bounds.Count; b++)
            {
                bool overlap = bounds[a].MinU < bounds[b].MaxU - 1e-4f && bounds[a].MaxU > bounds[b].MinU + 1e-4f &&
                               bounds[a].MinV < bounds[b].MaxV - 1e-4f && bounds[a].MaxV > bounds[b].MinV + 1e-4f;
                Assert.False(overlap, $"islands {a} and {b} overlap");
            }
        }

        // Equal-sized cube faces stay equal-sized in UV space (world proportions preserved).
        float first = bounds[0].MaxU - bounds[0].MinU;
        Assert.All(bounds, bb =>
        {
            Assert.Equal(first, bb.MaxU - bb.MinU, 3);
            Assert.Equal(first, bb.MaxV - bb.MinV, 3);
        });

        Assert.True(first > 0.15f, $"islands should use the tile meaningfully, got {first}");
    }

    [Fact]
    public void AutoUnwrap_Preserves_Relative_World_Scale_Between_Different_Faces()
    {
        // Two Z-facing quads: 4m and 1m wide — the packed islands must keep the 4:1 ratio.
        var positions = new Vec3[8];
        var rings = new List<IReadOnlyList<int>> { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 } };
        var normals = new[] { new Vec3(0, 0, 1), new Vec3(0, 0, 1) };
        SetQuad(positions, 0, 0f, 0f, 4f, 4f);
        SetQuad(positions, 4, 10f, 0f, 1f, 1f);

        var uvs = new List<Uv>(Enumerable.Repeat(default(Uv), 8));
        UnwrapOps.AutoUnwrap(uvs, rings, i => positions[i], f => normals[f]);

        float big = rings[0].Max(i => uvs[i].U) - rings[0].Min(i => uvs[i].U);
        float small = rings[1].Max(i => uvs[i].U) - rings[1].Min(i => uvs[i].U);
        Assert.Equal(4f, big / small, 2);
    }

    [Fact]
    public void ProjectionAxes_Drop_The_Dominant_Normal_Component()
    {
        Assert.Equal((1, 2), UnwrapOps.ProjectionAxes(new Vec3(1, 0, 0)));
        Assert.Equal((0, 2), UnwrapOps.ProjectionAxes(new Vec3(0, -1, 0)));
        Assert.Equal((0, 1), UnwrapOps.ProjectionAxes(new Vec3(0.1f, 0.2f, 0.9f)));
    }

    private static void SetQuad(Vec3[] positions, int at, float x, float y, float w, float h)
    {
        positions[at] = new Vec3(x, y, 0);
        positions[at + 1] = new Vec3(x + w, y, 0);
        positions[at + 2] = new Vec3(x + w, y + h, 0);
        positions[at + 3] = new Vec3(x, y + h, 0);
    }

    private static (Vec3[] Positions, Vec3[] Normals, List<IReadOnlyList<int>> Rings) Cube(float size)
    {
        float h = size * 0.5f;
        var normals = new[]
        {
            new Vec3(1, 0, 0), new Vec3(-1, 0, 0),
            new Vec3(0, 1, 0), new Vec3(0, -1, 0),
            new Vec3(0, 0, 1), new Vec3(0, 0, -1),
        };
        var positions = new Vec3[24];
        var rings = new List<IReadOnlyList<int>>();
        for (int f = 0; f < 6; f++)
        {
            var ring = new int[4];
            (int uAxis, int vAxis) = UnwrapOps.ProjectionAxes(normals[f]);
            for (int i = 0; i < 4; i++)
            {
                float u = (i is 1 or 2) ? h : -h;
                float v = (i is 2 or 3) ? h : -h;
                float[] c = { 0, 0, 0 };
                c[uAxis] = u;
                c[vAxis] = v;
                int dominant = 3 - uAxis - vAxis;
                c[dominant] = normals[f].Component(dominant) > 0 ? h : -h;
                positions[(f * 4) + i] = new Vec3(c[0], c[1], c[2]);
                ring[i] = (f * 4) + i;
            }

            rings.Add(ring);
        }

        return (positions, normals, rings);
    }
}
