using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Linting;

namespace Ged.App.Panels;

/// <summary>
/// The statistics dashboard: target-aware budget bars (count / cap, coloured by
/// severity) plus compiled geometry stats (faces / verts / rooms / portals /
/// pages). Refreshed by the shell after builds and on demand.
/// </summary>
internal sealed class StatisticsPanel : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 4, Margin = new Thickness(8) };

    public StatisticsPanel()
    {
        Content = new ScrollViewer { Content = _root };
        _root.Children.Add(new TextBlock { Text = "Open a level to see statistics.", Opacity = 0.6 });
    }

    /// <summary>Rebuilds the dashboard for a statistics snapshot and the active save target.</summary>
    public void Show(LevelStatistics stats, SaveTarget target)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _root.Children.Clear();

        _root.Children.Add(Header($"Budgets — {SaveTargets.DisplayName(target)}"));
        foreach (BudgetLine line in stats.Budgets)
        {
            _root.Children.Add(BudgetBar(line, target));
        }

        _root.Children.Add(Header("Geometry"));
        AddStat("Rooms", $"{stats.MainRooms} main + {stats.Subrooms} sub = {stats.Rooms}");
        AddStat("Portals", stats.Portals.ToString());
        AddStat("Faces", stats.Faces.ToString());
        AddStat("Face vertices", stats.FaceVertices.ToString());
        AddStat("Vertices", stats.Vertices.ToString());
        AddStat("Surfaces", stats.Surfaces.ToString());
        AddStat("Lightmap pages", stats.LightmapPages.ToString());
        AddStat("Source brushes", stats.Brushes.ToString());
    }

    private void AddStat(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };

        // Labels wrap; values trim with a tooltip carrying the full text (Task 1f).
        var l = new TextBlock
        {
            Text = label,
            Opacity = 0.75,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [ToolTip.TipProperty] = value,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        _root.Children.Add(grid);
    }

    private static Control BudgetBar(BudgetLine line, SaveTarget target)
    {
        int cap = line.Cap(target);
        double frac = Math.Clamp(line.Fraction(target), 0, 1);
        Color color = line.Severity(target) switch
        {
            LintSeverity.Error => Colors.IndianRed,
            LintSeverity.Warning => Colors.Goldenrod,
            _ => Colors.MediumSeaGreen,
        };

        var label = new TextBlock { Text = $"{line.Name}: {line.Count} / {cap}", FontSize = 12 };
        var track = new Border
        {
            Height = 10,
            Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            CornerRadius = new CornerRadius(2),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{Math.Max(frac, 0.001):0.###}*,{Math.Max(1 - frac, 0.001):0.###}*"),
                Children = { Fill(color) },
            },
        };

        return new StackPanel { Spacing = 1, Margin = new Thickness(0, 2), Children = { label, track } };
    }

    private static Border Fill(Color color) => new()
    {
        Background = new SolidColorBrush(color),
        CornerRadius = new CornerRadius(2),
    };

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 8, 0, 2),
    };
}
