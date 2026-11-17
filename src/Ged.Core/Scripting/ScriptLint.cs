using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Linting;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>lint</c> global: run the built-in <see cref="LevelLinter"/> and
/// contribute custom, project-specific findings that merge into the report. Read-only — running
/// the linter never mutates the document.
/// </summary>
public sealed class ScriptLint
{
    private readonly ScriptContext _ctx;
    private readonly List<ScriptLintFinding> _contributed = new();

    internal ScriptLint(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>lint.run()</c> — runs the linter and returns a report (incl. contributed findings).</summary>
    [ScriptDoc("Runs the level linter (plus findings this script added with lint.add) and returns a lint_report; the craft audit is level.audit{}. Read-only.", Example = "local r = lint.run() log.info(r.error_count .. \" errors\")")]
    public ScriptLintReport Run()
    {
        // The out-of-norm check measures the compiled geometry the level holds now: run ops.build() first after edits.
        var options = new LintOptions { Vfs = _ctx.Assets, CheckNorms = true, FileName = System.IO.Path.GetFileName(_ctx.Document.Path ?? string.Empty) };
        LintReport report = LevelLinter.Lint(_ctx.Document.Rfl, options);
        var findings = report.Findings
            .Select(f => new ScriptLintFinding(f.Severity.ToString(), f.Category.ToString(), f.Message, f.Uid ?? -1, f.Location))
            .Concat(_contributed)
            .ToList();
        return new ScriptLintReport(findings);
    }

    /// <summary>Lua: <c>lint.add("error"|"warning"|"info", "message" [, uid])</c> — contributes a finding.</summary>
    [ScriptDoc("Adds a custom finding (severity \"error\", \"warning\" or \"info\", message, optional uid) to later lint.run() reports.", Example = "lint.add(\"warning\", \"trigger has no target\", 42)")]
    public void Add(string severity, string message, int uid = -1)
    {
        string sev = NormalizeSeverity(severity);
        _contributed.Add(new ScriptLintFinding(sev, "Script", message ?? string.Empty, uid));
        _ctx.Log.Emit(sev == "Error" ? ScriptLogLevel.Error : sev == "Warning" ? ScriptLogLevel.Warning : ScriptLogLevel.Info,
            $"lint: {message}");
    }

    /// <summary>Lua: <c>lint.contributed()</c> — the findings this script added.</summary>
    [ScriptDoc("The findings this script added with lint.add, as lint_finding[].", Example = "log.info(#lint.contributed() .. \" custom findings\")")]
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
    [ScriptDoc("Every finding, built-in and script-added, as lint_finding[].")]
    public ScriptLintFinding[] Findings => _findings.ToArray();

    /// <summary>Lua: <c>report.count</c>.</summary>
    [ScriptDoc("The total number of findings.")]
    public int Count => _findings.Count;

    /// <summary>Lua: <c>report.error_count</c>.</summary>
    [ScriptDoc("The number of error findings.")]
    public int ErrorCount => _findings.Count(f => f.Severity == "Error");

    /// <summary>Lua: <c>report.warning_count</c>.</summary>
    [ScriptDoc("The number of warning findings.")]
    public int WarningCount => _findings.Count(f => f.Severity == "Warning");

    /// <summary>Lua: <c>report.is_clean</c>.</summary>
    [ScriptDoc("True when there are no errors and no warnings (info findings allowed).")]
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;

    public override string ToString() => $"{ErrorCount} error(s), {WarningCount} warning(s), {_findings.Count} total";
}

/// <summary>A single lint finding surfaced to Lua.</summary>
public sealed class ScriptLintFinding
{
    internal ScriptLintFinding(string severity, string category, string message, int uid, Vec3? location = null)
    {
        Severity = severity;
        Category = category;
        Message = message;
        Uid = uid;
        Location = location;
    }

    /// <summary>Lua: <c>finding.severity</c> — "Error" | "Warning" | "Info".</summary>
    [ScriptDoc("\"Error\", \"Warning\" or \"Info\".")]
    public string Severity { get; }

    /// <summary>Lua: <c>finding.category</c>.</summary>
    [ScriptDoc("The finding category (e.g. \"BrokenLink\", \"GeometryLeak\", \"Script\" for lint.add findings).")]
    public string Category { get; }

    /// <summary>Lua: <c>finding.message</c>.</summary>
    [ScriptDoc("The finding text.")]
    public string Message { get; }

    /// <summary>Lua: <c>finding.uid</c> — the related object UID, or -1.</summary>
    [ScriptDoc("The UID of the object or brush the finding is about, or -1.")]
    public int Uid { get; }

    /// <summary>Lua: <c>finding.has_location</c> — true when the finding carries a world position.</summary>
    [ScriptDoc("True when the finding has a world position (pos).")]
    public bool HasLocation => Location.HasValue;

    /// <summary>Lua: <c>finding.pos</c> — the world-space position the finding is about (an object, a
    /// leak edge midpoint), or nil when it has none.</summary>
    [ScriptDoc("The world position the finding is about (an object, a leak edge), or nil.")]
    public Vec3? Pos => Location;

    internal Vec3? Location { get; }

    public override string ToString() => $"[{Severity}] {Message}";
}
