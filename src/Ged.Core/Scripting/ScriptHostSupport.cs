using System;

namespace Ged.Core.Scripting;

/// <summary>
/// A confirmation gate for destructive operations (plan §5.10). Interactive callers show a
/// dialog; batch callers with a <c>--@allow-destructive</c> header auto-approve; dry-run
/// denies unconditionally. The facade calls this before any delete / overwrite-save /
/// package / playtest.
/// </summary>
public interface IScriptConfirmation
{
    /// <summary>Returns true to allow a destructive operation described by <paramref name="message"/>.</summary>
    bool Confirm(string title, string message);
}

/// <summary>A confirmation policy that always allows (batch / <c>--@allow-destructive</c>).</summary>
public sealed class AllowAllConfirmation : IScriptConfirmation
{
    public bool Confirm(string title, string message) => true;
}

/// <summary>A confirmation policy that always denies (unknown-script default outside dry-run).</summary>
public sealed class DenyAllConfirmation : IScriptConfirmation
{
    public bool Confirm(string title, string message) => false;
}

/// <summary>Progress from a long operation (build/light/package), forwarded to the UI overlay.</summary>
public readonly record struct ScriptProgress(string Stage, int Current, int Total)
{
    /// <summary>Fractional completion in [0,1], or -1 when indeterminate.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : -1;
}

/// <summary>A sink for operation progress (the App wires this to its progress overlay; §5.5).</summary>
public interface IScriptProgressSink
{
    void Report(ScriptProgress progress);
}
