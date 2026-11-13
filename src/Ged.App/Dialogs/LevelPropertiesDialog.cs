using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.App.Dialogs;

/// <summary>
/// The Level Properties dialog: stock fields (name/author/date,
/// ambient colour, fog colour/near/far, hardness, geomod texture, multiplayer flag)
/// plus the Alpine section (RFL version display, legacy cyclic timers/movers,
/// headlamp start, static-mesh ambient override + value, RF2-style geomod). The rows are
/// rendered from the Core schemas (<see cref="LevelPropertiesSchema"/>,
/// <see cref="AlpineLevelPropertiesSchema"/>) and read/written through
/// <see cref="LevelPropertyAccess"/> — the same path scripts use (<c>level.props</c> /
/// <c>level.alpine</c>). Every edit is undo-safe through the document.
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

        if (!LevelPropertyAccess.HasSection(_doc, LevelPropertyTarget.LevelInfo))
        {
            root.Children.Add(Note("This level has no level_info section."));
            return;
        }

        AddRows(root, LevelPropertiesSchema.Fields, LevelPropertyTarget.LevelInfo);
    }

    // ---- level_properties -----------------------------------------------------

    private void BuildStock(StackPanel root)
    {
        root.Children.Add(Header("Stock (level_properties)"));
        if (!LevelPropertyAccess.HasSection(_doc, LevelPropertyTarget.LevelProperties))
        {
            root.Children.Add(Note("This level has no level_properties section."));
            return;
        }

        AddRows(root, LevelPropertiesSchema.Fields, LevelPropertyTarget.LevelProperties);
    }

    // ---- alpine_level_properties ----------------------------------------------

    private void BuildAlpine(StackPanel root)
    {
        root.Children.Add(Header("Alpine (alpine_level_properties)"));
        root.Children.Add(Note($"RFL version: {_doc.Rfl.Header.Version} (0x{_doc.Rfl.Header.Version:X})  —  " + SaveTargets.DisplayName(SaveTargets.FromVersion(_doc.Rfl.Header.Version))));

        // Current values read through the shared accessor (defaults while the section is absent);
        // the section is only created by an actual edit, inside that edit's undo step, so opening
        // the dialog on a stock level never adds it and undoing the first edit removes it again.
        AddRows(root, AlpineLevelPropertiesSchema.Fields, LevelPropertyTarget.AlpineLevelProperties);
    }

    // ---- schema-driven rows ---------------------------------------------------

    /// <summary>
    /// One row per schema field stored in <paramref name="target"/>, in schema order: the editor
    /// kind picks the control (text, checkbox, ranged integer, number, colour; read-only display),
    /// and every value is read and written through <see cref="LevelPropertyAccess"/>.
    /// </summary>
    private void AddRows(StackPanel root, System.Collections.Generic.IReadOnlyList<LevelPropertyField> fields, LevelPropertyTarget target)
    {
        foreach (LevelPropertyField f in fields)
        {
            if (f.Target == target)
            {
                root.Children.Add(Row(f));
            }
        }
    }

    private Control Row(LevelPropertyField f)
    {
        InspectorField field = f.Field;
        object? value = LevelPropertyAccess.Get(_doc, f);
        void Set(object? v) => LevelPropertyAccess.Set(_doc, f, v);

        if (field.ReadOnly)
        {
            // Date is NOT user-editable: every real Save/Save As stamps it to the current
            // date/time automatically (RED parity), mirroring the header timestamp.
            var stack = new StackPanel { Spacing = 6 };
            bool date = field.EffectiveKey == "date";
            string shown = value switch
            {
                string s when s.Length > 0 => s,
                null or "" when date => "(set automatically on save)",
                null or "" => "(not set)",
                _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            };
            stack.Children.Add(ReadOnly(field.Label, shown));
            if (date)
            {
                stack.Children.Add(Note("Date is set automatically on save."));
            }

            return stack;
        }

        return field.Editor switch
        {
            InspectorEditor.Bool => Check(field.Label, value is true, v => Set(v)),
            InspectorEditor.Int =>
                IntNum(field.Label, value is int i ? i : 0, field.Min is double lo ? (int)lo : int.MinValue, field.Max is double hi ? (int)hi : int.MaxValue, v => Set(v)),
            InspectorEditor.Float => Num(field.Label, value is float fl ? fl : 0f, field.Min, field.Max, v => Set(v)),
            InspectorEditor.Color => Color(field.Label, value is RfColor c ? c : default, c => Set(c)),
            _ => Text(field.Label, value as string ?? string.Empty, v => Set(v)),
        };
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

    private static Control Num(string label, float value, double? min, double? max, Action<float> set)
    {
        // A ranged field (the payload rules) keeps to Alpine RED's dialog limits, in tenths when the range is small.
        decimal lo = min is double l ? (decimal)l : -1000000m;
        decimal hi = max is double h ? (decimal)h : 1000000m;
        var box = new NumericUpDown
        {
            Value = Math.Clamp((decimal)value, lo, hi), Increment = hi - lo <= 10m ? 0.1m : 1m, Minimum = lo, Maximum = hi,
        };
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
