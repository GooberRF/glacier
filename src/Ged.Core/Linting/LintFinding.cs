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
}

/// <summary>
/// One level-linter finding: a severity, a category, a human message, and the
/// primary object UID for jump-to (or null for level-wide findings). When
/// <see cref="BlocksSave"/> is true the finding must be cleared before the level
/// can be saved to the target that produced it.
/// </summary>
public sealed record LintFinding(
    LintSeverity Severity,
    LintCategory Category,
    string Message,
    int? Uid = null,
    int? SecondaryUid = null,
    bool BlocksSave = false)
{
    public override string ToString()
    {
        string loc = Uid is int u ? $" [uid {u}]" : string.Empty;
        return $"{Severity}/{Category}: {Message}{loc}";
    }
}
