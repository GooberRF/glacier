using System;

namespace Ged.Core.Scripting;

/// <summary>
/// Thrown by the facade when a script misuses the API (bad object kind, missing UID, a
/// destructive op denied by policy/dry-run, an unsupported API version). Carries actionable
/// text; the host surfaces it as a <see cref="ScriptErrorKind.Api"/> diagnostic with the
/// script's current source coordinates.
/// </summary>
public sealed class ScriptApiException : Exception
{
    public ScriptApiException(string message)
        : base(message)
    {
    }

    public ScriptApiException(string message, string? hint)
        : base(message) => Hint = hint;

    /// <summary>An optional one-line remediation hint.</summary>
    public string? Hint { get; }
}
