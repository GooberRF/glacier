using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Services;
using Ged.Core.Scripting;

namespace Ged.App.Panels;

/// <summary>
/// The docked Script Console (plan §6.1): a Lua REPL with line history, incremental evaluation,
/// color-coded output, and a "promote to editor" affordance. Read-only queries evaluate instantly;
/// a mutation runs in a transaction and reports the visible "1 undo step". Sits in the bottom
/// console row next to Log output / Linter.
/// </summary>
internal sealed class ScriptConsolePanel : UserControl
{
    private const int MaxLines = 500;

    private readonly ScriptingService _scripting;
    private readonly StackPanel _output = new() { Margin = new Thickness(8, 6) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private readonly Action _openEditor;

    public ScriptConsolePanel(ScriptingService scripting, Action openEditor)
    {
        _scripting = scripting;
        _openEditor = openEditor;

        _scroll = new ScrollViewer
        {
            Content = _output,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        _input = new TextBox
        {
            Watermark = "Lua ›  e.g.  return level.count",
            FontFamily = Mono,
            FontSize = 12,
            AcceptsReturn = false,
            Margin = new Thickness(6, 4),
        };
        _input.KeyDown += OnInputKeyDown;

        var run = Btn("Run", () => Submit(_input.Text ?? string.Empty));
        var clear = Btn("Clear", ClearOutput);
        var editor = Btn("Editor…", () => _openEditor());
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(6, 4, 6, 0),
            Children = { run, clear, editor },
        };

        var inputRow = new DockPanel();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Right);
        inputRow.Children.Add(toolbar);
        inputRow.Children.Add(_input);

        var root = new DockPanel();
        DockPanel.SetDock(inputRow, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(inputRow);
        root.Children.Add(_scroll);
        Content = root;

        AppendLine($"Script Console — {_scripting.EngineName}. Type an expression and press Enter.", Info);
        AppendLine("Everything routes through undo (one line = one undo step). Try:  return level.count", Muted);

        _scripting.LogWritten += OnLog;
        DetachedFromVisualTree += (_, _) => _scripting.LogWritten -= OnLog;
    }

    /// <summary>Focuses the input box (the "Focus Script Console" command).</summary>
    public void FocusInput() => _input.Focus();

    private static FontFamily Mono => new("Consolas, Cascadia Code, Menlo, monospace");

    private static IBrush Info => Brushes.Gray;

    private static IBrush Muted => new SolidColorBrush(Color.FromRgb(0x8a, 0x8a, 0x8a));

    private static IBrush Warn => new SolidColorBrush(Color.FromRgb(0xd8, 0x9e, 0x00));

    private static IBrush Err => new SolidColorBrush(Color.FromRgb(0xe0, 0x5a, 0x5a));

    private static IBrush Echo => new SolidColorBrush(Color.FromRgb(0x4a, 0x9e, 0xd8));

    private Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Submit(_input.Text ?? string.Empty);
                e.Handled = true;
                break;
            case Key.Up:
                Recall(-1);
                e.Handled = true;
                break;
            case Key.Down:
                Recall(+1);
                e.Handled = true;
                break;
        }
    }

    private void Recall(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        _input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
        _input.CaretIndex = _input.Text?.Length ?? 0;
    }

    private void Submit(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return;
        }

        _history.Add(line);
        _historyIndex = _history.Count;
        _input.Text = string.Empty;
        AppendLine("› " + line, Echo);

        ScriptRunResult result = _scripting.EvalConsole(line, CancellationToken.None);
        if (result.Success)
        {
            if (result.ReturnValue is { Length: > 0 } rv)
            {
                AppendLine("= " + rv, null); // inherit theme foreground
            }

            if (result.UndoNodesAdded > 0)
            {
                AppendLine("  (1 undo step — Ctrl+Z to revert)", Muted);
            }
        }
        else if (result.Error is { } err)
        {
            AppendLine(err.ToDisplayString(), Err);
        }
    }

    private void OnLog(ScriptLogEntry entry)
    {
        // Show every script log line — info/warn/error coloured, print() output plain.
        IBrush? brush = entry.Level switch
        {
            ScriptLogLevel.Error => Err,
            ScriptLogLevel.Warning => Warn,
            ScriptLogLevel.Info => Info,
            _ => null, // Output (print) inherits the theme foreground
        };
        AppendLine(entry.Message, brush);
    }

    private void AppendLine(string text, IBrush? brush)
    {
        var line = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        if (brush is not null)
        {
            line.Foreground = brush;
        }

        _output.Children.Add(line);

        while (_output.Children.Count > MaxLines)
        {
            _output.Children.RemoveAt(0);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void ClearOutput() => _output.Children.Clear();
}
