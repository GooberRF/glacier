namespace Ged.Core.Lighting;

/// <summary>The base bake algorithm (radio) selected in Level ▸ Lightmap Method.</summary>
public enum LightingBase
{
    /// <summary>RED Classic (stock) — the byte-parity direct kernel, unchanged. The default.</summary>
    RedClassic,

    /// <summary>Adds indirect light: after the direct pass, one or two cosine-weighted gather bounces.</summary>
    Bounced,
}

/// <summary>
/// The selected lightmap bake method (feature 1): a base algorithm plus composable
/// modifiers. The default (<see cref="LightingBase.RedClassic"/> with no modifiers) runs
/// the existing parity kernel exactly, so the parity gates stay byte-identical. Persisted
/// per-level in the .gedlayout.json sidecar and as a global default in settings.
/// </summary>
public sealed class LightingMethod
{
    /// <summary>The base algorithm (radio): RED Classic or Bounced.</summary>
    public LightingBase Base { get; set; } = LightingBase.RedClassic;

    /// <summary>Gather bounces when <see cref="Base"/> is Bounced: 1 or 2.</summary>
    public int Bounces { get; set; } = 1;

    /// <summary>Modifier: per-texel hemisphere ambient occlusion (multiplies the ambient term only).</summary>
    public bool AmbientOcclusion { get; set; }

    /// <summary>Modifier: N-sample area soft shadows (replaces the stock 2-sample penumbra).</summary>
    public bool SoftShadows { get; set; }

    /// <summary>
    /// Modifier (item 6 amendment): High-Resolution Lightmaps — raise the atlas texel density
    /// (256×256 pages, 255-texel fragments, ppm ×4) so projection cookies / gobos resolve crisply.
    /// Format-safe against RF.exe. A build-time surface concern, not a bake-kernel change, so it
    /// does not affect the parity kernel — only the surface resolution.
    /// </summary>
    public bool HighResLightmaps { get; set; }

    /// <summary>
    /// Modifier: cross-surface lightmap seam blend. Averages the abutting edge texels of
    /// coplanar surfaces a portal split into different rooms (e.g. a floor under a doorway),
    /// removing the visible seam RED's per-room lightmaps leave there. Reproduces Alpine's
    /// <c>-smoothlights</c> cross-room blend (lightmap.cpp:81-110). Orthogonal to the base, so
    /// RED Classic + Seam Blend is stock RED lighting with the doorway seam closed. Off by
    /// default — the RED-Classic default bake stays byte-parity with RED's own references.
    /// </summary>
    public bool SeamBlend { get; set; }

    /// <summary>
    /// Modifier: Corner Leak Fix. Closes two measured corner light-leak classes RED leaves:
    /// (1) AMBIENT leak — a texel takes its own surface's room ambient when that room contains
    /// it, instead of the smallest overlapping room's bbox ambient (a bright neighbour's ambient
    /// no longer bleeds onto a dark room's corner floor); (2) SHADOW leak — a fragment-overhang
    /// texel clamped onto its surface's bbox edge no longer starts its shadow ray exactly on a
    /// coincident room-boundary wall (which let a neighbouring room's light leak through). Off by
    /// default — the RED-Classic default bake stays byte-parity with RED's own references.
    /// </summary>
    public bool CornerLeakFix { get; set; }

    /// <summary>
    /// Modifier: Smooth Gutter Normals. On a smoothed multi-polygon surface, texels that land in
    /// the gutter between/around the face polygons (the fragment min-clamp overhang) no longer
    /// fall back to the FLAT plane normal — they weld to the nearest face's interpolated vertex
    /// normal, removing the normal discontinuity (a faceted rim) at polygon boundaries inside a
    /// smoothed surface. Also switches the smoothing-group vertex-normal average from RED's hard
    /// 90° hemisphere cutoff to an angle-weighted (cos-weighted) average, softening near-cutoff
    /// normal flips. Off by default — the RED-Classic default bake stays byte-parity.
    /// </summary>
    public bool SmoothGutters { get; set; }

    /// <summary>
    /// Modifier: "Movers cast shadows". Includes mover brushes (elevators, doors, lifts) as shadow
    /// occluders at their rest pose, so they shadow the static world, each other, and themselves.
    /// DEFAULT ON — an owner-decided quality deviation (a moving object's rest-pose shadow reads as more
    /// grounded), unlike RED which omits it (a mover's baked shadow would ghost when it animates). The
    /// parity gates pin the underlying <see cref="LightingOptions.MoverShadows"/> OFF (RED-matching), so
    /// this default does not disturb the byte-identity ratchets, which run raw options.
    /// </summary>
    public bool MoverShadows { get; set; } = true;

    /// <summary>True when this is exactly the stock RED Classic path (no bounce, no modifiers).</summary>
    public bool IsRedClassicDefault =>
        Base == LightingBase.RedClassic && !AmbientOcclusion && !SoftShadows && !HighResLightmaps && !SeamBlend
        && !CornerLeakFix && !SmoothGutters;

    /// <summary>The effective gather-bounce count (0 unless Bounced), clamped to 1 or 2.</summary>
    public int EffectiveBounces =>
        Base == LightingBase.Bounced ? (Bounces >= 2 ? 2 : 1) : 0;

    public LightingMethod Clone() => new()
    {
        Base = Base,
        Bounces = Bounces,
        AmbientOcclusion = AmbientOcclusion,
        SoftShadows = SoftShadows,
        HighResLightmaps = HighResLightmaps,
        SeamBlend = SeamBlend,
        CornerLeakFix = CornerLeakFix,
        SmoothGutters = SmoothGutters,
        MoverShadows = MoverShadows,
    };

    /// <summary>A short human label for the status bar / report ("RED Classic", "Bounced ×2 +AO").</summary>
    public string DisplayName()
    {
        string b = Base == LightingBase.Bounced ? $"Bounced ×{EffectiveBounces}" : "RED Classic";
        string mods =
            (AmbientOcclusion ? " +AO" : string.Empty) +
            (SoftShadows ? " +SoftShadows" : string.Empty) +
            (HighResLightmaps ? " +HiRes" : string.Empty) +
            (SeamBlend ? " +SeamBlend" : string.Empty) +
            (CornerLeakFix ? " +LeakFix" : string.Empty) +
            (SmoothGutters ? " +SmoothGutters" : string.Empty) +
            (MoverShadows ? string.Empty : " -MoverShadows");
        return b + mods;
    }
}
