using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Ged.Core.Model;

namespace Ged.Automation;

// The JSON schema of every automation command (CLI --json output and bridge responses). Property
// names serialize camelCase (AutomationJson.Options); null members are omitted. Positions and sizes
// are metres, as [x, y, z] arrays (left-handed: +X right, +Y up, +Z forward), rounded to 3 decimals.
// Additive changes only: fields are never renamed or repurposed within schema version 1.

/// <summary>One command invocation: <c>{ "command": "...", "args": { ... } }</c>.</summary>
public sealed class AutomationRequest
{
    public AutomationRequest(string command, JsonObject? args = null)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Args = args ?? new JsonObject();
    }

    /// <summary>The command name (<c>info</c>, <c>run</c>, <c>build</c>, …).</summary>
    public string Command { get; }

    /// <summary>The command's arguments (see each handler in <see cref="AutomationCommands"/>).</summary>
    public JsonObject Args { get; }
}

/// <summary>
/// One command outcome: <c>{ "ok": bool, "command": "...", "result": {...}?, "error": {...}? }</c>.
/// A failed command may still carry a <c>result</c> (e.g. a failed script run's diagnostics + log).
/// </summary>
public sealed class AutomationResponse
{
    /// <summary>True when the command succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>The command that ran.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>The command's result object (shape depends on the command).</summary>
    public JsonNode? Result { get; init; }

    /// <summary>The failure, when <see cref="Ok"/> is false.</summary>
    public AutomationError? Error { get; init; }
}

/// <summary><c>{ "code": "...", "message": "..." }</c> — <c>code</c> is one of <see cref="AutomationErrorCodes"/>.</summary>
public sealed record AutomationError(string Code, string Message);

/// <summary><c>hello</c>: who answered (used by <c>--attach</c> to probe the editor).</summary>
public sealed class HelloResult
{
    /// <summary>Always "Glacier".</summary>
    public string App { get; init; } = "Glacier";

    /// <summary>The answering build's version (InformationalVersion).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The bridge protocol version.</summary>
    public int Protocol { get; init; }

    /// <summary>"headless" or "editor".</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>The open level's path (null when unsaved or none).</summary>
    public string? Document { get; init; }

    /// <summary>True when a level is open.</summary>
    public bool HasDocument { get; init; }

    /// <summary>The mounted Red Faction install (omitted when none).</summary>
    public string? RfInstallDir { get; init; }

    /// <summary>
    /// The optional bridge features the answering editor supports (<see cref="BridgeCapabilities"/>); added by
    /// the editor bridge, omitted by a headless session and by editors that predate them.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>
    /// The editor's "Allow destructive AI actions" setting (an AI session's destructive actions need it on as
    /// well as the server's own <c>--allow-destructive</c>); added by the editor bridge, omitted elsewhere.
    /// </summary>
    public bool? DestructiveAutomation { get; init; }
}

/// <summary><c>info</c> / <c>open</c> / <c>new</c>: the open level at a glance.</summary>
public sealed class InfoResult
{
    /// <summary>Full path, or null for a never-saved level.</summary>
    public string? Path { get; init; }

    /// <summary>The level's file name ("untitled.rfl" when never saved).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The RFL version (saves always write 306).</summary>
    public int RflVersion { get; init; }

    /// <summary>True for an Alpine-format level (version ≥ 300).</summary>
    public bool Alpine { get; init; }

    /// <summary>True when the document has unsaved changes.</summary>
    public bool Dirty { get; init; }

    /// <summary>Number of brushes.</summary>
    public int Brushes { get; init; }

    /// <summary>Number of level objects (entities, items, lights, events, …).</summary>
    public int Objects { get; init; }

    /// <summary>Number of object links.</summary>
    public int Links { get; init; }

    /// <summary>Compiled geometry state: "none", "built", "stale" (brushes changed since), or "preview" (unsealed).</summary>
    public string Geometry { get; init; } = "none";

    /// <summary>Rooms in the current compiled geometry (0 when none).</summary>
    public int Rooms { get; init; }

    /// <summary>"headless" or "editor".</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>
    /// Notes about how the file loaded (a version outside the range Glacier reads, an interrupted save,
    /// a non-standard layout that saving rewrites); omitted when there are none.
    /// </summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>
    /// True when the level is read-only: it was saved by a newer Alpine Faction than Glacier writes (RFL version
    /// above 306), so every save is refused (the notes say why); omitted otherwise.
    /// </summary>
    public bool? ReadOnly { get; init; }
}

/// <summary>A script diagnostic: <c>{ kind, message, chunk, line, column, hint }</c> (line/column 1-based, 0 = unknown).</summary>
public sealed class ScriptDiagnosticDto
{
    /// <summary>"syntax", "runtime", "api", or "aborted".</summary>
    public string Kind { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? Chunk { get; init; }

    public int Line { get; init; }

    public int Column { get; init; }

    public string? Hint { get; init; }

    /// <summary>The one-line rendering shown in the Script Log ("name.lua:12: message (hint)").</summary>
    public string Display { get; init; } = string.Empty;
}

/// <summary>A log line: <c>{ level: "output"|"info"|"warning"|"error", message }</c>.</summary>
public sealed record LogLineDto(string Level, string Message);

/// <summary><c>run</c>: the outcome of one script run (one undo step when committed).</summary>
public sealed class RunResult
{
    /// <summary>The script name (chunk name in diagnostics).</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>True when the script completed without error.</summary>
    public bool Success { get; init; }

    /// <summary>True for a dry run (nothing applied).</summary>
    public bool DryRun { get; init; }

    /// <summary>True when the run's changes were committed as one undo step.</summary>
    public bool Committed { get; init; }

    /// <summary>The undo entry label ("CLI: &lt;script&gt;"), when committed.</summary>
    public string? UndoLabel { get; init; }

    /// <summary>Changes by kind ("placed", "moved", "deleted", …), in recorded order.</summary>
    public Dictionary<string, int> Changes { get; init; } = new();

    /// <summary>Total number of recorded changes.</summary>
    public int ChangeCount { get; init; }

    /// <summary>The script's return value, rendered as text.</summary>
    public string? ReturnValue { get; init; }

    /// <summary>The UIDs (objects, brushes, keyframes) a committed run created, ascending; empty otherwise.</summary>
    public List<int> Created { get; init; } = new();

    /// <summary>The provenance tags written on every created UID (absent when auto-tagging was off).</summary>
    public Dictionary<string, string>? Tags { get; init; }

    /// <summary>The failure, when <see cref="Success"/> is false.</summary>
    public ScriptDiagnosticDto? Error { get; init; }

    /// <summary>Everything the run logged (print/log.*/warnings), in order.</summary>
    public List<LogLineDto> Log { get; init; } = new();

    /// <summary>The build after the run (<c>build</c> / <c>light</c> args), when requested.</summary>
    public BuildResult? Build { get; init; }

    /// <summary>The save performed after the run (<c>save</c> arg), when requested and successful.</summary>
    public SaveResult? Saved { get; init; }
}

/// <summary><c>undo</c>: the command-line runs taken back.</summary>
public sealed class UndoResult
{
    /// <summary>The labels of the undone steps, newest first.</summary>
    public List<string> Undone { get; init; } = new();

    /// <summary><c>redo</c>: the labels of the re-applied steps, in the order they were redone (absent for <c>undo</c>).</summary>
    public List<string>? Redone { get; init; }

    /// <summary>The label of the step now on top of the history (null when nothing is left to undo).</summary>
    public string? Next { get; init; }
}

/// <summary>A build message: <c>{ severity, text, pos?, brushUid? }</c>.</summary>
public sealed class BuildMessageDto
{
    /// <summary>"info", "warning", or "error".</summary>
    public string Severity { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>The world point the message is about, when it has one.</summary>
    public double[]? Pos { get; init; }

    /// <summary>The brush the message is about, when known.</summary>
    public int? BrushUid { get; init; }
}

/// <summary><c>build</c>: compile statistics (RED's report figures) + messages with locations.</summary>
public sealed class BuildResult
{
    /// <summary>True for a sealed build (surfaces + t-joint seal); false for the fast unsealed preview.</summary>
    public bool Sealed { get; init; }

    /// <summary>True when lighting was baked.</summary>
    public bool Lit { get; init; }

    /// <summary>"Shared BSP" or "Incremental".</summary>
    public string Method { get; init; } = string.Empty;

    public int Brushes { get; init; }

    public int Rooms { get; init; }

    public int Subrooms { get; init; }

    public int Portals { get; init; }

    public int Faces { get; init; }

    public int Vertices { get; init; }

    public int Surfaces { get; init; }

    public int LightmapPages { get; init; }

    /// <summary>Wall-clock build time.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>True when any message is an error.</summary>
    public bool HasErrors { get; init; }

    /// <summary>
    /// Alpine RED's build-repair counters ("Repair holes when building"), present when the hole repair ran — a
    /// sealed build with the setting on; absent on a preview or with repair off.
    /// </summary>
    public BuildRepairDto? Repair { get; init; }

    public List<BuildMessageDto> Messages { get; init; } = new();

    /// <summary>The save performed after the build (<c>save</c> arg), when requested.</summary>
    public SaveResult? Saved { get; set; }
}

/// <summary>The eleven build-repair counters Alpine RED prints after a build, in its order.</summary>
public sealed class BuildRepairDto
{
    /// <summary>Holes (open-edge groups, per room) right before the repair.</summary>
    public int HolesAfterRooms { get; init; }

    public int PortalPlanesUnified { get; init; }

    public int PortalCopiesMoved { get; init; }

    public int SplitRoomsJoined { get; init; }

    public int VerticesWelded { get; init; }

    public int EdgesSplit { get; init; }

    public int CollapsedFacesRemoved { get; init; }

    public int PortalStripsRemoved { get; init; }

    public int PortalGapsFilled { get; init; }

    public int WallGapsFilled { get; init; }

    public int UnplacedPortalFacesRemoved { get; init; }

    /// <summary>The DTO for a build's counters, or null when the hole repair did not run.</summary>
    public static BuildRepairDto? From(Ged.Core.Compiler.BuildRepairStats s) => !s.Ran ? null : new BuildRepairDto
    {
        HolesAfterRooms = s.HolesAfterRooms,
        PortalPlanesUnified = s.PortalPlanesUnified,
        PortalCopiesMoved = s.PortalCopiesMoved,
        SplitRoomsJoined = s.SplitRoomsJoined,
        VerticesWelded = s.VerticesWelded,
        EdgesSplit = s.EdgesSplit,
        CollapsedFacesRemoved = s.CollapsedFacesRemoved,
        PortalStripsRemoved = s.PortalStripsRemoved,
        PortalGapsFilled = s.PortalGapsFilled,
        WallGapsFilled = s.WallGapsFilled,
        UnplacedPortalFacesRemoved = s.UnplacedPortalFacesRemoved,
    };
}

/// <summary><c>holes</c>: open edges of a SEALED build (never measured on a preview).</summary>
public sealed class HolesResult
{
    /// <summary>Number of leak edges.</summary>
    public int Count { get; init; }

    /// <summary>True when no leak was found.</summary>
    public bool Watertight { get; init; }

    /// <summary>Midpoint of each leak edge (first <see cref="HolesLimit"/> only), sorted for stable output.</summary>
    public List<double[]> Positions { get; init; } = new();

    /// <summary>True when <see cref="Positions"/> was capped.</summary>
    public bool Truncated { get; init; }

    /// <summary>The cap on listed positions.</summary>
    public const int HolesLimit = 500;
}

/// <summary>A lint finding: <c>{ severity, category, message, uid?, secondaryUid?, pos?, blocksSave }</c>.</summary>
public sealed class LintFindingDto
{
    /// <summary>"info", "warning", or "error".</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>The finding category (e.g. "brokenLink", "geometryLeak", "missingPlayerStart").</summary>
    public string Category { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public int? Uid { get; init; }

    public int? SecondaryUid { get; init; }

    public double[]? Pos { get; init; }

    public bool BlocksSave { get; init; }
}

/// <summary><c>lint</c>: the linter's findings (errors first) plus severity counts.</summary>
public sealed class LintResult
{
    public int Errors { get; init; }

    public int Warnings { get; init; }

    public int Info { get; init; }

    public List<LintFindingDto> Findings { get; init; } = new();

    /// <summary>Caveats about this run (e.g. the leak check was skipped on unsealed preview geometry).</summary>
    public List<string> Notes { get; init; } = new();
}

/// <summary><c>save</c>: where the level was written.</summary>
public sealed class SaveResult
{
    /// <summary>The written path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// The RFL version written: always the current Alpine version (306). A level opened at an older version is
    /// upgraded by the save, so this can differ from the version the level reported when it was opened.
    /// </summary>
    public int RflVersion { get; init; }

    /// <summary>
    /// True when the level was written to a file other than its own: a path other than the one it was opened from
    /// or last saved to, or the first save of a level that had never been saved. The document now points at
    /// <see cref="Path"/> either way.
    /// </summary>
    public bool SaveAs { get; init; }

    /// <summary>The file the level pointed at before this save; null for a level that had never been saved.</summary>
    public string? PreviousPath { get; init; }

    /// <summary>True when the header timestamp / level_info date were updated (something changed).</summary>
    public bool Stamped { get; init; }

    /// <summary>The save advisory ("Saved with unbuilt geometry changes — …"), when any.</summary>
    public string? Notice { get; init; }

    /// <summary>True when the save first built the level's geometry (it was missing, stale or a preview).</summary>
    public bool Built { get; init; }

    /// <summary>A prominent warning (a level written without compiled geometry with --no-build), when any.</summary>
    public string? Warning { get; init; }

    /// <summary>
    /// The save log: what upgrading the level to RFL 306 changed or activated (a fog near clip reset to 0, Looping
    /// sound events that now loop, a pre-release props layout rewritten) and an Alpine lightmap chunk dropped because
    /// Glacier rebuilt or relit the level; omitted when nothing.
    /// </summary>
    public IReadOnlyList<string>? UpgradeNotes { get; init; }
}

/// <summary>JSON helpers shared by the result builders.</summary>
/// <summary>
/// <c>snapshot</c>: one chunk of the open level's raw bytes plus its state (see
/// <c>AutomationCommands</c>; used by <c>glacier-cli render --attach</c>).
/// </summary>
public sealed class SnapshotResult
{
    /// <summary>Identifies this snapshot for follow-up chunk requests.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>The level's path (null when never saved).</summary>
    public string? Path { get; init; }

    /// <summary>The level's file name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The whole level's size in bytes.</summary>
    public int Size { get; init; }

    /// <summary>SHA-256 of the whole level (lowercase hex).</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Compiled geometry: none | preview | stale | built.</summary>
    public string Geometry { get; init; } = string.Empty;

    /// <summary>Brushes changed since the last build.</summary>
    public bool GeometryDirty { get; init; }

    /// <summary>The compiled geometry is an unsealed preview.</summary>
    public bool GeometryIsPreview { get; init; }

    /// <summary>Lighting changed since the last bake.</summary>
    public bool LightingDirty { get; init; }

    /// <summary>The document has unsaved changes.</summary>
    public bool Dirty { get; init; }

    /// <summary>The RF install the source session has mounted (for textures), or null.</summary>
    public string? RfInstallDir { get; init; }

    /// <summary>This chunk's byte offset.</summary>
    public int Offset { get; init; }

    /// <summary>This chunk, base64.</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>True when this chunk is the last.</summary>
    public bool Complete { get; init; }
}

internal static class Dto
{
    /// <summary>A position as a rounded [x, y, z] array (−0 normalized to 0, for stable output).</summary>
    public static double[] V(Vec3 v) => new[] { R(v.X), R(v.Y), R(v.Z) };

    /// <summary>A position as a rounded [x, y, z] array, or null.</summary>
    public static double[]? V(Vec3? v) => v is Vec3 p ? V(p) : null;

    /// <summary>Rounds to 3 decimals; −0 becomes 0.</summary>
    public static double R(double v)
    {
        double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
        return r == 0 ? 0 : r;
    }

    /// <summary>lowerCamel name of an enum value.</summary>
    public static string Camel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
