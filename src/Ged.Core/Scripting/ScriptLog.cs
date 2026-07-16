using System;
using System.Collections.Generic;

namespace Ged.Core.Scripting;

/// <summary>Severity of a script log line; mirrors <c>LintSeverity</c>/<c>BuildSeverity</c> for coloring.</summary>
public enum ScriptLogLevel
{
    /// <summary>Plain <c>print(...)</c> output.</summary>
    Output,
    Info,
    Warning,
    Error,
}

/// <summary>A single line written to the Script Log surface.</summary>
public readonly record struct ScriptLogEntry(ScriptLogLevel Level, string Message, DateTime Timestamp);

/// <summary>A destination for script log lines (the UI Script Log panel, a test buffer, stdout).</summary>
public interface IScriptLogSink
{
    void Write(ScriptLogEntry entry);
}

/// <summary>
/// The <c>log</c> global plus the target of Lua <c>print(...)</c>. All script output and errors
/// land here (plan §5.6) — never a modal storm — and are forwarded to an optional sink (the UI
/// panel) while also being retained in <see cref="Entries"/> for tests and REPL history.
/// </summary>
public sealed class ScriptLog
{
    private readonly List<ScriptLogEntry> _entries = new();
    private readonly IScriptLogSink? _sink;

    public ScriptLog(IScriptLogSink? sink = null) => _sink = sink;

    /// <summary>Every line written this session, in order.</summary>
    public IReadOnlyList<ScriptLogEntry> Entries => _entries;

    /// <summary>Lua: <c>log.info("…")</c>.</summary>
    public void Info(string message) => Emit(ScriptLogLevel.Info, message);

    /// <summary>Lua: <c>log.warn("…")</c>.</summary>
    public void Warn(string message) => Emit(ScriptLogLevel.Warning, message);

    /// <summary>Lua: <c>log.error("…")</c>.</summary>
    public void Error(string message) => Emit(ScriptLogLevel.Error, message);

    /// <summary>Target of Lua <c>print(...)</c> and general engine output.</summary>
    public void Output(string message) => Emit(ScriptLogLevel.Output, message);

    /// <summary>Writes a pre-classified entry (used by the runner/lint for diagnostics).
    /// Internal — scripts use info/warn/error/print, not this.</summary>
    internal void Emit(ScriptLogLevel level, string message)
    {
        var entry = new ScriptLogEntry(level, message ?? string.Empty, DateTime.Now);
        _entries.Add(entry);
        _sink?.Write(entry);
    }

    /// <summary>Clears retained entries (REPL "clear"). Does not notify the sink.</summary>
    public void Clear() => _entries.Clear();
}
