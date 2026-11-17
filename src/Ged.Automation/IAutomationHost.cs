using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Scripting;

namespace Ged.Automation;

/// <summary>A script run requested through automation.</summary>
public sealed class AutomationScriptRun
{
    /// <summary>The Lua source text.</summary>
    public required string Source { get; init; }

    /// <summary>The script's name (chunk name in diagnostics; the undo label is "CLI: &lt;name&gt;").</summary>
    public required string Name { get; init; }

    /// <summary>Roll back after running (report what would change).</summary>
    public bool DryRun { get; init; }

    /// <summary>Pre-authorize the script's destructive operations (delete, save, package).</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Execution limits.</summary>
    public ScriptExecutionLimits Limits { get; init; } = ScriptExecutionLimits.Default;

    /// <summary>
    /// Provenance tags written on everything the run creates (<see cref="ScriptServices.AutoTags"/>;
    /// a tag the script sets itself wins). Null = no auto-tagging (<c>--no-tag</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? AutoTags { get; init; }

    /// <summary>
    /// The undo-label prefix: <see cref="AutomationCommands.CliUndoPrefix"/> for the command line,
    /// <see cref="AutomationCommands.AiUndoPrefix"/> for a request that came from an AI agent (MCP).
    /// </summary>
    public string LabelPrefix { get; init; } = AutomationCommands.CliUndoPrefix;

    /// <summary>
    /// The modules the run carries from its script's folder, by name (<see cref="ScriptServices.LocalModules"/>): sources,
    /// never paths, so a host (the editor too) reads nothing from disk for them. Null = none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Modules { get; init; }

    /// <summary>An optional short description appended to the undo label ("AI: blockout.lua — add east wing").</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Where the run's level will be saved (a resolved, already-authorised path), or null. When the level has no file
    /// yet (a new level built with <c>--out</c>), that file's folder serves as the level's folder during the run: relative
    /// script paths resolve against it and scripts may read and write there.
    /// </summary>
    public string? SaveTarget { get; init; }

    /// <summary>The undo entry label.</summary>
    public string UndoLabel => LabelPrefix + " " + Name + (string.IsNullOrEmpty(Label) ? string.Empty : " — " + Label);
}

/// <summary>The outcome of a host save.</summary>
/// <param name="Path">The written path.</param>
/// <param name="SaveAs">True for a Save As.</param>
/// <param name="Stamped">True when the timestamp/date were updated.</param>
/// <param name="Notice">The save advisory.</param>
public readonly record struct HostSaveOutcome(string Path, bool SaveAs, bool Stamped, LevelSaveNotice Notice);

/// <summary>
/// What an automation command runs against: a headless session (<see cref="HeadlessAutomationHost"/>)
/// or the running editor (the editor's bridge host). The command handlers in
/// <see cref="AutomationCommands"/> are host-agnostic; everything that must differ — where the
/// document lives, how a script run / build / save is surfaced (Script Log, viewport, undo) — is a
/// host member. Hosts guarantee GUI-identical results: builds go through
/// <see cref="CompileOptionsFactory"/> with the user's <see cref="BuildSettings"/>, saves through
/// <see cref="LevelSavePolicy"/>.
/// </summary>
public interface IAutomationHost
{
    /// <summary>"headless" or "editor".</summary>
    string Mode { get; }

    /// <summary>The open document, or null.</summary>
    EditorDocument? Document { get; }

    /// <summary>The mounted game asset library, or null when no install is mounted.</summary>
    AssetVfs? Assets { get; }

    /// <summary>The mounted RF install directory, or null.</summary>
    string? RfInstallDir { get; }

    /// <summary>The build choices builds compile with (build method + effective lightmap method).</summary>
    BuildSettings BuildSettings { get; }

    /// <summary>The compiled-geometry state of the open document (dirty / preview / lighting-dirty).</summary>
    LevelBuildState BuildState { get; }

    /// <summary>Where commands may read and write files.</summary>
    PathAllowList PathPolicy { get; }

    /// <summary>Dependency-scan options (catalogs, dialogue file) for the linter.</summary>
    DependencyScanOptions CreateScanOptions();

    /// <summary>
    /// Runs <paramref name="work"/> where the document may be touched (the editor's UI thread;
    /// inline for a headless session). Every command executes through this.
    /// </summary>
    Task<T> RunOnDocumentThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellation);

    /// <summary>Opens a level file as the current document (replacing any open one).</summary>
    Task OpenAsync(string path, CancellationToken cancellation);

    /// <summary>Creates a new level from the File ▸ New template as the current document.</summary>
    Task NewAsync(CancellationToken cancellation);

    /// <summary>Runs a script against the open document as one undo step; log lines go to <paramref name="capture"/> too.</summary>
    ScriptRunResult RunScript(AutomationScriptRun run, Action<ScriptLogEntry> capture, CancellationToken cancellation);

    /// <summary>
    /// Compiles (sealed or preview, optionally lit), applies the result to the document, and returns it.
    /// The compiler's stage progress is forwarded to <paramref name="progress"/> (from the compiling
    /// thread); a canceled build applies nothing.
    /// </summary>
    Task<CompiledLevel> BuildAsync(bool sealedBuild, bool bakeLighting, bool shadows, CancellationToken cancellation, IProgress<AutomationProgress>? progress = null);

    /// <summary>Compiles a SEALED build without applying it and returns the leak-edge midpoints (progress as <see cref="BuildAsync"/>).</summary>
    Task<IReadOnlyList<Vec3>> CheckHolesAsync(CancellationToken cancellation, IProgress<AutomationProgress>? progress = null);

    /// <summary>
    /// Saves with the editor's save semantics (seal guard, timestamp rule, v306, sidecar). The path is
    /// already resolved and allow-listed by the caller.
    /// </summary>
    Task<HostSaveOutcome> SaveAsync(string path, bool saveAs, CancellationToken cancellation);

    /// <summary>A one-line activity note (the editor log / status bar; ignored headless).</summary>
    void ReportActivity(string message);

    /// <summary>
    /// The labels of the latest undo steps, newest first: at most <paramref name="max"/>, fewer when the
    /// history is shorter, empty when there is nothing to undo.
    /// </summary>
    IReadOnlyList<string> RecentUndoLabels(int max);

    /// <summary>Undoes the latest <paramref name="steps"/> undo steps exactly like the editor's Undo.</summary>
    Task UndoAsync(int steps, CancellationToken cancellation);

    /// <summary>Re-applies the next <paramref name="steps"/> redo steps exactly like the editor's Redo.</summary>
    Task RedoAsync(int steps, CancellationToken cancellation);

    /// <summary>
    /// The open document's brush editor (the one the host's own script runs use), or null when no level
    /// is open. Read-only commands (<c>inspect</c>, <c>query</c>, <c>checkpoint</c>) read brushes through
    /// it; creating a second brush editor would re-bind the document's undo batching.
    /// </summary>
    Ged.Core.Editing.BrushEditor? Brushes { get; }
}

/// <summary>Reads undo history labels (shared by the hosts).</summary>
public static class UndoHistory
{
    /// <summary>The labels of the latest steps of <paramref name="stack"/>, newest first (at most <paramref name="max"/>).</summary>
    public static IReadOnlyList<string> Recent(UndoStack stack, int max)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var labels = new List<string>();
        for (UndoNode? n = stack.Current; n is not null && n != stack.Root && labels.Count < max; n = n.Parent)
        {
            labels.Add(n.Description);
        }

        return labels;
    }

    /// <summary>The labels of the next redo steps of <paramref name="stack"/>, in redo order (at most <paramref name="max"/>).</summary>
    public static IReadOnlyList<string> RecentRedo(UndoStack stack, int max)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var labels = new List<string>();
        for (UndoNode? n = stack.Current.RedoChild; n is not null && labels.Count < max; n = n.RedoChild)
        {
            labels.Add(n.Description);
        }

        return labels;
    }
}
