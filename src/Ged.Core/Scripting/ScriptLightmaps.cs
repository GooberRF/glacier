using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;

namespace Ged.Core.Scripting;

/// <summary>
/// Lua <c>lightmap_settings</c>: the level's lightmap pipeline as Alpine RED stores it (the Alpine level properties
/// <c>legacy_lighting</c> and <c>lightmap_density</c>), returned by <c>ops.lightmaps()</c>.
/// </summary>
[ScriptDoc("The level's lightmap pipeline settings (Alpine level properties), as ops.lightmaps returns them.", Since = 2)]
public sealed class ScriptLightmapSettings
{
    internal ScriptLightmapSettings(AlpineLightmapSettings s)
    {
        Pipeline = AlpineLightmapSettings.Name(s.Pipeline);
        Density = s.Density;
        ChartsEnabled = s.ChartsEnabled;
        EffectiveDensity = s.EffectiveDensity;
        Codec = AlpineLightmapSettings.CodecName(s.Codec);
        Compression = AlpineLightmapSettings.CompressionName(s.Compression);
        HighRes = s.HighRes;
        D3D11Only = s.D3D11Only;
    }

    /// <summary>Lua: <c>s.codec</c>.</summary>
    [ScriptDoc("The layer codec the next bake writes: \"bc7\" (Alpine Faction's shipping codec, the default) or \"raw\" (uncompressed RGB8).", Since = 2)]
    public string Codec { get; }

    /// <summary>Lua: <c>s.compression</c>.</summary>
    [ScriptDoc("The level's BC7 compression: \"quality\", \"balanced\" or \"compact\" (level.alpine.lightmap_compression).", Since = 2)]
    public string Compression { get; }

    /// <summary>Lua: <c>s.highres</c>.</summary>
    [ScriptDoc("True when the level asks for high-resolution stock lightmaps (level.alpine.highres_lightmaps): 256x256 pages, 4x density.", Since = 2)]
    public bool HighRes { get; }

    /// <summary>Lua: <c>s.d3d11_only</c>.</summary>
    [ScriptDoc("True when the level's D3D11-only lightmaps setting is on: a save with Alpine charts then leaves the stock lightmaps out.", Since = 2)]
    public bool D3D11Only { get; }

    /// <summary>Lua: <c>s.pipeline</c>.</summary>
    [ScriptDoc("\"alpine\" (Alpine Faction's lighting: stock lightmaps plus the Direct3D 11 surface charts; the default) or \"legacy\" (stock lightmaps only).", Since = 2)]
    public string Pipeline { get; }

    /// <summary>Lua: <c>s.density</c>.</summary>
    [ScriptDoc("The stored lightmap density: 0 = default (8 texels per unit), 1-128 texels per unit, 255 = Alpine surface charts off.", Since = 2)]
    public int Density { get; }

    /// <summary>Lua: <c>s.effective_density</c>.</summary>
    [ScriptDoc("The texels per unit a bake starts from (it halves the density until the charts fit the page budget).", Since = 2)]
    public int EffectiveDensity { get; }

    /// <summary>Lua: <c>s.charts_enabled</c>.</summary>
    [ScriptDoc("True when Calculate Lighting bakes Alpine surface charts (the Alpine pipeline with density not Off).", Since = 2)]
    public bool ChartsEnabled { get; }

    public override string ToString() => $"{Pipeline}, density {Density}";
}

/// <summary>
/// Lua <c>lightmap_report</c>: what a lighting bake produced (op_report.lightmaps): the pipeline it ran, and for the
/// Alpine pipeline the surface charts — the density actually baked, budget halvings, pages, charts and tiles.
/// </summary>
[ScriptDoc("What a lighting bake produced (op_report.lightmaps): pipeline, Alpine chart density, budget halvings, pages, charts and tiles.", Since = 2)]
public sealed class ScriptLightmapReport
{
    internal ScriptLightmapReport(BakeStats stats)
    {
        Pipeline = stats.Pipeline is LightmapPipeline p ? AlpineLightmapSettings.Name(p) : "legacy";
        AlpineLightmapBake? a = stats.AlpineLightmaps;
        Density = a?.Density ?? 0;
        RequestedDensity = a?.RequestedDensity ?? 0;
        Halvings = a?.DensityHalvings ?? 0;
        Pages = a?.Pages.Length ?? 0;
        Charts = a?.ChartedSurfaces ?? 0;
        Tiles = a?.Tiles.Length ?? 0;
        DirectionalLights = stats.DirectionalLights;
        Skipped = stats.AlpineSkipped;
        Codec = a is null ? "none" : AlpineLightmapSettings.CodecName(a.Codec);
        Compression = a is null ? "quality" : AlpineLightmapSettings.CompressionName(a.Compression);
        Movers = a?.Movers.Count ?? 0;
        MoverCharts = 0;
        foreach (AlpineMoverCharts m in a?.Movers ?? System.Array.Empty<AlpineMoverCharts>())
        {
            MoverCharts += m.ChartCount;
        }

        LiquidEmitters = stats.LiquidEmitters;
        SectionBytes = a?.Body.Length ?? 0;
        TerrainCharts = a?.Terrains.Count ?? 0;
    }

    /// <summary>Lua: <c>r.terrain_charts</c>.</summary>
    [ScriptDoc("Terrains that got a baked light chart (built, not fullbright).", Since = 2)]
    public int TerrainCharts { get; }

    /// <summary>Lua: <c>r.codec</c>.</summary>
    [ScriptDoc("The layer codec written: \"bc7\", \"raw\", or \"none\" when no Alpine charts were baked.", Since = 2)]
    public string Codec { get; }

    /// <summary>Lua: <c>r.compression</c>.</summary>
    [ScriptDoc("The BC7 compression the layer was encoded at (\"quality\", \"balanced\", \"compact\").", Since = 2)]
    public string Compression { get; }

    /// <summary>Lua: <c>r.movers</c>.</summary>
    [ScriptDoc("Movers that got Alpine charts (the section's mover table).", Since = 2)]
    public int Movers { get; }

    /// <summary>Lua: <c>r.mover_charts</c>.</summary>
    [ScriptDoc("Mover surfaces that got an Alpine chart.", Since = 2)]
    public int MoverCharts { get; }

    /// <summary>Lua: <c>r.liquid_emitters</c>.</summary>
    [ScriptDoc("Liquid rooms whose emission lit the bake.", Since = 2)]
    public int LiquidEmitters { get; }

    /// <summary>Lua: <c>r.section_bytes</c>.</summary>
    [ScriptDoc("Size of the Alpine lightmap section body in bytes (0 when none was baked).", Since = 2)]
    public int SectionBytes { get; }

    /// <summary>Lua: <c>r.pipeline</c>.</summary>
    [ScriptDoc("The pipeline the bake ran: \"alpine\" or \"legacy\".", Since = 2)]
    public string Pipeline { get; }

    /// <summary>Lua: <c>r.density</c>.</summary>
    [ScriptDoc("Texels per unit the Alpine charts were baked at (0 when none were baked).", Since = 2)]
    public int Density { get; }

    /// <summary>Lua: <c>r.requested_density</c>.</summary>
    [ScriptDoc("The density the bake started from (the level's setting).", Since = 2)]
    public int RequestedDensity { get; }

    /// <summary>Lua: <c>r.halvings</c>.</summary>
    [ScriptDoc("How many times the density was halved to fit the page budget.", Since = 2)]
    public int Halvings { get; }

    /// <summary>Lua: <c>r.pages</c>.</summary>
    [ScriptDoc("Alpine lightmap pages (256x256) the charts occupy.", Since = 2)]
    public int Pages { get; }

    /// <summary>Lua: <c>r.charts</c>.</summary>
    [ScriptDoc("Surfaces that got an Alpine chart.", Since = 2)]
    public int Charts { get; }

    /// <summary>Lua: <c>r.tiles</c>.</summary>
    [ScriptDoc("Tiles in the Alpine atlas.", Since = 2)]
    public int Tiles { get; }

    /// <summary>Lua: <c>r.directional_lights</c>.</summary>
    [ScriptDoc("Alpine directional lights baked (the level sun and Directional Light objects that start on).", Since = 2)]
    public int DirectionalLights { get; }

    /// <summary>Lua: <c>r.skipped</c>.</summary>
    [ScriptDoc("Why no Alpine charts were baked (nil when they were).", Since = 2)]
    public string? Skipped { get; }

    public override string ToString() =>
        Charts > 0 ? $"{Pipeline}: {Charts} charts, {Pages} pages at density {Density}" : $"{Pipeline}: no Alpine charts";
}
