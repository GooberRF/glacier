using System;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>Engine light shape (matches gr::Light +0x08: 2=point, 3=spot, 4=tube).</summary>
public enum EngineLightType
{
    Point = 2,
    Spot = 3,
    Tube = 4,
}

/// <summary>
/// The runtime <c>gr::Light</c> a level light is baked as — the flattened form the
/// kernel consumes. Built from a <see cref="Light"/> by <see cref="FromModel"/>,
/// which reproduces RF.exe's level→light prep (FUN_0045f740): the RFL
/// <c>light_flags</c> bitfield is unpacked, the colour is premultiplied by the
/// current-state intensity, FOV degrees become cone thresholds, and dropoff_type
/// selects the attenuation curve. Editor-only and disabled lights are flagged so
/// the caller can exclude them from the game bake while still previewing them.
/// </summary>
public readonly struct EngineLight
{
    public EngineLightType Type { get; init; }

    /// <summary>Light position (tube endpoint 1).</summary>
    public Vec3 Position { get; init; }

    /// <summary>Tube endpoint 2 (== <see cref="Position"/> for point/spot).</summary>
    public Vec3 Position2 { get; init; }

    /// <summary>Spot cone axis: the light's forward direction (unit).</summary>
    public Vec3 SpotAxis { get; init; }

    /// <summary>Premultiplied float RGB = (colour/255) × intensity (gr::Light +0x40/44/48).</summary>
    public Vec3 Color { get; init; }

    public float Range { get; init; }

    public float RangeSq { get; init; }

    /// <summary>Attenuation table index (dropoff_type): 0 linear, 1 squared, 2 cosine, 3 sqrt.</summary>
    public int AttenAlgo { get; init; }

    /// <summary>Spot-only distance-atten offset (intensity_at_max_range); 0 = standard falloff.</summary>
    public float DistAttenOffset { get; init; }

    /// <summary>−cos(fov/2) inner cone threshold (spot).</summary>
    public float InnerThreshold { get; init; }

    /// <summary>−cos((fov+dropoff)/2) outer cone threshold (spot).</summary>
    public float OuterThreshold { get; init; }

    /// <summary>Squared cone ramp (programmatic lights only; false for level lights).</summary>
    public bool SquaredFovFalloff { get; init; }

    /// <summary>True when this light casts (raycast) shadows during the bake.</summary>
    public bool CastsShadows { get; init; }

    /// <summary>Tube (area) lights use a 2-sample penumbra; point/spot use a single sample.</summary>
    public bool IsArea { get; init; }

    /// <summary>Enabled flag (RFL light_flags bit 0x8) — disabled lights are excluded from the game bake.</summary>
    public bool Enabled { get; init; }

    /// <summary>From the editor_only_lights section — excluded from the game bake, available for preview.</summary>
    public bool EditorOnly { get; init; }

    /// <summary>Optional greyscale projection cookie (item 4); null = no gobo modulation.</summary>
    public LightCookie? Cookie { get; init; }

    /// <summary>Cookie U axis (the light's Right, perpendicular to the spot/point direction).</summary>
    public Vec3 CookieRight { get; init; }

    /// <summary>Cookie V axis (the light's Up).</summary>
    public Vec3 CookieUp { get; init; }

    /// <summary>tan of the spot outer half-angle: the cookie spans the outer cone at each distance.</summary>
    public float CookieConeTan { get; init; }

    /// <summary>
    /// Cookie projection sharpness (item 6): 1.0 = crisp (raw sample), 0.0 = fully blurred.
    /// EngineLight is a struct (no field initializer), so this defaults to 0 when unset —
    /// always construct cookie'd lights via <see cref="FromModel"/>, which sets it (default 1.0).
    /// </summary>
    public float CookieSharpness { get; init; }

    /// <summary>The premultiplied colour is all-zero (nothing to add).</summary>
    public bool IsBlack => Color.X <= 0f && Color.Y <= 0f && Color.Z <= 0f;

    /// <summary>Axis-aligned influence bounds (position ± range), for range-overlap surface culling.</summary>
    public Aabb Bounds
    {
        get
        {
            Vec3 mn = Vec3Math.Min(Position, Position2);
            Vec3 mx = Vec3Math.Max(Position, Position2);
            var r = new Vec3(Range, Range, Range);
            return new Aabb(mn.Sub(r), mx.Add(r));
        }
    }

    /// <summary>
    /// Builds the engine light from an RFL <see cref="Light"/>, unpacking
    /// <c>light_flags</c> and premultiplying colour × intensity exactly as RF's
    /// level prep does. <paramref name="editorOnly"/> marks lights from the
    /// editor_only_lights section.
    /// </summary>
    public static EngineLight FromModel(Light l, bool editorOnly, LightCookie? cookie = null, float cookieSharpness = 1f)
    {
        uint flags = l.Flags;
        bool enabled = (flags & 0x8u) != 0;
        bool shadowCasting = (flags & 0x4u) != 0;
        int rflType = (int)((flags & 0x30u) >> 4);   // 1 omni, 2 spot, 3 tube

        // Current-state intensity (RF FUN_0045f740, verified against the corpus:
        // glass_house's initial-state-OFF centre light is absent from RED's baked
        // atlas): off states use off_intensity, on/alternating-on use on_intensity,
        // unset defaults to 1.0.
        int initialState = (int)((flags & 0xF00u) >> 8); // 1 off, 2 on, 3/4 alternating
        float intensity = initialState switch
        {
            1 => l.OffIntensity,
            2 => l.OnIntensity,
            3 => l.OffIntensity,
            4 => l.OnIntensity,
            _ => 1f,
        };

        var color = new Vec3(
            l.Color.R / 255f * intensity,
            l.Color.G / 255f * intensity,
            l.Color.B / 255f * intensity);

        EngineLightType type = rflType switch
        {
            2 => EngineLightType.Spot,
            3 => EngineLightType.Tube,
            _ => EngineLightType.Point,
        };

        // Axis rows, verified against RF.exe FUN_0045f740 AND empirically against
        // the RED-baked corpus lightmaps (dm04's terrain spot batteries land dead
        // centre on this axis, beam·axis ≈ 0.97): the engine takes the spot dir
        // from its Matrix3+0x30 (fvec) and the tube axis from +0x18 (rvec), and the
        // keyed matrix reader maps the RFL mat3 (file order forward, right, up)
        // into the engine layout (rvec, uvec, fvec) — so the spot beam is the
        // FILE's forward row and a tube extends along the FILE's right row
        // (±tube_width/2, const 0x3f000000 = 0.5).
        Vec3 spotAxis = l.Rotation.Forward.Normalized();
        Vec3 tubeAxis = l.Rotation.Right.Normalized();
        float range = MathF.Max(0f, l.Range);

        if (type == EngineLightType.Tube)
        {
            Vec3 half = tubeAxis.Scale(MathF.Max(0f, l.TubeLightWidth) * 0.5f);
            return new EngineLight
            {
                Type = type,
                Position = l.Position.Sub(half),
                Position2 = l.Position.Add(half),
                SpotAxis = tubeAxis,
                Color = color,
                Range = range,
                RangeSq = range * range,
                AttenAlgo = Math.Clamp(l.DropoffType, 0, 3),
                CastsShadows = shadowCasting,
                IsArea = true,
                Enabled = enabled,
                EditorOnly = editorOnly,
            };
        }

        float outerThreshold = type == EngineLightType.Spot ? LightKernel.OuterThreshold(l.Fov, l.FovDropoff) : 0f;
        return new EngineLight
        {
            Type = type,
            Position = l.Position,
            Position2 = l.Position,
            SpotAxis = spotAxis,
            Color = color,
            Range = range,
            RangeSq = range * range,
            AttenAlgo = Math.Clamp(l.DropoffType, 0, 3),
            DistAttenOffset = type == EngineLightType.Spot ? l.IntensityAtMaxRange : 0f,
            InnerThreshold = type == EngineLightType.Spot ? LightKernel.InnerThreshold(l.Fov) : 0f,
            OuterThreshold = outerThreshold,
            SquaredFovFalloff = false,
            CastsShadows = shadowCasting,
            IsArea = false,
            Enabled = enabled,
            EditorOnly = editorOnly,

            // Cookie gobo (item 4): the cookie's U/V ride the light's Right/Up axes; for a spot the
            // cookie spans the OUTER cone, so its half-width per unit axial distance is tan(outerHalf).
            Cookie = cookie,
            CookieRight = l.Rotation.Right.Normalized(),
            CookieUp = l.Rotation.Up.Normalized(),
            CookieConeTan = type == EngineLightType.Spot ? SpotConeTan(outerThreshold) : 0f,
            CookieSharpness = cookieSharpness,
        };
    }

    /// <summary>tan of the spot outer half-angle from its negated-cosine threshold (−cos(half)).</summary>
    private static float SpotConeTan(float outerThreshold)
    {
        float cosOuter = -outerThreshold; // cos(outer half-angle), (0,1]
        if (cosOuter <= 1e-3f)
        {
            return 1e3f; // near-hemispherical cone: effectively flat gobo plane
        }

        float sinOuter = MathF.Sqrt(MathF.Max(0f, 1f - (cosOuter * cosOuter)));
        return sinOuter / cosOuter;
    }
}
