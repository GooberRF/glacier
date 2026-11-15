using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Assets;

namespace Ged.Core.Scripting;

/// <summary>
/// The root of the host-neutral script facade. It projects the existing editor
/// handles — it does not clone the model — and exposes the fixed set of Lua globals
/// (<see cref="Globals"/>): <c>ged</c>, <c>level</c>, <c>selection</c>, <c>assets</c>,
/// <c>ops</c>, <c>lint</c>, <c>log</c>, <c>rng</c>, <c>catalog</c>. This class has no engine dependency, so the
/// entire API is unit-testable without a window and without MoonSharp.
/// </summary>
public sealed class ScriptContext
{
    public ScriptContext(ScriptServices services, ScriptRunOptions options, ScriptLog log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        Document = services.Document ?? throw new ArgumentException("ScriptServices.Document is required.", nameof(services));
        Services = services;
        Options = options;
        Log = log ?? new ScriptLog();
        Rng = new ScriptRng(options.Seed);

        Brushes = services.Brushes ?? new BrushEditor(Document);
        Links = services.Links ?? new LinkService(Document);
        Groups = services.Groups ?? new GroupService(Document);
        Movers = services.Movers ?? new MoverService(Document);
        Prefabs = services.Prefabs ?? new PrefabInstanceService(Document);
        Metadata = services.Metadata ?? new GedObjectMetadataService(Document);
        Assets = services.Assets;
        _catalogs = services.Catalogs;
        Confirmation = options.DryRun
            ? new DenyAllConfirmation()
            : (options.AllowDestructive ? new AllowAllConfirmation() : services.Confirmation ?? new DenyAllConfirmation());
        Operations = services.Operations
            ?? services.OperationsFactory?.Invoke(this)
            ?? new CoreScriptOperations(this);

        Ged = new ScriptGed(this);
        Level = new ScriptLevel(this);
        Selection = new ScriptSelection(this);
        AssetsApi = new ScriptAssets(this);
        Ops = new ScriptOps(this);
        Lint = new ScriptLint(this);
        Catalog = new ScriptCatalog(this);
        Prefab = new ScriptPrefabs(this);
        Modules = new ScriptModules(this);
        Geo = new ScriptGeo(this);
        Spatial = new ScriptSpatial(this);
        KitCore = new ScriptKitCore(this);
        Design = new ScriptDesign(this);
    }

    // ---- Lua-facing globals ---------------------------------------------------

    public ScriptGed Ged { get; }

    public ScriptLevel Level { get; }

    public ScriptSelection Selection { get; }

    public ScriptAssets AssetsApi { get; }

    public ScriptOps Ops { get; }

    public ScriptLint Lint { get; }

    public ScriptLog Log { get; }

    public ScriptRng Rng { get; }

    public ScriptCatalog Catalog { get; }

    public ScriptPrefabs Prefab { get; }

    public ScriptModules Modules { get; }

    public ScriptGeo Geo { get; }

    public ScriptSpatial Spatial { get; }

    /// <summary>The design kit's native engine (ged.import("kit_core"), for the shipped kit modules).</summary>
    internal ScriptKitCore KitCore { get; }

    public ScriptDesign Design { get; }

    // ---- Shared internal state (reachable because the facade lives in Ged.Core) ----

    internal EditorDocument Document { get; }

    internal BrushEditor Brushes { get; }

    internal LinkService Links { get; }

    internal GroupService Groups { get; }

    internal MoverService Movers { get; }

    internal PrefabInstanceService Prefabs { get; }

    internal GedObjectMetadataService Metadata { get; }

    internal AssetVfs? Assets { get; }

    private Tables.GameCatalogs? _catalogs;

    /// <summary>The game class tables: the host's instance, else loaded once from the mounted VFS, else empty.</summary>
    internal Tables.GameCatalogs Catalogs => _catalogs ??= Assets is { } vfs ? Tables.GameCatalogs.Load(vfs) : Tables.GameCatalogs.Empty;

    internal IScriptOperations Operations { get; }

    internal IScriptConfirmation Confirmation { get; }

    internal ScriptServices Services { get; }

    internal ScriptRunOptions Options { get; }

    /// <summary>
    /// The run's cancellation token (the Stop button / the caller's cancellation), honored by long
    /// operations. The wall-clock timeout is NOT folded into this token: it limits script execution
    /// time only (<see cref="ScriptElapsed"/>), so a long build/bake is never cut off by it.
    /// </summary>
    public System.Threading.CancellationToken Cancellation { get; internal set; } = System.Threading.CancellationToken.None;

    // ---- Execution clock (wall-clock limit measures script time, not host-operation time) ----

    private readonly System.Diagnostics.Stopwatch _runClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Diagnostics.Stopwatch _hostClock = new();
    private int _hostDepth;

    /// <summary>Restarts the execution clock for a new run / console line (called by the runner).</summary>
    internal void StartExecutionClock()
    {
        FacadeCalls = 0;
        _runClock.Restart();
        _hostClock.Reset();
        _hostDepth = 0;
    }

    /// <summary>
    /// Script execution time of the current run: wall-clock time since the run started MINUS the
    /// time spent inside host operations (<c>ops.build</c>, <c>ops.light</c>, hole checks, save,
    /// package, playtest). This is what <see cref="ScriptExecutionLimits.Timeout"/> limits.
    /// </summary>
    public TimeSpan ScriptElapsed => _runClock.Elapsed - _hostClock.Elapsed;

    /// <summary>
    /// Diagnostic: facade methods called by the current run (counted by the script engine at each
    /// Lua → C# method entry; property reads are not counted). Surfaced as <see cref="ScriptRunResult.FacadeCalls"/>.
    /// </summary>
    public int FacadeCalls { get; private set; }

    /// <summary>Counts one facade method call (called by the script engine; see <see cref="FacadeCalls"/>).</summary>
    public void CountFacadeCall() => FacadeCalls++;

    /// <summary>Total time the current run has spent inside host operations.</summary>
    public TimeSpan HostOperationElapsed => _hostClock.Elapsed;

    /// <summary>True when <paramref name="limits"/> has a wall-clock timeout and the script's own
    /// execution time (<see cref="ScriptElapsed"/>) has exceeded it.</summary>
    public bool IsOverTimeout(ScriptExecutionLimits limits) =>
        limits is not null && limits.Timeout > TimeSpan.Zero && ScriptElapsed > limits.Timeout;

    /// <summary>
    /// Marks a host operation (build, light, hole check, save, package, playtest): time until the
    /// returned scope is disposed does not count toward the script's wall-clock timeout. Nestable.
    /// Cancellation (<see cref="Cancellation"/>) still applies inside the operation.
    /// </summary>
    public IDisposable EnterHostOperation()
    {
        if (_hostDepth++ == 0)
        {
            _hostClock.Start();
        }

        return new HostOperationScope(this);
    }

    private void ExitHostOperation()
    {
        if (_hostDepth > 0 && --_hostDepth == 0)
        {
            _hostClock.Stop();
        }
    }

    private sealed class HostOperationScope : IDisposable
    {
        private ScriptContext? _ctx;

        public HostOperationScope(ScriptContext ctx) => _ctx = ctx;

        public void Dispose()
        {
            _ctx?.ExitHostOperation();
            _ctx = null;
        }
    }

    /// <summary>True while running in dry-run/preview (transaction always rolled back).</summary>
    public bool DryRun => Options.DryRun;

    /// <summary>The number of contributed lint findings + a running mutation summary for dry-run reporting.</summary>
    internal ScriptChangeSummary Changes { get; } = new();

    // ---- Provenance + edit tracking --------------------------------

    private readonly SortedSet<int> _createdUids = new();
    private readonly SortedSet<int> _brushEdits = new();

    /// <summary>
    /// Records a UID created by this run (object, brush, keyframe). Called by every creating member
    /// so provenance tagging and the run result can name exactly what the run added.
    /// </summary>
    internal void NoteCreated(int uid) => _createdUids.Add(uid);

    /// <summary>
    /// Records a brush mutated by this run — decides which movers the end-of-run refresh
    /// (<see cref="SyncStaleMoverCopies"/> with <c>runEditsOnly</c>) considers.
    /// </summary>
    internal void NoteBrushEdited(int uid) => _brushEdits.Add(uid);

    /// <summary>The UIDs created so far this run, ascending.</summary>
    internal IReadOnlyCollection<int> CreatedUids => _createdUids;

    /// <summary>The brush UIDs edited so far this run, ascending.</summary>
    internal IReadOnlyCollection<int> EditedBrushUids => _brushEdits;

    /// <summary>Clears the per-run provenance/edit tracking (a REPL line is its own run).</summary>
    internal void ResetRunTracking()
    {
        _createdUids.Clear();
        _brushEdits.Clear();
    }

    /// <summary>
    /// <c>ged.try</c>: runs <paramref name="body"/> inside a joined child of the run's undo transaction. On success
    /// its changes stay part of the run's single undo step. When it throws an exception that
    /// <paramref name="recoverable"/> accepts, every level change it made is rolled back — the document, the
    /// session state a rollback restores (hidden brushes, selections, the UID watermark) and this run's change
    /// counts and edit tracking are exactly as they were before it — and the exception is returned in
    /// <paramref name="failure"/>. Any other exception (an abort: time limit, instruction budget, Stop, callback
    /// nesting) propagates untouched and the whole run rolls back as usual.
    /// </summary>
    public bool RunRecoverable(Action body, Func<Exception, bool> recoverable, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(recoverable);
        var session = new SessionSnapshot(this);
        Dictionary<string, int> counts = new(Changes.Counts, StringComparer.Ordinal);
        int[] created = _createdUids.ToArray();
        int[] edited = _brushEdits.ToArray();
        Editor.UndoStack.Transaction tx = Document.Undo.BeginTransaction("ged.try");
        try
        {
            body();
        }
        catch (Exception ex) when (recoverable(ex))
        {
            session.RollBack(tx);
            Changes.Restore(counts);
            _createdUids.Clear();
            _createdUids.UnionWith(created);
            _brushEdits.Clear();
            _brushEdits.UnionWith(edited);
            failure = ex;
            return false;
        }

        tx.Commit();
        failure = null;
        return true;
    }

    /// <summary>True when <paramref name="uid"/> is a live brush or level object (keyframes included).</summary>
    internal bool UidExists(int uid) => Brushes.FindBrush(uid) is not null || Document.FindByUid(uid) is not null;

    /// <summary>The created UIDs that still exist (a UID created then deleted in the same run is dropped), ascending.</summary>
    internal IReadOnlyList<int> LiveCreatedUids() => _createdUids.Where(UidExists).ToList();

    /// <summary>Why <see cref="ScriptServices.AutoTags"/> is unusable, or null when it is valid (checked once per run).</summary>
    internal string? AutoTagProblem()
    {
        if (Services.AutoTags is not { Count: > 0 } tags)
        {
            return null;
        }

        if (tags.Count > ObjectTags.MaxTagsPerUid)
        {
            return $"At most {ObjectTags.MaxTagsPerUid} automatic tags are allowed (got {tags.Count}).";
        }

        foreach (KeyValuePair<string, string> kv in tags)
        {
            if ((ObjectTags.KeyProblem(kv.Key) ?? ObjectTags.ValueProblem(kv.Value)) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// The provenance hook: merges <see cref="ScriptServices.AutoTags"/> UNDER the existing
    /// tags of every created UID that still exists (a tag the script set wins on a key collision; the
    /// per-UID limit is never exceeded) and writes them all with one <see cref="GedObjectMetadataService.SetBlocks"/>
    /// — part of the run's undo step. A no-op when auto-tagging is off or in dry-run.
    /// </summary>
    internal void FlushAutoTags()
    {
        if (DryRun || Services.AutoTags is not { Count: > 0 } auto)
        {
            return;
        }

        IReadOnlyDictionary<int, Dictionary<string, string>> all = Metadata.AllTags();
        var writes = new List<(int, Model.GedMetadataType, byte[]?)>();
        foreach (int uid in LiveCreatedUids())
        {
            Dictionary<string, string> merged = all.TryGetValue(uid, out Dictionary<string, string>? own)
                ? new Dictionary<string, string>(own, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            int before = merged.Count;
            foreach (KeyValuePair<string, string> kv in auto.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!merged.ContainsKey(kv.Key) && merged.Count < ObjectTags.MaxTagsPerUid)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            if (merged.Count != before)
            {
                writes.Add((uid, Model.GedMetadataType.Tags, ObjectTags.Encode(merged)));
            }
        }

        Metadata.SetBlocks(writes, "Tag created objects");
    }

    /// <summary>
    /// Refreshes the movers-section copies of every mover whose member brush was edited after the
    /// copies were last taken (mover creation, <c>mover:sync()</c>, or the level being opened) — the
    /// build, the baked mover lighting and the game use those copies, not the brush the viewport
    /// shows. Uses the document's shared record (<see cref="EditorDocument.MoverCopies"/> via
    /// <see cref="MoverService.SyncStaleCopies"/>), so a member edited in the editor before the run
    /// counts too. Runs inside the run's transaction (part of its single undo step) and logs one info
    /// line per mover it refreshes. With <paramref name="runEditsOnly"/> (the end-of-run pass) only
    /// movers with a member this run edited are considered; the pass before <c>ops.build</c> /
    /// <c>light</c> / <c>save</c> / <c>package</c> considers every stale mover so what those write is
    /// current. Movers whose members were never touched keep their bytes exactly.
    /// </summary>
    internal void SyncStaleMoverCopies(bool runEditsOnly = false)
    {
        if (runEditsOnly && _brushEdits.Count == 0)
        {
            return;
        }

        var synced = Movers.SyncStaleCopies(runEditsOnly ? g => g.Brushes.Any(_brushEdits.Contains) : null);
        foreach ((Model.Group mover, IReadOnlyList<int> stale) in synced)
        {
            Changes.Record("synced");
            string which = stale.Count == 1 ? $"brush {stale[0]}" : $"brushes {string.Join(", ", stale)}";
            Log.Info($"mover '{mover.Name}': refreshed the mover copy of {which}, edited after it became a mover member.");
        }

        if (synced.Count > 0)
        {
            Brushes.NotifyExternalBrushChange();
        }
    }

    // ---- Module loading -------------------------------------------------

    private IScriptModuleHost? _moduleHost;

    /// <summary>
    /// The engine session that executes <c>ged.import</c> modules (see <see cref="AttachModuleHost"/>).
    /// Attaching a different host clears the module cache, since cached module values belong to the
    /// engine instance that produced them.
    /// </summary>
    internal IScriptModuleHost? ModuleHost => _moduleHost;

    /// <summary>
    /// Binds the engine session that executes <c>ged.import</c> modules for this context (called by
    /// the engine when it creates a session over the context).
    /// </summary>
    public void AttachModuleHost(IScriptModuleHost? host)
    {
        if (!ReferenceEquals(_moduleHost, host))
        {
            ModuleCache.Clear();
            ImportStack.Clear();
        }

        _moduleHost = host;
    }

    /// <summary>Imported modules by name (per session: a REPL keeps its cache for its lifetime).</summary>
    internal Dictionary<string, object?> ModuleCache { get; } = new(StringComparer.Ordinal);

    /// <summary>The modules currently executing their top level, outermost first (circular-import detection).</summary>
    internal List<string> ImportStack { get; } = new();

    // ---- Texture pixel sizes for pixels-per-metre UV mapping ----------

    private TextureSizeLookup? _textureSizes;

    /// <summary>
    /// The pixel size of texture <paramref name="name"/>, decoded through the mounted VFS exactly as
    /// the Texture-mode panel does (<see cref="TextureSizeLookup"/>, cached per run); 256×256
    /// (<see cref="UvOps.DefaultTextureSize"/>) when no VFS is mounted, the name is empty, or decoding fails.
    /// </summary>
    internal (int W, int H) TextureSize(string? name)
    {
        _textureSizes ??= new TextureSizeLookup(Assets);
        return _textureSizes.GetOrDefault(name, (UvOps.DefaultTextureSize, UvOps.DefaultTextureSize));
    }

    /// <summary>The grain of texture <paramref name="name"/> (<see cref="TextureGrains"/>), or none when it is unknown.</summary>
    internal TextureGrain TextureGrainOf(string? name)
    {
        _textureSizes ??= new TextureSizeLookup(Assets);
        return _textureSizes.GetGrain(name) ?? Editing.TextureGrain.None;
    }

    /// <summary>The pixel size of texture <paramref name="name"/>, or null when it is unknown (no VFS, unresolved or undecodable).</summary>
    internal (int W, int H)? TextureSizeOrNull(string? name)
    {
        _textureSizes ??= new TextureSizeLookup(Assets);
        return _textureSizes.Get(name);
    }

    /// <summary>
    /// The globals the engine binds by name. The host maps each to a Lua global;
    /// the API-surface snapshot test asserts these against <see cref="ScriptApiV1.Globals"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object> Globals => new Dictionary<string, object>
    {
        ["ged"] = Ged,
        ["level"] = Level,
        ["selection"] = Selection,
        ["assets"] = AssetsApi,
        ["ops"] = Ops,
        ["lint"] = Lint,
        ["log"] = Log,
        ["rng"] = Rng,
        ["catalog"] = Catalog,
        ["prefab"] = Prefab,
        ["modules"] = Modules,
        ["geo"] = Geo,
        ["spatial"] = Spatial,
        ["design"] = Design,
    };

    /// <summary>Throws when a destructive op is not permitted; used by delete/save/package/playtest
    /// (public so a host-supplied <see cref="IScriptOperations"/> applies the same gate).</summary>
    public void RequireDestructive(string what)
    {
        if (DryRun)
        {
            throw new ScriptApiException(
                $"'{what}' is a destructive operation and is disabled in dry-run mode.",
                "Run the script for real (not Dry-Run) to allow it.");
        }

        if (!Confirmation.Confirm("Script action", $"The script wants to {what}. Allow it?"))
        {
            throw new ScriptApiException(
                $"'{what}' was not confirmed.",
                "Add a --@allow-destructive header, or confirm when prompted.");
        }
    }
}

/// <summary>Accumulates a lightweight change summary for dry-run / result reporting.</summary>
internal sealed class ScriptChangeSummary
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Record(string kind, int count = 1)
    {
        _counts.TryGetValue(kind, out int cur);
        _counts[kind] = cur + count;
    }

    public IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>Replaces the counts with <paramref name="counts"/> (a <c>ged.try</c> rollback).</summary>
    public void Restore(IReadOnlyDictionary<string, int> counts)
    {
        _counts.Clear();
        foreach (KeyValuePair<string, int> kv in counts)
        {
            _counts[kv.Key] = kv.Value;
        }
    }

    public int Total
    {
        get
        {
            int sum = 0;
            foreach (int v in _counts.Values)
            {
                sum += v;
            }

            return sum;
        }
    }

    public string Describe()
    {
        if (_counts.Count == 0)
        {
            return "no changes";
        }

        var parts = new List<string>();
        foreach (KeyValuePair<string, int> kv in _counts)
        {
            parts.Add($"{kv.Value} {kv.Key}");
        }

        return string.Join(", ", parts);
    }
}
