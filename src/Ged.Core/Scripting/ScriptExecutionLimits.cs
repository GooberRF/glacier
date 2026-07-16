using System;

namespace Ged.Core.Scripting;

/// <summary>
/// The sandbox execution constraints (plan §5.10): an instruction budget and a wall-clock
/// timeout, enforced by running the script as a coroutine that yields to the host every
/// <see cref="YieldEvery"/> VM instructions. Between yields the host checks the budget, the
/// timeout, and the shared <c>CancellationToken</c>, so a runaway <c>while true</c> loop is
/// always interruptible and never hangs the UI.
/// </summary>
public sealed class ScriptExecutionLimits
{
    /// <summary>Maximum VM instructions before the run is aborted. Non-positive = unbounded.</summary>
    public long InstructionBudget { get; init; } = 200_000_000;

    /// <summary>Wall-clock ceiling. <see cref="TimeSpan.Zero"/> or negative = unbounded.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Instructions between host-side yields (budget/timeout/cancellation checks).</summary>
    public int YieldEvery { get; init; } = 20_000;

    /// <summary>Interactive defaults: a generous budget with a 30 s timeout.</summary>
    public static ScriptExecutionLimits Default => new();

    /// <summary>Interactive editor runs: snappier 12 s ceiling so a runaway loop returns quickly.</summary>
    public static ScriptExecutionLimits Interactive => new()
    {
        InstructionBudget = 100_000_000,
        Timeout = TimeSpan.FromSeconds(12),
        YieldEvery = 15_000,
    };

    /// <summary>REPL defaults: quick to interrupt (short timeout, frequent yields).</summary>
    public static ScriptExecutionLimits Repl => new()
    {
        InstructionBudget = 50_000_000,
        Timeout = TimeSpan.FromSeconds(15),
        YieldEvery = 10_000,
    };

    /// <summary>No limits (used only by trusted batch callers / long procedural generation).</summary>
    public static ScriptExecutionLimits Unbounded => new()
    {
        InstructionBudget = 0,
        Timeout = TimeSpan.Zero,
        YieldEvery = 50_000,
    };
}
