using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Ged.Core.Scripting;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace Ged.Scripting;

/// <summary>
/// The MoonSharp implementation of <see cref="IScriptHost"/>. Pure managed, hard
/// sandbox by default (no <c>io</c>/<c>os</c> beyond the API surface), with an instruction budget +
/// timeout + cancellation enforced by running each chunk as a coroutine that yields to the host
///. The timeout measures script execution time (<see cref="ScriptContext.ScriptElapsed"/>),
/// excluding time spent inside host operations such as builds and bakes. The App only ever sees <see cref="IScriptHost"/>, so swapping engines is one project.
/// </summary>
public sealed class MoonSharpHost : IScriptHost
{
    public MoonSharpHost() => MoonSharpRegistry.EnsureRegistered();

    public string EngineName => "Lua (MoonSharp)";

    public string EngineVersion => $"Lua 5.2 · MoonSharp {typeof(Script).Assembly.GetName().Version?.ToString(2) ?? "2.0"}";

    public IScriptSession CreateSession(ScriptContext context) => new MoonSharpSession(context);
}

/// <summary>
/// A hard-sandboxed MoonSharp <see cref="Script"/> with the facade globals bound.
/// <para>
/// <b>Budgeted execution.</b> MoonSharp only auto-yields a coroutine; a Lua function that C# calls
/// back (a <c>where</c>/<c>each</c> predicate, a <c>ged.group</c> body, a <c>table.sort</c>
/// comparator, and later module top-levels) runs as a nested, non-yielding call, so a runaway loop
/// inside it would escape the instruction budget, the time limit and Stop — and freeze the editor's
/// UI thread in attach mode. Every Lua function this session runs — the chunk itself and every
/// callback, at any nesting depth — therefore goes through <see cref="RunBudgeted"/>: it runs as its
/// own coroutine with the run's <see cref="ScriptExecutionLimits.YieldEvery"/>, and each yield
/// checks the SAME instruction counter, script-time clock and cancellation token as the top level.
/// An overrun throws <see cref="ScriptAbortException"/>, which no Lua <c>pcall</c> can catch, so it
/// unwinds straight to <see cref="Execute"/> and becomes the normal Aborted diagnostic.
/// </para>
/// </summary>
internal sealed class MoonSharpSession : IScriptSession, IScriptModuleHost
{
    private static readonly ConditionalWeakTable<Script, MoonSharpSession> Sessions = new();

    private readonly Script _script;
    private readonly ScriptContext _context;

    /// <summary>The limits/cancellation/counter of the chunk being executed (null between runs).</summary>
    private ExecutionState? _exec;

    internal MoonSharpSession(ScriptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _script = new Script(CoreModules.Preset_HardSandbox);
        _script.Options.DebugPrint = s => context.Log.Output(s ?? string.Empty);
        _script.Options.CheckThreadAccess = false;

        foreach (System.Collections.Generic.KeyValuePair<string, object> g in context.Globals)
        {
            _script.Globals[g.Key] = g.Value;
        }

        BudgetStandardLibraryCallbacks();
        Sessions.AddOrUpdate(_script, this);
        context.AttachModuleHost(this);
    }

    /// <summary>
    /// Runs a module's top level as its own chunk in this session's sandboxed
    /// <see cref="Script"/> — a module sees exactly the globals a script sees — through
    /// <see cref="RunBudgeted"/>, so a runaway module body is charged to the run's instruction budget,
    /// time limit and Stop exactly like the script itself. Returns the chunk's first return value as an
    /// engine value; MoonSharp hands a returned <see cref="DynValue"/> back to Lua untouched.
    /// </summary>
    public object? Import(string name, string source, string chunkName)
    {
        DynValue chunk = _script.LoadString(LuaLocalInit.Apply(source ?? string.Empty), null, chunkName);
        DynValue result = _exec is null ? _script.Call(chunk) : RunBudgeted(chunk);
        if (result.Type == DataType.Tuple)
        {
            result = result.Tuple.Length > 0 ? result.Tuple[0] : DynValue.Nil;
        }

        return result.IsVoid() ? DynValue.Nil : result;
    }

    public ScriptExecution Execute(string source, string chunkName, ScriptExecutionLimits limits, CancellationToken cancellation)
    {
        source ??= string.Empty;
        DynValue chunk;
        try
        {
            chunk = _script.LoadString(LuaLocalInit.Apply(source), null, chunkName);
        }
        catch (SyntaxErrorException se)
        {
            // REPL convenience: an expression like "level.count" compiles as "return (level.count)".
            if (!TryLoadExpression(source, chunkName, out chunk))
            {
                return ScriptExecution.Fail(SyntaxDiagnostic(se, chunkName));
            }
        }

        ExecutionState? outer = _exec;
        var exec = new ExecutionState(limits, cancellation, chunkName);
        _exec = exec;
        try
        {
            DynValue result = RunBudgeted(chunk);

            // An abort raised inside a callback that library code swallowed still ends the run.
            return exec.Abort is { } swallowed ? AbortResult(swallowed, chunkName) : ScriptExecution.Ok(FormatReturn(result));
        }
        catch (Exception ex) when (exec.Abort is not null)
        {
            // Whatever surfaced (the abort itself, or a library's wrapper around it), the run was aborted.
            _ = ex;
            return AbortResult(exec.Abort, chunkName);
        }
        catch (ScriptAbortException abort)
        {
            return ScriptExecution.Fail(abort.Diagnostic);
        }
        catch (OperationCanceledException)
        {
            return ScriptExecution.Fail(new ScriptDiagnostic(
                ScriptErrorKind.Aborted, "Script canceled.", chunkName));
        }
        catch (SyntaxErrorException se)
        {
            return ScriptExecution.Fail(SyntaxDiagnostic(se, chunkName));
        }
        catch (ScriptRuntimeException re)
        {
            return ScriptExecution.Fail(RuntimeDiagnostic(re, chunkName, SiteOf(exec, re)));
        }
        catch (ScriptApiException api)
        {
            ErrorSite? site = SiteOf(exec, api);
            return ScriptExecution.Fail(new ScriptDiagnostic(ScriptErrorKind.Api, api.Message,
                site?.Chunk ?? chunkName, site?.Line ?? 0, hint: api.Hint));
        }
        catch (InterpreterException ie)
        {
            return ScriptExecution.Fail(new ScriptDiagnostic(
                ScriptErrorKind.Runtime, ie.DecoratedMessage ?? ie.Message, chunkName));
        }
        catch (IndexOutOfRangeException ior) when (ior.StackTrace?.Contains("MoonSharp.Interpreter.Execution.VM.Processor", StringComparison.Ordinal) == true)
        {
            // MoonSharp's VM stacks have a fixed size; runaway Lua recursion fills them and surfaces as an
            // index error from inside the VM rather than a Lua error.
            return ScriptExecution.Fail(new ScriptDiagnostic(ScriptErrorKind.Runtime,
                "Stack overflow: Lua recursion went too deep.", chunkName,
                hint: "A function keeps calling itself without stopping; add a base case or use a loop."));
        }
        finally
        {
            _exec = outer;
        }
    }

    public void Dispose()
    {
        // MoonSharp Script holds no unmanaged resources; drop the reference implicitly.
        Sessions.Remove(_script);
    }

    /// <summary>
    /// Calls a Lua function from C# under the owning session's budget (see the class remarks). Used by
    /// every callback converter; outside a budgeted run (no session, or between runs) it is a plain call.
    /// </summary>
    /// <summary>The script context of the session that owns <paramref name="script"/> (null outside one).</summary>
    internal static ScriptContext? ContextOf(Script script) =>
        script is not null && Sessions.TryGetValue(script, out MoonSharpSession? session) ? session._context : null;

    internal static DynValue Invoke(Closure function, params DynValue[] args)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function.OwnerScript is { } owner && Sessions.TryGetValue(owner, out MoonSharpSession? session) && session._exec is not null)
        {
            return session.RunBudgeted(DynValue.NewClosure(function), args);
        }

        return function.Call(args);
    }

    /// <summary>
    /// <c>ok, … = ged.try(fn, ...)</c>: calls <c>fn</c> with the remaining arguments under the run's budget
    /// (<see cref="RunBudgeted"/>: the instruction budget, time limit, Stop and the callback nesting cap all apply)
    /// inside <see cref="ScriptContext.RunRecoverable"/>. Success returns <c>true</c> followed by fn's return values.
    /// A Lua error or API error rolls back every level change fn made and returns <c>false</c> plus an error table
    /// <c>{message, hint, kind, chunk, line}</c> whose <c>tostring</c> is the message and hint. An abort
    /// (<see cref="ScriptAbortException"/>, cancellation, or any error raised once the run is aborting) is never
    /// caught: it unwinds to <see cref="Execute"/> like everywhere else.
    /// </summary>
    internal static DynValue Try(Script script, CallbackArguments args)
    {
        DynValue fn = args.Count > 0 ? args[0] : DynValue.Nil;
        if (fn.Type is not (DataType.Function or DataType.ClrFunction))
        {
            string got = fn.Type == DataType.UserData && fn.UserData?.Object is ScriptGed ? "ged itself" : $"a {fn.Type.ToString().ToLowerInvariant()}";
            throw new ScriptApiException($"ged.try: expected a function as the first argument, got {got}.",
                "Call it with a dot and pass the function itself: local ok, err = ged.try(build_cave, seed).");
        }

        if (!Sessions.TryGetValue(script, out MoonSharpSession? session) || session._exec is not { } exec)
        {
            throw new ScriptApiException("ged.try: only available while a script is running.");
        }

        DynValue[] rest = args.Count > 1 ? args.GetArray(1) : Array.Empty<DynValue>();
        DynValue result = DynValue.Void;
        bool ok = session._context.RunRecoverable(
            () => result = fn.Type == DataType.Function ? session.RunBudgeted(fn, rest) : script.Call(fn, rest),
            ex => exec.Abort is null && ex is ScriptApiException or InterpreterException,
            out Exception? failure);
        if (ok)
        {
            DynValue[] values = result.Type == DataType.Tuple ? result.Tuple : result.IsVoid() ? Array.Empty<DynValue>() : new[] { result };
            var all = new DynValue[values.Length + 1];
            all[0] = DynValue.True;
            Array.Copy(values, 0, all, 1, values.Length);
            return DynValue.NewTuple(all);
        }

        return DynValue.NewTuple(DynValue.False, DynValue.NewTable(session.TryError(exec, failure!)));
    }

    /// <summary>The <c>ged.try</c> error table for <paramref name="failure"/>, located like a run's diagnostic.</summary>
    private Table TryError(ExecutionState exec, Exception failure)
    {
        ScriptDiagnostic d = failure switch
        {
            ScriptRuntimeException re => RuntimeDiagnostic(re, exec.ChunkName, SiteOf(exec, re)),
            ScriptApiException api => SiteOf(exec, api) is { } site
                ? new ScriptDiagnostic(ScriptErrorKind.Api, api.Message, site.Chunk, site.Line, hint: api.Hint)
                : new ScriptDiagnostic(ScriptErrorKind.Api, api.Message, exec.ChunkName, hint: api.Hint),
            InterpreterException ie => new ScriptDiagnostic(ScriptErrorKind.Runtime, ie.DecoratedMessage ?? ie.Message, exec.ChunkName),
            _ => new ScriptDiagnostic(ScriptErrorKind.Runtime, failure.Message, exec.ChunkName),
        };

        var t = new Table(_script);
        t["message"] = d.Message;
        if (d.Hint is { Length: > 0 } hint)
        {
            t["hint"] = hint;
        }

        t["kind"] = d.Kind == ScriptErrorKind.Api ? "api" : "runtime";
        t["chunk"] = d.Chunk;
        if (d.Line > 0)
        {
            t["line"] = d.Line;
        }

        string text = d.Hint is { Length: > 0 } h ? $"{d.Message}  Hint: {h}" : d.Message;
        var meta = new Table(_script);
        meta["__tostring"] = DynValue.NewCallback((_, _) => DynValue.NewString(text), "tostring");
        t.MetaTable = meta;
        return t;
    }

    /// <summary>
    /// The deepest chain of Lua → C# → Lua callback hops one run may nest (a <c>ged.group</c> body
    /// calling <c>ged.group</c>, a predicate re-entering a query, a module imported from a callback…).
    /// Every hop adds CLR stack frames that no instruction budget sees, so unbounded recursion through
    /// callbacks would end in an uncatchable stack overflow that takes the whole process down — in
    /// attach mode, the designer's editor. Plain Lua-to-Lua recursion does not count: MoonSharp runs it
    /// on its own bounded VM stack, not the CLR stack.
    /// </summary>
    internal const int MaxCallbackDepth = 64;

    /// <summary>
    /// Instructions charged for entering a callback. A callback shorter than one yield slice never
    /// yields, so its own instructions are never counted; this nominal entry cost keeps a run that makes
    /// millions of tiny callbacks inside the budget's reach without penalising ordinary predicate
    /// passes over a large level.
    /// </summary>
    internal const int CallbackEntryCost = 100;

    /// <summary>
    /// Runs a Lua function (a chunk, a callback, a module body) to completion as a coroutine that
    /// auto-yields every <see cref="ScriptExecutionLimits.YieldEvery"/> instructions; each yield
    /// charges the shared instruction counter and checks cancellation, the script-time limit and the
    /// instruction budget. Reusable for any nested Lua entry point (module loading). Nesting is limited
    /// to <see cref="MaxCallbackDepth"/> levels (and to the stack actually left on the calling thread);
    /// exceeding either is a sticky abort like a budget overrun.
    /// </summary>
    internal DynValue RunBudgeted(DynValue function, params DynValue[] args)
    {
        ExecutionState exec = _exec ?? throw new InvalidOperationException("No script is executing in this session.");
        if (exec.Abort is { } pending)
        {
            throw pending;
        }

        exec.Depth++;
        try
        {
            if (exec.Depth > 1)
            {
                exec.Used += CallbackEntryCost;
            }

            if (exec.Depth > MaxCallbackDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                throw exec.Abort = new ScriptAbortException(new ScriptDiagnostic(
                    ScriptErrorKind.Aborted,
                    $"Callback nesting too deep (limit {MaxCallbackDepth}).",
                    exec.ChunkName,
                    hint: "A function passed to the API (a ged.group body, a where/each/sort callback) keeps calling back into itself. Recurse in plain Lua, or add a stopping condition."));
            }

            DynValue coroutine = _script.CreateCoroutine(function);
            coroutine.Coroutine.AutoYieldCounter = exec.YieldEvery;
            try
            {
                DynValue result = coroutine.Coroutine.Resume(args);
                while (result.Type == DataType.YieldRequest)
                {
                    CheckBudget(exec, coroutine.Coroutine);
                    result = coroutine.Coroutine.Resume();
                }

                return result;
            }
            catch (Exception ex) when (ex is ScriptApiException or ScriptRuntimeException)
            {
                NoteErrorSite(exec, ex, coroutine.Coroutine);
                throw;
            }
        }
        finally
        {
            exec.Depth--;
        }
    }

    /// <summary>
    /// Records where in the user's script an API or Lua error happened. Walks this coroutine's Lua
    /// call chain innermost-first and keeps the first call site in a chunk that is not an imported
    /// module (<c>lib/…</c>) — the line of the user's own call into the API or the kit. Runs at every
    /// callback level as the exception unwinds outwards, so a misuse inside module code reached
    /// through a callback still resolves to the user's line; when no user frame exists at all, the
    /// innermost module site is kept.
    /// </summary>
    private void NoteErrorSite(ExecutionState exec, Exception ex, Coroutine coroutine)
    {
        if (exec.Site is { IsUser: true } done && ReferenceEquals(done.Error, ex))
        {
            return;
        }

        IReadOnlyList<(string Chunk, int Line)> sites = ScriptCallSites.Read(_script, ex, coroutine);
        foreach ((string chunk, int line) in sites)
        {
            if (!ScriptCallSites.IsModuleChunk(chunk))
            {
                exec.Site = new ErrorSite(ex, chunk, line, IsUser: true);
                return;
            }
        }

        if ((exec.Site is null || !ReferenceEquals(exec.Site.Error, ex)) && sites.Count > 0)
        {
            exec.Site = new ErrorSite(ex, sites[0].Chunk, sites[0].Line, IsUser: false);
        }
    }

    /// <summary>The recorded site of <paramref name="ex"/>, when one was recorded for that exact exception.</summary>
    private static ErrorSite? SiteOf(ExecutionState exec, Exception ex) =>
        exec.Site is { } site && ReferenceEquals(site.Error, ex) ? site : null;

    /// <summary>
    /// Charges one yield slice and enforces cancellation, the script-time limit and the instruction
    /// budget. An abort is sticky: once raised, every later check (and the end of the run) re-raises
    /// it, so library code that catches exceptions around a callback cannot resurrect the run.
    /// </summary>
    private void CheckBudget(ExecutionState exec, Coroutine? coroutine = null)
    {
        if (exec.Abort is { } pending)
        {
            throw pending;
        }

        exec.Used += exec.YieldEvery;
        if (exec.Cancellation.IsCancellationRequested)
        {
            throw exec.Abort = new OperationCanceledException(exec.Cancellation);
        }

        // Wall-clock limit on SCRIPT time: host operations (ops.build / ops.light / hole checks /
        // save / package) are excluded, so building a large level never trips it.
        if (_context.IsOverTimeout(exec.Limits))
        {
            (string chunk, int line, string where) = Where(exec, coroutine);
            throw exec.Abort = new ScriptAbortException(new ScriptDiagnostic(
                ScriptErrorKind.Aborted,
                $"Script timed out after {exec.Limits.Timeout.TotalSeconds:0.#} s of script execution{where}.",
                chunk,
                line,
                hint: "Either the script loops forever, or it does a lot of work in Lua (loops over every node, brush or object). Kit and spatial.* calls run natively; split the work over several runs, or raise the limit: glacier-cli run --timeout <s> (MCP run_lua: timeout_s; 0 = no limit). Time in ops.build / ops.light and other host operations does not count."));
        }

        if (exec.Limits.InstructionBudget > 0 && exec.Used > exec.Limits.InstructionBudget)
        {
            (string chunk, int line, string where) = Where(exec, coroutine);
            throw exec.Abort = new ScriptAbortException(new ScriptDiagnostic(
                ScriptErrorKind.Aborted,
                $"Instruction budget ({exec.Limits.InstructionBudget:N0} Lua instructions) exceeded{where}.",
                chunk,
                line,
                hint: "Either the script loops forever, or it does a lot of work in Lua (loops over every node, brush or object). Kit and spatial.* calls run natively; split the work over several runs, or raise the limit: glacier-cli run --budget <instructions> (MCP run_lua: budget; 0 = no limit)."));
        }
    }

    /// <summary>
    /// Where a run was stopped: the user's line (the innermost frame outside imported modules) as the
    /// diagnostic's chunk and line, plus " at line N" / " in module M line K" text for the message.
    /// </summary>
    private (string Chunk, int Line, string Where) Where(ExecutionState exec, Coroutine? coroutine)
    {
        if (coroutine is null)
        {
            return (exec.ChunkName, 0, string.Empty);
        }

        IReadOnlyList<(string Chunk, int Line)> sites = ScriptCallSites.Current(_script, coroutine);
        (string Chunk, int Line)? user = null;
        foreach ((string chunk, int line) in sites)
        {
            if (!ScriptCallSites.IsModuleChunk(chunk))
            {
                user = (chunk, line);
                break;
            }
        }

        string where = string.Empty;
        if (sites.Count > 0 && ScriptCallSites.IsModuleChunk(sites[0].Chunk))
        {
            where = $" (inside {sites[0].Chunk} line {sites[0].Line})";
        }

        return user is { } us ? (us.Chunk, us.Line, where) : sites.Count > 0 ? (sites[0].Chunk, sites[0].Line, where) : (exec.ChunkName, 0, where);
    }

    private static ScriptExecution AbortResult(Exception abort, string chunkName) => abort is ScriptAbortException a
        ? ScriptExecution.Fail(a.Diagnostic)
        : ScriptExecution.Fail(new ScriptDiagnostic(ScriptErrorKind.Aborted, "Script canceled.", chunkName));

    /// <summary>
    /// The standard-library functions that call a Lua function back from C# (<c>table.sort</c>'s
    /// comparator, <c>string.gsub</c>'s replacement function) are wrapped so the callback is
    /// budgeted like every facade callback.
    /// </summary>
    private void BudgetStandardLibraryCallbacks()
    {
        WrapCallbackArgument("table", "sort", 1);
        WrapCallbackArgument("string", "gsub", 2);
        IterateQueries("ipairs");
        IterateQueries("pairs");
    }

    /// <summary>
    /// <c>ipairs(q)</c> / <c>pairs(q)</c> over a brush or object query iterate its handles (the same as
    /// <c>q.brushes</c> / <c>q.objects</c>), and any other non-table value gets an actionable API error
    /// instead of MoonSharp's internal "table expected, got userdata".
    /// </summary>
    private void IterateQueries(string name)
    {
        if (_script.Globals.Get(name) is not { Type: DataType.ClrFunction } original)
        {
            return;
        }

        _script.Globals[name] = DynValue.NewCallback((ctx, args) =>
        {
            DynValue first = args.Count > 0 ? args[0] : DynValue.Nil;
            if (first.Type == DataType.UserData)
            {
                object? items = first.UserData?.Object switch
                {
                    ScriptBrushQuery q => q.Brushes,
                    ScriptObjectQuery q => q.Objects,
                    _ => null,
                };
                if (items is null)
                {
                    string what = first.UserData?.Object is { } o ? ScriptApiReference.LuaTypeName(o.GetType()) : "userdata";
                    throw new ScriptApiException($"{name}: expected a table, got a {what}.",
                        "Iterate a list such as level.brushes, level.objects or a query's .brushes / .objects, or call q:each(fn).");
                }

                first = DynValue.FromObject(_script, items);
            }

            var forwarded = new DynValue[Math.Max(1, args.Count)];
            forwarded[0] = first;
            for (int i = 1; i < args.Count; i++)
            {
                forwarded[i] = args[i];
            }

            return DynValue.NewTailCallReq(original, forwarded);
        }, name);
    }

    private void WrapCallbackArgument(string library, string function, int argIndex)
    {
        if (_script.Globals.Get(library) is not { Type: DataType.Table } lib || lib.Table.Get(function) is not { Type: DataType.ClrFunction } original)
        {
            return;
        }

        lib.Table[function] = DynValue.NewCallback((ctx, args) =>
        {
            var forwarded = new DynValue[args.Count];
            for (int i = 0; i < args.Count; i++)
            {
                DynValue a = args[i];
                if (i == argIndex && a.Type == DataType.Function)
                {
                    Closure callback = a.Function;
                    a = DynValue.NewCallback((_, inner) => Invoke(callback, inner.GetArray()));
                }

                forwarded[i] = a;
            }

            return DynValue.NewTailCallReq(original, forwarded);
        }, $"{library}.{function}");
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
        (string chunk, int line, string message) = ParseCoords(se.DecoratedMessage ?? se.Message, chunkName);
        return new ScriptDiagnostic(ScriptErrorKind.Syntax, message, chunk, line,
            hint: "Check for a missing 'end', ')' or '\"'.");
    }

    /// <summary>
    /// A Lua runtime error. When it was raised inside an imported module (<c>lib/…</c>) and the user's
    /// own call into that module is known, the diagnostic points at the user's line and keeps the
    /// module location in the message.
    /// </summary>
    private static ScriptDiagnostic RuntimeDiagnostic(ScriptRuntimeException re, string chunkName, ErrorSite? site)
    {
        (string chunk, int line, string message) = ParseCoords(re.DecoratedMessage ?? re.Message, chunkName);
        if (ScriptCallSites.IsModuleChunk(chunk) && site is { IsUser: true })
        {
            string where = line > 0 ? $"{chunk}:{line}" : chunk;
            return new ScriptDiagnostic(ScriptErrorKind.Runtime, $"{message} (in {where})", site.Chunk, site.Line, hint: HintFor(message));
        }

        return new ScriptDiagnostic(ScriptErrorKind.Runtime, message, chunk, line, hint: HintFor(message));
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

    /// <summary>
    /// Splits MoonSharp's decorated "chunk:(line,col): message". The chunk is the one the error
    /// actually occurred in — an imported module's errors report <c>lib/&lt;name&gt;.lua</c> and its line,
    /// not the importing script's.
    /// </summary>
    private static (string Chunk, int Line, string Message) ParseCoords(string decorated, string chunkName)
    {
        Match m = CoordPattern.Match(decorated ?? string.Empty);
        if (m.Success && int.TryParse(m.Groups["line"].Value, out int line))
        {
            string chunk = m.Groups["chunk"].Value.Trim();
            return (chunk.Length > 0 ? chunk : chunkName, line, m.Groups["msg"].Value.Trim());
        }

        return (chunkName, 0, decorated ?? string.Empty);
    }

    /// <summary>The budget state of one executing chunk, shared by every nested callback it triggers.</summary>
    private sealed class ExecutionState
    {
        public ExecutionState(ScriptExecutionLimits limits, CancellationToken cancellation, string chunkName)
        {
            Limits = limits ?? ScriptExecutionLimits.Default;
            Cancellation = cancellation;
            ChunkName = chunkName;
            YieldEvery = Math.Max(1, Limits.YieldEvery);
        }

        public ScriptExecutionLimits Limits { get; }

        public CancellationToken Cancellation { get; }

        public string ChunkName { get; }

        public int YieldEvery { get; }

        /// <summary>Instructions charged so far (one <see cref="YieldEvery"/> slice per yield, at any depth).</summary>
        public long Used { get; set; }

        /// <summary>The abort (budget / timeout / cancellation) raised so far, if any — sticky for the rest of the run.</summary>
        public Exception? Abort { get; set; }

        /// <summary>How many budgeted Lua entry points are currently nested (the chunk itself is 1).</summary>
        public int Depth { get; set; }

        /// <summary>Where the most recent API / Lua error was raised in the user's script (see <see cref="NoteErrorSite"/>).</summary>
        public ErrorSite? Site { get; set; }
    }

    /// <summary>The script location an error is reported at; <see cref="IsUser"/> is false for a module-only site.</summary>
    private sealed record ErrorSite(Exception Error, string Chunk, int Line, bool IsUser);
}

/// <summary>
/// Reads the Lua call sites of a failed coroutine, innermost first, as (chunk, line) pairs.
/// <para>
/// MoonSharp 2.0 keeps each frame's return address after an exception, but not its source location
/// (<see cref="MoonSharp.Interpreter.Debugging.WatchItem.Location"/> is null for every frame but the
/// innermost). The instruction just before a return address is the CALL that entered the frame, and
/// its source reference is the call site, so the sites are recovered by looking that instruction up in
/// the script's byte code. MoonSharp exposes no public lookup for it, so the byte-code list is read by
/// reflection once; if a MoonSharp update renames those internals the sites come back empty and errors
/// simply report no line (the error-location tests catch that).
/// </para>
/// </summary>
internal static class ScriptCallSites
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? ByteCodeField = typeof(Script).GetField("m_ByteCode", Any);
    private static readonly FieldInfo? CodeField = ByteCodeField?.FieldType.GetField("Code", Any);
    private static readonly FieldInfo? SourceRefField = CodeField?.FieldType.IsGenericType == true
        ? CodeField.FieldType.GetGenericArguments()[0].GetField("SourceCodeRef", Any)
        : null;

    /// <summary>True for an imported module's chunk name (<c>lib/&lt;name&gt;.lua</c>).</summary>
    public static bool IsModuleChunk(string? chunk) => chunk is not null && chunk.StartsWith("lib/", StringComparison.Ordinal);

    public static IReadOnlyList<(string Chunk, int Line)> Read(Script script, Exception ex, Coroutine coroutine)
    {
        var sites = new List<(string, int)>();
        try
        {
            IList<WatchItem>? frames = ex is InterpreterException { CallStack: { Count: > 0 } stack } ? stack : coroutine.GetStackTrace(0);
            if (frames is null || ByteCodeField?.GetValue(script) is not { } byteCode || CodeField?.GetValue(byteCode) is not IList code || SourceRefField is null)
            {
                return sites;
            }

            if (ex is InterpreterException && frames.Count > 0 && frames[0].Location is { IsClrLocation: false } here)
            {
                Add(script, here, sites); // a Lua error() / runtime fault: where it happened
            }

            foreach (WatchItem frame in frames)
            {
                int call = frame.RetAddress - 1;
                if (call >= 0 && call < code.Count && SourceRefField.GetValue(code[call]) is SourceRef site)
                {
                    Add(script, site, sites);
                }
            }
        }
        catch (Exception)
        {
            // Location lookup is best-effort: the error itself is reported regardless.
        }

        return sites;
    }

    /// <summary>
    /// The current position of a suspended coroutine, innermost first: the line it is executing, then the line of
    /// each call on its Lua stack. Best-effort (empty when the stack cannot be read).
    /// </summary>
    public static IReadOnlyList<(string Chunk, int Line)> Current(Script script, Coroutine coroutine)
    {
        var sites = new List<(string, int)>();
        try
        {
            IList<WatchItem>? frames = coroutine.GetStackTrace(0);
            if (frames is null || frames.Count == 0)
            {
                return sites;
            }

            if (frames[0].Location is { IsClrLocation: false } here)
            {
                Add(script, here, sites);
            }

            if (ByteCodeField?.GetValue(script) is not { } byteCode || CodeField?.GetValue(byteCode) is not IList code || SourceRefField is null)
            {
                return sites;
            }

            foreach (WatchItem frame in frames)
            {
                int call = frame.RetAddress - 1;
                if (call >= 0 && call < code.Count && SourceRefField.GetValue(code[call]) is SourceRef site)
                {
                    Add(script, site, sites);
                }
            }
        }
        catch (Exception)
        {
            // Location lookup is best-effort: the abort itself is reported regardless.
        }

        return sites;
    }

    private static void Add(Script script, SourceRef site, List<(string, int)> sites)
    {
        if (site.IsClrLocation || site.FromLine <= 0)
        {
            return;
        }

        string name = script.GetSourceCode(site.SourceIdx)?.Name ?? string.Empty;
        sites.Add((name, site.FromLine));
    }
}

/// <summary>
/// Aborts the whole run (budget / time limit) from any callback depth. Deliberately NOT a MoonSharp
/// <see cref="InterpreterException"/>, so a Lua <c>pcall</c> cannot swallow it.
/// </summary>
internal sealed class ScriptAbortException : Exception
{
    public ScriptAbortException(ScriptDiagnostic diagnostic)
        : base(diagnostic.Message) => Diagnostic = diagnostic;

    public ScriptDiagnostic Diagnostic { get; }
}
