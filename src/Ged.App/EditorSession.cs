using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;

namespace Ged.App;

/// <summary>Room-graph render scoping (stock View menu).</summary>
public enum RoomVisibility
{
    /// <summary>Render every room (default).</summary>
    All,

    /// <summary>Render only rooms reachable from the camera room via portals (like in-game).</summary>
    Portals,

    /// <summary>Render only the room the camera is in.</summary>
    CurrentRoom,
}

/// <summary>
/// The editor's non-UI state: the mounted asset VFS, the open <see cref="EditorDocument"/>
/// and its built <see cref="RenderScene"/>, the object catalogs, and the grid
/// parameters. Rebuilds the scene from the document after edits and turns picks
/// and selections into highlight line sets.
/// </summary>
public sealed class EditorSession : IDisposable
{
    private Geometry? _staticGeometry;
    private GeometrySnapIndex? _snapIndex;
    private Geometry? _snapIndexGeom;
    private int _snapIndexBrushCount = -1;
    private Ged.Core.Rooms.RoomGraph? _roomGraph;
    private RenderScene? _scene;
    private BrushPickRegistry? _brushRegistry;
    private EntityCatalog? _entities;
    private ClutterCatalog? _clutter;
    private ItemCatalog? _items;
    private GlareCatalog? _glares;

    public AssetVfs? Vfs { get; private set; }

    public string? RfInstallDir { get; private set; }

    public EditorDocument? Document { get; private set; }

    /// <summary>The brush-editing service for the open document (null before a document is open).</summary>
    public BrushEditor? BrushEditor { get; private set; }

    public string? LevelPath => Document?.Path;

    /// <summary>The loaded entity catalog (null when no install is mounted).</summary>
    public EntityCatalog? Entities => _entities;

    /// <summary>The loaded clutter catalog.</summary>
    public ClutterCatalog? Clutter => _clutter;

    /// <summary>The loaded item catalog.</summary>
    public ItemCatalog? Items => _items;

    /// <summary>The loaded glare (corona) catalog from effects.tbl, for object→mesh corona spawning.</summary>
    public GlareCatalog? Glares => _glares;

    /// <summary>
    /// Dependency-scan options for the packfile builder / linter: the loaded
    /// catalogs (for game-shipped clutter/entity/item mesh resolution) plus the
    /// companion dialogue text file next to the level, if it exists.
    /// </summary>
    public Ged.Core.Packaging.DependencyScanOptions BuildScanOptions()
    {
        var options = new Ged.Core.Packaging.DependencyScanOptions
        {
            ClutterCatalog = _clutter,
            EntityCatalog = _entities,
            ItemCatalog = _items,
        };

        if (LevelPath is { } path)
        {
            string txt = System.IO.Path.ChangeExtension(path, ".txt");
            if (System.IO.File.Exists(txt))
            {
                options.DialogueTextFile = System.IO.Path.GetFileName(txt);
            }
        }

        return options;
    }

    public float GridBrightness { get; set; } = 1f;

    public float GridSize { get; set; } = 1f;

    public bool ShowLinks { get; set; } = true;

    /// <summary>Draw directional-event facing arrows ("Show Event Arrows"). On by default.</summary>
    public bool ShowEventArrows { get; set; } = true;

    /// <summary>
    /// When true, brush solid faces are drawn even where compiled static geometry
    /// exists (off by default: stock RED shows wireframe brush overlays, and solid
    /// fill would z-fight the identical compiled faces).
    /// </summary>
    public bool ShowBrushSolids { get; set; }

    /// <summary>Global "Animate Emitters" toggle (particle + bolt live previews).</summary>
    public bool AnimateEmitters { get; set; }

    /// <summary>Draw objects as bounding boxes (stock "Show objects as Bounding Boxes").</summary>
    public bool ShowBoundingBoxes { get; set; }

    /// <summary>Draw nav-point connection lines + arrows (stock "Show Path Node Connections").</summary>
    public bool ShowPathNodes { get; set; }

    /// <summary>Draw gas regions as translucent coloured volumes.</summary>
    public bool ShowGasRegions { get; set; } = true;

    /// <summary>Show measurement/dimension annotations (B7 View toggle).</summary>
    public bool ShowAnnotations { get; set; } = true;

    private readonly Dictionary<string, (Ged.Rendering.Scene.InlineTexture Tex, float Aspect)> _labelCache = new();

    /// <summary>Global "Show all ranges": draw every range/region sphere regardless of selection (default off).</summary>
    public bool ShowAllRanges { get; set; }

    /// <summary>
    /// "Draw unmerged brushwork" (default off): when off, brush-overlay faces the last
    /// build clipped away are hidden and unpickable, leaving only the merged brushwork;
    /// when on the overlay draws every authored (unmerged) face.
    /// </summary>
    public bool DrawUnmergedBrushwork { get; set; }

    /// <summary>
    /// Per-brush-face survival from the last geometry build (brush UID → local face
    /// index → survived), or null when no build data exists / brushes changed since —
    /// the overlay then draws everything.
    /// </summary>
    public IReadOnlyDictionary<int, bool[]>? BrushFaceSurvival { get; set; }

    /// <summary>
    /// Consumption-site hook (wired to <see cref="GeometryBuildController.EnsureMergedBrushStash"/>):
    /// <see cref="BuildScene"/> calls this whenever it is about to draw the MERGED brush overlay
    /// (Draw unmerged brushwork OFF) but no build has ever populated the survival stash. It kicks
    /// a background build so the merged view materializes on its own — covering every entry path
    /// (opening a level then entering an edit mode, a mode switch, a scene rebuild) with a single
    /// guard, not just the option toggle. No-op when a build is already in flight / a stash exists.
    /// </summary>
    public Func<bool>? RequestMergedBrushStash { get; set; }

    /// <summary>
    /// Surviving compiled fragments per brush face from the last build (item 5), or null
    /// when no build data exists / brushes changed since. When set, the brush overlay
    /// draws a partially-clipped face as its surviving fragment(s) instead of the full
    /// authored polygon. Cleared alongside <see cref="BrushFaceSurvival"/>.
    /// </summary>
    public Ged.Rendering.Scene.BrushFragmentIndex? BrushFragments { get; set; }

    /// <summary>
    /// Brush UIDs edited since the fragment stash was built (item 5b): they render their
    /// authored polygons instead of the stale stash, while untouched brushes keep their
    /// fragment overlay. Cleared when a fresh build replaces the stash.
    /// </summary>
    public HashSet<int> StaleFragmentBrushUids { get; } = new();

    /// <summary>
    /// The pickable kinds the selection-filter chips currently allow (kept in sync by
    /// the shell). Drives the brush id-buffer granularity so the most-specific enabled
    /// brush kind wins a pick (Vertices ⇒ vertex, else Faces ⇒ face, else brush).
    /// Defaults to Objects.
    /// </summary>
    public SelectKinds ActiveSelectKinds { get; set; } = SelectKinds.Objects;

    private SelectionRouter? _selection;

    /// <summary>
    /// The single mandatory, mode/chip-gated entry point for EVERY selection mutation.
    /// It reads the current <see cref="Document"/> / <see cref="BrushEditor"/> and
    /// <see cref="ActiveSelectKinds"/> live, so it survives document swaps. The raw
    /// Document/BrushEditor select primitives are internal, making this the only way App
    /// code can mutate selection (compile-time enforcement against out-of-mode leaks).
    /// </summary>
    public SelectionRouter Selection
    {
        get
        {
            if (_selection is null)
            {
                _selection = new SelectionRouter(
                    () => Document, () => BrushEditor, () => ActiveSelectKinds, k => SelectionDropped?.Invoke(k));
                _selection.LockBlocked += () => SelectionLockBlocked?.Invoke();
            }

            return _selection;
        }
    }

    /// <summary>Raised when a selection request was dropped by the mode/chip gate (subtle status hint).</summary>
    public event Action<SelectKinds>? SelectionDropped;

    /// <summary>Raised when a click resolved only to a locked item (G: "Locked — unlock to select").</summary>
    public event Action? SelectionLockBlocked;

    /// <summary>Room-graph visibility mode (all / portal-culled / current room only).</summary>
    public RoomVisibility RoomMode { get; set; } = RoomVisibility.All;

    /// <summary>Keep sky rooms drawn as the backdrop even under portal culling ("Draw Sky").</summary>
    public bool DrawSky { get; set; }

    /// <summary>"Draw Decals" (perspective-only, default off): project decal textures onto geometry.</summary>
    public bool DrawDecals { get; set; }

    /// <summary>Stock View-menu portal-face draw mode (None / SeeThru / Opaque).</summary>
    public Ged.Rendering.Scene.PortalFaceDrawMode PortalFaces { get; set; } = Ged.Rendering.Scene.PortalFaceDrawMode.None;

    /// <summary>Portal-face tint (RGBA), from the portal-brush element colour.</summary>
    public uint? PortalFaceColor { get; set; }

    /// <summary>Emit object glyphs untinted so RED's original full-colour icons pass through.</summary>
    public bool UseOriginalIcons { get; set; }

    /// <summary>
    /// The configured Alpine Faction launcher path (or its directory). The stock Alpine object
    /// icons (Note / Corona / EAX / Event) live only in <c>alpinefaction.vpp</c> beside the
    /// launcher, which may sit outside the mounted RF install — so the atlas composition reads
    /// them from there too (item 3). Null/blank → Alpine icons fall back to the drawn glyphs.
    /// </summary>
    public string? AlpineLauncherPath { get; set; }

    /// <summary>
    /// Height/width aspect ratios of the resolved original icon bitmaps from the last
    /// <see cref="BuildIconAtlas"/> composition (empty for the GED-drawn set, which is
    /// square by design). Fed into every scene build so a non-square original — RED's
    /// 32×64 MP-respawn icon, the 64×32 keyframe diamond — renders its billboard at the
    /// true aspect instead of squished into the square atlas cell.
    /// </summary>
    public IReadOnlyDictionary<Ged.Rendering.Graphics.EditorIcon, float>? OriginalIconAspects { get; private set; }

    /// <summary>
    /// Builds the billboard icon-atlas image: GED's own drawn set, or — when
    /// <paramref name="useOriginal"/> and a VFS is mounted — RED's original icon
    /// bitmaps composited in (per-icon graceful fallback to the GED cell). The Alpine object
    /// icons are additionally sourced from <c>alpinefaction.vpp</c> beside the configured
    /// launcher when the main VFS lacks them (item 3). Also records
    /// <see cref="OriginalIconAspects"/> for the billboard aspect correction.
    /// </summary>
    public byte[] BuildIconAtlas(bool useOriginal)
    {
        if (!useOriginal || Vfs is null)
        {
            OriginalIconAspects = null;
            return Ged.Rendering.Graphics.IconAtlas.Build();
        }

        using Ged.Core.Assets.AlpineIconSource? alpine =
            Ged.Core.Assets.AlpineIconSource.BesideLauncher(AlpineLauncherPath);

        byte[] atlas = Ged.Rendering.Graphics.IconAtlas.Compose(icon =>
        {
            if (!Ged.Rendering.Graphics.IconAtlas.OriginalFileNames.TryGetValue(icon, out string? name))
            {
                return null;
            }

            try
            {
                // Prefer the mounted VFS (ui.vpp etc.); fall back to the alpinefaction.vpp
                // beside the launcher for the Alpine-only icons the install VFS lacks.
                return Vfs.LoadTexture(name)?.Primary ?? alpine?.Load(name);
            }
            catch (Exception)
            {
                return null;
            }
        }, out IReadOnlyDictionary<Ged.Rendering.Graphics.EditorIcon, float> aspects);

        OriginalIconAspects = aspects;
        return atlas;
    }

    /// <summary>The active camera world position (fed each rebuild for point-in-room + culling).</summary>
    public Vector3 CameraPosition { get; set; }

    /// <summary>The animation clock (seconds) driving the emitter/liquid previews.</summary>
    public float EmitterTime { get; set; }

    /// <summary>Preferences element colours (RGBA) piped into the renderer draw paths (null = default).</summary>
    public uint? LinkColor { get; set; }

    public uint? BoundingBoxColor { get; set; }

    public uint? PathNodeColor { get; set; }

    public uint? RegionColor { get; set; }

    /// <summary>Per-emitter opt-out UIDs (inspector per-emitter toggle).</summary>
    public HashSet<int> DisabledEmitterUids { get; } = new();

    /// <summary>The room the camera is currently in (−1 = unknown), for the status bar.</summary>
    public int CurrentRoomId { get; private set; } = -1;

    /// <summary>Raised after the mounted VFS changes (mount / remount / unmount) so consumers refresh (item 7).</summary>
    public event Action? VfsChanged;

    /// <summary>
    /// Mounts (or remounts) the VFS for an RF install and loads the object catalogs.
    /// <paramref name="force"/> remounts even when the directory is unchanged (a live
    /// remount after the user picks a new/corrected path). Raises <see cref="VfsChanged"/>.
    /// </summary>
    public void MountInstall(string rfInstallDir, bool force = false)
    {
        if (!force && string.Equals(rfInstallDir, RfInstallDir, StringComparison.OrdinalIgnoreCase) && Vfs is not null)
        {
            return;
        }

        Vfs?.Dispose();
        Vfs = GameMount.Mount(rfInstallDir);
        RfInstallDir = rfInstallDir;
        LoadCatalogs();
        VfsChanged?.Invoke();
    }

    /// <summary>Unmounts the VFS and drops the catalogs (e.g. an invalid path was chosen). Raises <see cref="VfsChanged"/>.</summary>
    public void Unmount()
    {
        Vfs?.Dispose();
        Vfs = null;
        RfInstallDir = null;
        _entities = null;
        _clutter = null;
        _items = null;
        _glares = null;
        VfsChanged?.Invoke();
    }

    /// <summary>Opens an RFL into a new document and builds its scene.</summary>
    public RenderScene OpenLevel(string path)
    {
        Document = EditorDocument.Open(path);
        BrushEditor = new BrushEditor(Document);
        ClearBuildOverlays();
        return BuildScene();
    }

    /// <summary>Creates an empty document with sensible defaults.</summary>
    public RenderScene NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C; // Alpine baseline (v300)
        rfl.Header.LevelName = "untitled.rfl";

        // A fresh level opens in RED with initialized level_properties + level_info (the
        // Level Properties dialog and the metadata / editor view configs). Emit both with
        // researched defaults so New → Save produces a level RED reads as authored, not one
        // missing its property/info sections.
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelProperties, Array.Empty<byte>())
        {
            Content = LevelPropertiesSection.CreateDefault(),
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelInfo, Array.Empty<byte>())
        {
            Content = LevelInfoSection.CreateDefault(DateTime.Now),
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        Document = new EditorDocument(rfl);
        BrushEditor = new BrushEditor(Document);
        ClearBuildOverlays();
        return BuildScene();
    }

    /// <summary>
    /// Drops the per-document build-overlay stash (clipped-face survival map + compiled
    /// fragment index) when the document changes. Both are keyed by brush UID and hold
    /// world-space geometry from the level that was built — letting them survive into a
    /// newly opened/created level made the overlay hide the wrong faces and draw the
    /// previous level's fragments as phantom faces wherever UIDs collided (which is
    /// always: every level starts its UIDs from the same range).
    /// </summary>
    private void ClearBuildOverlays()
    {
        BrushFaceSurvival = null;
        BrushFragments = null;
        StaleFragmentBrushUids.Clear();
    }

    /// <summary>Rebuilds the render scene from the current document + grid settings.</summary>
    /// <summary>UIDs of the currently selected decals (for their in-scene facing face), or null.</summary>
    private HashSet<int>? SelectedDecalUids()
    {
        if (Document is null)
        {
            return null;
        }

        HashSet<int>? set = null;
        foreach (LevelObject o in Document.Selection)
        {
            if (o.Model is Decal d)
            {
                (set ??= new HashSet<int>()).Add(d.Header.Uid);
            }
        }

        return set;
    }

    public RenderScene BuildScene()
    {
        if (Document is null)
        {
            return new RenderScene();
        }

        EditMode mode = BrushEditor?.Mode ?? EditMode.Object;
        bool editingBrushes = mode is EditMode.Brush or EditMode.Face or EditMode.Edge or EditMode.Vertex;

        _staticGeometry = FindStaticGeometry(Document.Rfl);
        _roomGraph = _staticGeometry is { Rooms.Count: > 0 } g ? Ged.Core.Rooms.RoomGraph.Build(g) : null;
        HashSet<int>? visibleRooms = ComputeVisibleRooms();

        var options = new SceneBuildOptions
        {
            Entities = _entities,
            Clutter = _clutter,
            Items = _items,
            IncludeLinks = ShowLinks,
            EventFacingArrows = ShowEventArrows,
            ShowBoundingBoxes = ShowBoundingBoxes,
            ShowPathNodeConnections = ShowPathNodes,
            LinkColor = LinkColor,
            BoundingBoxColor = BoundingBoxColor,
            PathNodeColor = PathNodeColor,
            RegionColor = RegionColor,
            PortalFaces = PortalFaces,
            PortalFaceColor = PortalFaceColor,
            UseOriginalIcons = UseOriginalIcons,
            OriginalIconAspects = OriginalIconAspects,
            // Show-sky editor aid: always highlighted while editing brushes; in object/group
            // modes it follows the "Draw Sky" view setting.
            ShowSkyFaceAid = editingBrushes || DrawSky,
            // Range/region spheres are off by default. The compiled scene only draws the
            // SELECTION-INDEPENDENT ones ("Show all ranges" or a light's "Always Show Range");
            // the range of the *selected* object is drawn by the lightweight selection overlay
            // (BuildSelectionRangeLines) so a selection change never re-emits the scene (item 8).
            ShowAllRanges = ShowAllRanges,
            SelectedUids = null,
            // The selected decal's facing face IS baked into the scene (a filled portal-style quad
            // can't ride the line-only selection overlay), so a decal selection change triggers a
            // rebuild (MainWindow.SelectionChanged). Kept separate from SelectedUids so it never
            // re-enables the scene-baked range spheres (those stay in the lightweight overlay).
            SelectedDecalUids = SelectedDecalUids(),
            DrawDecals = DrawDecals,
            VisibleRooms = editingBrushes ? null : visibleRooms,
            // While editing brushes, render the source brushes instead of the
            // (stale) compiled geometry so there is no z-fighting.
            IncludeStaticGeometry = !editingBrushes,
        };
        RenderScene scene = SceneBuilder.Build(Document.Rfl, options);

        // Live emitter / gas previews (particles as billboards, bolts as polylines).
        if (AnimateEmitters || ShowGasRegions)
        {
            EffectsBuilder.Append(Document.Rfl, new EffectsOptions
            {
                Time = EmitterTime,
                AnimateEmitters = AnimateEmitters,
                ShowGasRegions = ShowGasRegions,
                DisabledEmitterUids = DisabledEmitterUids.Count > 0 ? DisabledEmitterUids : null,
            }, scene);
        }

        // Cull hidden objects (outliner / stock hide semantics), accounting for
        // Isolate Selection (B6): while isolated only the isolation set renders.
        var hidden = new HashSet<int>(Document.Objects.Where(Document.IsEffectivelyHidden).Select(o => o.Uid));
        if (hidden.Count > 0)
        {
            scene.Billboards.RemoveAll(b => IsHidden(b.PickId, hidden));
            scene.Meshes.RemoveAll(m => IsHidden(m.PickId, hidden));
        }

        // Brushes render in every mode (wireframe edges + solid preview).
        _brushRegistry = null;
        if (BrushEditor is { Brushes.Count: > 0 } be)
        {
            // The most-specific enabled brush kind wins a pick (multi-kind picking via
            // the selection-filter chips): Vertices ⇒ vertex ids, else Faces ⇒ face ids,
            // else whole-brush ids.
            BrushPickGranularity gran =
                (ActiveSelectKinds & SelectKinds.Vertices) != 0 ? BrushPickGranularity.Vertex :
                (ActiveSelectKinds & SelectKinds.Faces) != 0 ? BrushPickGranularity.Face :
                BrushPickGranularity.Brush;
            // Wireframe overlay always; solid fill only when the compiled geometry
            // is hidden (brush-edit / live preview) or the user opts in — otherwise
            // brush solids z-fight the identical compiled faces (stock parity: wire only).
            bool solidFill = editingBrushes || ShowBrushSolids;
            // "Draw unmerged brushwork" OFF (default): faces the last build clipped
            // away are hidden and unpickable; ON restores the draw-everything overlay.
            // The merged (OFF) overlay can only clip once a build has populated the stash.
            // If it never has (a freshly opened level entered in an edit mode, no toggle and
            // no edit), request one now from the single consumption site — otherwise the
            // overlay draws every authored face and OFF looks identical to ON until an edit
            // finally builds the stash. The build refreshes the scene when it lands.
            if (!DrawUnmergedBrushwork && BrushFaceSurvival is null)
            {
                RequestMergedBrushStash?.Invoke();
            }

            IReadOnlyDictionary<int, bool[]>? survival = DrawUnmergedBrushwork ? null : BrushFaceSurvival;
            Ged.Rendering.Scene.BrushFragmentIndex? fragments = DrawUnmergedBrushwork ? null : BrushFragments;
            IReadOnlySet<int>? stale = DrawUnmergedBrushwork || StaleFragmentBrushUids.Count == 0
                ? null : StaleFragmentBrushUids;
            // Isolate Selection (B6) + Layers-panel hide (item 9): cull isolated-out /
            // hidden brushes.
            bool anyHidden = be.HiddenBrushes.Count > 0;
            IReadOnlyList<Brush> brushesToDraw = Document.IsIsolated || anyHidden
                ? be.Brushes.Where(b => Document.IsVisibleUnderIsolation(b.Uid) && !be.IsBrushHidden(b.Uid)).ToList()
                : be.Brushes;
            // Item (b) — brush selection is SELECTION-INDEPENDENT in the compiled scene: the
            // selected brush's highlight is drawn purely by the lightweight overlay
            // (BuildBrushSelectionLines, rebuilt every selection change), never baked into the
            // scene tint. Baking it in (passing be.SelectedBrushes) left a stale yellow tint —
            // selecting brush A then B kept A visually selected until the next full RebuildScene
            // ("until an operation runs"), because the tiered refresh (e72152c) no longer rebuilds
            // the scene on a selection change. Keeping the scene selection-free preserves that perf
            // win AND removes the staleness (same tier split item 8 applied to object ranges).
            _brushRegistry = BrushEmitter.Append(
                scene, brushesToDraw, gran, selectedBrushes: null, solidFill,
                survivingFaces: survival, survivingFragments: fragments, staleFragmentBrushes: stale,
                portalFaces: PortalFaces, portalColor: PortalFaceColor,
                skyFaceAid: editingBrushes || DrawSky);
            ExpandBoundsToBrushes(scene, brushesToDraw);
        }

        if (ShowAnnotations && Document.Annotations.Count > 0)
        {
            AppendAnnotations(scene, Document.Annotations);
        }

        AppendGrid(scene);

        _scene = scene;
        return scene;
    }

    /// <summary>
    /// Appends dimension annotations (B7): a line, endpoint ticks and a CPU-rasterized
    /// distance-label billboard per annotation. Label bitmaps are cached per string and
    /// supplied to the GPU as inline textures (no VFS lookup).
    /// </summary>
    private void AppendAnnotations(RenderScene scene, IReadOnlyList<Annotation> annotations)
    {
        uint lineColor = Palette.Rgba(90, 220, 255);
        uint tickColor = Palette.Rgba(255, 255, 255);
        foreach (Annotation a in annotations)
        {
            var pa = new Vector3(a.A.X, a.A.Y, a.A.Z);
            var pb = new Vector3(a.B.X, a.B.Y, a.B.Z);
            scene.Lines.Add(new LineSegment(pa, pb, lineColor));

            Vector3 dir = pb - pa;
            float len = dir.Length();
            if (len > 1e-4f)
            {
                dir /= len;
                Vector3 up = MathF.Abs(dir.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
                Vector3 t1 = Vector3.Normalize(Vector3.Cross(dir, up));
                Vector3 t2 = Vector3.Cross(dir, t1);
                float tl = MathF.Max(0.1f, len * 0.04f);
                foreach (Vector3 end in new[] { pa, pb })
                {
                    scene.Lines.Add(new LineSegment(end - (t1 * tl), end + (t1 * tl), tickColor));
                    scene.Lines.Add(new LineSegment(end - (t2 * tl), end + (t2 * tl), tickColor));
                }
            }

            (Ged.Rendering.Scene.InlineTexture tex, float aspect) = LabelTexture(a.EffectiveLabel);
            string key = "$anno:" + a.EffectiveLabel;
            scene.InlineTextures[key] = tex;
            Vector3 mid = (pa + pb) * 0.5f;
            scene.Billboards.Add(new Billboard(
                BillboardKind.Vertex, mid + new Vector3(0, 0.15f, 0), 0.22f,
                Palette.Rgba(255, 255, 255), default, TextureName: key, Aspect: aspect));
        }
    }

    /// <summary>
    /// Adds a CPU-rasterized text label billboard to the scene at a world position (the
    /// transform-drag Δ/∠/% readout — item: transform indicators). Same LabelBitmap →
    /// InlineTexture path as the measurement annotations, cached per string.
    /// </summary>
    public void AppendOverlayLabel(RenderScene scene, string text, Vector3 position, float size = 0.28f)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        (Ged.Rendering.Scene.InlineTexture tex, float aspect) = LabelTexture(text);
        string key = "$xform:" + text;
        scene.InlineTextures[key] = tex;
        // Item 3: the drag-indicator LABEL rides the depth-disabled on-top channel (OnTop) like the
        // gizmo/indicator lines, so the distance / degree / scale % readout is never hidden behind
        // geometry between the camera and the pivot.
        scene.Billboards.Add(new Billboard(
            BillboardKind.Vertex, position, size,
            Palette.Rgba(255, 255, 255), default, TextureName: key, Aspect: aspect, OnTop: true));
    }

    private (Ged.Rendering.Scene.InlineTexture Tex, float Aspect) LabelTexture(string text)
    {
        if (_labelCache.TryGetValue(text, out (Ged.Rendering.Scene.InlineTexture, float) cached))
        {
            return cached;
        }

        (int w, int h, byte[] rgba) = LabelBitmap.Render(text, scale: 2, pad: 2);
        float aspect = h > 0 ? w / (float)h : 1f;
        var result = (new Ged.Rendering.Scene.InlineTexture(w, h, rgba), aspect);
        _labelCache[text] = result;
        return result;
    }

    /// <summary>
    /// Computes the visible-room set for the current <see cref="RoomMode"/> from the
    /// compiled geometry's room graph and the camera position, updating
    /// <see cref="CurrentRoomId"/>. Null = draw every room (mode All / no geometry /
    /// camera not in any room).
    /// </summary>
    private HashSet<int>? ComputeVisibleRooms()
    {
        if (_staticGeometry is not { Rooms.Count: > 0 } geo || _roomGraph is not { } graph)
        {
            CurrentRoomId = -1;
            return null;
        }

        var cam = new Vec3(CameraPosition.X, CameraPosition.Y, CameraPosition.Z);
        int room = graph.RoomAt(cam);
        CurrentRoomId = room >= 0 && room < geo.Rooms.Count ? geo.Rooms[room].Id : -1;

        if (RoomMode == RoomVisibility.All || room < 0)
        {
            return null;
        }

        HashSet<int> visible = RoomMode == RoomVisibility.CurrentRoom
            ? new HashSet<int> { room }
            : graph.Reachable(room);

        // Draw Sky: the sky room is the backdrop — keep it visible under culling.
        if (DrawSky)
        {
            for (int i = 0; i < geo.Rooms.Count; i++)
            {
                if (geo.Rooms[i].IsSkyroom != 0)
                {
                    visible.Add(i);
                }
            }
        }

        return visible;
    }

    /// <summary>The RFL room id the camera is in (−1 = unknown), for the live status bar.</summary>
    public int RoomIdAt(Vector3 pos)
    {
        if (_staticGeometry is not { Rooms.Count: > 0 } geo || _roomGraph is not { } graph)
        {
            return -1;
        }

        int r = graph.RoomAt(new Vec3(pos.X, pos.Y, pos.Z));
        return r >= 0 && r < geo.Rooms.Count ? geo.Rooms[r].Id : -1;
    }

    /// <summary>Fog for the world shader from the level's fog colour + far clip, or off when disabled.</summary>
    public Ged.Rendering.FogSettings GetFog(bool enabled, float farClip)
    {
        if (!enabled || Document is null)
        {
            return Ged.Rendering.FogSettings.Off;
        }

        foreach (RflSection s in Document.Rfl.Sections)
        {
            if (s.Content is LevelPropertiesSection lp)
            {
                RfColor c = lp.FogColor;
                var color = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
                float far = lp.FogFarPlane > 1f ? lp.FogFarPlane : farClip;
                float near = lp.FogNearPlane > 0f && lp.FogNearPlane < far ? lp.FogNearPlane : far * 0.1f;
                return new Ged.Rendering.FogSettings(true, color, near, far);
            }
        }

        return Ged.Rendering.FogSettings.Off;
    }

    private static void ExpandBoundsToBrushes(RenderScene scene, IReadOnlyList<Brush> brushes)
    {
        var min = new Vector3(scene.Bounds.P1.X, scene.Bounds.P1.Y, scene.Bounds.P1.Z);
        var max = new Vector3(scene.Bounds.P2.X, scene.Bounds.P2.Y, scene.Bounds.P2.Z);
        bool any = scene.Batches.Count > 0 || scene.Billboards.Count > 0;
        foreach (Brush b in brushes)
        {
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                Vec3 w = b.Position.Add(b.Rotation.Transform(v));
                var p = new Vector3(w.X, w.Y, w.Z);
                min = any ? Vector3.Min(min, p) : p;
                max = any ? Vector3.Max(max, p) : p;
                any = true;
            }
        }

        scene.Bounds = new Aabb(new Vec3(min.X, min.Y, min.Z), new Vec3(max.X, max.Y, max.Z));
        if (scene.SuggestedCameraTarget == Vector3.Zero && scene.SuggestedCameraPosition == Vector3.Zero)
        {
            Vector3 c = (min + max) * 0.5f;
            float r = MathF.Max((max - min).Length() * 0.5f, 2f);
            scene.SuggestedCameraTarget = c;
            scene.SuggestedCameraPosition = c + new Vector3(r, r * 0.6f, r);
        }
    }

    /// <summary>
    /// The snap-to-geometry index (B1) for the current level: compiled + brush vertices,
    /// edge midpoints and face planes. Cached and rebuilt when the compiled geometry or
    /// brush count changes (build / edit-invalidate); null when there is nothing to snap to.
    /// </summary>
    public GeometrySnapIndex? GetOrBuildSnapIndex()
    {
        int brushCount = BrushEditor?.Brushes.Count ?? 0;
        if (_snapIndex is not null && ReferenceEquals(_snapIndexGeom, _staticGeometry) && _snapIndexBrushCount == brushCount)
        {
            return _snapIndex;
        }

        _snapIndex = BuildSnapIndex();
        _snapIndexGeom = _staticGeometry;
        _snapIndexBrushCount = brushCount;
        return _snapIndex;
    }

    private GeometrySnapIndex? BuildSnapIndex()
    {
        var verts = new List<Vec3>();
        var edges = new List<(int, int)>();
        var faces = new List<SnapFace>();

        void Harvest(Geometry g, System.Func<Vec3, Vec3> toWorld, System.Func<Vec3, Vec3> normalToWorld)
        {
            int baseIdx = verts.Count;
            foreach (Vec3 v in g.Vertices)
            {
                verts.Add(toWorld(v));
            }

            foreach (Face f in g.Faces)
            {
                if (f.Vertices.Count < 3)
                {
                    continue;
                }

                var poly = new Vec3[f.Vertices.Count];
                for (int i = 0; i < f.Vertices.Count; i++)
                {
                    int vi = f.Vertices[i].Index;
                    poly[i] = verts[baseIdx + vi];
                    edges.Add((baseIdx + vi, baseIdx + f.Vertices[(i + 1) % f.Vertices.Count].Index));
                }

                Vec3 n = normalToWorld(f.Plane.Normal).Normalized();
                if (n.LengthSquared() > 1e-8f)
                {
                    faces.Add(new SnapFace(poly, n, -n.Dot(poly[0])));
                }
            }
        }

        if (_staticGeometry is { Vertices.Count: > 0 } sg)
        {
            Harvest(sg, v => v, n => n);
        }

        if (BrushEditor is { } be)
        {
            foreach (Brush b in be.Brushes)
            {
                Harvest(b.Geometry, v => b.Position.Add(b.Rotation.Transform(v)), n => b.Rotation.Transform(n));
            }
        }

        return verts.Count == 0 && faces.Count == 0
            ? null
            : GeometrySnapIndex.Build(verts, edges, faces, cellSize: 2f);
    }

    private static bool IsHidden(PickId id, HashSet<int> hidden) =>
        id.Kind is PickKind.Object or PickKind.Mesh && hidden.Contains(id.Index);

    private void AppendGrid(RenderScene scene)
    {
        var min = new Vector3(scene.Bounds.P1.X, scene.Bounds.P1.Y, scene.Bounds.P1.Z);
        var max = new Vector3(scene.Bounds.P2.X, scene.Bounds.P2.Y, scene.Bounds.P2.Z);
        Vector3 center = (min + max) * 0.5f;
        float extent = MathF.Max((max - min).Length(), 8f);
        float spacing = GridSize > 0.01f ? GridSize * MathF.Max(1f, MathF.Round(extent / 40f / GridSize)) : MathF.Max(1f, extent / 40f);
        GridBuilder.Append(scene, center, extent, spacing, GridBrightness, min.Y);
    }

    /// <summary>Builds a highlight line set for a pick result, or empty when nothing is picked.</summary>
    public IReadOnlyList<LineSegment> BuildSelectionLines(PickId pick)
    {
        var lines = new List<LineSegment>();
        if (pick.IsNone || _scene is null)
        {
            return lines;
        }

        uint yellow = Palette.Rgba(255, 240, 60, 255);

        if (pick.Kind == PickKind.Face && _staticGeometry is not null &&
            pick.Index >= 0 && pick.Index < _staticGeometry.Faces.Count)
        {
            Face f = _staticGeometry.Faces[pick.Index];
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                Vec3 a = VertexAt(_staticGeometry, f.Vertices[i].Index);
                Vec3 b = VertexAt(_staticGeometry, f.Vertices[(i + 1) % f.Vertices.Count].Index);
                lines.Add(new LineSegment(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), yellow));
            }

            return lines;
        }

        Vector3? pos = FindPickedPosition(pick);
        if (pos is Vector3 p)
        {
            AddBox(lines, p, 0.6f, yellow);
        }

        return lines;
    }

    /// <summary>Builds highlight lines for a set of level objects (outliner/property selection).</summary>
    public IReadOnlyList<LineSegment> BuildSelectionLines(IEnumerable<LevelObject> objects)
    {
        var lines = new List<LineSegment>();
        uint yellow = Palette.Rgba(255, 240, 60, 255);
        foreach (LevelObject o in objects)
        {
            Vec3 p = o.Position;
            AddBox(lines, new Vector3(p.X, p.Y, p.Z), 0.6f, yellow);
        }

        return lines;
    }

    /// <summary>
    /// The range/region wireframes for the SELECTED objects (a light's range sphere, a
    /// geo-region outline). Item 8: these live in the lightweight selection overlay rather
    /// than baked into the compiled scene, so a selection change updates only the overlay
    /// (~0.1&#160;ms) instead of forcing a full scene re-emission + GPU re-upload (~18&#160;ms on a
    /// big level). Skips anything already drawn selection-independently by "Show all ranges"
    /// or a light's stock "Always Show Range" flag (so nothing double-draws).
    /// </summary>
    public IReadOnlyList<LineSegment> BuildSelectionRangeLines(IEnumerable<LevelObject> objects)
    {
        var lines = new List<LineSegment>();
        if (ShowAllRanges)
        {
            return lines; // the scene already draws every range
        }

        foreach (LevelObject o in objects)
        {
            switch (o.Model)
            {
                case Light l when l.Range > 0.01f && !l.AlwaysShowRange:
                    AddSphere(lines, new Vector3(l.Position.X, l.Position.Y, l.Position.Z), l.Range,
                        Palette.Rgba(l.Color.R, l.Color.G, l.Color.B, 160));
                    break;
                case GeoRegion gr:
                    AddRegionOutline(lines, gr.Position, gr.Rotation, gr.Radius, gr.Width, gr.Height, gr.Depth,
                        RegionColor ?? Palette.Rgba(120, 255, 120, 200));
                    break;
                case Trigger tr:
                    AddRegionOutline(lines, tr.Position, tr.Rotation,
                        tr.Shape == Trigger.ShapeSphere ? tr.SphereRadius : null,
                        tr.BoxWidth, tr.BoxHeight, tr.BoxDepth, Palette.Rgba(255, 170, 90, 200));
                    break;
                case GasRegion gas:
                    AddRegionOutline(lines, gas.Header.Position, gas.Header.Rotation,
                        gas.Shape == GasRegionsSection.ShapeSphere ? gas.Radius : null,
                        gas.Width, gas.Height, gas.Depth, RegionColor ?? Palette.Rgba(150, 220, 120, 200));
                    break;
                case PushRegion push:
                    AddRegionOutline(lines, push.Header.Position, push.Header.Rotation,
                        push.Shape == PushRegionsSection.ShapeSphere ? push.Radius : null,
                        push.Extents?.X, push.Extents?.Y, push.Extents?.Z,
                        RegionColor ?? Palette.Rgba(120, 200, 255, 200));
                    break;
                case ClimbingRegion climb:
                    AddRegionOutline(lines, climb.Header.Position, climb.Header.Rotation, null,
                        climb.Extents.X, climb.Extents.Y, climb.Extents.Z,
                        RegionColor ?? Palette.Rgba(120, 210, 180, 200));
                    break;
            }
        }

        return lines;
    }

    /// <summary>
    /// Link lines for every link that touches the current selection (source OR destination is
    /// selected), drawn in the selection-highlight colour with a destination arrowhead. Lives
    /// in the lightweight selection overlay so it shows whenever objects are selected — even
    /// when the global "Show Links" toggle is off — and updates on a selection change without
    /// a scene rebuild. When "Show Links" is on the baked scene already draws every link; these
    /// simply highlight the selected object's links (incoming and outgoing) on top. Endpoints
    /// resolve through the document by UID, so keyframe and mover endpoints work too.
    /// </summary>
    public IReadOnlyList<LineSegment> BuildSelectionLinkLines(IEnumerable<LevelObject> objects)
    {
        var lines = new List<LineSegment>();
        if (Document is null)
        {
            return lines;
        }

        var selected = new HashSet<int>();
        foreach (LevelObject o in objects)
        {
            selected.Add(o.Uid);
        }

        if (selected.Count == 0)
        {
            return lines;
        }

        uint yellow = Palette.Rgba(255, 240, 60, 255);
        foreach ((int from, int to) in Ged.Core.Editing.DocumentLinks.AllEdges(Document))
        {
            if (!selected.Contains(from) && !selected.Contains(to))
            {
                continue;
            }

            if (Document.FindByUid(from) is { } a && Document.FindByUid(to) is { } b)
            {
                var pa = new Vector3(a.Position.X, a.Position.Y, a.Position.Z);
                var pb = new Vector3(b.Position.X, b.Position.Y, b.Position.Z);
                lines.Add(new LineSegment(pa, pb, yellow));
                OverlayBuilder.AddArrowHead(lines, pa, pb, yellow);
            }
        }

        return lines;
    }

    /// <summary>Returns a level object's world position for framing the camera.</summary>
    public Vector3 PositionOf(LevelObject o)
    {
        Vec3 p = o.Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>
    /// Guards a configured default brush texture at CREATE time: if a VFS is mounted and
    /// the name does not resolve there (a stale/dead persisted default, a typo in
    /// Settings), the stock rock default is used instead — a new brush must never be
    /// stamped with a texture the renderer can only draw as the white fallback. With no
    /// VFS mounted the name is kept as-is (nothing can be verified, and everything
    /// renders untextured anyway). This is the create-side backstop for the settings
    /// migration of the historical dead default (see SettingsStore).
    /// </summary>
    public string ResolveDefaultBrushTexture(string configured) => ResolveDefaultBrushTexture(Vfs, configured);

    /// <summary>Pure core of <see cref="ResolveDefaultBrushTexture(string)"/> (VFS injectable for tests).
    /// Delegates to the shared Core guard so the scripting API's <c>level.place_box</c> and the
    /// interactive create paths resolve defaults through the SAME code (white-brush fix).</summary>
    public static string ResolveDefaultBrushTexture(AssetVfs? vfs, string configured) =>
        Ged.Core.Editing.DefaultBrushTexture.Resolve(vfs, configured);

    private Vector3? FindPickedPosition(PickId pick)
    {
        if (_scene is null)
        {
            return null;
        }

        foreach (Billboard b in _scene.Billboards)
        {
            if (b.PickId == pick)
            {
                return b.Position;
            }
        }

        foreach (MeshInstance m in _scene.Meshes)
        {
            if (m.PickId == pick)
            {
                return m.World.Translation;
            }
        }

        return null;
    }

    /// <summary>Finds the level object a pick refers to (object/mesh/brush by UID).</summary>
    public LevelObject? ObjectForPick(PickId pick)
    {
        if (Document is null || pick.Kind is not (PickKind.Object or PickKind.Mesh or PickKind.Brush))
        {
            return null;
        }

        return Document.FindByUid(pick.Index);
    }

    /// <summary>
    /// Resolves a face pick (compiled static geometry or a brush face) to its
    /// world-space centroid and outward normal — the hook for staple-to-face and
    /// cursor-ray placement.
    /// </summary>
    public bool TryFaceHit(PickId pick, out Vec3 point, out Vec3 normal)
    {
        point = default;
        normal = default;

        if (pick.Kind == PickKind.Face && _staticGeometry is { } sg && pick.Index >= 0 && pick.Index < sg.Faces.Count)
        {
            Face f = sg.Faces[pick.Index];
            point = FaceCentroid(sg, f);
            normal = f.Plane.Normal.Normalized();
            return true;
        }

        if (TryResolveBrushFace(pick, out int uid, out int fi) && BrushEditor?.FindBrush(uid) is { } b &&
            fi >= 0 && fi < b.Geometry.Faces.Count)
        {
            Face f = b.Geometry.Faces[fi];
            Vec3 local = FaceCentroid(b.Geometry, f);
            point = b.Position.Add(b.Rotation.Transform(local));
            normal = b.Rotation.Transform(f.Plane.Normal).Normalized();
            return true;
        }

        return false;
    }

    /// <summary>
    /// The nearest front-facing CPU-raycast hit against the compiled static geometry / editable brush
    /// faces. <see cref="BrushUid"/> / <see cref="FaceIndex"/> identify the hit EDITABLE brush face
    /// (both −1 when the hit was compiled static geometry, which has no editable face — or no hit).
    /// </summary>
    public readonly record struct RayFaceHitResult(bool Hit, Vec3 Point, Vec3 Normal, int BrushUid, int FaceIndex);

    /// <summary>
    /// The PLACEMENT raycast — it traces WHAT THE USER SEES, not the raw brushwork:
    /// <list type="number">
    /// <item>when compiled static geometry exists in the document, it raycasts the COMPILED geometry
    /// ONLY (raw authored brush faces are excluded entirely — with the live-CSG preview active a raw
    /// unmerged brush face, or the carved-away region of one, could otherwise catch the ray where the
    /// merged result has an opening, placing things against geometry the user cannot see);</item>
    /// <item>only on a never-built level (no compiled geometry) does it fall back to the authored brush
    /// faces (the visible brush overlay), skipping hidden brushes.</item>
    /// </list>
    /// Backend-independent (no GPU pick). The shared drop resolver uses it for BOTH the ghost and the
    /// drop, so they stay identical; <paramref name="usedCompiled"/> reports which set resolved.
    /// </summary>
    public RayFaceHitResult RayPlacementHit(Vector3 origin, Vector3 direction, out bool usedCompiled)
    {
        usedCompiled = _staticGeometry is { Faces.Count: > 0 };
        var o = new Vec3(origin.X, origin.Y, origin.Z);
        var d = new Vec3(direction.X, direction.Y, direction.Z);
        if (d.LengthSquared() < 1e-12f)
        {
            return default;
        }

        float bestT = float.MaxValue;
        bool hit = false;
        Vec3 bestPoint = default;
        Vec3 bestNormal = default;
        int bestUid = -1;
        int bestFace = -1;

        void Test(Geometry g, int brushUid, Func<Vec3, Vec3> toWorld, Func<Vec3, Vec3> normalToWorld)
        {
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Vertices.Count < 3)
                {
                    continue;
                }

                Vec3 n = normalToWorld(f.Plane.Normal).Normalized();
                if (n.LengthSquared() < 1e-8f)
                {
                    continue;
                }

                float denom = n.Dot(d);
                if (MathF.Abs(denom) < 1e-7f)
                {
                    continue; // ray parallel to the face plane
                }

                Vec3 p0 = toWorld(VertexAt(g, f.Vertices[0].Index));
                float t = n.Dot(p0.Sub(o)) / denom;
                if (t <= 1e-4f || t >= bestT)
                {
                    continue;
                }

                Vec3 hitPoint = o.Add(d.Scale(t));
                if (!PointInFace(g, f, toWorld, n, hitPoint))
                {
                    continue;
                }

                bestT = t;
                // Outward normal faces the ray origin (a decal/object sits on the visible side).
                bestNormal = denom > 0 ? n.Scale(-1f) : n;
                bestPoint = hitPoint;
                bestUid = brushUid;
                bestFace = brushUid >= 0 ? fi : -1;
                hit = true;
            }
        }

        if (_staticGeometry is { Faces.Count: > 0 } sg)
        {
            Test(sg, -1, v => v, nrm => nrm); // (1) compiled ONLY — what the user sees
        }
        else if (BrushEditor is { } be)
        {
            foreach (Brush b in be.Brushes) // (2) never-built fallback: visible authored brush faces
            {
                if (!be.IsBrushHidden(b.Uid))
                {
                    Test(b.Geometry, b.Uid, v => b.Position.Add(b.Rotation.Transform(v)), nrm => b.Rotation.Transform(nrm));
                }
            }
        }

        return new RayFaceHitResult(hit, bestPoint, bestNormal, bestUid, bestFace);
    }

    /// <summary>The world-space outline of a brush face (its edges), for the drag-over highlight overlay.</summary>
    public IReadOnlyList<LineSegment> BuildBrushFaceOutline(int brushUid, int faceIndex, uint color)
    {
        var lines = new List<LineSegment>();
        if (BrushEditor?.FindBrush(brushUid) is { } b && faceIndex >= 0 && faceIndex < b.Geometry.Faces.Count)
        {
            AddFaceOutline(lines, b, b.Geometry.Faces[faceIndex], color);
        }

        return lines;
    }

    /// <summary>The result of a brush-face-only raycast (texture drag). See <see cref="RayBrushFaceHit"/>.</summary>
    public readonly record struct BrushFaceHit(bool Hit, Vec3 Point, Vec3 Normal, int BrushUid, int FaceIndex, bool BlockedByLock);

    /// <summary>
    /// CPU-raycasts a world ray against the AUTHORED, editable brush faces ONLY (never compiled static
    /// geometry — which is coplanar with brush output and would otherwise win the tie), returning the
    /// nearest UNLOCKED, non-hidden brush face — the texture-drag target. Locked brushes are skipped;
    /// when the nearest face(s) under the ray are all locked (no unlocked candidate) the result carries
    /// <see cref="BrushFaceHit.BlockedByLock"/> with that locked face's id, so the caller can show the
    /// standard locked hint / a blocked highlight. Hidden brushes are ignored entirely.
    /// </summary>
    public BrushFaceHit RayBrushFaceHit(Vector3 origin, Vector3 direction)
    {
        var o = new Vec3(origin.X, origin.Y, origin.Z);
        var d = new Vec3(direction.X, direction.Y, direction.Z);
        if (d.LengthSquared() < 1e-12f || BrushEditor is not { } be)
        {
            return default;
        }

        float bestT = float.MaxValue;
        float lockedT = float.MaxValue;
        bool hit = false, sawLocked = false;
        Vec3 bestPoint = default, bestNormal = default, lockedPoint = default, lockedNormal = default;
        int bestUid = -1, bestFace = -1, lockedUid = -1, lockedFace = -1;

        foreach (Brush b in be.Brushes)
        {
            if (be.IsBrushHidden(b.Uid))
            {
                continue;
            }

            bool locked = be.IsBrushLocked(b.Uid);
            Geometry g = b.Geometry;
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Vertices.Count < 3)
                {
                    continue;
                }

                Vec3 n = b.Rotation.Transform(f.Plane.Normal).Normalized();
                if (n.LengthSquared() < 1e-8f)
                {
                    continue;
                }

                float denom = n.Dot(d);
                if (MathF.Abs(denom) < 1e-7f)
                {
                    continue;
                }

                Vec3 p0 = b.Position.Add(b.Rotation.Transform(VertexAt(g, f.Vertices[0].Index)));
                float t = n.Dot(p0.Sub(o)) / denom;
                if (t <= 1e-4f)
                {
                    continue;
                }

                Vec3 hitPoint = o.Add(d.Scale(t));
                if (!PointInFace(g, f, v => b.Position.Add(b.Rotation.Transform(v)), n, hitPoint))
                {
                    continue;
                }

                Vec3 outward = denom > 0 ? n.Scale(-1f) : n;
                if (locked)
                {
                    if (t < lockedT)
                    {
                        lockedT = t;
                        lockedPoint = hitPoint;
                        lockedNormal = outward;
                        lockedUid = b.Uid;
                        lockedFace = fi;
                        sawLocked = true;
                    }
                }
                else if (t < bestT)
                {
                    bestT = t;
                    bestPoint = hitPoint;
                    bestNormal = outward;
                    bestUid = b.Uid;
                    bestFace = fi;
                    hit = true;
                }
            }
        }

        if (hit)
        {
            return new BrushFaceHit(true, bestPoint, bestNormal, bestUid, bestFace, false);
        }

        return sawLocked
            ? new BrushFaceHit(false, lockedPoint, lockedNormal, lockedUid, lockedFace, true)
            : default;
    }

    /// <summary>Even-odd point-in-polygon test for a face, projected onto its normal's minor plane.</summary>
    private static bool PointInFace(Geometry g, Face f, Func<Vec3, Vec3> toWorld, Vec3 n, Vec3 p)
    {
        // Drop the dominant axis of the normal so the projection preserves winding.
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        int drop = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        Vector2 Project(Vec3 v) => drop switch
        {
            0 => new Vector2(v.Y, v.Z),
            1 => new Vector2(v.X, v.Z),
            _ => new Vector2(v.X, v.Y),
        };

        Vector2 pt = Project(p);
        bool inside = false;
        int count = f.Vertices.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 vi = Project(toWorld(VertexAt(g, f.Vertices[i].Index)));
            Vector2 vj = Project(toWorld(VertexAt(g, f.Vertices[j].Index)));
            if (((vi.Y > pt.Y) != (vj.Y > pt.Y)) &&
                (pt.X < ((vj.X - vi.X) * (pt.Y - vi.Y) / (vj.Y - vi.Y)) + vi.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static Vec3 FaceCentroid(Geometry g, Face f)
    {
        Vec3 sum = Vec3.Zero;
        foreach (FaceVertex fv in f.Vertices)
        {
            sum = sum.Add(VertexAt(g, fv.Index));
        }

        return f.Vertices.Count > 0 ? sum.Scale(1f / f.Vertices.Count) : sum;
    }

    /// <summary>Resolves a brush-face pick to its (brush uid, face index), if any.</summary>
    public bool TryResolveBrushFace(PickId pick, out int brushUid, out int faceIndex)
    {
        brushUid = faceIndex = -1;
        return pick.Kind == PickKind.BrushFace && _brushRegistry is not null &&
            _brushRegistry.TryResolveFace(pick.Index, out brushUid, out faceIndex);
    }

    /// <summary>Resolves a brush-vertex pick to its (brush uid, vertex index), if any.</summary>
    public bool TryResolveBrushVertex(PickId pick, out int brushUid, out int vertexIndex)
    {
        brushUid = vertexIndex = -1;
        return pick.Kind == PickKind.BrushVertex && _brushRegistry is not null &&
            _brushRegistry.TryResolveVertex(pick.Index, out brushUid, out vertexIndex);
    }

    /// <summary>Highlight lines for the current brush/face/vertex selection.</summary>
    public IReadOnlyList<LineSegment> BuildBrushSelectionLines()
    {
        var lines = new List<LineSegment>();
        if (BrushEditor is null)
        {
            return lines;
        }

        uint hi = Palette.Rgba(255, 240, 60, 255);
        uint faceTint = Palette.Rgba(255, 160, 40, 255);

        foreach (int uid in BrushEditor.SelectedBrushes)
        {
            if (BrushEditor.FindBrush(uid) is Brush b)
            {
                AddBrushEdges(lines, b, hi);
            }
        }

        foreach ((int uid, int fi) in BrushEditor.SelectedFaces)
        {
            if (BrushEditor.FindBrush(uid) is Brush b && fi >= 0 && fi < b.Geometry.Faces.Count)
            {
                AddFaceOutline(lines, b, b.Geometry.Faces[fi], faceTint);
            }
        }

        foreach ((int uid, int vi) in BrushEditor.SelectedVertices)
        {
            if (BrushEditor.FindBrush(uid) is Brush b && vi >= 0 && vi < b.Geometry.Vertices.Count)
            {
                Vec3 w = BrushWorld(b, b.Geometry.Vertices[vi]);
                AddBox(lines, new Vector3(w.X, w.Y, w.Z), 0.12f, hi);
            }
        }

        // Selected edges (item 2) in bright orange; the hovered edge in cyan.
        uint edgeHi = Palette.Rgba(255, 130, 40, 255);
        foreach ((int uid, int v0, int v1) in BrushEditor.SelectedEdges)
        {
            AddEdgeLine(lines, uid, v0, v1, edgeHi);
        }

        if (HoveredEdge is (int huid, int hv0, int hv1))
        {
            AddEdgeLine(lines, huid, hv0, hv1, Palette.Rgba(120, 220, 255, 255));
        }

        return lines;
    }

    /// <summary>The edge under the cursor in Edge mode (brush UID + canonical vertex pair), or null.</summary>
    public (int Brush, int V0, int V1)? HoveredEdge { get; set; }

    private void AddEdgeLine(List<LineSegment> lines, int uid, int v0, int v1, uint color)
    {
        if (BrushEditor?.FindBrush(uid) is Brush b &&
            v0 >= 0 && v0 < b.Geometry.Vertices.Count && v1 >= 0 && v1 < b.Geometry.Vertices.Count)
        {
            Vec3 a = BrushWorld(b, b.Geometry.Vertices[v0]);
            Vec3 c = BrushWorld(b, b.Geometry.Vertices[v1]);
            lines.Add(new LineSegment(new Vector3(a.X, a.Y, a.Z), new Vector3(c.X, c.Y, c.Z), color));
        }
    }

    private static Vec3 BrushWorld(Brush b, Vec3 local) => b.Position.Add(b.Rotation.Transform(local));

    private static void AddBrushEdges(List<LineSegment> lines, Brush b, uint color)
    {
        var seen = new HashSet<(int, int)>();
        foreach (Face f in b.Geometry.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int c = f.Vertices[(i + 1) % n].Index;
                var key = a < c ? (a, c) : (c, a);
                if (seen.Add(key))
                {
                    Vec3 pa = BrushWorld(b, b.Geometry.Vertices[a]);
                    Vec3 pb = BrushWorld(b, b.Geometry.Vertices[c]);
                    lines.Add(new LineSegment(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), color));
                }
            }
        }
    }

    private static void AddFaceOutline(List<LineSegment> lines, Brush b, Face f, uint color)
    {
        int n = f.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            Vec3 pa = BrushWorld(b, b.Geometry.Vertices[f.Vertices[i].Index]);
            Vec3 pb = BrushWorld(b, b.Geometry.Vertices[f.Vertices[(i + 1) % n].Index]);
            lines.Add(new LineSegment(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), color));
        }
    }

    private static void AddBox(List<LineSegment> lines, Vector3 c, float half, uint color)
    {
        Span<Vector3> v = stackalloc Vector3[8];
        int idx = 0;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    v[idx++] = c + new Vector3(x * half, y * half, z * half);
                }
            }
        }

        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 },
        };
        for (int e = 0; e < 12; e++)
        {
            lines.Add(new LineSegment(v[edges[e, 0]], v[edges[e, 1]], color));
        }
    }

    // ---- Range/region overlay wireframes (item 8: selection ranges as overlay lines) ----
    // Mirrors SceneBuilder's range/region emission so the selection overlay is pixel-identical
    // to what the compiled scene used to draw when SelectedUids gated it.

    private static void AddSphere(List<LineSegment> lines, Vector3 center, float radius, uint color)
    {
        const int seg = 24;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 prev = default;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * MathF.Tau;
                float c = MathF.Cos(a) * radius;
                float s = MathF.Sin(a) * radius;
                Vector3 p = axis switch
                {
                    0 => center + new Vector3(c, s, 0f),
                    1 => center + new Vector3(c, 0f, s),
                    _ => center + new Vector3(0f, c, s),
                };
                if (i > 0)
                {
                    lines.Add(new LineSegment(prev, p, color));
                }

                prev = p;
            }
        }
    }

    private static void AddRegionOutline(List<LineSegment> lines, Vec3 pos, Mat3? rot,
        float? radius, float? width, float? height, float? depth, uint color)
    {
        var center = new Vector3(pos.X, pos.Y, pos.Z);
        if (radius is float r && r > 0.01f)
        {
            AddSphere(lines, center, r, color);
        }
        else if (width is float w && height is float ht && depth is float d)
        {
            AddOrientedBox(lines, center, rot, new Vector3(w, ht, d), color);
        }
    }

    private static void AddOrientedBox(List<LineSegment> lines, Vector3 center, Mat3? rot, Vector3 fullSize, uint color)
    {
        Vector3 h = fullSize * 0.5f;
        Matrix4x4 m = rot is Mat3 r
            ? new Matrix4x4(
                r.Right.X, r.Right.Y, r.Right.Z, 0f,
                r.Up.X, r.Up.Y, r.Up.Z, 0f,
                r.Forward.X, r.Forward.Y, r.Forward.Z, 0f,
                center.X, center.Y, center.Z, 1f)
            : Matrix4x4.CreateTranslation(center);
        Span<Vector3> corners = stackalloc Vector3[8];
        int idx = 0;
        for (int xi = -1; xi <= 1; xi += 2)
        {
            for (int yi = -1; yi <= 1; yi += 2)
            {
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    var local = new Vector3(xi * h.X, yi * h.Y, zi * h.Z);
                    corners[idx++] = rot is null ? center + local : Vector3.Transform(local, m);
                }
            }
        }

        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 },
        };
        for (int e = 0; e < 12; e++)
        {
            lines.Add(new LineSegment(corners[edges[e, 0]], corners[edges[e, 1]], color));
        }
    }

    private static Vec3 VertexAt(Geometry g, int index) =>
        index >= 0 && index < g.Vertices.Count ? g.Vertices[index] : default;

    private static Geometry? FindStaticGeometry(RflFile file)
    {
        file.ParseAllKnownSections();
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                return g.Geometry;
            }
        }

        return null;
    }

    private void LoadCatalogs()
    {
        _entities = TryLoad("entity.tbl", EntityCatalog.Load);
        _clutter = TryLoad("clutter.tbl", ClutterCatalog.Load);
        _items = TryLoad("items.tbl", ItemCatalog.Load);
        _glares = TryLoad("effects.tbl", GlareCatalog.Load);
    }

    /// <summary>Class names from the .tbl catalogs for palette dropdowns (empty when no VFS/catalog).</summary>
    public IReadOnlyList<string> ClassNames(Ged.Core.Editor.LevelObjectKind kind) => kind switch
    {
        Ged.Core.Editor.LevelObjectKind.Entity =>
            _entities?.Entities.Select(e => e.Name).OrderBy(n => n).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
        Ged.Core.Editor.LevelObjectKind.Clutter =>
            _clutter?.Clutters.Select(c => c.ClassName).OrderBy(n => n).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
        Ged.Core.Editor.LevelObjectKind.Item =>
            _items?.Items.Select(i => i.ClassName).OrderBy(n => n).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
        _ => Array.Empty<string>(),
    };

    private T? TryLoad<T>(string tableName, Func<byte[], T> parse)
        where T : class
    {
        try
        {
            byte[]? data = Vfs?.ReadFile(tableName);
            return data is null ? null : parse(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Vfs?.Dispose();
        Vfs = null;
    }
}
