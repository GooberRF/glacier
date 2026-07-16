using System;
using System.Text.RegularExpressions;
using System.Threading;
using Ged.Core.Scripting;
using MoonSharp.Interpreter;

namespace Ged.Scripting;

/// <summary>
/// The MoonSharp implementation of <see cref="IScriptHost"/> (plan §5.1). Pure managed, hard
/// sandbox by default (no <c>io</c>/<c>os</c> beyond the API surface), with an instruction budget +
/// timeout + cancellation enforced by running each chunk as a coroutine that yields to the host
/// (§5.10). The App only ever sees <see cref="IScriptHost"/>, so swapping engines is one project.
/// </summary>
public sealed class MoonSharpHost : IScriptHost
{
    public MoonSharpHost() => MoonSharpRegistry.EnsureRegistered();

    public string EngineName => "Lua (MoonSharp)";

    public string EngineVersion => $"Lua 5.2 · MoonSharp {typeof(Script).Assembly.GetName().Version?.ToString(2) ?? "2.0"}";

    public IScriptSession CreateSession(ScriptContext context) => new MoonSharpSession(context);
}

/// <summary>A hard-sandboxed MoonSharp <see cref="Script"/> with the facade globals bound.</summary>
internal sealed class MoonSharpSession : IScriptSession
{
    private readonly Script _script;

    internal MoonSharpSession(ScriptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _script = new Script(CoreModules.Preset_HardSandbox);
        _script.Options.DebugPrint = s => context.Log.Output(s ?? string.Empty);
        _script.Options.CheckThreadAccess = false;

        foreach (System.Collections.Generic.KeyValuePair<string, object> g in context.Globals)
        {
            _script.Globals[g.Key] = g.Value;
        }
    }

    public ScriptExecution Execute(string source, string chunkName, ScriptExecutionLimits limits, CancellationToken cancellation)
    {
        source ??= string.Empty;
        DynValue chunk;
        try
        {
            chunk = _script.LoadString(source, null, chunkName);
        }
        catch (SyntaxErrorException se)
        {
            // REPL convenience: an expression like "level.count" compiles as "return (level.count)".
            if (!TryLoadExpression(source, chunkName, out chunk))
            {
                return ScriptExecution.Fail(SyntaxDiagnostic(se, chunkName));
            }
        }

        DynValue coroutine = _script.CreateCoroutine(chunk);
        coroutine.Coroutine.AutoYieldCounter = Math.Max(1, limits.YieldEvery);
        long used = 0;

        try
        {
            DynValue result = coroutine.Coroutine.Resume();
            while (result.Type == DataType.YieldRequest)
            {
                used += limits.YieldEvery;
                cancellation.ThrowIfCancellationRequested();
                if (limits.InstructionBudget > 0 && used > limits.InstructionBudget)
                {
                    return ScriptExecution.Fail(new ScriptDiagnostic(
                        ScriptErrorKind.Aborted,
                        $"Instruction budget ({limits.InstructionBudget:N0}) exceeded.",
                        chunkName,
                        hint: "The script may have an infinite loop; add a bound or raise the budget."));
                }

                result = coroutine.Coroutine.Resume();
            }

            return ScriptExecution.Ok(FormatReturn(result));
        }
        catch (OperationCanceledException)
        {
            return ScriptExecution.Fail(new ScriptDiagnostic(
                ScriptErrorKind.Aborted, "Script canceled (timeout or Stop).", chunkName));
        }
        catch (SyntaxErrorException se)
        {
            return ScriptExecution.Fail(SyntaxDiagnostic(se, chunkName));
        }
        catch (ScriptRuntimeException re)
        {
            return ScriptExecution.Fail(RuntimeDiagnostic(re, chunkName));
        }
        catch (ScriptApiException api)
        {
            return ScriptExecution.Fail(new ScriptDiagnostic(ScriptErrorKind.Api, api.Message, chunkName, hint: api.Hint));
        }
        catch (InterpreterException ie)
        {
            return ScriptExecution.Fail(new ScriptDiagnostic(
                ScriptErrorKind.Runtime, ie.DecoratedMessage ?? ie.Message, chunkName));
        }
    }

    public void Dispose()
    {
        // MoonSharp Script holds no unmanaged resources; drop the reference implicitly.
    }

    private bool TryLoadExpression(string source, string chunkName, out DynValue chunk)
    {
        try
        {
            chunk = _script.LoadString("return (" + source + "\n)", null, chunkName);
            return true;
        }
        catch (SyntaxErrorException)
        {
            chunk = DynValue.Nil;
            return false;
        }
    }

    private static string? FormatReturn(DynValue value)
    {
        if (value is null || value.IsNil() || value.IsVoid())
        {
            return null;
        }

        return value.ToPrintString();
    }

    private static ScriptDiagnostic SyntaxDiagnostic(SyntaxErrorException se, string chunkName)
    {
        (int line, string message) = ParseCoords(se.DecoratedMessage ?? se.Message, chunkName);
        return new ScriptDiagnostic(ScriptErrorKind.Syntax, message, chunkName, line,
            hint: "Check for a missing 'end', ')' or '\"'.");
    }

    private static ScriptDiagnostic RuntimeDiagnostic(ScriptRuntimeException re, string chunkName)
    {
        (int line, string message) = ParseCoords(re.DecoratedMessage ?? re.Message, chunkName);
        return new ScriptDiagnostic(ScriptErrorKind.Runtime, message, chunkName, line, hint: HintFor(message));
    }

    private static string? HintFor(string message)
    {
        if (message.Contains("index a nil", StringComparison.OrdinalIgnoreCase))
        {
            return "A value is nil — check the name, or that the object/field exists.";
        }

        if (message.Contains("attempt to call a nil", StringComparison.OrdinalIgnoreCase))
        {
            return "Calling a nil value — check the function name (snake_case, e.g. set_texture).";
        }

        if (message.Contains("arithmetic", StringComparison.OrdinalIgnoreCase))
        {
            return "Arithmetic on a non-number — is a value nil or a string?";
        }

        return null;
    }

    private static readonly Regex CoordPattern =
        new(@"^(?<chunk>.*?):\((?<line>\d+),[\d,\- ]+\):\s*(?<msg>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    private static (int Line, string Message) ParseCoords(string decorated, string chunkName)
    {
        Match m = CoordPattern.Match(decorated ?? string.Empty);
        if (m.Success && int.TryParse(m.Groups["line"].Value, out int line))
        {
            return (line, m.Groups["msg"].Value.Trim());
        }

        return (0, decorated ?? string.Empty);
    }
}
