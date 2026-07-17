using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Ged.App.Camera;
using Ged.App.Dialogs;
using Ged.App.Docking;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Rendering;
using Ged.Rendering.Picking;
using AvDock = Avalonia.Controls.Dock;
using CoreGesture = Ged.Core.Input.KeyGesture;

namespace Ged.App;

/// <summary>
/// The editor shell: a Dock.Avalonia layout (viewport grid + Outliner /
/// Properties / History / placeholder panels), a menu and status bar, the command
/// system (registry + keymap + palette + rebindable hotkeys), camera schemes, and
/// the document lifecycle (open/new/save/MRU/drag-drop/autosave). Implements
/// <see cref="IEditorHost"/> so panels can drive the shell.
/// </summary>
public sealed partial class MainWindow : Window, IEditorHost
{
    private readonly AppSettings _settings;
    private readonly Keymap _keymap;
    private readonly CommandRegistry _registry;
    private readonly CommandDispatcher _dispatcher;
    private readonly EditorSession _session = new();
    private readonly ViewportGrid _viewportGrid;

    private readonly OutlinerPanel _outliner = new();
    private readonly PropertiesPanel _properties = new();
    private readonly HistoryPanel _history = new();
    private readonly Panels.ModeToolPanel _modePanel = new();
    private readonly Panels.PalettePanel _palette = new();
    private readonly Panels.LinkGraphPanel _linkGraph = new();
    private readonly Panels.DependencyGraphPanel _depGraph = new();
    private readonly Panels.LintPanel _lintPanel = new();
    private readonly Panels.StatisticsPanel _statsPanel = new();
    private readonly Panels.LayersPanel _layers = new();
    private GeometryBuildController? _buildController;

    /// <summary>When a recovered autosave was opened, the autosave file to delete on the next
    /// successful save (item 18); null otherwise.</summary>
    private string? _pendingAutosaveDelete;

    /// <summary>When armed, the next viewport face pick samples its texture into this callback
    /// (the Face-properties eyedropper, item 6) instead of selecting.</summary>
    private Action<string>? _textureSampleCallback;
    private bool _previewLighting;

    /// <summary>Item 4: the unified Log output panel — every operation (build, lighting, hole
    /// check, asset/packfile reports) appends a tagged entry here.</summary>
    private readonly Panels.LogOutputPanel _logOutput = new();

    /// <summary>Item 3: tracks in-flight operations for the bottom-right viewport progress overlay.</summary>
    private readonly Services.OperationProgressService _progress = new();

    /// <summary>The bottom-right toast stack — a full-window, input-transparent overlay above the dock.</summary>
    private readonly Controls.ToastHost _toastHost = new();

    /// <summary>The unified notification layer (status bar + Log + toast). Assigned in the constructor.</summary>
    private readonly Services.NotificationService _notifications;

    private readonly TextBlock _statusMode = MakeStatus("Mode: —");
    private readonly TextBlock _statusGrid = MakeStatus("Grid: —");
    private readonly TextBlock _statusRotate = MakeStatus("Rotate: —");
    private readonly TextBlock _statusSnap = MakeStatus("Snap: —");
    private readonly Ged.Core.Editing.SnapPolicy _snap = new();
    private Avalonia.Controls.Primitives.ToggleButton? _magnetButton;
    private Avalonia.Controls.Primitives.ToggleButton? _rulerButton;
    private Avalonia.Controls.Primitives.ToggleButton? _drawButton;
    private Avalonia.Controls.Primitives.ToggleButton? _selectButton;
    private Avalonia.Controls.Primitives.ToggleButton? _gizmoMoveBtn, _gizmoRotateBtn, _gizmoScaleBtn, _gizmoLocalBtn;
    private readonly TextBlock _statusSpeed = MakeStatus("Cam: —");
    private readonly TextBlock _statusCoords = MakeStatus("—");
    private readonly TextBlock _statusSelection = MakeStatus("Sel: —");
    private readonly TextBlock _statusFps = MakeStatus("fps: —");
    private readonly TextBlock _statusRoom = MakeStatus("Room: —");
    private readonly TextBlock _statusIsolation = MakeStatus(string.Empty);
    private readonly TextBlock _statusMount = MakeStatus("RF: …");
    private readonly TextBlock _statusMessage = MakeStatus(string.Empty);
    private readonly System.Collections.Generic.List<(MenuItem Item, CameraSchemeKind Kind)> _cameraSchemeItems = new();
    private readonly RenderOptionsModel _renderOptions;
    private readonly IncrementSetting _gridIncrement;
    private readonly IncrementSetting _rotationIncrement;

    // Selection-filter chips ([Brushes] [Faces] [Vertices] [Objects] [Groups]) — the
    // visible face of the mode system; kept in two-way sync with the editing mode.
    private readonly Ged.Core.Editing.SelectionFilter _filter = new();
    private readonly System.Collections.Generic.Dictionary<Ged.Core.Editing.SelectKinds, Avalonia.Controls.Primitives.ToggleButton> _filterChips = new();
    private DispatcherTimer? _emitterTimer;
    private DateTime _emitterStart;

    private readonly DispatcherTimer _autosaveTimer;
    private PickId _lastPick = PickId.None;

    /// <summary>
    /// The last pick that ACTUALLY selected something in-mode (item (a) masquerade fix). The
    /// selection-highlight box for the last click is drawn from THIS, not the raw
    /// <see cref="_lastPick"/> — so an out-of-mode pick that the router/<see cref="Services.PickGate"/>
    /// correctly rejected (an object clicked in Brush mode, a brush clicked in Object mode) no
    /// longer gets a phantom highlight that masquerades as a selection. <see cref="_lastPick"/>
    /// still records the raw hit for placement/face-hit (which needs the clicked face regardless).
    /// </summary>
    private PickId _lastPickHighlight = PickId.None;
    private string? _initialOpenPath;

    public MainWindow(string? initialOpenPath = null)
    {
        _settings = SettingsStore.Load();
        _keymap = KeymapStore.Load();
        _registry = CommandCatalog.BuildRegistry();
        _dispatcher = new CommandDispatcher(_registry, _keymap);
        _initialOpenPath = initialOpenPath;

        _session.GridBrightness = _settings.GridBrightness;
        _session.GridSize = _settings.GridSize;
        _session.ShowLinks = _settings.ShowLinks;
        _session.ShowEventArrows = _settings.ShowEventArrows;
        _session.ShowBoundingBoxes = _settings.ShowBoundingBoxes;
        _session.ShowPathNodes = _settings.ShowPathNodeConnections;
        _session.DrawUnmergedBrushwork = _settings.DrawUnmergedBrushwork;
        _session.ShowAllRanges = _settings.ShowAllRanges;
        _session.AnimateEmitters = _settings.AnimateEmitters;
        _session.DrawSky = _settings.DrawSky;
        _session.DrawDecals = _settings.DrawDecals;
        _session.PortalFaces = (Ged.Rendering.Scene.PortalFaceDrawMode)_settings.PortalFaceMode;
        ApplyElementColors();

        // Shared models behind the per-pane render-option toggles (item 3) and the
        // grid / rotation increment pickers (item 4) — global state, many UI surfaces.
        _renderOptions = RenderOptionsModel.BuildGlobal(
            _settings, _session, RebuildScene, Persist, ApplyBackfaceCulling, SetEmitterAnimation,
            ensureMergedBrushStash: () => _buildController?.EnsureMergedBrushStash(),
            gizmoVisible: () => GizmoVisible,
            toggleGizmo: ToggleGizmoVisible,
            applyFog: ApplyFog,
            getRoomMode: () => _session.RoomMode,
            setRoomMode: SetRoomMode,
            getPortalFaces: () => _session.PortalFaces,
            setPortalFaces: SetPortalFaces);
        _gridIncrement = new IncrementSetting(
            "Grid", " m", Ged.Core.Editing.SnapIncrements.GridPresets,
            () => _settings.GridSize, SetGridSize, Ged.Core.Editing.SnapIncrements.TryParseGrid,
            hotkeyLadder: Ged.Core.Editing.SnapIncrements.GridLadder);
        _rotationIncrement = new IncrementSetting(
            "Rot", "°", Ged.Core.Editing.SnapIncrements.RotationPresets,
            () => _settings.RotationStep, SetRotationStep, Ged.Core.Editing.SnapIncrements.TryParseRotation);

        _viewportGrid = new ViewportGrid(
            _dispatcher, (CameraSchemeKind)_settings.CameraScheme, ClampMode(_settings.RenderMode),
            _renderOptions, Viewport.ViewportBackends.UsesOpenGl(_settings.Renderer));

        Title = "Glacier";
        Icon = LoadAppIcon();
        Width = 1500;
        Height = 940;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _outliner.Bind(this);
        _properties.Bind(this);
        _properties.PickMeshFile = PickMeshFileAsync;
        _history.Bind(this);
        _layers.Bind(this);
        _palette.Bind(this);
        _linkGraph.Bind(this);
        _depGraph.Bind(this);

        _buildController = new GeometryBuildController(
            _session,
            msg => Dispatcher.UIThread.Post(() => _statusMessage.Text = msg),
            RebuildScene,
            ShowBuildReport)
        {
            // Item 4: operations without a full BuildReport (relight, remove-lightmaps, hole check)
            // still append a tagged line to the Log output panel.
            Log = (op, msg) => LogOperation(op, msg),

            // Item 3: register build/bake/hole-check runs with the viewport progress overlay.
            BeginOperation = name => _progress.Begin(name),
        };
        // FIFTH-round fix: BuildScene (the consumption site) requests the merged-brush stash
        // whenever the OFF view needs it but no build has produced one — so entering an edit
        // mode on a freshly opened level materializes the merged view without a toggle/edit.
        _session.RequestMergedBrushStash = () => _buildController!.EnsureMergedBrushStash();
        _buildController.UseSharedBspBuild = _settings.UseSharedBspBuild; // Geometry menu "Build method" (persisted)
        SyncLightingMethodToController(); // feature 1: seed the bake method from the global default

        // Unified notifications: every Notify hits the status bar + Log always, and additionally raises
        // a bottom-right toast when its severity passes the user's configured threshold. Build refusals
        // and failures route through it (the controller's Notify hook), so they surface as toasts too.
        _notifications = new Services.NotificationService(
            () => (Services.ToastLevel)_settings.ToastLevel,
            (_, message) => _dispatcher.ShowMessage(message),
            (severity, message) => LogOperation(Services.NotificationService.Tag(severity), message),
            (severity, message) => Dispatcher.UIThread.Post(() => _toastHost.Show(severity, message)));
        _buildController.Notify = _notifications.Notify;

        InitScripting(); // build the scripting service + console before the dock layout references it
        Content = BuildLayout();
        BindCommands();
        WireViewport();
        InitEditing();
        InitMount(); // item 7: mount status + live-remount refresh wiring
        UpdateStatusStatics();

        _dispatcher.Message += msg => Dispatcher.UIThread.Post(() => _statusMessage.Text = msg);

        // SelectionRouter dropped an out-of-mode selection: a low-severity Hint. It always reaches the
        // status bar (overwritten in place) + Log; it only toasts at the "Everything" level, and even
        // then coalescing collapses a marquee's repeats into one "×N" card — never a toast storm.
        _session.SelectionDropped += kind => _notifications.Notify(
            Services.NotificationSeverity.Hint,
            $"{kind} can't be selected in this mode — switch mode or Ctrl+click its chip.");

        // G: a click that resolves only to a locked brush/object selects nothing and hints.
        _session.SelectionLockBlocked += () => _notifications.Notify(
            Services.NotificationSeverity.Hint, "Locked — unlock to select.");

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        DragDrop.SetAllowDrop(this, true);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => TryAutosave();
        _autosaveTimer.Start();

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => _session.Dispose();
    }

    /// <summary>
    /// The Glacier window/taskbar icon, loaded from the embedded multi-size .ico
    /// (AvaloniaResource, so it survives single-file publish). Best-effort — a missing
    /// resource must not stop the editor from opening.
    /// </summary>
    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            return new WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://Glacier/Assets/AppIcon.ico")));
        }
        catch
        {
            return null;
        }
    }

    // ---- IEditorHost ----

    public EditorDocument? Document => _session.Document;

    /// <summary>The mandatory mode/chip-gated selection entry point (item: SelectionRouter).</summary>
    public Ged.Core.Editing.SelectionRouter Selection => _session.Selection;

    CommandDispatcher IEditorHost.Dispatcher => _dispatcher;

    public void RequestSceneRebuild() => RebuildScene();

    public void RefreshSelectionOverlay()
    {
        var lines = new List<Ged.Rendering.Scene.LineSegment>();
        if (Document is not null)
        {
            lines.AddRange(_session.BuildSelectionLines(Document.Selection));
            lines.AddRange(_session.BuildSelectionRangeLines(Document.Selection)); // item 8: selected range/region in the overlay
            lines.AddRange(_session.BuildSelectionLinkLines(Document.Selection));  // links touching the selection (shown even when Show Links is off)
        }

        lines.AddRange(_session.BuildBrushSelectionLines());
        lines.AddRange(BuildCutterGhost());
        lines.AddRange(BuildDrawToolGhost());
        lines.AddRange(BuildMarqueeLines());
        lines.AddRange(BuildSnapMarker());
        lines.AddRange(BuildAnnotationOverlay());
        lines.AddRange(BuildEditingOverlays());
        lines.AddRange(_clipPreview);
        lines.AddRange(_holeLines);

        // Item (a): highlight only the last pick that actually SELECTED something in-mode. Drawing
        // the raw _lastPick here highlighted out-of-mode picks the router already rejected (an
        // object clicked in Brush mode, a brush clicked in Object mode) — a purely visual
        // masquerade over correctly-unchanged state.
        if (!_lastPickHighlight.IsNone)
        {
            lines.AddRange(_session.BuildSelectionLines(_lastPickHighlight));
        }

        _viewportGrid.SetSelection(lines);

        // The gizmo/manipulator draws through a SEPARATE on-top overlay channel (depth test
        // disabled) so its handles are visible and aimable even when the selection is behind
        // other geometry (item 12). Its handle picking is CPU ray-based and already takes
        // priority at press time, so restoring visibility restores aim. The transform-drag
        // progress indicators (dimension line / angle arc / scale ghost) ride the same
        // on-top channel so they read over geometry mid-drag.
        // The prefab-unit padlock badge rides the same on-top channel as the gizmo so it reads over
        // geometry and its CPU pick (against the pick ray) matches what the user sees.
        _viewportGrid.SetGizmoOverlay(BuildGizmoLines().Concat(BuildTransformIndicatorLines()).Concat(BuildPrefabBadge()).ToList());
        _statusSelection.Text = "Sel: " + BuildSelectionReadout();
    }

    public void FrameObject(LevelObject o) => _viewportGrid.ActiveSurface.FramePoint(_session.PositionOf(o));

    /// <summary>
    /// Frames a brush by UID in the perspective viewport (Layers-panel double-click): fits its
    /// world-space AABB (over the transformed vertices) so the whole brush is in view — the same
    /// framing mechanism as the Outliner Jump To, targeted at the perspective camera.
    /// </summary>
    public void FrameBrush(int uid)
    {
        if (BrushEditor?.FindBrush(uid) is not Ged.Core.Model.Brush b)
        {
            return;
        }

        Ged.Core.Model.Geometry g = b.Geometry;
        if (g.Vertices.Count == 0)
        {
            _viewportGrid.CameraSurface.FramePoint(new Vector3(b.Position.X, b.Position.Y, b.Position.Z));
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Ged.Core.Model.Vec3 local in g.Vertices)
        {
            Ged.Core.Model.Vec3 w = BrushWorld.ToWorld(b.Rotation, b.Position, local);
            var v = new Vector3(w.X, w.Y, w.Z);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        _viewportGrid.CameraSurface.Frame(new Ged.Core.Model.Aabb(
            new Ged.Core.Model.Vec3(min.X, min.Y, min.Z),
            new Ged.Core.Model.Vec3(max.X, max.Y, max.Z)));
    }

    /// <summary>
    /// "View From": places the PERSPECTIVE camera at the object's exact position and aims it
    /// along the object's forward vector (yaw/pitch from its rotation matrix; roll ignored).
    /// One implementation shared by the Object-mode Gestures button and the Outliner menu.
    /// </summary>
    public void ViewFromObject(LevelObject o)
    {
        System.Numerics.Vector3 pos = _session.PositionOf(o);
        Ged.Core.Model.Mat3 rot = GetModelRotation(o.Model) ?? Ged.Core.Model.Mat3.Identity;
        Ged.Core.Model.Vec3 f = rot.Forward;
        _viewportGrid.CameraSurface.ViewFrom(pos, new System.Numerics.Vector3(f.X, f.Y, f.Z));
    }

    // ---- Layout ----

    private Control BuildLayout()
    {
        var root = new DockPanel();
        Menu menu = BuildMenu();
        DockPanel.SetDock(menu, AvDock.Top);
        root.Children.Add(menu);

        Control toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, AvDock.Top);
        root.Children.Add(toolbar);

        Control status = BuildStatusBar();
        DockPanel.SetDock(status, AvDock.Bottom);
        root.Children.Add(status);

        // Item 3: the progress overlay floats over the viewport grid, pinned bottom-right. On the
        // default Direct3D 11 backend each pane is a native child HWND that paints above every
        // Avalonia-drawn layer in the same region, so the card stack is rehosted in a native top-level
        // Popup (which paints ABOVE native children — the airspace escape the app's menus already rely
        // on) rather than an in-Grid overlay the HWND would hide. The popup opens/closes in step with
        // the overlay's active state; Avalonia keeps it pinned to the host's bottom-right as the window
        // moves/resizes and the dock re-lays-out (built-in PopupOpenState tracking — see BuildOverlayPopup).
        var viewportHost = new Grid();
        viewportHost.Children.Add(_viewportGrid);

        var progressOverlay = new Controls.ProgressOverlay(_progress);
        Popup progressPopup = BuildOverlayPopup(progressOverlay, viewportHost, inset: 14);
        progressOverlay.ActiveChanged += open => progressPopup.IsOpen = open;
        viewportHost.Children.Add(progressPopup);

        var factory = new DockFactory(
            viewportHost, _outliner, _properties, _history, _modePanel, _palette,
            BuildAssetBrowserPanel(),
            _logOutput,
            _linkGraph, _depGraph, _lintPanel, _statsPanel, _layers, _scriptConsole);
        var dock = new DockControl { Factory = factory, Layout = factory.CreateLayout() };
        factory.InitLayout(dock.Layout!);
        root.Children.Add(dock);

        // The toast stack is likewise rehosted in a native top-level Popup, anchored to the window's
        // content area so toasts stay visible over a MAXIMIZED viewport pane (whose native HWND would
        // otherwise own the bottom-right corner). Its surface has no background, so only the cards are
        // hit-testable; the popup is non-activating and never takes focus, so a click dismisses a card
        // without pulling focus off the main window. It is open while cards are present.
        var shell = new Grid();
        shell.Children.Add(root);

        Popup toastPopup = BuildOverlayPopup(_toastHost, shell, inset: 16);
        _toastHost.VisualCardsChanged += () => toastPopup.IsOpen = _toastHost.HasVisibleCards;
        shell.Children.Add(toastPopup);
        return shell;
    }

    /// <summary>
    /// Builds a native top-level <see cref="Popup"/> that pins <paramref name="content"/> to the
    /// bottom-right corner of <paramref name="target"/> with a fixed <paramref name="inset"/>. Native
    /// popups paint ABOVE the Direct3D 11 viewport child HWNDs (the airspace escape the app's menus and
    /// flyouts already rely on), so an overlay hosted this way is visible on every backend. The popup is
    /// non-activating and never takes focus, so it informs / dismisses without disturbing the viewport
    /// or the main window's focus. Position tracking is Avalonia's own: while open, a popup follows the
    /// window's <c>PositionChanged</c> and the target's <c>LayoutUpdated</c>, and re-runs placement when
    /// these offsets change (verified against Popup's PopupOpenState in 11.2.1), so the card never
    /// drifts on a window move/resize or a dock-layout change.
    /// </summary>
    private static Popup BuildOverlayPopup(Control content, Control target, double inset) => new()
    {
        Child = content,
        PlacementTarget = target,
        Placement = PlacementMode.AnchorAndGravity,
        PlacementAnchor = PopupAnchor.BottomRight,
        PlacementGravity = PopupGravity.TopLeft,
        HorizontalOffset = -inset, // inset left from the target's right edge
        VerticalOffset = -inset,   // inset up from the target's bottom edge
        IsLightDismissEnabled = false,       // open/close is driven by content presence, not click-away
        Focusable = false,                   // never grabs keyboard focus
        TakesFocusFromNativeControl = false, // never yanks focus off the viewport HWND when it opens
        WindowManagerAddShadowHint = false,  // the cards carry their own shadow
    };

    private Menu BuildMenu()
    {
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Cmd("New Level", CommandIds.FileNew));
        file.Items.Add(Cmd("Open…", CommandIds.FileOpen));
        file.Items.Add(BuildRecentMenu());
        file.Items.Add(new Separator());
        file.Items.Add(Cmd("Save", CommandIds.FileSave));
        file.Items.Add(Cmd("Save As…", CommandIds.FileSaveAs));
        file.Items.Add(new Separator());
        file.Items.Add(Cmd("Import Mesh…", CommandIds.FileImportMesh));
        var export = new MenuItem { Header = "Export" };
        export.Items.Add(Cmd("Selection To Mesh (.v3m)…", CommandIds.FileExportMesh));
        export.Items.Add(new Separator());
        export.Items.Add(Cmd("Level as glTF…", CommandIds.FileExportGltf));
        export.Items.Add(Cmd("Level as OBJ…", CommandIds.FileExportObj));
        export.Items.Add(Cmd("As VRML…", CommandIds.FileExportVrml));
        file.Items.Add(export);
        file.Items.Add(Cmd("Save Selection As Prefab…", CommandIds.FileSaveAsPrefab));
        file.Items.Add(new Separator());
        file.Items.Add(Cmd("Open Dialogue Text", CommandIds.FileDialogueText));
        file.Items.Add(Cmd("Create Level Packfile", CommandIds.FilePackfile));
        file.Items.Add(new Separator());
        file.Items.Add(Cmd("Play Level (F7)", CommandIds.FilePlayLevel));
        file.Items.Add(Cmd("Play Level from Camera (F8)", CommandIds.FilePlayFromCamera));
        file.Items.Add(Cmd("Play in Multi (F9)", CommandIds.FilePlayMulti));
        file.Items.Add(Cmd("Play in Multi from Camera (F10)", CommandIds.FilePlayMultiFromCamera));
        file.Items.Add(new Separator());
        var exit = new MenuItem { Header = "E_xit" };
        exit.Click += (_, _) => Close();
        file.Items.Add(exit);

        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(Cmd("Undo", CommandIds.EditUndo));
        edit.Items.Add(Cmd("Redo", CommandIds.EditRedo));
        edit.Items.Add(new Separator());
        edit.Items.Add(Cmd("Cut", CommandIds.EditCut));
        edit.Items.Add(Cmd("Copy", CommandIds.EditCopy));
        edit.Items.Add(Cmd("Paste", CommandIds.EditPaste));
        edit.Items.Add(Cmd("Delete", CommandIds.EditDelete));
        edit.Items.Add(new Separator());
        edit.Items.Add(Cmd("Properties", CommandIds.EditProperties));
        edit.Items.Add(Cmd("Level Properties", CommandIds.EditLevelProperties));
        edit.Items.Add(new Separator());
        var toggleEditorLight = new MenuItem { Header = "Toggle Light Editor-Only (section move)" };
        toggleEditorLight.Click += (_, _) => ToggleSelectedLightsEditorOnly();
        edit.Items.Add(toggleEditorLight);

        var select = new MenuItem { Header = "_Select" };
        select.Items.Add(Cmd("Invert Selection", CommandIds.SelectInvert));
        select.Items.Add(Cmd("Select By UID…", CommandIds.SelectByUid));
        select.Items.Add(Cmd("Select All", CommandIds.SelectAll));
        select.Items.Add(new Separator());
        select.Items.Add(Cmd("Hide Selected", CommandIds.VisHideSelected));
        select.Items.Add(Cmd("Unhide All Brushes", CommandIds.VisUnhideBrushes));
        select.Items.Add(Cmd("Hide All Objects", CommandIds.VisHideObjects));
        select.Items.Add(Cmd("Unhide All Objects", CommandIds.VisUnhideObjects));
        select.Items.Add(Cmd("Invert Hidden", CommandIds.VisInvertHidden));
        select.Items.Add(Cmd("Lock Selected", CommandIds.VisLock));
        select.Items.Add(Cmd("Unlock All", CommandIds.VisUnlockAll));

        var view = new MenuItem { Header = "_View" };
        foreach ((string label, RenderMode mode) in new[]
        {
            ("Just Textures", RenderMode.JustTextures),
            ("Textures w Lightmaps", RenderMode.TexturesAndLightmaps),
            ("Just Lightmaps", RenderMode.JustLightmaps),
            ("Rooms in Different Colors", RenderMode.RoomColors),
            ("Wireframe", RenderMode.Wireframe),
            ("Everything See-through", RenderMode.SeeThrough),
        })
        {
            RenderMode captured = mode;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { _viewportGrid.ActiveSurface.Mode = captured; UpdateStatusStatics(); };
            view.Items.Add(item);
        }

        view.Items.Add(new Separator());
        view.Items.Add(Cmd("1 Pane", CommandIds.View1Pane));
        view.Items.Add(Cmd("2 Panes", CommandIds.View2Pane));
        view.Items.Add(Cmd("4 Panes", CommandIds.View4Pane));
        view.Items.Add(Cmd("Maximize Viewport (Tab)", CommandIds.ViewMaximize));
        view.Items.Add(Cmd("Reset Viewport Layout", CommandIds.ViewResetLayout));
        view.Items.Add(new Separator());

        // Global camera scheme (one scheme for all panes; Settings ▸ Input keeps the
        // same value). Radio submenu — replaces the old per-pane toolbar dropdowns.
        var cameraScheme = new MenuItem { Header = "Camera Scheme" };
        foreach (CameraSchemeKind kind in Enum.GetValues<CameraSchemeKind>())
        {
            CameraSchemeKind captured = kind;
            var schemeItem = new MenuItem
            {
                Header = CameraSchemes.DisplayName(kind),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = (int)kind == _settings.CameraScheme,
            };
            schemeItem.Click += (_, _) => SetCameraScheme(captured);
            _cameraSchemeItems.Add((schemeItem, kind));
            cameraScheme.Items.Add(schemeItem);
        }

        view.Items.Add(cameraScheme);
        view.Items.Add(new Separator());
        // The per-object render toggles (Bounding Boxes, Draw unmerged brushwork, All Ranges,
        // Animate Emitters, Disable Backface Culling) live in each pane's render-mode dropdown.
        // Item 4 also relocated the remaining View-menu render toggles there: Show Links, Show
        // Path Node Connections, Show Gizmo, Show Annotations (all view types), plus Draw Sky,
        // Show Fog, Room Rendering and Portal Faces (perspective panes only). Only the gizmo
        // axis-space toggle and isolation remain here.
        view.Items.Add(Cmd("Gizmo: Local / World", CommandIds.GizmoLocalWorld));
        view.Items.Add(new Separator());
        view.Items.Add(Cmd("Isolate Selection", CommandIds.ViewIsolateSelection));
        var exitIsolation = new MenuItem { Header = "Exit Isolation" };
        exitIsolation.Click += (_, _) => ExitIsolationIfActive();
        view.Items.Add(exitIsolation);
        view.Items.Add(new Separator());
        view.Items.Add(Cmd("Cycle Grid Brightness", CommandIds.GridBrightness));
        view.Items.Add(Cmd("Grid Size Up", CommandIds.GridSizeUp));
        view.Items.Add(Cmd("Grid Size Down", CommandIds.GridSizeDown));
        view.Items.Add(Cmd("Rotation Step Up", CommandIds.GridRotationUp));
        view.Items.Add(Cmd("Rotation Step Down", CommandIds.GridRotationDown));

        var tools = new MenuItem { Header = "_Tools" };
        tools.Items.Add(Cmd("Command Palette", CommandIds.AppCommandPalette));
        var gizmo = new MenuItem { Header = "Transform Gizmo" };
        gizmo.Items.Add(Cmd("Move Gizmo", CommandIds.GizmoMove));
        gizmo.Items.Add(Cmd("Rotate Gizmo", CommandIds.GizmoRotate));
        gizmo.Items.Add(Cmd("Scale Gizmo", CommandIds.GizmoScale));
        gizmo.Items.Add(Cmd("Gizmo Off", CommandIds.GizmoNone));
        tools.Items.Add(gizmo);
        tools.Items.Add(Cmd("Toggle Snap (Magnet)", CommandIds.ToggleSnap));
        tools.Items.Add(Cmd("Ruler (measure)", CommandIds.ToolRuler));
        tools.Items.Add(Cmd("Clear Annotations", CommandIds.AnnotationsClear));
        tools.Items.Add(Cmd("Clip (two-point plane)…", CommandIds.EditClipDialog));
        tools.Items.Add(new Separator());
        tools.Items.Add(Cmd("Verify All Textures", CommandIds.ToolsVerifyTextures));
        tools.Items.Add(Cmd("Reload Textures", CommandIds.ToolsReloadTextures));
        tools.Items.Add(Cmd("Reload Meshes", CommandIds.ToolsReloadMeshes));
        tools.Items.Add(Cmd("Library Health Report", CommandIds.ToolsLibraryHealth));
        tools.Items.Add(new Separator());
        var runLinter = new MenuItem { Header = "Run Level Linter" };
        runLinter.Click += (_, _) => RunLinter(showPanel: true);
        tools.Items.Add(runLinter);
        var refreshStats = new MenuItem { Header = "Refresh Statistics" };
        refreshStats.Click += (_, _) => RefreshStatistics();
        tools.Items.Add(refreshStats);
        tools.Items.Add(new Separator());
        tools.Items.Add(Cmd("Settings…", CommandIds.AppSettings));

        var geometry = new MenuItem { Header = "_Geometry" };
        geometry.Items.Add(Cmd("Build Geometry", CommandIds.BuildGeometry));
        var livePreview = new MenuItem { Header = "Live CSG Preview", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        livePreview.Click += (_, _) =>
        {
            if (_buildController is not null)
            {
                _buildController.LivePreviewEnabled = !_buildController.LivePreviewEnabled;
                livePreview.IsChecked = _buildController.LivePreviewEnabled;
            }
        };
        geometry.Items.Add(livePreview);
        geometry.Items.Add(BuildMethodMenu());
        geometry.Items.Add(new Separator());
        var checkHoles = new MenuItem { Header = "Check for Holes (draw hole lines)" };
        checkHoles.Click += (_, _) => _ = CheckHolesAsync();
        geometry.Items.Add(checkHoles);
        var removeHoleLines = new MenuItem { Header = "Remove Hole Lines" };
        removeHoleLines.Click += (_, _) => { _holeLines = new List<Ged.Rendering.Scene.LineSegment>(); RefreshSelectionOverlay(); };
        geometry.Items.Add(removeHoleLines);
        var carve = new MenuItem { Header = "Carve (selected → intersecting)" };
        carve.Click += (_, _) => CarveSelected();
        geometry.Items.Add(carve);

        var level = new MenuItem { Header = "_Level" };
        level.Items.Add(Cmd("Calculate Lightmaps", CommandIds.BuildLightmapUvs));
        level.Items.Add(Cmd("Calculate Lighting (No Shadows)", CommandIds.BuildLightingNoShadows));
        level.Items.Add(Cmd("Calculate Lighting", CommandIds.BuildRelight));
        level.Items.Add(new Separator());
        level.Items.Add(Cmd("Calculate Maps and Light (No Shadows)", CommandIds.BuildMapsAndLightNoShadows));
        level.Items.Add(Cmd("Calculate Maps and Light", CommandIds.BuildMapsAndLight));
        level.Items.Add(new Separator());
        level.Items.Add(BuildLightmapMethodMenuItem(level)); // feature 1: stay-open method picker (flyout)
        level.Items.Add(new Separator());
        level.Items.Add(Cmd("Remove Lightmaps", CommandIds.BuildRemoveLightmaps));
        var previewLighting = new MenuItem { Header = "Preview Lighting", ToggleType = MenuItemToggleType.CheckBox, IsChecked = false };
        previewLighting.Click += (_, _) =>
        {
            _previewLighting = !_previewLighting;
            previewLighting.IsChecked = _previewLighting;
            if (_buildController is { } bc)
            {
                bc.PreviewLightingEnabled = _previewLighting; // arms the auto-relight debounce
            }

            ApplyPreviewLighting();
        };
        level.Items.Add(new Separator());
        level.Items.Add(previewLighting);

        var help = new MenuItem { Header = "_Help" };
        var discord = new MenuItem { Header = "Join the Community Discord" };
        discord.Click += (_, _) => OpenExternalLink(Services.HelpReference.DiscordUrl, "Discord invite");
        help.Items.Add(discord);
        help.Items.Add(new Separator());
        help.Items.Add(Cmd("Help Topics", CommandIds.HelpTopics));
        help.Items.Add(Cmd("About Glacier", CommandIds.HelpAbout));

        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(select);
        menu.Items.Add(view);
        menu.Items.Add(tools);
        menu.Items.Add(geometry);
        menu.Items.Add(level);
        menu.Items.Add(BuildScriptsMenu());
        menu.Items.Add(help);
        return menu;
    }

    private Control BuildToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Avalonia.Thickness(4, 2),
        };

        Button ToolBtn(string label, string commandId, string tip)
        {
            var btn = new Button
            {
                Content = label,
                Padding = new Avalonia.Thickness(8, 3),
                FontSize = 12,
                [ToolTip.TipProperty] = tip,
            };
            btn.Click += (_, _) => { _dispatcher.Invoke(commandId); AfterCommandFromMenu(commandId); };
            return btn;
        }

        // The three mutually-exclusive viewport tools (item 11): Select (default) | Draw
        // Brush | Ruler. Each is a ToggleButton whose highlight tracks the active tool via
        // UpdateToolButtons(); clicking one routes through the exclusive tool state machine.
        Avalonia.Controls.Primitives.ToggleButton ToolToggle(string label, string commandId, string tip)
        {
            var btn = new Avalonia.Controls.Primitives.ToggleButton
            {
                Content = label,
                Padding = new Avalonia.Thickness(8, 3),
                FontSize = 12,
                [ToolTip.TipProperty] = tip,
            };
            btn.Click += (_, _) => _dispatcher.Invoke(commandId);
            return btn;
        }

        panel.Children.Add(ToolBtn("Save", CommandIds.FileSave, "Save the level"));
        panel.Children.Add(ToolBtn("Build", CommandIds.BuildGeometry, "Build geometry"));
        _selectButton = ToolToggle("Select", CommandIds.ToolSelect,
            "Select tool (default): click to pick, drag to marquee-select. Deactivating Draw/Ruler returns here.");
        _selectButton.IsChecked = true;
        panel.Children.Add(_selectButton);
        _drawButton = ToolToggle("Draw Brush", CommandIds.BrushDraw,
            "Interactively draw a box brush: click the base point (face or grid), rubber-band the rectangle, click, extrude the height, click to create. ESC cancels.");
        panel.Children.Add(_drawButton);
        _rulerButton = ToolToggle("📏 Ruler", CommandIds.ToolRuler,
            "Measure: click two snap-aware points to create a persistent dimension annotation. ESC exits.");
        panel.Children.Add(_rulerButton);
        panel.Children.Add(new Border { Width = 1, Margin = new Avalonia.Thickness(4, 2), Background = Avalonia.Media.Brushes.Gray });

        // Grid / rotation increment pickers (item 4), immediately left of the magnet:
        // quick-select preset popover + free entry; the grid picker is wider (1/32 m
        // labels). Hotkeys [ / ] and Shift+[ / Shift+] step the same shared ladders.
        panel.Children.Add(Controls.IncrementFlyout.MakeDropDown(_gridIncrement, minWidth: 118));
        panel.Children.Add(Controls.IncrementFlyout.MakeDropDown(_rotationIncrement, minWidth: 86));

        panel.Children.Add(BuildMagnetSplitButton());

        // Manipulator tool cycle + Local/World, next to the magnet.
        _gizmoLocalBtn = GizmoToggle("⊞ Local", CommandIds.GizmoLocalWorld, "Gizmo axes follow the selection (Local) vs world (World). Persists.");
        _gizmoMoveBtn = GizmoToggle("↔ Move", CommandIds.GizmoMove, "Move tool — drag the axis arrows or plane quads.");
        _gizmoRotateBtn = GizmoToggle("⟳ Rotate", CommandIds.GizmoRotate, "Rotate tool — drag a ring.");
        _gizmoScaleBtn = GizmoToggle("⤢ Scale", CommandIds.GizmoScale, "Scale tool — drag an axis box or the centre.");
        panel.Children.Add(_gizmoMoveBtn);
        panel.Children.Add(_gizmoRotateBtn);
        panel.Children.Add(_gizmoScaleBtn);
        panel.Children.Add(_gizmoLocalBtn);
        UpdateGizmoToolButtons();
        panel.Children.Add(new Border { Width = 1, Margin = new Avalonia.Thickness(4, 2), Background = Avalonia.Media.Brushes.Gray });

        // Selection-filter chips: what a click can select. Plain click = only this kind
        // (switches to its mode); Ctrl+click = add this kind for simultaneous multi-pick.
        panel.Children.Add(FilterChip("Brushes", Ged.Core.Editing.SelectKinds.Brushes));
        panel.Children.Add(FilterChip("Faces", Ged.Core.Editing.SelectKinds.Faces));
        panel.Children.Add(FilterChip("Edges", Ged.Core.Editing.SelectKinds.Edges));
        panel.Children.Add(FilterChip("Vertices", Ged.Core.Editing.SelectKinds.Vertices));
        panel.Children.Add(FilterChip("Objects", Ged.Core.Editing.SelectKinds.Objects));
        panel.Children.Add(FilterChip("Groups", Ged.Core.Editing.SelectKinds.Groups));
        UpdateFilterChips();

        panel.Children.Add(new Border { Width = 1, Margin = new Avalonia.Thickness(4, 2), Background = Avalonia.Media.Brushes.Gray });

        // Procedural play icons: a green play glyph, doubled for multi, plus an amber
        // diamond badge for the "…from camera" variants (original artwork — no game art).
        panel.Children.Add(PlayToolBtn(Ged.Rendering.Graphics.PlayIcon.Level, CommandIds.FilePlayLevel, "Play Level (F7)"));
        panel.Children.Add(PlayToolBtn(Ged.Rendering.Graphics.PlayIcon.FromCamera, CommandIds.FilePlayFromCamera, "Play Level from Camera (F8)"));
        panel.Children.Add(PlayToolBtn(Ged.Rendering.Graphics.PlayIcon.Multi, CommandIds.FilePlayMulti, "Play in Multi (F9, Alpine launcher only)"));
        panel.Children.Add(PlayToolBtn(Ged.Rendering.Graphics.PlayIcon.MultiFromCamera, CommandIds.FilePlayMultiFromCamera, "Play in Multi from Camera (F10, Alpine launcher only)"));

        return new Border
        {
            Child = panel,
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            BorderBrush = Avalonia.Media.Brushes.Gray,
        };
    }

    /// <summary>
    /// A selection-filter chip. Plain-click makes its kind the sole selectable kind and
    /// switches to the matching mode (classic RED exclusive behaviour); Ctrl+click
    /// toggles the kind into/out of the filter for simultaneous multi-kind picking
    /// without changing the mode. The default toggle is suppressed — the checked state
    /// is driven entirely by <see cref="UpdateFilterChips"/>.
    /// </summary>
    private Control FilterChip(string label, Ged.Core.Editing.SelectKinds kind)
    {
        var chip = new Avalonia.Controls.Primitives.ToggleButton
        {
            Content = label,
            Padding = new Avalonia.Thickness(8, 3),
            FontSize = 12,
            [ToolTip.TipProperty] =
                $"Select {label} — click for only this kind (switches mode); Ctrl+click to add it for multi-pick.",
        };
        chip.AddHandler(PointerPressedEvent, (_, e) =>
        {
            e.Handled = true; // suppress the built-in toggle; we drive IsChecked ourselves
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                ToggleAdditionalFilter(kind);
            }
            else
            {
                SetMode(Ged.Core.Editing.SelectionFilter.ModeFor(kind));
            }
        }, RoutingStrategies.Tunnel);

        _filterChips[kind] = chip;
        return chip;
    }

    /// <summary>Ctrl+click a chip: add/remove a kind for multi-pick without changing mode.</summary>
    private void ToggleAdditionalFilter(Ged.Core.Editing.SelectKinds kind)
    {
        _filter.ToggleAdditional(kind);
        ClearInvalidSelection(); // a kind toggled off drops its now-unpickable selection
        UpdateFilterChips();
        RebuildScene(); // brush id-buffer granularity may change with the enabled kinds
        RefreshSelectionOverlay();
        _dispatcher.ShowMessage($"Selection filter: {_filter.Active}");
    }

    /// <summary>Reflects the filter state onto the chips (lit = allowed, bold = the mode's chip).</summary>
    private void UpdateFilterChips()
    {
        Ged.Core.Editing.SelectKinds primary = Ged.Core.Editing.SelectionFilter.PrimaryKindFor(_filter.Mode);
        foreach ((Ged.Core.Editing.SelectKinds kind, Avalonia.Controls.Primitives.ToggleButton chip) in _filterChips)
        {
            chip.IsChecked = _filter.Allows(kind);
            chip.FontWeight = kind == primary ? FontWeight.Bold : FontWeight.Normal;
        }

        _session.ActiveSelectKinds = _filter.Active;
    }

    /// <summary>
    /// A playtest toolbar button whose glyph is drawn procedurally by
    /// <see cref="Ged.Rendering.Graphics.PlayIconRenderer"/> (play / play+diamond /
    /// doubled / doubled+diamond) — original artwork, no game art.
    /// </summary>
    private Button PlayToolBtn(Ged.Rendering.Graphics.PlayIcon icon, string commandId, string tip)
    {
        var btn = new Button
        {
            Content = MakePlayIconImage(icon),
            Padding = new Avalonia.Thickness(6, 3),
            [ToolTip.TipProperty] = tip,
        };
        btn.Click += (_, _) => { _dispatcher.Invoke(commandId); AfterCommandFromMenu(commandId); };
        return btn;
    }

    /// <summary>Renders a play-icon RGBA image (via the shared renderer) into an Avalonia Image control.</summary>
    private static Image MakePlayIconImage(Ged.Rendering.Graphics.PlayIcon icon)
    {
        int size = Ged.Rendering.Graphics.PlayIconRenderer.Size;
        byte[] rgba = Ged.Rendering.Graphics.PlayIconRenderer.Render(icon, size);
        byte[] png = Ged.Core.IO.Tex.PngWriter.Encode(size, size, rgba);
        using var ms = new MemoryStream(png);
        return new Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(ms),
            Width = 18,
            Height = 18,
        };
    }

    private MenuItem BuildRecentMenu()
    {
        var recent = new MenuItem { Header = "Open _Recent" };
        if (_settings.RecentFiles.Count == 0)
        {
            recent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        else
        {
            foreach (string path in _settings.RecentFiles)
            {
                string captured = path;
                var item = new MenuItem { Header = Path.GetFileName(path) };
                item.Click += async (_, _) => await OpenLevelFileAsync(captured);
                recent.Items.Add(item);
            }
        }

        return recent;
    }

    private Control BuildStatusBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Avalonia.Thickness(8, 4) };
        panel.Children.Add(_statusMode);

        // The grid / rotate readouts are interactive (item 4): click → increment popover.
        panel.Children.Add(IncrementStatusButton(_statusGrid, _gridIncrement));
        panel.Children.Add(IncrementStatusButton(_statusRotate, _rotationIncrement));

        _statusIsolation.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x40));
        _statusIsolation.FontWeight = FontWeight.Bold;
        foreach (TextBlock t in new[] { _statusSnap, _statusSpeed, _statusCoords, _statusRoom, _statusIsolation, _statusSelection, _statusFps, _statusMessage })
        {
            panel.Children.Add(t);
        }

        // Mount status (item 7): clickable → Settings when not mounted.
        var mountBtn = new Button
        {
            Content = _statusMount,
            Padding = new Avalonia.Thickness(4, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Red Faction install mount status — click to configure",
        };
        mountBtn.Click += (_, _) => ShowSettings();
        panel.Children.Add(mountBtn);

        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x25)), Child = panel };
    }

    /// <summary>Wraps a status readout in a click target that opens the increment popover.</summary>
    private static Control IncrementStatusButton(TextBlock readout, Ged.App.Viewport.IncrementSetting setting)
    {
        var button = new Button
        {
            Content = readout,
            Padding = new Avalonia.Thickness(2, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Flyout = Controls.IncrementFlyout.Build(setting),
        };
        Avalonia.Controls.ToolTip.SetTip(button, $"Click to change the {setting.Label.ToLowerInvariant()} increment");
        return button;
    }

    // ---- Command wiring ----

    private void BindCommands()
    {
        _dispatcher.Bind(CommandIds.FileNew, () => _ = NewLevelAsync());
        _dispatcher.Bind(CommandIds.FileOpen, () => _ = OpenDialogAsync());
        _dispatcher.Bind(CommandIds.FileSave, () => _ = SaveAsync(false), () => Document is not null);
        _dispatcher.Bind(CommandIds.FileSaveAs, () => _ = SaveAsync(true), () => Document is not null);

        _dispatcher.Bind(CommandIds.EditUndo, () => { Document?.Undo.Undo(); AfterMutation(); }, () => Document?.Undo.CanUndo == true);
        _dispatcher.Bind(CommandIds.EditRedo, () => { Document?.Undo.Redo(); AfterMutation(); }, () => Document?.Undo.CanRedo == true);
        _dispatcher.Bind(CommandIds.EditCopy, () => Document?.CopySelection(), () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.EditCut, () => { Document?.CutSelection(); AfterMutation(); }, () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.EditPaste, () => { Document?.Paste(); AfterMutation(); }, () => Document?.HasClipboard == true);
        _dispatcher.Bind(CommandIds.EditDelete, () => { Document?.DeleteSelection(); AfterMutation(); }, () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.EditProperties, () => { _properties.Refresh(); _dispatcher.ShowMessage("Properties"); });
        _dispatcher.Bind(CommandIds.EditLevelProperties, ShowLevelProperties);

        _dispatcher.Bind(CommandIds.SelectInvert, () => _session.Selection.InvertObjects());
        _dispatcher.Bind(CommandIds.SelectAll, () => _session.Selection.SelectAllObjects());
        _dispatcher.Bind(CommandIds.SelectByUid, () => _ = SelectByUidAsync());

        _dispatcher.Bind(CommandIds.VisHideSelected, () => { Document?.HideSelected(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisUnhideBrushes, () => { Document?.UnhideAllBrushes(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisHideObjects, () => { Document?.HideAllObjects(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisUnhideObjects, () => { Document?.UnhideAllObjects(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisInvertHidden, () => { Document?.InvertHidden(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisHideExcept, () => { Document?.HideExceptClutterEntities(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisUnhideExcept, () => { Document?.UnhideExceptClutterEntities(); RebuildScene(); });
        _dispatcher.Bind(CommandIds.VisLock, () => Document?.LockSelected());
        _dispatcher.Bind(CommandIds.VisUnlockAll, () => Document?.UnlockAll());

        _dispatcher.Bind(CommandIds.View1Pane, () => _viewportGrid.SetLayout(1));
        _dispatcher.Bind(CommandIds.View2Pane, () => _viewportGrid.SetLayout(2));
        _dispatcher.Bind(CommandIds.View4Pane, () => _viewportGrid.SetLayout(4));
        _dispatcher.Bind(CommandIds.ViewMaximize, () => _viewportGrid.ToggleMaximize());
        _dispatcher.Bind(CommandIds.ViewResetLayout, () => _viewportGrid.ResetLayout());
        _dispatcher.Bind(CommandIds.ViewCyclePanes, () => _viewportGrid.SetLayout(_viewportGrid.LayoutMode == 4 ? 1 : _viewportGrid.LayoutMode + 1));
        _dispatcher.Bind(CommandIds.ViewCyclePanesBack, () => _viewportGrid.SetLayout(_viewportGrid.LayoutMode == 1 ? 4 : _viewportGrid.LayoutMode - 1));
        _dispatcher.Bind(CommandIds.ViewShowLinks, () => { _settings.ShowLinks = !_settings.ShowLinks; _session.ShowLinks = _settings.ShowLinks; RebuildScene(); Persist(); _renderOptions?.NotifyChanged(); });
        _dispatcher.Bind(CommandIds.ViewShowAllRanges, () => SetShowAllRanges(!_settings.ShowAllRanges));
        _dispatcher.Bind(CommandIds.ViewDisableBackfaceCulling, () => SetDisableBackfaceCulling(!_settings.DisableBackfaceCulling));
        _dispatcher.Bind(CommandIds.ViewPortalFacesNone, () => SetPortalFaces(Ged.Rendering.Scene.PortalFaceDrawMode.None));
        _dispatcher.Bind(CommandIds.ViewPortalFacesSeeThru, () => SetPortalFaces(Ged.Rendering.Scene.PortalFaceDrawMode.SeeThru));
        _dispatcher.Bind(CommandIds.ViewPortalFacesOpaque, () => SetPortalFaces(Ged.Rendering.Scene.PortalFaceDrawMode.Opaque));
        _dispatcher.Bind(CommandIds.ViewIsolateSelection, ToggleIsolation, () => Document is not null);

        // Hotkey stepping walks the shared preset ladders (item 4) — same values as the
        // status-bar popovers and the pane toolbar pickers.
        _dispatcher.Bind(CommandIds.GridBrightness, CycleGridBrightness);
        _dispatcher.Bind(CommandIds.GridSizeUp, () => _gridIncrement.StepUp());
        _dispatcher.Bind(CommandIds.GridSizeDown, () => _gridIncrement.StepDown());
        _dispatcher.Bind(CommandIds.GridRotationUp, () => _rotationIncrement.StepUp());
        _dispatcher.Bind(CommandIds.GridRotationDown, () => _rotationIncrement.StepDown());
        _dispatcher.Bind(CommandIds.ToggleSnap, () => SetSnapEnabled(!_settings.SnapEnabled));

        _dispatcher.Bind(CommandIds.CameraGotoPlayerStart, GoToPlayerStart);
        _dispatcher.Bind(CommandIds.CameraTeleportToObject, FrameSelection);
        _dispatcher.Bind(CommandIds.CameraTeleportXyz, () => _ = TeleportXyzAsync());

        _dispatcher.Bind(CommandIds.BuildGeometry, () => _ = _buildController!.BuildAsync(), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildLightmapUvs, () => _ = _buildController!.CalculateLightmapsAsync(), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildRelight, () => _ = _buildController!.CalculateLightingAsync(shadows: true), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildLightingNoShadows, () => _ = _buildController!.CalculateLightingAsync(shadows: false), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildMapsAndLight, () => _ = _buildController!.CalculateMapsAndLightAsync(shadows: true), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildMapsAndLightNoShadows, () => _ = _buildController!.CalculateMapsAndLightAsync(shadows: false), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildRemoveLightmaps, () => _buildController!.RemoveLightmaps(), () => Document is not null);
        _dispatcher.Bind(CommandIds.BuildCalcPaths, () => _ = CalculateNavPathsAsync(), () => Document is not null);

        _dispatcher.Bind(CommandIds.AppCommandPalette, () => _ = new CommandPalette(_dispatcher).ShowDialog(this));
        _dispatcher.Bind(CommandIds.AppSettings, ShowSettings);
        _dispatcher.Bind(CommandIds.HelpTopics, OpenHelpReference);
        _dispatcher.Bind(CommandIds.HelpAbout, () => Dialogs.AboutDialog.ShowFor(this));

        BindObjectCommands();
    }

    /// <summary>
    /// Preview Lighting: show the lit result and, on small levels, immediately
    /// re-bake (no shadows) using the exact CPU kernel so light placement is visible
    /// live before a full bake. Larger levels switch to the lit view and prompt for
    /// an explicit Calculate Lighting.
    /// </summary>
    private void ApplyPreviewLighting()
    {
        if (_buildController is null || Document is null)
        {
            return;
        }

        if (!_previewLighting)
        {
            _dispatcher.ShowMessage("Preview Lighting off.");
            return;
        }

        _viewportGrid.ForEachSurface(s =>
        {
            if (s.Mode == RenderMode.JustTextures)
            {
                s.Mode = RenderMode.TexturesAndLightmaps;
            }
        });

        int brushes = _session.BrushEditor?.Brushes.Count ?? 0;
        if (brushes > 0 && brushes <= GeometryBuildController.LivePreviewBrushLimit)
        {
            _dispatcher.ShowMessage("Preview Lighting: baking (no shadows)…");
            _ = _buildController.CalculateLightingAsync(shadows: false, preview: true);
        }
        else
        {
            _dispatcher.ShowMessage("Preview Lighting on — run Calculate Lighting (Shift+L) to bake this level.");
        }
    }

    private void WireViewport()
    {
        _viewportGrid.ForEachSurface(s =>
        {
            s.Picked += (id, additive) => OnPicked(s, id, additive);
            s.StatsUpdated += OnStats;
        });
        _viewportGrid.ActivePaneChanged += _ => UpdateStatusStatics();
    }

    // ---- Keyboard / dispatch ----

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        CoreGesture? g = GestureConvert.FromAvalonia(e.Key, e.KeyModifiers);
        if (g is not CoreGesture gesture)
        {
            return;
        }

        bool hasModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
        object? focused = FocusManager?.GetFocusedElement();

        // TAB is the viewport maximize/restore toggle whenever the pointer is over any
        // viewport pane or focus sits inside one (a native pane with Win32 focus never
        // reaches this tunnel — its keys route through ViewportSurface.OnKey). A pointer
        // parked on a viewport wins even while a panel/text box has focus. Otherwise
        // TAB is left alone so Avalonia performs normal focus traversal.
        if ((e.Key == Key.Tab) && !hasModifier)
        {
            if (_viewportGrid.TabTargetsViewport(focused) && _dispatcher.Dispatch(gesture))
            {
                e.Handled = true;
                AfterMutation();
            }

            return;
        }

        // A composited OpenGL viewport pane with keyboard focus drives its OWN keys through
        // the shared gesture router (transform keys M/R/N, ESC cancels, arrow nudges, held-key
        // camera navigation and gated hotkey dispatch) — exactly as the Direct3D 11 native pane
        // does via its WndProc. Let the key bubble on to the pane's OnKeyDown instead of
        // dispatching it here, so a gesture never fires twice. (A native D3D11 pane never
        // reaches this tunnel: its Win32 child window owns focus, so this guard is a no-op there.)
        if (focused is Viewport.GlViewportSurface)
        {
            return;
        }

        bool typing = focused is TextBox;
        if (typing && !hasModifier)
        {
            return; // let text input through
        }

        // A focused graph canvas owns its own keys (pan/zoom, edge delete) so global
        // gestures (Delete, Space=build) don't steal them from the graph panels.
        if (focused is Controls.GraphCanvas)
        {
            return;
        }

        if (TryTextureModeKey(gesture))
        {
            e.Handled = true;
            return;
        }

        if (_dispatcher.Dispatch(gesture))
        {
            e.Handled = true;
            AfterMutation();
        }
    }

    // ---- Command implementations ----

    private async Task NewLevelAsync()
    {
        // A new level needs the VFS exactly like an opened one (default brush textures,
        // the palette's clutter/item catalogs, mesh previews) — mount first, prompting only
        // when no valid install is configured (the same behaviour as File ▸ Open). The
        // startup mount usually makes this a no-op.
        await EnsureVfsAsync();

        Ged.Rendering.Scene.RenderScene scene = _session.NewLevel();
        LoadSceneIntoViewports(scene);
        Title = "Glacier — untitled.rfl";
        SubscribeDocument();
        ApplyMode(_filter.Mode, announce: false); // re-apply the persisted selection filter to the new document
        RefreshPanels();
        RefreshStatistics();
        // Fog comes from the DOCUMENT's level properties: re-apply so a foggy previously-open
        // level doesn't leave its fog on the fresh document's viewports.
        ApplyFog();
        _dispatcher.ShowMessage("New level.");
    }

    private async Task OpenDialogAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Red Faction Level",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Red Faction Level (.rfl)") { Patterns = new[] { "*.rfl" } } },
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is string path)
        {
            await OpenLevelFileAsync(path);
        }
    }

    public async Task OpenLevelFileAsync(string path)
    {
        try
        {
            await EnsureVfsAsync();
            RecoveryOutcome? recovery = await MaybeRecoverAsync(path);
            string loadPath = recovery?.LoadPath ?? path;
            if (recovery is { DeleteAutosaveNow: true })
            {
                TryDeleteAutosave(path + ".autosave.rfl");
            }

            Ged.Rendering.Scene.RenderScene scene = _session.OpenLevel(loadPath);
            if (recovery is not null)
            {
                _session.Document!.Path = path; // save always targets the ORIGINAL, never the autosave
            }

            // When the autosave was opened, delete it only after the next successful save.
            _pendingAutosaveDelete = recovery is { DeleteAutosaveOnSave: true } ? path + ".autosave.rfl" : null;
            LoadSceneIntoViewports(scene);
            SubscribeDocument();
            LoadSidecarInto(path); // B7 annotations + feature-1 lighting method (editor-only sidecar)
            ApplyMode(_filter.Mode, announce: false); // re-apply the persisted selection filter to the opened document
            RefreshPanels();
            RefreshStatistics();
            ApplyIconAtlas();
            ApplyFog();
            ApplyBackfaceCulling();
            SetEmitterAnimation(_session.AnimateEmitters);
            MaybeWarnLegacyLevel();

            _settings.PushRecent(path);
            Persist();
            UpdateTitle();
            _statusMessage.Text =
                $"{scene.Batches.Count} batches, {scene.TotalTriangleCount:N0} tris, " +
                $"{Document?.Objects.Count ?? 0} objects" +
                (_session.Vfs is null ? "  (no textures: RF install not set)" : string.Empty);
        }
        catch (Exception ex)
        {
            _statusMessage.Text = $"Open failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves the level RED-style: writing NEVER bakes lighting or recompiles geometry — the level is
    /// serialized exactly as it was last built, so stale compiled geometry / stale lightmaps are the
    /// author's to rebuild (Geometry ▸ Build, Build ▸ Calculate Lighting), just like stock RED. The SOLE
    /// exception is GED's unsealed live-CSG preview geometry, which the seal guard re-seals with a
    /// geometry-only build before writing. Returns true when the file was written, false when the save
    /// was cancelled (no path chosen) OR aborted by the seal guard (a build is in flight, or the re-seal
    /// did not complete) — callers such as the playtest launcher use the result to abort a
    /// save-before-launch flow.
    /// </summary>
    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (Document is null)
        {
            return false;
        }

        string? path = Document.Path;
        if (saveAs || path is null)
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Level As",
                DefaultExtension = "rfl",
                SuggestedFileName = Path.GetFileName(path ?? "untitled.rfl"),
                FileTypeChoices = new[] { new FilePickerFileType("Red Faction Level (.rfl)") { Patterns = new[] { "*.rfl" } } },
            });
            path = file?.TryGetLocalPath();
        }

        if (path is null)
        {
            return false;
        }

        // GED writes Alpine v305 always (Goober's format policy) — there is no version
        // target to choose. A loaded pre-305 level is upgraded to v305 on save
        // (EditorDocument.Save / SaveAs → RflFile.UpgradeToAlpine), exactly as Alpine
        // RED does. Save As only picks a new path.

        // The header timestamp bumps to "now" on every real save that carries a change,
        // but a no-op Save of an untouched file stays byte-identical (the timestamp is
        // preserved). "Changed" = document edits OR pending geometry/lighting rebakes;
        // captured before the bake below since MarkSaved will clear IsDirty. Save As is
        // always a fresh artifact, so it always stamps.
        bool modified = saveAs
            || Document.IsDirty
            || (_buildController is { GeometryDirty: true } or { GeometryIsPreview: true } or { LightingDirty: true });

        // RED-style save (owner decision — "RED-style + seal guard"). Writing NEVER bakes lighting or
        // recompiles geometry: stock RED serializes exactly what was last built, and Glacier now matches.
        // Stale compiled geometry / stale lightmaps are the author's to rebuild (Geometry ▸ Build,
        // Build ▸ Calculate Lighting); a merely-dirty save proceeds as-is, nudged by a Hint.
        //
        // The ONE exception is GED's live-CSG / merged-stash PREVIEW: it applied UNSEALED geometry into
        // the document (a state RED never has — the fast path skips the t-joint seal, leaving thousands
        // of open edges), so persisting it would ship a level that sparkles / leaks in-game. The SEAL
        // GUARD re-seals that with a geometry-only build (no lighting bake) before writing, and is the
        // SOLE rebuild trigger and the SOLE path that can abort a save.
        //
        // Captured before the seal build / write, which clear these flags.
        bool wasPreview = _buildController?.GeometryIsPreview ?? false;
        bool wasGeometryDirty = _buildController?.GeometryDirty ?? false;
        bool wasLightingDirty = _buildController?.LightingDirty ?? false;

        SaveGuard.SaveNotice notice;
        if (_buildController is { } build && SaveGuard.RequiresSeal(wasPreview))
        {
            _dispatcher.ShowMessage("Re-sealing preview geometry before save…");
            bool sealBuildRan = await build.BuildAsync(); // interactive geometry-only re-seal — no lighting bake

            // Only the seal path can abort. A refused seal (another user build in flight) or one that
            // completes with the geometry still unsealed writes NOTHING and tells the user why.
            switch (SaveGuard.EvaluateSeal(sealBuildRan, build.GeometryDirty, build.GeometryIsPreview))
            {
                case SaveGuard.PreSaveOutcome.AbortBuildRunning:
                    _notifications.Notify(Services.NotificationSeverity.Warning,
                        "Save aborted — a build is already running. Retry when it finishes.");
                    return false;
                case SaveGuard.PreSaveOutcome.AbortSealIncomplete:
                    _notifications.Notify(Services.NotificationSeverity.Warning,
                        "Save aborted — the geometry re-seal did not complete.");
                    return false;
            }

            notice = SaveGuard.SaveNotice.GeometryResealed;
        }
        else
        {
            // Merely dirty ⇒ save as-is (RED-style); one nudge per save, geometry winning over lighting.
            notice = SaveGuard.NoticeForDirtySave(wasGeometryDirty, wasLightingDirty);
        }

        // Pre-save linter summary: budget/compatibility violations are surfaced
        // (informational — GED always saves Alpine v305, which supports every feature).
        Ged.Core.Linting.LintReport lint = RunLinter(showPanel: false);
        if (!lint.IsClean)
        {
            _lintPanel.Show(lint, FrameUid);
            if (lint.HasBlockingIssues)
            {
                _dispatcher.ShowMessage($"Pre-save linter: {lint.Blocking.Count} save-target violation(s) — see Linter panel.");
            }
        }

        try
        {
            if (saveAs)
            {
                Document.SaveAs(path, updateTimestamp: true);
            }
            else
            {
                // Writes Alpine v305 (upgrading a loaded pre-305 level in place). The
                // header stamps only when something actually changed, so a no-op Save of
                // an untouched v305 file stays byte-identical (the v305-source invariant).
                Document.Save(path, updateTimestamp: modified);
            }

            SaveSidecarFor(path); // B7 annotations + feature-1 lighting method (editor-only)

            // A successful save supersedes a recovered autosave — remove it now (item 18).
            if (_pendingAutosaveDelete is string pending)
            {
                TryDeleteAutosave(pending);
                _pendingAutosaveDelete = null;
            }

            _settings.PushRecent(path);
            Persist();
            UpdateTitle();
            AfterMutation();
            _statusMessage.Text = $"Saved {Path.GetFileName(path)} [Alpine v{Document.Rfl.Header.Version}]";

            // RED-style advisory (owner decision): one nudge per save about staleness / re-seal.
            switch (notice)
            {
                case SaveGuard.SaveNotice.UnbuiltGeometry:
                    _notifications.Notify(Services.NotificationSeverity.Hint,
                        "Saved with unbuilt geometry changes — rebuild when ready.");
                    break;
                case SaveGuard.SaveNotice.UnbakedLighting:
                    _notifications.Notify(Services.NotificationSeverity.Hint,
                        "Saved with unbaked lighting changes — bake when ready.");
                    break;
                case SaveGuard.SaveNotice.GeometryResealed:
                    _notifications.Notify(Services.NotificationSeverity.Info,
                        "Geometry re-sealed for save — lightmaps were reset; bake lighting when ready.");
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            _notifications.Notify(Services.NotificationSeverity.Error, $"Save failed: {ex.Message}");
            return false;
        }
    }

    private async Task SelectByUidAsync()
    {
        if (Document is null)
        {
            return;
        }

        string? text = await InputDialog.ShowAsync(this, "Select By UID", "Enter object UID:");
        if (int.TryParse(text, out int uid))
        {
            LevelObject? o = _session.Selection.SelectObjectByUid(uid);
            _statusMessage.Text = o is null ? $"No object with UID {uid}" : $"Selected {o.DisplayName}";
        }
    }

    private async Task TeleportXyzAsync()
    {
        string? text = await InputDialog.ShowAsync(this, "Teleport Camera", "Enter X Y Z:", "0 0 0");
        string[] parts = (text ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            _viewportGrid.ActiveSurface.ViewFrom(new Vector3(x, y, z));
        }
    }

    private void GoToPlayerStart()
    {
        LevelObject? start = Document?.Objects.FirstOrDefault(o => o.Kind == LevelObjectKind.PlayerStart);
        if (start is not null)
        {
            _viewportGrid.ActiveSurface.FramePoint(_session.PositionOf(start));
        }
        else
        {
            _dispatcher.ShowMessage("No player start in this level.");
        }
    }

    private void FrameSelection()
    {
        LevelObject? o = Document?.Selection.FirstOrDefault();
        if (o is not null)
        {
            _viewportGrid.ActiveSurface.FramePoint(_session.PositionOf(o));
        }
    }

    private void ShowLevelProperties()
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        var dlg = new LevelPropertiesDialog(Document);
        dlg.Closed += (_, _) => AfterMutation();
        dlg.Show(this);
    }

    private void ShowSettings()
    {
        var dlg = new SettingsDialog(_settings, _keymap, _registry, ApplySettings, ApplyRfInstall);
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// Sets the single global camera scheme (View ▸ Camera Scheme / Settings ▸ Input):
    /// propagates to every pane, persists, and keeps the radio submenu in sync.
    /// </summary>
    private void SetCameraScheme(CameraSchemeKind kind)
    {
        _settings.CameraScheme = (int)kind;
        _viewportGrid.SetScheme(kind);
        SyncCameraSchemeMenu();
        Persist();
        UpdateStatusStatics();
    }

    private void SyncCameraSchemeMenu()
    {
        foreach ((MenuItem mi, CameraSchemeKind k) in _cameraSchemeItems)
        {
            mi.IsChecked = (int)k == _settings.CameraScheme;
        }
    }

    private void ApplySettings()
    {
        // Item 2: switch at the APPLICATION level (matching startup) so every top-level — the main
        // window plus any floated dock tool-windows, which inherit the app variant — re-themes live.
        // The Dock chrome brushes now re-resolve on this change (see ThemeResources).
        Avalonia.Styling.ThemeVariant variant =
            _settings.DarkTheme ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = variant;
        }

        RequestedThemeVariant = variant;
        _viewportGrid.SetScheme((CameraSchemeKind)_settings.CameraScheme);
        SyncCameraSchemeMenu();
        _viewportGrid.ForEachSurface(s => s.CameraSpeed = _settings.CameraSpeed);
        _session.GridBrightness = _settings.GridBrightness;
        _session.GridSize = _settings.GridSize;
        _session.ShowLinks = _settings.ShowLinks;
        _session.ShowEventArrows = _settings.ShowEventArrows;
        _session.ShowAllRanges = _settings.ShowAllRanges;

        // The Settings dialog edits the same globals the pane dropdown/pickers show.
        _renderOptions.NotifyChanged();
        _gridIncrement.NotifyChanged();
        _rotationIncrement.NotifyChanged();

        ApplyElementColors();
        ApplyIconAtlas();
        ApplyFog();
        ApplyBackfaceCulling();
        RebuildScene();
        UpdateStatusStatics();
    }

    private void CycleGridBrightness()
    {
        _settings.GridBrightness = _settings.GridBrightness >= 1.5f ? 0.4f : _settings.GridBrightness + 0.3f;
        _session.GridBrightness = _settings.GridBrightness;
        RebuildScene();
        Persist();
        _statusMessage.Text = $"Grid brightness {_settings.GridBrightness:0.0}";
    }

    /// <summary>Applies a new grid size everywhere (settings, session grid display, snap consumers).</summary>
    private void SetGridSize(float value)
    {
        _settings.GridSize = Math.Clamp(value, Ged.Core.Editing.SnapIncrements.GridMin, Ged.Core.Editing.SnapIncrements.GridMax);
        _session.GridSize = _settings.GridSize;
        RebuildScene();
        Persist();
        UpdateStatusStatics();
    }

    /// <summary>Applies a new rotation increment everywhere (settings, snap consumers).</summary>
    private void SetRotationStep(float value)
    {
        _settings.RotationStep = Math.Clamp(value, Ged.Core.Editing.SnapIncrements.RotationMin, Ged.Core.Editing.SnapIncrements.RotationMax);
        Persist();
        UpdateStatusStatics();
    }

    // ---- Scene / panels / status ----

    private void LoadSceneIntoViewports(Ged.Rendering.Scene.RenderScene scene)
    {
        _viewportGrid.LoadScene(scene, _session.Vfs, scene.SuggestedCameraPosition, scene.SuggestedCameraTarget);
        _lastPick = PickId.None;
        _lastPickHighlight = PickId.None;
        RefreshSelectionOverlay();
    }

    private void RebuildScene()
    {
        if (Document is null)
        {
            return;
        }

        Ged.Rendering.Scene.RenderScene scene = _session.BuildScene();
        AppendTransformIndicatorLabel(scene); // live Δ/∠/% label while a gizmo drag is active
        _viewportGrid.RefreshScene(scene, _session.Vfs);
        RefreshSelectionOverlay();
    }

    private void SubscribeDocument()
    {
        if (Document is null)
        {
            return;
        }

        _links = new LinkService(Document);
        _prefabInstances = new PrefabInstanceService(Document);
        _prefabInstances.InstancesChanged += () =>
        {
            // An instance may have been orphaned / re-instantiated away: drop stale unit state.
            if (_prefabUnit?.ValidateExisting() == true)
            {
                RefreshSelectionOverlay();
            }

            _properties.Refresh();
            _outliner.Refresh();
        };
        InitPrefabUnit(); // Feature F: prefab-instance unit-selection controller for this document
        _metadata = new Ged.Core.Editing.GedObjectMetadataService(Document); // item 4: light cookies + future per-object metadata
        _navGraph = new NavGraphService(Document);
        _groups = new GroupService(Document);
        _movers = new MoverService(Document);
        _cutscenes = new CutsceneService(Document);
        Document.ObjectsChanged += () => { _outliner.Refresh(); _history.Refresh(); _linkGraph.Refresh(); _depGraph.Refresh(); };
        // Item 8 (tiered refresh): a selection change is a LIGHTWEIGHT overlay update — the
        // selected object's highlight box + range/region wireframe are drawn in the selection
        // overlay (RefreshSelectionOverlay), NOT by re-emitting + re-uploading the whole scene.
        // Full scene rebuilds are reserved for document/structural/build changes below.
        Document.SelectionChanged += () =>
        {
            _properties.Refresh();

            // Almost every selection change is a lightweight overlay update. The lone exception is
            // decals: a selected decal's facing face is a FILLED portal-style quad baked into the
            // emitted scene (the line-only selection overlay can't carry it), so adding OR removing
            // a decal from the selection must re-emit. Decals are few, so the common path still
            // avoids the rebuild. RebuildScene refreshes the selection overlay itself.
            bool decalNow = SelectionHasDecal();
            bool needRebuild = decalNow || _selectionHadDecal;
            _selectionHadDecal = decalNow;
            if (needRebuild)
            {
                RebuildScene();
            }
            else
            {
                RefreshSelectionOverlay();
            }

            _linkGraph.Refresh();
            RefreshGroupPanelIfActive();
            UpdateGizmoState();
        };
        Document.VisibilityChanged += () => _outliner.Refresh();
        Document.DirtyChanged += () => { UpdateTitle(); _history.Refresh(); };
        Document.LinksChanged += () => { RebuildScene(); _linkGraph.Refresh(); _properties.Refresh(); };
        Document.AnnotationsChanged += () => { RebuildScene(); _outliner.Refresh(); SaveAnnotationSidecar(); };

        if (_session.BrushEditor is { } be)
        {
            be.BrushesChanged += () =>
            {
                // A pure edit of known brushes (LastChangedBrushUids != null) flags the owning
                // prefab instance "modified" — structural changes (create/delete/reorder/import,
                // which report null) never do, so placement + propagation don't self-flag (item 1).
                if (be.LastChangedBrushUids is { } changed && _prefabInstances is { } pf)
                {
                    foreach (int uid in changed)
                    {
                        pf.MarkMemberModified(uid);
                    }
                }

                RebuildScene();
                _history.Refresh();
                _layers.Refresh();
            };
            be.VisibilityChanged += () => { RebuildScene(); _layers.Refresh(); }; // item 9: brush lock/hide
            be.SelectionChanged += () =>
            {
                _properties.Refresh(); // brush selection drives the brush inspector
                RefreshSelectionOverlay();
                UpdateStatusStatics();
                UpdateGizmoState();
                _layers.SyncSelectionHighlight(); // item 8: highlight-only, no full row rebuild
                if (TextureToolsActive)
                {
                    RefreshTexturePanelSelection();
                }
            };
        }

        _layers.Refresh();
        _buildController?.Attach();
    }

    private void CarveSelected()
    {
        if (_session.BrushEditor is not { } be)
        {
            _dispatcher.ShowMessage("Carve needs a selected brush (the cutter).");
            return;
        }

        int carved = be.CarveSelected();
        _dispatcher.ShowMessage(carved > 0
            ? $"Carved {carved} brush(es)."
            : "Carve: the selected brush intersects nothing.");
        if (carved > 0)
        {
            AfterMutation();
        }
    }

    private void ShowBuildReport(string operation, BuildReport r)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"{(operation == "Lighting" ? "Build + lighting" : "Build")} complete in {r.ElapsedMs:0} ms");
        lines.AppendLine($"Rooms {r.Rooms}  (subrooms {r.Subrooms})   Portals {r.Portals}");
        lines.AppendLine($"Faces {r.Faces}   Face-verts {r.FaceVertices}   Vertices {r.Vertices}");
        lines.AppendLine($"Brushes {r.Brushes}   Surfaces {r.Surfaces}   Lightmap pages {r.LightmapPages}   UIDs {r.Uids}");
        if (r.Messages.Count > 0)
        {
            lines.AppendLine();
            lines.AppendLine($"Warnings ({r.Messages.Count}):");
            foreach (BuildMessage m in r.Messages)
            {
                lines.AppendLine($"  [{m.Severity}] {m.Text}");
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            _logOutput.Append(operation, lines.ToString());
            RefreshStatistics();
        });
    }

    /// <summary>Item 4: appends a tagged block to the Log output panel (thread-safe).</summary>
    internal void LogOperation(string operation, string text) =>
        Dispatcher.UIThread.Post(() => _logOutput.Append(operation, text));

    private void MaybeWarnLegacyLevel()
    {
        if (Document is null || _settings.SuppressLegacyWarning)
        {
            return;
        }

        int version = Document.Rfl.Header.Version;
        if (version < Ged.Core.Editing.SaveTargets.FirstAlpineVersion)
        {
            _dispatcher.ShowMessage(
                $"Legacy level (v{version}, pre-Alpine). GED opens it read-write; saving to Alpine (v305) upgrades it. " +
                "Disable this notice in Settings ▸ General.");
        }
    }

    private void RefreshPanels()
    {
        _outliner.Refresh();
        _properties.Refresh();
        _history.Refresh();
        _linkGraph.Refresh();
        _depGraph.Refresh();
    }

    private void AfterMutation()
    {
        RebuildScene();
        RefreshPanels();
    }

    /// <summary>Arms the texture eyedropper: the next face pick samples its texture (item 6).</summary>
    public void ArmTextureEyedropper(Action<string> onSampled)
    {
        _textureSampleCallback = onSampled;
        _dispatcher.ShowMessage("Eyedropper: click a brush face to sample its texture.");
    }

    private void OnPicked(Viewport.IViewportSurface surface, PickId id, bool additive)
    {
        _lastPick = id;
        _lastPickHighlight = PickId.None; // item (a): only an accepted in-mode selection lights the pick

        // Texture eyedropper (item 6): the armed next-click samples the clicked face's
        // texture into the caller (the Face-properties field), consuming the pick.
        if (_textureSampleCallback is { } sample)
        {
            _textureSampleCallback = null;
            if (_session.TryResolveBrushFace(id, out int suid, out int sface) &&
                BrushEd?.TextureNameOf(suid, sface) is string tex)
            {
                sample(tex);
                _dispatcher.ShowMessage($"Picked texture {tex}");
            }
            else
            {
                _dispatcher.ShowMessage("Eyedropper: that was not a brush face — nothing sampled.");
            }

            return;
        }

        // Feature F: prefab-instance unit selection intercepts BEFORE per-kind selection — a click
        // on a tracked member selects the whole instance as a unit (double-click / badge enters it).
        // Returns false (fall through) for non-members and for members while inside their instance.
        if (HandlePrefabPick(surface, id, additive))
        {
            return;
        }

        if (HandleModePick(surface, id, additive))
        {
            // An in-mode brush/face/vertex selection lights the pick; an empty-click CLEAR leaves
            // id == None, so BuildSelectionLines(None) draws nothing (item (a)).
            _lastPickHighlight = id;
            RefreshSelectionOverlay();
            return;
        }

        // Strict mode-scoped document selection (item 5): out-of-mode pick kinds are
        // ignored even when they are the topmost id-buffer hit — the click is a no-op
        // instead of a cross-mode selection. Object/Mesh picks are level objects and
        // need the Objects (or Groups) chip. PickKind.Brush here is either an editable
        // brush (NOT a document object — Object mode must not select it) or a mover's
        // geometry (a real level object): movers stay clickable in Object mode, and
        // Group mode accepts any brush pick as a group member (stock behaviour).
        if (Document is not null)
        {
            bool objectsAllowed = _filter.Allows(Ged.Core.Editing.SelectKinds.Objects)
                || _filter.Allows(Ged.Core.Editing.SelectKinds.Groups);
            LevelObject? o = _session.ObjectForPick(id);
            if (o is not null && Ged.App.Services.PickGate.AllowsDocumentSelect(
                    _filter.Active, id.Kind, o.Kind == Ged.Core.Editor.LevelObjectKind.Mover))
            {
                _lastPickHighlight = id; // in-mode object selection lights the pick (item (a))
                _session.Selection.SelectObject(o, additive);
            }
            else if (id.IsNone && objectsAllowed)
            {
                Document.ClearSelection();
            }
        }

        RefreshSelectionOverlay();
    }

    private void OnStats(double fps, Vector3 cameraPosition)
    {
        _statusFps.Text = $"fps: {fps:F0}";
        _statusCoords.Text = $"cam: {cameraPosition.X:F1}, {cameraPosition.Y:F1}, {cameraPosition.Z:F1}";
        int room = _session.RoomIdAt(cameraPosition);
        _statusRoom.Text = room >= 0 ? $"Room: {room}" : "Room: —";
    }

    private void UpdateStatusStatics()
    {
        Ged.Core.Editing.EditMode mode = _session.BrushEditor?.Mode ?? Ged.Core.Editing.EditMode.Object;
        _statusMode.Text = $"Mode: {mode}  [{(_coordLocal ? "Local" : "Global")}]";
        _statusGrid.Text = $"Grid: {_settings.GridSize:0.###} m";
        _statusRotate.Text = $"Rotate: {_settings.RotationStep:0} deg";
        _statusSpeed.Text = $"Cam: {_settings.CameraSpeed:0} m/s  [{CameraSchemes.DisplayName(_viewportGrid.ActiveSurface.SchemeKind)}]";
        UpdateIsolationStatus();
        SyncSnapPolicy();
    }

    /// <summary>Copies grid/rotation/scale/enabled from settings into the shared snap policy + status.</summary>
    private void SyncSnapPolicy()
    {
        _snap.Enabled = _settings.SnapEnabled;
        _snap.GridSize = _settings.GridSize;
        _snap.RotationStepDegrees = _settings.RotationStep;
        _snap.ScaleStep = _settings.ScaleStep;
        _snap.Kinds = (Ged.Core.Editing.SnapKinds)_settings.SnapKinds;

        _statusSnap.Text = _settings.SnapEnabled
            ? $"Snap: On · {SnapKindsLabel()}"
            : "Snap: Off";
        if (_magnetButton is not null && _magnetButton.IsChecked != _settings.SnapEnabled)
        {
            _magnetButton.IsChecked = _settings.SnapEnabled;
        }
    }

    private void SetSnapEnabled(bool on)
    {
        if (_settings.SnapEnabled == on)
        {
            SyncSnapPolicy();
            return;
        }

        _settings.SnapEnabled = on;
        SyncSnapPolicy();
        _dispatcher.ShowMessage(on ? "Snap on (magnet)" : "Snap off");
        Persist();
    }

    private Avalonia.Controls.Primitives.ToggleButton GizmoToggle(string label, string commandId, string tip)
    {
        var btn = new Avalonia.Controls.Primitives.ToggleButton
        {
            Content = label,
            Padding = new Avalonia.Thickness(7, 3),
            FontSize = 12,
            [ToolTip.TipProperty] = tip,
        };
        btn.Click += (_, _) => _dispatcher.Invoke(commandId);
        return btn;
    }

    /// <summary>Keeps the toolbar tool toggles + Local/World + Show-Gizmo menu in sync with the gizmo state.</summary>
    partial void UpdateGizmoToolButtons()
    {
        if (_gizmoMoveBtn is null || _gizmoRotateBtn is null || _gizmoScaleBtn is null || _gizmoLocalBtn is null)
        {
            return;
        }

        _gizmoMoveBtn.IsChecked = GizmoVisible && ActiveGizmoTool == Ged.Core.Editing.GizmoTool.Move;
        _gizmoRotateBtn.IsChecked = GizmoVisible && ActiveGizmoTool == Ged.Core.Editing.GizmoTool.Rotate;
        _gizmoScaleBtn.IsChecked = GizmoVisible && ActiveGizmoTool == Ged.Core.Editing.GizmoTool.Scale;
        _gizmoMoveBtn.IsEnabled = GizmoToolEnabled(Ged.Core.Editing.GizmoTool.Move);
        _gizmoRotateBtn.IsEnabled = GizmoToolEnabled(Ged.Core.Editing.GizmoTool.Rotate);
        _gizmoScaleBtn.IsEnabled = GizmoToolEnabled(Ged.Core.Editing.GizmoTool.Scale);
        _gizmoLocalBtn.IsChecked = GizmoLocal;
        _gizmoLocalBtn.Content = GizmoLocal ? "⊞ Local" : "⊞ World";
        // Keep the pane-dropdown "Show Gizmo" checkbox in sync (item 4).
        _renderOptions?.NotifyChanged();
    }

    private void UpdateTitle()
    {
        string name = Document?.Path is string p ? Path.GetFileName(p) : "untitled.rfl";
        string dirty = Document?.IsDirty == true ? "*" : string.Empty;
        Title = $"Glacier — {dirty}{name}";
    }

    // ---- Live previews, room modes, linter/statistics ----

    /// <summary>
    /// Sets the room-graph render scoping (the Room Rendering chooser, relocated to each
    /// perspective pane's render-mode dropdown — item 4) and notifies the pane checkboxes.
    /// </summary>
    private void SetRoomMode(RoomVisibility mode)
    {
        _session.RoomMode = mode;
        _session.CameraPosition = _viewportGrid.ActiveSurface.CameraPosition;
        RebuildScene();
        _renderOptions?.NotifyChanged();
    }

    /// <summary>
    /// Sets the portal-face draw mode (the Portal Faces chooser, relocated to each perspective
    /// pane's render-mode dropdown — item 4; also bound to the ViewPortalFaces* commands). Keeps
    /// the pane radio buttons in sync.
    /// </summary>
    private void SetPortalFaces(Ged.Rendering.Scene.PortalFaceDrawMode mode)
    {
        _session.PortalFaces = mode;
        _settings.PortalFaceMode = (int)mode;
        RebuildScene();
        Persist();
        _renderOptions?.NotifyChanged();
    }

    /// <summary>
    /// Toggles the global "Show all ranges" visualization (light-range / region
    /// spheres for every object, not just the selection). Keeps the settings, session,
    /// and the pane-dropdown checkbox in sync whether toggled from the dropdown or the command.
    /// </summary>
    private void SetShowAllRanges(bool on)
    {
        _settings.ShowAllRanges = on;
        _session.ShowAllRanges = on;
        _renderOptions.NotifyChanged(); // keep the pane dropdown checkboxes in sync
        RebuildScene();
        Persist();
    }

    private void SetEmitterAnimation(bool on)
    {
        if (on)
        {
            _emitterStart = DateTime.UtcNow;
            _emitterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _emitterTimer.Tick -= OnEmitterTick;
            _emitterTimer.Tick += OnEmitterTick;
            _emitterTimer.Start();
        }
        else
        {
            _emitterTimer?.Stop();
        }
    }

    private void OnEmitterTick(object? sender, EventArgs e)
    {
        if (Document is null || !_session.AnimateEmitters)
        {
            return;
        }

        _session.EmitterTime = (float)(DateTime.UtcNow - _emitterStart).TotalSeconds;
        _session.CameraPosition = _viewportGrid.ActiveSurface.CameraPosition;

        // Drive in-shader liquid UV scroll off the same animation clock (no rebuild needed).
        _viewportGrid.ForEachSurface(s => s.AnimationTime = _session.EmitterTime);
        RebuildScene();
    }

    private void ApplyFog()
    {
        Ged.Rendering.FogSettings fog = _session.GetFog(_settings.ShowFog, _settings.FarClip);
        _viewportGrid.ForEachSurface(s => s.Fog = fog);
    }

    private void SetDisableBackfaceCulling(bool on)
    {
        _settings.DisableBackfaceCulling = on;
        _renderOptions.NotifyChanged(); // keep the pane dropdown checkboxes in sync
        ApplyBackfaceCulling();
        Persist();
    }

    /// <summary>Pushes the back-face-cull toggle to every viewport (a pure raster state; no rebuild).</summary>
    private void ApplyBackfaceCulling() =>
        _viewportGrid.ForEachSurface(s => s.DisableBackfaceCulling = _settings.DisableBackfaceCulling);

    private void ApplyIconAtlas()
    {
        _session.UseOriginalIcons = _settings.UseOriginalIcons && _session.Vfs is not null;
        // The Alpine object icons live in alpinefaction.vpp beside the launcher (item 3): feed
        // the current launcher path in so the composition can read them. Re-read each rebuild,
        // so a launcher-path change refreshes the icons live via ApplySettings.
        _session.AlpineLauncherPath = string.IsNullOrWhiteSpace(_settings.GameExePath) ? null : _settings.GameExePath;
        byte[]? atlas = null;
        try
        {
            atlas = _session.BuildIconAtlas(_session.UseOriginalIcons);
            GpuHost.Device.SetIconAtlas(atlas);
        }
        catch (Exception)
        {
            // Renderer unavailable (headless) — the GED default atlas stays; harmless.
        }

        // Keep the palette row glyphs in lockstep with the viewport billboards: original
        // icons render untinted from the composed atlas, GED's own set uses the per-kind
        // tint. Re-resolving is a cheap CPU blit, so a settings flip updates the rows live.
        Services.PaletteIcons.Configure(_session.UseOriginalIcons ? atlas : null, _session.UseOriginalIcons);
        _palette.RefreshIcons();
    }

    private void ApplyElementColors()
    {
        _session.LinkColor = ParseColor(_settings.ColorLinks);
        _session.BoundingBoxColor = ParseColor(_settings.ColorBoundingBox);
        _session.PathNodeColor = ParseColor(_settings.ColorNodes);
        _session.RegionColor = ParseColor(_settings.ColorRegions);
        _session.PortalFaceColor = ParseColor(_settings.ColorBrushPortal);
    }

    private static uint? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        try
        {
            Color c = Color.Parse(hex);
            return Ged.Rendering.Scene.Palette.Rgba(c.R, c.G, c.B, c.A);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private SaveTarget CurrentSaveTarget() =>
        Document is null ? SaveTarget.Alpine : SaveTargets.FromVersion(Document.Rfl.Header.Version);

    private Ged.Core.Linting.LintReport RunLinter(bool showPanel)
    {
        if (Document is null)
        {
            return new Ged.Core.Linting.LintReport(System.Array.Empty<Ged.Core.Linting.LintFinding>());
        }

        var options = new Ged.Core.Linting.LintOptions
        {
            Target = CurrentSaveTarget(),
            Vfs = _session.Vfs,
            ScanOptions = _session.BuildScanOptions(),
            MaxTextureDimension = 1024,
        };
        Ged.Core.Linting.LintReport report = Ged.Core.Linting.LevelLinter.Lint(Document.Rfl, options);
        _lintPanel.Show(report, FrameUid);
        _statusMessage.Text = report.Summary();
        return report;
    }

    private async Task CheckHolesAsync()
    {
        if (_buildController is null)
        {
            return;
        }

        await _buildController.CheckHolesAsync();
        var lines = new List<Ged.Rendering.Scene.LineSegment>();
        uint red = Ged.Rendering.Scene.Palette.Rgba(255, 40, 40, 255);
        foreach (Ged.Core.Model.Vec3 h in _buildController.HoleLocations)
        {
            var c = new Vector3(h.X, h.Y, h.Z);
            lines.Add(new Ged.Rendering.Scene.LineSegment(c - new Vector3(0.5f, 0, 0), c + new Vector3(0.5f, 0, 0), red));
            lines.Add(new Ged.Rendering.Scene.LineSegment(c - new Vector3(0, 0.5f, 0), c + new Vector3(0, 0.5f, 0), red));
            lines.Add(new Ged.Rendering.Scene.LineSegment(c - new Vector3(0, 0, 0.5f), c + new Vector3(0, 0, 0.5f), red));
        }

        _holeLines = lines;
        RefreshSelectionOverlay();

        // Jump the camera to the first leak.
        if (_buildController.HoleLocations.Count > 0)
        {
            Ged.Core.Model.Vec3 first = _buildController.HoleLocations[0];
            _viewportGrid.ActiveSurface.FramePoint(new Vector3(first.X, first.Y, first.Z));
        }
    }

    private void ToggleSelectedLightsEditorOnly()
    {
        if (Document is null)
        {
            return;
        }

        int[] lightUids = Document.Selection
            .Where(o => o.Kind == Ged.Core.Editor.LevelObjectKind.Light)
            .Select(o => o.Uid)
            .ToArray();
        if (lightUids.Length == 0)
        {
            _dispatcher.ShowMessage("Select one or more lights first.");
            return;
        }

        int moved = lightUids.Count(uid => Ged.Core.Editing.LightRelocation.Toggle(Document, uid));
        RebuildScene();
        RefreshPanels();
        _dispatcher.ShowMessage($"Relocated {moved} light(s) between the runtime and editor-only sections.");
    }

    private void RefreshStatistics()
    {
        if (Document is null)
        {
            return;
        }

        Ged.Core.Linting.LevelStatistics stats = Ged.Core.Linting.LevelStatisticsBuilder.Compute(Document.Rfl);
        _statsPanel.Show(stats, CurrentSaveTarget());
    }

    // ---- Lifecycle / IO plumbing ----

    private async void OnOpened(object? sender, EventArgs e)
    {
        _viewportGrid.ForEachSurface(s => s.CameraSpeed = _settings.CameraSpeed);
        Viewport.IViewportSurface first = _viewportGrid.ActiveSurface;

        // Startup stamp: which viewport surface backend the panes actually instantiated
        // (Direct3D 11 native panes, or the composited OpenGL panes). This is the verifiable
        // proof that the Renderer=OpenGL setting switched the panes app-wide.
        string surfaceKind = first is Viewport.GlViewportSurface ? "OpenGL (composited)" : "Direct3D 11 (native)";
        CrashHandler.LogInfo("viewport", $"panes instantiated: {surfaceKind}; Renderer setting = {_settings.Renderer}");

        if (first.InitError is not null)
        {
            _statusMessage.Text = $"Renderer init failed: {first.InitError}";
        }

        if (Ged.Core.AppPaths.UsingProfileFallback)
        {
            // The exe directory is not writable (e.g. installed under Program Files), so
            // settings.cfg/keymap.cfg/logs/cache fell back to the user profile. Warn once
            // so settings never appear to be lost silently.
            _dispatcher.ShowMessage(
                "This folder is read-only — settings, logs and cache are being stored under your user profile (%APPDATA%/%LOCALAPPDATA%\\Glacier) instead of next to the app.");
        }

        if (!_settings.FirstRunComplete)
        {
            await new Dialogs.FirstRunWizard(_settings, _keymap).ShowDialog(this);
            KeymapStore.Save(_keymap);
            ApplySettings();
            Persist();
        }

        // Mount the configured install NOW (quietly) so the palette Clutter/Items tabs, the
        // asset browser and the icon atlas are populated before any document exists — a fresh
        // launch + File ▸ New previously never mounted, leaving them all empty.
        MountConfiguredInstallAtStartup();

        if (_initialOpenPath is not null && File.Exists(_initialOpenPath))
        {
            string path = _initialOpenPath;
            _initialOpenPath = null;
            await OpenLevelFileAsync(path);
        }
        else
        {
            await MaybeRecoverUntitledCrashAsync();
        }
    }

    /// <summary>
    /// Offers to recover an emergency autosave from a crash while editing an UNSAVED
    /// level (saved levels are recovered by <see cref="MaybeRecoverAsync"/> on re-open).
    /// The crash handler writes those into the portable <c>recovery\</c> directory.
    /// </summary>
    private async Task MaybeRecoverUntitledCrashAsync()
    {
        try
        {
            string recoveryDir = Ged.Core.AppPaths.RecoveryDirectory;
            if (!Directory.Exists(recoveryDir))
            {
                return;
            }

            string? newest = Directory.EnumerateFiles(recoveryDir, "untitled-*.autosave.rfl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return;
            }

            string? choice = await InputDialog.ShowAsync(this, "Recover Crashed Session",
                "An unsaved level was emergency-saved after a crash. Type 'yes' to open it:", "yes");
            if (string.Equals(choice?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Ged.Rendering.Scene.RenderScene scene = _session.OpenLevel(newest);
                _session.Document!.Path = null; // still an untitled document (Save As to keep it)
                LoadSceneIntoViewports(scene);
                SubscribeDocument();
                ApplyMode(_filter.Mode, announce: false);
                RefreshPanels();
                _statusMessage.Text = "Recovered crashed session (unsaved — use Save As).";
            }

            // Either way, retire the recovery file so it is offered only once.
            try
            {
                File.Delete(newest);
            }
            catch (Exception ex)
            {
                CrashHandler.LogNonFatal("recovery-cleanup", ex);
            }
        }
        catch (Exception ex)
        {
            CrashHandler.LogNonFatal("untitled-recovery", ex);
        }
    }

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeConfirmed && _settings.PromptForSave && Document?.IsDirty == true)
        {
            e.Cancel = true;
            SaveCloseChoice choice = await PromptSaveOnCloseAsync();
            switch (choice)
            {
                case SaveCloseChoice.Cancel:
                    return; // stay open
                case SaveCloseChoice.Save:
                    await SaveAsync(saveAs: false);
                    if (Document?.IsDirty == true)
                    {
                        return; // save cancelled/blocked — stay open
                    }

                    break;
                case SaveCloseChoice.Discard:
                    break;
            }

            _closeConfirmed = true;
            Persist();
            KeymapStore.Save(_keymap);
            Close();
            return;
        }

        Persist();
        KeymapStore.Save(_keymap);
    }

    private enum SaveCloseChoice
    {
        Save,
        Discard,
        Cancel,
    }

    private async Task<SaveCloseChoice> PromptSaveOnCloseAsync()
    {
        var tcs = new TaskCompletionSource<SaveCloseChoice>();
        var dialog = new Window
        {
            Title = "Unsaved Changes",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        Button MakeButton(string text, SaveCloseChoice choice, bool primary = false)
        {
            var b = new Button { Content = text, MinWidth = 90, IsDefault = primary };
            b.Click += (_, _) => { tcs.TrySetResult(choice); dialog.Close(); };
            return b;
        }

        string name = Document?.Path is string p ? Path.GetFileName(p) : "untitled.rfl";
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"Save changes to {name} before closing?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        MakeButton("Save", SaveCloseChoice.Save, primary: true),
                        MakeButton("Discard", SaveCloseChoice.Discard),
                        MakeButton("Cancel", SaveCloseChoice.Cancel),
                    },
                },
            },
        };
        dialog.Closing += (_, _) => tcs.TrySetResult(SaveCloseChoice.Cancel);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task EnsureVfsAsync()
    {
        if (_session.Vfs is not null)
        {
            return;
        }

        string? dir = _settings.RfInstallDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            dir = await PromptForRfInstallAsync();
        }

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            // Mount + refresh every consumer via the VfsChanged event (item 7).
            ApplyRfInstall(dir);
        }
    }

    private async Task<string?> PromptForRfInstallAsync()
    {
        IStorageFolder? suggested = null;
        // Neutral start location: open beside the configured Alpine Faction launcher when one
        // is set, otherwise let the picker open at the OS default.
        string? launcher = _settings.GameExePath;
        if (!string.IsNullOrWhiteSpace(launcher))
        {
            string? launcherDir = Path.GetDirectoryName(launcher);
            if (!string.IsNullOrEmpty(launcherDir) && Directory.Exists(launcherDir))
            {
                suggested = await StorageProvider.TryGetFolderFromPathAsync(launcherDir);
            }
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate your Red Faction install (for textures/meshes)",
            AllowMultiple = false,
            SuggestedStartLocation = suggested,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // In-app placeable drag (mesh / prefab / catalog class) dropped into a viewport (item E).
        bool placeable = e.Data.Contains(PlaceableDrag.Format);
        LogOperation("DragDrop", $"drop received: format={(placeable ? "yes" : "no")}");
        if (placeable && e.Data.Get(PlaceableDrag.Format) is string descriptor)
        {
            HandlePlaceableDrop(descriptor, e);
            return;
        }

        var paths = e.Data.GetFiles()?
            .Select(i => i.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList() ?? new List<string>();

        string? rfl = paths.FirstOrDefault(p => p.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase));
        if (rfl is not null)
        {
            await OpenLevelFileAsync(rfl);
            return;
        }

        // Drag-drop a group .rfg onto the viewport: import it at the placement point.
        string? rfg = paths.FirstOrDefault(p => p.EndsWith(".rfg", StringComparison.OrdinalIgnoreCase));
        if (rfg is not null && Document is not null)
        {
            try
            {
                Ged.Core.IO.Rfg.RfgFile group = Ged.Core.IO.Rfg.RfgFile.Load(rfg);
                var placed = RfgInterop.Import(Document, group, PlacementPoint);
                AfterMutation();
                _dispatcher.ShowMessage($"Imported {placed.Count} object(s) from {Path.GetFileName(rfg)}.");
            }
            catch (Exception ex)
            {
                _dispatcher.ShowMessage($"Import .rfg failed: {ex.Message}");
            }
        }
    }

    // ---- Drag a placeable asset out into a viewport (item E) ------------------

    /// <summary>Makes a tile a drag source that places <paramref name="descriptor"/> where it is dropped.</summary>
    private void WirePlaceableDrag(Control tile, string descriptor) =>
        PlaceableDrag.WireSource(tile, () => descriptor,
            onPress: () => _assetPreview.Cancel(),           // a press suppresses the hover popover
            onLog: msg => LogOperation("DragDrop", msg));

    /// <summary>
    /// Places the dragged asset at the drop point: a ray through the pane pixel under the cursor is
    /// cast at the geometry (<see cref="EditorSession.TryRayFaceHit"/>) and the asset lands on the hit
    /// face; with no hit (or a drop outside any pane) it falls back to the in-front-of-camera
    /// placement point. One undo transaction + a status message per drop.
    /// </summary>
    private void HandlePlaceableDrop(string descriptor, DragEventArgs e)
    {
        if (Document is null)
        {
            LogOperation("DragDrop", "placed: none (no level open)");
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        if (!PlaceableDrag.TryParse(descriptor, out PlaceableKind kind, out string arg1, out string? arg2))
        {
            LogOperation("DragDrop", $"placed: none (unparsable descriptor '{descriptor}')");
            return;
        }

        (Ged.Core.Model.Vec3 point, bool onSurface) = ResolveDropPoint(e);
        string where = onSurface ? "on the surface" : "in front of the camera";

        switch (kind)
        {
            case PlaceableKind.Prefab:
                LogOperation("DragDrop", $"placed: prefab '{Path.GetFileNameWithoutExtension(arg1)}' {where}");
                PlacePrefabAt(arg1, point, $"at the drop point ({where})");
                break;
            case PlaceableKind.Mesh:
                PlaceDroppedObject(LevelObjectKind.MeshObject, arg1, point, where);
                break;
            case PlaceableKind.Class:
                if (Enum.TryParse(arg1, out LevelObjectKind objKind))
                {
                    PlaceDroppedObject(objKind, arg2, point, where);
                }
                else
                {
                    LogOperation("DragDrop", $"placed: none (unknown kind '{arg1}')");
                }

                break;
        }
    }

    private void PlaceDroppedObject(LevelObjectKind kind, string? className, Ged.Core.Model.Vec3 point, string where)
    {
        try
        {
            _lastPlacedKind = kind;
            _lastPlacedClass = className;
            LevelObject? placed = Document!.PlaceObject(kind, point, className);
            OnObjectPlaced(placed);
            LogOperation("DragDrop", placed is null ? "placed: none" : $"placed: uid={placed.Uid} ({placed.Kind}) {where}");
            if (placed is not null)
            {
                _dispatcher.ShowMessage($"Placed {placed.Kind} {where} (uid {placed.Uid}).");
            }
        }
        catch (Exception ex)
        {
            LogOperation("DragDrop", $"placed: none (error: {ex.Message})");
            _dispatcher.ShowMessage($"Place failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a top-level drop position to a world placement point: the pane under the cursor is
    /// found and a ray through its pixel is cast at the geometry; the hit point (if any) is returned
    /// with <c>onSurface = true</c>. With no geometry hit (a miss, or a drop outside any pane) the
    /// in-front-of-camera placement point is used (the item-A convention).
    /// </summary>
    private (Ged.Core.Model.Vec3 Point, bool OnSurface) ResolveDropPoint(DragEventArgs e)
    {
        if (!TryResolveDropPane(e, out IViewportSurface surface, out int px, out int py))
        {
            LogOperation("DragDrop", "pane resolved: none — camera fallback");
            return (PlacementPoint, false);
        }

        LogOperation("DragDrop", $"pane resolved: {surface.ViewType} @ px ({px},{py})");
        if (surface.PixelRay(px, py) is (Vector3 origin, Vector3 dir) &&
            _session.TryRayFaceHit(origin, dir, out Ged.Core.Model.Vec3 hit, out _))
        {
            LogOperation("DragDrop", $"ray hit: ({hit.X:0.##}, {hit.Y:0.##}, {hit.Z:0.##})");
            return (hit, true);
        }

        LogOperation("DragDrop", "ray hit: none — camera fallback");
        return (PlacementPoint, false);
    }

    /// <summary>
    /// Finds the viewport pane whose render surface is under the drop, returning its physical pixel.
    /// The drop position is read relative to each pane's surface control (DIP) and scaled by the
    /// render scaling to the device pixels the native surface's pixel-ray math expects. Panes not
    /// currently in the visual tree (maximized layout) are skipped.
    /// </summary>
    private bool TryResolveDropPane(DragEventArgs e, out IViewportSurface surface, out int px, out int py)
    {
        surface = null!;
        px = py = 0;
        double scale = this.GetVisualRoot()?.RenderScaling ?? 1.0;
        foreach (ViewportPane pane in _viewportGrid.Panes)
        {
            Control ctrl = pane.Surface.AsControl();
            if (ctrl.GetVisualRoot() is null)
            {
                continue; // detached pane (maximized layout)
            }

            Avalonia.Point local = e.GetPosition(ctrl);
            if (local.X < 0 || local.Y < 0 || local.X > ctrl.Bounds.Width || local.Y > ctrl.Bounds.Height)
            {
                continue;
            }

            (int w, int h) = pane.Surface.SurfaceSize;
            surface = pane.Surface;
            px = Math.Clamp((int)Math.Round(local.X * scale), 0, Math.Max(0, w - 1));
            py = Math.Clamp((int)Math.Round(local.Y * scale), 0, Math.Max(0, h - 1));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Serializes the open document for the crash handler's emergency save. Returns
    /// (null, null) when there is nothing to save or serialization fails — must never
    /// throw (it runs from the crash path).
    /// </summary>
    public (byte[]? Bytes, string? Path) TryGetEmergencyDocument()
    {
        try
        {
            if (Document is null)
            {
                return (null, null);
            }

            return (Document.SaveToBytes(updateTimestamp: false), Document.Path);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private void TryAutosave(bool force = false)
    {
        if (!_settings.AutosaveEnabled && !force)
        {
            return;
        }

        if (Document?.IsDirty != true || Document.Path is null)
        {
            return;
        }

        // Deferred while a camera/pointer drag is active (stock-parity nicety).
        bool interacting = false;
        _viewportGrid.ForEachSurface(s => interacting |= s.IsInteracting);
        if (interacting && !force)
        {
            return;
        }

        try
        {
            string autosavePath = Document.Path + ".autosave.rfl";
            File.WriteAllBytes(autosavePath, Document.SaveToBytes(updateTimestamp: false));
            if (!force)
            {
                _statusMessage.Text = $"Autosaved {Path.GetFileName(autosavePath)}";
            }
        }
        catch (Exception ex)
        {
            CrashHandler.LogNonFatal("autosave", ex);
            _notifications.Notify(Services.NotificationSeverity.Warning, $"Autosave failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Offers recovery when a newer autosave exists (item 18): shows the redesigned
    /// <see cref="Dialogs.RecoveryDialog"/> and returns the resolved outcome (which file to
    /// load, save-target and autosave disposal), or null when there is nothing to recover.
    /// This does NOT load anything itself — <see cref="OpenLevelFileAsync"/> performs the
    /// single load, so the earlier fragile double-load is gone.
    /// </summary>
    private async Task<RecoveryOutcome?> MaybeRecoverAsync(string path)
    {
        string autosavePath = path + ".autosave.rfl";
        if (!File.Exists(autosavePath))
        {
            return null;
        }

        try
        {
            if (File.GetLastWriteTimeUtc(autosavePath) <= File.GetLastWriteTimeUtc(path))
            {
                return null;
            }

            RecoveryChoice choice = await Dialogs.RecoveryDialog.ShowAsync(this, path, autosavePath);
            return RecoveryDecision.Resolve(path, autosavePath, choice);
        }
        catch (Exception)
        {
            return null; // any recovery failure falls back to opening the original
        }
    }

    private static void TryDeleteAutosave(string autosavePath)
    {
        try
        {
            if (File.Exists(autosavePath))
            {
                File.Delete(autosavePath);
            }
        }
        catch (Exception)
        {
            // Non-fatal — a stale autosave that can't be deleted is harmless.
        }
    }

    private void Persist() => SettingsStore.Save(_settings);

    // ---- Menu helpers ----

    private MenuItem Cmd(string header, string commandId)
    {
        var item = new MenuItem { Header = header };
        string gesture = _dispatcher.GestureLabel(commandId);
        if (!string.IsNullOrEmpty(gesture))
        {
            item.InputGesture = TryKeyGesture(gesture);
        }

        item.Click += (_, _) => { _dispatcher.Invoke(commandId); AfterCommandFromMenu(commandId); };
        return item;
    }

    /// <summary>Opens the offline HTML help reference (Help ▸ Help Topics / F1) in the default browser.</summary>
    private void OpenHelpReference()
    {
        string? path = Services.HelpReference.ResolvePath();
        if (path is null)
        {
            _dispatcher.ShowMessage("The help file (help.html) was not found next to the executable. Reinstall or restore it to view Help Topics.");
            return;
        }

        OpenExternalLink(path, "help reference");
    }

    /// <summary>Opens a URL or local file in the OS default handler (browser), toasting on failure.</summary>
    private void OpenExternalLink(string target, string label)
    {
        try
        {
            Services.HelpReference.OpenExternal(target);
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Could not open the {label}: {ex.Message}");
        }
    }

    /// <summary>
    /// Geometry-menu "Build Method" chooser: RED's authentic single accumulated shared BSP (the
    /// default after the owner-approved flip) vs GED's Incremental accumulator. The choice is persisted
    /// (<see cref="AppSettings.UseSharedBspBuild"/>), pushed to the build controller, and invalidates the current
    /// build so the next Build Geometry / save / hole-check recompiles with it. The Incremental method stays fully
    /// functional and selectable — nothing is removed.
    /// </summary>
    private MenuItem BuildMethodMenu()
    {
        var menu = new MenuItem { Header = "Build Method" };
        MenuItem shared = null!;
        MenuItem legacy = null!;

        void Pick(bool useShared)
        {
            _settings.UseSharedBspBuild = useShared;
            if (_buildController is { } bc)
            {
                bc.UseSharedBspBuild = useShared;
                bc.InvalidateGeometry();
            }

            if (shared.Icon is CheckBox scb)
            {
                scb.IsChecked = useShared;
            }

            if (legacy.Icon is CheckBox lcb)
            {
                lcb.IsChecked = !useShared;
            }

            Persist();
        }

        shared = new MenuItem
        {
            Header = "RED-authentic Shared BSP",
            Icon = new CheckBox { IsChecked = _settings.UseSharedBspBuild, IsHitTestVisible = false },
        };
        shared.Click += (_, _) => Pick(true);

        legacy = new MenuItem
        {
            Header = "Incremental",
            Icon = new CheckBox { IsChecked = !_settings.UseSharedBspBuild, IsHitTestVisible = false },
        };
        legacy.Click += (_, _) => Pick(false);

        menu.Items.Add(shared);
        menu.Items.Add(legacy);
        return menu;
    }

    private static MenuItem ToggleCmd(string header, bool initial, Action<bool> set)
    {
        var item = new MenuItem { Header = header, Icon = new CheckBox { IsChecked = initial, IsHitTestVisible = false } };
        item.Click += (_, _) =>
        {
            bool next = item.Icon is CheckBox cb && cb.IsChecked != true;
            if (item.Icon is CheckBox box)
            {
                box.IsChecked = next;
            }

            set(next);
        };
        return item;
    }

    private void AfterCommandFromMenu(string commandId)
    {
        // Structural/undo commands already refresh; nothing extra needed here.
    }

    partial void UpdateToolButtons()
    {
        if (_selectButton is not null)
        {
            _selectButton.IsChecked = _toolState.Active == ViewportTool.Select;
        }

        if (_drawButton is not null)
        {
            _drawButton.IsChecked = _toolState.Active == ViewportTool.Draw;
        }

        if (_rulerButton is not null)
        {
            _rulerButton.IsChecked = _toolState.Active == ViewportTool.Ruler;
        }
    }

    private static Avalonia.Input.KeyGesture? TryKeyGesture(string display)
    {
        try
        {
            return Avalonia.Input.KeyGesture.Parse(display);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static RenderMode ClampMode(int value) =>
        Enum.IsDefined(typeof(RenderMode), value) ? (RenderMode)value : RenderMode.TexturesAndLightmaps;

    private static TextBlock MakeStatus(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gainsboro,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
    };
}
