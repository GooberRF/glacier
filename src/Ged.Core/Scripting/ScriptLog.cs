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
/// land here — never a modal storm — and are forwarded to an optional sink (the UI
/// panel) while also being retained in <see cref="Entries"/> for tests and REPL history.
/// </summary>
public sealed class ScriptLog
{
    private readonly List<ScriptLogEntry> _entries = new();
    private readonly IScriptLogSink? _sink;

    public ScriptLog(IScriptLogSink? sink = null) => _sink = sink;

    /// <summary>Every line written this session, in order.</summary>
    [ScriptDoc("The log entries this run has written so far (level, message, timestamp).", Example = "log.info(#log.entries .. \" entries\")")]
    public IReadOnlyList<ScriptLogEntry> Entries => _entries;

    /// <summary>Lua: <c>log.info("…")</c>.</summary>
    [ScriptDoc("Writes an info line to the Script Log.", Example = "log.info(\"placed 12 lights\")")]
    public void Info(string message) => Emit(ScriptLogLevel.Info, message);

    /// <summary>Lua: <c>log.warn("…")</c>.</summary>
    [ScriptDoc("Writes a warning line to the Script Log (the run still succeeds).", Example = "log.warn(\"no lights found\")")]
    public void Warn(string message) => Emit(ScriptLogLevel.Warning, message);

    /// <summary>Lua: <c>log.error("…")</c>.</summary>
    [ScriptDoc("Writes an error line to the Script Log without stopping the run (use error() to fail and roll back).", Example = "log.error(\"bad trigger\")")]
    public void Error(string message) => Emit(ScriptLogLevel.Error, message);

    /// <summary>Target of Lua <c>print(...)</c> and general engine output.</summary>
    [ScriptDoc("Writes a plain output line to the Script Log (what print() does).", Example = "log.output(\"done\")")]
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
    [ScriptDoc("Clears this run's collected log entries.", Example = "log.clear()")]
    public void Clear() => _entries.Clear();
}
