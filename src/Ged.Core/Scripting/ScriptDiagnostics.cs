using System;

namespace Ged.Core.Scripting;

/// <summary>
/// A script error carrying source coordinates when the engine supplies them (plan §5.6).
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

    public static ScriptRunResult Ok(string? returnValue, bool committed, int undoNodesAdded, bool wasDryRun) =>
        new(true, committed, returnValue, null, undoNodesAdded, wasDryRun);

    public static ScriptRunResult Failed(ScriptDiagnostic error, bool wasDryRun) =>
        new(false, false, null, error, 0, wasDryRun);
}
