using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ged.App.Panels;

/// <summary>
/// The unified "Log output" panel (item 4). Every long-running editor operation — geometry
/// builds, lightmap / lighting bakes, Check for Holes, texture/library reports, packfile
/// writes — appends a block here, each prefixed with a timestamp and the operation name so
/// the different operation types stay legible in one stream. Newest output autoscrolls into
/// view; a Clear button empties the log; the buffer is trimmed so a long session can't grow
/// it without bound.
/// </summary>
internal sealed class LogOutputPanel : UserControl
{
    private const int MaxChars = 120_000;
    private const int TrimTo = 90_000;

    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8),
        FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo, monospace"),
        FontSize = 12,
    };

    private readonly ScrollViewer _scroll;
    private readonly StringBuilder _buffer = new();
    private bool _empty = true;

    public LogOutputPanel()
    {
        _text.Text = "Log output — build, lighting, hole-check and asset results appear here.";

        _scroll = new ScrollViewer
        {
            Content = _text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var clear = new Button { Content = "Clear", FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(6, 4) };
        clear.Click += (_, _) => Clear();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { clear } };

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_scroll);
        Content = root;
    }

    /// <summary>Appends a tagged entry (operation header + body) and autoscrolls to the newest line.</summary>
    public void Append(string operation, string text)
    {
        if (_empty)
        {
            _buffer.Clear();
            _empty = false;
        }
        else
        {
            _buffer.Append('\n');
        }

        string stamp = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        _buffer.Append('[').Append(stamp).Append("]  ").Append(operation).Append('\n');
        _buffer.Append((text ?? string.Empty).TrimEnd()).Append('\n');

        if (_buffer.Length > MaxChars)
        {
            int cut = _buffer.Length - TrimTo;
            int nl = IndexOfNewline(cut);
            _buffer.Remove(0, nl >= 0 ? nl + 1 : cut);
            _buffer.Insert(0, "… (older log trimmed) …\n");
        }

        _text.Text = _buffer.ToString();

        // Autoscroll: keep the newest output in view. Deferred so the ScrollViewer has re-measured
        // the taller content first.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Clears the log back to its empty placeholder.</summary>
    public void Clear()
    {
        _buffer.Clear();
        _empty = true;
        _text.Text = "Log output — build, lighting, hole-check and asset results appear here.";
    }

    /// <summary>Current log text (test hook).</summary>
    internal string LogText => _empty ? string.Empty : _buffer.ToString();

    private int IndexOfNewline(int from)
    {
        for (int i = from; i < _buffer.Length; i++)
        {
            if (_buffer[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
