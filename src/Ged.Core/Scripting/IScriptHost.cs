using System;
using System.Threading;

namespace Ged.Core.Scripting;

/// <summary>
/// The swappable engine boundary (plan §5.1, §8): the App references this, never MoonSharp
/// directly, so replacing the engine (MoonSharp ↔ Jint) is a one-project change. Implemented by
/// <c>Ged.Scripting.MoonSharpHost</c>.
/// </summary>
public interface IScriptHost
{
    /// <summary>The engine name (e.g. "Lua (MoonSharp)").</summary>
    string EngineName { get; }

    /// <summary>The engine/language version string.</summary>
    string EngineVersion { get; }

    /// <summary>Creates a session that binds <paramref name="context"/>'s globals; globals persist
    /// across <see cref="IScriptSession.Execute"/> calls within the session (used by the REPL).</summary>
    IScriptSession CreateSession(ScriptContext context);
}

/// <summary>A live engine session with the facade globals bound. Not thread-safe; one run at a time.</summary>
public interface IScriptSession : IDisposable
{
    /// <summary>Executes one chunk under the given limits + cancellation, translating engine errors
    /// (with source coordinates where available) into a <see cref="ScriptExecution"/>.</summary>
    ScriptExecution Execute(string source, string chunkName, ScriptExecutionLimits limits, CancellationToken cancellation);
}

/// <summary>The engine-level outcome of executing one chunk (before the runner applies undo policy).</summary>
public sealed record ScriptExecution(bool Success, string? ReturnValue, ScriptDiagnostic? Error)
{
    public static ScriptExecution Ok(string? returnValue) => new(true, returnValue, null);

    public static ScriptExecution Fail(ScriptDiagnostic error) => new(false, null, error);
}
