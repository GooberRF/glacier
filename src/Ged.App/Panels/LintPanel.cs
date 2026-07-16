using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Linting;

namespace Ged.App.Panels;

/// <summary>
/// The level-linter results panel: a header summary plus one row per finding with
/// a severity icon, description, and (when the finding names a UID) a "Jump" that
/// frames the object in the active viewport. Populated on demand by the shell.
/// </summary>
internal sealed class LintPanel : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(6) };
    private readonly TextBlock _header = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 6, 6, 2) };

    public LintPanel()
    {
        _header.Text = "Linter — run from Tools ▸ Run Level Linter.";
        Content = new DockPanel
        {
            Children =
            {
                DockTop(_header),
                new ScrollViewer { Content = _list },
            },
        };
    }

    /// <summary>Shows a linter report; <paramref name="jump"/> frames the object with the given UID.</summary>
    public void Show(LintReport report, Action<int> jump)
    {
        ArgumentNullException.ThrowIfNull(report);
        _list.Children.Clear();
        _header.Text = report.Summary();

        if (report.IsClean)
        {
            _list.Children.Add(new TextBlock { Text = "No issues found.", Opacity = 0.6, Margin = new Thickness(4) });
            return;
        }

        foreach (LintFinding f in report.Findings)
        {
            _list.Children.Add(Row(f, jump));
        }
    }

    private static Control Row(LintFinding f, Action<int> jump)
    {
        var icon = new TextBlock
        {
            Text = Glyph(f.Severity),
            Foreground = new SolidColorBrush(Color(f.Severity)),
            FontWeight = FontWeight.Bold,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var text = new TextBlock
        {
            Text = (f.BlocksSave ? "[BLOCKS SAVE] " : string.Empty) + f.Message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(2) };
        row.Children.Add(icon);
        var col = new StackPanel { Spacing = 1 };
        col.Children.Add(text);
        col.Children.Add(new TextBlock { Text = f.Category.ToString(), FontSize = 10, Opacity = 0.5 });
        row.Children.Add(col);

        if (f.Uid is int uid)
        {
            var jumpBtn = new Button { Content = "Jump", FontSize = 10, Padding = new Thickness(6, 1), Margin = new Thickness(4, 0, 0, 0) };
            jumpBtn.Click += (_, _) => jump(uid);
            row.Children.Add(jumpBtn);
        }

        return row;
    }

    private static string Glyph(LintSeverity s) => s switch
    {
        LintSeverity.Error => "✖",
        LintSeverity.Warning => "▲",
        _ => "ℹ",
    };

    private static Color Color(LintSeverity s) => s switch
    {
        LintSeverity.Error => Colors.IndianRed,
        LintSeverity.Warning => Colors.Goldenrod,
        _ => Colors.SteelBlue,
    };

    private static Control DockTop(Control c)
    {
        DockPanel.SetDock(c, Avalonia.Controls.Dock.Top);
        return c;
    }
}
