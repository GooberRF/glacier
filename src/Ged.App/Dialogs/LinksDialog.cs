using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ged.Core.Editing;
using Ged.Core.Editor;

namespace Ged.App.Dialogs;

/// <summary>
/// The Ctrl+L links dialog for an originator (trigger / event / clutter / nav
/// point): lists its current links, adds by UID (validated), removes, and jumps
/// the camera to a linked object. Every mutation goes through the undo-safe
/// <see cref="LinkService"/>.
/// </summary>
internal sealed class LinksDialog : Window
{
    private readonly EditorDocument _doc;
    private readonly LinkService _links;
    private readonly LevelObject _origin;
    private readonly Action<int> _jump;
    private readonly ListBox _list = new() { Height = 220 };
    private readonly TextBox _uidBox = new() { Watermark = "target UID", Width = 120 };

    public LinksDialog(EditorDocument doc, LinkService links, LevelObject origin, Action<int> jump)
    {
        _doc = doc;
        _links = links;
        _origin = origin;
        _jump = jump;

        Title = $"Links — {origin.DisplayName} (uid {origin.Uid})";
        Width = 360;
        Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(10), Spacing = 8 };
        root.Children.Add(new TextBlock { Text = "Current links (event links go FROM this object TO the target):", TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 });
        root.Children.Add(_list);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addRow.Children.Add(_uidBox);
        addRow.Children.Add(Button("Add", OnAdd));
        addRow.Children.Add(Button("Remove", OnRemove));
        root.Children.Add(addRow);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actionRow.Children.Add(Button("Jump To", OnJump));
        actionRow.Children.Add(Button("Close", Close));
        root.Children.Add(actionRow);

        Content = root;
        RefreshList();
    }

    private void RefreshList()
    {
        var links = LinkModel.LinksOf(_origin);
        _list.ItemsSource = links?.Select(u =>
        {
            LevelObject? o = _doc.FindByUid(u);
            return o is null ? $"{u}  (missing)" : $"{u}  {o.Kind}  {o.DisplayName}";
        }).ToList();
    }

    private int? SelectedUid()
    {
        if (_list.SelectedItem is string s)
        {
            string first = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid))
            {
                return uid;
            }
        }

        return null;
    }

    private void OnAdd()
    {
        if (int.TryParse(_uidBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid))
        {
            LinkResult r = _links.AddLink(_origin, uid);
            if (!r.Ok)
            {
                Title = r.Message;
            }

            RefreshList();
        }
    }

    private void OnRemove()
    {
        if (SelectedUid() is int uid)
        {
            _links.RemoveLink(_origin, uid);
            RefreshList();
        }
    }

    private void OnJump()
    {
        if (SelectedUid() is int uid)
        {
            _jump(uid);
        }
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => onClick();
        return b;
    }
}
