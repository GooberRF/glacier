namespace Ged.Core.IO.Rfl;

/// <summary>
/// Known RFL/RFG section type identifiers. Values not listed here are handled
/// as opaque round-tripped blobs. Includes RF1, Alpine/Dash, and the RF2 types
/// that may appear in the wild (RF2 editing is out of scope, but the ids are
/// recognized so files are never misread).
/// </summary>
public enum SectionType : uint
{
    End = 0x00000000,
    StaticGeometry = 0x00000100,
    GeoRegions = 0x00000200,
    Lights = 0x00000300,
    CutsceneCameras = 0x00000400,
    AmbientSounds = 0x00000500,
    Events = 0x00000600,
    MpRespawnPoints = 0x00000700,
    Unknown800 = 0x00000800,
    LevelProperties = 0x00000900,
    ParticleEmitters = 0x00000A00,
    GasRegions = 0x00000B00,
    RoomEffects = 0x00000C00,
    ClimbingRegions = 0x00000D00,
    BoltEmitters = 0x00000E00,
    Targets = 0x00000F00,
    Decals = 0x00001000,
    PushRegions = 0x00001100,
    Lightmaps = 0x00001200,
    Movers = 0x00002000,
    MovingGroups = 0x00003000,
    Cutscenes = 0x00004000,
    CutscenePathNodes = 0x00005000,
    CutscenePaths = 0x00006000,
    TgaFiles = 0x00007000,
    VcmFiles = 0x00007001,
    MvfFiles = 0x00007002,
    V3dFiles = 0x00007003,
    VfxFiles = 0x00007004,
    EaxEffects = 0x00008000,
    WaypointLists = 0x00010000,
    NavPoints = 0x00020000,
    Entities = 0x00030000,
    Items = 0x00040000,
    Clutters = 0x00050000,
    Triggers = 0x00060000,
    PlayerStart = 0x00070000,
    LevelInfo = 0x01000000,
    Brushes = 0x02000000,
    Groups = 0x03000000,
    EditorOnlyLights = 0x04000000,

    // Alpine Faction / Dash Faction sections
    AlpineLevelProperties = 0x0AFBA5ED,
    AlpineMeshObjects = 0x0AFBAE01,
    AlpineNoteObjects = 0x0AFBAE02,
    AlpineCoronaObjects = 0x0AFBAE03,
    AlpineBagObjects = 0x0AFBAE04,
    AlpineBrushInfo = 0x0AFBAE05, // .rfg only

    // Alpine Faction 1.4/1.5 object and data chunks (editor_patch/level.h:39-54). No chunk version: each
    // grows by appended fields gated on the RFL version; the lightmap chunk carries its own section version.
    AlpineWeatherRegions = 0x0AFBAE06,
    AlpineVehicleFactories = 0x0AFBAE07,
    AlpineProjectionCameras = 0x0AFBAE08,
    AlpineLightmaps = 0x0AFBAE09,
    AlpineRopeEmitters = 0x0AFBAE0A,
    AlpineTerrain = 0x0AFBAE0B,
    AlpineDirectionalLights = 0x0AFBAE0C,
    AlpineWaypointHelpers = 0x0AFBAE0D,
    AlpineMoverPaths = 0x0AFBAE0E,
    AlpineLiquidEmission = 0x0AFBAE0F,

    DashLevelProperties = 0xDA58FA00,

    // GED editor-only sections (0x6ED0xxxx — GED's own id space). Unknown to RED/RF/Alpine,
    // which skip them as opaque round-tripped blobs; GED parses them for its own features.
    GedPrefabInstances = 0x6ED00001, // prefab-instance lineage metadata
    GedObjectMetadata = 0x6ED00002,  // general per-object metadata (item 4; first user: light cookies)
}

/// <summary>Classification helpers for section ids outside the enumerated set.</summary>
public static class SectionIds
{
    /// <summary>The 0x0AFBxxxx id space Alpine Faction's chunks live in (props 0x0AFBA5ED, objects 0x0AFBAExx).</summary>
    public static bool IsAlpineSpace(uint typeId) => (typeId & 0xFFFF0000u) == 0x0AFB0000u;

    /// <summary>
    /// A foreign editor's chunk that Alpine RED retains verbatim and re-emits: Glacier's own 0x6ED00000
    /// space or RED+'s 0x5ED00000 space (Alpine editor_patch/level.h:59-76, level.cpp:225-271). Glacier
    /// preserves every unparsed chunk raw in place, so these round-trip byte for byte.
    /// </summary>
    public static bool IsForeignEditorSpace(uint typeId) =>
        (typeId & 0xFFF00000u) is 0x6ED00000u or 0x5ED00000u;

    /// <summary>RED+'s foreign chunk space (0x5ED00000), preserved raw.</summary>
    public static bool IsRedPlusSpace(uint typeId) => (typeId & 0xFFF00000u) == 0x5ED00000u;

    /// <summary>A short user-facing name for a section id ("weather region", "0x0AFBAE20", ...).</summary>
    public static string Describe(uint typeId) => (SectionType)typeId switch
    {
        SectionType.AlpineLevelProperties => "Alpine level properties",
        SectionType.AlpineMeshObjects => "Alpine mesh",
        SectionType.AlpineNoteObjects => "Alpine note",
        SectionType.AlpineCoronaObjects => "Alpine corona",
        SectionType.AlpineBagObjects => "Alpine bag",
        SectionType.AlpineWeatherRegions => "weather region",
        SectionType.AlpineVehicleFactories => "vehicle factory",
        SectionType.AlpineProjectionCameras => "projection camera",
        SectionType.AlpineLightmaps => "Alpine lightmap",
        SectionType.AlpineRopeEmitters => "rope emitter",
        SectionType.AlpineTerrain => "terrain",
        SectionType.AlpineDirectionalLights => "directional light",
        SectionType.AlpineWaypointHelpers => "waypoint helper",
        SectionType.AlpineMoverPaths => "mover path",
        SectionType.AlpineLiquidEmission => "liquid emission",
        _ => $"0x{typeId:X8}",
    };
}

