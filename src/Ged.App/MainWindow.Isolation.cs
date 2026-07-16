using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.App;

/// <summary>
/// Isolate Selection (B6): a non-destructive view filter that hides everything except
/// the current selection and its group members. It never mutates the undoable per-object
/// Hidden flags, so exiting restores the EXACT prior visibility (pre-existing hidden
/// objects stay hidden). Mode switches and document swaps auto-exit.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Isolate Selection command: isolates the selection, or exits if already isolated.</summary>
    private void ToggleIsolation()
    {
        if (Document is not { } doc)
        {
            return;
        }

        if (doc.IsIsolated)
        {
            ExitIsolationIfActive();
            return;
        }

        HashSet<int> visible = ComputeIsolationSet(doc);
        if (visible.Count == 0)
        {
            _dispatcher.ShowMessage("Isolate Selection: select something first.");
            return;
        }

        doc.IsolateSelection(visible);
        RebuildScene();
        UpdateIsolationStatus();
        _dispatcher.ShowMessage($"Isolated {visible.Count} item(s) — Isolate Selection again (or Exit Isolation) to restore.");
    }

    /// <summary>Exits isolation and restores the prior visibility (no-op when not isolated).</summary>
    private void ExitIsolationIfActive()
    {
        if (Document is not { IsIsolated: true } doc)
        {
            return;
        }

        doc.ExitIsolation();
        RebuildScene();
        UpdateIsolationStatus();
        _dispatcher.ShowMessage("Exited isolation — visibility restored.");
    }

    /// <summary>Reflects the isolation state onto the status-bar indicator.</summary>
    private void UpdateIsolationStatus() =>
        _statusIsolation.Text = Document?.IsIsolated == true ? "◉ ISOLATED" : string.Empty;

    /// <summary>
    /// The UIDs kept visible under isolation: the selected objects and brushes, expanded
    /// with every member of a user group or moving group any selected item belongs to.
    /// </summary>
    private HashSet<int> ComputeIsolationSet(EditorDocument doc)
    {
        var seed = new HashSet<int>(doc.Selection.Select(o => o.Uid));
        if (BrushEd is { } be)
        {
            foreach (int uid in be.SelectedBrushes)
            {
                seed.Add(uid);
            }
        }

        var visible = new HashSet<int>(seed);
        foreach (Group grp in AllGroups())
        {
            if (grp.Brushes.Any(seed.Contains) || grp.Objects.Any(seed.Contains))
            {
                foreach (int uid in grp.Brushes)
                {
                    visible.Add(uid);
                }

                foreach (int uid in grp.Objects)
                {
                    visible.Add(uid);
                }
            }
        }

        return visible;
    }

    private IEnumerable<Group> AllGroups()
    {
        IEnumerable<Group> user = _groups?.Groups ?? Enumerable.Empty<Group>();
        IEnumerable<Group> moving = _movers?.Movers ?? Enumerable.Empty<Group>();
        return user.Concat(moving);
    }
}
