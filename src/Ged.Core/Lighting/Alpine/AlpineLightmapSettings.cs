using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;

namespace Ged.Core.Lighting.Alpine;

/// <summary>Which lightmap pipeline a bake runs (the level's Alpine "Legacy lighting" property).</summary>
public enum LightmapPipeline
{
    /// <summary>
    /// Alpine Faction's lighting pipeline, the default: the stock lightmaps plus Alpine's higher-density surface
    /// charts (RFL chunk 0x0AFBAE09) that Alpine Faction draws on Direct3D 11.
    /// </summary>
    Alpine,

    /// <summary>The stock RED lightmaps only (level property <c>legacy_lighting</c> set).</summary>
    Legacy,
}

/// <summary>
/// The Alpine lightmap settings of one level, read from its <c>alpine_level_properties</c> chunk the way Alpine RED
/// reads them (editor_patch/level.h:992-1005, 1079-1082 <c>surface_charts_enabled</c>; alpine_lightmap.h:66-79). A
/// level without the chunk takes Alpine's defaults — Alpine lighting at the default density — which is also what
/// Glacier saves for it (pre-306 levels switch to Alpine lighting on upgrade, ALPINE-COMPAT-SPEC §11.1 Q6).
/// </summary>
public sealed record AlpineLightmapSettings
{
    /// <summary>The pipeline (Legacy when <c>legacy_lighting</c> is set).</summary>
    public LightmapPipeline Pipeline { get; init; } = LightmapPipeline.Alpine;

    /// <summary>The stored <c>lightmap_density</c>: 0 = default (8), 1–128 texels per unit, 255 = Alpine charts off.</summary>
    public byte Density { get; init; }

    /// <summary>
    /// Alpine RED's <c>surface_charts_enabled</c>: the Alpine pipeline with density not Off. Only then does a bake
    /// write surface charts, and only then may a saved level carry them (alpine_lightmaps.cpp:1792-1810).
    /// </summary>
    public bool ChartsEnabled => Pipeline == LightmapPipeline.Alpine && Density != AlpineLightmapLayout.DensityOff;

    /// <summary>The texels per world unit a bake starts from (it halves until the charts fit the page budget).</summary>
    public byte EffectiveDensity => AlpineLightmapLayout.EffectiveDensity(Density == AlpineLightmapLayout.DensityOff ? (byte)0 : Density);

    /// <summary>The layer codec a bake writes: BC7 (Alpine RED's shipping codec), or raw RGB8 like Alpine RED's <c>-rawlightmaps</c>.</summary>
    public AlpineLightmapCodec Codec { get; init; } = AlpineLightmapCodec.Bc7;

    /// <summary>The level's <c>lightmap_compression</c> (alpine_lightmap.h:797-811).</summary>
    public AlpineLightmapCompression Compression { get; init; }

    /// <summary>The level's <c>highres_lightmaps</c>: 256² stock pages, 254-texel fragments, 4× density (lightmap.cpp:44-50, 3945-3993).</summary>
    public bool HighRes { get; init; }

    /// <summary>The level's "D3D11-only lightmaps" setting (<c>d3d11_only_lightmaps</c> bit1, alpine_lightmap.h:58-64).</summary>
    public bool D3D11Only { get; init; }

    /// <summary><c>alpha_faces_occlude</c>: faces with an alpha texture block light (lightmap.cpp:139-143, 1899-1901).</summary>
    public bool AlphaFacesOcclude { get; init; }

    /// <summary><c>invisible_faces_occlude</c>: invisible faces block light reaching their front (lightmap.cpp:133-137, 1888-1903).</summary>
    public bool InvisibleFacesOcclude { get; init; }

    /// <summary><c>alpha_tested_occlusion</c>, active only with the Alpine pipeline (level.h:1073-1076).</summary>
    public bool AlphaTestedOcclusion { get; init; }

    /// <summary><c>meshes_occlude</c>: Alpine mesh objects cast baked shadows (lightmap.cpp:1206-1240).</summary>
    public bool MeshesOcclude { get; init; }

    /// <summary>The brushes flagged "No shadow cast" (lightmap.cpp:151-236).</summary>
    public IReadOnlyCollection<int> NoShadowCastBrushUids { get; init; } = Array.Empty<int>();

    /// <summary>
    /// True when the fixed pipeline's occluder rules apply (Alpine pipeline): the occluder set holds every compiled face
    /// but portals and see-through faces, and each light skips the classes its rules exclude (lightmap.cpp:1100-1257,
    /// 1876-1918). Legacy lighting keeps the stock occluder set.
    /// </summary>
    public bool FixedPipeline => Pipeline == LightmapPipeline.Alpine;

    /// <summary>
    /// The level's terrains whose compiled geometry is current (copies, in level order): a relight gives each a terrain
    /// chart (editor_patch/alpine_lightmaps.cpp:580-622). A build replaces them with the terrains it compiled.
    /// </summary>
    public IReadOnlyList<Model.AlpineTerrain> Terrains { get; init; } = Array.Empty<Model.AlpineTerrain>();

    /// <summary>The level's built terrains, as <see cref="Terrains"/> holds them.</summary>
    public static IReadOnlyList<Model.AlpineTerrain> BuiltTerrains(RflFile rfl)
    {
        var list = new List<Model.AlpineTerrain>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.AlpineTerrain && s.Content is AlpineTerrainSection { IsReadOnly: false } ts)
            {
                foreach (Model.AlpineTerrain t in ts.Terrains)
                {
                    if (t.BuildIsCurrent)
                    {
                        Model.AlpineTerrain copy = t.DeepCopy();
                        Model.AlpineTerrainEdit.Sanitize(copy);
                        list.Add(copy);
                    }
                }
            }
        }

        return list;
    }

    /// <summary>The level's settings (Alpine defaults when it has no Alpine level properties chunk).</summary>
    public static AlpineLightmapSettings FromLevel(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        AlpineLevelPropertiesSection? props = FindProperties(rfl);
        return props is null
            ? new AlpineLightmapSettings { Codec = rfl.AlpineLightmapCodec, Terrains = BuiltTerrains(rfl) }
            : new AlpineLightmapSettings
            {
                Terrains = BuiltTerrains(rfl),
                Codec = rfl.AlpineLightmapCodec,
                Pipeline = props.LegacyLighting != 0 ? LightmapPipeline.Legacy : LightmapPipeline.Alpine,
                Density = props.LightmapDensity,
                Compression = AlpineLayerEncoder.FromWire(props.LightmapCompression),
                HighRes = props.HighresLightmaps != 0,
                D3D11Only = (props.D3D11OnlyLightmaps & AlpineLevelPropertiesSection.D3D11OnlySetting) != 0,
                AlphaFacesOcclude = props.AlphaFacesOcclude != 0,
                InvisibleFacesOcclude = props.InvisibleFacesOcclude != 0,
                AlphaTestedOcclusion = props.AlphaTestedOcclusion != 0 && props.LegacyLighting == 0,
                MeshesOcclude = props.MeshesOcclude != 0,
                NoShadowCastBrushUids = props.NoShadowCastBrushUids.ToArray(),
            };
    }

    /// <summary>
    /// Stores a pipeline and/or density on the level's Alpine level properties (creating the chunk when needed), as
    /// Alpine RED's Level Properties dialog does (editor_patch/level.cpp:2340, 2421). Returns true when anything
    /// changed. Density is clamped to Alpine's stored range: 0, 1–128, or 255 for Off (129–254 store as 128).
    /// </summary>
    public static bool Apply(RflFile rfl, LightmapPipeline? pipeline, int? density)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        if (pipeline is null && density is null)
        {
            return false;
        }

        RflSection host = rfl.GetOrCreateSection(
            SectionType.AlpineLevelProperties,
            () => new AlpineLevelPropertiesSection { Version = AlpineLevelPropertiesSection.CurrentVersion });
        if (host.Content is not AlpineLevelPropertiesSection props)
        {
            return false;
        }

        bool changed = false;
        if (pipeline is LightmapPipeline p)
        {
            byte legacy = p == LightmapPipeline.Legacy ? (byte)1 : (byte)0;
            if (props.LegacyLighting != legacy)
            {
                props.LegacyLighting = legacy;
                changed = true;
            }
        }

        if (density is int d)
        {
            byte stored = d switch
            {
                <= 0 => 0,
                >= AlpineLightmapLayout.DensityOff => AlpineLightmapLayout.DensityOff,
                > AlpineLightmapLayout.DensityMax => AlpineLightmapLayout.DensityMax,
                _ => (byte)d,
            };
            if (props.LightmapDensity != stored)
            {
                props.LightmapDensity = stored;
                changed = true;
            }
        }

        if (changed)
        {
            host.Dirty = true;
        }

        return changed;
    }

    /// <summary>
    /// The editor's form of <see cref="Apply(RflFile, LightmapPipeline?, int?)"/>: one undoable command (creating the
    /// Alpine level properties chunk inside it when the level has none, as the Level Properties dialog does). Returns
    /// true when anything changed.
    /// </summary>
    public static bool Apply(Editor.EditorDocument doc, LightmapPipeline? pipeline, int? density)
    {
        ArgumentNullException.ThrowIfNull(doc);
        AlpineLightmapSettings now = FromLevel(doc.Rfl);
        byte legacy = (pipeline ?? now.Pipeline) == LightmapPipeline.Legacy ? (byte)1 : (byte)0;
        byte stored = density is int d ? StoredDensity(d) : now.Density;
        bool hasChunk = Editing.AlpinePropertiesEdit.Find(doc) is not null;
        if (hasChunk && (legacy != 0) == (now.Pipeline == LightmapPipeline.Legacy) && stored == now.Density)
        {
            return false;
        }

        if (!hasChunk && legacy == 0 && stored == 0)
        {
            return false; // what a level without the chunk already means
        }

        Editing.AlpinePropertiesEdit edit = Editing.AlpinePropertiesEdit.Begin(doc);
        AlpineLevelPropertiesSection props = edit.Content;
        byte oldLegacy = props.LegacyLighting, oldDensity = props.LightmapDensity;
        doc.Undo.Execute(new Editor.RelayCommand(
            "Lightmap pipeline",
            () =>
            {
                edit.Attach();
                props.LegacyLighting = legacy;
                props.LightmapDensity = stored;
                edit.Host.Dirty = true;
            },
            () =>
            {
                props.LegacyLighting = oldLegacy;
                props.LightmapDensity = oldDensity;
                edit.Host.Dirty = true;
                edit.Detach();
            }));
        return true;
    }

    /// <summary>Alpine's stored density for a requested value: 0, 1–128, or 255 for Off (129–254 store as 128).</summary>
    public static byte StoredDensity(int d) => d switch
    {
        <= 0 => 0,
        >= AlpineLightmapLayout.DensityOff => AlpineLightmapLayout.DensityOff,
        > AlpineLightmapLayout.DensityMax => AlpineLightmapLayout.DensityMax,
        _ => (byte)d,
    };

    /// <summary>The codec's name for scripts and the command line ("bc7" / "raw").</summary>
    public static string CodecName(AlpineLightmapCodec c) => c == AlpineLightmapCodec.RawRgb8 ? "raw" : "bc7";

    /// <summary>Parses "bc7" / "raw" (case-insensitive); null for anything else.</summary>
    public static AlpineLightmapCodec? ParseCodec(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "bc7" => AlpineLightmapCodec.Bc7,
        "raw" or "rgb8" or "raw_rgb8" => AlpineLightmapCodec.RawRgb8,
        _ => null,
    };

    /// <summary>The compression mode's name ("quality" / "balanced" / "compact").</summary>
    public static string CompressionName(AlpineLightmapCompression c) => c.ToString().ToLowerInvariant();

    /// <summary>The lower-case name scripts and the command line use ("alpine" / "legacy").</summary>
    public static string Name(LightmapPipeline p) => p == LightmapPipeline.Legacy ? "legacy" : "alpine";

    /// <summary>Parses "alpine" / "legacy" (case-insensitive); null for anything else.</summary>
    public static LightmapPipeline? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "alpine" => LightmapPipeline.Alpine,
        "legacy" or "stock" => LightmapPipeline.Legacy,
        _ => null,
    };

    internal static AlpineLevelPropertiesSection? FindProperties(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId != (uint)SectionType.AlpineLevelProperties)
            {
                continue;
            }

            if (s.Content is null)
            {
                try
                {
                    if (RflSectionRegistry.TryParse(s, rfl.Context, out IRflSectionContent? parsed))
                    {
                        s.Content = parsed;
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return s.Content as AlpineLevelPropertiesSection;
        }

        return null;
    }
}
