using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl.Sections;

namespace Ged.Core.IO.Rfl;

/// <summary>What Glacier has recomputed in a level since it was loaded (see <see cref="RflFile.GlacierLightingChanges"/>).</summary>
[Flags]
public enum GlacierLightingChange
{
    /// <summary>Nothing: the loaded geometry and lightmaps are what the file had.</summary>
    None = 0,

    /// <summary>Glacier rebuilt the static geometry (any build applied to the level, preview builds included).</summary>
    Geometry = 1,

    /// <summary>Glacier recalculated (or reset) the stock lightmaps.</summary>
    Lighting = 2,
}

/// <summary>
/// The save-time rule for Alpine Faction's lightmap chunk (0x0AFBAE09) until Glacier writes Alpine lightmaps itself
/// (ALPINE-COMPAT-SPEC §9 interim rule).
///
/// <para>
/// Alpine RED re-emits a retained chunk only while the geometry and lighting it was baked for are still the ones
/// being saved; a Build Geometry between the bake and the save, a fresh Calculate Lighting, or a bake that writes no
/// charts (Legacy lighting) drops it with a warning (editor_patch/alpine_lightmaps.cpp:1785-1849 <c>save_decide</c>,
/// the retained-signature rule at :158-164 and :208-212; the legacy gate editor_patch/lightmap.cpp:118-124). The game
/// would otherwise reject or misapply it: it checks each part's fingerprints against the surfaces it loads and falls
/// back to the stock lightmaps on a mismatch (game_patch/graphics/af_lightmap.cpp:391-470, 640-760;
/// alpine_lightmap_reader.h:541-766) — and after a relight alone the fingerprints still match, so stale charts would
/// replace the fresh stock bake on Direct3D 11. Glacier bakes stock lightmaps only, so once it has rebuilt the
/// geometry or relit the level it is in Alpine RED's no-charts position: the chunk is dropped on save.
/// </para>
///
/// <para>
/// The two props bit fields Alpine recomputes on save follow (editor_patch/level.h:1309-1310, 1324-1325;
/// level.cpp:1134-1168, 1213-1215): <c>d3d11_only_lightmaps</c> bit0 ("the stock section is omitted") can only be set
/// when surface charts stand in for the stock lightmaps (alpine_lightmaps.cpp:1849), so it is cleared — Glacier writes
/// the stock section; <c>require_d3d11</c> bit0 is recomputed from Alpine's reasons (the mapper's setting, stock
/// lightmaps omitted, a mesh draw scale other than 1, a Mesh_Set_Scale event). The mapper's setting bits (bit1 of
/// each) are kept. A level Glacier did not rebuild or relight is untouched, so it re-saves byte for byte.
/// </para>
/// </summary>
internal static class AlpineLightmapSavePolicy
{
    /// <summary>Applies the rule to <paramref name="rfl"/> in place and returns the save-log lines (empty when nothing changed).</summary>
    public static IReadOnlyList<string> Reconcile(RflFile rfl)
    {
        var notes = new List<string>();
        GlacierLightingChange changes = rfl.GlacierLightingChanges;
        if (changes == GlacierLightingChange.None)
        {
            return notes;
        }

        RflContext context = rfl.Context;
        bool dropped = false;
        bool hadTerrainLighting = false;
        for (int i = rfl.Sections.Count - 1; i >= 0; i--)
        {
            RflSection section = rfl.Sections[i];
            if (section.TypeId != (uint)SectionType.AlpineLightmaps)
            {
                continue;
            }

            if (Content(section, context) is AlpineLightmapsSection lm
                && lm.Tables.Any(t => t.Tag == AlpineLightmapsSection.TagTerrain && t.RecordCount > 0))
            {
                hadTerrainLighting = true;
            }

            rfl.Sections.RemoveAt(i);
            dropped = true;
        }

        bool hasStockLightmaps = rfl.Sections.Any(s => s.TypeId == (uint)SectionType.Lightmaps);
        AlpineLevelPropertiesSection? props = null;
        RflSection? propsHost = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.AlpineLevelProperties && Content(s, context) is AlpineLevelPropertiesSection p)
            {
                props = p;
                propsHost = s;
                break;
            }
        }

        if (dropped)
        {
            string what = changes switch
            {
                GlacierLightingChange.Geometry => "Glacier rebuilt the geometry",
                GlacierLightingChange.Lighting => "Glacier recalculated the lighting",
                _ => "Glacier rebuilt the geometry and recalculated the lighting",
            };
            string terrain = hadTerrainLighting ? " Its terrain lighting was dropped with it." : string.Empty;
            string fallback = hasStockLightmaps
                ? "Alpine Faction now uses the stock lightmaps Glacier wrote."
                : "The level has no stock lightmaps yet: run Calculate Lighting in Glacier before playing it.";
            notes.Add(
                $"Dropped the level's Alpine lightmaps (Alpine RED's Direct3D 11 lightmap bake): {what} since they were " +
                $"baked, so they no longer match the level.{terrain} {fallback} To get Alpine lighting back, re-bake the " +
                "level in Alpine RED (Calculate Lighting).");
        }

        if (props is not null && propsHost is not null)
        {
            // No surface charts are written, so the stock section cannot be omitted (alpine_lightmaps.cpp:1849).
            byte d3d11Only = (byte)(props.D3D11OnlyLightmaps & ~AlpineLevelPropertiesSection.D3D11OnlyStockOmitted);
            byte require = (byte)(props.RequireD3D11 & ~AlpineLevelPropertiesSection.RequireD3D11Needed);
            if (NeedsDirect3D11(rfl, props, context))
            {
                require |= AlpineLevelPropertiesSection.RequireD3D11Needed;
            }

            if (d3d11Only != props.D3D11OnlyLightmaps || require != props.RequireD3D11)
            {
                props.D3D11OnlyLightmaps = d3d11Only;
                props.RequireD3D11 = require;
                propsHost.Dirty = true;
            }

            if (dropped && (props.D3D11OnlyLightmaps & AlpineLevelPropertiesSection.D3D11OnlySetting) != 0)
            {
                notes.Add(
                    "The level's \"D3D11-only lightmaps\" setting is kept, but it has no effect until Alpine RED re-bakes the " +
                    "level: Glacier writes stock lightmaps.");
            }
        }

        return notes;
    }

    /// <summary>
    /// Alpine RED's reasons a level needs Direct3D 11, minus "D3D11-only lightmaps" (no charts are written here):
    /// the mapper's setting, a mesh with a draw scale other than 1, a Mesh_Set_Scale event (level.cpp:1134-1168).
    /// </summary>
    private static bool NeedsDirect3D11(RflFile rfl, AlpineLevelPropertiesSection props, RflContext context)
    {
        if ((props.RequireD3D11 & AlpineLevelPropertiesSection.RequireD3D11Setting) != 0)
        {
            return true;
        }

        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.AlpineMeshObjects && Content(s, context) is AlpineMeshObjectsSection meshes
                && meshes.Meshes.Any(m => m.DrawScale != 1f))
            {
                return true;
            }

            if (s.TypeId == (uint)SectionType.Events && Content(s, context) is EventsSection events
                && events.Events.Any(e => e.ClassName.Equals("Mesh_Set_Scale", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IRflSectionContent? Content(RflSection section, RflContext context)
    {
        if (section.Content is null)
        {
            try
            {
                if (RflSectionRegistry.TryParse(section, context, out IRflSectionContent? parsed))
                {
                    section.Content = parsed;
                }
            }
            catch (Exception)
            {
                return null; // unreadable: left as it is
            }
        }

        return section.Content;
    }
}
