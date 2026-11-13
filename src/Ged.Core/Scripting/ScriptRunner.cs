using System;
using System.Collections.Generic;
using System.Threading;
using Ged.Core.Editor;

namespace Ged.Core.Scripting;

/// <summary>
/// Orchestrates the load-bearing transaction wrapper: a whole script run is wrapped in
/// one <see cref="UndoStack.Transaction"/>. On success outside dry-run the transaction commits —
/// <b>one</b> undo entry for the entire run (<c>Ctrl+Z</c> reverts it). On any error, or in dry-run,
/// the transaction rolls back — the document is restored exactly, so a thrown script never leaves a
/// half-applied document and dry-run doubles as the crash-safe preview path.
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
        if (ctx.AutoTagProblem() is { } tagProblem)
        {
            return ScriptRunResult.Failed(new ScriptDiagnostic(ScriptErrorKind.Api,
                $"The automatic provenance tags are invalid: {tagProblem}", options.ChunkName,
                hint: "Fix the host's automatic tags (or turn auto-tagging off)."), options.DryRun);
        }

        // The token carries only the caller's cancellation (Stop). The wall-clock timeout is enforced
        // by the engine host against the context's execution clock, which excludes host-operation time
        // (ops.build / ops.light / …), so a long build on a large level is never cut off by it.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external);
        ctx.Cancellation = linked.Token;
        ctx.StartExecutionClock();

        int beforeNodes = doc.Undo.NodeCount;
        var session0 = new SessionSnapshot(ctx);
        UndoStack.Transaction tx = doc.Undo.BeginTransaction(options.UndoLabel ?? $"Script: {options.ChunkName}");
        ScriptExecution exec;
        try
        {
            using IScriptSession session = _host.CreateSession(ctx);
            exec = session.Execute(source, options.ChunkName, options.Limits, linked.Token);
        }
        catch (Exception ex)
        {
            session0.RollBack(tx);
            return ScriptRunResult.Failed(FromException(ex, options.ChunkName), options.DryRun);
        }

        if (!exec.Success)
        {
            session0.RollBack(tx);
            ScriptDiagnostic error = exec.Error ?? new ScriptDiagnostic(ScriptErrorKind.Runtime, "Script failed.", options.ChunkName);
            log.Emit(ScriptLogLevel.Error, error.ToDisplayString());
            return ScriptRunResult.Failed(error, options.DryRun);
        }

        // Mover copies of members edited this run are refreshed, and provenance tags written, inside the
        // run's single undo step; created UIDs are read after both.
        IReadOnlyList<int> created;
        try
        {
            ctx.SyncStaleMoverCopies(runEditsOnly: true);
            if (options.DryRun)
            {
                session0.RollBack(tx);
                log.Info($"Dry-run complete: would change {ctx.Changes.Describe()} (nothing applied).");
                return ScriptRunResult.Ok(exec.ReturnValue, committed: false, undoNodesAdded: 0, wasDryRun: true)
                    .WithChanges(ctx.Changes.Counts)
                    .WithStats(ctx.FacadeCalls, ctx.ScriptElapsed);
            }

            ctx.FlushAutoTags();
            created = ctx.LiveCreatedUids();
        }
        catch (Exception ex)
        {
            session0.RollBack(tx);
            ScriptDiagnostic error = FromException(ex, options.ChunkName);
            log.Emit(ScriptLogLevel.Error, error.ToDisplayString());
            return ScriptRunResult.Failed(error, options.DryRun);
        }

        tx.Commit();
        int added = Math.Max(0, doc.Undo.NodeCount - beforeNodes);
        if (added > 0 && options.UndoLabel is { Length: > 0 } label)
        {
            // A one-command run commits that command's own node (e.g. "Create brush"); an explicit
            // label (command-line runs: "CLI: <script>") must name the step whatever it contains.
            doc.Undo.Current.Description = label;
        }

        if (ctx.Changes.Total > 0 || added > 0)
        {
            log.Info($"Applied: {ctx.Changes.Describe()} — 1 undo step.");
        }

        return ScriptRunResult.Ok(exec.ReturnValue, committed: added > 0, undoNodesAdded: added, wasDryRun: false)
            .WithChanges(ctx.Changes.Counts)
            .WithCreated(added > 0 ? created : Array.Empty<int>())
            .WithStats(ctx.FacadeCalls, ctx.ScriptElapsed);
    }

    /// <summary>Opens a persistent REPL session whose globals survive across evaluated lines; each
    /// <see cref="ScriptReplSession.Eval"/> is its own undo step.</summary>
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
/// A persistent REPL session. Globals persist across lines; each evaluated line runs
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
        // As in ScriptRunner.Run: the token is the caller's cancellation only; the per-line timeout
        // is measured on the execution clock (host operations excluded).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _ctx.Cancellation = linked.Token;
        _ctx.StartExecutionClock();
        _ctx.ResetRunTracking();

        int beforeNodes = doc.Undo.NodeCount;
        var session0 = new SessionSnapshot(_ctx);
        UndoStack.Transaction tx = doc.Undo.BeginTransaction("Console");
        ScriptExecution exec;
        try
        {
            exec = _session.Execute(line, "console", _options.Limits, linked.Token);
        }
        catch (Exception ex)
        {
            session0.RollBack(tx);
            return ScriptRunResult.Failed(ScriptRunner.FromException(ex, "console"), false);
        }

        if (!exec.Success)
        {
            session0.RollBack(tx);
            ScriptDiagnostic error = exec.Error ?? new ScriptDiagnostic(ScriptErrorKind.Runtime, "Error.", "console");
            _log.Emit(ScriptLogLevel.Error, error.ToDisplayString());
            return ScriptRunResult.Failed(error, false);
        }

        IReadOnlyList<int> created;
        try
        {
            if (_ctx.AutoTagProblem() is { } tagProblem)
            {
                throw new ScriptApiException($"The automatic provenance tags are invalid: {tagProblem}");
            }

            _ctx.SyncStaleMoverCopies(runEditsOnly: true);
            _ctx.FlushAutoTags();
            created = _ctx.LiveCreatedUids();
        }
        catch (Exception ex)
        {
            session0.RollBack(tx);
            ScriptDiagnostic error = ScriptRunner.FromException(ex, "console");
            _log.Emit(ScriptLogLevel.Error, error.ToDisplayString());
            return ScriptRunResult.Failed(error, false);
        }

        tx.Commit();
        int added = Math.Max(0, doc.Undo.NodeCount - beforeNodes);
        if (exec.ReturnValue is { Length: > 0 } rv)
        {
            _log.Output($"= {rv}");
        }

        return ScriptRunResult.Ok(exec.ReturnValue, committed: added > 0, undoNodesAdded: added, wasDryRun: false)
            .WithCreated(added > 0 ? created : Array.Empty<int>());
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// The editor state a script run can change outside the undo stack — session-hidden brushes, the
/// object and brush selections, and the UID allocator's watermark — captured before the run so a
/// rollback (an error, or a dry run) leaves no trace of the run at all: the level, what is hidden and
/// selected, and the UIDs the next run will be given are exactly as before.
/// </summary>
internal sealed class SessionSnapshot
{
    private readonly ScriptContext _ctx;
    private readonly int _uidWatermark;
    private readonly int[] _objectSelection;
    private readonly Editing.BrushEditor.SessionState _brushState;

    public SessionSnapshot(ScriptContext ctx)
    {
        _ctx = ctx;
        _uidWatermark = ctx.Document.UidWatermark;
        _objectSelection = ctx.Document.CaptureSelection();
        _brushState = ctx.Brushes.CaptureSessionState();
    }

    /// <summary>Rolls the run's transaction back, then restores the session state captured before it.</summary>
    public void RollBack(UndoStack.Transaction tx)
    {
        tx.Rollback();
        _ctx.Document.RestoreUidWatermark(_uidWatermark);
        _ctx.Document.RestoreSelection(_objectSelection);
        _ctx.Brushes.RestoreSessionState(_brushState);
    }
}
