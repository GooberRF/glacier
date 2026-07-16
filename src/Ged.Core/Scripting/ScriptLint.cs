using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Linting;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>lint</c> global (plan §5.4 / §2.5): run the built-in <see cref="LevelLinter"/> and
/// contribute custom, project-specific findings that merge into the report. Read-only — running
/// the linter never mutates the document.
/// </summary>
public sealed class ScriptLint
{
    private readonly ScriptContext _ctx;
    private readonly List<ScriptLintFinding> _contributed = new();

    internal ScriptLint(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>lint.run()</c> — runs the linter and returns a report (incl. contributed findings).</summary>
    public ScriptLintReport Run()
    {
        var options = new LintOptions { Vfs = _ctx.Assets };
        LintReport report = LevelLinter.Lint(_ctx.Document.Rfl, options);
        var findings = report.Findings
            .Select(f => new ScriptLintFinding(f.Severity.ToString(), f.Category.ToString(), f.Message, f.Uid ?? -1))
            .Concat(_contributed)
            .ToList();
        return new ScriptLintReport(findings);
    }

    /// <summary>Lua: <c>lint.add("error"|"warning"|"info", "message" [, uid])</c> — contributes a finding.</summary>
    public void Add(string severity, string message, int uid = -1)
    {
        string sev = NormalizeSeverity(severity);
        _contributed.Add(new ScriptLintFinding(sev, "Script", message ?? string.Empty, uid));
        _ctx.Log.Emit(sev == "Error" ? ScriptLogLevel.Error : sev == "Warning" ? ScriptLogLevel.Warning : ScriptLogLevel.Info,
            $"lint: {message}");
    }

    /// <summary>Lua: <c>lint.contributed()</c> — the findings this script added.</summary>
    public ScriptLintFinding[] Contributed() => _contributed.ToArray();

    private static string NormalizeSeverity(string severity) => (severity ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "error" or "err" => "Error",
        "warning" or "warn" => "Warning",
        _ => "Info",
    };
}

/// <summary>A merged lint report surfaced to Lua by <c>lint.run()</c>.</summary>
public sealed class ScriptLintReport
{
    private readonly List<ScriptLintFinding> _findings;

    internal ScriptLintReport(List<ScriptLintFinding> findings) => _findings = findings;

    /// <summary>Lua: <c>report.findings</c>.</summary>
    public ScriptLintFinding[] Findings => _findings.ToArray();

    /// <summary>Lua: <c>report.count</c>.</summary>
    public int Count => _findings.Count;

    /// <summary>Lua: <c>report.error_count</c>.</summary>
    public int ErrorCount => _findings.Count(f => f.Severity == "Error");

    /// <summary>Lua: <c>report.warning_count</c>.</summary>
    public int WarningCount => _findings.Count(f => f.Severity == "Warning");

    /// <summary>Lua: <c>report.is_clean</c>.</summary>
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;

    public override string ToString() => $"{ErrorCount} error(s), {WarningCount} warning(s), {_findings.Count} total";
}

/// <summary>A single lint finding surfaced to Lua.</summary>
public sealed class ScriptLintFinding
{
    internal ScriptLintFinding(string severity, string category, string message, int uid)
    {
        Severity = severity;
        Category = category;
        Message = message;
        Uid = uid;
    }

    /// <summary>Lua: <c>finding.severity</c> — "Error" | "Warning" | "Info".</summary>
    public string Severity { get; }

    /// <summary>Lua: <c>finding.category</c>.</summary>
    public string Category { get; }

    /// <summary>Lua: <c>finding.message</c>.</summary>
    public string Message { get; }

    /// <summary>Lua: <c>finding.uid</c> — the related object UID, or -1.</summary>
    public int Uid { get; }

    public override string ToString() => $"[{Severity}] {Message}";
}
