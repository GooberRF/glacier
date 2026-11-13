using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Scripting;

/// <summary>
/// A script error carrying source coordinates when the engine supplies them.
/// Rendered in the Script Log as <c>script.lua:12: attempt to index nil field 'texture'</c>
/// with a one-line friendly hint.
/// </summary>
public sealed class ScriptDiagnostic
{
    public ScriptDiagnostic(ScriptErrorKind kind, string message, string? chunk = null, int line = 0, int column = 0, string? hint = null)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        Chunk = chunk;
        Line = line;
        Column = column;
        Hint = hint;
    }

    public ScriptErrorKind Kind { get; }

    /// <summary>The raw engine message (already decorated with coordinates when available).</summary>
    public string Message { get; }

    /// <summary>The chunk/source name (e.g. the script file name), or null.</summary>
    public string? Chunk { get; }

    /// <summary>1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>1-based column, or 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>An optional one-line actionable hint appended after the message.</summary>
    public string? Hint { get; }

    /// <summary>A single-line rendering for the Script Log.</summary>
    public string ToDisplayString()
    {
        string loc = Line > 0
            ? $"{Chunk ?? "script"}:{Line}: "
            : (Chunk is not null ? $"{Chunk}: " : string.Empty);
        string body = $"{loc}{Message}";
        return Hint is { Length: > 0 } ? $"{body}  ({Hint})" : body;
    }

    public override string ToString() => ToDisplayString();
}

/// <summary>The category of a <see cref="ScriptDiagnostic"/>.</summary>
public enum ScriptErrorKind
{
    /// <summary>The script failed to parse.</summary>
    Syntax,

    /// <summary>The script threw at runtime (nil index, bad arithmetic, …).</summary>
    Runtime,

    /// <summary>A facade call was misused (bad kind, missing UID, destructive op denied).</summary>
    Api,

    /// <summary>The instruction budget or timeout was exceeded, or the run was canceled.</summary>
    Aborted,
}

/// <summary>
/// The outcome of one script run. On success <see cref="ReturnValue"/> holds the Lua return
/// (as a string for display); on failure <see cref="Error"/> is populated. <see cref="Committed"/>
/// records whether the transaction was committed (a real mutation) or rolled back (dry-run, error,
/// or a pure query) — this is what the "1 undo step" affordance keys off.
/// </summary>
public sealed class ScriptRunResult
{
    private ScriptRunResult(bool success, bool committed, string? returnValue, ScriptDiagnostic? error, int undoNodesAdded, bool wasDryRun)
    {
        Success = success;
        Committed = committed;
        ReturnValue = returnValue;
        Error = error;
        UndoNodesAdded = undoNodesAdded;
        WasDryRun = wasDryRun;
    }

    public bool Success { get; }

    /// <summary>True when the run's transaction was committed (produced an undo entry).</summary>
    public bool Committed { get; }

    public string? ReturnValue { get; }

    public ScriptDiagnostic? Error { get; }

    /// <summary>Undo nodes the run added (0 for a pure query / rolled-back run, 1 for a committed run).</summary>
    public int UndoNodesAdded { get; }

    /// <summary>True when the run executed in dry-run/preview mode (always rolled back).</summary>
    public bool WasDryRun { get; }

    /// <summary>
    /// What the run changed (or, in dry-run, would have changed), by kind — e.g. <c>placed</c>,
    /// <c>moved</c>, <c>deleted</c> — in first-recorded order. Empty for a failed or read-only run.
    /// </summary>
    public IReadOnlyDictionary<string, int> Changes { get; private init; } = new Dictionary<string, int>();

    /// <summary>The total of <see cref="Changes"/>.</summary>
    public int ChangeCount
    {
        get
        {
            int sum = 0;
            foreach (int v in Changes.Values)
            {
                sum += v;
            }

            return sum;
        }
    }

    /// <summary>
    /// The UIDs (objects, brushes, keyframes) a committed run created and that still exist at commit,
    /// ascending. Empty for a dry run, a failed run, or a run that changed nothing. C#-only: the
    /// command-line tools report it so a caller can address exactly what a run added.
    /// </summary>
    public IReadOnlyList<int> CreatedUids { get; private init; } = Array.Empty<int>();

    /// <summary>A copy of this result carrying <paramref name="changes"/> (snapshotted).</summary>
    public ScriptRunResult WithChanges(IReadOnlyDictionary<string, int> changes) =>
        new(Success, Committed, ReturnValue, Error, UndoNodesAdded, WasDryRun)
        {
            Changes = new Dictionary<string, int>(changes, StringComparer.Ordinal),
            CreatedUids = CreatedUids,
            FacadeCalls = FacadeCalls,
            ScriptTime = ScriptTime,
        };

    /// <summary>A copy of this result carrying <paramref name="created"/> (snapshotted, sorted ascending).</summary>
    public ScriptRunResult WithCreated(IReadOnlyCollection<int> created) =>
        new(Success, Committed, ReturnValue, Error, UndoNodesAdded, WasDryRun)
        {
            Changes = Changes,
            CreatedUids = created.OrderBy(u => u).ToArray(),
            FacadeCalls = FacadeCalls,
            ScriptTime = ScriptTime,
        };

    /// <summary>
    /// Diagnostic: how many facade methods (Lua → C# entry points such as <c>level.brush</c> or
    /// <c>q:tag</c>) the run called. Property reads are not counted. The organic-geometry corpus budgets it,
    /// so a regression to per-vertex or per-face callbacks fails loudly.
    /// </summary>
    public int FacadeCalls { get; private init; }

    /// <summary>Diagnostic: the run's script execution time (host operations such as builds excluded).</summary>
    public TimeSpan ScriptTime { get; private init; }

    /// <summary>A copy of this result carrying the run's call count and script time.</summary>
    public ScriptRunResult WithStats(int facadeCalls, TimeSpan scriptTime) =>
        new(Success, Committed, ReturnValue, Error, UndoNodesAdded, WasDryRun)
        {
            Changes = Changes,
            CreatedUids = CreatedUids,
            FacadeCalls = facadeCalls,
            ScriptTime = scriptTime,
        };

    public static ScriptRunResult Ok(string? returnValue, bool committed, int undoNodesAdded, bool wasDryRun) =>
        new(true, committed, returnValue, null, undoNodesAdded, wasDryRun);

    public static ScriptRunResult Failed(ScriptDiagnostic error, bool wasDryRun) =>
        new(false, false, null, error, 0, wasDryRun);
}
