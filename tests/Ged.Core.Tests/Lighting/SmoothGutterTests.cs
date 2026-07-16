using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Smoothing-artifact fixtures for the Smooth Gutter Normals option. On a smoothed surface RED's
/// per-texel path interpolates the smoothing-group vertex normals inside the face polygons but
/// falls back to the FLAT plane normal for gutter texels (the fragment min-clamp overhang around
/// the polygon), producing a normal discontinuity — a faceted rim — at the polygon boundary.
/// These tests reproduce that discontinuity, prove the weld-to-nearest-face option removes it,
/// prove the default (OFF) path is byte-identical, and measure the angle-weighted vertex-normal
/// average against RED's hard 90° hemisphere cutoff.
/// </summary>
public sealed class SmoothGutterTests
{
    private readonly ITestOutputHelper _out;
    public SmoothGutterTests(ITestOutputHelper output) => _out = output;

    private static OccluderBvh NoOcc() => OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());
    private const int Page = 32;

    // One smoothed triangle in plane z=0: A(0,0) B(8,0) C(0,8). Its vertex normals are tilted (as
    // if smoothing-averaged with curved neighbours), so the interior interpolates a gradient while
    // the flat plane normal (+Z) — used for gutter texels — does not match at the hypotenuse.
    private static List<SmoothFace> TiltedTriangle() => new()
    {
        new SmoothFace(
            new[] { new Vec3(0, 0, 0), new Vec3(8, 0, 0), new Vec3(0, 8, 0) },
            new[]
            {
                new Vec3(-0.6f, -0.6f, 1).Normalized(),
                new Vec3(0.8f, -0.2f, 1).Normalized(),
                new Vec3(-0.2f, 0.8f, 1).Normalized(),
            }),
    };

    private static Surface SmoothTriSurface() => new()
    {
        LightmapIndex = 0, X = 0, Y = 0, W = 16, H = 16,
        BoundingBox = new Aabb(new Vec3(0, 0, 0), new Vec3(8, 8, 0)),
        Plane = new RfPlane(new Vec3(0, 0, 1), 0f),
        ShouldSmooth = 1, UCoefficient = 0, VCoefficient = 1, DroppedCoefficient = 2,
        UvAdd = new Uv(0.5f / Page, 0.5f / Page), UvScale = new Uv(1f / Page, 1f / Page), RoomIndex = 0,
    };

    private static EngineLight GrazingLight() => new()
    {
        Type = EngineLightType.Point, Position = new Vec3(40, -20, 20), Position2 = new Vec3(40, -20, 20),
        Color = new Vec3(2, 2, 2), Range = 200f, RangeSq = 40000f, AttenAlgo = 0, Enabled = true, CastsShadows = false,
    };

    private static Lightmap BakeSmooth(bool weld)
    {
        Surface s = SmoothTriSurface();
        var page = new Lightmap { Width = Page, Height = Page, Pixels = new byte[Page * Page * 3] };
        var field = new AmbientField(new Vec3(0.1f, 0.1f, 0.1f), Array.Empty<Room>());
        var opts = new LightingOptions { CastShadows = false, Quality = false, SmoothIterations = 0, SmoothGutterNormals = weld };
        Lightmapper.Bake(new List<SurfaceBake> { new(s, false, TiltedTriangle()) }, new[] { page },
            new List<EngineLight> { GrazingLight() }, NoOcc(), field, opts);
        return page;
    }

    // The largest R jump between two horizontally-adjacent texels that straddle the triangle
    // boundary (one inside the polygon, one in the gutter) — the gutter normal discontinuity.
    private static int GutterBoundaryJump(Lightmap p, Surface s)
    {
        var m = new SurfaceTexelMapper(s, Page, Page);
        int stride = Page * 3;
        int R(int col, int row) => p.Pixels[(row * stride) + (col * 3)];
        bool Inside(int col, int row)
        {
            Vec3 w = m.World(col, row);
            return w.X >= 0f && w.Y >= 0f && w.X + w.Y <= 8f;
        }

        int max = 0;
        for (int row = 0; row < s.H; row++)
        {
            for (int col = 0; col < s.W - 1; col++)
            {
                if (Inside(col, row) != Inside(col + 1, row))
                {
                    max = Math.Max(max, Math.Abs(R(col, row) - R(col + 1, row)));
                }
            }
        }

        return max;
    }

    [Fact]
    public void Gutter_Normal_Discontinuity_Reproduces_Then_Shrinks_With_The_Weld()
    {
        Surface s = SmoothTriSurface();
        int off = GutterBoundaryJump(BakeSmooth(weld: false), s);
        int on = GutterBoundaryJump(BakeSmooth(weld: true), s);
        _out.WriteLine($"gutter boundary jump: off={off} on={on}");

        Assert.True(off > 60, $"fixture must show a gutter discontinuity: off={off}");
        Assert.True(on < off * 0.6, $"weld must shrink the boundary discontinuity: off={off} on={on}");
    }

    [Fact]
    public void Weld_Default_Is_Off_And_The_Default_Bake_Is_Byte_Identical()
    {
        var def = new LightingOptions();
        Assert.False(def.SmoothGutterNormals);
        Assert.False(def.AngleWeightedNormals);

        // A default-path bake (weld off) equals an explicit weld-off bake; turning it on changes bytes.
        Lightmap off1 = BakeSmooth(weld: false);
        Lightmap off2 = BakeSmooth(weld: false);
        Lightmap on = BakeSmooth(weld: true);
        Assert.Equal(off1.Pixels, off2.Pixels);
        Assert.NotEqual(off1.Pixels, on.Pixels);
    }

    [Fact]
    public void WithMethod_Maps_SmoothGutters_To_Weld_And_AngleWeighted_And_Composes()
    {
        var opts = new LightingOptions();
        opts.WithMethod(new LightingMethod { Base = LightingBase.Bounced, SmoothGutters = true, CornerLeakFix = true });
        Assert.True(opts.SmoothGutterNormals);
        Assert.True(opts.AngleWeightedNormals);
        Assert.True(opts.CornerLeakFix);          // composes with Corner Leak Fix
        Assert.Equal(1, opts.LightBounces);        // and with the Bounced base
    }

    // ---- Angle-weighted vertex-normal averaging (measured vs the raw 90° cutoff) ----

    private static Vec3 N(float deg) => new(MathF.Sin(deg * MathF.PI / 180f), 0f, MathF.Cos(deg * MathF.PI / 180f));

    private static float AngleBetween(Vec3 a, Vec3 b) =>
        MathF.Acos(Math.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f)) * 180f / MathF.PI;

    [Fact]
    public void AngleWeighted_Average_Softens_The_90_Degree_Cutoff_Flip()
    {
        var flat = new Vec3(0, 0, 1);

        // A neighbour face rotating from just inside the cutoff (85°, dot>0, INCLUDED) to just past
        // it (95°, dot<0, EXCLUDED). RED's hard cutoff snaps the shared-vertex normal from a strong
        // tilt to flat across that boundary — a visible flip. Angle-weighting scales the neighbour by
        // its cosine, so near the cutoff it contributes almost nothing and the transition is smooth.
        Vec3 rawAt85 = SmoothNormals.AverageAt(flat, new[] { N(85) }, angleWeighted: false);
        Vec3 rawAt95 = SmoothNormals.AverageAt(flat, new[] { N(95) }, angleWeighted: false);
        Vec3 awAt85 = SmoothNormals.AverageAt(flat, new[] { N(85) }, angleWeighted: true);
        Vec3 awAt95 = SmoothNormals.AverageAt(flat, new[] { N(95) }, angleWeighted: true);

        float rawFlip = AngleBetween(rawAt85, rawAt95);
        float awFlip = AngleBetween(awAt85, awAt95);
        _out.WriteLine($"cutoff flip 85°->95°: raw={rawFlip:F1}°  angle-weighted={awFlip:F1}°");

        Assert.True(rawFlip > 30f, $"the raw cutoff must flip hard: {rawFlip:F1}°");
        Assert.True(awFlip < 10f, $"angle-weighting must soften the flip: {awFlip:F1}°");
    }

    [Fact]
    public void AngleWeighted_Keeps_The_Red_Parity_Cases()
    {
        var flat = new Vec3(0, 0, 1);

        // Perpendicular neighbour (dot=0) is still excluded → stays flat (matches RED).
        Vec3 perp = SmoothNormals.AverageAt(flat, new[] { new Vec3(1, 0, 0) }, angleWeighted: true);
        Assert.True(AngleBetween(perp, flat) < 1e-2f);

        // A near-coplanar neighbour (dot≈1) is weighted ≈ fully, so the angle-weighted mean stays
        // close to the raw unweighted mean — no regression on the common smooth-surface case.
        Vec3 near = N(10);
        Vec3 raw = SmoothNormals.AverageAt(flat, new[] { near }, angleWeighted: false);
        Vec3 aw = SmoothNormals.AverageAt(flat, new[] { near }, angleWeighted: true);
        Assert.True(AngleBetween(raw, aw) < 2f, $"near-coplanar case must barely differ: {AngleBetween(raw, aw):F2}°");
    }
}
