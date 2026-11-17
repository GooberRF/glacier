using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;

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
/// The save-time rule for Alpine Faction's lightmap chunk (0x0AFBAE09), mirroring Alpine RED's <c>save_decide</c>
/// (editor_patch/alpine_lightmaps.cpp:1786-1870) and <c>alpine_lm_serialize_chunk</c> (:2613-2696):
/// <list type="number">
/// <item><b>Fresh charts.</b> When Glacier's last bake produced Alpine charts (<see cref="RflFile.AlpineLightmaps"/>)
/// for the geometry and lightmaps being saved, they replace the level's chunk, stamped with the fingerprints of the
/// surface records as written — unless the level now says charts are not allowed (Legacy lighting, or density Off),
/// in which case they are dropped with a note, as Alpine RED does (:1832-1839).</item>
/// <item><b>No fresh charts, level rebuilt or relit.</b> The loaded chunk no longer matches: it is dropped (a build
/// between the bake and the save, a fresh relight, or a bake that wrote no charts — :158-164, :208-212, :1840-1846;
/// the legacy gate editor_patch/lightmap.cpp:118-124). The game would otherwise reject or misapply it
/// (game_patch/graphics/af_lightmap.cpp:391-470, 640-760; alpine_lightmap_reader.h:541-766).</item>
/// <item><b>Unchanged level.</b> The chunk is re-emitted byte for byte — unless it carries surface or mover charts and
/// the level says charts are not allowed, which Alpine RED drops on save (the "charts not allowed" case,
/// :1824-1839); a terrain-only chunk is kept.</item>
/// </list>
/// <para>
/// The two props bit fields Alpine recomputes on save follow (editor_patch/level.h:1309-1310, 1324-1325;
/// level.cpp:1134-1168, 1213-1215): <c>d3d11_only_lightmaps</c> bit0 ("the stock section is omitted") can only be set
/// when surface charts stand in for the stock lightmaps (alpine_lightmaps.cpp:1849): with fresh surface charts and the
/// level's "D3D11-only lightmaps" setting the save omits 0x1200 and writes every surface index −1
/// (<see cref="RflFile.StockLightmapsSuppressed"/>) and sets it, otherwise it is cleared; <c>require_d3d11</c> bit0 is
/// recomputed from Alpine's reasons (the mapper's setting, stock lightmaps omitted, a mesh draw scale other than 1, a
/// Mesh_Set_Scale event). The mapper's setting bits (bit1 of each) are kept.
/// </para>
/// </summary>
internal static class AlpineLightmapSavePolicy
{
    /// <summary>Applies the rule to <paramref name="rfl"/> in place and returns the save-log lines (empty when nothing changed).</summary>
    public static IReadOnlyList<string> Reconcile(RflFile rfl)
    {
        var notes = new List<string>();
        GlacierLightingChange changes = rfl.GlacierLightingChanges;
        RflContext context = rfl.Context;
        AlpineLightmapSettings settings = AlpineLightmapSettings.FromLevel(rfl);
        rfl.StockLightmapsSuppressed = false;

        if (FreshCharts(rfl, context) is { } fresh)
        {
            // Charts on brushwork follow the level's pipeline; a section with terrain charts alone is always emitted
            // (alpine_lightmaps.cpp:1792-1810).
            bool brushwork = fresh.Bake.ChartedSurfaces > 0 || fresh.Bake.Movers.Count > 0;
            if (!settings.ChartsEnabled && (brushwork || fresh.Bake.Terrains.Count == 0))
            {
                bool hadTerrain = RemoveChunks(rfl, context);
                notes.Add(
                    (settings.Pipeline == LightmapPipeline.Legacy
                        ? "Legacy lighting is set, so the Alpine lightmaps baked without it were not saved"
                        : "Alpine surface lightmaps are off, so the Alpine lightmaps baked with them were not saved")
                    + (hadTerrain ? ", terrain lighting included" : string.Empty)
                    + "; the level uses its stock lightmaps. Run Calculate Lighting again.");
            }
            else
            {
                // "D3D11-only lightmaps": only surface charts may stand in for the stock section (alpine_lightmaps.cpp:1849).
                bool suppress = settings.D3D11Only && fresh.Bake.ChartedSurfaces > 0;
                rfl.StockLightmapsSuppressed = suppress;
                WriteFresh(rfl, fresh.Bake, fresh.Geometry, context, suppress);
                int moverCharts = fresh.Bake.Movers.Sum(m => m.ChartCount);
                notes.Add(
                    $"Wrote the level's Alpine lightmaps: {fresh.Bake.ChartedSurfaces} surface charts" +
                    (fresh.Bake.Movers.Count > 0 ? $" and {moverCharts} charts on {fresh.Bake.Movers.Count} movers" : string.Empty) +
                    (fresh.Bake.Terrains.Count > 0 ? $", {fresh.Bake.Terrains.Count} terrain chart(s)" : string.Empty) +
                    $" on {fresh.Bake.Pages.Length} pages at {fresh.Bake.Density} texels per unit" +
                    (fresh.Bake.DensityHalvings > 0 ? $" (halved from {fresh.Bake.RequestedDensity} to fit the page budget)" : string.Empty) +
                    (fresh.Bake.Codec == AlpineLightmapCodec.Bc7 ? ", BC7-compressed" : ", stored as raw RGB8") +
                    (suppress
                        ? ". D3D11-only lightmaps: the stock lightmaps are not saved and the level needs Alpine Faction on Direct3D 11."
                        : ". Alpine Faction draws them on Direct3D 11; other renderers use the stock lightmaps."));
                if (suppress)
                {
                    foreach (int uid in MoversWithoutCharts(rfl, context, fresh.Bake))
                    {
                        notes.Add($"Mover {uid} has no Alpine lighting and renders fullbright on this D3D11-only level.");
                    }
                }
            }

            UpdateProperties(rfl, context, notes, chartsWritten: settings.ChartsEnabled, noteSetting: true);
            NoteTerrainLighting(rfl, context, notes);
            return notes;
        }

        if (changes != GlacierLightingChange.None)
        {
            bool hadTerrain = false;
            bool dropped = false;
            foreach (RflSection section in rfl.Sections.Where(s => s.TypeId == (uint)SectionType.AlpineLightmaps))
            {
                dropped = true;
                hadTerrain |= Content(section, context) is AlpineLightmapsSection lm && HasTerrain(lm);
            }

            RemoveChunks(rfl, context);
            if (dropped)
            {
                string what = changes switch
                {
                    GlacierLightingChange.Geometry => "Glacier rebuilt the geometry",
                    GlacierLightingChange.Lighting => "Glacier recalculated the lighting",
                    _ => "Glacier rebuilt the geometry and recalculated the lighting",
                };
                string terrain = hadTerrain ? " Its terrain lighting was dropped with it." : string.Empty;
                string fallback = rfl.Sections.Any(s => s.TypeId == (uint)SectionType.Lightmaps)
                    ? "Alpine Faction now uses the stock lightmaps Glacier wrote."
                    : "The level has no stock lightmaps yet: run Calculate Lighting in Glacier before playing it.";
                string again = settings.ChartsEnabled
                    ? "Run Calculate Lighting to bake the Alpine lightmaps again."
                    : "The level uses Legacy lighting, so no Alpine lightmaps are baked.";
                notes.Add(
                    $"Dropped the level's Alpine lightmaps (the Direct3D 11 lightmap chunk): {what} since they were baked, " +
                    $"so they no longer match the level.{terrain} {fallback} {again}");
            }

            UpdateProperties(rfl, context, notes, chartsWritten: false, noteSetting: dropped);
            return notes;
        }

        // An unchanged level keeps its chunk, except surface or mover charts the level no longer allows.
        if (!settings.ChartsEnabled)
        {
            bool dropped = false;
            for (int i = rfl.Sections.Count - 1; i >= 0; i--)
            {
                RflSection section = rfl.Sections[i];
                if (section.TypeId == (uint)SectionType.AlpineLightmaps
                    && Content(section, context) is AlpineLightmapsSection { IsUsable: true } lm
                    && (lm.ChartedSurfaceCount > 0 || lm.Tables.Any(t => t.Tag == AlpineLightmapsSection.TagMovers && t.RecordCount > 0)))
                {
                    rfl.Sections.RemoveAt(i);
                    dropped = true;
                }
            }

            if (dropped)
            {
                notes.Add(
                    (settings.Pipeline == LightmapPipeline.Legacy
                        ? "Legacy lighting is set, so the level's Alpine lightmaps (baked without it) were dropped"
                        : "Alpine surface lightmaps are off, so the level's Alpine lightmaps (baked with them) were dropped")
                    + (rfl.Sections.Any(s => s.TypeId == (uint)SectionType.Lightmaps)
                        ? "; Alpine Faction uses the stock lightmaps."
                        : "; the level has no stock lightmaps: run Calculate Lighting in Glacier before playing it."));
                UpdateProperties(rfl, context, notes, chartsWritten: false, noteSetting: true);
            }
        }

        NoteTerrainLighting(rfl, context, notes);
        return notes;
    }

    /// <summary>
    /// The attached charts when they still describe the static geometry and stock pages being saved (Alpine RED's
    /// <c>fresh_geometry</c>: same solid, same surface count, same surfaces signature — alpine_lightmaps.cpp:1800-1801).
    /// </summary>
    private static (AlpineLightmapBake Bake, Geometry Geometry)? FreshCharts(RflFile rfl, RflContext context)
    {
        if (rfl.AlpineLightmaps is not { Geometry: { } baked } bake)
        {
            return null;
        }

        Geometry? current = null;
        IReadOnlyList<Lightmap>? pages = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.StaticGeometry && Content(s, context) is GeometrySection g)
            {
                current = g.Geometry;
            }
            else if (s.TypeId == (uint)SectionType.Lightmaps && Content(s, context) is LightmapsSection l)
            {
                pages = l.Lightmaps;
            }
        }

        return ReferenceEquals(current, baked) && bake.Charts.Length == baked.Surfaces.Count
            && bake.Signature == AlpineLightmapBake.SurfacesSignature(baked.Surfaces, pages)
            ? (bake, baked)
            : null;
    }

    /// <summary>Replaces the level's chunk with the fresh charts, fingerprinted for the surfaces being written.</summary>
    private static void WriteFresh(RflFile rfl, AlpineLightmapBake bake, Geometry geometry, RflContext context, bool suppress)
    {
        Dictionary<int, Geometry?> movers = MoverGeometries(rfl, context);
        byte[] body = AlpineLightmapWriter.Stamp(
            bake.Body, geometry, uid => movers.TryGetValue(uid, out Geometry? g) ? g : null, suppress, bake.Movers);
        var section = new RflSection((uint)SectionType.AlpineLightmaps, body);
        int at = rfl.Sections.FindIndex(s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        rfl.Sections.RemoveAll(s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        if (at >= 0 && at <= rfl.Sections.Count)
        {
            rfl.Sections.Insert(at, section); // where the level had its chunk (a loaded file keeps its order)
        }
        else
        {
            rfl.InsertSection(section); // the "after movers" slot (RflSectionOrder)
        }
    }

    /// <summary>
    /// The movers section's geometry per mover uid (null for a uid two movers share: the game matches neither —
    /// game_patch/graphics/af_lightmap.cpp:300-307).
    /// </summary>
    private static Dictionary<int, Geometry?> MoverGeometries(RflFile rfl, RflContext context)
    {
        var map = new Dictionary<int, Geometry?>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Movers && Content(s, context) is MoversSection movers)
            {
                foreach (Brush b in movers.Movers)
                {
                    map[b.Uid] = map.ContainsKey(b.Uid) ? null : b.Geometry;
                }
            }
        }

        return map;
    }

    /// <summary>The movers with lightmap surfaces that the bake's mover table does not chart.</summary>
    private static IEnumerable<int> MoversWithoutCharts(RflFile rfl, RflContext context, AlpineLightmapBake bake)
    {
        var charted = new HashSet<int>(bake.Movers.Select(m => m.Uid));
        foreach ((int uid, Geometry? g) in MoverGeometries(rfl, context))
        {
            if (g is { Surfaces.Count: > 0 } && !charted.Contains(uid))
            {
                yield return uid;
            }
        }
    }

    /// <summary>
    /// One note per terrain whose baked chart in the chunk being saved no longer matches it (sculpted, holes or moved
    /// since the bake): the game keeps that terrain on its fallback light until Calculate Lighting runs again
    /// (editor_patch/alpine_lightmaps.cpp:1748-1775 <c>warn_terrain_lighting</c>). Unlike Alpine RED, only when the
    /// terrains were edited since the level was loaded, and terrains with no chart get no note.
    /// </summary>
    private static void NoteTerrainLighting(RflFile rfl, RflContext context, List<string> notes)
    {
        AlpineTerrainSection? terrains = null;
        byte[]? chunk = null;
        foreach (RflSection s in rfl.Sections)
        {
            // Only terrains this session changed: a level saved as it was loaded keeps whatever lighting state its file
            // had without a note, so a save of an unchanged level stays a silent no-op.
            if (s.TypeId == (uint)SectionType.AlpineTerrain && Content(s, context) is AlpineTerrainSection { IsReadOnly: false } ts
                && s.Dirty && !s.GetBodyBytes(context).AsSpan().SequenceEqual(s.RawBytes))
            {
                terrains = ts;
            }
            else if (s.TypeId == (uint)SectionType.AlpineLightmaps)
            {
                chunk = Content(s, context) is AlpineLightmapsSection lm ? lm.RawBytes.ToArray() : s.RawBytes;
            }
        }

        if (terrains is null || chunk is null)
        {
            return;
        }

        AlpineLightmapReadResult read = AlpineLightmapValidator.Read(chunk, null);
        foreach (AlpineTerrain t in terrains.Terrains)
        {
            int i = read.Terrains.FindIndex(c => c.TerrainUid == t.Uid);
            if (i >= 0 && read.TerrainOk[i] && !t.Fullbright && !AlpineLightmapValidator.TerrainChartMatches(read.Terrains[i], t))
            {
                notes.Add($"{Compiler.TerrainChunkEmitter.Label(t)} changed since its lighting was baked: Alpine Faction draws it with "
                    + "the level's ambient and sun until Calculate Lighting runs again.");
            }
        }
    }

    /// <summary>Removes every Alpine lightmap chunk; true when one of them carried terrain lighting.</summary>
    private static bool RemoveChunks(RflFile rfl, RflContext context)
    {
        bool terrain = false;
        for (int i = rfl.Sections.Count - 1; i >= 0; i--)
        {
            if (rfl.Sections[i].TypeId != (uint)SectionType.AlpineLightmaps)
            {
                continue;
            }

            terrain |= Content(rfl.Sections[i], context) is AlpineLightmapsSection lm && HasTerrain(lm);
            rfl.Sections.RemoveAt(i);
        }

        return terrain;
    }

    private static bool HasTerrain(AlpineLightmapsSection lm) =>
        lm.Tables.Any(t => t.Tag == AlpineLightmapsSection.TagTerrain && t.RecordCount > 0);

    /// <summary>Recomputes the two Alpine props bit fields that describe the saved lightmaps (see the class remarks).</summary>
    private static void UpdateProperties(RflFile rfl, RflContext context, List<string> notes, bool chartsWritten, bool noteSetting)
    {
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

        if (props is null || propsHost is null)
        {
            return;
        }

        // bit0 says the stock section is omitted from this file, which only fresh surface charts can stand in for
        // (alpine_lightmaps.cpp:1849); the omission is a reason the level needs Direct3D 11 (level.cpp:1134-1168).
        bool omitted = rfl.StockLightmapsSuppressed;
        byte d3d11Only = (byte)((props.D3D11OnlyLightmaps & ~AlpineLevelPropertiesSection.D3D11OnlyStockOmitted)
            | (omitted ? AlpineLevelPropertiesSection.D3D11OnlyStockOmitted : 0));
        byte require = (byte)(props.RequireD3D11 & ~AlpineLevelPropertiesSection.RequireD3D11Needed);
        if (omitted || NeedsDirect3D11(rfl, props, context))
        {
            require |= AlpineLevelPropertiesSection.RequireD3D11Needed;
        }

        if (d3d11Only != props.D3D11OnlyLightmaps || require != props.RequireD3D11)
        {
            props.D3D11OnlyLightmaps = d3d11Only;
            props.RequireD3D11 = require;
            propsHost.Dirty = true;
        }

        if (noteSetting && !omitted && (props.D3D11OnlyLightmaps & AlpineLevelPropertiesSection.D3D11OnlySetting) != 0)
        {
            notes.Add(chartsWritten
                ? "The level's \"D3D11-only lightmaps\" setting has no effect on this save: no surface got an Alpine chart, so the stock lightmaps are kept."
                : "The level's \"D3D11-only lightmaps\" setting has no effect on this save: the level has no fresh Alpine lightmaps, so its stock lightmaps are kept.");
        }
    }

    /// <summary>
    /// Alpine RED's reasons a level needs Direct3D 11, minus "D3D11-only lightmaps" (the stock section is always
    /// written here): the mapper's setting, a mesh with a draw scale other than 1, a Mesh_Set_Scale event
    /// (level.cpp:1134-1168).
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
