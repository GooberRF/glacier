using System;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Packaging;

namespace Ged.Core.Scripting;

/// <summary>
/// The live editor services a script run binds against. The App passes its shared instances
/// (so a script that changes selection or geometry is reflected in the viewport); tests and the
/// (future) headless CLI pass fresh ones. Only <see cref="Document"/> is required; the rest are
/// created on demand so unit tests can construct a context from a bare document.
/// </summary>
public sealed class ScriptServices
{
    public required EditorDocument Document { get; init; }

    /// <summary>The App's live brush editor (shared selection). A fresh one is created when null.</summary>
    public BrushEditor? Brushes { get; init; }

    /// <summary>The link-graph service. A fresh one is created when null.</summary>
    public LinkService? Links { get; init; }

    /// <summary>The group service. A fresh one is created when null.</summary>
    public GroupService? Groups { get; init; }

    /// <summary>The mounted asset VFS for texture/asset lookup + package dependency resolution.</summary>
    public AssetVfs? Assets { get; init; }

    /// <summary>The operation backend (build/light/holecheck/save/package/playtest).
    /// A <see cref="CoreScriptOperations"/> is created when null.</summary>
    public IScriptOperations? Operations { get; init; }

    /// <summary>Destructive-op confirmation gate. Defaults to deny-all when null.</summary>
    public IScriptConfirmation? Confirmation { get; init; }

    /// <summary>Where operation progress is reported (the App's overlay). Optional.</summary>
    public IScriptProgressSink? Progress { get; init; }

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
