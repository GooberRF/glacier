namespace Ged.Rendering.Scene;

/// <summary>
/// The point-object categories drawn as camera-facing billboard glyphs. Each
/// kind has a distinct tint (see <see cref="Palette.BillboardTint"/>) so object
/// types are visually separable without real icon art (which lands later).
/// </summary>
public enum BillboardKind
{
    Light,
    Event,
    AmbientSound,
    Respawn,
    ParticleEmitter,
    BoltEmitter,
    NavPoint,
    PlayerStart,
    Target,
    Item,
    Clutter,
    Entity,
    CutsceneCamera,
    Region,
    Decal,
    Keyframe,
    Corona,
    Note,
    Bag,
    Trigger,
    GasRegion,
    ClimbRegion,
    PushRegion,
    RoomEffect,
    Eax,
    PathNode,

    /// <summary>An editable-brush vertex dot (Vertex mode).</summary>
    Vertex,
    Other,
}
