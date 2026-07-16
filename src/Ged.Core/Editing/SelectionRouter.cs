using System;
using System.Collections.Generic;
using Ged.Core.Editor;

namespace Ged.Core.Editing;

/// <summary>
/// The single mandatory entry point for EVERY selection mutation in the editor (item: the
/// deferred architectural fix for out-of-mode selection leaks). Viewport clicks, marquee,
/// Outliner / Layers / Link / Dependency panels, palette placement auto-select, link
/// gestures, group selection, prefab placement, import and mode transitions all route here.
///
/// <para>The router enforces the mode + chip contract: a requested selection KIND must be
/// permitted by the currently-active <see cref="SelectKinds"/> chips (which each mode switch
/// resets to the mode's strict default; Ctrl+chip opts additional kinds in until the next
/// switch). A disallowed kind is DROPPED — never a cross-mode selection — and raises
/// <see cref="Dropped"/> once so the shell can show a subtle status hint (not a toast
/// storm).</para>
///
/// <para>The raw <see cref="EditorDocument"/> / <see cref="BrushEditor"/> select primitives
/// are <c>internal</c>; the router (same assembly) is their only public surface, so App-layer
/// code cannot bypass the gate at compile time. Clears and mode-transition purges are always
/// permitted (they never ADD a disallowed kind).</para>
/// </summary>
public sealed class SelectionRouter
{
    private readonly Func<EditorDocument?> _doc;
    private readonly Func<BrushEditor?> _brushes;
    private readonly Func<SelectKinds> _active;
    private readonly Action<SelectKinds>? _onDropped;

    public SelectionRouter(
        Func<EditorDocument?> doc,
        Func<BrushEditor?> brushes,
        Func<SelectKinds> activeKinds,
        Action<SelectKinds>? onDropped = null)
    {
        _doc = doc;
        _brushes = brushes;
        _active = activeKinds;
        _onDropped = onDropped;
    }

    /// <summary>Raised (with the dropped kind) when a selection was rejected by the mode/chip gate.</summary>
    public event Action<SelectKinds>? Dropped;

    /// <summary>The kinds a selection may currently target (mode default + any Ctrl+chip additions).</summary>
    public SelectKinds Active => _active();

    /// <summary>True when the router would permit a selection of <paramref name="kind"/> right now.</summary>
    public bool Permits(SelectKinds kind) => (_active() & Gate(kind)) != 0;

    // Objects and (whole) brushes are BOTH selectable in Group mode as group members, so their
    // gates widen to include Groups; faces/vertices/edges stay strict to their own chip.
    private static SelectKinds Gate(SelectKinds kind) => kind switch
    {
        SelectKinds.Objects => SelectKinds.Objects | SelectKinds.Groups,
        SelectKinds.Brushes => SelectKinds.Brushes | SelectKinds.Groups,
        _ => kind,
    };

    private bool Reject(SelectKinds kind)
    {
        _onDropped?.Invoke(kind);
        Dropped?.Invoke(kind);
        return false;
    }

    // ---- Object / group selection --------------------------------------------

    public bool SelectObject(LevelObject o, bool additive = false)
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        _doc()?.Select(o, additive);
        return true;
    }

    public bool SelectObjects(IEnumerable<LevelObject> objects, bool additive = false)
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        _doc()?.SelectMany(objects, additive);
        return true;
    }

    public bool ToggleObject(LevelObject o)
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        _doc()?.ToggleSelection(o);
        return true;
    }

    public bool SelectAllObjects()
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        _doc()?.SelectAll();
        return true;
    }

    public bool SelectAllOfKind(LevelObjectKind kind)
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        _doc()?.SelectAllOfKind(kind);
        return true;
    }

    public bool InvertObjects()
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        _doc()?.InvertSelection();
        return true;
    }

    public LevelObject? SelectObjectByUid(int uid)
    {
        if (!Permits(SelectKinds.Objects))
        {
            Reject(SelectKinds.Objects);
            return null;
        }

        return _doc()?.SelectByUid(uid);
    }

    // ---- Brush / face / vertex / edge sub-selection ---------------------------

    public bool SelectBrush(int uid, bool additive = false)
    {
        if (!Permits(SelectKinds.Brushes))
        {
            return Reject(SelectKinds.Brushes);
        }

        _brushes()?.SelectBrush(uid, additive);
        return true;
    }

    public bool ToggleBrush(int uid)
    {
        if (!Permits(SelectKinds.Brushes))
        {
            return Reject(SelectKinds.Brushes);
        }

        _brushes()?.ToggleBrush(uid);
        return true;
    }

    public bool SelectFace(int brush, int face, bool additive = false)
    {
        if (!Permits(SelectKinds.Faces))
        {
            return Reject(SelectKinds.Faces);
        }

        _brushes()?.SelectFace(brush, face, additive);
        return true;
    }

    public bool ToggleFace(int brush, int face)
    {
        if (!Permits(SelectKinds.Faces))
        {
            return Reject(SelectKinds.Faces);
        }

        _brushes()?.ToggleFace(brush, face);
        return true;
    }

    public bool SelectVertex(int brush, int vertex, bool additive = false)
    {
        if (!Permits(SelectKinds.Vertices))
        {
            return Reject(SelectKinds.Vertices);
        }

        _brushes()?.SelectVertex(brush, vertex, additive);
        return true;
    }

    public bool ToggleVertex(int brush, int vertex)
    {
        if (!Permits(SelectKinds.Vertices))
        {
            return Reject(SelectKinds.Vertices);
        }

        _brushes()?.ToggleVertex(brush, vertex);
        return true;
    }

    public bool SelectEdge(int brush, int v0, int v1, bool additive = false)
    {
        if (!Permits(SelectKinds.Edges))
        {
            return Reject(SelectKinds.Edges);
        }

        _brushes()?.SelectEdge(brush, v0, v1, additive);
        return true;
    }

    public bool ToggleEdge(int brush, int v0, int v1)
    {
        if (!Permits(SelectKinds.Edges))
        {
            return Reject(SelectKinds.Edges);
        }

        _brushes()?.ToggleEdge(brush, v0, v1);
        return true;
    }

    public bool SelectEdges(int brush, IEnumerable<BrushEdge> edges, bool additive)
    {
        if (!Permits(SelectKinds.Edges))
        {
            return Reject(SelectKinds.Edges);
        }

        _brushes()?.SelectEdges(brush, edges, additive);
        return true;
    }

    // ---- Clears (always permitted: they never add a disallowed kind) ----------

    public void ClearObjects() => _doc()?.ClearSelection();

    public void ClearBrushes() => _brushes()?.ClearSelection();

    public void ClearAll()
    {
        _doc()?.ClearSelection();
        _brushes()?.ClearSelection();
    }
}
