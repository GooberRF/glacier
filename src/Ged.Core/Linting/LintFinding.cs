using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>Severity of a <see cref="LintFinding"/>, ordered ascending.</summary>
public enum LintSeverity
{
    /// <summary>Informational — no action required.</summary>
    Info,

    /// <summary>A problem that will not stop a save but should be reviewed.</summary>
    Warning,

    /// <summary>A problem serious enough to block a save to the offending target.</summary>
    Error,
}

/// <summary>The broad class a <see cref="LintFinding"/> belongs to (for grouping/filtering).</summary>
public enum LintCategory
{
    /// <summary>A link points at a missing UID or an invalid target kind.</summary>
    BrokenLink,

    /// <summary>A referenced texture/mesh/sound does not resolve from any mount.</summary>
    MissingAsset,

    /// <summary>Two objects (or a brush and an object) share a UID.</summary>
    DuplicateUid,

    /// <summary>An object-category count is at or over its engine budget.</summary>
    LimitBudget,

    /// <summary>Compiled geometry has open (non-manifold) edges — a possible leak.</summary>
    GeometryLeak,

    /// <summary>A nav point is isolated or otherwise problematic.</summary>
    NavPoint,

    /// <summary>A trigger has no links, so it does nothing.</summary>
    TriggerWithoutLinks,

    /// <summary>An event is disconnected from the link graph.</summary>
    EventOrphan,

    /// <summary>A texture is non-power-of-two or oversize.</summary>
    TextureSize,

    /// <summary>The level has no spawn point (no Player Start and no MP respawn points).</summary>
    MissingPlayerStart,

    /// <summary>
    /// A brush is portal-flagged but solid: RED's membrane form (it carves nothing; its faces in open space become
    /// portal faces). Informational.
    /// </summary>
    SolidPortal,

    /// <summary>The level has brushes but no compiled geometry (or no rooms): the game crashes loading it.</summary>
    MissingGeometry,

    /// <summary>
    /// A level metric (doorway width, corridor height, light density, …) lies outside the p5–p95 range of the shipped
    /// corpus norms. Always informational; never blocks a save.
    /// </summary>
    OutOfNorm,

    /// <summary>
    /// An Alpine Faction feature whose game-side prerequisite the level does not meet (e.g. a Payload_Handler with
    /// Legacy Movers on, which Alpine refuses to run as a cart).
    /// </summary>
    AlpinePrerequisite,

    /// <summary>
    /// A brush face the player can see still has the placeholder texture every new brush starts with (RED's stock
    /// default, Rck_Default.tga) or no texture at all. Measured on the compiled geometry (an unbuilt level is not
    /// checked); portal, sky, invisible and hidden faces are not reported. A warning; never blocks a save.
    /// </summary>
    PlaceholderTexture,

    /// <summary>
    /// An air portal brush lies in space that is already open, so it opens no wall: RED builds a portal from an air
    /// portal brush only where it cuts through solid, and none from this one.
    /// </summary>
    PortalOpensNothing,

    /// <summary>
    /// A decal's box touches a compiled face with more than 19 vertex records (t-junction points along its edges): the
    /// game and the editor overflow their fixed decal-clip buffers on it and crash while the level loads. Unbuilt
    /// levels are not checked (a note says so).
    /// </summary>
    DecalOnComplexFace,

    /// <summary>
    /// An air brush is stored with its faces wound outward. RED stores every air brush inward and folds its faces
    /// into the world as stored, so RED would rebuild this brush inside-out. Glacier's own compiler is unaffected,
    /// and saving the level in Glacier re-stores the brush inward.
    /// </summary>
    AirBrushInsideOut,
}

/// <summary>
/// One level-linter finding: a severity, a category, a human message, and the
/// primary object UID for jump-to (or null for level-wide findings). When
/// <see cref="BlocksSave"/> is true the finding must be cleared before the level
/// can be saved to the target that produced it. <see cref="Location"/> is the
/// world-space point the finding is about (an object's position, a leak edge's
/// midpoint), or null when the finding has no natural location.
/// </summary>
public sealed record LintFinding(
    LintSeverity Severity,
    LintCategory Category,
    string Message,
    int? Uid = null,
    int? SecondaryUid = null,
    bool BlocksSave = false,
    Vec3? Location = null)
{
    public override string ToString()
    {
        string loc = Uid is int u ? $" [uid {u}]" : string.Empty;
        string at = Location is Vec3 p ? $" at {p}" : string.Empty;
        return $"{Severity}/{Category}: {Message}{loc}{at}";
    }
}
