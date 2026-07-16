using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.App.Dialogs;

/// <summary>
/// Stock "Waypoint List": manage the level's named waypoint lists — create / rename
/// / delete a list, and add the currently-selected nav points to a list or remove
/// members. Waypoint members are indices into the nav-points array; every edit is
/// undo-safe through <see cref="NavGraphService"/>.
/// </summary>
internal sealed class WaypointListDialog : Window
{
    private readonly NavGraphService _nav;
    private readonly EditorDocument _doc;
    private readonly ListBox _lists = new() { MinWidth = 190, MinHeight = 220 };
    private readonly ListBox _members = new() { MinWidth = 200, MinHeight = 220 };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = Brushes.Gray };

    public WaypointListDialog(Window owner, NavGraphService nav, EditorDocument doc)
    {
        _nav = nav;
        _doc = doc;

        Title = "Waypoint Lists";
        Width = 480;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var newBtn = new Button { Content = "New" };
        newBtn.Click += async (_, _) => await NewListAsync();
        var renameBtn = new Button { Content = "Rename" };
        renameBtn.Click += async (_, _) => await RenameListAsync();
        var deleteBtn = new Button { Content = "Delete" };
        deleteBtn.Click += (_, _) => DeleteList();

        var addBtn = new Button { Content = "Add selected nav points" };
        addBtn.Click += (_, _) => AddSelected();
        var removeBtn = new Button { Content = "Remove" };
        removeBtn.Click += (_, _) => RemoveMember();

        _lists.SelectionChanged += (_, _) => RefreshMembers();

        var listCol = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Lists", FontWeight = FontWeight.Bold },
                _lists,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { newBtn, renameBtn, deleteBtn } },
            },
        };

        var memberCol = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Members (nav-point indices)", FontWeight = FontWeight.Bold },
                _members,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { addBtn, removeBtn } },
            },
        };

        var cols = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { listCol, memberCol } };

        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 8,
            Children = { cols, _status, close },
        };

        RefreshLists();
    }

    private void RefreshLists()
    {
        int keep = _lists.SelectedIndex;
        IReadOnlyList<WaypointListRow> rows = _nav.WaypointLists
            .Select((l, i) => new WaypointListRow(i, l.Name, l.WaypointIndices.Count))
            .ToList();
        _lists.ItemsSource = rows.Select(r => $"{r.Index}: {r.Name}  ({r.Count})").ToList();
        _lists.SelectedIndex = rows.Count == 0 ? -1 : (keep >= 0 && keep < rows.Count ? keep : 0);
        RefreshMembers();
        UpdateStatus();
    }

    private void RefreshMembers()
    {
        IReadOnlyList<WaypointList> lists = _nav.WaypointLists;
        int li = _lists.SelectedIndex;
        if (li < 0 || li >= lists.Count)
        {
            _members.ItemsSource = null;
            return;
        }

        Dictionary<int, int> map = NavIndexToUid();
        _members.ItemsSource = lists[li].WaypointIndices
            .Select(wi => map.TryGetValue(wi, out int uid) ? $"index {wi} → nav uid {uid}" : $"index {wi} (unresolved)")
            .ToList();
    }

    private Dictionary<int, int> NavIndexToUid()
    {
        var map = new Dictionary<int, int>();
        foreach (LevelObject o in _doc.Objects.Where(o => o.Kind == LevelObjectKind.NavPoint))
        {
            int idx = o.IndexInSection;
            if (idx >= 0)
            {
                map[idx] = o.Uid;
            }
        }

        return map;
    }

    private async Task NewListAsync()
    {
        string? name = await InputDialog.ShowAsync(this, "New Waypoint List", "List name:", $"waypoints_{_nav.WaypointLists.Count}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _nav.AddWaypointList(name.Trim());
        RefreshLists();
        _lists.SelectedIndex = _nav.WaypointLists.Count - 1;
        RefreshMembers();
    }

    private async Task RenameListAsync()
    {
        int li = _lists.SelectedIndex;
        if (li < 0 || li >= _nav.WaypointLists.Count)
        {
            return;
        }

        string? name = await InputDialog.ShowAsync(this, "Rename Waypoint List", "List name:", _nav.WaypointLists[li].Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _nav.RenameWaypointList(li, name.Trim());
        RefreshLists();
    }

    private void DeleteList()
    {
        int li = _lists.SelectedIndex;
        if (li < 0 || li >= _nav.WaypointLists.Count)
        {
            return;
        }

        _nav.RemoveWaypointList(li);
        RefreshLists();
    }

    private void AddSelected()
    {
        int li = _lists.SelectedIndex;
        if (li < 0 || li >= _nav.WaypointLists.Count)
        {
            UpdateStatus("Select a waypoint list first.");
            return;
        }

        List<int> indices = _doc.Selection
            .Where(o => o.Kind == LevelObjectKind.NavPoint)
            .Select(o => o.IndexInSection)
            .Where(i => i >= 0)
            .ToList();
        if (indices.Count == 0)
        {
            UpdateStatus("Select nav points in the viewport to add them.");
            return;
        }

        _nav.AddWaypoints(li, indices);
        RefreshLists();
        _lists.SelectedIndex = li;
        RefreshMembers();
        UpdateStatus($"Added {indices.Count} waypoint(s).");
    }

    private void RemoveMember()
    {
        int li = _lists.SelectedIndex;
        int mi = _members.SelectedIndex;
        if (li < 0 || mi < 0)
        {
            return;
        }

        _nav.RemoveWaypointAt(li, mi);
        RefreshLists();
        _lists.SelectedIndex = li;
        RefreshMembers();
    }

    private void UpdateStatus(string? message = null) =>
        _status.Text = message ?? $"{_nav.WaypointLists.Count} waypoint list(s). Members index into the nav-points array.";

    private readonly record struct WaypointListRow(int Index, string Name, int Count);
}
