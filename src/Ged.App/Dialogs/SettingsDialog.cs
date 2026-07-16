using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.App.Camera;
using Ged.Core.Input;
using AvDock = Avalonia.Controls.Dock;
using CoreGesture = Ged.Core.Input.KeyGesture;

namespace Ged.App.Dialogs;

/// <summary>
/// The tabbed settings dialog: General, Viewport, Input (camera scheme + keymap
/// editor with presets, rebinding and inline conflict warnings) and Theme. Applies
/// live through <paramref name="onApply"/> and persists on close.
/// </summary>
internal sealed class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly Keymap _keymap;
    private readonly CommandRegistry _registry;
    private readonly Action _onApply;
    private readonly System.Func<string?, Ged.Core.Assets.RfInstallScan>? _onRfInstallChanged;

    private string? _capturingCommandId;
    private Button? _capturingButton;
    private StackPanel? _keymapList;

    public SettingsDialog(AppSettings settings, Keymap keymap, CommandRegistry registry, Action onApply,
        System.Func<string?, Ged.Core.Assets.RfInstallScan>? onRfInstallChanged = null)
    {
        _settings = settings;
        _keymap = keymap;
        _registry = registry;
        _onApply = onApply;
        _onRfInstallChanged = onRfInstallChanged;

        Title = "Settings";
        Width = 640;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "General", Content = GeneralTab() },
                new TabItem { Header = "Viewport", Content = ViewportTab() },
                new TabItem { Header = "Texture", Content = TextureTab() },
                new TabItem { Header = "Input", Content = InputTab() },
                new TabItem { Header = "Theme", Content = ThemeTab() },
            },
        };

        var close = new Button { Content = "Close", IsDefault = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close();

        var search = new TextBox { Watermark = "Search settings…", Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        search.TextChanged += (_, _) => FilterRows(tabs, search.Text ?? string.Empty);

        var root = new DockPanel { Margin = new Avalonia.Thickness(10) };
        DockPanel.SetDock(search, AvDock.Top);
        DockPanel.SetDock(close, AvDock.Bottom);
        root.Children.Add(search);
        root.Children.Add(close);
        root.Children.Add(tabs);
        Content = root;

        KeyDown += OnCaptureKey;
        Closed += (_, _) => { SettingsStore.Save(_settings); KeymapStore.Save(_keymap); };
    }

    // ---- Tabs ----

    private Control GeneralTab()
    {
        var panel = FormPanel();
        panel.Children.Add(CheckRow("Autosave enabled", _settings.AutosaveEnabled, v => _settings.AutosaveEnabled = v));
        panel.Children.Add(NumberRow("Autosave interval (min)", _settings.AutosaveIntervalMinutes,
            v => _settings.AutosaveIntervalMinutes = Math.Max(1, (int)v)));
        panel.Children.Add(CheckRow("Prompt to save on close", _settings.PromptForSave, v => _settings.PromptForSave = v));
        panel.Children.Add(CheckRow("Suppress legacy-level (pre-Alpine) open warning", _settings.SuppressLegacyWarning, v => _settings.SuppressLegacyWarning = v));
        panel.Children.Add(NumberRow("Default nav point height", _settings.NavPointHeight, v => _settings.NavPointHeight = (float)v));
        panel.Children.Add(RfInstallRow());
        panel.Children.Add(GameExeRow());
        panel.Children.Add(TextRow("Playtest extra args", _settings.PlaytestExtraArgs, v => _settings.PlaytestExtraArgs = v.Trim()));
        panel.Children.Add(TextRow("Playtest launch template (blank = direct; e.g. wine {exe} {args})", _settings.PlaytestLaunchTemplate, v => _settings.PlaytestLaunchTemplate = v.Trim()));
        panel.Children.Add(TextRow("Prefab directory (blank = default)", _settings.PrefabDirectory, v => _settings.PrefabDirectory = v.Trim()));
        return Scroll(panel);
    }

    private Control ViewportTab()
    {
        var panel = FormPanel();
        panel.Children.Add(RendererRow());
        panel.Children.Add(NumberRow("Far clip", _settings.FarClip, v => { _settings.FarClip = (float)v; _onApply(); }));
        panel.Children.Add(NumberRow("Grid size (m)", _settings.GridSize, v => { _settings.GridSize = (float)v; _onApply(); }));
        panel.Children.Add(NumberRow("Grid brightness", _settings.GridBrightness, v => { _settings.GridBrightness = (float)v; _onApply(); }));
        panel.Children.Add(NumberRow("Rotation step (deg)", _settings.RotationStep, v => _settings.RotationStep = (float)v));
        panel.Children.Add(NumberRow("Scale step (fraction, e.g. 0.05)", _settings.ScaleStep, v => { _settings.ScaleStep = (float)v; _onApply(); }));
        panel.Children.Add(CheckRow("Snap mouse drags to grid (magnet)", _settings.SnapEnabled, v => { _settings.SnapEnabled = v; _onApply(); }));
        panel.Children.Add(NumberRow("Camera speed", _settings.CameraSpeed, v => { _settings.CameraSpeed = (float)v; _onApply(); }));
        panel.Children.Add(CheckRow("Use original object icons (from game files)", _settings.UseOriginalIcons, v => { _settings.UseOriginalIcons = v; _onApply(); }));
        panel.Children.Add(CheckRow("Show all ranges (light/region spheres for every object)", _settings.ShowAllRanges, v => { _settings.ShowAllRanges = v; _onApply(); }));
        panel.Children.Add(CheckRow("Disable backface culling (render both faces of solid geometry)", _settings.DisableBackfaceCulling, v => { _settings.DisableBackfaceCulling = v; _onApply(); }));

        panel.Children.Add(SectionHeader("Element colors"));
        var resetColors = new Button { Content = "Reset colors to RED defaults" };
        var colorPanel = new StackPanel { Spacing = 4 };
        resetColors.Click += (_, _) => { ResetColorsToDefault(); colorPanel.Children.Clear(); PopulateColorRows(colorPanel); _onApply(); };
        panel.Children.Add(resetColors);
        PopulateColorRows(colorPanel);
        panel.Children.Add(colorPanel);
        return Scroll(panel);
    }

    private void PopulateColorRows(StackPanel c)
    {
        c.Children.Add(ColorRow("Background", _settings.ColorBackground, v => { _settings.ColorBackground = v; _onApply(); }));
        c.Children.Add(ColorRow("Grid", _settings.ColorGrid, v => _settings.ColorGrid = v));
        c.Children.Add(ColorRow("Cookie cutter", _settings.ColorCookieCutter, v => _settings.ColorCookieCutter = v));
        c.Children.Add(ColorRow("Brush", _settings.ColorBrush, v => _settings.ColorBrush = v));
        c.Children.Add(ColorRow("Locked brush", _settings.ColorBrushLocked, v => _settings.ColorBrushLocked = v));
        c.Children.Add(ColorRow("Detail brush", _settings.ColorBrushDetail, v => _settings.ColorBrushDetail = v));
        c.Children.Add(ColorRow("Portal", _settings.ColorBrushPortal, v => _settings.ColorBrushPortal = v));
        c.Children.Add(ColorRow("Mover", _settings.ColorMover, v => _settings.ColorMover = v));
        c.Children.Add(ColorRow("Links", _settings.ColorLinks, v => { _settings.ColorLinks = v; _onApply(); }));
        c.Children.Add(ColorRow("Nodes", _settings.ColorNodes, v => { _settings.ColorNodes = v; _onApply(); }));
        c.Children.Add(ColorRow("Bounding boxes", _settings.ColorBoundingBox, v => { _settings.ColorBoundingBox = v; _onApply(); }));
        c.Children.Add(ColorRow("Triggers", _settings.ColorTriggers, v => _settings.ColorTriggers = v));
        c.Children.Add(ColorRow("Regions", _settings.ColorRegions, v => { _settings.ColorRegions = v; _onApply(); }));
    }

    /// <summary>Resets every element colour to its default (RED stock values; item 8).</summary>
    private void ResetColorsToDefault()
    {
        var d = new AppSettings();
        _settings.ColorBackground = d.ColorBackground;
        _settings.ColorGrid = d.ColorGrid;
        _settings.ColorCookieCutter = d.ColorCookieCutter;
        _settings.ColorBrush = d.ColorBrush;
        _settings.ColorBrushLocked = d.ColorBrushLocked;
        _settings.ColorBrushDetail = d.ColorBrushDetail;
        _settings.ColorBrushPortal = d.ColorBrushPortal;
        _settings.ColorMover = d.ColorMover;
        _settings.ColorLinks = d.ColorLinks;
        _settings.ColorNodes = d.ColorNodes;
        _settings.ColorBoundingBox = d.ColorBoundingBox;
        _settings.ColorTriggers = d.ColorTriggers;
        _settings.ColorRegions = d.ColorRegions;
    }

    private Control TextureTab()
    {
        var panel = FormPanel();
        panel.Children.Add(SectionHeader("Default textures (applied by face orientation at creation)"));
        panel.Children.Add(TextRow("Ceiling texture", _settings.DefaultCeilingTexture, v => _settings.DefaultCeilingTexture = v));
        panel.Children.Add(TextRow("Wall texture", _settings.DefaultWallTexture, v => _settings.DefaultWallTexture = v));
        panel.Children.Add(TextRow("Floor texture", _settings.DefaultFloorTexture, v => _settings.DefaultFloorTexture = v));
        panel.Children.Add(SectionHeader("Mapping"));
        panel.Children.Add(NumberRow("Pixels / meter (≤ 8192)", _settings.PixelsPerMeter,
            v => _settings.PixelsPerMeter = Math.Clamp((float)v, 1f, 8192f)));
        panel.Children.Add(new TextBlock
        {
            Text = "Pick the exact texture names from the Texture-mode browser (Current Texture → set as default).",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        });
        return Scroll(panel);
    }

    private Control InputTab()
    {
        var panel = FormPanel();

        var scheme = new ComboBox { ItemsSource = Enum.GetValues<CameraSchemeKind>(), SelectedIndex = _settings.CameraScheme, MinWidth = 160 };
        scheme.SelectionChanged += (_, _) =>
        {
            if (scheme.SelectedItem is CameraSchemeKind k)
            {
                _settings.CameraScheme = (int)k;
                _onApply();
            }
        };
        panel.Children.Add(LabeledRow("Camera scheme", scheme));

        var preset = new ComboBox { ItemsSource = CommandCatalog.PresetNames, SelectedItem = _keymap.PresetName, MinWidth = 160 };
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedItem is string name && name != _keymap.PresetName)
            {
                _keymap.ApplyPreset(name);
                RebuildKeymapList();
            }
        };
        panel.Children.Add(LabeledRow("Keymap preset", preset));

        var resetAll = new Button { Content = "Reset all overrides" };
        resetAll.Click += (_, _) => { _keymap.ResetAll(); RebuildKeymapList(); };
        panel.Children.Add(resetAll);

        panel.Children.Add(SectionHeader("Hotkeys (click a gesture to rebind, Esc to cancel)"));
        _keymapList = new StackPanel { Spacing = 1 };
        RebuildKeymapList();
        panel.Children.Add(_keymapList);
        return Scroll(panel);
    }

    private Control ThemeTab()
    {
        var panel = FormPanel();
        var dark = new RadioButton { Content = "Dark", IsChecked = _settings.DarkTheme, GroupName = "theme" };
        var light = new RadioButton { Content = "Light", IsChecked = !_settings.DarkTheme, GroupName = "theme" };
        dark.IsCheckedChanged += (_, _) => { if (dark.IsChecked == true) { _settings.DarkTheme = true; _onApply(); } };
        light.IsCheckedChanged += (_, _) => { if (light.IsChecked == true) { _settings.DarkTheme = false; _onApply(); } };
        panel.Children.Add(new TextBlock { Text = "Application theme", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(dark);
        panel.Children.Add(light);
        return Scroll(panel);
    }

    // ---- Keymap editor ----

    private void RebuildKeymapList()
    {
        if (_keymapList is null)
        {
            return;
        }

        _keymapList.Children.Clear();
        IReadOnlyList<KeyConflict> conflicts = _keymap.FindConflicts(_registry);
        var conflictedIds = new HashSet<string>(conflicts.SelectMany(c => c.CommandIds), StringComparer.Ordinal);

        foreach (string category in _registry.Categories)
        {
            _keymapList.Children.Add(new TextBlock
            {
                Text = category,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.7,
                Margin = new Avalonia.Thickness(0, 6, 0, 2),
            });

            foreach (CommandDefinition def in _registry.InCategory(category))
            {
                _keymapList.Children.Add(KeymapRow(def, conflictedIds.Contains(def.Id)));
            }
        }
    }

    private Control KeymapRow(CommandDefinition def, bool conflicted)
    {
        var grid = new Grid { Margin = new Avalonia.Thickness(0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var name = new TextBlock
        {
            Text = def.DisplayName + (def.Implemented ? string.Empty : "  (later)"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            Opacity = def.Implemented ? 1.0 : 0.55,
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        CoreGesture? gesture = _keymap.Resolve(def.Id);
        var gestureButton = new Button
        {
            Content = gesture?.Display ?? "—",
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo, monospace"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = conflicted ? Brushes.OrangeRed : Brushes.Gainsboro,
        };
        gestureButton.Click += (_, _) => BeginCapture(def.Id, gestureButton);
        Grid.SetColumn(gestureButton, 1);
        grid.Children.Add(gestureButton);

        var reset = new Button { Content = "⟲", FontSize = 11, [ToolTip.TipProperty] = "Reset to preset" };
        reset.Click += (_, _) => { _keymap.ResetBinding(def.Id); RebuildKeymapList(); };
        Grid.SetColumn(reset, 2);
        grid.Children.Add(reset);

        return grid;
    }

    private void BeginCapture(string commandId, Button button)
    {
        _capturingCommandId = commandId;
        _capturingButton = button;
        button.Content = "press a key…";
        Focus();
    }

    private void OnCaptureKey(object? sender, KeyEventArgs e)
    {
        if (_capturingCommandId is null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
        {
            return; // wait for the non-modifier key
        }

        if (e.Key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        CoreGesture? g = Services.GestureConvert.FromAvalonia(e.Key, e.KeyModifiers);
        if (g is CoreGesture gesture)
        {
            _keymap.Rebind(_capturingCommandId, gesture);
        }

        _capturingCommandId = null;
        _capturingButton = null;
        RebuildKeymapList();
    }

    private void CancelCapture()
    {
        _capturingCommandId = null;
        if (_capturingButton is not null)
        {
            RebuildKeymapList();
            _capturingButton = null;
        }
    }

    // ---- Form primitives ----

    private static StackPanel FormPanel() => new() { Spacing = 6, Margin = new Avalonia.Thickness(4) };

    private static Control Scroll(Control content) => new ScrollViewer { Content = content };

    private static Control SectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Avalonia.Thickness(0, 10, 0, 2),
    };

    private static Control LabeledRow(string label, Control editor)
    {
        var grid = new Grid { Tag = label };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(190)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        // Labels wrap to multiple lines rather than clipping when they exceed the
        // 190px column; the grid row auto-sizes to the tallest cell (see Task 1f).
        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(editor);
        return grid;
    }

    /// <summary>Filters every tab's rows by a search term against the row labels.</summary>
    private void FilterRows(TabControl tabs, string query)
    {
        query = query.Trim();
        foreach (object? item in tabs.Items)
        {
            if (item is not TabItem tab || tab.Content is not ScrollViewer sv || sv.Content is not StackPanel panel)
            {
                continue;
            }

            foreach (Control child in panel.Children)
            {
                if (child.Tag is string label)
                {
                    child.IsVisible = query.Length == 0 ||
                        label.Contains(query, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private static Control CheckRow(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { IsChecked = value };
        check.IsCheckedChanged += (_, _) => set(check.IsChecked ?? false);
        return LabeledRow(label, check);
    }

    /// <summary>
    /// The renderer (GPU backend) selector: Direct3D 11 (default) or OpenGL. The shared
    /// GPU device is created once at startup, so the choice is restart-scoped — the row
    /// carries an explicit note and persists to <see cref="AppSettings.Renderer"/>.
    /// </summary>
    private Control RendererRow()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "Direct3D 11 (default)", "OpenGL" },
            SelectedIndex = _settings.Renderer == (int)Ged.Rendering.Graphics.GraphicsBackend.OpenGl ? 1 : 0,
            MinWidth = 200,
        };
        combo.SelectionChanged += (_, _) =>
        {
            _settings.Renderer = combo.SelectedIndex == 1
                ? (int)Ged.Rendering.Graphics.GraphicsBackend.OpenGl
                : (int)Ged.Rendering.Graphics.GraphicsBackend.Direct3D11;
        };

        var note = new TextBlock
        {
            Text = "Takes effect after restarting Glacier. OpenGL is the cross-platform, composited backend.",
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(combo);
        stack.Children.Add(note);
        return LabeledRow("Renderer (restart required)", stack);
    }

    /// <summary>The RF install-path row with live validation (item 7): as-you-type feedback
    /// plus a live remount (via the host callback) on commit.</summary>
    private Control RfInstallRow()
    {
        var box = new TextBox { Text = _settings.RfInstallDir ?? string.Empty };
        var feedback = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        void ShowValidation(string? path)
        {
            Ged.Core.Assets.RfInstallScan scan = Ged.Core.Assets.RfInstall.Scan(path);
            feedback.Text = scan.StatusText();
            feedback.Foreground = scan.Valid ? Brushes.LightGreen : Brushes.OrangeRed;
        }

        box.GetObservable(TextBox.TextProperty).Subscribe(new Ged.App.Panels.AnonymousObserver(t => ShowValidation(t)));
        box.LostFocus += (_, _) =>
        {
            string v = box.Text ?? string.Empty;
            _settings.RfInstallDir = v;
            // Live remount + consumer refresh; the host reports the validated scan.
            Ged.Core.Assets.RfInstallScan scan = _onRfInstallChanged?.Invoke(v) ?? Ged.Core.Assets.RfInstall.Scan(v);
            feedback.Text = scan.StatusText();
            feedback.Foreground = scan.Valid ? Brushes.LightGreen : Brushes.OrangeRed;
        };
        ShowValidation(_settings.RfInstallDir);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(LabeledRow("RF install path", box));
        stack.Children.Add(feedback);
        return stack;
    }

    private static Control TextRow(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value };
        box.LostFocus += (_, _) => set(box.Text ?? string.Empty);
        return LabeledRow(label, box);
    }

    /// <summary>
    /// The Alpine Faction launcher path play-tests run through (AlpineFactionLauncher.exe).
    /// A text box plus a Browse… file picker; the trimmed value persists on Close. This is the
    /// single game-executable prompt — GED no longer asks for a stock RF.exe launch path.
    /// </summary>
    private Control GameExeRow()
    {
        var box = new TextBox { Text = _settings.GameExePath };
        box.LostFocus += (_, _) =>
        {
            _settings.GameExePath = (box.Text ?? string.Empty).Trim();
            // Re-apply so the Alpine object icons re-resolve from alpinefaction.vpp beside the
            // new launcher path (item 3, same live-refresh pattern as the icon setting).
            _onApply();
        };

        var browse = new Button { Content = "Browse…", Margin = new Avalonia.Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Locate AlpineFactionLauncher.exe",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } } },
                });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is string p)
            {
                box.Text = p;
                _settings.GameExePath = p.Trim();
                _onApply();
            }
        };

        var row = new DockPanel();
        DockPanel.SetDock(browse, AvDock.Right);
        row.Children.Add(browse);
        row.Children.Add(box); // fills the remaining width
        return LabeledRow("Alpine Faction launcher (AlpineFactionLauncher.exe)", row);
    }

    private static Control NumberRow(string label, double value, Action<double> set)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Left };
        box.LostFocus += (_, _) =>
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                set(v);
            }
        };
        return LabeledRow(label, box);
    }

    private static Control ColorRow(string label, string hex, Action<string> set)
    {
        var swatch = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1) };
        TrySetSwatch(swatch, hex);
        var box = new TextBox { Text = hex, Width = 100 };
        box.LostFocus += (_, _) =>
        {
            string v = box.Text ?? hex;
            set(v);
            TrySetSwatch(swatch, v);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, box } };
        return LabeledRow(label, row);
    }

    private static void TrySetSwatch(Border swatch, string hex)
    {
        try
        {
            swatch.Background = new SolidColorBrush(Color.Parse(hex));
        }
        catch (FormatException)
        {
            // leave previous
        }
    }
}
