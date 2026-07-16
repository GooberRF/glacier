using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Brush = Ged.Core.Model.Brush;
using Geometry = Ged.Core.Model.Geometry;
using Vec3 = Ged.Core.Model.Vec3;

namespace Ged.App;

/// <summary>
/// The Texture / UV tools (hosted on Face mode's Texture/UV tab):
/// a functional texture picker (category tree, instant filter, async thumbnail grid,
/// current-texture display, pick/apply/cycle, show-only-used), the mapping operators
/// (box/planar/cylinder, snap/resize/flip, UV copy/paste at pixels-per-meter), and the
/// per-face property editor (flags, scroll, lightmap resolution, 32-bit smoothing groups) —
/// all multi-select mixed-value aware and routed through the undo system so only the brushes
/// section is dirtied. These commands are Face-scoped and fire on the face selection.
/// </summary>
public sealed partial class MainWindow
{
    private readonly ThumbnailCache _thumbs = new();
    private readonly Dictionary<string, (int W, int H)> _texDimCache = new(StringComparer.OrdinalIgnoreCase);

    private string _texCurrent = BrushCreateParams.DefaultTexture;
    private int _texCylinderAxis = 1; // Y
    private Uv[]? _uvClipboard;

    private StackPanel? _texPropsHost;
    private WrapPanel? _texGridHost;
    private TextBlock? _texGridInfo;
    private Border? _texCurrentSwatch;
    private TextBlock? _texCurrentLabel;
    private TextBox? _texFilterBox;
    private CheckBox? _texShowOnlyUsed;
    private ComboBox? _texCategoryBox;

    private BrushEditor? Be => _session.BrushEditor;

    private void InitTexture()
    {
        _texCurrent = _settings.DefaultWallTexture;

        _dispatcher.Bind(CommandIds.TexMapBox, () => ApplyBoxMap());
        _dispatcher.Bind(CommandIds.TexMapPlanar, () => ApplyPlanarMap());
        _dispatcher.Bind(CommandIds.TexMapCylinder, () => ApplyCylinderMap());
        _dispatcher.Bind(CommandIds.TexSnapMap, () => ApplySnapMap());
        _dispatcher.Bind(CommandIds.TexFlipX, () => ApplyUvFace("Flip map X", (g, fi) => UvOps.FlipU(g.Faces[fi])));
        _dispatcher.Bind(CommandIds.TexFlipY, () => ApplyUvFace("Flip map Y", (g, fi) => UvOps.FlipV(g.Faces[fi])));
        _dispatcher.Bind(CommandIds.TexUvCopy, () => TexCopyUv());
        _dispatcher.Bind(CommandIds.TexUvPaste, () => TexPasteUv());
        _dispatcher.Bind(CommandIds.TexApply, ApplyCurrentTexture);
        _dispatcher.Bind(CommandIds.TexPick, PickTextureFromFace);
        _dispatcher.Bind(CommandIds.TexReselect, () => { Be?.ReselectPrevious(); RefreshTexturePanelSelection(); RefreshSelectionOverlay(); });
        // Shift+D (Select Same Texture) is Face-scoped; only refresh the texture panel when
        // the Texture/UV tab is active.
        _dispatcher.Bind(CommandIds.SelectSameTexture, () =>
        {
            Be?.SelectSameTexture();
            if (TextureToolsActive)
            {
                RefreshTexturePanelSelection();
            }

            RefreshSelectionOverlay();
        });
        _dispatcher.Bind(CommandIds.TexUvUnwrap, OpenUvUnwrap);
        _dispatcher.Bind(CommandIds.TexGrow, () => { Be?.GrowFacesToBrush(); RefreshTexturePanelSelection(); RefreshSelectionOverlay(); });

        // On Face mode's Texture/UV tab, H/V flip the map (they otherwise hit the global
        // Hide binding); intercept them before dispatch on every viewport pane. On the
        // Geometry tab H stays Hide (geometry priority — item 0h).
        _viewportGrid.ForEachSurface(s => s.KeyPreDispatch = TryTextureModeKey);

        // Ctrl+P focuses the face-property editor on the Texture/UV tab; otherwise the
        // Properties panel (which now shows face properties too — item 0f).
        _dispatcher.Bind(CommandIds.EditProperties, () =>
        {
            if (TextureToolsActive)
            {
                RebuildTexProps();
                _dispatcher.ShowMessage("Face properties");
            }
            else
            {
                _properties.Refresh();
                _dispatcher.ShowMessage("Properties");
            }
        });
    }

    // ---- Panel ----------------------------------------------------------------

    private Control BuildTexturePanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };

        // Current texture display.
        root.Children.Add(Header("Current Texture"));
        _texCurrentSwatch = new Border { Width = 64, Height = 64, BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1) };
        _texCurrentLabel = new TextBlock { Text = _texCurrent, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var curRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        curRow.Children.Add(_texCurrentSwatch);
        curRow.Children.Add(_texCurrentLabel);
        root.Children.Add(curRow);
        UpdateCurrentTextureDisplay();

        root.Children.Add(Row(
            Btn("Pick Texture", PickTextureFromFace),
            Btn("Apply", ApplyCurrentTexture),
            Btn("Set Wall Default", () => { _settings.DefaultWallTexture = _texCurrent; Persist(); _dispatcher.ShowMessage($"Wall default = {_texCurrent}"); })));
        root.Children.Add(Row(Btn("< Prev", () => CycleTexture(-1)), Btn("Next >", () => CycleTexture(1))));

        // Browser: category + filter + only-used + grid.
        root.Children.Add(Header("Browse"));
        _texCategoryBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _texCategoryBox.SelectionChanged += (_, _) => RebuildThumbnailGrid();
        root.Children.Add(_texCategoryBox);

        _texFilterBox = new TextBox { Watermark = "filter name…", FontSize = 12 };
        _texFilterBox.TextChanged += (_, _) => RebuildThumbnailGrid();
        root.Children.Add(_texFilterBox);

        _texShowOnlyUsed = new CheckBox { Content = "Show Only Used", IsChecked = false };
        _texShowOnlyUsed.IsCheckedChanged += (_, _) => RebuildThumbnailGrid();
        root.Children.Add(_texShowOnlyUsed);

        _texGridInfo = new TextBlock { FontSize = 10, Foreground = Brushes.Gray };
        root.Children.Add(_texGridInfo);
        _texGridHost = new WrapPanel { Orientation = Orientation.Horizontal };
        var gridScroll = new ScrollViewer { Height = 220, Content = _texGridHost, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        root.Children.Add(gridScroll);
        PopulateCategories();

        // Mapping.
        root.Children.Add(Header("Mapping"));
        root.Children.Add(Num("Pixels / meter", _settings.PixelsPerMeter, v => _settings.PixelsPerMeter = Math.Clamp(v, 1f, UvOps.MaxPixelsPerMeter)));
        // Planar Map has no gesture: Ctrl+E is Extrude in Face scope (see CommandCatalog), so
        // Planar is a toolbar button only. Box (Ctrl+Q) / Cylinder (Ctrl+W) are bound in RED Classic.
        root.Children.Add(Row(Btn("Box (Ctrl+Q)", ApplyBoxMap), Btn("Planar", ApplyPlanarMap), Btn("Cylinder (Ctrl+W)", ApplyCylinderMap)));
        var axis = new ComboBox { ItemsSource = new[] { "Cylinder axis X", "Cylinder axis Y", "Cylinder axis Z" }, SelectedIndex = _texCylinderAxis, HorizontalAlignment = HorizontalAlignment.Stretch };
        axis.SelectionChanged += (_, _) => _texCylinderAxis = Math.Max(0, axis.SelectedIndex);
        root.Children.Add(axis);
        root.Children.Add(Row(Btn("Snap Map", ApplySnapMap), Btn("Resize Map…", () => _ = ResizeMapDialogAsync())));
        root.Children.Add(Row(Btn("Flip X", () => ApplyUvFace("Flip map X", (g, fi) => UvOps.FlipU(g.Faces[fi]))), Btn("Flip Y", () => ApplyUvFace("Flip map Y", (g, fi) => UvOps.FlipV(g.Faces[fi])))));
        root.Children.Add(Row(Btn("UV Copy", () => TexCopyUv()), Btn("UV Paste", () => TexPasteUv()), Btn("Scale…", () => _ = ScaleUvDialogAsync())));
        // Item 4: stretch the whole face selection's combined UV bbox to fill one tile,
        // aspect-preserving, as a single undo step.
        root.Children.Add(Row(Btn("Fit", FitUvs), Btn("UV Unwrap…", OpenUvUnwrap)));
        root.Children.Add(Note("Box maps each face on its own axis; Planar shares one projection; Cylinder wraps the chosen axis. Ctrl+C/V copy/paste UVs between faces."));

        // Per-face properties.
        root.Children.Add(Header("Face Properties (Ctrl+P)"));
        _texPropsHost = new StackPanel { Spacing = 3 };
        root.Children.Add(_texPropsHost);
        RebuildTexProps();

        return root;
    }

    private void RefreshTexturePanelSelection()
    {
        RebuildTexProps();
        if (_texShowOnlyUsed?.IsChecked == true)
        {
            RebuildThumbnailGrid();
        }
    }

    // ---- Texture browser ------------------------------------------------------

    private void PopulateCategories()
    {
        if (_texCategoryBox is null)
        {
            return;
        }

        var names = new List<string>();
        if (_session.Vfs is { } vfs)
        {
            names.AddRange(vfs.GetTextureCategories().Select(c => c.ToString()));
        }

        if (names.Count == 0)
        {
            names.Add("(mount an RF install for textures)");
        }

        _texCategoryBox.ItemsSource = names;
        _texCategoryBox.SelectedIndex = names.FindIndex(n => n.StartsWith("All", StringComparison.Ordinal)) is int i && i >= 0 ? i : 0;
        RebuildThumbnailGrid();
    }

    private IReadOnlyList<string> CurrentCategoryFiles()
    {
        if (_session.Vfs is not { } vfs || _texCategoryBox?.SelectedIndex is not int idx || idx < 0)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        return idx < cats.Count ? cats[idx].Files : Array.Empty<string>();
    }

    private void RebuildThumbnailGrid()
    {
        if (_texGridHost is null)
        {
            return;
        }

        _texGridHost.Children.Clear();
        IEnumerable<string> files = CurrentCategoryFiles();

        string filter = _texFilterBox?.Text?.Trim() ?? string.Empty;
        if (filter.Length > 0)
        {
            files = files.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        if (_texShowOnlyUsed?.IsChecked == true)
        {
            HashSet<string> used = UsedTextureNames();
            files = files.Where(f => used.Contains(f));
        }

        var list = files.Take(300).ToList();
        _texGridInfo!.Text = $"{list.Count} textures" + (list.Count == 300 ? " (capped; refine filter)" : string.Empty);

        foreach (string name in list)
        {
            _texGridHost.Children.Add(BuildThumbButton(name));
        }
    }

    private Control BuildThumbButton(string name)
    {
        var img = new Image { Width = 48, Height = 48, Stretch = Stretch.Fill };
        var btn = new Button
        {
            Width = 56,
            Height = 66,
            Margin = new Avalonia.Thickness(2),
            Padding = new Avalonia.Thickness(2),
            [ToolTip.TipProperty] = name,
            Content = new StackPanel { Children = { img, new TextBlock { Text = Shorten(name), FontSize = 8, MaxWidth = 52, TextTrimming = TextTrimming.CharacterEllipsis } } },
        };
        btn.Click += (_, _) => { _texCurrent = name; UpdateCurrentTextureDisplay(); };
        btn.DoubleTapped += (_, _) => { _texCurrent = name; UpdateCurrentTextureDisplay(); ApplyCurrentTexture(); };
        LoadThumbAsync(img, name);
        return btn;
    }

    private static string Shorten(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private async void LoadThumbAsync(Image img, string name)
    {
        if (_session.Vfs is not { } vfs)
        {
            return;
        }

        try
        {
            byte[]? png = await Task.Run(() =>
            {
                try
                {
                    return _thumbs.GetOrCreate(name, "vfs1", () =>
                    {
                        DecodedTexture? d = vfs.LoadTexture(name);
                        return d?.Primary ?? new TextureImage(1, 1, new byte[4]);
                    });
                }
                catch (Exception)
                {
                    return null;
                }
            });

            if (png is null)
            {
                return;
            }

            using var ms = new MemoryStream(png);
            img.Source = new Bitmap(ms);
        }
        catch (Exception)
        {
            // Non-fatal: leave the thumbnail blank.
        }
    }

    private void UpdateCurrentTextureDisplay()
    {
        if (_texCurrentLabel is not null)
        {
            _texCurrentLabel.Text = _texCurrent;
        }

        if (_texCurrentSwatch is not null)
        {
            var img = new Image { Stretch = Stretch.Fill };
            _texCurrentSwatch.Child = img;
            LoadThumbAsync(img, _texCurrent);
        }
    }

    private void CycleTexture(int direction)
    {
        var files = CurrentCategoryFiles();
        if (files.Count == 0)
        {
            return;
        }

        int cur = files.ToList().FindIndex(f => string.Equals(f, _texCurrent, StringComparison.OrdinalIgnoreCase));
        int next = ((cur < 0 ? 0 : cur) + direction + files.Count) % files.Count;
        _texCurrent = files[next];
        UpdateCurrentTextureDisplay();
    }

    private HashSet<string> UsedTextureNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Brush b in Be?.Brushes ?? Array.Empty<Brush>())
        {
            foreach (string t in b.Geometry.Textures)
            {
                used.Add(t);
            }
        }

        return used;
    }

    // ---- Texture apply / pick -------------------------------------------------

    private void ApplyCurrentTexture()
    {
        if (Be is null || Be.SelectedFaces.Count == 0)
        {
            _dispatcher.ShowMessage("Select faces to texture.");
            return;
        }

        Report(Be.EditSelectedFaces($"Apply {_texCurrent}", (g, fi) =>
            g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, _texCurrent)));
        AfterBrushEdit();
    }

    private void PickTextureFromFace()
    {
        if (Be is null || Be.SelectedFaces.Count == 0)
        {
            _dispatcher.ShowMessage("Select a face to pick its texture.");
            return;
        }

        (int uid, int fi) = Be.SelectedFaces.First();
        if (Be.TextureNameOf(uid, fi) is string name)
        {
            _texCurrent = name;
            UpdateCurrentTextureDisplay();
            _dispatcher.ShowMessage($"Picked {name}");
        }
    }

    // ---- Mapping --------------------------------------------------------------

    private float Ppm => UvOps.ClampPpm(_settings.PixelsPerMeter);

    private (int W, int H) TexDims(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return (UvOps.DefaultTextureSize, UvOps.DefaultTextureSize);
        }

        if (_texDimCache.TryGetValue(name, out (int W, int H) dims))
        {
            return dims;
        }

        (int, int) result = (UvOps.DefaultTextureSize, UvOps.DefaultTextureSize);
        try
        {
            DecodedTexture? d = _session.Vfs?.LoadTexture(name);
            if (d is not null && d.Width > 0 && d.Height > 0)
            {
                result = (d.Width, d.Height);
            }
        }
        catch (Exception)
        {
            // Use defaults on any decode failure.
        }

        _texDimCache[name] = result;
        return result;
    }

    private (int W, int H) FaceTexDims(Geometry g, int faceIndex)
    {
        Face f = g.Faces[faceIndex];
        string? name = f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : null;
        return TexDims(name);
    }

    private void ApplyBoxMap()
    {
        float ppm = Ppm;
        Report(Be?.EditSelectedFaces("Box map", (g, fi) =>
        {
            (int w, int h) = FaceTexDims(g, fi);
            UvOps.BoxMap(g, g.Faces[fi], ppm, w, h);
        }) ?? OpResult.Fail("Select faces."));
        AfterBrushEdit();
    }

    private void ApplyPlanarMap()
    {
        if (Be is null || Be.SelectedFaces.Count == 0)
        {
            _dispatcher.ShowMessage("Select faces to map.");
            return;
        }

        Vec3 refN = FirstSelectedFaceNormal();
        float ppm = Ppm;
        Report(Be.EditSelectedFaces("Planar map", (g, fi) =>
        {
            (int w, int h) = FaceTexDims(g, fi);
            UvOps.PlanarMap(g, new[] { fi }, refN, ppm, w, h);
        }));
        AfterBrushEdit();
    }

    private void ApplyCylinderMap()
    {
        float ppm = Ppm;
        int axis = _texCylinderAxis;
        Report(Be?.EditSelectedFaces("Cylinder map", (g, fi) =>
        {
            (int w, int h) = FaceTexDims(g, fi);
            UvOps.CylinderMap(g, g.Faces[fi], axis, ppm, w, h);
        }) ?? OpResult.Fail("Select faces."));
        AfterBrushEdit();
    }

    private void ApplySnapMap()
    {
        float ppm = Ppm;
        float grid = Math.Max(0.03125f, _settings.GridSize);
        Report(Be?.EditSelectedFaces("Snap map", (g, fi) =>
        {
            (int w, _) = FaceTexDims(g, fi);
            float step = grid * ppm / Math.Max(1, w);
            UvOps.SnapToGrid(g.Faces[fi], step);
        }) ?? OpResult.Fail("Select faces."));
        AfterBrushEdit();
    }

    private void ApplyUvFace(string desc, Action<Geometry, int> op)
    {
        Report(Be?.EditSelectedFaces(desc, op) ?? OpResult.Fail("Select faces."));
        AfterBrushEdit();
    }

    /// <summary>
    /// Texture-mode key interception for gestures owned by Global commands: H flips
    /// the map in X, V flips in Y. Returns true when consumed.
    /// </summary>
    private bool TryTextureModeKey(KeyGesture gesture)
    {
        if (!TextureToolsActive || gesture.Modifiers != GestureModifiers.None)
        {
            return false;
        }

        if (gesture.Key == "H")
        {
            ApplyUvFace("Flip map X", (g, fi) => UvOps.FlipU(g.Faces[fi]));
            return true;
        }

        if (gesture.Key == "V")
        {
            ApplyUvFace("Flip map Y", (g, fi) => UvOps.FlipV(g.Faces[fi]));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Item 4 — Fit: stretches the selected faces' combined UV bounding box to fill one [0,1]
    /// tile, aspect-preserving (uniform scale, shorter axis centred), across a multi-face
    /// selection as one bbox. The transform is computed once from the pre-edit UVs, then
    /// applied per face through <see cref="BrushEditor.EditSelectedFaces"/> so the whole
    /// operation is a single undo step, consistent with the neighbouring UV operators.
    /// </summary>
    private void FitUvs()
    {
        if (Be is null || Be.SelectedFaces.Count == 0)
        {
            _dispatcher.ShowMessage("Select face(s) to fit.");
            return;
        }

        var faces = TexSelectedFaces().Select(t => t.F).ToList();
        if (faces.Count == 0)
        {
            return;
        }

        UvOps.UvFitTransform fit = UvOps.ComputeFitTransform(faces);
        Report(Be.EditSelectedFaces("Fit UVs to tile", (g, fi) => UvOps.ApplyFit(g.Faces[fi], fit)));
        AfterBrushEdit();
    }

    private void OpenUvUnwrap()
    {
        if (Be is null || Be.SelectedFaces.Count == 0)
        {
            _dispatcher.ShowMessage("Select face(s) to unwrap.");
            return;
        }

        var win = new Dialogs.UvUnwrapWindow(Be, LoadTextureBitmap, () => { AfterBrushEdit(); }, _settings, Persist);
        win.Show(this);
    }

    private Bitmap? LoadTextureBitmap(string name)
    {
        DecodedTexture? d = _session.Vfs?.LoadTexture(name);
        if (d is null)
        {
            return null;
        }

        byte[] png = PngWriter.Encode(d.Primary);
        return new Bitmap(new MemoryStream(png));
    }

    private Vec3 FirstSelectedFaceNormal()
    {
        (int uid, int fi) = Be!.SelectedFaces.First();
        Brush? b = Be.FindBrush(uid);
        return b is not null && fi >= 0 && fi < b.Geometry.Faces.Count ? b.Geometry.Faces[fi].Plane.Normal : new Vec3(0, 0, 1);
    }

    private bool TexCopyUv()
    {
        if (Be is null || Be.SelectedFaces.Count == 0)
        {
            return false;
        }

        (int uid, int fi) = Be.SelectedFaces.First();
        Brush? b = Be.FindBrush(uid);
        if (b is null || fi < 0 || fi >= b.Geometry.Faces.Count)
        {
            return false;
        }

        _uvClipboard = UvOps.Copy(b.Geometry.Faces[fi]);
        _dispatcher.ShowMessage($"Copied UVs ({_uvClipboard.Length} corners).");
        return true;
    }

    private bool TexPasteUv()
    {
        if (_uvClipboard is null || Be is null || Be.SelectedFaces.Count == 0)
        {
            return false;
        }

        Uv[] uvs = _uvClipboard;
        Report(Be.EditSelectedFaces("Paste UVs", (g, fi) => UvOps.Paste(g.Faces[fi], uvs)));
        AfterBrushEdit();
        return true;
    }

    private async Task ResizeMapDialogAsync()
    {
        string? text = await Dialogs.InputDialog.ShowAsync(this, "Resize Map", "Scale U V:", "2 2");
        if (TryParse2(text, out float su, out float sv))
        {
            ApplyUvFace("Resize map", (g, fi) => UvOps.Scale(g.Faces[fi], su, sv));
        }
    }

    private async Task ScaleUvDialogAsync()
    {
        string? text = await Dialogs.InputDialog.ShowAsync(this, "Scale UVs", "Scale U V:", "1 1");
        if (TryParse2(text, out float su, out float sv))
        {
            ApplyUvFace("Scale UVs", (g, fi) => UvOps.Scale(g.Faces[fi], su, sv));
        }
    }

    private static bool TryParse2(string? s, out float a, out float b)
    {
        a = b = 1f;
        string[] parts = (s ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
    }

    // ---- Per-face properties --------------------------------------------------

    private List<(Geometry G, Face F)> TexSelectedFaces()
    {
        var list = new List<(Geometry, Face)>();
        if (Be is null)
        {
            return list;
        }

        foreach ((int uid, int fi) in Be.SelectedFaces)
        {
            if (Be.FindBrush(uid) is Brush b && fi >= 0 && fi < b.Geometry.Faces.Count)
            {
                list.Add((b.Geometry, b.Geometry.Faces[fi]));
            }
        }

        return list;
    }

    private void RebuildTexProps()
    {
        if (_texPropsHost is null)
        {
            return;
        }

        _texPropsHost.Children.Clear();
        var faces = TexSelectedFaces();
        if (faces.Count == 0)
        {
            _texPropsHost.Children.Add(new TextBlock { Text = "Select face(s) to edit properties.", FontSize = 11, Foreground = Brushes.Gray });
            return;
        }

        _texPropsHost.Children.Add(new TextBlock { Text = $"{faces.Count} face(s)", FontSize = 11, Foreground = Brushes.Gray });

        // Genuinely-authored flags (user-selectable).
        _texPropsHost.Children.Add(FlagCheck("Full-bright", faces, FaceFlags.FullBright));
        _texPropsHost.Children.Add(FlagCheck("Show Sky", faces, FaceFlags.ShowSky));
        _texPropsHost.Children.Add(FlagCheck("Mirrored", faces, FaceFlags.Mirrored));

        // Build-derived flags: RED generates these at build time from the texture and brush
        // (never as user-set face attributes), so they are shown read-only rather than editable.
        _texPropsHost.Children.Add(Header("Build-derived (read-only)"));
        _texPropsHost.Children.Add(FlagIndicator("Has Alpha", faces, FaceFlags.HasAlpha));
        _texPropsHost.Children.Add(FlagIndicator("Has Holes", faces, FaceFlags.HasHoles));
        _texPropsHost.Children.Add(FlagIndicator("Invisible", faces, FaceFlags.IsInvisible));
        _texPropsHost.Children.Add(FlagIndicator("Liquid Surface", faces, FaceFlags.LiquidSurface));
        _texPropsHost.Children.Add(FlagIndicator("Detail", faces, FaceFlags.IsDetail));

        // Scroll velocities.
        Uv scroll0 = FaceProps.GetScroll(faces[0].G, faces[0].F);
        bool scrollMixed = faces.Any(t => !FaceProps.GetScroll(t.G, t.F).Equals(scroll0));
        _texPropsHost.Children.Add(Num("Scroll U (px/s)", scrollMixed ? 0f : scroll0.U, u => SetScroll(u, null)));
        _texPropsHost.Children.Add(Num("Scroll V (px/s)", scrollMixed ? 0f : scroll0.V, v => SetScroll(null, v)));

        // Lightmap resolution.
        int res0 = FaceProps.GetLightmapResolution(faces[0].F);
        bool resMixed = faces.Any(t => FaceProps.GetLightmapResolution(t.F) != res0);
        var resCombo = new ComboBox
        {
            ItemsSource = new[] { "Lowest", "Low", "High", "Highest" },
            SelectedIndex = resMixed ? -1 : res0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        resCombo.SelectionChanged += (_, _) =>
        {
            if (resCombo.SelectedIndex >= 0)
            {
                int r = resCombo.SelectedIndex;
                Report(Be!.EditSelectedFaces("Lightmap resolution", (g, fi) => FaceProps.SetLightmapResolution(g.Faces[fi], r)));
                AfterBrushEdit();
            }
        };
        _texPropsHost.Children.Add(Labeled("Lightmap Resolution", resCombo));

        // Smoothing groups (32-bit mask).
        _texPropsHost.Children.Add(Header("Smoothing Groups"));
        _texPropsHost.Children.Add(BuildSmoothingGroupGrid(faces));
    }

    private Control FlagCheck(string label, List<(Geometry G, Face F)> faces, FaceFlags flag)
    {
        bool all = faces.All(t => FaceProps.Get(t.F, flag));
        bool none = faces.All(t => !FaceProps.Get(t.F, flag));
        var cb = new CheckBox { Content = label, IsThreeState = !all && !none, IsChecked = all ? true : (none ? false : (bool?)null) };
        cb.IsCheckedChanged += (_, _) =>
        {
            bool val = cb.IsChecked ?? false;
            cb.IsThreeState = false;
            Report(Be!.EditSelectedFaces($"Set {label}", (g, fi) => FaceProps.Set(g.Faces[fi], flag, val)));
            AfterBrushEdit();
        };
        return cb;
    }

    /// <summary>
    /// Read-only indicator for a build-derived flag: a disabled checkbox reflecting the current
    /// stored value (three-state across a mixed selection), with a tooltip explaining it is
    /// generated by the build. It carries no change handler, so it never edits the face.
    /// </summary>
    private static Control FlagIndicator(string label, List<(Geometry G, Face F)> faces, FaceFlags flag)
    {
        bool all = faces.All(t => FaceProps.Get(t.F, flag));
        bool none = faces.All(t => !FaceProps.Get(t.F, flag));
        return new CheckBox
        {
            Content = label,
            IsEnabled = false,
            IsHitTestVisible = false,
            IsThreeState = !all && !none,
            IsChecked = all ? true : (none ? false : (bool?)null),
            [ToolTip.TipProperty] = "Determined by the build from the texture and brush.",
        };
    }

    private void SetScroll(float? u, float? v)
    {
        var faces = TexSelectedFaces();
        if (faces.Count == 0)
        {
            return;
        }

        Report(Be!.EditSelectedFaces("Set scroll", (g, fi) =>
        {
            Uv cur = FaceProps.GetScroll(g, g.Faces[fi]);
            FaceProps.SetScroll(g, g.Faces[fi], u ?? cur.U, v ?? cur.V);
        }));
        AfterBrushEdit();
    }

    private Control BuildSmoothingGroupGrid(List<(Geometry G, Face F)> faces)
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 260 };
        for (int g = 0; g < 32; g++)
        {
            int group = g;
            bool all = faces.All(t => FaceProps.GetSmoothingGroup(t.F, group));
            bool none = faces.All(t => !FaceProps.GetSmoothingGroup(t.F, group));
            var tb = new ToggleButton
            {
                Content = group.ToString(CultureInfo.InvariantCulture),
                Width = 28,
                Height = 24,
                FontSize = 9,
                Margin = new Avalonia.Thickness(1),
                IsChecked = all ? true : (none ? false : (bool?)null),
                IsThreeState = !all && !none,
            };
            tb.IsCheckedChanged += (_, _) =>
            {
                bool val = tb.IsChecked ?? false;
                tb.IsThreeState = false;
                Report(Be!.EditSelectedFaces($"Smoothing group {group}", (geo, fi) => FaceProps.SetSmoothingGroup(geo.Faces[fi], group, val)));
                AfterBrushEdit();
            };
            grid.Children.Add(tb);
        }

        return grid;
    }
}
