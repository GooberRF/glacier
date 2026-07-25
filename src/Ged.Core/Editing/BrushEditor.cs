using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The brush-editing service over an <see cref="EditorDocument"/>: it owns the
/// <c>brushes</c> section (creating it on first use), the mode + brush/face/vertex
/// selection, and every mutation routed through the document's undo stack with the
/// section marked dirty. In-place geometry edits snapshot the affected brushes so
/// undo is exact and a failed operation rolls back without corrupting the model.
/// Pure of UI — fully unit-testable.
/// </summary>
public sealed class BrushEditor
{
    private readonly EditorDocument _doc;
    private readonly HashSet<int> _selectedBrushes = new();
    private readonly HashSet<(int Brush, int Face)> _selectedFaces = new();
    private readonly HashSet<(int Brush, int Vertex)> _selectedVertices = new();
    private readonly HashSet<(int Brush, int V0, int V1)> _selectedEdges = new();

    // One-level selection memory (Backspace reselect), captured before a replacing
    // selection in any mode; stock highlights this in Texture mode.
    private (HashSet<int> Brushes, HashSet<(int, int)> Faces, HashSet<(int, int)> Vertices, HashSet<(int, int, int)> Edges) _selectionMemory =
        (new(), new(), new(), new());

    // Notification-batch state (item: Instant undo must not animate a coalesced drag). While a batch
    // is open, per-sub-command BrushesChanged notifications are COALESCED into one fire on close, with
    // the affected UIDs unioned (any structural/unknown change makes the batched notify structural).
    private int _changeBatchDepth;
    private bool _changePending;
    private bool _changeUidsUnknown;
    private readonly HashSet<int> _changeUids = new();

    public BrushEditor(EditorDocument document)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));

        // Let the document's undo stack coalesce a whole atomic Undo/Redo/jump into ONE BrushesChanged:
        // a multi-command drag entry (the gizmo CompositeCommand, or a coalesced M-N node) otherwise
        // fires a scene refresh per accumulated sub-command, so an "Instant" undo visibly walked the
        // brush backward through every drag frame. The Replay path steps node-by-node and does NOT use
        // this scope, so it still animates deliberately.
        _doc.Undo.AtomicApplyScope = BatchChanges;
    }

    /// <summary>
    /// Opens a scope that COALESCES <see cref="BrushesChanged"/>: notifications raised while the scope
    /// is open are held and fired exactly once (with the union of affected UIDs) when it closes. Nesting
    /// is depth-counted; the single fire happens when the outermost scope closes. Used by the undo stack
    /// to make an Instant undo/redo of a multi-step drag land in one refresh (see the constructor).
    /// </summary>
    public IDisposable BatchChanges()
    {
        _changeBatchDepth++;
        return new ChangeBatch(this);
    }

    /// <summary>Closes one batch level; when the outermost closes, fires the coalesced notification once.</summary>
    private void EndChangeBatch()
    {
        if (--_changeBatchDepth != 0)
        {
            return;
        }

        bool pending = _changePending;
        IReadOnlyCollection<int>? uids = _changeUidsUnknown ? null : _changeUids.ToList();
        _changePending = false;
        _changeUidsUnknown = false;
        _changeUids.Clear();
        if (pending)
        {
            LastChangedBrushUids = uids;
            BrushesChanged?.Invoke();
        }
    }

    private sealed class ChangeBatch : IDisposable
    {
        private readonly BrushEditor _be;
        private bool _disposed;

        public ChangeBatch(BrushEditor be) => _be = be;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _be.EndChangeBatch();
        }
    }

    /// <summary>Raised after brushes are added/removed/reordered or geometry changes.</summary>
    public event Action? BrushesChanged;

    /// <summary>
    /// The brush UIDs affected by the most recent change, or null when the change was
    /// structural/unknown (create, delete, clip, reorder) — those invalidate any
    /// build-overlay stash wholesale. A pure transform of known brushes (a gizmo / M-N
    /// drag) reports exactly those UIDs so only they fall back to authored polygons while
    /// untouched brushes keep their fragment overlay (item 5b).
    /// </summary>
    public IReadOnlyCollection<int>? LastChangedBrushUids { get; private set; }

    /// <summary>Raised when the brush/face/vertex selection changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the editing mode changes.</summary>
    public event Action<EditMode>? ModeChanged;

    public EditMode Mode { get; private set; } = EditMode.Object;

    /// <summary>The brushes in time order (list order = build/time order).</summary>
    public IReadOnlyList<Brush> Brushes => FindSection()?.Brushes ?? (IReadOnlyList<Brush>)Array.Empty<Brush>();

    public IReadOnlyCollection<int> SelectedBrushes => _selectedBrushes;

    public IReadOnlyCollection<(int Brush, int Face)> SelectedFaces => _selectedFaces;

    public IReadOnlyCollection<(int Brush, int Vertex)> SelectedVertices => _selectedVertices;

    /// <summary>The selected edges as (brush UID, canonical low vertex, high vertex) — item 2.</summary>
    public IReadOnlyCollection<(int Brush, int V0, int V1)> SelectedEdges => _selectedEdges;

    public void SetMode(EditMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    public Brush? FindBrush(int uid) => Brushes.FirstOrDefault(b => b.Uid == uid);

    /// <summary>True when the brush is locked (<see cref="BrushState.Locked"/>) — unselectable
    /// and untransformable until unlocked. Unknown UIDs read as unlocked.</summary>
    public bool IsBrushLocked(int uid) => FindBrush(uid)?.State == BrushState.Locked;

    /// <summary>The time index (build order) of a brush, or -1.</summary>
    public int TimeIndex(int uid)
    {
        BrushesSection? s = FindSection();
        return s is null ? -1 : s.Brushes.FindIndex(b => b.Uid == uid);
    }

    // ---- Selection ------------------------------------------------------------

    public bool IsBrushSelected(int uid) => _selectedBrushes.Contains(uid);

    /// <summary>
    /// Clears the brush/face/vertex sub-selections whose kind is not retained — used on a
    /// mode / selection-filter switch so a selection made under one granularity cannot
    /// linger (and be transformed) under a mode that does not allow it.
    /// </summary>
    public void RetainSelectionKinds(bool brushes, bool faces, bool vertices, bool edges = true)
    {
        bool cleared =
            (!brushes && _selectedBrushes.Count > 0) ||
            (!faces && _selectedFaces.Count > 0) ||
            (!vertices && _selectedVertices.Count > 0) ||
            (!edges && _selectedEdges.Count > 0);
        if (!cleared)
        {
            return;
        }

        CaptureSelectionMemory();
        if (!brushes)
        {
            _selectedBrushes.Clear();
        }

        if (!faces)
        {
            _selectedFaces.Clear();
        }

        if (!vertices)
        {
            _selectedVertices.Clear();
        }

        if (!edges)
        {
            _selectedEdges.Clear();
        }

        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selectedBrushes.Count + _selectedFaces.Count + _selectedVertices.Count + _selectedEdges.Count == 0)
        {
            return;
        }

        CaptureSelectionMemory();
        _selectedBrushes.Clear();
        _selectedFaces.Clear();
        _selectedVertices.Clear();
        _selectedEdges.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects an edge (canonicalized), replacing the edge selection unless additive.</summary>
    internal void SelectEdge(int brush, int v0, int v1, bool additive = false)
    {
        if (!additive)
        {
            CaptureSelectionMemory();
            _selectedEdges.Clear();
        }

        _selectedEdges.Add(Canon(brush, v0, v1));
        SelectionChanged?.Invoke();
    }

    internal void ToggleEdge(int brush, int v0, int v1)
    {
        (int, int, int) key = Canon(brush, v0, v1);
        if (!_selectedEdges.Remove(key))
        {
            _selectedEdges.Add(key);
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>Adds a set of edges to the current edge selection (loop / ring select).</summary>
    internal void SelectEdges(int brush, IEnumerable<BrushEdge> edges, bool additive)
    {
        if (!additive)
        {
            CaptureSelectionMemory();
            _selectedEdges.Clear();
        }

        foreach (BrushEdge e in edges)
        {
            _selectedEdges.Add(Canon(brush, e.V0, e.V1));
        }

        SelectionChanged?.Invoke();
    }

    public bool IsEdgeSelected(int brush, int v0, int v1) => _selectedEdges.Contains(Canon(brush, v0, v1));

    private static (int, int, int) Canon(int brush, int v0, int v1) => v0 <= v1 ? (brush, v0, v1) : (brush, v1, v0);

    internal void SelectBrush(int uid, bool additive = false)
    {
        if (!additive)
        {
            CaptureSelectionMemory();
            _selectedBrushes.Clear();
        }

        _selectedBrushes.Add(uid);
        SelectionChanged?.Invoke();
    }

    internal void ToggleBrush(int uid)
    {
        if (!_selectedBrushes.Remove(uid))
        {
            _selectedBrushes.Add(uid);
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Adds many brushes to the selection in one shot, raising <see cref="SelectionChanged"/>
    /// EXACTLY ONCE (the batch marquee path — item P1: per-brush <see cref="SelectBrush"/> fired an
    /// event per caught brush, and each event drove a full panel rebuild, so a big box-select was
    /// O(n²) work that froze the UI). Mirrors <see cref="EditorDocument.SelectMany"/>.
    /// </summary>
    internal void SelectBrushes(IEnumerable<int> uids, bool additive = false)
    {
        if (!additive)
        {
            CaptureSelectionMemory();
            _selectedBrushes.Clear();
        }

        foreach (int uid in uids)
        {
            _selectedBrushes.Add(uid);
        }

        SelectionChanged?.Invoke();
    }

    internal void SelectFace(int brush, int face, bool additive = false)
    {
        if (!additive)
        {
            CaptureSelectionMemory();
            _selectedFaces.Clear();
        }

        _selectedFaces.Add((brush, face));
        SelectionChanged?.Invoke();
    }

    internal void ToggleFace(int brush, int face)
    {
        if (!_selectedFaces.Remove((brush, face)))
        {
            _selectedFaces.Add((brush, face));
        }

        SelectionChanged?.Invoke();
    }

    internal void SelectVertex(int brush, int vertex, bool additive = false)
    {
        if (!additive)
        {
            CaptureSelectionMemory();
            _selectedVertices.Clear();
        }

        _selectedVertices.Add((brush, vertex));
        SelectionChanged?.Invoke();
    }

    internal void ToggleVertex(int brush, int vertex)
    {
        if (!_selectedVertices.Remove((brush, vertex)))
        {
            _selectedVertices.Add((brush, vertex));
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>Grows the current face/vertex selection to the whole owning brush (Shift+S).</summary>
    public void GrowToBrush()
    {
        var owners = _selectedFaces.Select(f => f.Brush)
            .Concat(_selectedVertices.Select(v => v.Brush))
            .Distinct()
            .ToList();
        foreach (int uid in owners)
        {
            _selectedBrushes.Add(uid);
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Texture-mode Shift+S: grows the face selection to every face of the brushes
    /// that own a currently-selected face.
    /// </summary>
    public void GrowFacesToBrush()
    {
        if (_selectedFaces.Count == 0)
        {
            return;
        }

        var owners = _selectedFaces.Select(f => f.Brush).Distinct().ToList();
        foreach (int uid in owners)
        {
            if (FindBrush(uid) is Brush b)
            {
                for (int fi = 0; fi < b.Geometry.Faces.Count; fi++)
                {
                    _selectedFaces.Add((uid, fi));
                }
            }
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Shift+D: restricted to the brushes that already have at least one selected face,
    /// selects every face in those brushes whose texture name matches the texture of at
    /// least one currently-selected face (the union of the selected faces' textures).
    /// Brushes with no selected face are left untouched — matches elsewhere in the level
    /// are NOT pulled in. No-op when no face is selected.
    /// </summary>
    public void SelectSameTexture()
    {
        if (_selectedFaces.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var brushesWithSelection = new HashSet<int>();
        foreach ((int uid, int fi) in _selectedFaces)
        {
            brushesWithSelection.Add(uid);
            if (TextureNameOf(uid, fi) is string name)
            {
                wanted.Add(name);
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        CaptureSelectionMemory();
        _selectedFaces.Clear();
        foreach (Brush b in Brushes)
        {
            if (!brushesWithSelection.Contains(b.Uid))
            {
                continue; // only expand within brushes that already had a selected face
            }

            for (int fi = 0; fi < b.Geometry.Faces.Count; fi++)
            {
                Face f = b.Geometry.Faces[fi];
                if (f.Texture >= 0 && f.Texture < b.Geometry.Textures.Count &&
                    wanted.Contains(b.Geometry.Textures[f.Texture]))
                {
                    _selectedFaces.Add((b.Uid, fi));
                }
            }
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>Backspace: swaps the current selection with the remembered previous one.</summary>
    public void ReselectPrevious()
    {
        var current = (
            new HashSet<int>(_selectedBrushes),
            new HashSet<(int, int)>(_selectedFaces),
            new HashSet<(int, int)>(_selectedVertices),
            new HashSet<(int, int, int)>(_selectedEdges));

        _selectedBrushes.Clear();
        _selectedBrushes.UnionWith(_selectionMemory.Brushes);
        _selectedFaces.Clear();
        _selectedFaces.UnionWith(_selectionMemory.Faces);
        _selectedVertices.Clear();
        _selectedVertices.UnionWith(_selectionMemory.Vertices);
        _selectedEdges.Clear();
        _selectedEdges.UnionWith(_selectionMemory.Edges);

        _selectionMemory = current;
        SelectionChanged?.Invoke();
    }

    /// <summary>The texture name bound to a brush face, or null if unresolved.</summary>
    public string? TextureNameOf(int brushUid, int faceIndex)
    {
        Brush? b = FindBrush(brushUid);
        if (b is null || faceIndex < 0 || faceIndex >= b.Geometry.Faces.Count)
        {
            return null;
        }

        int tex = b.Geometry.Faces[faceIndex].Texture;
        return tex >= 0 && tex < b.Geometry.Textures.Count ? b.Geometry.Textures[tex] : null;
    }

    private void CaptureSelectionMemory() => _selectionMemory = (
        new HashSet<int>(_selectedBrushes),
        new HashSet<(int, int)>(_selectedFaces),
        new HashSet<(int, int)>(_selectedVertices),
        new HashSet<(int, int, int)>(_selectedEdges));

    // ---- Create / delete / reorder --------------------------------------------

    /// <summary>Creates a brush from cookie-cutter params at a world pose, appended at end-of-time.</summary>
    public int CreateBrush(BrushCreateParams p, Vec3 position, Mat3 rotation, IO.Mesh.V3dFile? mesh = null)
    {
        int uid = _doc.AllocateUid();
        Brush b = BrushFactory.Create(p, uid, mesh);
        b.Position = position;
        b.Rotation = rotation;
        AddBrush(b, "Create brush");
        return uid;
    }

    /// <summary>Adds an existing brush (undo-able), creating the section if needed.</summary>
    public void AddBrush(Brush brush, string description = "Add brush")
    {
        (BrushesSection section, RflSection host, bool createdSection) = EnsureSection();
        _doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                if (createdSection && !_doc.Rfl.Sections.Contains(host))
                {
                    InsertSection(host);
                }

                section.Brushes.Add(brush);
                host.Dirty = true;
                Changed();
            },
            () =>
            {
                section.Brushes.Remove(brush);
                host.Dirty = true;
                if (createdSection && section.Brushes.Count == 0)
                {
                    _doc.Rfl.Sections.Remove(host);
                }

                Changed();
            }));
    }

    /// <summary>
    /// Carve (stock permanent boolean): subtracts a selected cutter brush from
    /// every other brush it intersects, replacing their geometry as one undo-able
    /// edit. Returns the number of brushes carved.
    /// </summary>
    public int CarveSelected()
    {
        BrushesSection? section = FindSection();
        if (section is null || _selectedBrushes.Count == 0)
        {
            return 0;
        }

        int cutterUid = System.Linq.Enumerable.First(_selectedBrushes);
        Brush? cutter = FindBrush(cutterUid);
        if (cutter is null)
        {
            return 0;
        }

        var swaps = new List<(Brush Target, Geometry Old, Geometry Carved)>();
        foreach (Brush b in section.Brushes)
        {
            if (b.Uid == cutterUid)
            {
                continue;
            }

            Geometry? carved = CarveOps.Carve(b, cutter);
            if (carved is not null)
            {
                swaps.Add((b, b.Geometry, carved));
            }
        }

        if (swaps.Count == 0)
        {
            return 0;
        }

        RflSection host = HostOf(section);
        _doc.Undo.Execute(new RelayCommand($"Carve {swaps.Count} brush(es)",
            () =>
            {
                foreach (var s in swaps)
                {
                    s.Target.Geometry = s.Carved;
                }

                host.Dirty = true;
                Changed();
            },
            () =>
            {
                foreach (var s in swaps)
                {
                    s.Target.Geometry = s.Old;
                }

                host.Dirty = true;
                Changed();
            }));
        return swaps.Count;
    }

    /// <summary>Deletes brushes by UID (undo-able).</summary>
    public void DeleteBrushes(IReadOnlyCollection<int> uids)
    {
        BrushesSection? section = FindSection();
        if (section is null || uids.Count == 0)
        {
            return;
        }

        RflSection host = HostOf(section);
        var captured = section.Brushes
            .Select((b, i) => (Brush: b, Index: i))
            .Where(t => uids.Contains(t.Brush.Uid))
            .OrderByDescending(t => t.Index)
            .ToArray();
        if (captured.Length == 0)
        {
            return;
        }

        foreach (var (b, _) in captured)
        {
            _selectedBrushes.Remove(b.Uid);
        }

        // Deleting a brush that belongs to a group/mover must scrub it from the group and remove its
        // movers-section copy, dissolving an emptied moving group — otherwise the group keeps a dead
        // brush UID and an orphan mover animating a phantom (tester reports 3 & 5).
        var deletedUids = captured.Select(t => t.Brush.Uid).ToList();
        MovingGroupMaintenance.Snapshot? maintenance = null;
        _doc.Undo.Execute(new RelayCommand($"Delete {captured.Length} brush(es)",
            () =>
            {
                foreach (var (b, _) in captured)
                {
                    section.Brushes.Remove(b);
                }

                host.Dirty = true;
                maintenance = MovingGroupMaintenance.ApplyMemberDeletion(_doc.Rfl, deletedUids);
                Changed();
                if (maintenance is not null)
                {
                    _doc.RefreshObjects();
                }
            },
            () =>
            {
                bool hadMaintenance = maintenance is not null;
                if (maintenance is { } snap)
                {
                    MovingGroupMaintenance.Revert(_doc.Rfl, snap);
                    maintenance = null;
                }

                foreach (var (b, index) in Enumerable.Reverse(captured))
                {
                    section.Brushes.Insert(Math.Clamp(index, 0, section.Brushes.Count), b);
                }

                host.Dirty = true;
                Changed();
                if (hadMaintenance)
                {
                    _doc.RefreshObjects();
                }
            }));
    }

    /// <summary>Moves the selected brushes to the start of time (front of the list).</summary>
    public void MoveToStartOfTime(IReadOnlyCollection<int> uids) => Reorder(uids, toStart: true);

    /// <summary>Moves the selected brushes to the end of time (back of the list).</summary>
    public void MoveToEndOfTime(IReadOnlyCollection<int> uids) => Reorder(uids, toStart: false);

    private void Reorder(IReadOnlyCollection<int> uids, bool toStart)
    {
        BrushesSection? section = FindSection();
        if (section is null || uids.Count == 0)
        {
            return;
        }

        RflSection host = HostOf(section);
        List<Brush> before = section.Brushes.ToList();
        List<Brush> moving = before.Where(b => uids.Contains(b.Uid)).ToList();
        List<Brush> rest = before.Where(b => !uids.Contains(b.Uid)).ToList();
        List<Brush> after = toStart ? moving.Concat(rest).ToList() : rest.Concat(moving).ToList();
        if (after.SequenceEqual(before))
        {
            return;
        }

        _doc.Undo.Execute(new RelayCommand(toStart ? "Move to start of time" : "Move to end of time",
            () => { Replace(section.Brushes, after); host.Dirty = true; Changed(); },
            () => { Replace(section.Brushes, before); host.Dirty = true; Changed(); }));
    }

    /// <summary>
    /// Reorders the brush list to match <paramref name="newOrderUids"/> (Layers-panel drag /
    /// nudge). One undo entry; marks the geometry dirty so the live CSG preview rebuilds.
    /// UIDs not present in the list keep their relative order at the end.
    /// </summary>
    public void ReorderTo(IReadOnlyList<int> newOrderUids)
    {
        ArgumentNullException.ThrowIfNull(newOrderUids);
        BrushesSection? section = FindSection();
        if (section is null)
        {
            return;
        }

        RflSection host = HostOf(section);
        List<Brush> before = section.Brushes.ToList();
        var byUid = before.ToDictionary(b => b.Uid);
        var listed = new HashSet<int>();
        var after = new List<Brush>(before.Count);
        foreach (int uid in newOrderUids)
        {
            if (listed.Add(uid) && byUid.TryGetValue(uid, out Brush? b))
            {
                after.Add(b);
            }
        }

        foreach (Brush b in before)
        {
            if (!listed.Contains(b.Uid))
            {
                after.Add(b);
            }
        }

        if (after.Count != before.Count || after.SequenceEqual(before))
        {
            return;
        }

        _doc.Undo.Execute(new RelayCommand("Reorder brushes",
            () => { Replace(section.Brushes, after); host.Dirty = true; Changed(); },
            () => { Replace(section.Brushes, before); host.Dirty = true; Changed(); }));
    }

    /// <summary>Locks/unlocks brushes via their <see cref="BrushState"/> (undoable, dirties the file).</summary>
    public void SetBrushLocked(IReadOnlyCollection<int> uids, bool locked)
    {
        BrushesSection? section = FindSection();
        if (section is null)
        {
            return;
        }

        int target = locked ? BrushState.Locked : BrushState.Normal;
        var changes = section.Brushes
            .Where(b => uids.Contains(b.Uid) && (b.State == BrushState.Locked) != locked)
            .Select(b => (Brush: b, Old: b.State))
            .ToList();
        if (changes.Count == 0)
        {
            return;
        }

        RflSection host = HostOf(section);
        _doc.Undo.Execute(new RelayCommand(locked ? "Lock brushes" : "Unlock brushes",
            () => { foreach (var (b, _) in changes) { b.State = target; } host.Dirty = true; VisibilityChanged?.Invoke(); },
            () => { foreach (var (b, old) in changes) { b.State = old; } host.Dirty = true; VisibilityChanged?.Invoke(); }));

        // A locked item must not stay selected (G: coherent state). Selection is transient
        // (never part of undo here), so drop the now-locked brushes/sub-geometry outside the
        // undo command, mirroring the session-only object lock.
        if (locked)
        {
            var lockedSet = new HashSet<int>(changes.Select(c => c.Brush.Uid));
            bool removed = _selectedBrushes.RemoveWhere(lockedSet.Contains) > 0;
            removed |= _selectedFaces.RemoveWhere(f => lockedSet.Contains(f.Brush)) > 0;
            removed |= _selectedVertices.RemoveWhere(v => lockedSet.Contains(v.Brush)) > 0;
            removed |= _selectedEdges.RemoveWhere(e => lockedSet.Contains(e.Brush)) > 0;
            if (removed)
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Unlocks EVERY locked brush (stock Shift+Q / "Unlock All"): a brush lock is a PERSISTED
    /// state field (<see cref="BrushState.Locked"/>) loaded from the RFL — RED's lock/unlock
    /// command (RED.exe lock_members @ 0x442020) writes the brush state field directly
    /// (lock: [brush+0x48]=2 @ 0x442073, unlock: [brush+0x48]=0 @ 0x44207c), the same field
    /// serialized as rfl brush.state ({0 normal, 2 locked, 3 selected}). So a level shipped with
    /// locked brushes (e.g. ctf06 UID 414, state=2) can be unlocked and re-saved unlocked.
    /// Undoable and dirties the file (the persisted field changed, so Save must write it and undo
    /// must restore it — RED writes the field but was not observed to set its modified flag on this
    /// path); a no-op (nothing locked) leaves the document byte-identical. Returns the count freed.
    /// </summary>
    public int UnlockAll()
    {
        var lockedUids = Brushes.Where(b => b.State == BrushState.Locked).Select(b => b.Uid).ToList();
        if (lockedUids.Count > 0)
        {
            SetBrushLocked(lockedUids, locked: false);
        }

        return lockedUids.Count;
    }

    // ---- Brush hide (session-only view state; not persisted, like object lock) ----

    private readonly HashSet<int> _hiddenBrushes = new();

    /// <summary>Raised when brush lock/hide state changes so the scene + Layers panel refresh.</summary>
    public event Action? VisibilityChanged;

    /// <summary>The session-hidden brush UIDs (not persisted).</summary>
    public IReadOnlyCollection<int> HiddenBrushes => _hiddenBrushes;

    public bool IsBrushHidden(int uid) => _hiddenBrushes.Contains(uid);

    /// <summary>Hides/shows brushes (session state); raises <see cref="VisibilityChanged"/>.</summary>
    public void SetBrushHidden(IReadOnlyCollection<int> uids, bool hidden)
    {
        bool changed = false;
        foreach (int uid in uids)
        {
            changed |= hidden ? _hiddenBrushes.Add(uid) : _hiddenBrushes.Remove(uid);
        }

        if (changed)
        {
            VisibilityChanged?.Invoke();
        }
    }

    // ---- Copy / paste ---------------------------------------------------------

    private readonly List<Brush> _clipboard = new();

    public bool HasClipboard => _clipboard.Count > 0;

    /// <summary>Copies the selected brushes (deep clone) to the brush clipboard.</summary>
    public void CopySelected()
    {
        _clipboard.Clear();
        foreach (int uid in _selectedBrushes)
        {
            if (FindBrush(uid) is Brush b)
            {
                _clipboard.Add(GeometryClone.Deep(b));
            }
        }
    }

    /// <summary>Pastes the clipboard brushes with fresh UIDs and independent geometry.</summary>
    public IReadOnlyList<int> Paste(Vec3 offset = default)
    {
        if (_clipboard.Count == 0)
        {
            return Array.Empty<int>();
        }

        (BrushesSection section, RflSection host, bool createdSection) = EnsureSection();
        var pasted = _clipboard.Select(b =>
        {
            Brush clone = GeometryClone.Deep(b);
            clone.Uid = _doc.AllocateUid();
            clone.Position = clone.Position.Add(offset);
            clone.State = BrushState.Normal;
            return clone;
        }).ToList();

        _doc.Undo.Execute(new RelayCommand($"Paste {pasted.Count} brush(es)",
            () =>
            {
                if (createdSection && !_doc.Rfl.Sections.Contains(host))
                {
                    InsertSection(host);
                }

                section.Brushes.AddRange(pasted);
                host.Dirty = true;
                Changed();
            },
            () =>
            {
                foreach (Brush b in pasted)
                {
                    section.Brushes.Remove(b);
                }

                host.Dirty = true;
                if (createdSection && section.Brushes.Count == 0)
                {
                    _doc.Rfl.Sections.Remove(host);
                }

                Changed();
            }));

        return pasted.Select(b => b.Uid).ToList();
    }

    // ---- In-place edits (transforms, ops) -------------------------------------

    /// <summary>
    /// Applies an in-place edit to the given brushes as one undo entry. Each brush
    /// is snapshotted before; on any failure every brush is restored and no undo
    /// entry is recorded. Returns the first failure, or success.
    /// </summary>
    public OpResult EditBrushes(IReadOnlyCollection<int> uids, string description, Func<Brush, OpResult> mutate)
    {
        BrushesSection? section = FindSection();
        if (section is null)
        {
            return OpResult.Fail("No brushes to edit.");
        }

        var targets = section.Brushes.Where(b => uids.Contains(b.Uid)).ToList();
        if (targets.Count == 0)
        {
            return OpResult.Fail("No brushes selected.");
        }

        RflSection host = HostOf(section);
        var before = targets.Select(GeometryClone.Deep).ToList();
        OpResult overall = OpResult.Ok(description);
        int triangulated = 0;
        foreach (Brush b in targets)
        {
            OpResult r = mutate(b);
            if (!r)
            {
                overall = r;
                break;
            }

            // Carry the edit-time planarity-guard count out of the mutate lambda: EditBrushes seeds
            // `overall` with a fresh Ok(description), so a successful op's FacesTriangulated would
            // otherwise be dropped (leaving the App's NotePlanarized silent). Summed across brushes.
            triangulated += r.FacesTriangulated;
        }

        if (!overall)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                Assign(targets[i], before[i]);
            }

            return overall;
        }

        var after = targets.Select(GeometryClone.Deep).ToList();
        var snapBefore = before;
        var snapAfter = after;
        var live = targets;
        _doc.Undo.Execute(new RelayCommand(description,
            () => { for (int i = 0; i < live.Count; i++) { Assign(live[i], snapAfter[i]); } host.Dirty = true; Changed(); },
            () => { for (int i = 0; i < live.Count; i++) { Assign(live[i], snapBefore[i]); } host.Dirty = true; Changed(); }));
        return overall with { FacesTriangulated = triangulated };
    }

    /// <summary>
    /// Applies a per-face action to every currently-selected face (grouped by brush)
    /// as one undo entry. Used by Texture mode for texture apply, UV mapping and
    /// per-face property edits. Only the brushes section is dirtied.
    /// </summary>
    public OpResult EditSelectedFaces(string description, Action<Geometry, int> action)
    {
        if (_selectedFaces.Count == 0)
        {
            return OpResult.Fail("Select a face first.");
        }

        var groups = _selectedFaces.GroupBy(f => f.Brush).ToList();
        Editor.UndoStack.Transaction? tx = groups.Count > 1 ? _doc.Undo.BeginTransaction(description) : null;
        OpResult worst = OpResult.Ok(description);
        foreach (var grp in groups)
        {
            var faces = grp.Select(f => f.Face).ToList();
            OpResult r = EditBrushes(new[] { grp.Key }, description, b =>
            {
                foreach (int fi in faces)
                {
                    if (fi >= 0 && fi < b.Geometry.Faces.Count)
                    {
                        action(b.Geometry, fi);
                    }
                }

                return OpResult.Ok();
            });
            if (!r)
            {
                worst = r;
            }
        }

        tx?.Commit();
        return worst;
    }

    /// <summary>
    /// Commits per-corner texture-UV assignments (the UV Unwrap editor's output) as
    /// one undo entry, grouped by brush. Each tuple targets a specific face corner.
    /// </summary>
    public OpResult SetFaceUvs(string description, IReadOnlyList<(int Brush, int Face, int Corner, Uv Uv)> edits)
    {
        if (edits.Count == 0)
        {
            return OpResult.Ok(description);
        }

        var groups = edits.GroupBy(e => e.Brush).ToList();
        Editor.UndoStack.Transaction? tx = groups.Count > 1 ? _doc.Undo.BeginTransaction(description) : null;
        OpResult worst = OpResult.Ok(description);
        foreach (var grp in groups)
        {
            var items = grp.ToList();
            OpResult r = EditBrushes(new[] { grp.Key }, description, b =>
            {
                foreach (var (_, face, corner, uv) in items)
                {
                    if (face >= 0 && face < b.Geometry.Faces.Count &&
                        corner >= 0 && corner < b.Geometry.Faces[face].Vertices.Count)
                    {
                        b.Geometry.Faces[face].Vertices[corner].TextureCoords = uv;
                    }
                }

                return OpResult.Ok();
            });
            if (!r)
            {
                worst = r;
            }
        }

        tx?.Commit();
        return worst;
    }

    /// <summary>Applies an always-succeeding transform to the selected brushes.</summary>
    public void TransformSelected(string description, Action<Brush> mutate, string? coalesceKey = null) =>
        EditBrushesCoalesced(_selectedBrushes.ToList(), description, b => { mutate(b); return OpResult.Ok(); }, coalesceKey);

    /// <summary>Like <see cref="EditBrushes"/> but supports coalescing a drag gesture into one entry.</summary>
    public OpResult EditBrushesCoalesced(IReadOnlyCollection<int> uids, string description, Func<Brush, OpResult> mutate, string? coalesceKey)
    {
        BrushesSection? section = FindSection();
        if (section is null)
        {
            return OpResult.Fail("No brushes to edit.");
        }

        var targets = section.Brushes.Where(b => uids.Contains(b.Uid)).ToList();
        if (targets.Count == 0)
        {
            return OpResult.Fail("No brushes selected.");
        }

        RflSection host = HostOf(section);
        var before = targets.Select(GeometryClone.Deep).ToList();
        foreach (Brush b in targets)
        {
            OpResult r = mutate(b);
            if (!r)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Assign(targets[i], before[i]);
                }

                return r;
            }
        }

        var after = targets.Select(GeometryClone.Deep).ToList();
        int[] editedUids = targets.Select(b => b.Uid).ToArray();
        _doc.Undo.Execute(new RelayCommand(description,
            () => { for (int i = 0; i < targets.Count; i++) { Assign(targets[i], after[i]); } host.Dirty = true; Changed(editedUids); },
            () => { for (int i = 0; i < targets.Count; i++) { Assign(targets[i], before[i]); } host.Dirty = true; Changed(editedUids); },
            coalesceKey));
        return OpResult.Ok(description);
    }

    /// <summary>
    /// Clips the selected brushes by a world plane, replacing each with its
    /// resulting piece(s) as one undo entry. Split doubles a brush; Cut trims it.
    /// </summary>
    public OpResult Clip(Vec3 planePoint, Vec3 planeNormal, ClipMode mode, bool flipNormal)
    {
        BrushesSection? section = FindSection();
        if (section is null || _selectedBrushes.Count == 0)
        {
            return OpResult.Fail("Select a brush to clip.");
        }

        RflSection host = HostOf(section);
        List<Brush> before = section.Brushes.ToList();
        var after = new List<Brush>();
        string? failure = null;
        foreach (Brush b in before)
        {
            if (!_selectedBrushes.Contains(b.Uid))
            {
                after.Add(b);
                continue;
            }

            ClipResult r = BrushOps.Clip(b, planePoint, planeNormal, mode, flipNormal);
            if (!r.Success)
            {
                failure = r.Message;
                after.Add(b);
                continue;
            }

            for (int i = 0; i < r.Pieces.Count; i++)
            {
                after.Add(new Brush
                {
                    Uid = i == 0 ? b.Uid : _doc.AllocateUid(),
                    Position = b.Position,
                    Rotation = b.Rotation,
                    Geometry = r.Pieces[i],
                    Flags = b.Flags,
                    Life = b.Life,
                    State = BrushState.Normal,
                });
            }
        }

        if (after.Count == before.Count && failure is not null)
        {
            return OpResult.Fail(failure);
        }

        _doc.Undo.Execute(new RelayCommand(mode == ClipMode.Split ? "Clip: split" : "Clip: cut",
            () => { Replace(section.Brushes, after); host.Dirty = true; Changed(); },
            () => { Replace(section.Brushes, before); host.Dirty = true; Changed(); }));
        return OpResult.Ok("Clip");
    }

    /// <summary>Fuses the selected brushes into one (undo-able).</summary>
    public OpResult Fuse()
    {
        BrushesSection? section = FindSection();
        if (section is null)
        {
            return OpResult.Fail("No brushes to fuse.");
        }

        var targets = section.Brushes.Where(b => _selectedBrushes.Contains(b.Uid)).ToList();
        (OpResult res, Brush? fused) = BrushOps.Fuse(targets);
        if (!res || fused is null)
        {
            return res;
        }

        RflSection host = HostOf(section);
        List<Brush> before = section.Brushes.ToList();
        int at = before.IndexOf(targets[0]);
        List<Brush> after = before.Where(b => !_selectedBrushes.Contains(b.Uid)).ToList();
        after.Insert(Math.Clamp(at, 0, after.Count), fused);
        var keep = fused.Uid;

        _doc.Undo.Execute(new RelayCommand("Fuse",
            () => { Replace(section.Brushes, after); host.Dirty = true; _selectedBrushes.Clear(); _selectedBrushes.Add(keep); Changed(); SelectionChanged?.Invoke(); },
            () => { Replace(section.Brushes, before); host.Dirty = true; Changed(); }));
        return OpResult.Ok("Fuse");
    }

    // ---- Section plumbing -----------------------------------------------------

    private BrushesSection? FindSection()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                return bs;
            }
        }

        return null;
    }

    private RflSection HostOf(BrushesSection content)
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (ReferenceEquals(s.Content, content))
            {
                return s;
            }
        }

        throw new InvalidOperationException("Brushes section content is not attached to a section.");
    }

    private (BrushesSection Section, RflSection Host, bool Created) EnsureSection()
    {
        BrushesSection? existing = FindSection();
        if (existing is not null)
        {
            return (existing, HostOf(existing), false);
        }

        var section = new BrushesSection();
        var host = new RflSection((uint)SectionType.Brushes, Array.Empty<byte>()) { Content = section, Dirty = true };
        return (section, host, true);
    }

    private void InsertSection(RflSection host)
    {
        _doc.Rfl.InsertSection(host);
    }

    private static void Replace(List<Brush> list, List<Brush> contents)
    {
        list.Clear();
        list.AddRange(contents);
    }

    /// <summary>Deep-copies a source brush's pose/flags/geometry into a live target.</summary>
    private static void Assign(Brush target, Brush source)
    {
        target.Position = source.Position;
        target.Rotation = source.Rotation;
        target.Flags = source.Flags;
        target.Life = source.Life;
        target.State = source.State;
        target.Geometry = GeometryClone.Deep(source.Geometry);
    }

    /// <summary>
    /// Raises <see cref="BrushesChanged"/>, recording the affected brush UIDs
    /// (<paramref name="affectedUids"/> = null for a structural/unknown change that
    /// invalidates the whole build-overlay stash).
    /// </summary>
    private void Changed(IReadOnlyCollection<int>? affectedUids = null)
    {
        // Selection cleanup runs every time (cheap; keeps the selection valid mid-batch).
        _selectedFaces.RemoveWhere(f => FindBrush(f.Brush) is null);
        _selectedVertices.RemoveWhere(v => FindBrush(v.Brush) is null);
        _selectedEdges.RemoveWhere(e => FindBrush(e.Brush) is null);
        _selectedBrushes.RemoveWhere(uid => FindBrush(uid) is null);

        // Inside a batch (an Instant undo/redo of a multi-command drag), hold the notification and union
        // the affected UIDs; the scope fires BrushesChanged once on close. A structural change (null uids)
        // makes the whole batch structural, so the stash is invalidated wholesale exactly once.
        if (_changeBatchDepth > 0)
        {
            _changePending = true;
            if (affectedUids is null)
            {
                _changeUidsUnknown = true;
            }
            else
            {
                foreach (int uid in affectedUids)
                {
                    _changeUids.Add(uid);
                }
            }

            return;
        }

        LastChangedBrushUids = affectedUids;
        BrushesChanged?.Invoke();
    }
}
