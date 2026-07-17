using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Raised when a single-target selection was refused SOLELY because the hit resolves to a
    /// locked item (brush/object). The shell shows a "Locked — unlock to select." Hint. Batch
    /// paths (marquee, invert, select-all, group/unit) silently skip locked items instead.
    /// </summary>
    public event Action? LockBlocked;

    private void RaiseLockBlocked() => LockBlocked?.Invoke();

    /// <summary>True when a level object is locked (session lock; unselectable/untransformable).</summary>
    private bool IsObjectLocked(LevelObject o) => _doc()?.IsLocked(o) == true;

    /// <summary>True when a brush is locked (<see cref="BrushState.Locked"/>).</summary>
    private bool IsBrushLocked(int uid) => _brushes()?.IsBrushLocked(uid) == true;

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

        // G: a locked object is unselectable. A click resolving ONLY to a locked hit selects
        // nothing and hints (the GPU id-buffer exposes just the topmost hit, so there is no
        // cheap fall-through to the next unlocked item — select nothing, per the brief).
        if (IsObjectLocked(o))
        {
            RaiseLockBlocked();
            return false;
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

        // Batch path (group select, Outliner group): silently skip locked members.
        _doc()?.SelectMany(objects.Where(o => !IsObjectLocked(o)), additive);
        return true;
    }

    public bool ToggleObject(LevelObject o)
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        if (IsObjectLocked(o))
        {
            RaiseLockBlocked();
            return false;
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

        if (_doc() is { } doc)
        {
            doc.SelectMany(doc.Objects.Where(o => !IsObjectLocked(o)));
        }

        return true;
    }

    public bool SelectAllOfKind(LevelObjectKind kind)
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        if (_doc() is { } doc)
        {
            doc.SelectMany(doc.Objects.Where(o => o.Kind == kind && !IsObjectLocked(o)));
        }

        return true;
    }

    public bool InvertObjects()
    {
        if (!Permits(SelectKinds.Objects))
        {
            return Reject(SelectKinds.Objects);
        }

        // Stock I, lock-aware: the new selection is every currently-unselected, unlocked object.
        // Materialize BEFORE SelectMany, which clears the selection before enumerating (so a lazy
        // IsSelected filter would otherwise see an already-cleared selection and match everything).
        if (_doc() is { } doc)
        {
            var inverted = doc.Objects.Where(o => !doc.IsSelected(o) && !IsObjectLocked(o)).ToList();
            doc.SelectMany(inverted);
        }

        return true;
    }

    public LevelObject? SelectObjectByUid(int uid)
    {
        if (!Permits(SelectKinds.Objects))
        {
            Reject(SelectKinds.Objects);
            return null;
        }

        if (_doc()?.FindByUid(uid) is { } o && IsObjectLocked(o))
        {
            RaiseLockBlocked();
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

        if (IsBrushLocked(uid))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(uid))
        {
            RaiseLockBlocked();
            return false;
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

        // Sub-geometry of a locked brush is off-limits too (defense: no editing a locked brush).
        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
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

        if (IsBrushLocked(brush))
        {
            RaiseLockBlocked();
            return false;
        }

        _brushes()?.SelectEdges(brush, edges, additive);
        return true;
    }

    // ---- Prefab-instance UNIT selection (Feature F) ---------------------------

    /// <summary>
    /// Selects a whole prefab instance as a UNIT: every member brush AND object, in one shot.
    /// This is the mixed-kind gate the brief calls for — modelled on GROUP selection, where one
    /// chip (Groups) widens BOTH the object and brush gates so a group's members of either kind
    /// select together. Here the unit is permitted whenever the active chips permit whole-object
    /// OR whole-brush selection (both widen to include Groups). Sub-geometry modes (Face/Vertex/
    /// Edge) permit neither, so a member click there is never escalated to a unit. G point 4: an
    /// instance with ANY locked member is unselectable as a unit — refused with a lock hint.
    /// </summary>
    public bool SelectPrefabUnit(IReadOnlyCollection<int> memberUids)
    {
        if (!Permits(SelectKinds.Objects) && !Permits(SelectKinds.Brushes))
        {
            return Reject(SelectKinds.Objects);
        }

        // G point 4: an instance with ANY locked member is unselectable as a unit (all-or-nothing).
        if (AnyMemberLocked(memberUids))
        {
            RaiseLockBlocked();
            return false;
        }

        _doc()?.ClearSelection();
        _brushes()?.ClearSelection();
        AddPrefabMembers(memberUids);
        return true;
    }

    /// <summary>
    /// Additively selects a set of prefab-instance members (both kinds), skipping locked ones — the
    /// marquee-time variant that folds several caught instances into one multi-selection. Same
    /// group-like gate as <see cref="SelectPrefabUnit"/>.
    /// </summary>
    public bool AddPrefabUnitMembers(IReadOnlyCollection<int> memberUids)
    {
        if (!Permits(SelectKinds.Objects) && !Permits(SelectKinds.Brushes))
        {
            return Reject(SelectKinds.Objects);
        }

        AddPrefabMembers(memberUids);
        return true;
    }

    private bool AnyMemberLocked(IReadOnlyCollection<int> memberUids)
    {
        EditorDocument? doc = _doc();
        BrushEditor? be = _brushes();
        foreach (int uid in memberUids)
        {
            if (be?.FindBrush(uid) is not null)
            {
                if (be.IsBrushLocked(uid))
                {
                    return true;
                }
            }
            else if (doc?.FindByUid(uid) is { } o && doc.IsLocked(o))
            {
                return true;
            }
        }

        return false;
    }

    private void AddPrefabMembers(IReadOnlyCollection<int> memberUids)
    {
        EditorDocument? doc = _doc();
        BrushEditor? be = _brushes();
        foreach (int uid in memberUids)
        {
            if (be?.FindBrush(uid) is not null)
            {
                if (!be.IsBrushLocked(uid))
                {
                    be.SelectBrush(uid, additive: true);
                }
            }
            else if (doc?.FindByUid(uid) is { } o && !doc.IsLocked(o))
            {
                doc.Select(o, additive: true);
            }
        }
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
