using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The backend for the heavy <c>ops</c> operations. The Core default
/// (<see cref="CoreScriptOperations"/>) drives the pure-Core pipeline directly with progress +
/// cancellation; the App may supply an implementation that routes through its
/// <c>GeometryBuildController</c> so a script build shows the same progress overlay + Build
/// Output the toolbar buttons do. Scripts never touch <c>WorldBsp</c>/<c>LightKernel</c> — these
/// are opaque, cancelable operations.
/// </summary>
public interface IScriptOperations
{
    /// <summary>Compiles geometry (optionally baking lighting) and applies it to the document.</summary>
    ScriptOpReport Build(bool bakeLighting, bool shadows);

    /// <summary>Compiles a sealed build and returns the world-space midpoints of any leaks.</summary>
    IReadOnlyList<Vec3> CheckHoles();

    /// <summary>Saves the document to <paramref name="path"/> (or its current path). GED always
    /// writes Alpine v306 (<c>SaveTarget</c> is a compatibility reference only, not a file target).</summary>
    void Save(string? path);

    /// <summary>Reports Alpine-only features that would not run on stock RF (compatibility check).</summary>
    string CompatibilitySummary();

    /// <summary>Packages the level + its dependencies into a .vpp at <paramref name="path"/>.</summary>
    ScriptOpReport Package(string? path, bool multiplayer);

    /// <summary>Launches the level in-game (single or multiplayer).</summary>
    void Playtest(bool multiplayer);
}

/// <summary>A compact, Lua-friendly outcome for a heavy operation.</summary>
public sealed class ScriptOpReport
{
    public ScriptOpReport(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    /// <summary>Lua: <c>report.ok</c>.</summary>
    [ScriptDoc("True when the operation succeeded.")]
    public bool Success { get; }

    /// <summary>Lua: <c>report.message</c>.</summary>
    [ScriptDoc("A one-line summary of what the operation did.")]
    public string Message { get; }

    /// <summary>Lua: <c>report.count</c> — an operation-specific tally (faces built, files packed, …).</summary>
    [ScriptDoc("An operation-specific tally (faces built, files packed, …).")]
    public int Count { get; init; }

    /// <summary>Lua: <c>report.path</c> — an output path when the op produced a file.</summary>
    [ScriptDoc("The output file path when the operation wrote one, else nil.")]
    public string? Path { get; init; }

    /// <summary>Lua: <c>report.surfaces</c> — lightmap surfaces the build made.</summary>
    [ScriptDoc("For ops.build / ops.light: the lightmap surfaces the build made (a level holds at most surface_cap; 0 for other operations).", Since = 2)]
    public int Surfaces { get; init; }

    /// <summary>Lua: <c>report.surfaces_wanted</c> — lightmap surfaces the lit faces would make before the cap.</summary>
    [ScriptDoc("For ops.build / ops.light: the lightmap surfaces the lit faces would make without the cap (more than surface_cap means faces went unlit).", Since = 2)]
    public int SurfacesWanted { get; init; }

    /// <summary>Lua: <c>report.unlit_faces</c> — lit faces left without a surface past the cap.</summary>
    [ScriptDoc("For ops.build / ops.light: lit faces that got no lightmap surface because the level reached surface_cap (they draw unlit); 0 normally.", Since = 2)]
    public int UnlitFaces { get; init; }

    /// <summary>Lua: <c>report.surface_cap</c> — the most lightmap surfaces a level holds (Alpine RED's limit).</summary>
    [ScriptDoc("The most lightmap surfaces a level holds (32767, Alpine RED's limit).", Since = 2)]
    public int SurfaceCap => Compiler.SurfaceBuilder.MaxLevelSurfaces;

    /// <summary>Lua: <c>report.repair</c> — the build-repair counters of a build (nil when it did not repair).</summary>
    [ScriptDoc("For ops.build / ops.light: the hole-repair counters (repair_report) when the build repaired holes (the editor's Repair Holes When Building setting, on by default), else nil.", Since = 2)]
    public ScriptRepairReport? Repair { get; init; }

    /// <summary>Lua: <c>report.lightmaps</c> — what a lighting bake produced (nil for a build without lighting).</summary>
    [ScriptDoc("For ops.light: what the bake produced (lightmap_report: pipeline, Alpine chart density, pages, charts), else nil.", Since = 2)]
    public ScriptLightmapReport? Lightmaps { get; init; }

    public override string ToString() => Message;
}

/// <summary>
/// Lua <c>repair_report</c>: Alpine RED's eleven build-repair counters for one sealed build, in the order Alpine RED's
/// build log prints them.
/// </summary>
[ScriptDoc("The hole-repair counters of one sealed build, as Alpine RED's build log lists them.", Since = 2)]
public sealed class ScriptRepairReport
{
    private readonly Compiler.BuildRepairStats _s;

    internal ScriptRepairReport(Compiler.BuildRepairStats stats) => _s = stats;

    [ScriptDoc("Holes (groups of open edges sharing a vertex, per room) found right before the repair.", Since = 2)]
    public int HolesAfterRooms => _s.HolesAfterRooms;

    [ScriptDoc("Portal faces whose plane was unified with their coplanar neighbours'.", Since = 2)]
    public int PortalPlanesUnified => _s.PortalPlanesUnified;

    [ScriptDoc("Portal copies moved into the room in front of them.", Since = 2)]
    public int PortalCopiesMoved => _s.PortalCopiesMoved;

    [ScriptDoc("Rooms joined to the room the room builder had split them from.", Since = 2)]
    public int SplitRoomsJoined => _s.SplitRoomsJoined;

    [ScriptDoc("Vertices welded onto a neighbour within 1 mm to close a crack.", Since = 2)]
    public int VerticesWelded => _s.VerticesWelded;

    [ScriptDoc("Edges split at a vertex lying on them (t-junctions).", Since = 2)]
    public int EdgesSplit => _s.EdgesSplit;

    [ScriptDoc("Collapsed (zero-area) faces removed.", Since = 2)]
    public int CollapsedFacesRemoved => _s.CollapsedFacesRemoved;

    [ScriptDoc("Zero-width portal strips removed.", Since = 2)]
    public int PortalStripsRemoved => _s.PortalStripsRemoved;

    [ScriptDoc("Lost portal copies restored.", Since = 2)]
    public int PortalGapsFilled => _s.PortalGapsFilled;

    [ScriptDoc("Small flat wall gaps (up to 1 square unit) filled with new faces.", Since = 2)]
    public int WallGapsFilled => _s.WallGapsFilled;

    [ScriptDoc("Portal faces removed because they separated no two rooms.", Since = 2)]
    public int UnplacedPortalFacesRemoved => _s.UnplacedPortalFacesRemoved;

    public override string ToString() =>
        $"{HolesAfterRooms} hole(s) before repair; {VerticesWelded} welded, {EdgesSplit} split, {WallGapsFilled + PortalGapsFilled} filled";
}
