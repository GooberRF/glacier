using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>The volume shape of a bounded directional light (common/include/common/alpine_dir_light.h:28-34).</summary>
public enum AlpineLightShape : byte
{
    None = 0,
    Box = 1,
    Sphere = 2,
    Cylinder = 3,
}

/// <summary>
/// A bounded directional light's volume and its light weight, reimplemented from
/// common/include/common/alpine_dir_light.h:214-384 (<c>make_volume</c>, <c>volume_inside_distance</c>,
/// <c>volume_weight</c>, <c>volume_bounding_radius</c>, <c>volume_ray_exit_distance</c>).
/// </summary>
public sealed class AlpineLightVolume
{
    public AlpineLightShape Shape { get; init; }

    public Vec3 Center { get; init; }

    /// <summary>Unit travel direction (the cylinder axis).</summary>
    public Vec3 Axis { get; init; } = new(0f, -1f, 0f);

    public Vec3 BoxRight { get; init; } = new(1f, 0f, 0f);

    public Vec3 BoxForward { get; init; } = new(0f, 0f, 1f);

    public float HalfX { get; init; }

    public float HalfY { get; init; }

    public float HalfZ { get; init; }

    public float Radius { get; init; }

    public float HalfLength { get; init; }

    public float Feather { get; init; }

    /// <summary>Distance from p to the nearest boundary: positive inside, ≤ 0 outside (alpine_dir_light.h:270-292).</summary>
    public float InsideDistance(Vec3 p)
    {
        Vec3 d = p.Sub(Center);
        switch (Shape)
        {
            case AlpineLightShape.Box:
                float lx = d.Dot(BoxRight), lz = d.Dot(BoxForward);
                return MathF.Min(MathF.Min(HalfX - MathF.Abs(lx), HalfY - MathF.Abs(d.Y)), HalfZ - MathF.Abs(lz));
            case AlpineLightShape.Sphere:
                return Radius - MathF.Sqrt(d.Dot(d));
            case AlpineLightShape.Cylinder:
                float t = d.Dot(Axis);
                Vec3 radial = d.Sub(Axis.Scale(t));
                return MathF.Min(Radius - MathF.Sqrt(radial.Dot(radial)), HalfLength - MathF.Abs(t));
            default:
                return float.PositiveInfinity;
        }
    }

    /// <summary>Light weight at p: clamp(inside / feather, 0, 1); a hard edge at feather 0 (alpine_dir_light.h:294-306).</summary>
    public float Weight(Vec3 p)
    {
        if (Shape == AlpineLightShape.None)
        {
            return 1f;
        }

        float inside = InsideDistance(p);
        return Feather > 0f ? Math.Clamp(inside / Feather, 0f, 1f) : inside >= 0f ? 1f : 0f;
    }

    /// <summary>Radius of a sphere about the centre enclosing the volume (alpine_dir_light.h:308-322).</summary>
    public float BoundingRadius => Shape switch
    {
        AlpineLightShape.Box => MathF.Sqrt((HalfX * HalfX) + (HalfY * HalfY) + (HalfZ * HalfZ)),
        AlpineLightShape.Sphere => Radius,
        AlpineLightShape.Cylinder => MathF.Sqrt((Radius * Radius) + (HalfLength * HalfLength)),
        _ => float.PositiveInfinity,
    };

    /// <summary>Distance along unit <paramref name="dir"/> from <paramref name="origin"/> to where the ray leaves the volume (alpine_dir_light.h:324-384).</summary>
    public float RayExitDistance(Vec3 origin, Vec3 dir)
    {
        const float eps = 1e-12f;
        Vec3 o = origin.Sub(Center);
        switch (Shape)
        {
            case AlpineLightShape.Box:
            {
                Span<float> ol = stackalloc float[] { o.Dot(BoxRight), o.Y, o.Dot(BoxForward) };
                Span<float> dl = stackalloc float[] { dir.Dot(BoxRight), dir.Y, dir.Dot(BoxForward) };
                Span<float> h = stackalloc float[] { HalfX, HalfY, HalfZ };
                float tExit = float.PositiveInfinity;
                for (int i = 0; i < 3; i++)
                {
                    if (MathF.Abs(ol[i]) > h[i])
                    {
                        return 0f;
                    }

                    if (MathF.Abs(dl[i]) > eps)
                    {
                        tExit = MathF.Min(tExit, ((dl[i] > 0f ? h[i] : -h[i]) - ol[i]) / dl[i]);
                    }
                }

                return MathF.Max(tExit, 0f);
            }

            case AlpineLightShape.Sphere:
            {
                float c = o.Dot(o) - (Radius * Radius);
                if (c > 0f)
                {
                    return 0f;
                }

                float b = o.Dot(dir);
                return MathF.Max(-b + MathF.Sqrt(MathF.Max((b * b) - c, 0f)), 0f);
            }

            case AlpineLightShape.Cylinder:
            {
                float s0 = o.Dot(Axis), sd = dir.Dot(Axis);
                Vec3 op = o.Sub(Axis.Scale(s0)), dp = dir.Sub(Axis.Scale(sd));
                float cr = op.Dot(op) - (Radius * Radius);
                if (cr > 0f || MathF.Abs(s0) > HalfLength)
                {
                    return 0f;
                }

                float tSide = float.PositiveInfinity;
                float a = dp.Dot(dp);
                if (a > eps)
                {
                    float b = op.Dot(dp);
                    tSide = (-b + MathF.Sqrt(MathF.Max((b * b) - (a * cr), 0f))) / a;
                }

                float tCap = MathF.Abs(sd) > eps ? ((sd > 0f ? HalfLength : -HalfLength) - s0) / sd : float.PositiveInfinity;
                return MathF.Max(MathF.Min(tSide, tCap), 0f);
            }

            default:
                return float.PositiveInfinity;
        }
    }
}

/// <summary>
/// Alpine Faction's directional lights as its baker treats them: the level sun (when enabled) and every
/// Directional Light object that starts on, each a temporary type-1 scene light (editor_patch/lightmap.cpp:2074-2150).
/// Read from the level's on-disk data only — the <c>alpine_level_properties</c> sun fields and the 0x0AFBAE0C chunk.
/// </summary>
public static class AlpineDirectionalLights
{
    /// <summary>The most Directional Light objects a bake takes (alpine_dir_light.h:19, lightmap.cpp:1557-1558).</summary>
    public const int MaxLights = 1024;

    private const float DegToRad = 3.14159265358979f / 180f;

    /// <summary>The level's baked directional lights (empty for a level with no sun and no Directional Light objects).</summary>
    public static List<EngineLight> FromLevel(RflFile rfl, ICollection<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        var lights = new List<EngineLight>();
        if (AlpineLightmapSettings.FindProperties(rfl) is { EnableSun: not 0 } props)
        {
            lights.Add(Sun(props));
        }

        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId != (uint)SectionType.AlpineDirectionalLights)
            {
                continue;
            }

            if (s.Content is null && RflSectionRegistry.TryParse(s, rfl.Context, out IRflSectionContent? parsed))
            {
                s.Content = parsed;
            }

            if (s.Content is not AlpineDirectionalLightsSection section)
            {
                continue;
            }

            int taken = 0;
            foreach (AlpineDirectionalLight d in section.Records)
            {
                if (Object(d) is not EngineLight l)
                {
                    continue; // starts off
                }

                if (taken == MaxLights)
                {
                    log?.Add($"Only {MaxLights} directional lights are baked; the rest are left out.");
                    break;
                }

                lights.Add(l);
                taken++;
            }
        }

        return lights;
    }

    /// <summary>
    /// The unit vector toward the sun for a yaw and pitch in degrees (common/include/common/lighting/alpine_lighting.h:29-37),
    /// after Alpine's sanitising of the stored values (editor_patch/level.h:1096-1110).
    /// </summary>
    public static Vec3 SunToLight(float yawDeg, float pitchDeg)
    {
        float yaw = float.IsFinite(yawDeg) ? yawDeg % 360f : 0f;
        if (yaw < 0f)
        {
            yaw += 360f;
        }

        float pitch = Math.Clamp(float.IsFinite(pitchDeg) ? pitchDeg : 90f, 0f, 90f);
        float y = yaw * DegToRad, p = pitch * DegToRad;
        float cp = MathF.Cos(p);
        return new Vec3(cp * MathF.Sin(y), MathF.Sin(p), cp * MathF.Cos(y));
    }

    /// <summary>The level sun (lightmap.cpp:2115-2123): unbounded, sky passes, its own liquid / shadow / spread settings.</summary>
    public static EngineLight Sun(AlpineLevelPropertiesSection props)
    {
        float intensity = Math.Clamp(float.IsFinite(props.SunIntensity) ? props.SunIntensity : 1f, 0f, 10f);
        float spread = Math.Clamp(float.IsFinite(props.SunSpreadAngle) ? props.SunSpreadAngle : 0f, 0f, 45f);
        return Make(
            SunToLight(props.SunYaw, props.SunPitch), intensity, props.SunColorR, props.SunColorG, props.SunColorB,
            props.SunCastBakedShadows != 0, spread, volume: null, liquidOccludes: props.SunLiquidOccludes != 0,
            skyPasses: true, outsideCasts: true);
    }

    /// <summary>
    /// A Directional Light object as Alpine bakes it, after <c>sanitize_record</c> (alpine_dir_light.h:144-212), or
    /// null when it starts off (lightmap.cpp:2125-2149).
    /// </summary>
    public static EngineLight? Object(AlpineDirectionalLight d)
    {
        byte on = d.InitiallyOn > 1 ? (byte)1 : d.InitiallyOn;
        if (on == 0)
        {
            return null;
        }

        Vec3 travel = SanitizedTravel(d.Orientation.Forward);
        float intensity = Clamp(d.Intensity, 0f, 10f, 1f);
        float spread = Clamp(d.Spread, 0f, 45f, 0f);
        byte shape = d.Shape > 3 ? (byte)0 : d.Shape;
        float ex = Clamp(d.ExtentX, 0f, 10000f, 10f), ey = Clamp(d.ExtentY, 0f, 10000f, 10f), ez = Clamp(d.ExtentZ, 0f, 10000f, 10f);
        float feather = Clamp(d.Feather, 0f, 10000f, 0f);
        Vec3 pos = new(
            float.IsFinite(d.Position.X) ? d.Position.X : 0f,
            float.IsFinite(d.Position.Y) ? d.Position.Y : 0f,
            float.IsFinite(d.Position.Z) ? d.Position.Z : 0f);

        AlpineLightVolume? volume = null;
        if (shape != 0)
        {
            float yaw = NormalizeDegrees(d.BoxYaw) * DegToRad;
            float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
            volume = new AlpineLightVolume
            {
                Shape = (AlpineLightShape)shape,
                Center = pos,
                Axis = travel,
                Feather = feather,
                BoxRight = new Vec3(c, 0f, -s),
                BoxForward = new Vec3(s, 0f, c),
                HalfX = shape == 1 ? ex * 0.5f : 0f,
                HalfY = shape == 1 ? ey * 0.5f : 0f,
                HalfZ = shape == 1 ? ez * 0.5f : 0f,
                Radius = shape is 2 or 3 ? ex : 0f,
                HalfLength = shape == 3 ? ey * 0.5f : 0f,
            };
        }

        return Make(
            travel.Scale(-1f), intensity, d.Color.R, d.Color.G, d.Color.B, Bool(d.CastBakedShadows, 1), spread, volume,
            liquidOccludes: Bool(d.LiquidOccludes, 1), skyPasses: Bool(d.SkyPasses, 1), outsideCasts: Bool(d.OutsideCasts, 1));
    }

    /// <summary>
    /// The soft-shadow ray directions: the axis, then two rings of four at 0.55× and 1.0× tan(spread), phases 45° and
    /// 0° (lightmap.cpp:1845-1874). Fixed, so bakes are reproducible.
    /// </summary>
    public static Vec3[] ConeDirections(Vec3 axis, float spreadDeg)
    {
        if (spreadDeg <= 0f)
        {
            return new[] { axis };
        }

        Vec3 up = MathF.Abs(axis.Y) < 0.9f ? new Vec3(0f, 1f, 0f) : new Vec3(1f, 0f, 0f);
        Vec3 u = up.Cross(axis);
        float len = MathF.Sqrt(u.Dot(u));
        if (!(len >= 1e-6f))
        {
            return new[] { axis };
        }

        u = u.Scale(1f / len);
        Vec3 v = axis.Cross(u);
        float tanMax = MathF.Tan(spreadDeg * DegToRad);
        var dirs = new Vec3[9];
        dirs[0] = axis;
        int n = 1;
        for (int ring = 0; ring < 2; ring++)
        {
            float r = tanMax * (ring == 0 ? 0.55f : 1f);
            float phase = ring == 0 ? 45f : 0f;
            for (int k = 0; k < 4; k++)
            {
                float a = (phase + (90f * k)) * DegToRad;
                float cs = MathF.Cos(a) * r, sn = MathF.Sin(a) * r;
                Vec3 d = axis.Add(u.Scale(cs)).Add(v.Scale(sn));
                dirs[n++] = d.Scale(1f / MathF.Sqrt(d.Dot(d)));
            }
        }

        return dirs;
    }

    // Every directional light is a type-1 scene light of intensity ×4 with colour rgb/255: the ×4 offsets the
    // engine's fixed 0.25 directional gain (lightmap.cpp:2078-2094; RED.exe FUN_004894c0 case 1).
    private static EngineLight Make(
        Vec3 toLight, float intensity, byte r, byte g, byte b, bool castShadows, float spread, AlpineLightVolume? volume,
        bool liquidOccludes, bool skyPasses, bool outsideCasts)
    {
        float len = MathF.Sqrt(toLight.Dot(toLight));
        Vec3 axis = len >= 1e-6f ? toLight.Scale(1f / len) : new Vec3(0f, 1f, 0f);
        float k = intensity * 4f / 255f;
        return new EngineLight
        {
            Type = EngineLightType.Directional,
            Position = axis,
            Position2 = axis,
            SpotAxis = axis,
            Color = new Vec3(r * k, g * k, b * k),
            Range = float.PositiveInfinity,
            RangeSq = float.PositiveInfinity,
            CastsShadows = castShadows,
            Enabled = true,
            ShadowDirections = ConeDirections(axis, spread),
            Volume = volume,
            LiquidOccludes = liquidOccludes,
            SkyPasses = skyPasses,
            OutsideCasts = outsideCasts,
        };
    }

    // sanitize_basis's travel direction: a non-finite or near-zero fvec falls back to straight down (alpine_dir_light.h:144-155).
    private static Vec3 SanitizedTravel(Vec3 f)
    {
        bool finite = float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z);
        float len = finite ? MathF.Sqrt(f.Dot(f)) : 0f;
        return len >= 1e-6f && float.IsFinite(len) ? f.Scale(1f / len) : new Vec3(0f, -1f, 0f);
    }

    private static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    private static bool Bool(byte v, byte fallback) => (v > 1 ? fallback : v) != 0;

    private static float NormalizeDegrees(float deg)
    {
        if (!float.IsFinite(deg))
        {
            return 0f;
        }

        float r = deg % 360f;
        if (r < 0f)
        {
            r += 360f;
        }

        return r >= 360f ? 0f : r;
    }
}
