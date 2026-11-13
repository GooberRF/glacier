using System;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editor;

/// <summary>
/// The File ▸ New level template, shared by the editor and headless hosts (GUI, <c>glacier-cli new</c>,
/// MCP <c>new_level</c>, a script run without a level) so a new level is the same document no matter who
/// creates it. It carries no geometry: Alpine RED's new-level alpine_level_properties, initialized
/// level_properties, a single-player Player Start, and level_info, in RED's canonical section order, at
/// the current Alpine version (v306, as Alpine RED starts a new level — editor_patch/main.cpp:113-116).
/// </summary>
/// <remarks>
/// The alpine_level_properties chunk is what Alpine RED saves for a new level: version 6, every field at
/// Alpine's defaults (the Alpine, not legacy, lighting pipeline among them) except Legacy Cyclic Timers and
/// Legacy Movers, which a new level has off (AlpineLevelProperties::LoadNewLevelDefaults,
/// editor_patch/level.h:1123-1128, applied at CDedLevel construction and File ▸ New, level.cpp:155-191).
/// Without the chunk the game would fall back to the existing-level (legacy) defaults (game_patch/misc/level.h:59-65).
/// Existing levels are never given it by this template.
/// </remarks>
public static class LevelTemplates
{
    /// <summary>The level name a new, never-saved level carries.</summary>
    public const string DefaultLevelName = "untitled.rfl";

    /// <summary>
    /// Builds the new-level file. <paramref name="createdAt"/> stamps level_info's date (defaults to
    /// now); pass a fixed time for deterministic output.
    /// </summary>
    public static RflFile CreateNewRfl(DateTime? createdAt = null)
    {
        var rfl = new RflFile();
        rfl.Header.Version = RflFile.AlpineSaveVersion; // v306, the version every save writes
        rfl.Header.LevelName = DefaultLevelName;

        // A fresh level opens in RED with initialized level_properties + level_info (the
        // Level Properties dialog and the metadata / editor view configs). Emit both with
        // researched defaults so New → Save produces a level RED reads as authored, not one
        // missing its property/info sections.
        // Emit the sections in RED's canonical on-disk order (see RflSectionOrder):
        // level_properties, then player_start, then level_info near the tail. The
        // geometry/lightmaps sections a build later creates slot in canonically
        // (lightmaps before static_geometry) via GeometryBuildService, so a
        // from-scratch level saves in the same order RED writes and never hits the
        // lightmaps-after-geometry black-render defect.
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelProperties, Array.Empty<byte>())
        {
            Content = LevelPropertiesSection.CreateDefault(),
            Dirty = true,
        });

        // Author a single-player start so a from-scratch level has somewhere for RF to spawn
        // the player. Without one the header's player_start_offset stays 0 and RF drops the
        // player into the void outside every room — the camera resolves to no room and the
        // portal renderer draws nothing (a fully black screen in-game). The start sits one
        // unit above the grid origin (identity orientation → facing +Z), i.e. the centre of
        // the default working volume: a floor built on the grid plane (y=0) leaves the spawn
        // just above it. The writer recomputes player_start_offset from this section on save.
        rfl.Sections.Add(new RflSection((uint)SectionType.PlayerStart, Array.Empty<byte>())
        {
            Content = new PlayerStartSection { Position = new Vec3(0f, 1f, 0f), Rotation = Mat3.Identity },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelInfo, Array.Empty<byte>())
        {
            Content = LevelInfoSection.CreateDefault(createdAt ?? DateTime.Now),
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        // Alpine RED's new-level properties, at their canonical place (first: before level_properties).
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineLevelProperties, Array.Empty<byte>())
        {
            Content = AlpineLevelPropertiesSection.CreateForNewLevel(),
            Dirty = true,
        });
        return rfl;
    }

    /// <summary>Creates a new, unsaved (path-less) editor document from the new-level template.</summary>
    public static EditorDocument CreateNew(DateTime? createdAt = null) => new(CreateNewRfl(createdAt));
}
