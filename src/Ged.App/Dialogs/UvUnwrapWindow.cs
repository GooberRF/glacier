using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Ged.App.Controls;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Brush = Ged.Core.Model.Brush;

namespace Ged.App.Dialogs;

/// <summary>
/// The UV Unwrap editor: a floating, custom-drawn 2D view of the selected face(s)
/// UV layout over their (tiled) texture. Supports vertex/edge/face selection (Ctrl add /
/// Alt remove), move/rotate/scale either by arrow-key increments or, while holding M/R/S,
/// by dragging a 2D gizmo at the selection centroid (Shift+S non-uniform scale), V/H flip,
/// Shift+V/Shift+H align, G grid-snap toggle, D/T decal-vs-texture display toggle, Show
/// Tiled, wheel/Shift+RMB zoom, Shift+LMB pan, and Print (save the current view to a .tga).
/// Every edit — including one completed gizmo drag — commits as one brush-undo entry.
/// </summary>
internal sealed class UvUnwrapWindow : Window
{
    private readonly BrushEditor _be;
    private readonly Func<string, Bitmap?> _loadTexture;
    private readonly Action _onCommitted;

    // Item 3b(4): outlines the hovered/selected face in the main 3D viewport. Null when the host did
    // not wire it. Called with an empty list to clear (hover ends / window closes).
    private readonly Action<IReadOnlyList<(int Uid, int Face)>>? _onHighlightFaces;

    private readonly List<(int Brush, int Face, int Corner)> _refs = new();
    private readonly List<Uv> _uvs = new();
    private readonly HashSet<int> _sel = new();

    // Per-face corner rings (indices into _uvs), rebuilt with the working set. Drives
    // polygon drawing and edge/face picking.
    private readonly List<IReadOnlyList<int>> _rings = new();

    // Per-ring identity (brush uid, face index, texture), parallel to _rings — feeds the per-face
    // colours, the index labels and the status readout.
    private readonly List<UvWorkingSet.FaceRef> _ringInfo = new();

    // The ring currently under the pointer (-1 = none), for the hover readout + cross-highlight.
    private int _hoverRing = -1;

    private static readonly Typeface LabelTypeface = new("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);

    private Bitmap? _texture;
    private double _zoom = 256;
    private double _panU;
    private double _panV = -0.2;
    private bool _showTiled = true;
    private bool _gridSnap;
    private bool _decalMode;
    private float _moveInc = 0.05f;
    private float _rotInc = 15f;
    private float _scaleInc = 1.1f;
    private float _gridStep = 0.0625f;
    private Color _lineColor = Colors.Lime;

    // Item 3b: the picking granularity. Selection is always stored as a vertex set (_sel);
    // the mode only changes how a click / box maps to that set.
    private SelectMode _mode = SelectMode.Vertex;

    private char _xform;
    private bool _panning;
    private bool _zooming;
    private Point _lastPointer;
    private bool _fitted;

    // Item 3a: deferred LMB gesture — a press may resolve to a click pick or, once it drags
    // past the threshold, a rubber-band box select.
    private bool _pointerDown;
    private bool _boxSelecting;
    private Point _pressScreen;
    private Point _boxCurrent;
    private KeyModifiers _pressMods;

    private enum SelectMode
    {
        Vertex,
        Edge,
        Face,
    }

    private readonly UvView _view;
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2), Foreground = Brushes.Gainsboro };

    // Item 3b(3): the face-identity readout — "Face N: brush U face F — texture" for the hovered or
    // selected face(s), above the persistent help line so a mixed-texture selection is visible.
    private readonly TextBlock _faceReadout = new() { Margin = new Thickness(6, 3, 6, 0), FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };

    private readonly AppSettings? _settings;
    private readonly Action? _persistSettings;

    // Group B — the Rotate° and Grid toolbar selectors reuse the main window's increment
    // picker (preset dropdown + free entry), driving the same _rotInc / _gridStep fields the
    // old free-type boxes did (session-local, exactly as before).
    private readonly IncrementSetting _rotSetting;
    private readonly IncrementSetting _gridSetting;

    // Group C — held-M/R/S 2D gizmo drag state. A drag previews by re-transforming a snapshot
    // of the working set (so it never drifts) and commits once on release = one undo step.
    private enum GizmoHandle
    {
        None,
        MoveX,
        MoveY,
        MoveFree,
        Rotate,
        ScaleX,
        ScaleY,
        ScaleUniform,
    }

    // Screen-space gizmo geometry (pixels), sized like the main-window manipulator's handles.
    private const double GizArm = 46;      // axis arm length
    private const double GizInner = 12;    // arm start offset (clears the centre handle)
    private const double GizHandle = 6.5;  // half-size / pick tolerance for square handles
    private const double GizCentre = 8;    // half-size of the centre square
    private const double GizRing = 42;     // rotate ring radius
    private const double GizRingTol = 7;   // ring pick band half-width
    private const double GizCorner = 34;   // corner uniform-scale handle offset (both axes)

    private bool _gizmoDragging;
    private char _gizmoKind;               // 'M' / 'R' / 'S' captured at drag start
    private GizmoHandle _gizmoHandle;
    private List<Uv> _dragOrig = new();
    private int[] _dragSel = Array.Empty<int>();
    private Uv _dragCentroid;
    private Uv _dragStartUv;
    private Point _gizmoPointer;

    public UvUnwrapWindow(
        BrushEditor be,
        Func<string, Bitmap?> loadTexture,
        Action onCommitted,
        AppSettings? settings = null,
        Action? persistSettings = null,
        Action<IReadOnlyList<(int Uid, int Face)>>? onHighlightFaces = null)
    {
        _be = be;
        _loadTexture = loadTexture;
        _onCommitted = onCommitted;
        _settings = settings;
        _persistSettings = persistSettings;
        _onHighlightFaces = onHighlightFaces;

        Title = "UV Unwrap";
        Width = 940;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Item 9: the UV editor remembers its size + position across sessions (like stock
        // RED's persisted tool-window placements). Restore clamped to sane minimums; an
        // unset/legacy setting keeps the centred default.
        if (_settings is { UvWindowWidth: >= 400, UvWindowHeight: >= 300 })
        {
            Width = _settings.UvWindowWidth;
            Height = _settings.UvWindowHeight;
            if (_settings.UvWindowX != int.MinValue && _settings.UvWindowY != int.MinValue)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(_settings.UvWindowX, _settings.UvWindowY);
            }
        }

        Closing += (_, _) =>
        {
            // Clear the 3D viewport cross-highlight when the editor closes.
            _onHighlightFaces?.Invoke(System.Array.Empty<(int, int)>());

            if (_settings is not null)
            {
                _settings.UvWindowX = Position.X;
                _settings.UvWindowY = Position.Y;
                _settings.UvWindowWidth = Width;
                _settings.UvWindowHeight = Height;
                _persistSettings?.Invoke();
            }
        };

        LoadWorkingSet();

        // Item 1: the custom-drawn canvas paints tiles at arbitrary screen coords; without
        // clipping it bleeds over the docked toolbar and status bar. Confine it to its layout
        // slot so it always sits strictly below the (possibly wrapped) toolbar and above the
        // status bar, at every zoom / pan.
        _view = new UvView(this) { Focusable = true, ClipToBounds = true };
        _view.PointerPressed += OnViewPointerPressed;
        _view.PointerMoved += OnViewPointerMoved;
        _view.PointerReleased += OnViewPointerReleased;
        _view.PointerWheelChanged += OnViewWheel;
        _view.PointerExited += OnViewPointerExited;

        // Group B — same preset ladders + control as the main toolbar's Rot / Grid pickers.
        // Rotation reuses degrees directly; the UV grid step is a tile-space fraction (no metre
        // unit), so it borrows the grid preset values but drops the " m" suffix.
        _rotSetting = new IncrementSetting(
            "Rotate", "°", SnapIncrements.RotationPresets,
            () => _rotInc, v => _rotInc = v, SnapIncrements.TryParseRotation);
        _gridSetting = new IncrementSetting(
            "Grid", string.Empty, SnapIncrements.GridPresets,
            () => _gridStep, v => _gridStep = v, SnapIncrements.TryParseGrid);

        var root = new DockPanel();
        Control toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        root.Children.Add(toolbar);
        var statusBar = new StackPanel { Orientation = Orientation.Vertical };
        statusBar.Children.Add(_faceReadout);
        statusBar.Children.Add(_status);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x1E)),
            ClipToBounds = true,
            Child = _view,
        });
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        UpdateStatus();
        UpdateFaceReadoutAndHighlight();
    }

    private void LoadWorkingSet()
    {
        _refs.Clear();
        _uvs.Clear();
        _rings.Clear();
        _ringInfo.Clear();

        // Flatten EVERY selected face (across every brush) into the shared working set — one ring per
        // face, all partitioning _uvs. Pure + unit-tested (see UvWorkingSetTests).
        UvWorkingSet.Data data = UvWorkingSet.Build(_be);
        _refs.AddRange(data.Refs);
        _uvs.AddRange(data.Uvs);
        _rings.AddRange(data.Rings);
        _ringInfo.AddRange(data.Faces);

        if (data.FirstTexture is not null)
        {
            try
            {
                _texture = _loadTexture(data.FirstTexture);
            }
            catch (Exception)
            {
                _texture = null;
            }
        }
    }

    private Control BuildToolbar()
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };

        // Item 3b: selection-mode group — Vertices / Edges / Faces (this deliberately goes
        // beyond stock RED, which only had vertices). Sits at the top of the window.
        panel.Children.Add(BuildModeGroup());
        panel.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 2), Background = new SolidColorBrush(Color.FromRgb(0x44, 0x46, 0x4C)) });

        panel.Children.Add(Toggle("Show Tiled", _showTiled, v => { _showTiled = v; _view.InvalidateVisual(); }));
        panel.Children.Add(Toggle("Grid Snap (G)", _gridSnap, v => { _gridSnap = v; }));
        panel.Children.Add(Toggle("Decal UVs (D/T)", _decalMode, v => { _decalMode = v; _view.InvalidateVisual(); }));
        panel.Children.Add(NumBox("Move", _moveInc, v => _moveInc = v));
        // Group B: Rotate° and Grid are now preset dropdowns (matching the main window); Move
        // and Scale keep their free-entry boxes.
        panel.Children.Add(IncrementFlyout.MakeDropDown(_rotSetting, minWidth: 84));
        panel.Children.Add(NumBox("Scale", _scaleInc, v => _scaleInc = v));
        panel.Children.Add(IncrementFlyout.MakeDropDown(_gridSetting, minWidth: 98));

        // Single-face / fallback line colour. When MULTIPLE faces are loaded each face is drawn in its
        // own Okabe–Ito colour (with a numbered label) so they are tellable apart, and this dropdown is
        // ignored — so it is DISABLED in that state (the working set is fixed at window open) rather
        // than pretending to work. Kept visible for layout stability + single-face discoverability.
        bool singleFace = _rings.Count <= 1;
        var color = new ComboBox
        {
            ItemsSource = new[] { "Line: Green", "Line: Cyan", "Line: Yellow", "Line: White" },
            SelectedIndex = 0,
            Margin = new Thickness(4, 0),
            IsEnabled = singleFace,
            [ToolTip.TipProperty] = singleFace
                ? "Outline colour for a single loaded face. With several faces, each gets its own colourblind-safe colour + number."
                : "Per-face colors are used when multiple faces are loaded.",
        };
        color.SelectionChanged += (_, _) =>
        {
            _lineColor = color.SelectedIndex switch { 1 => Colors.Cyan, 2 => Colors.Yellow, 3 => Colors.White, _ => Colors.Lime };
            _view.InvalidateVisual();
        };
        panel.Children.Add(color);

        panel.Children.Add(MakeButton("Flip V", () => Apply("Flip V", u => UnwrapOps.FlipV(u, SelOrAll()))));
        panel.Children.Add(MakeButton("Flip H", () => Apply("Flip H", u => UnwrapOps.FlipU(u, SelOrAll()))));
        // Item 10: planar-project each face by its normal's dominant axis and shelf-pack the
        // islands into the [0,1] tile (world proportions kept, nothing overlapping).
        panel.Children.Add(MakeButton("Auto Unwrap", () => Apply("Auto Unwrap", AutoUnwrap)));
        // Item 11: scale+centre the selected (or all) UVs to fill the base tile.
        panel.Children.Add(MakeButton("Fit UVs", () => Apply("Fit UVs", u => UnwrapOps.FitToTile(u, SelOrAll()))));
        panel.Children.Add(MakeButton("Print → TGA", () => _ = PrintAsync()));
        panel.Children.Add(MakeButton("Fit View", () => { _fitted = false; _view.InvalidateVisual(); }));
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2C)), Child = panel };
    }

    private Control BuildModeGroup()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "Select:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) });
        row.Children.Add(ModeRadio("Vertices", SelectMode.Vertex));
        row.Children.Add(ModeRadio("Edges", SelectMode.Edge));
        row.Children.Add(ModeRadio("Faces", SelectMode.Face));
        return row;
    }

    private RadioButton ModeRadio(string label, SelectMode mode)
    {
        var rb = new RadioButton
        {
            Content = label,
            GroupName = "UvSelectMode",
            IsChecked = _mode == mode,
            FontSize = 11,
            Margin = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        rb.IsCheckedChanged += (_, _) =>
        {
            if (rb.IsChecked == true && _mode != mode)
            {
                // The stored vertex set survives the switch unchanged (see UvSelection docs);
                // only the highlight interpretation changes.
                _mode = mode;
                _view.InvalidateVisual();
                UpdateStatus();
            }
        };
        return rb;
    }

    // ---- Input ----------------------------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.M or Key.R or Key.S:
                _xform = e.Key.ToString()[0];
                _view.InvalidateVisual(); // show the 2D gizmo while held (with a non-empty selection)
                e.Handled = true;
                return;
            case Key.G:
                _gridSnap = !_gridSnap;
                UpdateStatus();
                e.Handled = true;
                return;
            case Key.D:
                _decalMode = true;
                _view.InvalidateVisual();
                e.Handled = true;
                return;
            case Key.T:
                _decalMode = false;
                _view.InvalidateVisual();
                e.Handled = true;
                return;
            case Key.V:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    Apply("Align V", u => UnwrapOps.AlignV(u, SelOrAll()));
                }
                else
                {
                    Apply("Flip V", u => UnwrapOps.FlipV(u, SelOrAll()));
                }

                e.Handled = true;
                return;
            case Key.H:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    Apply("Align H", u => UnwrapOps.AlignU(u, SelOrAll()));
                }
                else
                {
                    Apply("Flip H", u => UnwrapOps.FlipU(u, SelOrAll()));
                }

                e.Handled = true;
                return;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                HandleArrow(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                return;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.M or Key.R or Key.S && _xform == e.Key.ToString()[0])
        {
            _xform = '\0';
            _view.InvalidateVisual(); // hide the gizmo on release (an in-progress drag continues)
        }
    }

    private void HandleArrow(Key key, bool shift)
    {
        IReadOnlyCollection<int> sel = SelOrAll();
        switch (_xform)
        {
            case 'R':
                float deg = key is Key.Left or Key.Down ? -_rotInc : _rotInc;
                Apply("Rotate UVs", u => UnwrapOps.Rotate(u, sel, deg));
                break;
            case 'S':
                float f = key is Key.Left or Key.Down ? 1f / _scaleInc : _scaleInc;
                (float su, float sv) = shift
                    ? (key is Key.Left or Key.Right ? f : 1f, key is Key.Up or Key.Down ? f : 1f) // non-uniform per axis
                    : (f, f);
                Apply("Scale UVs", u => UnwrapOps.Scale(u, sel, su, sv));
                break;
            default: // Move (M or none)
                float du = key == Key.Left ? -_moveInc : key == Key.Right ? _moveInc : 0f;
                float dv = key == Key.Up ? -_moveInc : key == Key.Down ? _moveInc : 0f;
                Apply("Move UVs", u => UnwrapOps.Move(u, sel, du, dv));
                break;
        }
    }

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _view.Focus();
        PointerPoint p = e.GetCurrentPoint(_view);
        _lastPointer = p.Position;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Pan: Shift+LMB (documented gesture, preserved) or Middle-mouse drag.
        if (p.Properties.IsMiddleButtonPressed || (shift && p.Properties.IsLeftButtonPressed))
        {
            _panning = true;
            return;
        }

        // Zoom: Shift+RMB drag (preserved; wheel also zooms).
        if (shift && p.Properties.IsRightButtonPressed)
        {
            _zooming = true;
            return;
        }

        // Plain LMB: while holding M/R/S over a gizmo handle, begin a gizmo drag; otherwise
        // defer — a small movement resolves to a click pick, a drag to a rubber-band box select.
        if (p.Properties.IsLeftButtonPressed)
        {
            if (_xform is 'M' or 'R' or 'S' && _sel.Count > 0)
            {
                GizmoHandle handle = HitGizmo(p.Position);
                if (handle != GizmoHandle.None)
                {
                    BeginGizmoDrag(handle, p.Position);
                    return;
                }
            }

            _pointerDown = true;
            _boxSelecting = false;
            _pressScreen = p.Position;
            _boxCurrent = p.Position;
            _pressMods = e.KeyModifiers;
        }
    }

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        Point pos = e.GetCurrentPoint(_view).Position;
        double dx = pos.X - _lastPointer.X;
        double dy = pos.Y - _lastPointer.Y;
        _lastPointer = pos;

        if (_gizmoDragging)
        {
            UpdateGizmoDrag(pos);
            return;
        }

        if (_panning)
        {
            _panU -= dx / _zoom;
            _panV -= dy / _zoom;
            _view.InvalidateVisual();
        }
        else if (_zooming)
        {
            _zoom = Math.Clamp(_zoom * Math.Exp(-dy * 0.01), 8, 8192);
            _view.InvalidateVisual();
        }
        else if (_pointerDown)
        {
            if (!_boxSelecting &&
                (Math.Abs(pos.X - _pressScreen.X) > 3 || Math.Abs(pos.Y - _pressScreen.Y) > 3))
            {
                _boxSelecting = true;
            }

            if (_boxSelecting)
            {
                _boxCurrent = pos;
                _view.InvalidateVisual();
            }
        }
        else
        {
            // Plain hover (no button, no drag): identify the face under the pointer + cross-highlight it.
            UpdateHover(pos);
        }
    }

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gizmoDragging)
        {
            CommitGizmoDrag();
            _gizmoDragging = false;
            _view.InvalidateVisual();
            return;
        }

        if (_pointerDown)
        {
            if (_boxSelecting)
            {
                CommitBoxSelect(_pressScreen, _boxCurrent, _pressMods);
            }
            else
            {
                PickAt(_pressScreen, _pressMods);
            }
        }

        _pointerDown = false;
        _boxSelecting = false;
        _panning = false;
        _zooming = false;
        _view.InvalidateVisual();
    }

    /// <summary>Clears the hover readout + cross-highlight when the pointer leaves the canvas.</summary>
    private void OnViewPointerExited(object? sender, PointerEventArgs e)
    {
        if (_hoverRing != -1)
        {
            _hoverRing = -1;
            UpdateFaceReadoutAndHighlight();
            _view.InvalidateVisual();
        }
    }

    private void OnViewWheel(object? sender, PointerWheelEventArgs e)
    {
        Point pos = e.GetCurrentPoint(_view).Position;
        double beforeU = ScreenToU(pos.X);
        double beforeV = ScreenToV(pos.Y);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 8, 8192);
        // Keep the point under the cursor stable.
        _panU = beforeU - (pos.X / _zoom);
        _panV = beforeV - (pos.Y / _zoom);
        _view.InvalidateVisual();
    }

    /// <summary>
    /// Click pick in the active selection mode. Maps the hit to the vertex indices it implies
    /// (a vertex, an edge's two endpoints, or a whole face's corners), then applies the
    /// modifier: plain replaces, Ctrl adds, Alt removes. Clicking empty space with no modifier
    /// clears — Shift+LMB never reaches here (it pans).
    /// </summary>
    private void PickAt(Point screen, KeyModifiers mods)
    {
        float u = (float)ScreenToU(screen.X);
        float v = (float)ScreenToV(screen.Y);
        float radius = (float)(10.0 / _zoom); // 10px pick tolerance in UV units

        var picked = new List<int>();
        switch (_mode)
        {
            case SelectMode.Edge:
                (int a, int b) = UvSelection.NearestEdge(_uvs, _rings, u, v, radius);
                if (a >= 0)
                {
                    picked.Add(a);
                    picked.Add(b);
                }

                break;

            case SelectMode.Face:
                int face = UvSelection.FaceContainingPoint(_uvs, _rings, u, v);
                if (face < 0)
                {
                    int nv = UvSelection.NearestVertex(_uvs, u, v, radius);
                    face = nv >= 0 ? RingOf(nv) : -1;
                }

                if (face >= 0)
                {
                    picked.AddRange(_rings[face]);
                }

                break;

            default:
                int vert = UvSelection.NearestVertex(_uvs, u, v, radius);
                if (vert >= 0)
                {
                    picked.Add(vert);
                }

                break;
        }

        ApplyPick(picked, mods);
    }

    /// <summary>Rubber-band box select — selects the vertices its mode implies inside the box.</summary>
    private void CommitBoxSelect(Point p0, Point p1, KeyModifiers mods)
    {
        float minU = (float)Math.Min(ScreenToU(p0.X), ScreenToU(p1.X));
        float maxU = (float)Math.Max(ScreenToU(p0.X), ScreenToU(p1.X));
        float minV = (float)Math.Min(ScreenToV(p0.Y), ScreenToV(p1.Y));
        float maxV = (float)Math.Max(ScreenToV(p0.Y), ScreenToV(p1.Y));

        List<int> boxed = _mode switch
        {
            SelectMode.Edge => UvSelection.EdgeVerticesInRect(_uvs, _rings, minU, minV, maxU, maxV),
            SelectMode.Face => UvSelection.FaceVerticesInRect(_uvs, _rings, minU, minV, maxU, maxV),
            _ => UvSelection.VerticesInRect(_uvs, minU, minV, maxU, maxV),
        };

        ApplyPick(boxed, mods);
    }

    /// <summary>
    /// Folds a pick / box result into the selection: Alt removes, Ctrl adds (union), plain
    /// replaces (an empty plain pick therefore clears).
    /// </summary>
    private void ApplyPick(List<int> picked, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Alt))
        {
            foreach (int i in picked)
            {
                _sel.Remove(i);
            }
        }
        else if (mods.HasFlag(KeyModifiers.Control))
        {
            foreach (int i in picked)
            {
                _sel.Add(i);
            }
        }
        else
        {
            _sel.Clear();
            foreach (int i in picked)
            {
                _sel.Add(i);
            }
        }

        UpdateStatus();
        UpdateFaceReadoutAndHighlight();
        _view.InvalidateVisual();
    }

    /// <summary>The ring (face) index that owns vertex <paramref name="vertex"/>, or -1.</summary>
    private int RingOf(int vertex)
    {
        for (int r = 0; r < _rings.Count; r++)
        {
            if (_rings[r].Contains(vertex))
            {
                return r;
            }
        }

        return -1;
    }

    // ---- Apply / commit -------------------------------------------------------

    private IReadOnlyCollection<int> SelOrAll() =>
        _sel.Count > 0 ? _sel : Enumerable.Range(0, _uvs.Count).ToList();

    private void Apply(string desc, Action<List<Uv>> op)
    {
        if (_uvs.Count == 0)
        {
            return;
        }

        op(_uvs);
        if (_gridSnap)
        {
            UnwrapOps.SnapToGrid(_uvs, SelOrAll(), _gridStep);
        }

        var edits = new List<(int, int, int, Uv)>(_refs.Count);
        for (int i = 0; i < _refs.Count; i++)
        {
            edits.Add((_refs[i].Brush, _refs[i].Face, _refs[i].Corner, _uvs[i]));
        }

        _be.SetFaceUvs(desc, edits);
        _onCommitted();
        _view.InvalidateVisual();
        UpdateStatus();
    }

    /// <summary>
    /// Item 10 — Auto Unwrap over the working set: groups corners into per-face rings,
    /// resolves each corner's brush-local position and each face's normal, and lets
    /// <see cref="UnwrapOps.AutoUnwrap"/> project + pack them into the base tile.
    /// </summary>
    private void AutoUnwrap(List<Uv> uvs)
    {
        var rings = new List<IReadOnlyList<int>>();
        var normals = new List<Vec3>();
        var positions = new Vec3[_refs.Count];

        var byFace = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < _refs.Count; i++)
        {
            var key = (_refs[i].Brush, _refs[i].Face);
            if (!byFace.TryGetValue(key, out List<int>? list))
            {
                byFace[key] = list = new List<int>();
            }

            list.Add(i);
        }

        foreach (KeyValuePair<(int Brush, int Face), List<int>> entry in byFace)
        {
            Brush? b = _be.FindBrush(entry.Key.Brush);
            if (b is null || entry.Key.Face < 0 || entry.Key.Face >= b.Geometry.Faces.Count)
            {
                continue;
            }

            Face face = b.Geometry.Faces[entry.Key.Face];
            foreach (int i in entry.Value)
            {
                int corner = _refs[i].Corner;
                if (corner >= 0 && corner < face.Vertices.Count)
                {
                    int vi = face.Vertices[corner].Index;
                    if (vi >= 0 && vi < b.Geometry.Vertices.Count)
                    {
                        positions[i] = b.Geometry.Vertices[vi];
                    }
                }
            }

            rings.Add(entry.Value);
            normals.Add(face.Plane.Normal);
        }

        UnwrapOps.AutoUnwrap(uvs, rings, i => positions[i], f => normals[f]);
    }

    private async System.Threading.Tasks.Task PrintAsync()
    {
        try
        {
            var size = new PixelSize(Math.Max(2, (int)_view.Bounds.Width), Math.Max(2, (int)_view.Bounds.Height));
            var rtb = new RenderTargetBitmap(size);
            rtb.Render(_view);

            var buffer = new byte[size.Width * size.Height * 4];
            System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                rtb.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
            }
            finally
            {
                handle.Free();
            }

            // Avalonia gives us premultiplied BGRA; TgaWriter wants RGBA.
            var rgba = new byte[buffer.Length];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                rgba[i] = buffer[i + 2];
                rgba[i + 1] = buffer[i + 1];
                rgba[i + 2] = buffer[i];
                rgba[i + 3] = buffer[i + 3];
            }

            byte[] tga = TgaWriter.Encode(new TextureImage(size.Width, size.Height, rgba));

            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Print UV Layout",
                DefaultExtension = "tga",
                SuggestedFileName = "uv_layout.tga",
                FileTypeChoices = new[] { new FilePickerFileType("Targa image") { Patterns = new[] { "*.tga" } } },
            });
            if (file?.TryGetLocalPath() is string path)
            {
                File.WriteAllBytes(path, tga);
                _status.Text = $"Saved {Path.GetFileName(path)}";
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Print failed: {ex.Message}";
        }
    }

    private void UpdateStatus() =>
        _status.Text = $"{_uvs.Count} UV verts, {_sel.Count} selected  |  {_mode} mode  |  {(_decalMode ? "Decal" : "Texture")} UVs  |  grid snap {(_gridSnap ? "on" : "off")}  |  LMB box-select (Ctrl add, Alt remove), hold M/R/S: drag gizmo or arrows, V/H flip, Shift+V/H align, wheel zoom, Shift+LMB / MMB pan";

    // ---- Coordinate mapping ---------------------------------------------------

    private double UToScreen(double u) => (u - _panU) * _zoom;

    private double VToScreen(double v) => (v - _panV) * _zoom;

    private double ScreenToU(double x) => (x / _zoom) + _panU;

    private double ScreenToV(double y) => (y / _zoom) + _panV;

    // ---- Rendering (called back from the view) --------------------------------

    private void EnsureFitted(Rect bounds)
    {
        if (_fitted || _uvs.Count == 0 || bounds.Width < 4)
        {
            return;
        }

        float minU = _uvs.Min(p => p.U), maxU = _uvs.Max(p => p.U);
        float minV = _uvs.Min(p => p.V), maxV = _uvs.Max(p => p.V);
        // Include the base [0,1] tile so the texture is visible even for tight UVs.
        minU = MathF.Min(minU, 0);
        minV = MathF.Min(minV, 0);
        maxU = MathF.Max(maxU, 1);
        maxV = MathF.Max(maxV, 1);
        double rangeU = Math.Max(0.5, maxU - minU);
        double rangeV = Math.Max(0.5, maxV - minV);
        _zoom = Math.Clamp(Math.Min(bounds.Width / rangeU, bounds.Height / rangeV) * 0.85, 8, 8192);
        _panU = ((minU + maxU) / 2.0) - (bounds.Width / 2.0 / _zoom);
        _panV = ((minV + maxV) / 2.0) - (bounds.Height / 2.0 / _zoom);
        _fitted = true;
    }

    private void Draw(DrawingContext ctx, Rect bounds)
    {
        EnsureFitted(bounds);

        // Tiled texture background (item 2: fills the whole visible canvas at EVERY zoom).
        if (_texture is not null)
        {
            var tile = new Rect(UToScreen(0), VToScreen(0), _zoom, _zoom);
            if (!_showTiled)
            {
                ctx.DrawImage(_texture, tile);
            }
            else
            {
                // The exact span of tiles intersecting the viewport, derived from the current
                // pan/zoom (no fixed ±N window that leaves a void when zoomed out).
                int u0 = (int)Math.Floor(ScreenToU(0));
                int u1 = (int)Math.Ceiling(ScreenToU(bounds.Width));
                int v0 = (int)Math.Floor(ScreenToV(0));
                int v1 = (int)Math.Ceiling(ScreenToV(bounds.Height));
                long count = (long)(u1 - u0) * (v1 - v0);

                if (count <= 400)
                {
                    // Few tiles (zoomed in / mid): draw each visible tile directly — exact, and
                    // avoids a large tile-brush intermediate at high zoom.
                    for (int tu = u0; tu < u1; tu++)
                    {
                        for (int tv = v0; tv < v1; tv++)
                        {
                            var dest = new Rect(UToScreen(tu), VToScreen(tv), _zoom, _zoom);
                            using (ctx.PushOpacity(_decalMode && (tu != 0 || tv != 0) ? 0.35 : 1.0))
                            {
                                ctx.DrawImage(_texture, dest);
                            }
                        }
                    }
                }
                else
                {
                    // Many small tiles (zoomed out): one repeating image brush fills the whole
                    // viewport in a single draw. The destination rect sizes/anchors one tile to
                    // the base [0,1] cell; TileMode.Tile repeats it in every direction. (Here the
                    // tile is small, so the brush intermediate is small.)
                    var brush = new ImageBrush(_texture)
                    {
                        TileMode = TileMode.Tile,
                        Stretch = Stretch.Fill,
                        DestinationRect = new RelativeRect(tile, RelativeUnit.Absolute),
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                    };
                    using (ctx.PushOpacity(_decalMode ? 0.35 : 1.0))
                    {
                        ctx.FillRectangle(brush, new Rect(0, 0, bounds.Width, bounds.Height));
                    }

                    // In decal mode the base [0,1] tile stays bright over the dimmed field.
                    if (_decalMode)
                    {
                        ctx.DrawImage(_texture, tile);
                    }
                }
            }
        }

        // Base [0,1] tile outline.
        var tilePen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
        ctx.DrawRectangle(null, tilePen, new Rect(UToScreen(0), VToScreen(0), _zoom, _zoom));

        // UV polygons, per-face coloured (multi-face → Okabe–Ito palette, single face → the toolbar
        // colour), with edge / face selection highlight for the active mode. Selection stays orange
        // across every face so "selected" reads unambiguously over the per-face identity colours.
        var edgeHiPen = new Pen(new SolidColorBrush(Colors.Orange), 2.6);
        var selBrush = new SolidColorBrush(Colors.Orange);
        var faceHi = new SolidColorBrush(Color.FromArgb(52, 255, 165, 0));
        bool label = _rings.Count > 1; // number the faces only when there is more than one to tell apart
        for (int r = 0; r < _rings.Count; r++)
        {
            IReadOnlyList<int> ring = _rings[r];
            int n = ring.Count;
            Color faceColor = FaceColor(r);
            var linePen = new Pen(new SolidColorBrush(faceColor), 1.4);
            var dotBrush = new SolidColorBrush(faceColor);

            if (n >= 3 && RingFullySelected(ring))
            {
                var geo = new StreamGeometry();
                using (StreamGeometryContext gc = geo.Open())
                {
                    gc.BeginFigure(new Point(UToScreen(_uvs[ring[0]].U), VToScreen(_uvs[ring[0]].V)), true);
                    for (int i = 1; i < n; i++)
                    {
                        gc.LineTo(new Point(UToScreen(_uvs[ring[i]].U), VToScreen(_uvs[ring[i]].V)));
                    }

                    gc.EndFigure(true);
                }

                ctx.DrawGeometry(faceHi, null, geo);
            }

            for (int i = 0; i < n; i++)
            {
                int a = ring[i];
                int b = ring[(i + 1) % n];
                bool edgeSel = _sel.Contains(a) && _sel.Contains(b);
                ctx.DrawLine(edgeSel ? edgeHiPen : linePen,
                    new Point(UToScreen(_uvs[a].U), VToScreen(_uvs[a].V)),
                    new Point(UToScreen(_uvs[b].U), VToScreen(_uvs[b].V)));
            }

            // Vertices, coloured by their owning face (each _uvs entry belongs to exactly one ring).
            foreach (int vi in ring)
            {
                double x = UToScreen(_uvs[vi].U);
                double y = VToScreen(_uvs[vi].V);
                bool sel = _sel.Contains(vi);
                ctx.DrawEllipse(sel ? selBrush : dotBrush, null, new Point(x, y), sel ? 4 : 2.5, sel ? 4 : 2.5);
            }

            if (label)
            {
                DrawRingLabel(ctx, ring, r, faceColor);
            }
        }

        // Rubber-band box overlay while dragging.
        if (_boxSelecting)
        {
            double bx = Math.Min(_pressScreen.X, _boxCurrent.X);
            double by = Math.Min(_pressScreen.Y, _boxCurrent.Y);
            double bw = Math.Abs(_boxCurrent.X - _pressScreen.X);
            double bh = Math.Abs(_boxCurrent.Y - _pressScreen.Y);
            var boxPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1) { DashStyle = DashStyle.Dash };
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(36, 120, 170, 255)), boxPen, new Rect(bx, by, bw, bh));
        }

        DrawGizmo(ctx);
    }

    private bool RingFullySelected(IReadOnlyList<int> ring)
    {
        if (ring.Count == 0)
        {
            return false;
        }

        foreach (int v in ring)
        {
            if (!_sel.Contains(v))
            {
                return false;
            }
        }

        return true;
    }

    // ---- Multi-face identification (colours / labels / readout / cross-highlight) ----

    /// <summary>The outline/vertex/label colour of ring <paramref name="ringIndex"/> (single face → the toolbar colour).</summary>
    private Color FaceColor(int ringIndex) => UvFaceIdentity.FaceColor(ringIndex, _rings.Count, _lineColor);

    /// <summary>Draws a face's 1-based index at its UV centroid on a dark pill so it reads over any texture.</summary>
    private void DrawRingLabel(DrawingContext ctx, IReadOnlyList<int> ring, int ringIndex, Color color)
    {
        if (ring.Count == 0)
        {
            return;
        }

        double cx = 0, cy = 0;
        foreach (int vi in ring)
        {
            cx += UToScreen(_uvs[vi].U);
            cy += VToScreen(_uvs[vi].V);
        }

        cx /= ring.Count;
        cy /= ring.Count;

        string text = (ringIndex + 1).ToString(CultureInfo.InvariantCulture);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 13, new SolidColorBrush(color));
        var pill = new Rect(cx - (ft.Width / 2) - 3, cy - (ft.Height / 2) - 1, ft.Width + 6, ft.Height + 2);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 22, 26)), null, new RoundedRect(pill, 3));
        ctx.DrawText(ft, new Point(cx - (ft.Width / 2), cy - (ft.Height / 2)));
    }

    /// <summary>The status readout for one loaded face (or empty when the index is out of range).</summary>
    private string RingReadout(int ringIndex)
    {
        if (ringIndex < 0 || ringIndex >= _ringInfo.Count)
        {
            return string.Empty;
        }

        UvWorkingSet.FaceRef info = _ringInfo[ringIndex];
        return UvFaceIdentity.Readout(ringIndex, info.BrushUid, info.FaceIndex, info.Texture);
    }

    /// <summary>The rings that own at least one currently-selected vertex.</summary>
    private List<int> SelectedRings()
    {
        var rings = new List<int>();
        for (int r = 0; r < _rings.Count; r++)
        {
            foreach (int v in _rings[r])
            {
                if (_sel.Contains(v))
                {
                    rings.Add(r);
                    break;
                }
            }
        }

        return rings;
    }

    /// <summary>Pushes the given rings' brush faces to the 3D viewport cross-highlight (empty clears it).</summary>
    private void PushHighlight(IReadOnlyList<int> rings)
    {
        if (_onHighlightFaces is null)
        {
            return;
        }

        var faces = new List<(int Uid, int Face)>(rings.Count);
        foreach (int r in rings)
        {
            if (r >= 0 && r < _ringInfo.Count)
            {
                faces.Add((_ringInfo[r].BrushUid, _ringInfo[r].FaceIndex));
            }
        }

        _onHighlightFaces(faces);
    }

    /// <summary>
    /// Refreshes the face-identity readout and the 3D cross-highlight: a hovered ring wins; otherwise
    /// the selected face(s) are shown (a mixed-texture selection is surfaced explicitly); otherwise a
    /// hint. The highlight follows the same target so the viewport always outlines what the readout names.
    /// </summary>
    private void UpdateFaceReadoutAndHighlight()
    {
        if (_hoverRing >= 0)
        {
            _faceReadout.Text = RingReadout(_hoverRing);
            PushHighlight(new[] { _hoverRing });
            return;
        }

        List<int> selRings = SelectedRings();
        if (selRings.Count == 1)
        {
            _faceReadout.Text = RingReadout(selRings[0]);
        }
        else if (selRings.Count > 1)
        {
            string tex = UvFaceIdentity.TextureSummary(selRings.Select(r => _ringInfo[r].Texture));
            _faceReadout.Text = $"{selRings.Count} faces selected — {tex}";
        }
        else if (_ringInfo.Count == 1)
        {
            _faceReadout.Text = RingReadout(0);
        }
        else
        {
            _faceReadout.Text = _ringInfo.Count > 1 ? $"{_ringInfo.Count} faces loaded — hover a face to identify it" : string.Empty;
        }

        PushHighlight(selRings);
    }

    /// <summary>Resolves the ring under the pointer (face polygon, else nearest vertex) and refreshes on change.</summary>
    private void UpdateHover(Point pos)
    {
        float u = (float)ScreenToU(pos.X);
        float v = (float)ScreenToV(pos.Y);
        int ring = UvSelection.FaceContainingPoint(_uvs, _rings, u, v);
        if (ring < 0)
        {
            int nv = UvSelection.NearestVertex(_uvs, u, v, (float)(10.0 / _zoom));
            ring = nv >= 0 ? RingOf(nv) : -1;
        }

        if (ring != _hoverRing)
        {
            _hoverRing = ring;
            UpdateFaceReadoutAndHighlight();
            _view.InvalidateVisual();
        }
    }

    // ---- 2D gizmo (held M/R/S) ------------------------------------------------

    /// <summary>The gizmo kind to draw now: the live drag's kind, or the held key when a
    /// non-empty selection makes the manipulator applicable, else none.</summary>
    private char GizmoActiveKind()
    {
        if (_gizmoDragging)
        {
            return _gizmoKind;
        }

        return _xform is 'M' or 'R' or 'S' && _sel.Count > 0 ? _xform : '\0';
    }

    /// <summary>Hit-tests the pointer against the current gizmo's handles (screen space).</summary>
    private GizmoHandle HitGizmo(Point p)
    {
        if (_sel.Count == 0)
        {
            return GizmoHandle.None;
        }

        Uv c = UnwrapOps.Centroid(_uvs, _sel.ToArray());
        double gx = UToScreen(c.U);
        double gy = VToScreen(c.V);
        double dx = p.X - gx;
        double dy = p.Y - gy;

        switch (_xform)
        {
            case 'M':
                if (Math.Abs(dx) <= GizCentre && Math.Abs(dy) <= GizCentre)
                {
                    return GizmoHandle.MoveFree;
                }

                if (NearSegment(p, gx + GizInner, gy, gx + GizArm, gy, GizHandle))
                {
                    return GizmoHandle.MoveX;
                }

                if (NearSegment(p, gx, gy + GizInner, gx, gy + GizArm, GizHandle))
                {
                    return GizmoHandle.MoveY;
                }

                break;

            case 'R':
                double dist = Math.Sqrt((dx * dx) + (dy * dy));
                if (Math.Abs(dist - GizRing) <= GizRingTol)
                {
                    return GizmoHandle.Rotate;
                }

                break;

            case 'S':
                if (InBox(p, gx + GizArm, gy, GizHandle))
                {
                    return GizmoHandle.ScaleX;
                }

                if (InBox(p, gx, gy + GizArm, GizHandle))
                {
                    return GizmoHandle.ScaleY;
                }

                if ((Math.Abs(dx) <= GizCentre && Math.Abs(dy) <= GizCentre) || InBox(p, gx + GizCorner, gy + GizCorner, GizHandle))
                {
                    return GizmoHandle.ScaleUniform;
                }

                break;
        }

        return GizmoHandle.None;
    }

    private void BeginGizmoDrag(GizmoHandle handle, Point screen)
    {
        _dragSel = _sel.ToArray();
        _dragOrig = new List<Uv>(_uvs);
        _dragCentroid = UnwrapOps.Centroid(_uvs, _dragSel);
        _dragStartUv = new Uv((float)ScreenToU(screen.X), (float)ScreenToV(screen.Y));
        _gizmoPointer = screen;
        _gizmoKind = _xform;
        _gizmoHandle = handle;
        _gizmoDragging = true;
        _view.InvalidateVisual();
    }

    /// <summary>
    /// Live-previews the drag by re-applying the cumulative transform to the pre-drag snapshot
    /// (never accumulating), so the working set — and therefore the drawn UVs — track the pointer
    /// without drift. Nothing is committed to the brush undo system until release.
    /// </summary>
    private void UpdateGizmoDrag(Point pos)
    {
        _gizmoPointer = pos;
        var cur = new Uv((float)ScreenToU(pos.X), (float)ScreenToV(pos.Y));
        for (int i = 0; i < _uvs.Count && i < _dragOrig.Count; i++)
        {
            _uvs[i] = _dragOrig[i];
        }

        string readout;
        switch (_gizmoKind)
        {
            case 'R':
                // Snaps to the Rotate° step while Grid Snap is on; free (continuous) when off.
                float deg = UvGizmoMath.AngleDegrees(_dragCentroid, _dragStartUv, cur);
                if (_gridSnap)
                {
                    deg = UvGizmoMath.SnapAngle(deg, _rotInc);
                }

                UnwrapOps.Rotate(_uvs, _dragSel, deg);
                readout = $"Rotate {deg:0.#}°";
                break;

            case 'S':
                float su, sv;
                if (_gizmoHandle == GizmoHandle.ScaleX)
                {
                    su = UvGizmoMath.AxisScale(_dragCentroid.U, _dragStartUv.U, cur.U);
                    sv = 1f;
                }
                else if (_gizmoHandle == GizmoHandle.ScaleY)
                {
                    su = 1f;
                    sv = UvGizmoMath.AxisScale(_dragCentroid.V, _dragStartUv.V, cur.V);
                }
                else
                {
                    su = sv = UvGizmoMath.UniformScale(_dragCentroid, _dragStartUv, cur);
                }

                UnwrapOps.Scale(_uvs, _dragSel, su, sv);
                readout = $"Scale {su:0.###} × {sv:0.###}";
                break;

            default: // 'M'
                UvGizmoMath.Axis axis = _gizmoHandle switch
                {
                    GizmoHandle.MoveX => UvGizmoMath.Axis.U,
                    GizmoHandle.MoveY => UvGizmoMath.Axis.V,
                    _ => UvGizmoMath.Axis.Both,
                };
                (float du, float dv) = UvGizmoMath.MoveDelta(_dragCentroid, _dragStartUv, cur, axis, _gridSnap, _gridStep);
                UnwrapOps.Move(_uvs, _dragSel, du, dv);
                readout = $"Move ΔU {du:0.###} ΔV {dv:0.###}";
                break;
        }

        _status.Text = $"UV gizmo — {readout}  |  grid snap {(_gridSnap ? "on" : "off")}";
        _view.InvalidateVisual();
    }

    /// <summary>Commits the completed drag as ONE undo entry (consistent with the other UV edits).</summary>
    private void CommitGizmoDrag()
    {
        bool changed = false;
        for (int i = 0; i < _uvs.Count && i < _dragOrig.Count; i++)
        {
            if (_uvs[i] != _dragOrig[i])
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            UpdateStatus();
            return;
        }

        string desc = _gizmoKind switch { 'R' => "Rotate UVs", 'S' => "Scale UVs", _ => "Move UVs" };
        var edits = new List<(int, int, int, Uv)>(_refs.Count);
        for (int i = 0; i < _refs.Count; i++)
        {
            edits.Add((_refs[i].Brush, _refs[i].Face, _refs[i].Corner, _uvs[i]));
        }

        _be.SetFaceUvs(desc, edits);
        _onCommitted();
        UpdateStatus();
    }

    /// <summary>
    /// Draws the 2D manipulator at the selection's UV centroid: move arrows + free square,
    /// rotate ring, or scale axis boxes + uniform handles. Axis colours follow the main-window
    /// gizmo palette (settings ColorAxisX/Y, Okabe–Ito fallbacks).
    /// </summary>
    private void DrawGizmo(DrawingContext ctx)
    {
        char kind = GizmoActiveKind();
        if (kind == '\0')
        {
            return;
        }

        int[] sel = _gizmoDragging ? _dragSel : _sel.ToArray();
        if (sel.Length == 0)
        {
            return;
        }

        Uv c = UnwrapOps.Centroid(_uvs, sel);
        double gx = UToScreen(c.U);
        double gy = VToScreen(c.V);

        Color colU = ParseAxisColor(_settings?.ColorAxisX, Color.FromRgb(0xD5, 0x5E, 0x00));
        Color colV = ParseAxisColor(_settings?.ColorAxisY, Color.FromRgb(0x00, 0x9E, 0x73));
        Color colUni = Color.FromRgb(0xF0, 0xC8, 0x40);

        switch (kind)
        {
            case 'M':
                DrawArrow(ctx, gx + GizInner, gy, gx + GizArm, gy, colU, _gizmoHandle == GizmoHandle.MoveX);
                DrawArrow(ctx, gx, gy + GizInner, gx, gy + GizArm, colV, _gizmoHandle == GizmoHandle.MoveY);
                DrawHandleBox(ctx, gx, gy, GizCentre, colUni, _gizmoDragging && _gizmoHandle == GizmoHandle.MoveFree);
                break;

            case 'R':
            {
                bool active = _gizmoDragging && _gizmoHandle == GizmoHandle.Rotate;
                var ringPen = new Pen(new SolidColorBrush(active ? Colors.White : colUni), active ? 3 : 2);
                ctx.DrawEllipse(null, ringPen, new Point(gx, gy), GizRing, GizRing);

                // Grab-point marker: the live pointer direction while dragging, else the +U spoke.
                double hx = gx + GizRing, hy = gy;
                if (_gizmoDragging)
                {
                    double vx = _gizmoPointer.X - gx;
                    double vy = _gizmoPointer.Y - gy;
                    double len = Math.Sqrt((vx * vx) + (vy * vy));
                    if (len > 1e-3)
                    {
                        hx = gx + (vx / len * GizRing);
                        hy = gy + (vy / len * GizRing);
                    }

                    ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1), new Point(gx, gy), new Point(hx, hy));
                }

                ctx.DrawEllipse(new SolidColorBrush(active ? Colors.White : colUni), null, new Point(hx, hy), 4, 4);
                break;
            }

            case 'S':
                ctx.DrawLine(new Pen(new SolidColorBrush(colU), 2), new Point(gx, gy), new Point(gx + GizArm, gy));
                ctx.DrawLine(new Pen(new SolidColorBrush(colV), 2), new Point(gx, gy), new Point(gx, gy + GizArm));
                DrawHandleBox(ctx, gx + GizArm, gy, GizHandle, colU, _gizmoDragging && _gizmoHandle == GizmoHandle.ScaleX);
                DrawHandleBox(ctx, gx, gy + GizArm, GizHandle, colV, _gizmoDragging && _gizmoHandle == GizmoHandle.ScaleY);
                DrawHandleBox(ctx, gx + GizCorner, gy + GizCorner, GizHandle, colUni, _gizmoDragging && _gizmoHandle == GizmoHandle.ScaleUniform);
                DrawHandleBox(ctx, gx, gy, GizCentre, colUni, _gizmoDragging && _gizmoHandle == GizmoHandle.ScaleUniform);
                break;
        }
    }

    private static void DrawArrow(DrawingContext ctx, double x0, double y0, double x1, double y1, Color color, bool active)
    {
        Color draw = active ? Colors.White : color;
        var pen = new Pen(new SolidColorBrush(draw), active ? 3 : 2);
        ctx.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));

        double ang = Math.Atan2(y1 - y0, x1 - x0);
        const double h = 7;
        double a1 = ang + 2.6, a2 = ang - 2.6;
        var head = new StreamGeometry();
        using (StreamGeometryContext gc = head.Open())
        {
            gc.BeginFigure(new Point(x1, y1), true);
            gc.LineTo(new Point(x1 + (h * Math.Cos(a1)), y1 + (h * Math.Sin(a1))));
            gc.LineTo(new Point(x1 + (h * Math.Cos(a2)), y1 + (h * Math.Sin(a2))));
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(new SolidColorBrush(draw), null, head);
    }

    private static void DrawHandleBox(DrawingContext ctx, double cx, double cy, double half, Color color, bool active)
    {
        var fill = new SolidColorBrush(active ? Colors.White : color);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)), 1);
        ctx.DrawRectangle(fill, pen, new Rect(cx - half, cy - half, half * 2, half * 2));
    }

    private static Color ParseAxisColor(string? hex, Color fallback) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out Color c) ? c : fallback;

    private static bool InBox(Point p, double cx, double cy, double half) =>
        Math.Abs(p.X - cx) <= half && Math.Abs(p.Y - cy) <= half;

    private static bool NearSegment(Point p, double x0, double y0, double x1, double y1, double tol)
    {
        double vx = x1 - x0, vy = y1 - y0;
        double len2 = (vx * vx) + (vy * vy);
        double t = len2 <= 1e-9 ? 0 : (((p.X - x0) * vx) + ((p.Y - y0) * vy)) / len2;
        t = Math.Clamp(t, 0, 1);
        double px = x0 + (t * vx), py = y0 + (t * vy);
        double ddx = p.X - px, ddy = p.Y - py;
        return (ddx * ddx) + (ddy * ddy) <= tol * tol;
    }

    // ---- Toolbar primitives ---------------------------------------------------

    private static Button MakeButton(string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(2, 0), FontSize = 11 };
        b.Click += (_, _) => action();
        return b;
    }

    private static CheckBox Toggle(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(4, 0), FontSize = 11 };
        cb.IsCheckedChanged += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    private static Control NumBox(string label, float value, Action<float> set)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 52, FontSize = 11 };
        box.LostFocus += (_, _) =>
        {
            if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                set(v);
            }
        };
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0) };
        p.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) });
        p.Children.Add(box);
        return p;
    }

    /// <summary>The custom-drawn UV canvas; forwards rendering back to the owner window.</summary>
    private sealed class UvView : Control
    {
        private readonly UvUnwrapWindow _owner;

        public UvView(UvUnwrapWindow owner) => _owner = owner;

        public override void Render(DrawingContext context) => _owner.Draw(context, Bounds);
    }
}
