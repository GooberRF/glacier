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
    DashLevelProperties = 0xDA58FA00,

    // GED editor-only sections (0x6ED0xxxx — GED's own id space). Unknown to RED/RF/Alpine,
    // which skip them as opaque round-tripped blobs; GED parses them for its own features.
    GedPrefabInstances = 0x6ED00001, // prefab-instance lineage metadata
    GedObjectMetadata = 0x6ED00002,  // general per-object metadata (item 4; first user: light cookies)
}
