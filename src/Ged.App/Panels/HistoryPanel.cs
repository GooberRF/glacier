using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editor;

namespace Ged.App.Panels;

/// <summary>
/// The undo <b>tree</b>: every recorded command as a node, rendered as an
/// indented branch rail with the current node highlighted. Performing a new edit after
/// undos forks a branch instead of discarding the redo tail, so alternate futures stay
/// visible here. Clicking any node time-travels the document to it (walking the undo/redo
/// path between the current node and the target); on the single main branch this behaves
/// exactly like the old linear jump.
/// </summary>
internal sealed class HistoryPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ScrollViewer _scroll;
    private IEditorHost? _host;

    public HistoryPanel()
    {
        _scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Content = _scroll;
    }

    public void Bind(IEditorHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        _rows.Children.Clear();
        if (_host?.Document is not EditorDocument doc)
        {
            return;
        }

        UndoStack undo = doc.Undo;
        Control? currentRow = null;

        // DFS from the root: the first child continues on the same rail; later children
        // (forks) indent one level, so branches read as nested sub-rails.
        void Emit(UndoNode node, int depth)
        {
            Control row = BuildRow(undo, node, depth);
            _rows.Children.Add(row);
            if (ReferenceEquals(node, undo.Current))
            {
                currentRow = row;
            }

            IReadOnlyList<UndoNode> kids = node.Children;
            for (int i = 0; i < kids.Count; i++)
            {
                Emit(kids[i], depth + (i == 0 ? 0 : 1));
            }
        }

        Emit(undo.Root, 0);
        currentRow?.BringIntoView();
    }

    private Control BuildRow(UndoStack undo, UndoNode node, int depth)
    {
        bool isCurrent = ReferenceEquals(node, undo.Current);
        bool isRoot = ReferenceEquals(node, undo.Root);
        bool isFork = node.Parent is { } p && p.Children.Count > 1 && !ReferenceEquals(p.Children[0], node);

        var text = new TextBlock
        {
            Text = (isFork ? "⤷ " : string.Empty) + (isRoot ? "— open —" : node.Description),
            FontSize = 12,
            FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stamp = new TextBlock
        {
            Text = isRoot ? string.Empty : node.Timestamp.ToString("HH:mm:ss"),
            FontSize = 9,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var line = new DockPanel { Margin = new Thickness(6 + (depth * 14), 0, 4, 0) };
        DockPanel.SetDock(stamp, Avalonia.Controls.Dock.Right);
        line.Children.Add(stamp);
        line.Children.Add(text);

        var border = new Border
        {
            Child = line,
            Padding = new Thickness(3, 2),
            Background = isCurrent ? new SolidColorBrush(Color.FromArgb(80, 90, 140, 255)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = isRoot ? "Baseline (undo everything)" : $"Jump to: {node.Description}",
        };

        border.PointerPressed += (_, _) =>
        {
            undo.MoveToNode(node);
            _host?.RequestSceneRebuild();
            _host?.RefreshSelectionOverlay();
            Refresh();
        };
        return border;
    }
}

/// <summary>A titled placeholder shown where a full panel is not mounted.</summary>
internal sealed class PlaceholderPanel : UserControl
{
    public PlaceholderPanel(string title, string note)
    {
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = note, Opacity = 0.6, TextWrapping = TextWrapping.Wrap },
            },
        };
    }
}
