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
}
