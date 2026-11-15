using System;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Packaging;

namespace Ged.Core.Scripting;

/// <summary>
/// The live editor services a script run binds against. The App passes its shared instances
/// (so a script that changes selection or geometry is reflected in the viewport); tests and the
/// headless command-line tools pass fresh ones. Only <see cref="Document"/> is required; the rest are
/// created on demand so unit tests can construct a context from a bare document. A record, so a host
/// derives a variant with <c>services with { … }</c> and never drops a property it did not mention.
/// </summary>
public sealed record ScriptServices
{
    public required EditorDocument Document { get; init; }

    /// <summary>The App's live brush editor (shared selection). A fresh one is created when null.</summary>
    public BrushEditor? Brushes { get; init; }

    /// <summary>The link-graph service. A fresh one is created when null.</summary>
    public LinkService? Links { get; init; }

    /// <summary>The group service. A fresh one is created when null.</summary>
    public GroupService? Groups { get; init; }

    /// <summary>The App's live mover service (keyframe / mover field edits). A fresh one is created when null.</summary>
    public MoverService? Movers { get; init; }

    /// <summary>The App's live prefab-instance service (lineage records). A fresh one is created when null.</summary>
    public PrefabInstanceService? Prefabs { get; init; }

    /// <summary>The App's live object-metadata service (light cookies, provenance tags). A fresh one is created when null.</summary>
    public GedObjectMetadataService? Metadata { get; init; }

    /// <summary>
    /// The prefab library root <c>prefab.list</c> / <c>prefab.place</c> read from; every
    /// prefab file access is confined to it. Null ⇒ <see cref="Ged.Core.AppPaths.DefaultPrefabsDirectory"/>.
    /// </summary>
    public string? PrefabsDirectory { get; init; }

    /// <summary>
    /// The user module folder <c>ged.import(name)</c> reads <c>&lt;name&gt;.lua</c> from, after the
    /// modules shipped with Glacier (which always win). Null ⇒ <c>&lt;scripts&gt;/lib</c>
    /// (<see cref="ScriptModuleResolver.DefaultModuleDirectory"/>).
    /// </summary>
    public string? ModuleDirectory { get; init; }

    /// <summary>
    /// The modules a run carries from its script's own folder, by name (collected before the run by
    /// <see cref="ScriptModuleResolver.CollectLocal"/>, e.g. by <c>glacier-cli run &lt;file&gt;</c> and the MCP
    /// <c>run_lua</c> file argument). <c>ged.import(name)</c> resolves them after the shipped modules and before
    /// <see cref="ModuleDirectory"/>. Null (the editor's own runs) = none.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? LocalModules { get; init; }

    /// <summary>
    /// The detail module library folder (<c>library/modules</c>) the <c>modules</c> global reads. Null ⇒
    /// <see cref="Ged.Core.Modules.ModuleLibraryLocator"/>'s search (<c>GED_MODULE_LIBRARY</c>, then the folder beside
    /// Glacier or in the source checkout).
    /// </summary>
    public string? ModuleLibraryDirectory { get; init; }

    /// <summary>
    /// Provenance auto-tagging: when non-empty, a successful non-dry run tags every UID it
    /// created (and that still exists) with these key/value pairs, merged UNDER the run's own tags
    /// (a tag the script set wins on a key collision), in the run's single undo step. Null/empty ⇒
    /// off (the default; editor-internal runs never auto-tag). The command-line tools supply e.g.
    /// <c>ai.script</c> and <c>ai.session</c>.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? AutoTags { get; init; }

    /// <summary>The mounted asset VFS for texture/asset lookup + package dependency resolution.</summary>
    public AssetVfs? Assets { get; init; }

    /// <summary>
    /// The game's object class tables for <c>catalog</c> and class validation in
    /// <c>level.place{}</c>. Null ⇒ loaded lazily from <see cref="Assets"/> when a VFS is mounted,
    /// else <see cref="GameCatalogs.Empty"/>.
    /// </summary>
    public Tables.GameCatalogs? Catalogs { get; init; }

    /// <summary>
    /// The host's compiled-geometry state of <see cref="Document"/> at the start of the run (dirty / preview), for the
    /// freshness the <c>spatial</c> and <c>design</c> results report. Null = not reported (the state reads "unknown"
    /// until the script builds).
    /// </summary>
    public Func<Editor.LevelBuildState>? BuildState { get; init; }

    /// <summary>The operation backend (build/light/holecheck/save/package/playtest).
    /// When null, <see cref="OperationsFactory"/> is used, else a <see cref="CoreScriptOperations"/> is created.</summary>
    public IScriptOperations? Operations { get; init; }

    /// <summary>
    /// Creates the operation backend for a run once its <see cref="ScriptContext"/> exists — the way a
    /// host supplies its own backend that still needs the run's log, cancellation and destructive-op
    /// gate (the editor wraps <see cref="CoreScriptOperations"/> to refresh its viewport and to launch
    /// playtests). Ignored when <see cref="Operations"/> is set.
    /// </summary>
    public Func<ScriptContext, IScriptOperations>? OperationsFactory { get; init; }

    /// <summary>
    /// The user's build choices (build method, lightmap method) that script builds compile with,
    /// through the same <see cref="CompileOptionsFactory"/> the editor uses. Null means
    /// <see cref="BuildSettings.EditorDefaults"/> — what a fresh editor install builds with.
    /// </summary>
    public BuildSettings? BuildSettings { get; init; }

    /// <summary>
    /// An existing texture-traits cache to reuse for build face flags (the editor shares its own).
    /// When null and <see cref="Assets"/> is mounted, the run creates one.
    /// </summary>
    public TextureTraitsCache? TextureTraits { get; init; }

    /// <summary>Destructive-op confirmation gate. Defaults to deny-all when null.</summary>
    public IScriptConfirmation? Confirmation { get; init; }

    /// <summary>Where operation progress is reported (the App's overlay). Optional.</summary>
    public IScriptProgressSink? Progress { get; init; }

    /// <summary>
    /// The read policy for <c>level.import_mesh</c>: given a fully resolved model path, returns the path to
    /// read or throws a <see cref="ScriptApiException"/> when the host does not allow it (the hosts check
    /// their path allow-list: the level's folder, <c>user_maps</c>, the scripts library, plus files the user
    /// named with <c>--allow-import</c> on a headless command line). Null ⇒ file import is not available.
    /// </summary>
    public Func<string, string>? ImportFileAccess { get; init; }

    /// <summary>
    /// The write policy for <c>level.import_mesh{as = "mesh"}</c>'s .v3m: given the target path, returns it
    /// or throws a <see cref="ScriptApiException"/> (automation hosts check the save allow-list). Null ⇒ no
    /// check beyond the fixed <c>&lt;install&gt;/user_maps/meshes</c> target and the destructive-op gate.
    /// </summary>
    public Func<string, string>? MeshWriteAccess { get; init; }

    /// <summary>The RF install directory (for package output defaults + playtest). Optional.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Builds dependency-scan options (catalogs) for packaging. Optional.</summary>
    public Func<DependencyScanOptions>? ScanOptionsFactory { get; init; }

    /// <summary>
    /// The editor's configured per-orientation default brush textures (Settings ▸ Texture
    /// preferences), applied by <c>level.place_box</c> exactly like the Draw Brush tool: each is
    /// resolved through the shared <see cref="DefaultBrushTexture"/> guard (a dead/blank name
    /// falls back to the stock rock default) so a scripted brush never renders the white
    /// missing-texture fallback. Null (headless/tests) means "use the stock default".
    /// </summary>
    public string? DefaultFloorTexture { get; init; }

    /// <summary>See <see cref="DefaultFloorTexture"/>.</summary>
    public string? DefaultWallTexture { get; init; }

    /// <summary>See <see cref="DefaultFloorTexture"/>.</summary>
    public string? DefaultCeilingTexture { get; init; }
}
