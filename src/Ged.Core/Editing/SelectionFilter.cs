using System;

namespace Ged.Core.Editing;

/// <summary>
/// The pickable object kinds a viewport click may select, as toggleable flags — the
/// model behind the top-toolbar selection-filter chips.
/// </summary>
[Flags]
public enum SelectKinds
{
    None = 0,
    Brushes = 1,
    Faces = 2,
    Vertices = 4,
    Objects = 8,
    Groups = 16,
    Edges = 32,
}

/// <summary>
/// The selection filter behind the top-toolbar chips ([Brushes] [Faces] [Vertices]
/// [Objects] [Groups]): which pickable kinds a click may select, kept in two-way sync
/// with the editing <see cref="EditMode"/>.
///
/// <para>Plain-clicking a chip (<see cref="SetPrimary"/>) is exclusive — it becomes the
/// sole active kind and switches the tool to the matching mode, the classic RED
/// behaviour. Entering a mode elsewhere (<see cref="SyncFromMode"/>) highlights that
/// mode's chip the same exclusive way. Ctrl-clicking a chip
/// (<see cref="ToggleAdditional"/>) adds/removes a kind for simultaneous multi-kind
/// picking within the current mode, without changing the mode.</para>
/// </summary>
public sealed class SelectionFilter
{
    private SelectKinds _active;
    private EditMode _mode;

    public SelectionFilter(EditMode mode = EditMode.Object)
    {
        _mode = mode;
        _active = PrimaryKindFor(mode);
    }

    /// <summary>Raised whenever the active kinds or mode change.</summary>
    public event Action? Changed;

    /// <summary>The kinds a click may currently select (one or more chips lit).</summary>
    public SelectKinds Active => _active;

    /// <summary>The editing mode the primary chip maps to (drives tool panels + scope).</summary>
    public EditMode Mode => _mode;

    /// <summary>True when a pick of <paramref name="kind"/> is allowed to select.</summary>
    public bool Allows(SelectKinds kind) => (_active & kind) != 0;

    /// <summary>
    /// Plain-click a chip: exclusive. The clicked kind becomes the only active kind
    /// and the tool switches to its mode (classic RED single-mode behaviour).
    /// </summary>
    public void SetPrimary(SelectKinds kind)
    {
        if (kind == SelectKinds.None)
        {
            return;
        }

        EditMode mode = ModeFor(kind);
        if (_active == kind && _mode == mode)
        {
            return;
        }

        _active = kind;
        _mode = mode;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ctrl-click a chip: toggle it into/out of the filter without changing the mode,
    /// enabling simultaneous multi-kind picking. The filter never becomes empty — the
    /// primary (mode) kind always stays lit.
    /// </summary>
    public void ToggleAdditional(SelectKinds kind)
    {
        if (kind == SelectKinds.None)
        {
            return;
        }

        SelectKinds next = _active ^ kind;

        // Never let the mode's own kind be cleared, and never leave the filter empty.
        next |= PrimaryKindFor(_mode);
        if (next == _active)
        {
            return;
        }

        _active = next;
        Changed?.Invoke();
    }

    /// <summary>
    /// A mode was entered elsewhere (hotkey / command / tool panel): the chips follow,
    /// exclusively lighting that mode's kind.
    /// </summary>
    public void SyncFromMode(EditMode mode)
    {
        SelectKinds primary = PrimaryKindFor(mode);
        if (_mode == mode && _active == primary)
        {
            return;
        }

        _mode = mode;
        _active = primary;
        Changed?.Invoke();
    }

    /// <summary>The chip kind that a mode lights.</summary>
    public static SelectKinds PrimaryKindFor(EditMode mode) => mode switch
    {
        EditMode.Brush => SelectKinds.Brushes,
        EditMode.Face => SelectKinds.Faces,
        EditMode.Edge => SelectKinds.Edges,
        EditMode.Vertex => SelectKinds.Vertices,
        EditMode.Group => SelectKinds.Groups,
        _ => SelectKinds.Objects,
    };

    /// <summary>The editing mode a chip kind switches to.</summary>
    public static EditMode ModeFor(SelectKinds kind) => kind switch
    {
        SelectKinds.Brushes => EditMode.Brush,
        SelectKinds.Faces => EditMode.Face,
        SelectKinds.Edges => EditMode.Edge,
        SelectKinds.Vertices => EditMode.Vertex,
        SelectKinds.Groups => EditMode.Group,
        _ => EditMode.Object,
    };
}
