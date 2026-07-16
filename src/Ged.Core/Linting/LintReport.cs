using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Linting;

/// <summary>
/// The result of a <see cref="LevelLinter"/> run: the findings plus severity
/// counts and the subset that blocks a save to the linted target.
/// </summary>
public sealed class LintReport
{
    public LintReport(IReadOnlyList<LintFinding> findings)
    {
        // Errors first, then warnings, then info; stable within a severity.
        Findings = findings
            .OrderByDescending(f => f.Severity)
            .ToList();
    }

    public IReadOnlyList<LintFinding> Findings { get; }

    public int ErrorCount => Findings.Count(f => f.Severity == LintSeverity.Error);

    public int WarningCount => Findings.Count(f => f.Severity == LintSeverity.Warning);

    public int InfoCount => Findings.Count(f => f.Severity == LintSeverity.Info);

    /// <summary>True when nothing was found.</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>The findings that block a save to the linted target.</summary>
    public IReadOnlyList<LintFinding> Blocking => Findings.Where(f => f.BlocksSave).ToList();

    /// <summary>True when a save to the linted target must be blocked.</summary>
    public bool HasBlockingIssues => Findings.Any(f => f.BlocksSave);

    /// <summary>A one-line summary for the status bar / pre-save prompt.</summary>
    public string Summary()
    {
        if (IsClean)
        {
            return "Linter: no issues.";
        }

        var parts = new List<string>();
        if (ErrorCount > 0)
        {
            parts.Add($"{ErrorCount} error(s)");
        }

        if (WarningCount > 0)
        {
            parts.Add($"{WarningCount} warning(s)");
        }

        if (InfoCount > 0)
        {
            parts.Add($"{InfoCount} info");
        }

        return "Linter: " + string.Join(", ", parts) + ".";
    }
}
