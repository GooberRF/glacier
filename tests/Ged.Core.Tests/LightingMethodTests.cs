using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Feature 1 lightmap methods: the AO-on-ambient factor on a half-occluded texel, the
/// N-sample soft-shadow mask averaging, and the gather bounce adding energy from a lit
/// neighbour surface. The default (RED Classic) path is covered by the existing byte-parity
/// gates; these exercise the new modifiers.
/// </summary>
public sealed class LightingMethodTests
{
    private static OccluderBvh Wall(params (Vec3 A, Vec3 B, Vec3 C)[] tris) => OccluderBvh.Build(tris);

    private static (Vec3, Vec3, Vec3)[] Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d) =>
        new[] { (a, b, c), (a, c, d) };

    // ---- Ambient occlusion ----------------------------------------------------

    [Fact]
    public void AO_Is_1_With_No_Occluders()
    {
        float ao = AmbientOcclusion.Factor(OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>()),
            Vec3.Zero, new Vec3(0, 1, 0), 32, 3f);
        Assert.Equal(1f, ao, 4);
    }

    [Fact]
    public void AO_Is_Roughly_Half_On_A_Half_Occluded_Hemisphere()
    {
        // A large wall just to +X of the texel occludes the +X half of the hemisphere.
        OccluderBvh occ = Wall(Quad(
            new Vec3(0.1f, -10, -10), new Vec3(0.1f, 10, -10), new Vec3(0.1f, 10, 10), new Vec3(0.1f, -10, 10)));

        float ao = AmbientOcclusion.Factor(occ, Vec3.Zero, new Vec3(0, 1, 0), 128, 3f);
        Assert.InRange(ao, 0.25f, 0.7f); // ~half open
    }

    [Fact]
    public void AO_Approaches_Zero_When_Boxed_In()
    {
        // Walls on all four sides + a ceiling occlude almost the whole hemisphere.
        var tris = new List<(Vec3, Vec3, Vec3)>();
        tris.AddRange(Quad(new Vec3(0.1f, -5, -5), new Vec3(0.1f, 5, -5), new Vec3(0.1f, 5, 5), new Vec3(0.1f, -5, 5)));
        tris.AddRange(Quad(new Vec3(-0.1f, -5, -5), new Vec3(-0.1f, 5, -5), new Vec3(-0.1f, 5, 5), new Vec3(-0.1f, -5, 5)));
        tris.AddRange(Quad(new Vec3(-5, -5, 0.1f), new Vec3(5, -5, 0.1f), new Vec3(5, 5, 0.1f), new Vec3(-5, 5, 0.1f)));
        tris.AddRange(Quad(new Vec3(-5, -5, -0.1f), new Vec3(5, -5, -0.1f), new Vec3(5, 5, -0.1f), new Vec3(-5, 5, -0.1f)));
        tris.AddRange(Quad(new Vec3(-5, 1f, -5), new Vec3(5, 1f, -5), new Vec3(5, 1f, 5), new Vec3(-5, 1f, 5)));

        float ao = AmbientOcclusion.Factor(OccluderBvh.Build(tris), Vec3.Zero, new Vec3(0, 1, 0), 128, 3f);
        Assert.InRange(ao, 0f, 0.25f);
    }

    // ---- Soft shadows ---------------------------------------------------------

    [Fact]
    public void SoftShadow_Mask_Is_1_Fully_Lit_0_Fully_Blocked_And_Averages_When_Partial()
    {
        Vec3 origin = Vec3.Zero;
        Vec3 light = new(0, 5, 0);

        Assert.Equal(1f, AreaShadow.Mask(OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>()), origin, light, 1.5f, 16), 4);

        OccluderBvh full = Wall(Quad(
            new Vec3(-20, 2.5f, -20), new Vec3(20, 2.5f, -20), new Vec3(20, 2.5f, 20), new Vec3(-20, 2.5f, 20)));
        Assert.InRange(AreaShadow.Mask(full, origin, light, 1.5f, 32), 0f, 0.05f);

        // A wall covering only x>0 blocks about half of the sampled light disc.
        OccluderBvh half = Wall(Quad(
            new Vec3(0f, 2.5f, -20), new Vec3(20, 2.5f, -20), new Vec3(20, 2.5f, 20), new Vec3(0f, 2.5f, 20)));
        float mask = AreaShadow.Mask(half, origin, light, 3f, 64);
        Assert.InRange(mask, 0.2f, 0.8f);
    }

    // ---- Bounce gather --------------------------------------------------------

    private static Surface MakeSurface(byte atlasX, byte atlasY, byte w, byte h, float planeY, float ny, int page = 64)
    {
        return new Surface
        {
            LightmapIndex = 0,
            X = atlasX, Y = atlasY, W = w, H = h,
            Plane = new RfPlane(new Vec3(0, ny, 0), -ny * planeY),
            UCoefficient = 0, VCoefficient = 2, DroppedCoefficient = 1,
            UvScale = new Uv(1f / page, 1f / page),
            UvAdd = new Uv((atlasX + 0.5f) / page, (atlasY + 0.5f) / page),
            BoundingBox = new Aabb(new Vec3(-1, planeY - 1, -1), new Vec3(w + 1, planeY + 1, h + 1)),
            RoomIndex = 0,
            ShouldSmooth = 0,
        };
    }

    [Fact]
    public void LitSurfaceField_Fetches_A_Lit_Neighbours_Colour()
    {
        Surface s0 = MakeSurface(0, 0, 8, 8, planeY: 0f, ny: 1f);
        var mapper = new SurfaceTexelMapper(s0, 64, 64);
        var buf = new float[8 * 8 * 3];
        Array.Fill(buf, 0.7f); // uniformly lit neighbour

        LitSurfaceField field = LitSurfaceField.Build(
            new[] { mapper }, new float[]?[] { buf }, new[] { 8 }, new[] { 8 });

        // A ray from below pointing up hits the surface (Y=0) and returns its lit colour.
        Vec3? c = field.SampleColor(new Vec3(3, -1, 3), new Vec3(0, 1, 0), 50f);
        Assert.NotNull(c);
        Assert.Equal(0.7f, c!.Value.X, 3);

        // A ray pointing away escapes.
        Assert.Null(field.SampleColor(new Vec3(3, -1, 3), new Vec3(0, -1, 0), 50f));
    }

    [Fact]
    public void A_Gather_Bounce_Adds_Energy_From_A_Lit_Neighbour_Surface()
    {
        // surface0 on the floor (Y=0, up), surface1 above it (Y=2, facing down). One bright
        // light lights both; the bounce adds surface0's reflected light onto surface1.
        Surface s0 = MakeSurface(0, 0, 8, 8, planeY: 0f, ny: 1f);
        Surface s1 = MakeSurface(16, 0, 8, 8, planeY: 2f, ny: -1f);
        var surfaces = new List<SurfaceBake> { new(s0, false), new(s1, false) };

        // Light ABOVE both surfaces: surface0 (facing up) is lit; surface1 (facing down)
        // is direct-unlit (only ambient), so the ONLY extra energy it can gain is the
        // bounce off surface0 — an unambiguous test of the gather.
        var light = new EngineLight
        {
            Type = EngineLightType.Point,
            Position = new Vec3(4, 6, 4),
            Position2 = new Vec3(4, 6, 4),
            Color = new Vec3(3, 3, 3),
            Range = 30f,
            RangeSq = 900f,
            AttenAlgo = 0,
            Enabled = true,
            CastsShadows = false,
        };
        var lights = new List<EngineLight> { light };
        OccluderBvh occ = OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());
        var ambient = new AmbientField(new Vec3(0.1f, 0.1f, 0.1f), Array.Empty<Room>());

        long Sum(bool bounce)
        {
            var page = new Lightmap { Width = 64, Height = 64, Pixels = new byte[64 * 64 * 3] };
            var opts = new LightingOptions
            {
                CastShadows = false, Quality = false, SmoothIterations = 0,
                LightBounces = bounce ? 1 : 0, BounceSamples = 24, BounceAlbedo = 0.9f,
            };
            Lightmapper.Bake(surfaces, new[] { page }, lights, occ, ambient, opts);

            long sum = 0;
            int stride = 64 * 3;
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    int o = ((s1.Y + row) * stride) + ((s1.X + col) * 3);
                    sum += page.Pixels[o] + page.Pixels[o + 1] + page.Pixels[o + 2];
                }
            }

            return sum;
        }

        long noBounce = Sum(false);
        long withBounce = Sum(true);
        Assert.True(withBounce > noBounce,
            $"bounce must add energy from the lit neighbour (no-bounce {noBounce}, bounce {withBounce})");
    }
}
