using System;
using System.Collections.Generic;

namespace Ged.Core.IO.Rfl;

/// <summary>
/// RED's canonical on-disk RFL section order. Used ONLY to choose the insertion
/// index for a NEW section (<see cref="RflFile.InsertSection"/>); sections that
/// already exist in a loaded file are never reordered, so raw-section
/// preservation and the byte-identity round-trip stay intact.
///
/// <para>
/// Derivation: the order below was enumerated empirically from every RED-authored
/// level in <c>research/example_rfls</c> — 30+ Volition v180 levels (dm/ctf), the
/// v200 community level, and the v304 Alpine levels (dmabrupt the flagship,
/// ctfstockintrade, dmedgeofdespair, dmfoundation, dmwarzone, …). Two properties
/// hold across the WHOLE corpus and are the ones that matter for correctness:
/// </para>
/// <list type="number">
///   <item>the file-reference lists, <c>alpine_level_properties</c>, the Alpine
///   object sections and <c>level_properties</c> all precede the geometry, and</item>
///   <item><c>lightmaps</c> (0x1200) is written IMMEDIATELY BEFORE
///   <c>static_geometry</c> (0x100).</item>
/// </list>
/// <para>
/// The second is not cosmetic. RF's level loader is two-phase and binds every
/// world/mover surface to its lightmap at geometry-load time from a registry that
/// is only populated by an already-parsed <c>lightmaps</c> section (RF.exe
/// FUN_00460820 dispatch, FUN_004ee210 surface bind). A <c>lightmaps</c> section
/// that FOLLOWS <c>static_geometry</c> is reached only in the loader's
/// post-geometry phase, which has no case for it — it is skipped, the registry is
/// empty at bind time so every surface is bound to one fabricated blank 128x128
/// page, and the whole level renders black. Writing lightmaps first is therefore
/// mandatory.
/// </para>
/// <para>
/// The middle (post-geometry) region drifts slightly by level/version in the
/// corpus (e.g. the relative position of <c>eax_effects</c> vs
/// <c>climbing_regions</c>); the table encodes the v304/v305 Alpine spine
/// (dmabrupt) since GED always saves v305. Because the table only positions NEW
/// sections and never reorders existing ones, that residual drift is immaterial.
/// </para>
/// </summary>
public static class RflSectionOrder
{
    /// <summary>
    /// Canonical section sequence; the index of a type IS its ordering rank.
    /// A new section is placed before the first already-present section whose
    /// rank is strictly greater (see <see cref="InsertionIndex"/>).
    /// </summary>
    private static readonly uint[] Order =
    {
        // --- Phase 1: everything RF consumes before/at geometry load ---
        (uint)SectionType.TgaFiles,               // 0x7000  file-reference lists (written first
        (uint)SectionType.VcmFiles,               // 0x7001  despite their high type ids)
        (uint)SectionType.MvfFiles,               // 0x7002
        (uint)SectionType.V3dFiles,               // 0x7003
        (uint)SectionType.VfxFiles,               // 0x7004
        (uint)SectionType.AlpineLevelProperties,  // 0x0AFBA5ED
        (uint)SectionType.DashLevelProperties,    // 0xDA58FA00 (Dash analog; not in corpus, grouped with props)
        (uint)SectionType.AlpineMeshObjects,      // 0x0AFBAE01
        (uint)SectionType.AlpineNoteObjects,      // 0x0AFBAE02
        (uint)SectionType.AlpineCoronaObjects,    // 0x0AFBAE03
        (uint)SectionType.AlpineBagObjects,       // 0x0AFBAE04
        (uint)SectionType.LevelProperties,        // 0x900
        (uint)SectionType.Lightmaps,              // 0x1200 <-- BEFORE static_geometry (mandatory; see remarks)
        (uint)SectionType.StaticGeometry,         // 0x100

        // --- Phase 2: post-geometry world data ---
        (uint)SectionType.GeoRegions,             // 0x200
        (uint)SectionType.Lights,                 // 0x300
        (uint)SectionType.CutsceneCameras,        // 0x400
        (uint)SectionType.AmbientSounds,          // 0x500
        (uint)SectionType.Events,                 // 0x600
        (uint)SectionType.CutscenePathNodes,      // 0x5000 (grouped with cutscene paths, as RED writes them by Events)
        (uint)SectionType.CutscenePaths,          // 0x6000
        (uint)SectionType.MpRespawnPoints,        // 0x700
        (uint)SectionType.ParticleEmitters,       // 0xA00
        (uint)SectionType.GasRegions,             // 0xB00
        (uint)SectionType.Decals,                 // 0x1000
        (uint)SectionType.PushRegions,            // 0x1100
        (uint)SectionType.RoomEffects,            // 0xC00
        (uint)SectionType.ClimbingRegions,        // 0xD00
        (uint)SectionType.BoltEmitters,           // 0xE00
        (uint)SectionType.Targets,                // 0xF00
        (uint)SectionType.EaxEffects,             // 0x8000 (v180 only; absent in Alpine)
        (uint)SectionType.Cutscenes,              // 0x4000
        (uint)SectionType.Movers,                 // 0x2000
        (uint)SectionType.MovingGroups,           // 0x3000
        (uint)SectionType.PlayerStart,            // 0x70000
        (uint)SectionType.WaypointLists,          // 0x10000
        (uint)SectionType.NavPoints,              // 0x20000
        (uint)SectionType.Entities,               // 0x30000
        (uint)SectionType.Items,                  // 0x40000
        (uint)SectionType.Clutters,               // 0x50000
        (uint)SectionType.Triggers,               // 0x60000
        (uint)SectionType.LevelInfo,              // 0x1000000
        (uint)SectionType.Brushes,                // 0x2000000
        (uint)SectionType.Groups,                 // 0x3000000
        (uint)SectionType.EditorOnlyLights,       // 0x4000000

        // --- GED custom chunks: after every RED section, before End (RED/RF/Alpine skip them) ---
        (uint)SectionType.GedPrefabInstances,     // 0x6ED00001
        (uint)SectionType.GedObjectMetadata,      // 0x6ED00002
    };

    private static readonly Dictionary<uint, int> RankOf = BuildRankMap();

    /// <summary>
    /// The canonical ordering rank of a section type. The End terminator sorts
    /// last (so a new section always lands before it); an unrecognized/opaque type
    /// sorts after every known non-End section but before End.
    /// </summary>
    public static int Rank(uint typeId)
    {
        if (typeId == (uint)SectionType.End)
        {
            return int.MaxValue;
        }

        return RankOf.TryGetValue(typeId, out int rank) ? rank : Order.Length;
    }

    /// <summary>
    /// The index at which to insert a NEW section of <paramref name="newTypeId"/>
    /// so it sits in canonical order relative to the sections already present,
    /// WITHOUT reordering any of them. Returns the position of the first existing
    /// section whose canonical rank exceeds the new section's rank (the End
    /// terminator always qualifies), or the list end when none does.
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<RflSection> sections, uint newTypeId)
    {
        ArgumentNullException.ThrowIfNull(sections);
        int rank = Rank(newTypeId);
        for (int i = 0; i < sections.Count; i++)
        {
            if (Rank(sections[i].TypeId) > rank)
            {
                return i;
            }
        }

        return sections.Count;
    }

    private static Dictionary<uint, int> BuildRankMap()
    {
        var map = new Dictionary<uint, int>(Order.Length);
        for (int i = 0; i < Order.Length; i++)
        {
            map[Order[i]] = i;
        }

        return map;
    }
}
