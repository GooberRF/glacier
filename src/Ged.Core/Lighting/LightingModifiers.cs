using System;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// Shared sampling helpers for the feature-1 lightmap modifiers: an orthonormal basis
/// and a deterministic low-discrepancy point/direction generator (so a bake is
/// reproducible and unit-testable).
/// </summary>
internal static class Sampling
{
    /// <summary>An orthonormal tangent basis (t, b) for a unit normal.</summary>
    public static (Vec3 T, Vec3 B) Basis(Vec3 n)
    {
        Vec3 up = MathF.Abs(n.Y) < 0.99f ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
        Vec3 t = up.Cross(n).Normalized();
        Vec3 b = n.Cross(t);
        return (t, b);
    }
}

/// <summary>
/// Per-texel hemisphere ambient occlusion (feature 1 modifier): M cosine-weighted rays
/// against the occluder BVH; the returned factor in [0,1] multiplies the ambient term
/// only (standard AO-on-ambient). Occluders beyond <c>radius</c> do not darken.
/// </summary>
public static class AmbientOcclusion
{
    /// <summary>
    /// The AO factor at <paramref name="point"/> with surface normal <paramref name="normal"/>:
    /// 1 = fully open, 0 = fully occluded. Deterministic (Hammersley + golden-ratio
    /// cosine-weighted hemisphere).
    /// </summary>
    public static float Factor(OccluderBvh occluders, Vec3 point, Vec3 normal, int samples, float radius, RfPlane? plane = null)
    {
        if (occluders.IsEmpty || samples <= 0 || radius <= 0f)
        {
            return 1f;
        }

        Vec3 n = normal.Normalized();
        (Vec3 t, Vec3 b) = Sampling.Basis(n);
        Vec3 origin = point.Add(n.Scale(0.02f));
        int occluded = 0;
        const float golden = 0.61803398875f;
        for (int k = 0; k < samples; k++)
        {
            float u1 = (k + 0.5f) / samples;          // stratified radius²
            float u2 = (k * golden) % 1f;             // golden-ratio angle
            float r = MathF.Sqrt(u1);
            float phi = u2 * MathF.PI * 2f;
            float x = r * MathF.Cos(phi);
            float y = r * MathF.Sin(phi);
            float z = MathF.Sqrt(MathF.Max(0f, 1f - u1)); // cosine-weighted up component
            Vec3 dir = t.Scale(x).Add(b.Scale(y)).Add(n.Scale(z));
            Vec3 target = origin.Add(dir.Scale(radius));
            if (occluders.Occluded(origin, target, plane))
            {
                occluded++;
            }
        }

        return 1f - (occluded / (float)samples);
    }
}

/// <summary>
/// N-sample area soft shadows (feature 1 modifier): jitter the light position over a
/// small disc facing the texel and average the visibility mask, replacing the stock
/// 2-sample penumbra with a smooth 0..1 ramp.
/// </summary>
public static class AreaShadow
{
    /// <summary>
    /// The averaged visibility (0 = fully shadowed, 1 = fully lit) from
    /// <paramref name="origin"/> to a light at <paramref name="lightPos"/> sampled over a
    /// disc of <paramref name="radius"/> facing the light. Deterministic.
    /// </summary>
    public static float Mask(OccluderBvh occluders, Vec3 origin, Vec3 lightPos, float radius, int samples, RfPlane? plane = null)
    {
        if (occluders.IsEmpty)
        {
            return 1f;
        }

        samples = Math.Max(1, samples);
        Vec3 toLight = lightPos.Sub(origin);
        float dist = toLight.Length();
        Vec3 dir = dist > 1e-5f ? toLight.Scale(1f / dist) : new Vec3(0, 1, 0);
        (Vec3 t, Vec3 b) = Sampling.Basis(dir);

        int lit = 0;
        const float golden = 2.399963f; // golden-angle spiral
        for (int k = 0; k < samples; k++)
        {
            float rr = radius * MathF.Sqrt((k + 0.5f) / samples);
            float ang = k * golden;
            Vec3 sample = lightPos.Add(t.Scale(rr * MathF.Cos(ang))).Add(b.Scale(rr * MathF.Sin(ang)));
            if (!occluders.Occluded(origin, sample, plane))
            {
                lit++;
            }
        }

        return lit / (float)samples;
    }
}
