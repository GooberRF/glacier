using System;

namespace Ged.Core.Scripting;

/// <summary>
/// Per-run options: dry-run vs apply, destructive authorization, the deterministic seed, the
/// chunk name shown in errors, and the sandbox limits. The runner combines these with
/// <see cref="ScriptServices"/> to build the <see cref="ScriptContext"/> and wrap the run in a
/// single undo transaction.
/// </summary>
public sealed class ScriptRunOptions
{
    /// <summary>The source/chunk name surfaced in error coordinates (e.g. the file name).</summary>
    public string ChunkName { get; init; } = "script";

    /// <summary>When true the transaction is always rolled back — the run reports what it *would*
    /// change without touching the document, and destructive ops are disabled.</summary>
    public bool DryRun { get; init; }

    /// <summary>Pre-authorizes destructive ops (the <c>--@allow-destructive</c> batch posture).
    /// Ignored in dry-run.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>The deterministic RNG seed for this run.</summary>
    public int Seed { get; init; }

    /// <summary>Sandbox execution limits.</summary>
    public ScriptExecutionLimits Limits { get; init; } = ScriptExecutionLimits.Default;

    /// <summary>The API version the script targets, when known from a <c>--@api</c> header.</summary>
    public int? DeclaredApiVersion { get; init; }

    /// <summary>
    /// The label of the run's single undo entry. Null (default) = <c>"Script: &lt;chunk&gt;"</c>; runs
    /// started from outside the editor (command-line tools, agents) use <c>"CLI: &lt;script&gt;"</c> so
    /// the History panel shows where each step came from.
    /// </summary>
    public string? UndoLabel { get; init; }
}
