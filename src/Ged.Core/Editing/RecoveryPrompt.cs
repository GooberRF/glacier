using System;

namespace Ged.Core.Editing;

/// <summary>The user's choice from the "Recover unsaved changes?" dialog.</summary>
public enum RecoveryChoice
{
    /// <summary>Open the newer autosave (recommended / default). The autosave file is kept
    /// on disk until the next successful save writes the original.</summary>
    OpenAutosave,

    /// <summary>Open the original file, leaving the autosave on disk untouched.</summary>
    OpenOriginal,

    /// <summary>Delete the autosave and open the original.</summary>
    DeleteAutosaveAndOpenOriginal,
}

/// <summary>
/// The resolved outcome of a recovery choice: which file to load, where a subsequent Save
/// writes (always the ORIGINAL path), and how the autosave file is disposed of. Pure — the
/// App executes it (load, delete, set the save target).
/// </summary>
public sealed record RecoveryOutcome(
    string LoadPath,
    string SaveTargetPath,
    bool DeleteAutosaveNow,
    bool DeleteAutosaveOnSave);

/// <summary>
/// Pure recovery decision logic (item 18), independent of the Avalonia dialog so the
/// outcomes are unit-testable: which file loads, whether the autosave is retained or
/// deleted, and the save-path behavior.
/// </summary>
public static class RecoveryDecision
{
    /// <summary>Resolves a <see cref="RecoveryChoice"/> into a concrete <see cref="RecoveryOutcome"/>.</summary>
    public static RecoveryOutcome Resolve(string originalPath, string autosavePath, RecoveryChoice choice) => choice switch
    {
        // Load the autosave content but target the ORIGINAL on save; remove the autosave
        // only after that save succeeds (so a crash before saving does not lose it).
        RecoveryChoice.OpenAutosave =>
            new RecoveryOutcome(autosavePath, originalPath, DeleteAutosaveNow: false, DeleteAutosaveOnSave: true),

        // Open the original; keep the autosave file on disk (the user may recover later).
        RecoveryChoice.OpenOriginal =>
            new RecoveryOutcome(originalPath, originalPath, DeleteAutosaveNow: false, DeleteAutosaveOnSave: false),

        // Open the original and discard the autosave immediately.
        RecoveryChoice.DeleteAutosaveAndOpenOriginal =>
            new RecoveryOutcome(originalPath, originalPath, DeleteAutosaveNow: true, DeleteAutosaveOnSave: false),

        _ => new RecoveryOutcome(originalPath, originalPath, false, false),
    };

    /// <summary>
    /// A short human diff hint for the dialog, e.g. "12 minutes newer". Uses the autosave's
    /// age relative to the original's last-saved time; clamps negatives to "same age".
    /// </summary>
    public static string DescribeAgeDifference(DateTime originalUtc, DateTime autosaveUtc)
    {
        TimeSpan d = autosaveUtc - originalUtc;
        if (d <= TimeSpan.Zero)
        {
            return "the autosave is the same age or older";
        }

        string unit = d.TotalDays >= 1 ? Plural(d.TotalDays, "day")
            : d.TotalHours >= 1 ? Plural(d.TotalHours, "hour")
            : d.TotalMinutes >= 1 ? Plural(d.TotalMinutes, "minute")
            : Plural(d.TotalSeconds, "second");
        return $"the autosave is {unit} newer";
    }

    private static string Plural(double value, string unit)
    {
        int n = (int)Math.Floor(value);
        return $"{n} {unit}{(n == 1 ? string.Empty : "s")}";
    }

    /// <summary>Formats a byte count for display (e.g. "1.4 MB").</summary>
    public static string DescribeSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
