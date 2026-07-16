using System;
using System.Threading;
using Ged.Core.Editor;

namespace Ged.Core.Scripting;

/// <summary>
/// Orchestrates the load-bearing transaction wrapper (plan §5.2): a whole script run is wrapped in
/// one <see cref="UndoStack.Transaction"/>. On success outside dry-run the transaction commits —
/// <b>one</b> undo entry for the entire run (<c>Ctrl+Z</c> reverts it). On any error, or in dry-run,
/// the transaction rolls back — the document is restored exactly, so a thrown script never leaves a
/// half-applied document and dry-run doubles as the crash-safe preview path (§5.7).
/// </summary>
public sealed class ScriptRunner
{
    private readonly IScriptHost _host;

    public ScriptRunner(IScriptHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    public string EngineName => _host.EngineName;

    public string EngineVersion => _host.EngineVersion;

    /// <summary>Runs <paramref name="source"/> to completion as one undoable step.</summary>
    public ScriptRunResult Run(
        ScriptServices services,
        string source,
        ScriptRunOptions options,
        ScriptLog log,
        CancellationToken external = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        if (options.DeclaredApiVersion is int requested && requested > ScriptApiV1.Version)
        {
            return ScriptRunResult.Failed(new ScriptDiagnostic(
                ScriptErrorKind.Api,
                $"This script requires script API v{requested}, but this build provides v{ScriptApiV1.Version}.",
                options.ChunkName,
                hint: "Update Glacier or lower the script's --@api requirement."), options.DryRun);
        }

        var ctx = new ScriptContext(services, options, log);
        EditorDocument doc = services.Document;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external);
        if (options.Limits.Timeout > TimeSpan.Zero)
        {
            linked.CancelAfter(options.Limits.Timeout);
        }

        ctx.Cancellation = linked.Token;

        int beforeNodes = doc.Undo.NodeCount;
        UndoStack.Transaction tx = doc.Undo.BeginTransaction($"Script: {options.ChunkName}");
        ScriptExecution exec;
        try
        {
            using IScriptSession session = _host.CreateSession(ctx);
            exec = session.Execute(source, options.ChunkName, options.Limits, linked.Token);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            return ScriptRunResult.Failed(FromException(ex, options.ChunkName), options.DryRun);
        }

        if (!exec.Success)
        {
            tx.Rollback();
            ScriptDiagnostic error = exec.Error ?? new ScriptDiagnostic(ScriptErrorKind.Runtime, "Script failed.", options.ChunkName);
            log.Emit(ScriptLogLevel.Error, error.ToDisplayString());
            return ScriptRunResult.Failed(error, options.DryRun);
        }

        if (options.DryRun)
        {
            tx.Rollback();
            log.Info($"Dry-run complete: would change {ctx.Changes.Describe()} (nothing applied).");
            return ScriptRunResult.Ok(exec.ReturnValue, committed: false, undoNodesAdded: 0, wasDryRun: true);
        }

        tx.Commit();
        int added = Math.Max(0, doc.Undo.NodeCount - beforeNodes);
        if (ctx.Changes.Total > 0 || added > 0)
        {
            log.Info($"Applied: {ctx.Changes.Describe()} — 1 undo step.");
        }

        return ScriptRunResult.Ok(exec.ReturnValue, committed: added > 0, undoNodesAdded: added, wasDryRun: false);
    }

    /// <summary>Opens a persistent REPL session whose globals survive across evaluated lines; each
    /// <see cref="ScriptReplSession.Eval"/> is its own undo step (plan §6.1).</summary>
    public ScriptReplSession CreateRepl(ScriptServices services, ScriptRunOptions options, ScriptLog log)
    {
        var ctx = new ScriptContext(services, options, log);
        IScriptSession session = _host.CreateSession(ctx);
        return new ScriptReplSession(ctx, session, options, log);
    }

    internal static ScriptDiagnostic FromException(Exception ex, string chunk) => ex switch
    {
        ScriptApiException api => new ScriptDiagnostic(ScriptErrorKind.Api, api.Message, chunk, hint: api.Hint),
        OperationCanceledException => new ScriptDiagnostic(ScriptErrorKind.Aborted, "Script canceled.", chunk),
        _ => new ScriptDiagnostic(ScriptErrorKind.Runtime, ex.Message, chunk),
    };
}

/// <summary>
/// A persistent REPL session (plan §6.1). Globals persist across lines; each evaluated line runs
/// inside its own transaction so read-only queries add no undo entry and each mutation is exactly
/// one undo step with the visible "1 undo step" affordance.
/// </summary>
public sealed class ScriptReplSession : IDisposable
{
    private readonly ScriptContext _ctx;
    private readonly IScriptSession _session;
    private readonly ScriptRunOptions _options;
    private readonly ScriptLog _log;

    internal ScriptReplSession(ScriptContext ctx, IScriptSession session, ScriptRunOptions options, ScriptLog log)
    {
        _ctx = ctx;
        _session = session;
        _options = options;
        _log = log;
    }

    public ScriptLog Log => _log;

    /// <summary>Evaluates one REPL line as a single undo step (or none, for a pure query).</summary>
    public ScriptRunResult Eval(string line, CancellationToken cancellation = default)
    {
        EditorDocument doc = _ctx.Document;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (_options.Limits.Timeout > TimeSpan.Zero)
        {
            linked.CancelAfter(_options.Limits.Timeout);
        }

        _ctx.Cancellation = linked.Token;

        int beforeNodes = doc.Undo.NodeCount;
        UndoStack.Transaction tx = doc.Undo.BeginTransaction("Console");
        ScriptExecution exec;
        try
        {
            exec = _session.Execute(line, "console", _options.Limits, linked.Token);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            return ScriptRunResult.Failed(ScriptRunner.FromException(ex, "console"), false);
        }

        if (!exec.Success)
        {
            tx.Rollback();
            ScriptDiagnostic error = exec.Error ?? new ScriptDiagnostic(ScriptErrorKind.Runtime, "Error.", "console");
            _log.Emit(ScriptLogLevel.Error, error.ToDisplayString());
            return ScriptRunResult.Failed(error, false);
        }

        tx.Commit();
        int added = Math.Max(0, doc.Undo.NodeCount - beforeNodes);
        if (exec.ReturnValue is { Length: > 0 } rv)
        {
            _log.Output($"= {rv}");
        }

        return ScriptRunResult.Ok(exec.ReturnValue, committed: added > 0, undoNodesAdded: added, wasDryRun: false);
    }

    public void Dispose() => _session.Dispose();
}
