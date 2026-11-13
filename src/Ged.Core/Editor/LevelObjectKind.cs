namespace Ged.Core.Editor;

/// <summary>
/// The editor-facing category of a level object, used for outliner grouping,
/// type-scoped selection/visibility, and copy/paste routing. Maps loosely to the
/// RFL sections but is the level of granularity a user thinks in.
/// </summary>
public enum LevelObjectKind
{
    Entity,
    Item,
    Clutter,
    Light,
    Trigger,
    Event,
    AmbientSound,
    MpRespawnPoint,
    ParticleEmitter,
    BoltEmitter,
    NavPoint,
    Target,
    CutsceneCamera,
    CutscenePathNode,
    Decal,
    GeoRegion,
    GasRegion,
    ClimbRegion,
    PushRegion,
    Mover,

    /// <summary>A room-effect marker (sky/liquid room or ambient-light override; RFL room_effects).</summary>
    RoomEffect,

    /// <summary>An EAX environmental-audio effect zone (RFL eax_effects).</summary>
    Eax,

    /// <summary>A mover keyframe (a moving group's path waypoint; carries its own UID).</summary>
    Keyframe,
    MeshObject,
    NoteObject,
    CoronaObject,
    BagObject,
    PlayerStart,

    // Alpine Faction 1.5 (RFL 306) objects. Read-only in the editor for now: they load, list and inspect, and
    // round-trip byte for byte, but cannot yet be placed, moved or edited.

    /// <summary>An Alpine weather region (0x0AFBAE06).</summary>
    WeatherRegion,

    /// <summary>An Alpine vehicle factory (0x0AFBAE07).</summary>
    VehicleFactory,

    /// <summary>An Alpine projection camera (0x0AFBAE08).</summary>
    ProjectionCamera,

    /// <summary>An Alpine rope emitter (0x0AFBAE0A).</summary>
    RopeEmitter,

    /// <summary>An Alpine directional light (0x0AFBAE0C).</summary>
    DirectionalLight,

    /// <summary>An Alpine vehicle waypoint helper (0x0AFBAE0D).</summary>
    WaypointHelper,

    /// <summary>An Alpine terrain (0x0AFBAE0B); its position is the grid origin.</summary>
    Terrain,
}
