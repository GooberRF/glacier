using System;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// The per-texel / per-light lighting kernel, reverse-engineered byte-for-byte
/// from RED.exe's baker (FUN_004894c0) — which is identical to RF.exe's runtime
/// light math (docs/research/red-lighting-model.md §(b), spotlight-addendum.md).
/// Pure and allocation-free so it can be hammered from the multithreaded bake and
/// pinned by hand-computed unit fixtures.
/// </summary>
public static class LightKernel
{
    /// <summary>Degrees→radians (RF constant π/180, DAT_00589428).</summary>
    public const float DegToRad = 0.017453292f;

    /// <summary>
    /// Distance attenuation table (RED DAT_0057c978, index = dropoff_type):
    /// 0 linear <c>1−d/r</c>, 1 squared <c>(1−d/r)²</c>, 2 cosine <c>cos((d/r)·π/2)</c>,
    /// 3 sqrt <c>√(1−d/r)</c>. Hard cutoff to 0 at <c>d ≥ r</c> (no 1/d²).
    /// </summary>
    public static float Attenuate(int algo, float d, float r)
    {
        if (r <= 0f || d >= r)
        {
            return 0f;
        }

        float x = d / r; // 0..1 inside the range
        return algo switch
        {
            0 => 1f - x,
            1 => (1f - x) * (1f - x),
            2 => MathF.Cos(x * (MathF.PI * 0.5f)),
            3 => MathF.Sqrt(1f - x),
            _ => 1f - x,
        };
    }

    /// <summary>
    /// Point-light matte wrap <c>(2·N·L + 1)/3</c> — the soft terminator that makes
    /// RF point lighting wrap ~30° past 90°. Used when the surface is not smoothed.
    /// </summary>
    public static float PointWrap(float ndotl) => ((2f * ndotl) + 1f) / 3f;

    /// <summary>Spot/tube matte wrap <c>(N·L + 1)/2</c> (softer, half-lambert).</summary>
    public static float SpotWrap(float ndotl) => (ndotl + 1f) / 2f;

    /// <summary>
    /// Spot cone ramp given the negated axis cosine <paramref name="coneDot"/>
    /// (−1 at the cone centre, −cos(halfAngle) at an edge) and the two thresholds
    /// <c>inner=−cos(fov/2)</c>, <c>outer=−cos((fov+dropoff)/2)</c> (inner &lt; outer &lt; 0).
    /// 1 inside the inner cone, a linear 1→0 ramp to the outer edge, 0 outside.
    /// Squared when <paramref name="squared"/> (programmatic lights only).
    /// </summary>
    public static float SpotFalloff(float coneDot, float inner, float outer, bool squared)
    {
        if (coneDot >= outer)
        {
            return 0f; // outside the outer cone
        }

        float f = coneDot < inner
            ? 1f
            : 1f - ((coneDot - inner) / (outer - inner));
        return squared ? f * f : f;
    }

    /// <summary>−cos(fov°/2) inner cone threshold (spotlight-addendum.md).</summary>
    public static float InnerThreshold(float fovDegrees) =>
        -MathF.Cos(fovDegrees * 0.5f * DegToRad);

    /// <summary>−cos((fov+dropoff)°/2) outer cone threshold.</summary>
    public static float OuterThreshold(float fovDegrees, float dropoffDegrees) =>
        -MathF.Cos((fovDegrees + dropoffDegrees) * 0.5f * DegToRad);

    /// <summary>Closest point on segment [<paramref name="a"/>,<paramref name="b"/>] to <paramref name="p"/> (tube lights).</summary>
    public static Vec3 ClosestPointOnSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        Vec3 ab = b.Sub(a);
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f)
        {
            return a;
        }

        float t = Math.Clamp(p.Sub(a).Dot(ab) / len2, 0f, 1f);
        return a.Add(ab.Scale(t));
    }

    /// <summary>
    /// Full per-texel scalar contribution factor for one light (before the shadow
    /// mask and premultiplied colour): <c>dropoff · ndl</c> for point/tube, and
    /// <c>coneRamp · dropoff · ndl</c> for spot. Returns 0 when the texel is out of
    /// range, behind the light (ndl ≤ 0), or outside the cone.
    /// </summary>
    /// <param name="p">Texel world position.</param>
    /// <param name="n">Surface normal at the texel (unit).</param>
    /// <param name="shouldSmooth">True to use raw N·L (smoothed surfaces); false applies the matte wrap.</param>
    public static float Factor(in EngineLight light, Vec3 p, Vec3 n, bool shouldSmooth)
    {
        // Effective light point: the light position, or the closest point on the
        // tube segment (a tube is then treated exactly like a point light).
        Vec3 lightPoint = light.Type == EngineLightType.Tube
            ? ClosestPointOnSegment(p, light.Position, light.Position2)
            : light.Position;

        Vec3 toLight = lightPoint.Sub(p);
        float d2 = toLight.LengthSquared();
        if (d2 >= light.RangeSq || d2 < 1e-12f)
        {
            return d2 < 1e-12f ? 0f : 0f; // at the light or out of range
        }

        float d = MathF.Sqrt(d2);
        Vec3 l = toLight.Scale(1f / d); // unit texel→light

        float raw = n.Dot(l);
        float ndl = light.Type == EngineLightType.Spot
            ? (shouldSmooth ? raw : SpotWrap(raw))
            : (shouldSmooth ? raw : PointWrap(raw));
        if (ndl <= 0f)
        {
            return 0f;
        }

        if (light.Type == EngineLightType.Spot)
        {
            // Negated axis cosine: −1 at the cone centre (l ≈ −axis).
            float coneDot = light.SpotAxis.Dot(l);
            if (coneDot >= light.OuterThreshold)
            {
                return 0f;
            }

            float dEff = (1f - light.DistAttenOffset) * d;
            float atten = Attenuate(light.AttenAlgo, dEff, light.Range);
            float cone = SpotFalloff(coneDot, light.InnerThreshold, light.OuterThreshold, light.SquaredFovFalloff);
            return cone * atten * ndl;
        }

        // Point + tube: plain distance attenuation (no cone, no dist-atten offset).
        return Attenuate(light.AttenAlgo, d, light.Range) * ndl;
    }
}
