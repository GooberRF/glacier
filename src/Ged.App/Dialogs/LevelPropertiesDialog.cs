using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.App.Dialogs;

/// <summary>
/// The Level Properties dialog: stock fields (name/author/date,
/// ambient colour, fog colour/near/far, hardness, geomod texture, multiplayer flag)
/// plus the Alpine section (RFL version display, legacy cyclic timers/movers,
/// headlamp start, static-mesh ambient override + value, RF2-style geomod). Every
/// edit is undo-safe through the document.
/// </summary>
internal sealed class LevelPropertiesDialog : Window
{
    private readonly EditorDocument _doc;

    public LevelPropertiesDialog(EditorDocument doc)
    {
        _doc = doc;
        _doc.Rfl.ParseAllKnownSections();

        Title = "Level Properties";
        Width = 460;
        Height = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Margin = new Avalonia.Thickness(14), Spacing = 6 };
        BuildInfo(content);
        BuildStock(content);
        BuildAlpine(content);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        content.Children.Add(close);

        Content = new ScrollViewer { Content = content };
    }

    private LevelInfoSection? Info => Find<LevelInfoSection>();

    private LevelPropertiesSection? Props => Find<LevelPropertiesSection>();

    // ---- level_info -----------------------------------------------------------

    private void BuildInfo(StackPanel root)
    {
        root.Children.Add(Header("Info (level_info)"));

        // Read-only RFL header timestamp (formatted local date/time). It is not editable:
        // every real Save/Save As stamps it to the current time automatically.
        uint ts = _doc.Rfl.Header.Timestamp;
        root.Children.Add(Note(ts == 0
            ? "Last Modified: not set (set automatically on save)"
            : $"Last Modified: {_doc.Rfl.Header.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} (set automatically on save)"));

        if (Info is not { } info)
        {
            root.Children.Add(Note("This level has no level_info section."));
            return;
        }

        RflSection host = Host<LevelInfoSection>()!;
        root.Children.Add(Text("Level Name", info.LevelName, v => Edit(host, "Level name", info.LevelName, v, x => info.LevelName = x)));
        root.Children.Add(Text("Author", info.Author, v => Edit(host, "Author", info.Author, v, x => info.Author = x)));

        // Date is NOT user-editable: every real Save/Save As stamps it to the current
        // date/time automatically (RED parity), mirroring the header timestamp.
        root.Children.Add(ReadOnly("Date", string.IsNullOrEmpty(info.Date) ? "(set automatically on save)" : info.Date));
        root.Children.Add(Note("Date is set automatically on save."));
        root.Children.Add(Check("Multiplayer Level", info.MultiplayerLevel != 0, v => Edit(host, "Multiplayer", info.MultiplayerLevel, B(v), x => info.MultiplayerLevel = x)));
    }

    // ---- level_properties -----------------------------------------------------

    private void BuildStock(StackPanel root)
    {
        root.Children.Add(Header("Stock (level_properties)"));
        if (Props is not { } p)
        {
            root.Children.Add(Note("This level has no level_properties section."));
            return;
        }

        RflSection host = Host<LevelPropertiesSection>()!;
        root.Children.Add(Text("Default Geomod Texture", p.GeomodTexture, v => Edit(host, "Geomod texture", p.GeomodTexture, v, x => p.GeomodTexture = x)));
        root.Children.Add(IntNum("Hardness (0–100, 0 allowed [Alpine])", p.Hardness, 0, 100, v => Edit(host, "Hardness", p.Hardness, v, x => p.Hardness = x)));
        root.Children.Add(Color("Ambient Light", p.AmbientColor, c => Edit(host, "Ambient color", p.AmbientColor, c, x => p.AmbientColor = x)));
        root.Children.Add(Color("Fog Color", p.FogColor, c => Edit(host, "Fog color", p.FogColor, c, x => p.FogColor = x)));
        root.Children.Add(Num("Fog Near Plane (PS2 only)", p.FogNearPlane, v => Edit(host, "Fog near", p.FogNearPlane, v, x => p.FogNearPlane = x)));
        root.Children.Add(Num("Fog Far Plane", p.FogFarPlane, v => Edit(host, "Fog far", p.FogFarPlane, v, x => p.FogFarPlane = x)));
    }

    // ---- alpine_level_properties ----------------------------------------------

    private void BuildAlpine(StackPanel root)
    {
        root.Children.Add(Header("Alpine (alpine_level_properties)"));
        root.Children.Add(Note($"RFL version: {_doc.Rfl.Header.Version} (0x{_doc.Rfl.Header.Version:X})  —  " + SaveTargets.DisplayName(SaveTargets.FromVersion(_doc.Rfl.Header.Version))));

        // Read current values (defaults when the section is absent); the section is
        // only created lazily when a field is actually edited, so opening the dialog
        // on a stock level never adds it.
        AlpineLevelPropertiesSection? cur = Find<AlpineLevelPropertiesSection>();

        root.Children.Add(Check("Legacy Cyclic Timers", (cur?.LegacyCyclicTimers ?? 0) != 0, v => SetAlpineByte("Legacy cyclic timers", a => a.LegacyCyclicTimers, (a, x) => a.LegacyCyclicTimers = x, B(v))));
        root.Children.Add(Check("Legacy Movers", (cur?.LegacyMovers ?? 0) != 0, v => SetAlpineByte("Legacy movers", a => a.LegacyMovers, (a, x) => a.LegacyMovers = x, B(v))));
        root.Children.Add(Check("Player Starts With Headlamp", (cur?.StartsWithHeadlamp ?? 0) != 0, v => SetAlpineByte("Headlamp", a => a.StartsWithHeadlamp, (a, x) => a.StartsWithHeadlamp = x, B(v))));
        root.Children.Add(Check("Override Static-Mesh Ambient", (cur?.OverrideStaticMeshAmbientLightModifier ?? 0) != 0, v => SetAlpineByte("SM ambient override", a => a.OverrideStaticMeshAmbientLightModifier, (a, x) => a.OverrideStaticMeshAmbientLightModifier = x, B(v))));
        root.Children.Add(Num("Static-Mesh Ambient Value", cur?.StaticMeshAmbientLightModifier ?? 1f, v => SetAlpineFloat("SM ambient value", a => a.StaticMeshAmbientLightModifier, (a, x) => a.StaticMeshAmbientLightModifier = x, v)));
        root.Children.Add(Check("RF2-Style (brush) Geomod", (cur?.Rf2StyleGeomod ?? 0) != 0, v => SetAlpineByte("RF2 geomod", a => a.Rf2StyleGeomod, (a, x) => a.Rf2StyleGeomod = x, B(v))));
    }

    private void SetAlpineByte(string desc, Func<AlpineLevelPropertiesSection, byte> get, Action<AlpineLevelPropertiesSection, byte> set, byte value)
    {
        AlpineLevelPropertiesSection alp = EnsureAlpine();
        RflSection host = Host<AlpineLevelPropertiesSection>()!;
        Edit(host, desc, get(alp), value, x => set(alp, x));
    }

    private void SetAlpineFloat(string desc, Func<AlpineLevelPropertiesSection, float> get, Action<AlpineLevelPropertiesSection, float> set, float value)
    {
        AlpineLevelPropertiesSection alp = EnsureAlpine();
        RflSection host = Host<AlpineLevelPropertiesSection>()!;
        Edit(host, desc, get(alp), value, x => set(alp, x));
    }

    // ---- helpers --------------------------------------------------------------

    private AlpineLevelPropertiesSection EnsureAlpine()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.AlpineLevelProperties, () => new AlpineLevelPropertiesSection { Version = 4 });
        var alp = (AlpineLevelPropertiesSection)host.Content!;
        if (alp.Version < 4)
        {
            alp.Version = 4;
        }

        return alp;
    }

    private void Edit<T>(RflSection host, string desc, T oldValue, T newValue, Action<T> apply) =>
        _doc.EditValue(host, desc, oldValue, newValue, apply);

    private static byte B(bool v) => v ? (byte)1 : (byte)0;

    private T? Find<T>()
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }

    private RflSection? Host<T>()
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is T)
            {
                return s;
            }
        }

        return null;
    }

    private static Control Header(string t) => new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 8, 0, 2) };

    private static Control Note(string t) => new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray };

    private static Control Labeled(string label, Control c)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        p.Children.Add(c);
        return p;
    }

    private static Control Text(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value };
        box.LostFocus += (_, _) => set(box.Text ?? string.Empty);
        return Labeled(label, box);
    }

    /// <summary>A non-editable value display (used for fields set automatically, e.g. Date).</summary>
    private static Control ReadOnly(string label, string value) =>
        Labeled(label, new TextBox { Text = value, IsReadOnly = true, IsEnabled = false });

    private static Control Num(string label, float value, Action<float> set)
    {
        var box = new NumericUpDown { Value = (decimal)value, Increment = 1m, Minimum = -1000000m, Maximum = 1000000m };
        box.ValueChanged += (_, _) => set((float)(box.Value ?? 0));
        return Labeled(label, box);
    }

    private static Control IntNum(string label, int value, int min, int max, Action<int> set)
    {
        var box = new NumericUpDown { Value = value, Increment = 1m, Minimum = min, Maximum = max };
        box.ValueChanged += (_, _) => set((int)(box.Value ?? 0));
        return Labeled(label, box);
    }

    private static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.IsCheckedChanged += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    private static Control Color(string label, RfColor value, Action<RfColor> set)
    {
        var swatch = new Border { Width = 26, Height = 18, Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(value.R, value.G, value.B)), BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1) };
        var box = new TextBox { Text = $"{value.R} {value.G} {value.B}", Width = 120 };
        box.LostFocus += (_, _) =>
        {
            string[] parts = (box.Text ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                byte.TryParse(parts[0], out byte r) && byte.TryParse(parts[1], out byte g) && byte.TryParse(parts[2], out byte b))
            {
                var c = new RfColor(r, g, b, value.A);
                swatch.Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, b));
                set(c);
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, swatch } };
        return Labeled(label + " (R G B)", row);
    }
}
