using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ged.App.Controls;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.App.Dialogs;

/// <summary>What the Level Properties dialog asks of the editor around it (all optional; tests inject their own).</summary>
internal sealed class LevelPropertiesServices
{
    /// <summary>The active viewport camera's forward vector (Sunlight ▸ Set from camera), or null.</summary>
    public Func<Vector3?>? CameraForward { get; set; }

    /// <summary>Picks a file of a kind ("texture" or "bitmap") and returns its file name, or null.</summary>
    public Func<string, Task<string?>>? BrowseFile { get; set; }

    /// <summary>Picks a colour, or null when cancelled.</summary>
    public Func<RfColor, Task<RfColor?>>? PickColor { get; set; }

    /// <summary>Asks a yes/no question (title, message).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Loads a bitmap for the minimap preview box, or null.</summary>
    public Func<string, Bitmap?>? LoadBitmap { get; set; }

    /// <summary>Shows a status message.</summary>
    public Action<string>? ShowMessage { get; set; }
}

/// <summary>
/// Level Properties: Alpine RED's six tabs (General, Lighting, Lightmaps, Gameplay, Minimap, Compatibility) with its
/// groups, field order, labels and helper controls (editor_patch/level.cpp:1458-1626 and resources.rc; the owner's
/// RED screenshots). Modal with OK / Cancel: the fields are edited here and OK writes every change through
/// <see cref="LevelPropertyAccess"/> (the path <c>level.props</c> / <c>level.alpine</c> use) as ONE undo step; Cancel
/// writes nothing. The Level Flags Outside / Inside buttons act at once with RED's warning, as RED's do
/// (<see cref="LevelRoomFlags"/>). OK first checks the minimap fields as Alpine RED does (level.cpp:1966-1990).
/// </summary>
internal sealed class LevelPropertiesDialog : Window
{
    private readonly EditorDocument _doc;
    private readonly LevelPropertiesServices _services;
    private readonly Dictionary<LevelPropertyField, object?> _pending = new();
    private readonly List<Action> _enables = new();
    private readonly TabControl _tabs = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    public LevelPropertiesDialog(EditorDocument doc, LevelPropertiesServices? services = null)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _services = services ?? new LevelPropertiesServices();
        _doc.Rfl.ParseAllKnownSections();

        Title = "Level Properties";
        Width = 580;
        Height = 800;
        MinWidth = 440;
        MinHeight = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _tabs.Items.Add(Tab("General", BuildGeneral()));
        _tabs.Items.Add(Tab("Lighting", BuildLighting()));
        _tabs.Items.Add(Tab("Lightmaps", BuildLightmaps()));
        _tabs.Items.Add(Tab("Gameplay", BuildGameplay()));
        _tabs.Items.Add(Tab("Minimap", BuildMinimap()));
        _tabs.Items.Add(Tab("Compatibility", BuildCompatibility()));
        _tabs.SelectedIndex = 0;

        // Compact tab headers, so the six tabs sit in one row as in Alpine RED.
        _tabs.Resources["TabItemHeaderFontSize"] = 13.0;
        _tabs.Resources["TabItemMinHeight"] = 30.0;

        var ok = new Button { Name = "ok", Content = "OK", IsDefault = true, MinWidth = 88 };
        ok.Click += (_, _) =>
        {
            if (Apply())
            {
                Accepted = true;
                Close();
            }
        };
        var cancel = new Button { Name = "cancel", Content = "Cancel", IsCancel = true, MinWidth = 88 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } };

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(_error, Avalonia.Controls.Dock.Bottom);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(buttons);
        root.Children.Add(_error);
        root.Children.Add(_tabs);
        Content = root;
        UpdateEnables();
    }

    /// <summary>True once OK applied the dialog.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The message OK last refused with (empty when none).</summary>
    internal string ErrorText => _error.Text ?? string.Empty;

    /// <summary>The tab control (test hook: select a tab to render it).</summary>
    internal TabControl Tabs => _tabs;

    // ---- General ------------------------------------------------------------------------------------------

    private Control BuildGeneral()
    {
        var p = Page();
        LevelPropertyField name = Field(LevelPropertyTarget.LevelInfo, "LevelName");
        LevelPropertyField author = Field(LevelPropertyTarget.LevelInfo, "Author");
        LevelPropertyField mp = Field(LevelPropertyTarget.LevelInfo, "MultiplayerLevel");
        bool info = LevelPropertyAccess.HasSection(_doc, LevelPropertyTarget.LevelInfo);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        AddAt(top, Label("Level name:"), 0, 0);
        AddAt(top, Disabled(TextField(name, "level_name"), info), 0, 1);
        var version = new TextBlock { Name = "rfl_version", Text = _doc.Rfl.Header.Version.ToString(CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center };
        AddAt(top, Horizontal(Label("RFL version:"), version), 0, 2);
        AddAt(top, Label("Author:"), 1, 0);
        AddAt(top, Disabled(TextField(author, "author"), info), 1, 1);
        AddAt(top, Disabled(Check(mp, "Multiplayer level", "multiplayer"), info), 1, 2);
        AddAt(top, Label("Date:"), 2, 0);
        string date = LevelPropertyAccess.Get(_doc, Field(LevelPropertyTarget.LevelInfo, "Date")) as string ?? string.Empty;
        var dateText = new TextBlock
        {
            Name = "level_date", Text = date.Length > 0 ? date : "(set automatically on save)", VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(dateText, "Set automatically on save.");
        Grid.SetColumnSpan(dateText, 2);
        AddAt(top, dateText, 2, 1);
        foreach (Control c in top.Children)
        {
            // RED's gaps: the version and Multiplayer level sit clear of the text boxes, and the rows do not touch.
            c.Margin = new Thickness(Grid.GetColumn(c) == 2 ? 14 : 0, 0, 0, Grid.GetRow(c) < 2 ? 6 : 0);
        }

        p.Children.Add(top);

        var outside = new Button { Name = "level_outside", Content = "Outside", MinWidth = 76 };
        var inside = new Button { Name = "level_inside", Content = "Inside", MinWidth = 76 };
        ToolTip.SetTip(outside, "Flags every room of the level as outside (every room effect too).");
        ToolTip.SetTip(inside, "Flags every room of the level as inside (every room effect too).");
        outside.Click += async (_, _) => await FlagRooms(true);
        inside.Click += async (_, _) => await FlagRooms(false);
        p.Children.Add(GroupBox("Level Flags", Horizontal(outside, inside)));

        LevelPropertyField texture = Field(LevelPropertyTarget.LevelProperties, "GeomodTexture");
        LevelPropertyField hardness = Field(LevelPropertyTarget.LevelProperties, "Hardness");
        bool props = LevelPropertyAccess.HasSection(_doc, LevelPropertyTarget.LevelProperties);
        var geo = new StackPanel { Spacing = 8 };
        TextBox textureBox = TextField(texture, "geomod_texture");
        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) =>
        {
            if (_services.BrowseFile is { } pick && await pick("texture") is { Length: > 0 } file)
            {
                textureBox.Text = file;
                Put(texture, file);
            }
        };
        geo.Children.Add(Row("GeoMod texture:", Disabled(Horizontal(Wide(textureBox, 160), browse), props), 110));
        geo.Children.Add(Row("Level hardness:", Disabled(Horizontal(IntBox(hardness, "hardness", 0, 100, 126), Hint("0 (large craters) - 100 (no craters)")), props), 110));
        geo.Children.Add(Check(Alpine("rf2_style_geomod"), "Brush-based geomod (RF2-style)", "rf2_style_geomod"));
        p.Children.Add(GroupBox("GeoMod", geo));

        p.Children.Add(Check(Alpine("require_d3d11_setting"), "Require Direct3D 11 Renderer", "require_d3d11"));
        return p;
    }

    private async Task FlagRooms(bool outside)
    {
        bool go = _services.Confirm is { } confirm
            ? await confirm("Level Properties", LevelRoomFlags.Warning(outside))
            : await ConfirmDialog.ShowAsync(this, "Level Properties", LevelRoomFlags.Warning(outside));
        if (!go)
        {
            return;
        }

        int changed = LevelRoomFlags.SetAllRooms(_doc, outside);
        _services.ShowMessage?.Invoke(changed == 0
            ? $"Every room is already {(outside ? "outside" : "inside")}."
            : $"Flagged every room {(outside ? "outside" : "inside")} ({changed} rooms and room effects changed).");
    }

    // ---- Lighting -----------------------------------------------------------------------------------------

    private Control BuildLighting()
    {
        var p = Page();
        bool props = LevelPropertyAccess.HasSection(_doc, LevelPropertyTarget.LevelProperties);
        LevelPropertyField ambient = Field(LevelPropertyTarget.LevelProperties, "AmbientColor");
        var ambientRow = ColorRow(ambient, "ambient_color", out Action<RfColor> setAmbient);
        var dflt = new Button { Name = "ambient_default", Content = "Default" };
        ToolTip.SetTip(dflt, "RED's new-level ambient light, <40, 40, 40>.");
        dflt.Click += (_, _) => setAmbient(new RfColor(40, 40, 40, ColorOf(ambient).A));
        p.Children.Add(GroupBox("Ambient Light", Disabled(Row("Color:", Horizontal(ambientRow, dflt), 70), props)));

        var fog = new StackPanel { Spacing = 8 };
        fog.Children.Add(Row("Color:", ColorRow(Field(LevelPropertyTarget.LevelProperties, "FogColor"), "fog_color", out _), 108));
        fog.Children.Add(Row("Near clip plane:", Horizontal(FloatBox(Field(LevelPropertyTarget.LevelProperties, "FogNearPlane"), "fog_near", 0, 100000, 1m, 140, spin: true), Hint("(Direct3D 11 only)")), 108));
        fog.Children.Add(Row("Far clip plane:", FloatBox(Field(LevelPropertyTarget.LevelProperties, "FogFarPlane"), "fog_far", 0, 100000, 1m, 140, spin: true), 108));
        fog.Children.Add(Row("Camera far clip:", Horizontal(FloatBox(Alpine("camera_far_clip"), "camera_far_clip", 0, 100000, 1m, 140, "0", spin: true), Hint("(0 = use fog far clip)")), 108));
        p.Children.Add(GroupBox("Distance Based Fog", Disabled(fog, props)));

        LevelPropertyField overrideField = Alpine("override_static_mesh_ambient");
        CheckBox overrideBox = Check(overrideField, "Override static mesh ambient light scale", "override_static_mesh_ambient");
        Control scale = FloatBox(Alpine("static_mesh_ambient"), "static_mesh_ambient", 0, 1000, 0.1m, 80, "0.000");
        p.Children.Add(GroupBox("Static Meshes", new WrapPanel { Children = { overrideBox, Horizontal(Label("   New scale value:"), scale) } }));

        var sun = new StackPanel { Spacing = 6 };
        CheckBox enable = Check(Alpine("sun_enabled"), "Enable directional sunlight", "sun_enabled");
        var fromCamera = new Button { Name = "sun_from_camera", Content = "Set from camera" };
        ToolTip.SetTip(fromCamera, "Aims the sun along the active viewport camera: look the way the sunlight travels.");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        AddAt(top, enable, 0, 0);
        AddAt(top, fromCamera, 0, 1);
        sun.Children.Add(top);
        NumericUpDown yaw = FloatBox(Alpine("sun_yaw"), "sun_yaw", 0, 360, 1m, 66, "0.000");
        NumericUpDown pitch = FloatBox(Alpine("sun_pitch"), "sun_pitch", 0, 90, 1m, 66, "0.000");
        var angles = new WrapPanel
        {
            Children =
            {
                Horizontal(Label("Yaw:"), yaw), Horizontal(Label("Pitch:"), pitch),
                Horizontal(Label("Intensity:"), FloatBox(Alpine("sun_intensity"), "sun_intensity", 0, 10, 0.1m, 66, "0.000")),
                Horizontal(Label("Spread:"), FloatBox(Alpine("sun_spread_angle"), "sun_spread_angle", 0, 45, 0.5m, 66, "0.000")),
            },
        };
        foreach (Control c in angles.Children)
        {
            c.Margin = new Thickness(0, 0, 12, 4);
        }

        sun.Children.Add(angles);
        fromCamera.Click += (_, _) => SunFromCamera(yaw, pitch);
        sun.Children.Add(Row("Color:", SunColorRow(), 50));
        var checks = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        AddAt(checks, Check(Alpine("sun_cast_baked_shadows"), "Casts shadows (lightmaps)", "sun_cast_baked_shadows"), 0, 0);
        AddAt(checks, Check(Alpine("sun_affects_meshes"), "Affects mesh lighting", "sun_affects_meshes"), 0, 1);
        AddAt(checks, MeshModeCheck(), 1, 0);
        AddAt(checks, Check(Alpine("sun_drives_shadowmap_dir"), "Aligns dynamic shadows", "sun_drives_shadowmap_dir"), 1, 1);
        AddAt(checks, Check(Alpine("sun_liquid_occludes"), "Water blocks sunlight", "sun_liquid_occludes"), 2, 0);
        sun.Children.Add(checks);
        p.Children.Add(GroupBox("Sunlight", sun));
        return p;
    }

    /// <summary>Yaw and pitch from the camera: the camera looks along the sun's rays (level.cpp:1352-1371, level.h:937-955).</summary>
    private void SunFromCamera(NumericUpDown yaw, NumericUpDown pitch)
    {
        if (_services.CameraForward?.Invoke() is not { } f || SunAngles(f) is not (float y, float p))
        {
            _services.ShowMessage?.Invoke("No 3D viewport camera to take the sun direction from.");
            return;
        }

        yaw.Value = (decimal)y;
        pitch.Value = (decimal)p;
    }

    /// <summary>The sun yaw and pitch (degrees) of light travelling along <paramref name="travel"/>, or null.</summary>
    internal static (float Yaw, float Pitch)? SunAngles(Vector3 travel)
    {
        float len = travel.Length();
        if (!float.IsFinite(len) || len <= 0f)
        {
            return null;
        }

        Vector3 toSun = -travel / len;
        float yaw = MathF.Atan2(toSun.X, toSun.Z) * (180f / MathF.PI) % 360f;
        if (yaw < 0f)
        {
            yaw += 360f;
        }

        float pitch = Math.Clamp(MathF.Asin(Math.Clamp(toSun.Y, -1f, 1f)) * (180f / MathF.PI), 0f, 90f);
        return (MathF.Round(yaw, 3), MathF.Round(pitch, 3));
    }

    private Control SunColorRow()
    {
        LevelPropertyField r = Alpine("sun_red");
        LevelPropertyField g = Alpine("sun_green");
        LevelPropertyField b = Alpine("sun_blue");
        RfColor Current() => new((byte)(Value(r) is int rr ? rr : 255), (byte)(Value(g) is int gg ? gg : 255), (byte)(Value(b) is int bb ? bb : 255), 255);
        return ColorEditor("sun_color", Current, c =>
        {
            Put(r, (int)c.R);
            Put(g, (int)c.G);
            Put(b, (int)c.B);
        }, out _);
    }

    /// <summary>"Scale mesh sunlight by lightmaps" is sun mesh mode 0 (level.cpp:2370, 2457).</summary>
    private CheckBox MeshModeCheck()
    {
        LevelPropertyField f = Alpine("sun_mesh_mode");
        var cb = new CheckBox { Name = "sun_mesh_mode", Content = "Scale mesh sunlight by lightmaps", IsChecked = Value(f) is int m && m == 0 };
        cb.IsCheckedChanged += (_, _) => Put(f, cb.IsChecked == true ? 0 : 1);
        return cb;
    }

    // ---- Lightmaps ----------------------------------------------------------------------------------------

    private CheckBox? _legacyLighting;

    private Control BuildLightmaps()
    {
        var p = Page();
        p.Children.Add(Check(Alpine("highres_lightmaps"), "High-resolution lightmaps", "highres_lightmaps"));
        var blocking = new StackPanel { Spacing = 4 };
        blocking.Children.Add(Check(Alpine("invisible_faces_occlude"), "Invisible faces block light", "invisible_faces_occlude"));
        blocking.Children.Add(Check(Alpine("alpha_faces_occlude"), "Alpha-textured faces block light", "alpha_faces_occlude"));
        blocking.Children.Add(Check(Alpine("meshes_occlude"), "Meshes block light", "meshes_occlude"));
        CheckBox alphaTested = Check(Alpine("alpha_tested_occlusion"), "Alpha-tested light occlusion", "alpha_tested_occlusion");
        blocking.Children.Add(alphaTested);
        p.Children.Add(GroupBox("Light Blocking", blocking));
        CheckBox d3d11 = Check(Alpine("d3d11_only_lightmaps_setting"), "D3D11-only lightmaps (omit stock lightmaps)", "d3d11_only_lightmaps");
        p.Children.Add(d3d11);

        LevelPropertyField densityField = Alpine("lightmap_density");
        int stored = Value(densityField) is int d ? d : 0;
        IReadOnlyList<(int Value, string Label)> choices = LightmapDensityChoices.For(stored);
        var density = new ComboBox { Name = "lightmap_density", ItemsSource = choices.Select(c => c.Label).ToList(), Width = 120 };
        density.SelectedIndex = choices.ToList().FindIndex(c => c.Value == stored);
        density.SelectionChanged += (_, _) =>
        {
            if (density.SelectedIndex >= 0)
            {
                Put(densityField, choices[density.SelectedIndex].Value);
                UpdateEnables();
            }
        };
        LevelPropertyField compressionField = Alpine("lightmap_compression");
        var compression = new ComboBox
        {
            Name = "lightmap_compression", ItemsSource = new[] { "Quality", "Balanced", "Compact" }, Width = 120,
            SelectedIndex = Math.Clamp(Value(compressionField) is int c ? c : 0, 0, 2),
        };
        compression.SelectionChanged += (_, _) =>
        {
            if (compression.SelectedIndex >= 0)
            {
                Put(compressionField, compression.SelectedIndex);
            }
        };
        p.Children.Add(Horizontal(Label("Lightmap density:"), density, Label("  Compression:"), compression));
        p.Children.Add(Hint("Calculate Lighting bakes with every setting here, as Alpine RED does. High-resolution lightmaps need a Build Geometry; D3D11-only lightmaps take effect when the level is saved with Alpine lightmaps."));

        // Alpine RED's enables (level.cpp:2173-2185): D3D11-only needs Alpine lighting and a density other than Off;
        // alpha-tested occlusion needs the ray-traced shadows legacy lighting drops.
        _enables.Add(() =>
        {
            bool legacy = _legacyLighting?.IsChecked == true;
            bool off = density.SelectedIndex >= 0 && choices[density.SelectedIndex].Value == LightmapDensityChoices.Off;
            d3d11.IsEnabled = !legacy && !off;
            alphaTested.IsEnabled = !legacy;
        });
        return p;
    }

    // ---- Gameplay -----------------------------------------------------------------------------------------

    private Control BuildGameplay()
    {
        var p = Page();
        p.Children.Add(Check(Alpine("starts_with_headlamp"), "Player starts with headlamp", "starts_with_headlamp"));
        p.Children.Add(Check(Alpine("vehicle_flight_ceiling_enabled"), "Limit vehicle flight ceiling", "vehicle_flight_ceiling_enabled"));
        p.Children.Add(Row("Ceiling height (Y):", FloatBox(Alpine("vehicle_flight_ceiling"), "vehicle_flight_ceiling", -100000, 100000, 1m, 100, "0.000"), 120, 18));

        var payload = new StackPanel { Spacing = 6 };
        var pairs = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        void Pair(int row, int col, string label, string key)
        {
            AddAt(pairs, Label(label), row, col);
            LevelPropertyField f = Alpine(key);
            AddAt(pairs, FloatBox(f, key, (decimal)(f.Field.Min ?? -100000), (decimal)(f.Field.Max ?? 100000), 0.1m, 80, "0.##"), row, col + 1);
        }

        Pair(0, 0, "Push speed (m/s):", "payload_push_speed");
        Pair(0, 2, "Checkpoint bonus (s):", "payload_checkpoint_time_bonus");
        Pair(1, 0, "Acceleration (m/s/s):", "payload_cart_accel");
        Pair(1, 2, "Overtime grace (s):", "payload_overtime_grace");
        Pair(2, 0, "Rollback (x push speed):", "payload_rollback_speed");
        Pair(2, 2, "Heal + ammo (HP/s):", "payload_heal_per_second");
        payload.Children.Add(pairs);
        payload.Children.Add(Label("Respawn delay modifiers (s):"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        AddAt(grid, Centered("Base"), 0, 1);
        AddAt(grid, Centered("Per checkpoint"), 0, 2);
        AddAt(grid, Centered("Final stretch"), 0, 3);
        string[] teams = { "attacker", "defender" };
        string[] parts = { "base", "per_checkpoint", "final_stretch" };
        for (int t = 0; t < 2; t++)
        {
            AddAt(grid, Label(t == 0 ? "Attackers:" : "Defenders:"), t + 1, 0);
            for (int i = 0; i < 3; i++)
            {
                string key = $"payload_{teams[t]}_respawn_{parts[i]}";
                AddAt(grid, FloatBox(Alpine(key), key, -60, 60, 0.5m, 80, "0.##"), t + 1, i + 1);
            }
        }

        payload.Children.Add(grid);
        p.Children.Add(GroupBox("Payload", payload));
        return p;
    }

    // ---- Minimap ------------------------------------------------------------------------------------------

    private TextBox? _minimapBitmap;
    private Border? _minimapPreview;

    private Control BuildMinimap()
    {
        var p = Page();
        p.Children.Add(Check(Alpine("minimap_enabled"), "Enable minimap", "minimap_enabled"));
        LevelPropertyField bitmap = Alpine("minimap_bitmap");
        _minimapBitmap = TextField(bitmap, "minimap_bitmap");
        _minimapBitmap.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                UpdateMinimapPreview();
            }
        };
        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) =>
        {
            if (_services.BrowseFile is { } pick && await pick("bitmap") is { Length: > 0 } file)
            {
                _minimapBitmap.Text = file;
                Put(bitmap, file);
            }
        };
        p.Children.Add(Row("Bitmap:", Horizontal(Wide(_minimapBitmap, 170), browse), 60));

        LevelPropertyField min = Alpine("minimap_world_min");
        LevelPropertyField max = Alpine("minimap_world_max");
        var bounds = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        AddAt(bounds, Label("Min X:"), 0, 0);
        AddAt(bounds, AxisBox(min, 0, "minimap_min_x"), 0, 1);
        AddAt(bounds, Label("Min Z:"), 0, 2);
        AddAt(bounds, AxisBox(min, 2, "minimap_min_z"), 0, 3);
        AddAt(bounds, Label("Max X:"), 1, 0);
        AddAt(bounds, AxisBox(max, 0, "minimap_max_x"), 1, 1);
        AddAt(bounds, Label("Max Z:"), 1, 2);
        AddAt(bounds, AxisBox(max, 2, "minimap_max_z"), 1, 3);
        Control cut = Horizontal(FloatBox(Alpine("minimap_cut_height"), "minimap_cut_height", -100000, 100000, 1m, 112, spin: true), Hint("(0 = no cut)"));
        Grid.SetColumnSpan(cut, 3);
        AddAt(bounds, Label("Cut height (Y):"), 2, 0);
        AddAt(bounds, cut, 2, 1);
        foreach (Control c in bounds.Children)
        {
            c.Margin = new Thickness(Grid.GetColumn(c) == 2 ? 12 : 0, 0, Grid.GetColumn(c) is 0 or 2 ? 6 : 0, 4);
        }

        _minimapPreview = new Border
        {
            Name = "minimap_preview", Width = 78, Height = 78, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        UpdateMinimapPreview();
        p.Children.Add(Horizontal(GroupBox("World bounds", bounds), _minimapPreview));

        // Alpine RED bakes the minimap by rendering the built level top-down (editor_patch/minimap_bake.cpp); Glacier
        // does not bake minimaps, so the bake controls are shown greyed (FEATURES approximation 123).
        var size = new ComboBox { Name = "minimap_bake_size", ItemsSource = new[] { "512 x 512", "1024 x 1024", "2048 x 2048" }, SelectedIndex = 1, Width = 120, IsEnabled = false };
        var bake = new Button { Name = "minimap_bake", Content = "Bake from level", IsEnabled = false };
        var useBounds = new CheckBox { Name = "minimap_bake_use_bounds", Content = "Bake into the world bounds above (else fit the level)", IsEnabled = false };
        const string Why = "Minimaps are baked in Alpine RED: bake one there, or pick a bitmap and set its world bounds here.";
        foreach (Control c in new Control[] { size, bake, useBounds })
        {
            ToolTip.SetTip(c, Why);
            ToolTip.SetShowOnDisabled(c, true);
        }

        p.Children.Add(Horizontal(Label("Bake size:"), size, bake));
        p.Children.Add(useBounds);
        p.Children.Add(Hint(Why));
        return p;
    }

    private void UpdateMinimapPreview()
    {
        if (_minimapPreview is null)
        {
            return;
        }

        string name = _minimapBitmap?.Text ?? string.Empty;
        Bitmap? bmp = name.Length > 0 ? _services.LoadBitmap?.Invoke(name) : null;
        _minimapPreview.Child = bmp is null ? null : new Image { Source = bmp, Stretch = Stretch.Uniform };
    }

    /// <summary>Alpine RED's OK checks of the minimap fields (level.cpp:1966-1990); false keeps the dialog open.</summary>
    private bool ValidateMinimap()
    {
        string bitmap = Value(Alpine("minimap_bitmap")) as string ?? string.Empty;
        if (bitmap.Length > 31 || bitmap.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
        {
            return Reject("The minimap bitmap must be a bare file name of at most 31 characters.");
        }

        if (Value(Alpine("minimap_enabled")) is not true)
        {
            return true;
        }

        if (bitmap.Length == 0)
        {
            return Reject("An enabled minimap needs a bitmap: pick one, or bake it in Alpine RED.");
        }

        Ged.Core.Model.Vec3 min = Value(Alpine("minimap_world_min")) is Ged.Core.Model.Vec3 a ? a : default;
        Ged.Core.Model.Vec3 max = Value(Alpine("minimap_world_max")) is Ged.Core.Model.Vec3 b ? b : default;
        if (max.X - min.X < 1f || max.Z - min.Z < 1f)
        {
            return Reject("The minimap world bounds must be at least 1 m across in X and Z (Max greater than Min).");
        }

        return true;

        bool Reject(string message)
        {
            _error.Text = message;
            _tabs.SelectedIndex = 4;
            return false;
        }
    }

    // ---- Compatibility ------------------------------------------------------------------------------------

    private Control BuildCompatibility()
    {
        var p = Page();
        p.Children.Add(Check(Alpine("legacy_cyclic_timers"), "Legacy Cyclic_Timer events", "legacy_cyclic_timers"));
        p.Children.Add(Check(Alpine("legacy_movers"), "Legacy movers", "legacy_movers"));
        _legacyLighting = Check(Alpine("legacy_lighting"), "Legacy lighting", "legacy_lighting");
        _legacyLighting.IsCheckedChanged += (_, _) => UpdateEnables();
        p.Children.Add(_legacyLighting);
        return p;
    }

    // ---- apply --------------------------------------------------------------------------------------------

    /// <summary>The fields OK would change: the edited ones whose value differs from the level's.</summary>
    internal IReadOnlyList<(LevelPropertyField Field, object? Value)> Changes() =>
        _pending.Where(kv => !Equals(LevelPropertyAccess.Get(_doc, kv.Key), kv.Value)).Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>OK: validates, then writes every change as one undo step. False keeps the dialog open.</summary>
    internal bool Apply()
    {
        _error.Text = string.Empty;
        if (!ValidateMinimap())
        {
            return false;
        }

        ClampFogNear();
        IReadOnlyList<(LevelPropertyField Field, object? Value)> changes = Changes();
        if (changes.Count == 0)
        {
            return true;
        }

        try
        {
            using (_doc.Undo.BeginTransaction("Level Properties"))
            {
                foreach ((LevelPropertyField f, object? v) in changes)
                {
                    LevelPropertyAccess.Set(_doc, f, v);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _error.Text = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>The game drops a fog near clip that is not below the far clip, so OK pulls it just below (level.cpp:1749-1776).</summary>
    private void ClampFogNear()
    {
        LevelPropertyField near = Field(LevelPropertyTarget.LevelProperties, "FogNearPlane");
        LevelPropertyField far = Field(LevelPropertyTarget.LevelProperties, "FogFarPlane");
        if (Value(near) is not float n || Value(far) is not float f)
        {
            return;
        }

        float clamped = MathF.Max(n, 0f);
        if (f > 0f && clamped >= f)
        {
            clamped = MathF.Max(MathF.Round(f - 0.01f, 2), 0f);
        }

        if (clamped != n)
        {
            Put(near, clamped);
        }
    }

    // ---- field plumbing -----------------------------------------------------------------------------------

    private static IEnumerable<LevelPropertyField> AllFields =>
        LevelPropertiesSchema.Fields.Concat(AlpineLevelPropertiesSchema.Fields).Concat(AlpineLevelPropertiesSchema.Alpine15Fields);

    private static LevelPropertyField Field(LevelPropertyTarget target, string path) =>
        AllFields.First(f => f.Target == target && f.Field.Path == path);

    private static LevelPropertyField Alpine(string key) =>
        AllFields.First(f => f.Target == LevelPropertyTarget.AlpineLevelProperties && !f.Field.ReadOnly && f.Field.EffectiveKey == key);

    private object? Value(LevelPropertyField f) => _pending.TryGetValue(f, out object? v) ? v : LevelPropertyAccess.Get(_doc, f);

    private void Put(LevelPropertyField f, object? value) => _pending[f] = value;

    private RfColor ColorOf(LevelPropertyField f) => Value(f) is RfColor c ? c : new RfColor(0, 0, 0, 255);

    private void UpdateEnables()
    {
        foreach (Action a in _enables)
        {
            a();
        }
    }

    private TextBox TextField(LevelPropertyField f, string name)
    {
        var box = new TextBox { Name = name, Text = Value(f) as string ?? string.Empty };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Put(f, box.Text ?? string.Empty);
            }
        };
        return box;
    }

    private CheckBox Check(LevelPropertyField f, string label, string name)
    {
        var cb = new CheckBox { Name = name, Content = label, IsChecked = Value(f) is true };
        cb.IsCheckedChanged += (_, _) => Put(f, cb.IsChecked == true);
        return cb;
    }

    private NumericUpDown IntBox(LevelPropertyField f, string name, int min, int max, double width)
    {
        var box = new NumericUpDown { Name = name, Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = width, Value = Value(f) is int i ? Math.Clamp(i, min, max) : min };
        box.ValueChanged += (_, _) => Put(f, (int)(box.Value ?? min));
        return box;
    }

    private NumericUpDown FloatBox(LevelPropertyField f, string name, decimal min, decimal max, decimal increment, double width, string format = "0.00", bool spin = false)
    {
        decimal v = Value(f) is float x && float.IsFinite(x) ? Math.Clamp((decimal)x, min, max) : 0m;
        // RED edits most numbers in plain boxes and puts spinners only where its dialog has them.
        var box = new NumericUpDown { Name = name, Minimum = min, Maximum = max, Increment = increment, FormatString = format, Width = width, Value = v, ShowButtonSpinner = spin };
        box.ValueChanged += (_, _) => Put(f, (float)(box.Value ?? 0m));
        return box;
    }

    /// <summary>One axis of a vector field (the minimap bounds edit X and Z; Y is kept).</summary>
    private NumericUpDown AxisBox(LevelPropertyField f, int axis, string name)
    {
        Ged.Core.Model.Vec3 v0 = Value(f) is Ged.Core.Model.Vec3 v ? v : default;
        float a = axis == 0 ? v0.X : v0.Z;
        var box = new NumericUpDown { Name = name, Minimum = -100000, Maximum = 100000, Increment = 1, FormatString = "0.00", Width = 114, Value = float.IsFinite(a) ? (decimal)a : 0m };
        box.ValueChanged += (_, _) =>
        {
            Ged.Core.Model.Vec3 cur = Value(f) is Ged.Core.Model.Vec3 c ? c : default;
            float x = (float)(box.Value ?? 0m);
            Put(f, axis == 0 ? cur with { X = x } : cur with { Z = x });
        };
        return box;
    }

    private Control ColorRow(LevelPropertyField f, string name, out Action<RfColor> set) =>
        ColorEditor(name, () => ColorOf(f), c => Put(f, c), out set);

    /// <summary>RED's colour row: a swatch, Change color, and the editable "&lt;r, g, b&gt;" text (level.cpp:1378-1436).</summary>
    private Control ColorEditor(string name, Func<RfColor> get, Action<RfColor> put, out Action<RfColor> set)
    {
        RfColor c0 = get();
        var swatch = new Border { Name = name + "_swatch", Width = 60, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var text = new TextBox { Name = name, Width = 128 };
        void Show(RfColor c)
        {
            swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            text.Text = string.Create(CultureInfo.InvariantCulture, $"<{c.R}, {c.G}, {c.B}>");
        }

        void Apply(RfColor c)
        {
            put(c);
            Show(c);
        }

        Show(c0);
        text.LostFocus += (_, _) =>
        {
            if (ParseColor(text.Text) is (byte r, byte g, byte b))
            {
                Apply(new RfColor(r, g, b, get().A));
            }
            else
            {
                Show(get());
            }
        };
        var change = new Button { Name = name + "_change", Content = "Change color" };
        change.Click += async (_, _) =>
        {
            RfColor cur = get();
            RfColor? picked = _services.PickColor is { } pick ? await pick(cur) : await RgbColorDialog.ShowAsync(this, cur);
            if (picked is { } p)
            {
                Apply(new RfColor(p.R, p.G, p.B, cur.A));
            }
        };
        set = Apply;
        return Horizontal(swatch, change, text);
    }

    /// <summary>Parses "&lt;r, g, b&gt;", "r, g, b" or "r g b" (each 0–255), as Alpine RED's colour text does.</summary>
    internal static (byte R, byte G, byte B)? ParseColor(string? text)
    {
        string[] parts = (text ?? string.Empty).Trim().TrimStart('<').TrimEnd('>').Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var v = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > 255)
            {
                return null;
            }

            v[i] = (byte)n;
        }

        return (v[0], v[1], v[2]);
    }

    // ---- layout helpers -----------------------------------------------------------------------------------

    private static TabItem Tab(string header, Control content) => new()
    {
        Header = header,
        FontSize = 13,
        Padding = new Thickness(9, 4),
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
    };

    private static StackPanel Page() => new() { Spacing = 10, Margin = new Thickness(8) };

    private static Control GroupBox(string title, Control body) => new Border
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(8, 4, 8, 8),
        Child = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = title, FontSize = 12, Opacity = 0.85 }, body } },
    };

    private static Control Row(string label, Control editor, double labelWidth, double indent = 0)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth.ToString(CultureInfo.InvariantCulture)},*"), Margin = new Thickness(indent, 0, 0, 0) };
        grid.Children.Add(Label(label));
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(editor);
        return grid;
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) };

    private static TextBlock Centered(string text) => new() { Text = text, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };

    private static TextBlock Hint(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Horizontal(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Control c in items)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            p.Children.Add(c);
        }

        return p;
    }

    private static T Wide<T>(T c, double width)
        where T : Control
    {
        c.Width = width;
        return c;
    }

    private static T Disabled<T>(T c, bool enabled)
        where T : Control
    {
        c.IsEnabled = enabled;
        if (!enabled)
        {
            ToolTip.SetTip(c, "This level has no section for this property.");
        }

        return c;
    }

    private static void AddAt(Grid grid, Control c, int row, int col)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        grid.Children.Add(c);
    }
}
