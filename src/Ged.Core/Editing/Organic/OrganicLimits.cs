namespace Ged.Core.Editing.Organic;

/// <summary>
/// Authoring limits for generated (organic) brushes. These keep every piece hand-editable in the
/// editor; they sit far below the engine's own limits.
/// </summary>
public static class OrganicLimits
{
    /// <summary>The fewest points a convex hull brush is built from.</summary>
    public const int MinInputPoints = 4;

    /// <summary>The most points one hull brush may be built from.</summary>
    public const int MaxInputPoints = 256;

    /// <summary>
    /// The most hull vertices per brush. Authoring limit (keeps organic pieces hand-editable); far
    /// below Cairn's 5,000-triangle brush report line LC073 (ORGANIC-GEOMETRY-SPEC §1.2).
    /// </summary>
    public const int MaxHullVertices = 64;

    /// <summary>The most faces per hull brush (authoring limit, as <see cref="MaxHullVertices"/>).</summary>
    public const int MaxHullFaces = 128;

    /// <summary>The most corners per face: Cairn LC074 (REFERENCE.md:2912) — larger faces are split.</summary>
    public const int MaxFaceCorners = 16;

    /// <summary>The largest hull extent in metres (the scripting API's brush limit, <c>ScriptBrushHandle.MaxExtent</c>).</summary>
    public const double MaxExtent = 4096;

    /// <summary>
    /// The largest world coordinate magnitude for generated geometry, in metres: on the 1/1024 m grid every
    /// world vertex (and the local offset from the brush position) is then exact in float32
    /// (2^13 · 2^10 &lt; 2^24; ORGANIC-GEOMETRY-SPEC §0.3).
    /// </summary>
    public const double MaxCoordinate = 8192;

    /// <summary>The most hulls one bulk call may create.</summary>
    public const int MaxBulkHulls = 2000;

    /// <summary>
    /// The hull-building work one bulk call may ask for: the sum over its hulls of (points per hull)². Building a
    /// hull costs about the square of its point count (one 256-point hull takes a few hundred milliseconds), so
    /// this keeps a single call to seconds: 2000 hulls of 32 points fit, 32 hulls of 256 points fit.
    /// </summary>
    public const long MaxHullWork = 2_097_152;

    /// <summary>The finest snap grid (the construction grid, 1/1024 m).</summary>
    public const double MinSnap = 1.0 / 1024;

    /// <summary>The coarsest snap grid, in metres.</summary>
    public const double MaxSnap = 1;

    /// <summary>The smallest box-map tile, in metres per texture repeat.</summary>
    public const double MinTileMetres = 0.25;

    /// <summary>The largest box-map tile, in metres per texture repeat.</summary>
    public const double MaxTileMetres = 64;
}
