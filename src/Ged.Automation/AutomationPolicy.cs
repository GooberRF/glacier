using System;
using System.Collections.Generic;

namespace Ged.Automation;

/// <summary>
/// Progress of a long-running command (a build's compile stages): <paramref name="Stage"/> is the
/// compiler's stage text, <paramref name="Current"/> of <paramref name="Total"/> the step within it
/// (0 of 1 for a stage without steps).
/// </summary>
public readonly record struct AutomationProgress(string Stage, int Current, int Total);

/// <summary>
/// What an automation session may do, decided by whoever STARTED the session (a server's launch flags,
/// the editor's settings) — never by a request. When <see cref="AutomationCommands.Policy"/> is set, the
/// <c>allowDestructive</c> request argument is ignored and <see cref="AllowDestructive"/> decides; with no
/// policy (the command line) the request argument keeps carrying the human's own <c>--allow-destructive</c>.
/// </summary>
public sealed class AutomationPolicy
{
    /// <summary>The commands that change the document, the undo history or files (refused when <see cref="ReadOnly"/>).</summary>
    public static readonly IReadOnlySet<string> MutatingCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "new", "open", "run", "build", "save", "undo", "redo",
    };

    /// <summary>Allows discarding unsaved changes, overwriting another existing file, and a script's delete / save / package.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Refuses every command in <see cref="MutatingCommands"/> (a <c>run</c> dry run is still allowed).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The command names this session may execute; null = every command.</summary>
    public IReadOnlySet<string>? AllowedCommands { get; init; }
}
