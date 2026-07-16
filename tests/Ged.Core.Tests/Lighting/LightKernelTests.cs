using System;
using Ged.Core.Compiler;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Tests.Compiler;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Hand-computed acceptance fixtures pinning the per-texel/per-light kernel to
/// RED.exe's baker (docs/research/red-lighting-model.md §(b), spotlight-addendum.md).
/// Numbers, not eyeballs: each attenuation curve at d=0/r/2/r, the spot cone
/// thresholds for fov=60°/dropoff=30°, the wrap terms, tube closest-point, the
/// ambient init + neutral encoding, and the proportional overbright clamp.
/// </summary>
public sealed class LightKernelTests
{
    private const float Eps = 1e-5f;

    // ---- Attenuation curves (dropoff_type 0..3) at d = 0, r/2, r ----

    [Fact]
    public void Attenuation_Linear()
    {
        Assert.Equal(1f, LightKernel.Attenuate(0, 0f, 10f), 5);
        Assert.Equal(0.5f, LightKernel.Attenuate(0, 5f, 10f), 5);
        Assert.Equal(0f, LightKernel.Attenuate(0, 10f, 10f), 5); // hard cutoff at d=r
        Assert.Equal(0f, LightKernel.Attenuate(0, 11f, 10f), 5);
    }

    [Fact]
    public void Attenuation_Squared()
    {
        Assert.Equal(1f, LightKernel.Attenuate(1, 0f, 10f), 5);
        Assert.Equal(0.25f, LightKernel.Attenuate(1, 5f, 10f), 5); // (1-0.5)^2
        Assert.Equal(0f, LightKernel.Attenuate(1, 10f, 10f), 5);
    }

    [Fact]
    public void Attenuation_Cosine()
    {
        Assert.Equal(1f, LightKernel.Attenuate(2, 0f, 10f), 5);          // cos(0)
        Assert.Equal(MathF.Cos(MathF.PI / 4f), LightKernel.Attenuate(2, 5f, 10f), 5); // cos(45°)=0.70710
        Assert.Equal(0f, LightKernel.Attenuate(2, 10f, 10f), 5);        // cos(90°)
    }

    [Fact]
    public void Attenuation_Sqrt()
    {
        Assert.Equal(1f, LightKernel.Attenuate(3, 0f, 10f), 5);
        Assert.Equal(MathF.Sqrt(0.5f), LightKernel.Attenuate(3, 5f, 10f), 5); // sqrt(1-0.5)=0.70710
        Assert.Equal(0f, LightKernel.Attenuate(3, 10f, 10f), 5);
    }

    // ---- Wrap terms ----

    [Fact]
    public void Point_Matte_Wrap()
    {
        Assert.Equal(1f, LightKernel.PointWrap(1f), 5);          // face-on
        Assert.Equal(1f / 3f, LightKernel.PointWrap(0f), 5);     // grazing → 0.333
        Assert.Equal(0f, LightKernel.PointWrap(-0.5f), 5);       // terminator wraps to −0.5
        Assert.True(LightKernel.PointWrap(-1f) < 0f);            // fully behind → negative (gated out)
    }

    [Fact]
    public void Spot_Matte_Wrap()
    {
        Assert.Equal(1f, LightKernel.SpotWrap(1f), 5);
        Assert.Equal(0.5f, LightKernel.SpotWrap(0f), 5);
        Assert.Equal(0f, LightKernel.SpotWrap(-1f), 5);
    }

    // ---- Spot cone thresholds + ramp (fov=60°, dropoff=30°) ----

    [Fact]
    public void Spot_Cone_Thresholds_Fov60_Dropoff30()
    {
        float inner = LightKernel.InnerThreshold(60f);          // −cos(30°)
        float outer = LightKernel.OuterThreshold(60f, 30f);     // −cos(45°)
        Assert.Equal(-MathF.Cos(30f * MathF.PI / 180f), inner, 5); // −0.86602
        Assert.Equal(-MathF.Cos(45f * MathF.PI / 180f), outer, 5); // −0.70710
        Assert.True(inner < outer && outer < 0f);
    }

    [Fact]
    public void Spot_Cone_Ramp()
    {
        float inner = -0.8660254f, outer = -0.70710677f;
        Assert.Equal(1f, LightKernel.SpotFalloff(-1f, inner, outer, false), 5);    // centre
        Assert.Equal(1f, LightKernel.SpotFalloff(inner, inner, outer, false), 5);  // inner edge
        Assert.Equal(0f, LightKernel.SpotFalloff(outer, inner, outer, false), 5);  // outer edge
        Assert.Equal(0f, LightKernel.SpotFalloff(0f, inner, outer, false), 5);     // outside
        float mid = (inner + outer) / 2f;
        Assert.Equal(0.5f, LightKernel.SpotFalloff(mid, inner, outer, false), 5);  // half-way ramp
        // Squared ramp option.
        Assert.Equal(0.25f, LightKernel.SpotFalloff(mid, inner, outer, true), 5);
    }

    // ---- Tube closest-point ----

    [Fact]
    public void Tube_Closest_Point_On_Segment()
    {
        var a = new Vec3(-2, 0, 0);
        var b = new Vec3(2, 0, 0);
        // Perpendicular above the middle → clamps to the segment midpoint (origin).
        Vec3 mid = LightKernel.ClosestPointOnSegment(new Vec3(0, 3, 0), a, b);
        Assert.Equal(0f, mid.X, 5);
        Assert.Equal(0f, mid.Y, 5);
        // Beyond an endpoint → clamps to the endpoint.
        Assert.Equal(2f, LightKernel.ClosestPointOnSegment(new Vec3(10, 5, 0), a, b).X, 5);
        Assert.Equal(-2f, LightKernel.ClosestPointOnSegment(new Vec3(-10, 5, 0), a, b).X, 5);
    }

    // ---- Full point-light factor: face-on white light at half range ----

    [Fact]
    public void Point_Factor_FaceOn_HalfRange_Linear()
    {
        var light = new EngineLight
        {
            Type = EngineLightType.Point,
            Position = new Vec3(0, 5, 0),
            Range = 10f,
            RangeSq = 100f,
            AttenAlgo = 0,
            Color = new Vec3(1, 1, 1),
            Enabled = true,
        };
        // Texel on the floor at origin, normal up: N·L = 1 (face-on), d=5, r=10.
        float f = LightKernel.Factor(light, new Vec3(0, 0, 0), new Vec3(0, 1, 0), shouldSmooth: false);
        // wrap(1)=1 * linear(5,10)=0.5 → 0.5
        Assert.Equal(0.5f, f, 4);
    }

    [Fact]
    public void Point_Factor_OutOfRange_Is_Zero()
    {
        var light = new EngineLight
        {
            Type = EngineLightType.Point, Position = new Vec3(0, 20, 0),
            Range = 10f, RangeSq = 100f, Color = new Vec3(1, 1, 1), Enabled = true,
        };
        Assert.Equal(0f, LightKernel.Factor(light, Vec3Zero, new Vec3(0, 1, 0), false), 5);
    }

    // ---- Light axis conventions (verified vs RED-baked corpus: dm04 spot batteries) ----

    [Fact]
    public void Spot_Beam_Points_Along_The_Rotation_Forward_Row()
    {
        // Identity rotation: Forward = +Z. A spot at the origin aiming +Z must
        // fully light a texel on its axis and reject one behind it.
        var l = new Light
        {
            Uid = 1,
            Position = new Vec3(0, 0, 0),
            Rotation = Mat3.Identity,
            Flags = 0x8 | (2u << 4) | (2u << 8), // enabled, spot, on
            Color = new RfColor(255, 255, 255, 255),
            Range = 20f,
            Fov = 60f,
            FovDropoff = 30f,
            OnIntensity = 1f,
        };
        EngineLight e = EngineLight.FromModel(l, false);
        Assert.Equal(EngineLightType.Spot, e.Type);
        Assert.Equal(0f, e.SpotAxis.X, 4);
        Assert.Equal(0f, e.SpotAxis.Y, 4);
        Assert.Equal(1f, e.SpotAxis.Z, 4);

        // Texel 5m down the beam, surface facing back toward the light.
        float onAxis = LightKernel.Factor(e, new Vec3(0, 0, 5), new Vec3(0, 0, -1), false);
        Assert.True(onAxis > 0.4f, $"on-axis texel should be lit (f={onAxis})");

        // Texel behind the light: outside the cone.
        float behind = LightKernel.Factor(e, new Vec3(0, 0, -5), new Vec3(0, 0, 1), false);
        Assert.Equal(0f, behind, 5);
    }

    [Fact]
    public void Tube_Extends_Along_The_Rotation_Right_Row()
    {
        // Identity rotation: Right = +X. A 4m tube must have endpoints at ±2 X.
        var l = new Light
        {
            Uid = 2,
            Position = new Vec3(0, 0, 0),
            Rotation = Mat3.Identity,
            Flags = 0x8 | (3u << 4) | (2u << 8), // enabled, tube, on
            Color = new RfColor(255, 255, 255, 255),
            Range = 10f,
            TubeLightWidth = 4f,
            OnIntensity = 1f,
        };
        EngineLight e = EngineLight.FromModel(l, false);
        Assert.Equal(EngineLightType.Tube, e.Type);
        Assert.Equal(-2f, e.Position.X, 4);
        Assert.Equal(2f, e.Position2.X, 4);
        Assert.Equal(0f, e.Position.Y, 4);
        Assert.Equal(0f, e.Position.Z, 4);
    }

    // ---- Ambient init + neutral encoding (128 = neutral) ----

    [Fact]
    public void White_Ambient_Half_Encodes_To_Neutral_127()
    {
        // buffer = ambient(1.0)×0.5 = 0.5 → 0.5×255 = 127.5 → ftol 127.
        (byte r, byte g, byte b) = LightEncoder.Encode(0.5f, 0.5f, 0.5f, proportional: true);
        Assert.Equal(127, r);
        Assert.Equal(127, g);
        Assert.Equal(127, b);
    }

    [Fact]
    public void Encode_Clamps_Negative_To_Zero()
    {
        (byte r, byte g, byte b) = LightEncoder.Encode(-1f, 0f, 1f, true);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(255, b);
    }

    [Fact]
    public void Proportional_Clamp_Preserves_Hue()
    {
        // (1.5, 0.5, 0.5) → ×255 = (382.5,127.5,127.5); max 382.5>255 → scale 255/382.5.
        (byte r, byte g, byte b) = LightEncoder.Encode(1.5f, 0.5f, 0.5f, proportional: true);
        Assert.Equal(255, r);
        // 127.5 × (255/382.5) = 85.0 → 85 (hue preserved: g==b, ratio to r kept)
        Assert.Equal(85, g);
        Assert.Equal(85, b);
    }

    [Fact]
    public void PerChannel_Clamp_Keeps_Unsaturated_Channels()
    {
        // No-clamp (Alpine) load behaviour: independent clamp, non-overbright kept.
        (byte r, byte g, byte b) = LightEncoder.Encode(1.5f, 0.5f, 0.5f, proportional: false);
        Assert.Equal(255, r);
        Assert.Equal(127, g);
        Assert.Equal(127, b);
    }

    // ---- Texel→world round-trip vs SurfaceBuilder's UV forward transform ----

    [Fact]
    public void Texel_To_World_RoundTrips_The_Surface_Uv_Transform()
    {
        // Compile an 8×8×8 air box; take a wall surface and verify SurfaceTexelMapper
        // is the exact inverse of uv_scale/uv_add (texel centre → world → atlas UV).
        var brushes = new System.Collections.Generic.List<Brush>
        {
            CompilerTestBrushes.AirBox(1, new Vec3(0, 0, 0), 8, 8, 8),
        };
        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;
        Assert.NotEmpty(g.Surfaces);

        Lightmap page = c.Lightmaps[0];
        foreach (Surface s in g.Surfaces)
        {
            var mapper = new SurfaceTexelMapper(s, page.Width, page.Height);
            for (int row = 0; row < s.H; row += Math.Max(1, s.H / 3))
            {
                for (int col = 0; col < s.W; col += Math.Max(1, s.W / 3))
                {
                    Vec3 p = mapper.World(col, row);

                    // Forward transform (SurfaceBuilder): worldUV·scale + add == atlas UV.
                    float atlasU = (p.Component(s.UCoefficient) * s.UvScale.U) + s.UvAdd.U;
                    float atlasV = (p.Component(s.VCoefficient) * s.UvScale.V) + s.UvAdd.V;
                    float expectU = (s.X + col + 0.5f) / page.Width;
                    float expectV = (s.Y + row + 0.5f) / page.Height;
                    Assert.Equal(expectU, atlasU, 4);
                    Assert.Equal(expectV, atlasV, 4);

                    // And P lies on the surface plane.
                    float d = s.Plane.Normal.Dot(p) + s.Plane.Offset;
                    Assert.True(MathF.Abs(d) < 1e-2f, $"texel off-plane by {d}");
                }
            }
        }
    }

    private static Vec3 Vec3Zero => new(0, 0, 0);
}
