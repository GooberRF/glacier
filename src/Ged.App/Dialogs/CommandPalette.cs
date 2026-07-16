using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Services;
using Ged.Core.Input;
using AvDock = Avalonia.Controls.Dock;

namespace Ged.App.Dialogs;

/// <summary>
/// The command palette (Ctrl+Shift+P): a fuzzy search over every registered
/// command showing its bound gesture; Enter or click runs the selected command
/// through the same dispatcher the menus and hotkeys use.
/// </summary>
internal sealed class CommandPalette : Window
{
    private readonly CommandDispatcher _dispatcher;
    private readonly TextBox _search = new() { Watermark = "Type a command…", FontSize = 14 };
    private readonly ListBox _list = new() { FontSize = 13 };
    private List<CommandDefinition> _matches = new();

    public CommandPalette(CommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Title = "Command Palette";
        Width = 560;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;

        var panel = new DockPanel { Margin = new Avalonia.Thickness(8) };
        DockPanel.SetDock(_search, AvDock.Top);
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        Content = panel;

        _list.ItemTemplate = new FuncDataTemplate<CommandDefinition>((def, _) => Row(def), true);

        _search.GetObservable(TextBox.TextProperty).Subscribe(new Panels.AnonymousObserver(_ => UpdateMatches()));
        _search.KeyDown += OnSearchKey;
        _list.DoubleTapped += (_, _) => RunSelected();

        Opened += (_, _) =>
        {
            UpdateMatches();
            _search.Focus();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _matches.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void UpdateMatches()
    {
        string query = _search.Text?.Trim() ?? string.Empty;
        _matches = _dispatcher.Registry.Commands
            // Continuous camera movement is driven by the held-key poller, not the dispatcher;
            // a one-shot palette invocation can't reproduce it, so it is not offered here (it
            // stays visible in Settings ▸ Input and the hotkey reference).
            .Where(c => !c.HeldKey)
            .Where(c => Fuzzy(query, c.DisplayName) || Fuzzy(query, c.Category + " " + c.DisplayName))
            .OrderByDescending(c => Score(query, c.DisplayName))
            .Take(200)
            .ToList();
        _list.ItemsSource = _matches;
        if (_matches.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    private void RunSelected()
    {
        if (_list.SelectedItem is CommandDefinition def)
        {
            Close();
            _dispatcher.Invoke(def.Id);
        }
    }

    private Control Row(CommandDefinition def)
    {
        var grid = new Grid { Margin = new Avalonia.Thickness(2, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(new TextBlock { Text = def.DisplayName, Opacity = def.Implemented ? 1.0 : 0.5 });
        left.Children.Add(new TextBlock { Text = def.Category, Opacity = 0.4, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        if (!def.Implemented)
        {
            left.Children.Add(new TextBlock { Text = "(later)", Opacity = 0.4, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        string gesture = _dispatcher.GestureLabel(def.Id);
        if (!string.IsNullOrEmpty(gesture))
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x30, 0x33, 0x3A)),
                CornerRadius = new CornerRadius(3),
                Padding = new Avalonia.Thickness(6, 1),
                Child = new TextBlock { Text = gesture, FontSize = 11, FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo, monospace") },
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }

        return grid;
    }

    private static bool Fuzzy(string query, string target)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        int qi = 0;
        foreach (char c in target)
        {
            if (qi < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
            }
        }

        return qi == query.Length;
    }

    private static int Score(string query, string target)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        return target.Contains(query, StringComparison.OrdinalIgnoreCase) ? 100 - target.Length : 0;
    }
}
