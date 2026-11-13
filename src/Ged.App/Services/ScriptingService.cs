using System;
using System.Threading;
using Ged.Core.Scripting;
using Ged.Scripting;

namespace Ged.App.Services;

/// <summary>
/// Supplies the live <see cref="ScriptServices"/> for the open document. Implemented by
/// <see cref="MainWindow"/>, which knows the document, brush editor, mounted asset library,
/// install directory and dependency-scan options. Returns null when no level is open.
/// </summary>
internal interface IScriptEnvironment
{
    ScriptServices? BuildServices(IScriptProgressSink progress, IScriptConfirmation confirmation);

    /// <summary>Called after a run applied changes, so the shell can rebuild the scene + refresh UI.</summary>
    void OnScriptApplied();

    /// <summary>Called after every run / console line (success or failure), after <see cref="OnScriptApplied"/>,
    /// so the shell can carry out work a script queued for after it finishes (e.g. a playtest launch).</summary>
    void OnScriptFinished(ScriptRunResult result)
    {
    }
}

/// <summary>Per-caller overrides for <see cref="ScriptingService.Run(string, string, bool, bool, CancellationToken, ScriptRunExtras?)"/>.</summary>
internal sealed class ScriptRunExtras
{
    /// <summary>The undo entry label (null = "Script: &lt;chunk&gt;").</summary>
    public string? UndoLabel { get; init; }

    /// <summary>Execution limits (null = the interactive editor limits).</summary>
    public ScriptExecutionLimits? Limits { get; init; }

    /// <summary>Receives every log line of the run (in addition to the Script Log), on the run's thread.</summary>
    public Action<ScriptLogEntry>? LogTap { get; init; }

    /// <summary>
    /// Wraps the editor's script operations for this run (e.g. the command-line path allow-list on
    /// <c>ops.save</c> / <c>ops.package</c>); receives the run's context and the editor's backend.
    /// </summary>
    public Func<ScriptContext, IScriptOperations, IScriptOperations>? WrapOperations { get; init; }

    /// <summary>
    /// Provenance tags applied to everything the run creates (command-line runs; see
    /// <see cref="ScriptServices.AutoTags"/>). Null for Script Editor / Console runs, which never auto-tag.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? AutoTags { get; init; }
}

/// <summary>
/// The App's scripting facade over <see cref="ScriptRunner"/> (MoonSharp). Runs one-shot scripts
/// and REPL lines synchronously on the UI thread — which keeps the document single-threaded and
/// safe — while the instruction budget + timeout (a background timer cancels the token) still abort
/// a runaway loop. All script output is raised via <see cref="LogWritten"/> so the Script Console
/// (the dedicated Script Log surface) sees it. Heavy operations report to the shared progress
/// overlay via a lazily-begun operation card.
/// </summary>
internal sealed class ScriptingService
{
    private readonly IScriptEnvironment _env;
    private readonly OperationProgressService _progress;
    private readonly ScriptRunner _runner = new(new MoonSharpHost());

    private ScriptReplSession? _repl;
    private object? _replDocKey;
    private LazyProgressSink? _replProgress;

    public ScriptingService(IScriptEnvironment env, OperationProgressService progress)
    {
        _env = env;
        _progress = progress;
    }

    /// <summary>Raised for every script log line (info/warn/error/output), on the UI thread.</summary>
    public event Action<ScriptLogEntry>? LogWritten;

    public string EngineName => _runner.EngineName;

    public string EngineVersion => _runner.EngineVersion;

    /// <summary>Runs a full script once as a single undo step (or a preview when <paramref name="dryRun"/>).</summary>
    public ScriptRunResult Run(string source, string chunkName, bool dryRun, bool allowDestructive, CancellationToken cancellation) =>
        Run(source, chunkName, dryRun, allowDestructive, cancellation, extras: null);

    /// <summary>
    /// Runs a full script once as a single undo step, with the per-caller overrides in
    /// <paramref name="extras"/> (undo label, limits, a log tap) — used by command-line runs sent to
    /// the open editor. Output still reaches the Script Log exactly as for an editor-started run.
    /// </summary>
    public ScriptRunResult Run(
        string source, string chunkName, bool dryRun, bool allowDestructive, CancellationToken cancellation, ScriptRunExtras? extras)
    {
        Action<ScriptLogEntry>? tap = extras?.LogTap;
        var log = new ScriptLog(new RelaySink(e =>
        {
            tap?.Invoke(e);
            Raise(e);
        }));
        using var progress = new LazyProgressSink(_progress, $"Script: {chunkName}");
        ScriptServices? services = _env.BuildServices(progress, Confirmation(allowDestructive || dryRun));
        if (services is null)
        {
            return Fail(chunkName, dryRun);
        }

        if (extras?.WrapOperations is not null || extras?.AutoTags is { Count: > 0 })
        {
            services = WithOverrides(services, extras!.WrapOperations, extras.AutoTags);
        }

        ScriptMetadata meta = ScriptMetadata.Parse(source);
        var options = new ScriptRunOptions
        {
            ChunkName = chunkName,
            DryRun = dryRun,
            AllowDestructive = allowDestructive || meta.AllowDestructive,
            DeclaredApiVersion = meta.ApiVersion,
            Seed = 0,
            Limits = extras?.Limits ?? ScriptExecutionLimits.Interactive,
            UndoLabel = extras?.UndoLabel,
        };

        ScriptRunResult result = _runner.Run(services, source, options, log, cancellation);
        if (result.Committed)
        {
            _env.OnScriptApplied();
        }

        _env.OnScriptFinished(result);
        return result;
    }

    /// <summary>Evaluates one REPL line in a session whose globals persist across lines.</summary>
    public ScriptRunResult EvalConsole(string line, CancellationToken cancellation)
    {
        ScriptReplSession? repl = EnsureRepl();
        if (repl is null)
        {
            return Fail("console", false);
        }

        ScriptRunResult result = repl.Eval(line, cancellation);
        if (result.Committed)
        {
            _env.OnScriptApplied();
        }

        _env.OnScriptFinished(result);
        return result;
    }

    /// <summary>Drops the REPL session (e.g. on document close) so it rebinds to the new document.</summary>
    public void ResetConsole()
    {
        _repl?.Dispose();
        _repl = null;
        _replDocKey = null;
        _replProgress?.Dispose();
        _replProgress = null;
    }

    private ScriptReplSession? EnsureRepl()
    {
        _replProgress ??= new LazyProgressSink(_progress, "Script console");
        ScriptServices? services = _env.BuildServices(_replProgress, new AllowAllConfirmation());
        if (services is null)
        {
            return null;
        }

        object key = services.Document;
        if (_repl is not null && ReferenceEquals(_replDocKey, key))
        {
            return _repl;
        }

        _repl?.Dispose();
        var log = new ScriptLog(new RelaySink(Raise));
        _repl = _runner.CreateRepl(services, new ScriptRunOptions { ChunkName = "console", Limits = ScriptExecutionLimits.Repl }, log);
        _replDocKey = key;
        return _repl;
    }

    private ScriptRunResult Fail(string chunk, bool dryRun)
    {
        var diag = new ScriptDiagnostic(ScriptErrorKind.Api, "Open a level before running a script.", chunk);
        Raise(new ScriptLogEntry(ScriptLogLevel.Error, diag.ToDisplayString(), DateTime.Now));
        return ScriptRunResult.Failed(diag, dryRun);
    }

    private void Raise(ScriptLogEntry entry)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            LogWritten?.Invoke(entry);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LogWritten?.Invoke(entry));
        }
    }

    private static IScriptConfirmation Confirmation(bool allow) =>
        allow ? new AllowAllConfirmation() : new DenyAllConfirmation();

    /// <summary>
    /// A copy of <paramref name="s"/> whose operation backend is wrapped by <paramref name="wrap"/> (when
    /// given) and which auto-tags with <paramref name="autoTags"/> (when given).
    /// </summary>
    private static ScriptServices WithOverrides(
        ScriptServices s,
        Func<ScriptContext, IScriptOperations, IScriptOperations>? wrap,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? autoTags)
    {
        IScriptOperations? fixedOps = s.Operations;
        Func<ScriptContext, IScriptOperations>? factory = s.OperationsFactory;
        return s with
        {
            AutoTags = autoTags ?? s.AutoTags,
            Operations = wrap is null ? fixedOps : null,
            OperationsFactory = wrap is null
                ? factory
                : ctx => wrap(ctx, fixedOps ?? factory?.Invoke(ctx) ?? new CoreScriptOperations(ctx)),
        };
    }

    private sealed class RelaySink : IScriptLogSink
    {
        private readonly Action<ScriptLogEntry> _onWrite;

        public RelaySink(Action<ScriptLogEntry> onWrite) => _onWrite = onWrite;

        public void Write(ScriptLogEntry entry) => _onWrite(entry);
    }

    /// <summary>Adapts <see cref="IScriptProgressSink"/> to the overlay, beginning a card only on first report.</summary>
    private sealed class LazyProgressSink : IScriptProgressSink, IDisposable
    {
        private readonly OperationProgressService _service;
        private readonly string _name;
        private OperationProgress? _op;

        public LazyProgressSink(OperationProgressService service, string name)
        {
            _service = service;
            _name = name;
        }

        public void Report(ScriptProgress progress)
        {
            _op ??= _service.Begin(_name);
            _op.Report(progress.Stage, progress.Current, progress.Total);
        }

        public void Dispose()
        {
            _op?.Dispose();
            _op = null;
        }
    }
}
