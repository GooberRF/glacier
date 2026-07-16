using System.Collections.Generic;
using Ged.Core.Assets;

namespace Ged.Core.Packaging;

/// <summary>
/// What a scanned level dependency <em>is</em>, so the packfile builder can group
/// the review tree and the tests can assert an exact per-kind file set. Mirrors
/// Alpine's <c>editor_patch</c> pack-file scanner categories (§9 parity list).
/// </summary>
public enum DependencyKind
{
    /// <summary>A brush/compiled static-geometry face texture.</summary>
    FaceTexture,

    /// <summary>A liquid-room surface texture (room effect or compiled liquid room).</summary>
    LiquidTexture,

    /// <summary>A decal texture.</summary>
    DecalTexture,

    /// <summary>A particle-emitter bitmap.</summary>
    ParticleBitmap,

    /// <summary>A bolt-emitter bitmap.</summary>
    BoltBitmap,

    /// <summary>A corona bitmap (billboard or volumetric).</summary>
    CoronaBitmap,

    /// <summary>A sound (.wav) referenced by an event field.</summary>
    EventSound,

    /// <summary>A bitmap referenced by an event field (Display_Fullscreen_Image / Swap_Textures / Monitor_State ...).</summary>
    EventBitmap,

    /// <summary>A mesh (.v3m/.v3c) referenced by an event field (Switch_Model / Alpine Mesh_* ...).</summary>
    EventMesh,

    /// <summary>A vclip referenced by an event field (noted; resolves from a table normally).</summary>
    EventVclip,

    /// <summary>A video referenced by an event field (Play_Video).</summary>
    EventVideo,

    /// <summary>An animation (.rfa) referenced by an event field.</summary>
    EventAnimation,

    /// <summary>An MVF referenced by an event field.</summary>
    EventMvf,

    /// <summary>An Alpine mesh object's .v3m/.v3c file.</summary>
    MeshObject,

    /// <summary>A texture referenced by a mesh (material diffuse or a per-slot override).</summary>
    MeshObjectTexture,

    /// <summary>An .rfa animation referenced by a mesh object (state/corpse anim) or an entity (state/death anim).</summary>
    MeshAnimation,

    /// <summary>An ambient-sound .wav.</summary>
    AmbientSound,

    /// <summary>A mover / moving-group sound (.wav).</summary>
    MoverSound,

    /// <summary>The level's geomod crater / default texture.</summary>
    GeomodTexture,

    /// <summary>A texture whose winning file is an ATX descriptor.</summary>
    AtxDescriptor,

    /// <summary>A frame file referenced by an ATX descriptor.</summary>
    AtxFrame,

    /// <summary>A game-shipped clutter mesh (resolved via clutter.tbl).</summary>
    ClutterMesh,

    /// <summary>A clutter skin texture.</summary>
    ClutterSkin,

    /// <summary>A game-shipped entity mesh (resolved via entity.tbl).</summary>
    EntityMesh,

    /// <summary>An entity skin texture.</summary>
    EntitySkin,

    /// <summary>A game-shipped item mesh (resolved via items.tbl).</summary>
    ItemMesh,

    /// <summary>The level's companion dialogue text file.</summary>
    DialogueText,
}

/// <summary>How a dependency resolves against the mounted VFS.</summary>
public enum DependencyStatus
{
    /// <summary>Resolves from a loose/user mount — packed into the level VPP.</summary>
    Included,

    /// <summary>Resolves from a base-game packfile — the engine already ships it, so it is skipped.</summary>
    BaseGameSkipped,

    /// <summary>Does not resolve from any mount — reported as missing.</summary>
    Missing,
}

/// <summary>
/// A raw dependency reference gathered from the level, before VFS resolution.
/// <paramref name="Uid"/> is the referencing object's UID when one exists (for
/// jump-to-usage), or null for level-wide references (geometry / properties).
/// <paramref name="ParentFile"/> is the resolved name of the file this reference
/// was expanded from (a mesh for its material textures, an ATX for its frames) so
/// the dependency graph can nest indirect deps under their parent.
/// </summary>
public sealed record DependencyRef(string FileName, DependencyKind Kind, string Origin, int? Uid = null, string? ParentFile = null);

/// <summary>One referencer of a dependency: a human-readable origin plus the object UID (for jump-to).</summary>
public sealed record DependencyReferer(string Description, int? Uid);

/// <summary>A resolved dependency: where it lives and how to read it. Null return = missing.</summary>
public sealed record DependencyResolution(
    string ResolvedName,
    AssetSourceKind SourceKind,
    string SourceDescription,
    string? LoosePath,
    long Size,
    System.Func<byte[]?> Read);

/// <summary>
/// One dependency in a <see cref="DependencyScanResult"/>: the resolved (or raw,
/// if missing) file name, its kind, resolution status/location, size, and the set
/// of level objects that reference it (for the review tree and jump-to-usage).
/// </summary>
public sealed class PackDependency
{
    public required string FileName { get; init; }

    public required DependencyKind Kind { get; init; }

    public DependencyStatus Status { get; set; }

    public string? SourceDescription { get; set; }

    public string? LoosePath { get; set; }

    public long Size { get; set; }

    /// <summary>The objects/fields that reference this file (deduplicated, in discovery order).</summary>
    public List<string> Origins { get; } = new();

    /// <summary>
    /// The referencers of this file with their object UIDs where known — the
    /// dependency graph's "why is this included" list with jump-to buttons.
    /// </summary>
    public List<DependencyReferer> Referers { get; } = new();

    /// <summary>
    /// Resolved names of the files this dependency was expanded from (a mesh for
    /// its material textures, an ATX descriptor for its frames). Empty for a file
    /// referenced directly by the level. Drives the dependency graph's nesting.
    /// </summary>
    public HashSet<string> Parents { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the file's bytes for packing; null when missing.</summary>
    public System.Func<byte[]?>? Read { get; set; }

    public bool IsBaseGame => Status == DependencyStatus.BaseGameSkipped;

    public override string ToString() => $"{FileName} [{Kind}] {Status}";
}
