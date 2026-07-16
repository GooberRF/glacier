using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Bridges the compiler to an open level: pulls the brushes and room effects out
/// of an <see cref="RflFile"/>, compiles them, and swaps the freshly compiled
/// <c>static_geometry</c> + <c>lightmaps</c> sections back in (marked dirty so a
/// save re-serializes them; every other section stays byte-identical). Pure —
/// the app layer wraps this on a background thread.
/// </summary>
public static class GeometryBuildService
{
    /// <summary>Extracts the brushes and room effects, compiles, and returns the result.</summary>
    public static CompiledLevel Build(RflFile rfl, CompileOptions? options = null)
    {
        rfl.ParseAllKnownSections();

        // Exclude mover-owned brushes from the static world fold — exactly what RED does. Their
        // brushes-section copy is editor round-trip data; RF.exe animates the movers-section copy.
        // Folding them into static_geometry leaves an immovable unlit duplicate at the rest position.
        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);
        List<RoomEffect> effects = Find<RoomEffectsSection>(rfl)?.Effects ?? new List<RoomEffect>();

        options ??= new CompileOptions();
        PopulateLighting(rfl, options);

        // [ALPINE] Feed the geoable/breakable brush set into the compile so each such brush
        // isolates into its own detail room (RED's populate_isolated_face_map): the set is the
        // union of the alpine_level_properties geoable + breakable brush-uid lists plus any
        // editor-marked geoable brush. Geoable brushes carry infinite life and no on-disk flag,
        // so they are re-tagged with BrushFlags.Geoable here to drive the room-builder isolation.
        if (options.Alpine)
        {
            var isolated = CollectIsolatedBrushUids(rfl, brushes);
            if (isolated.Count > 0)
            {
                options.IsolatedBrushUids = isolated;
                brushes = TagGeoableForIsolation(brushes, isolated);
            }
        }

        CompiledLevel result = GeometryCompiler.Compile(brushes, effects, options);

        // Preserve authored per-room state the compile cannot derive: is_airlock is authored via RED's
        // room-property UI and PRESERVED in the serialized room table (never recomputed — dmabrupt ships
        // 17 airlock rooms with zero airlock effects; flagship 29, AirlockRuleDiag). Carry each source
        // room's flag onto its spatially-matching rebuilt room so a GED rebuild keeps it like RED does.
        if (Find<GeometrySection>(rfl) is { } sourceGeo)
        {
            RoomFlagPreservation.PreserveAirlock(sourceGeo.Geometry, result.Geometry);
        }

        // Bake the mover brushes' own lightmap surfaces into the same (regenerated) atlas — RED does this at
        // each mover's rest position against the static world. Without it the movers keep RED's page indices
        // into GED's differently-packed atlas and render dark (Goober's "elevators/door too dark") report.
        if (Find<MoversSection>(rfl) is { Movers.Count: > 0 } movers)
        {
            result.BakedMoverUids = MoverLighting.Bake(movers.Movers, result, options);
        }

        return result;
    }

    /// <summary>
    /// The union of geoable + breakable brush UIDs from alpine_level_properties plus any brush
    /// the editor already marked geoable (<see cref="BrushFlags.Geoable"/>). These are the brushes
    /// that must each isolate into their own compiled detail room.
    /// </summary>
    private static HashSet<int> CollectIsolatedBrushUids(RflFile rfl, IReadOnlyList<Brush> brushes)
    {
        var set = new HashSet<int>();
        if (Find<AlpineLevelPropertiesSection>(rfl) is { } alp)
        {
            foreach (AlpineGeoableEntry e in alp.GeoableEntries)
            {
                set.Add(e.BrushUid);
            }

            foreach (AlpineBreakableEntry e in alp.BreakableEntries)
            {
                set.Add(e.BrushUid);
            }
        }

        foreach (Brush b in brushes)
        {
            if (((BrushFlags)b.Flags & BrushFlags.Geoable) != 0)
            {
                set.Add(b.Uid);
            }
        }

        return set;
    }

    /// <summary>
    /// Returns <paramref name="brushes"/> with every isolated brush that lacks the geoable flag
    /// replaced by a shallow clone carrying it, so the room builder isolates it. The document's
    /// brushes are never mutated (the clone shares the immutable source geometry by reference).
    /// </summary>
    private static List<Brush> TagGeoableForIsolation(IReadOnlyList<Brush> brushes, HashSet<int> isolated)
    {
        var result = new List<Brush>(brushes.Count);
        foreach (Brush b in brushes)
        {
            if (isolated.Contains(b.Uid) && ((BrushFlags)b.Flags & BrushFlags.Geoable) == 0)
            {
                result.Add(new Brush
                {
                    Uid = b.Uid,
                    Position = b.Position,
                    Rotation = b.Rotation,
                    Geometry = b.Geometry,
                    Flags = b.Flags | (uint)BrushFlags.Geoable,
                    Life = b.Life,
                    State = b.State,
                    OpType = b.OpType,
                });
            }
            else
            {
                result.Add(b);
            }
        }

        return result;
    }

    /// <summary>Pulls the lights + level ambient out of the document into the compile options (unless the caller set them).</summary>
    public static void PopulateLighting(RflFile rfl, CompileOptions options)
    {
        rfl.ParseAllKnownSections();
        if (options.Lights.Count == 0)
        {
            options.Lights = LightsOfType(rfl, SectionType.Lights);
        }

        if (options.EditorOnlyLights.Count == 0)
        {
            options.EditorOnlyLights = LightsOfType(rfl, SectionType.EditorOnlyLights);
        }

        options.LevelAmbient ??= FindLevelProperties(rfl)?.AmbientColor;
    }

    private static List<Light> LightsOfType(RflFile rfl, SectionType type)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)type && s.Content is LightsSection ls)
            {
                return ls.Lights;
            }
        }

        return new List<Light>();
    }

    private static LevelPropertiesSection? FindLevelProperties(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is LevelPropertiesSection lp)
            {
                return lp;
            }
        }

        return null;
    }

    /// <summary>Compiles and applies the result to the document in one call.</summary>
    public static CompiledLevel BuildAndApply(RflFile rfl, CompileOptions? options = null)
    {
        CompiledLevel result = Build(rfl, options);
        Apply(rfl, result);
        return result;
    }

    /// <summary>Swaps the compiled geometry + lightmaps into the file (creating the sections if absent).</summary>
    public static void Apply(RflFile rfl, CompiledLevel result)
    {
        RflContext ctx = rfl.Context;

        // Preserve the existing geometry header fields (unknown1/modifiability/name).
        GeometrySection? geoSection = FindSection(rfl, SectionType.StaticGeometry)?.Content as GeometrySection;
        Geometry compiled = result.Geometry;
        if (geoSection is not null)
        {
            compiled.Unknown1 = geoSection.Geometry.Unknown1;
            compiled.Modifiability = geoSection.Geometry.Modifiability;
            compiled.ModifiabilityOld = geoSection.Geometry.ModifiabilityOld;
            compiled.Name = geoSection.Geometry.Name;
        }

        // Version <= 0xB4 stores the face-scroll table a second time after surfaces.
        if (ctx.HasLegacyFaceScrollData)
        {
            compiled.LegacyFaceScrollData = new List<FaceScrollData>(compiled.FaceScrollData);
        }

        SetSection(rfl, SectionType.StaticGeometry, new GeometrySection { Geometry = compiled });
        SetSection(rfl, SectionType.Lightmaps, new LightmapsSection { Lightmaps = result.Lightmaps });

        // Movers were re-baked into the atlas above (their geometry was mutated in place); re-serialise them.
        if (result.BakedMoverUids.Count > 0 && FindSection(rfl, SectionType.Movers) is { } moverSection)
        {
            moverSection.Dirty = true;
        }

        // [ALPINE] Recompute the geoable/breakable brush → room-uid tables against the freshly
        // compiled rooms (RED does this on every save: compute_geoable_room_uids /
        // compute_breakable_room_uids). Without this the tables keep room UIDs from the previous
        // compile, so in-game geomod finds no room (geoable dead) and every breakable falls back
        // to Glass instead of its authored material.
        UpdateAlpineRoomLinks(rfl, result);
    }

    /// <summary>
    /// Writes the recomputed room UIDs back into <c>alpine_level_properties</c>: prunes entries
    /// whose brush no longer exists, sets each surviving entry's room UID from the compile, and
    /// adds a geoable entry for any editor-marked geoable brush not yet listed. Marks the section
    /// dirty only when something actually changed, so a level with no geoable/breakable brushes
    /// round-trips byte-identically.
    /// </summary>
    private static void UpdateAlpineRoomLinks(RflFile rfl, CompiledLevel result)
    {
        // Only an Alpine compile owns these tables; a stock/non-Alpine build leaves them untouched.
        if (!result.AlpineBuild)
        {
            return;
        }

        BrushesSection? brushSec = Find<BrushesSection>(rfl);
        var liveUids = new HashSet<int>(brushSec?.Brushes.Select(b => b.Uid) ?? Enumerable.Empty<int>());
        var authoredGeoable = brushSec?.Brushes
            .Where(b => ((BrushFlags)b.Flags & BrushFlags.Geoable) != 0)
            .Select(b => b.Uid).ToList() ?? new List<int>();

        RflSection? host = FindSection(rfl, SectionType.AlpineLevelProperties);
        var alp = host?.Content as AlpineLevelPropertiesSection;
        if (alp is null)
        {
            // No geoable/breakable data and none authored → leave the file untouched.
            if (authoredGeoable.Count == 0)
            {
                return;
            }

            host = rfl.GetOrCreateSection(
                SectionType.AlpineLevelProperties, () => new AlpineLevelPropertiesSection { Version = 4 });
            alp = (AlpineLevelPropertiesSection)host.Content!;
        }

        bool changed = false;

        // Bridge editor-marked geoable brushes into the persistent (brush_uid) table — RED keeps
        // geoable state there, never in the on-disk brush record.
        foreach (int uid in authoredGeoable)
        {
            if (!alp.GeoableEntries.Any(e => e.BrushUid == uid))
            {
                alp.GeoableEntries.Add(new AlpineGeoableEntry { BrushUid = uid });
                changed = true;
            }
        }

        changed |= RecomputeGeoableRoomUids(alp.GeoableEntries, liveUids, result.BrushRoomUid);
        changed |= RecomputeBreakableRoomUids(alp.BreakableEntries, liveUids, result.BrushRoomUid);

        if (changed)
        {
            host!.Dirty = true;
        }
    }

    /// <summary>Prunes stale geoable entries and refreshes each survivor's room UID from the compile.</summary>
    private static bool RecomputeGeoableRoomUids(
        List<AlpineGeoableEntry> entries, HashSet<int> liveUids, IReadOnlyDictionary<int, int> brushRoomUid)
    {
        bool changed = false;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (!liveUids.Contains(entries[i].BrushUid))
            {
                entries.RemoveAt(i);
                changed = true;
                continue;
            }

            int roomUid = brushRoomUid.GetValueOrDefault(entries[i].BrushUid, 0);
            if (entries[i].RoomUid != roomUid)
            {
                entries[i].RoomUid = roomUid;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Prunes stale breakable entries and refreshes each survivor's room UID (material preserved).</summary>
    private static bool RecomputeBreakableRoomUids(
        List<AlpineBreakableEntry> entries, HashSet<int> liveUids, IReadOnlyDictionary<int, int> brushRoomUid)
    {
        bool changed = false;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (!liveUids.Contains(entries[i].BrushUid))
            {
                entries.RemoveAt(i);
                changed = true;
                continue;
            }

            int roomUid = brushRoomUid.GetValueOrDefault(entries[i].BrushUid, 0);
            if (entries[i].RoomUid != roomUid)
            {
                entries[i].RoomUid = roomUid;
                changed = true;
            }
        }

        return changed;
    }

    private static void SetSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        RflSection? section = FindSection(rfl, type);
        if (section is null)
        {
            // Insert before the trailing End terminator, else append.
            section = new RflSection((uint)type, System.Array.Empty<byte>());
            int insertAt = rfl.Sections.FindIndex(s => s.IsEnd);
            if (insertAt < 0)
            {
                rfl.Sections.Add(section);
            }
            else
            {
                rfl.Sections.Insert(insertAt, section);
            }
        }

        section.Content = content;
        section.Dirty = true;
    }

    private static RflSection? FindSection(RflFile rfl, SectionType type)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)type)
            {
                return s;
            }
        }

        return null;
    }

    private static T? Find<T>(RflFile rfl)
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }
}
