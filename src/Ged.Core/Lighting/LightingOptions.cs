using System;
using System.Threading;

namespace Ged.Core.Lighting;

/// <summary>Progress callback payload for a bake: stage text + n/m surface counter.</summary>
public readonly record struct BakeProgress(string Stage, int Current, int Total);

/// <summary>
/// Tunables for a lightmap bake. Defaults reproduce RED's stock baker with
/// Alpine's <c>-smoothlights</c> quality on (per-texel room ambient, cross-surface
/// edge blending, border replication) and the proportional hue-preserving clamp.
/// </summary>
public sealed class LightingOptions
{
    /// <summary>Raycast occluder shadows (Calculate Lighting); off = fullbright masks (Calculate Lighting w/o Shadows).</summary>
    public bool CastShadows { get; set; } = true;

    /// <summary>
    /// Keep RED's proportional hue-preserving overbright clamp (scale RGB by 255/max
    /// when a channel exceeds 255). Off = per-channel clamp, matching Alpine's
    /// no-clamp load behaviour (preserves non-overbright channels).
    /// </summary>
    public bool ProportionalClamp { get; set; } = true;

    /// <summary>Alpine smoothlights quality: per-texel room ambient + cross-surface edge blending + border replication.</summary>
    public bool Quality { get; set; } = true;

    /// <summary>
    /// Edge-aware smoothing passes over the float lightmap buffer before the
    /// overbright clamp (RED's pass-2 blend). Spreads clamped highlight energy into
    /// neighbours for the soft even wash RED produces. 0 disables.
    /// </summary>
    public int SmoothIterations { get; set; } = 1;

    /// <summary>
    /// Cross-surface lightmap seam blend: average the abutting edge texels of coplanar surfaces a
    /// portal split into different rooms (a floor under a doorway), removing the visible seam.
    /// Reproduces Alpine's <c>-smoothlights</c> cross-room blend (lightmap.cpp:81-110). OFF by
    /// default (like Alpine's opt-in <c>-smoothlights</c>) — the RED-Classic default bake stays
    /// byte-identical to RED's own seam-carrying references. Turned on by the
    /// <see cref="LightingMethod.SeamBlend"/> author option via <see cref="WithMethod"/>. See
    /// <see cref="CrossSurfaceBlend"/>.
    /// </summary>
    public bool CrossRoomBlend { get; set; }

    // ---- Feature 1: lightmap method (0/off = stock RED Classic — byte-identical) ----

    /// <summary>Indirect gather bounces after the direct pass: 0 = none (RED Classic), 1 or 2 = Bounced.</summary>
    public int LightBounces { get; set; }

    /// <summary>Diffuse albedo approximation for the bounce gather (constant, since GED does not sample diffuse maps here).</summary>
    public float BounceAlbedo { get; set; } = 0.5f;

    /// <summary>Cosine-weighted hemisphere rays per texel for each gather bounce.</summary>
    public int BounceSamples { get; set; } = 16;

    /// <summary>Modifier: multiply the ambient term by a per-texel hemisphere occlusion factor.</summary>
    public bool AmbientOcclusion { get; set; }

    /// <summary>Hemisphere rays for the AO factor (M).</summary>
    public int AoSamples { get; set; } = 24;

    /// <summary>AO ray max distance (occluders past this do not darken); nearer hits darken more.</summary>
    public float AoRadius { get; set; } = 3f;

    /// <summary>Modifier: replace the stock 2-sample penumbra with N-sample area soft shadows.</summary>
    public bool SoftShadows { get; set; }

    /// <summary>Area-shadow samples per light (N, kept modest).</summary>
    public int SoftShadowSamples { get; set; } = 8;

    /// <summary>Jitter radius (metres) of the sampled light area for soft shadows.</summary>
    public float SoftShadowRadius { get; set; } = 0.4f;

    /// <summary>
    /// Modifier: Corner Leak Fix. Enables (1) own-room-preferred ambient selection — a texel
    /// inside its surface's own room takes that room's ambient instead of the smallest overlapping
    /// room's; and (2) edge-aware shadow-ray bias — a texel clamped onto its surface's bbox edge
    /// has its shadow-ray origin nudged into the surface interior so it does not start on a
    /// coincident room-boundary wall. OFF keeps the byte-parity path exactly.
    /// </summary>
    public bool CornerLeakFix { get; set; }

    /// <summary>
    /// Modifier: weld gutter texels of a smoothed surface to the nearest face's interpolated
    /// vertex normal (and on-face position) instead of the flat plane normal, removing the normal
    /// discontinuity at polygon boundaries. OFF keeps the flat-normal fallback (byte-parity).
    /// </summary>
    public bool SmoothGutterNormals { get; set; }

    /// <summary>
    /// Modifier: build smoothing-group vertex normals with an angle-weighted (cos-weighted) average
    /// instead of RED's hard 90° hemisphere cutoff, softening near-cutoff normal flips. OFF keeps
    /// RED's unweighted &gt;0 cutoff (byte-parity). Consumed by the smooth-face builders.
    /// </summary>
    public bool AngleWeightedNormals { get; set; }

    /// <summary>True when this is exactly the stock RED Classic path (no bounce, AO or soft shadows).</summary>
    public bool IsRedClassicMethod =>
        LightBounces <= 0 && !AmbientOcclusion && !SoftShadows && !CornerLeakFix
        && !SmoothGutterNormals && !AngleWeightedNormals;

    /// <summary>Applies a <see cref="LightingMethod"/>'s selections onto these options (in place).</summary>
    public LightingOptions WithMethod(LightingMethod? method)
    {
        if (method is null)
        {
            return this;
        }

        LightBounces = method.EffectiveBounces;
        AmbientOcclusion = method.AmbientOcclusion;
        SoftShadows = method.SoftShadows;
        CrossRoomBlend = method.SeamBlend;
        CornerLeakFix = method.CornerLeakFix;
        SmoothGutterNormals = method.SmoothGutters;
        AngleWeightedNormals = method.SmoothGutters;
        return this;
    }

    /// <summary>Stock-target level: warn in the build report when a face is lit by more than 64 lights.</summary>
    public bool WarnStockLightLimit { get; set; }

    /// <summary>
    /// Item 4 — resolves a light UID to its decoded greyscale projection cookie (gobo), or null when
    /// the light has none. Set by the build layer from the object-metadata chunk + VFS; the two bake
    /// paths (compile bake, exact-CPU preview relight) both consult it when building engine lights.
    /// </summary>
    public Func<int, LightCookie?>? CookieResolver { get; set; }

    /// <summary>
    /// Item 6 — resolves a light UID to its cookie projection sharpness (1.0 crisp … 0.0 blurred);
    /// 1.0 when unset. Threaded alongside <see cref="CookieResolver"/> into the engine lights.
    /// </summary>
    public Func<int, float>? CookieSharpnessResolver { get; set; }

    /// <summary>Max worker threads (0 = <see cref="Environment.ProcessorCount"/>).</summary>
    public int MaxThreads { get; set; }

    /// <summary>Optional progress sink (stage + n/m), invoked from the bake driver thread.</summary>
    public Action<BakeProgress>? Progress { get; set; }

    /// <summary>Cancellation token; checked between surfaces.</summary>
    public CancellationToken Cancellation { get; set; } = CancellationToken.None;
}

/// <summary>The measured result of a bake, for the build report / perf artifact.</summary>
public sealed class BakeStats
{
    public int Surfaces { get; set; }

    public int Lights { get; set; }

    public int Texels { get; set; }

    public int MaxLightsOnAnyFace { get; set; }

    public double ElapsedMs { get; set; }

    public int OverLimitFaces { get; set; }

    /// <summary>Edge-texel pairs blended by the cross-surface seam blend (0 on the RED-Classic path).</summary>
    public int SeamTexelsBlended { get; set; }
}
