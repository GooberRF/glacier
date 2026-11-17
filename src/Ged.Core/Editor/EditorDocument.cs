using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Editor;

/// <summary>
/// The open editor document: a loaded <see cref="RflFile"/>, its parsed objects
/// projected as <see cref="LevelObject"/> handles, a selection/hidden/locked
/// model, an <see cref="UndoStack"/>, dirty tracking, and a UID registry. All
/// content edits go through the undo stack and mark the relevant RFL section
/// dirty so that <see cref="Save"/> re-serializes only what changed — an
/// unmodified open→save stays byte-identical (minus the timestamp).
/// </summary>
public sealed class EditorDocument
{
    private readonly HashSet<int> _sessionHidden = new();
    private readonly HashSet<LevelObject> _selection = new();
    private readonly HashSet<int> _locked = new();
    private HashSet<int>? _isolationVisibleUids;
    private readonly List<(LevelObjectKind Kind, object Model, LevelObject Template)> _clipboard = new();
    private List<LevelObject> _objects = new();
    private Dictionary<int, LevelObject> _byUid = new();
    private int _nextUid = 1;
    private int _savedPosition;
    private bool _externalDirty;

    public EditorDocument(RflFile rfl, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        Rfl = rfl;
        Path = path;

        // Repair the black-render section-order defect in early Glacier-authored files:
        // a lightmaps section written after static_geometry (which RF's two-phase loader
        // skips, leaving every world face bound to a blank lightmap and rendering black).
        // No-op for RED files and correctly-ordered files, so a clean document stays
        // byte-identical on resave. See RflFile.RepairLightmapOrder.
        Rfl.RepairLightmapOrder();

        Undo.Changed += () => DirtyChanged?.Invoke();

        // A whole-mover Object-mode transform (any path: gizmo, nudge, scripts, automation) carries the
        // mover's member brush along with its movers-section copy in the same undo step.
        Undo.CommandRunner = new Editing.MoverObjectTransformLink(this).Run;

        // RED lists every mover in the editor groups section as well as in moving_groups, byte-identical
        // (see MovingGroupMirror). Share those instances so an edit to a mover is an edit to both copies;
        // no bytes change, so the document stays clean.
        Editing.MovingGroupMirror.Link(Rfl);
        RefreshObjects();

        // Each mover's copy was taken before the document was opened: snapshot its member brushes now,
        // so later edits to them (and only those) are recognised as leaving the copy stale.
        MoverCopies.TrackUntracked(Rfl);

        // [ALPINE] Populate each brush's editor-visible geoable state (BrushFlags.Geoable) from the
        // alpine_level_properties geoable table, so a brush that is geoable in the file shows checked
        // in Properties and carries the Layers "G" badge. Purely in-memory (the brush record never
        // stores geoable), so a freshly loaded document stays clean and byte-identical on resave.
        Editing.AlpineGeoableState.SyncBrushFlagsFromTable(Rfl);
    }

    /// <summary>Raised after the object list is rebuilt (add/remove/paste/delete).</summary>
    public event Action? ObjectsChanged;

    /// <summary>Raised whenever the selection set changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised whenever visibility (hidden/locked) state changes.</summary>
    public event Action? VisibilityChanged;

    /// <summary>Raised when the dirty flag may have changed.</summary>
    public event Action? DirtyChanged;

    /// <summary>Raised after any link edit (add/remove/break) so link views can refresh.</summary>
    public event Action? LinksChanged;

    /// <summary>Invoked by the link service after a link edit.</summary>
    public void NotifyLinksChanged() => LinksChanged?.Invoke();

    public RflFile Rfl { get; }

    public string? Path { get; set; }

    public UndoStack Undo { get; } = new();

    /// <summary>
    /// Which mover member brushes changed since their <c>movers</c>-section copy was taken — the one
    /// record the editor, scripts and the automation hosts share to decide which mover copies a save or
    /// build must refresh (see <see cref="SyncStaleMoverCopies"/>).
    /// </summary>
    public MoverCopyTracker MoverCopies { get; } = new();

    /// <summary>
    /// Refreshes the <c>movers</c>-section copy of every mover whose member brushes were edited after the
    /// copy was taken (<see cref="MoverService.SyncStaleCopies"/>), folded into the latest undo step.
    /// Untouched movers are left exactly as they are. Saves call this first; builds and the other
    /// writers (playtest, packaging, autosave) call it before they read the copies. Returns the number
    /// of movers refreshed.
    /// </summary>
    public int SyncStaleMoverCopies() => new MoverService(this).SyncStaleCopies().Count;

    /// <summary>All level objects (movers, point objects, regions, player start).</summary>
    public IReadOnlyList<LevelObject> Objects => _objects;

    /// <summary>The current multi-selection.</summary>
    public IReadOnlyCollection<LevelObject> Selection => _selection;

    /// <summary>True while the document has unsaved content changes.</summary>
    public bool IsDirty => Undo.Position != _savedPosition || _externalDirty;

    /// <summary>
    /// Marks the document dirty for a change that does not go through the undo
    /// stack — notably a geometry build swapping the compiled sections.
    /// </summary>
    public void MarkDirty()
    {
        _externalDirty = true;
        DirtyChanged?.Invoke();
    }

    public bool HasClipboard => _clipboard.Count > 0;

    public static EditorDocument Open(string path) => new(RflFile.Load(path), path);

    public static EditorDocument OpenBytes(byte[] data, string? path = null) => new(RflFile.Load(data), path);

    /// <summary>Rebuilds the flat object list from the current RFL sections.</summary>
    public void RefreshObjects()
    {
        Rfl.ParseAllKnownSections();
        _objects = LevelObjectEnumerator.Enumerate(Rfl, _sessionHidden);
        _byUid = new Dictionary<int, LevelObject>();
        foreach (LevelObject o in _objects)
        {
            _byUid[o.Uid] = o;
        }

        _nextUid = Math.Max(_nextUid, MaxUsedUid() + 1);
        _selection.RemoveWhere(o => !_byUid.TryGetValue(o.Uid, out LevelObject? cur) || !ReferenceEquals(cur, o));
        ObjectsChanged?.Invoke();
    }

    /// <summary>
    /// The highest UID in use: level objects, brushes (not projected as objects but they still consume
    /// UIDs), and every UID that carries GED object metadata (tags, light cookies). Metadata counts
    /// because a reused UID would silently inherit the metadata of whatever used to own it.
    /// </summary>
    private int MaxUsedUid()
    {
        int max = 0;
        foreach (LevelObject o in _objects)
        {
            max = Math.Max(max, o.Uid);
        }

        foreach (RflSection section in Rfl.Sections)
        {
            if (section.Content is BrushesSection bs)
            {
                foreach (var b in bs.Brushes)
                {
                    max = Math.Max(max, b.Uid);
                }
            }
            else if (section.Content is GedObjectMetadataSection meta)
            {
                foreach (GedObjectMetadataRecord rec in meta.Entries)
                {
                    max = Math.Max(max, rec.Uid);
                }
            }
            else if (section.Content is AlpineMoverPathsSection paths)
            {
                // Alpine's per-keyframe / per-mover path records are keyed by UID; a reissued UID must not revive
                // a deleted keyframe's curve (editor_patch/mover_path.cpp:1571-1577).
                foreach (AlpineMoverPathKeyframe k in paths.Keyframes)
                {
                    max = Math.Max(max, k.KeyframeUid);
                }

                foreach (AlpineMoverPathMover m in paths.Movers)
                {
                    max = Math.Max(max, m.FirstKeyframeUid);
                }
            }
            else if (section.Content is AlpineLiquidEmissionSection glow)
            {
                foreach (AlpineLiquidEmission e in glow.Records)
                {
                    max = Math.Max(max, e.RoomEffectUid);
                }
            }
        }

        return max;
    }

    /// <summary>True when <paramref name="uid"/> belongs to a level object or a brush.</summary>
    internal bool IsUidInUse(int uid)
    {
        if (_byUid.ContainsKey(uid))
        {
            return true;
        }

        foreach (RflSection section in Rfl.Sections)
        {
            if (section.Content is BrushesSection bs && bs.Brushes.Any(b => b.Uid == uid))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The next UID <see cref="AllocateUid"/> will consider (a watermark a rolled-back script run restores).</summary>
    internal int UidWatermark => _nextUid;

    /// <summary>
    /// Returns UIDs consumed by work that was rolled back (a dry run or a failed script run), so a later
    /// run allocates the same UIDs a fresh run would. Never lowers the watermark below a UID still in use.
    /// </summary>
    internal void RestoreUidWatermark(int watermark) => _nextUid = Math.Max(watermark, MaxUsedUid() + 1);

    public LevelObject? FindByUid(int uid) => _byUid.GetValueOrDefault(uid);

    /// <summary>Allocates a fresh, unused UID.</summary>
    public int AllocateUid()
    {
        while (_byUid.ContainsKey(_nextUid))
        {
            _nextUid++;
        }

        return _nextUid++;
    }

    // ---- Selection (not undoable, does not dirty the file) --------------------

    public bool IsSelected(LevelObject o) => _selection.Contains(o);

    public bool IsLocked(LevelObject o) => _locked.Contains(o.Uid);

    public void ClearSelection()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        _selection.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>The UIDs of the selected objects (a rolled-back script run restores them with <see cref="RestoreSelection"/>).</summary>
    internal int[] CaptureSelection() => _selection.Select(o => o.Uid).ToArray();

    /// <summary>Re-selects exactly the objects in <paramref name="uids"/> that exist; raises SelectionChanged only on a change.</summary>
    internal void RestoreSelection(IReadOnlyCollection<int> uids)
    {
        var wanted = uids.Select(FindByUid).OfType<LevelObject>().ToHashSet();
        if (_selection.SetEquals(wanted))
        {
            return;
        }

        _selection.Clear();
        _selection.UnionWith(wanted);
        SelectionChanged?.Invoke();
    }

    internal void Select(LevelObject o, bool additive = false)
    {
        if (!additive)
        {
            _selection.Clear();
        }

        _selection.Add(o);
        SelectionChanged?.Invoke();
    }

    internal void SelectMany(IEnumerable<LevelObject> objects, bool additive = false)
    {
        if (!additive)
        {
            _selection.Clear();
        }

        foreach (LevelObject o in objects)
        {
            _selection.Add(o);
        }

        SelectionChanged?.Invoke();
    }

    internal void ToggleSelection(LevelObject o)
    {
        if (!_selection.Remove(o))
        {
            _selection.Add(o);
        }

        SelectionChanged?.Invoke();
    }

    internal void SelectAll() => SelectMany(_objects);

    internal void SelectAllOfKind(LevelObjectKind kind) =>
        SelectMany(_objects.Where(o => o.Kind == kind));

    /// <summary>Stock <c>I</c>: swaps selected and unselected.</summary>
    internal void InvertSelection()
    {
        var newSet = _objects.Where(o => !_selection.Contains(o)).ToList();
        _selection.Clear();
        foreach (LevelObject o in newSet)
        {
            _selection.Add(o);
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>Stock <c>U</c>: selects the object with the given UID, if any.</summary>
    internal LevelObject? SelectByUid(int uid)
    {
        LevelObject? o = FindByUid(uid);
        if (o is not null)
        {
            Select(o);
        }

        return o;
    }

    // ---- Visibility (undoable, dirties the file) ------------------------------

    /// <summary>Stock <c>H</c>: hides the selected objects.</summary>
    public void HideSelected() => ApplyHidden(_selection.ToList(), _ => true, "Hide selected");

    /// <summary>Stock <c>W</c>: hides every object.</summary>
    public void HideAllObjects() => ApplyHidden(_objects, _ => true, "Hide all objects");

    /// <summary>Stock <c>Shift+W</c>: unhides every object.</summary>
    public void UnhideAllObjects() => ApplyHidden(_objects, _ => false, "Unhide all objects");

    /// <summary>Stock <c>Shift+H</c>: unhides brushes/movers (modern: unhide all).</summary>
    public void UnhideAllBrushes() => ApplyHidden(_objects, _ => false, "Unhide all brushes");

    /// <summary>Stock <c>Ctrl+H</c>: inverts the hidden state of every object.</summary>
    public void InvertHidden() => ApplyHidden(_objects, o => !o.Hidden, "Invert hidden");

    /// <summary>Stock <c>X</c>: hides everything except clutter and entities.</summary>
    public void HideExceptClutterEntities() =>
        ApplyHidden(_objects, o => o.Kind is not (LevelObjectKind.Clutter or LevelObjectKind.Entity), "Hide all but clutter/entities");

    /// <summary>Stock <c>Shift+X</c>: unhides everything except clutter and entities.</summary>
    public void UnhideExceptClutterEntities() =>
        ApplyHidden(_objects, o => false, "Unhide all but clutter/entities");

    private void ApplyHidden(IReadOnlyList<LevelObject> targets, Func<LevelObject, bool> compute, string description)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var snapshot = targets.Select(o => (Obj: o, Old: o.Hidden, New: compute(o)))
            .Where(t => t.Old != t.New)
            .ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        Undo.Execute(new RelayCommand(description,
            () =>
            {
                foreach (var t in snapshot)
                {
                    t.Obj.Hidden = t.New;
                }

                VisibilityChanged?.Invoke();
            },
            () =>
            {
                foreach (var t in snapshot)
                {
                    t.Obj.Hidden = t.Old;
                }

                VisibilityChanged?.Invoke();
            }));
    }

    // ---- Lock (session only, not persisted) -----------------------------------

    /// <summary>Stock <c>Q</c>: locks the selected objects (editor-session state). A locked
    /// object is unselectable/untransformable, so locking removes it from the selection
    /// (coherent state); lock is session-only, so the deselect is not undoable either.</summary>
    public void LockSelected()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        foreach (LevelObject o in _selection)
        {
            _locked.Add(o.Uid);
        }

        _selection.Clear();
        SelectionChanged?.Invoke();
        VisibilityChanged?.Invoke();
    }

    /// <summary>Stock <c>Shift+Q</c>: unlocks everything.</summary>
    public void UnlockAll()
    {
        if (_locked.Count == 0)
        {
            return;
        }

        _locked.Clear();
        VisibilityChanged?.Invoke();
    }

    public void ToggleLock(LevelObject o)
    {
        if (!_locked.Remove(o.Uid))
        {
            _locked.Add(o.Uid);

            // Newly locked: drop it from the selection so no locked item stays selected.
            if (_selection.Remove(o))
            {
                SelectionChanged?.Invoke();
            }
        }

        VisibilityChanged?.Invoke();
    }

    /// <summary>
    /// Locks/unlocks a set of objects by UID (session lock; the same mechanism as <see cref="ToggleLock"/>
    /// and the Outliner padlock). Used by group lock to propagate to member OBJECTS so a locked group's
    /// object members are unselectable/untransformable with the same observable behaviour as its brush
    /// members (which use <see cref="BrushState.Locked"/>). Newly locked objects drop out of the
    /// selection. Not undoable (session-only), consistent with the other object-lock paths.
    /// </summary>
    public void SetObjectLocked(IEnumerable<int> uids, bool locked)
    {
        ArgumentNullException.ThrowIfNull(uids);
        bool changed = false;
        foreach (int uid in uids)
        {
            changed |= locked ? _locked.Add(uid) : _locked.Remove(uid);
        }

        if (!changed)
        {
            return;
        }

        if (locked && _selection.RemoveWhere(o => _locked.Contains(o.Uid)) > 0)
        {
            SelectionChanged?.Invoke();
        }

        VisibilityChanged?.Invoke();
    }

    // ---- Isolation (session view state; non-destructive, not undoable) ----------

    /// <summary>Stock B6: true while an Isolate Selection filter is active.</summary>
    public bool IsIsolated => _isolationVisibleUids is not null;

    /// <summary>
    /// Isolates a set of UIDs (the selection plus its group members): while active only
    /// these render. This is a non-destructive view overlay — it never touches the
    /// undoable <see cref="LevelObject.Hidden"/> flags, so exiting restores the EXACT
    /// prior visibility (a pre-existing hidden object stays hidden). Re-isolating with a
    /// new set just replaces the visible set.
    /// </summary>
    public void IsolateSelection(IEnumerable<int> visibleUids)
    {
        ArgumentNullException.ThrowIfNull(visibleUids);
        _isolationVisibleUids = new HashSet<int>(visibleUids);
        VisibilityChanged?.Invoke();
    }

    /// <summary>Exits isolation, restoring the exact prior visibility (no-op when not isolated).</summary>
    public void ExitIsolation()
    {
        if (_isolationVisibleUids is null)
        {
            return;
        }

        _isolationVisibleUids = null;
        VisibilityChanged?.Invoke();
    }

    /// <summary>
    /// Whether an object is hidden for rendering, accounting for isolation. While
    /// isolated, only the isolation set shows (independent of the per-object hidden
    /// flags); otherwise the object's own <see cref="LevelObject.Hidden"/> flag applies.
    /// </summary>
    public bool IsEffectivelyHidden(LevelObject o) =>
        _isolationVisibleUids is { } vis ? !vis.Contains(o.Uid) : o.Hidden;

    /// <summary>Whether a UID (object or brush) renders under the current isolation state.</summary>
    public bool IsVisibleUnderIsolation(int uid) =>
        _isolationVisibleUids is null || _isolationVisibleUids.Contains(uid);

    // ---- Annotations (editor-only, sidecar-backed, undoable) -------------------

    private readonly List<Annotation> _annotations = new();
    private int _nextAnnotationId = 1;

    /// <summary>Raised whenever the annotation set changes (add/remove/load).</summary>
    public event Action? AnnotationsChanged;

    /// <summary>The editor-only measurement/dimension annotations (feature 4 / B7).</summary>
    public IReadOnlyList<Annotation> Annotations => _annotations;

    /// <summary>Finds an annotation by id, or null.</summary>
    public Annotation? FindAnnotation(int id) => _annotations.FirstOrDefault(a => a.Id == id);

    /// <summary>Adds a dimension annotation between two world points (one undo entry).</summary>
    public Annotation AddAnnotation(Vec3 a, Vec3 b, string? label = null)
    {
        var ann = new Annotation { Id = _nextAnnotationId++, A = a, B = b, Label = label };
        Undo.Execute(new RelayCommand("Add annotation",
            () => { if (!_annotations.Contains(ann)) { _annotations.Add(ann); } AnnotationsChanged?.Invoke(); },
            () => { _annotations.Remove(ann); AnnotationsChanged?.Invoke(); }));
        return ann;
    }

    /// <summary>Removes an annotation by id (one undo entry); no-op when absent.</summary>
    public void RemoveAnnotation(int id)
    {
        int index = _annotations.FindIndex(a => a.Id == id);
        if (index < 0)
        {
            return;
        }

        Annotation ann = _annotations[index];
        int at = index;
        Undo.Execute(new RelayCommand("Delete annotation",
            () => { _annotations.Remove(ann); AnnotationsChanged?.Invoke(); },
            () => { _annotations.Insert(Math.Clamp(at, 0, _annotations.Count), ann); AnnotationsChanged?.Invoke(); }));
    }

    /// <summary>
    /// Replaces the annotation set (loading from the sidecar on open). Not undoable and
    /// does not dirty the document; re-seeds the id counter above the loaded ids.
    /// </summary>
    public void SetAnnotations(IEnumerable<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _annotations.Clear();
        _annotations.AddRange(annotations);
        _nextAnnotationId = 1;
        foreach (Annotation a in _annotations)
        {
            _nextAnnotationId = Math.Max(_nextAnnotationId, a.Id + 1);
        }

        AnnotationsChanged?.Invoke();
    }

    // ---- Copy / cut / paste / delete ------------------------------------------

    public void CopySelection()
    {
        _clipboard.Clear();
        foreach (LevelObject o in _selection.Where(o => o.CanRemove))
        {
            _clipboard.Add((o.Kind, o.CloneModel(), o));
        }
    }

    public void CutSelection()
    {
        CopySelection();
        DeleteSelection("Cut");
    }

    /// <summary>Pastes the clipboard, assigning fresh UIDs, as one undo entry.</summary>
    public IReadOnlyList<int> Paste()
    {
        if (_clipboard.Count == 0)
        {
            return Array.Empty<int>();
        }

        var added = new List<(System.Collections.IList List, object Model)>();
        var newUids = new List<int>();
        var remap = new Dictionary<int, int>();
        var pastedRoomEffects = new List<(int From, int To)>();
        foreach (var entry in _clipboard)
        {
            if (entry.Template.OwningList is not { } list)
            {
                continue;
            }

            object clone = ModelCloner.Clone(entry.Model);
            int uid = AllocateUid();
            ObjectUid.Set(clone, uid);
            _byUid[uid] = entry.Template; // reserve the UID so AllocateUid won't reuse it
            added.Add((list, clone));
            newUids.Add(uid);
            if (ObjectUid.TryGet(entry.Model, out int fromUid))
            {
                remap[fromUid] = uid;
                if (entry.Model is RoomEffect)
                {
                    pastedRoomEffects.Add((fromUid, uid));
                }
            }
        }

        if (added.Count == 0)
        {
            return Array.Empty<int>();
        }

        // A pasted waypoint helper keeps only the links between helpers that were copied together, re-aimed at the
        // copies (Alpine clones a helper without its links and rebuilds those between copied objects:
        // editor_patch/waypoint_helper.cpp:362-378).
        foreach (var (_, model) in added)
        {
            if (model is AlpineWaypointHelper helper)
            {
                List<int> kept = helper.Links.Where(remap.ContainsKey).Select(u => remap[u]).ToList();
                helper.Links.Clear();
                helper.Links.AddRange(kept);
            }
        }

        // A pasted room effect carries its liquid emission with it (liquid_emission.cpp:73-83).
        (Action Apply, Action Revert)? emissions = Editing.AlpineAttachedData.CopyEmissions(Rfl, pastedRoomEffects);

        var dirtySections = _clipboard.Select(e => e.Template.Section).Distinct().ToArray();
        Undo.Execute(new RelayCommand($"Paste {added.Count} object(s)",
            () =>
            {
                foreach (var (list, model) in added)
                {
                    list.Add(model);
                }

                foreach (RflSection s in dirtySections)
                {
                    s.Dirty = true;
                }

                emissions?.Apply();
                RefreshObjects();
            },
            () =>
            {
                emissions?.Revert();
                foreach (var (list, model) in added)
                {
                    list.Remove(model);
                }

                foreach (RflSection s in dirtySections)
                {
                    s.Dirty = true;
                }

                RefreshObjects();
            }));

        return newUids;
    }

    /// <summary>Deletes the selected list-backed objects as one undo entry.</summary>
    public void DeleteSelection(string description = "Delete")
    {
        var targets = _selection.Where(o => o.CanRemove).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        // Capture each object with its owning list and index (descending so removal
        // indices stay valid); undo re-inserts ascending.
        var captured = targets
            .Select(o => (o.OwningList!, o.Model, Index: o.IndexInSection, o.Section))
            .Where(t => t.Index >= 0)
            .OrderByDescending(t => t.Index)
            .ToArray();

        // The deleted UIDs drive the moving-group maintenance pass below: a deleted brush/object member
        // is scrubbed from its group and its movers-section copy removed; a deleted keyframe that empties
        // a moving group dissolves it — so no operation leaves an orphan/0-keyframe group or dead UID.
        var deletedUids = targets.Select(o => o.Uid).ToList();

        _selection.Clear();
        SelectionChanged?.Invoke();

        Editing.MovingGroupMaintenance.Snapshot? maintenance = null;
        GedObjectMetadataService.TagDropSnapshot? droppedTags = null;
        (Action Apply, Action Revert)? attached = null;
        Undo.Execute(new RelayCommand($"{description} {captured.Length} object(s)",
            () =>
            {
                foreach (var (list, model, _, section) in captured)
                {
                    list.Remove(model);
                    section.Dirty = true;
                }

                // A deleted keyframe's curve / a deleted room effect's liquid emission goes with it (Alpine saves
                // these records only for objects that exist: mover_path.cpp:1413-1440, liquid_emission.cpp:394-410).
                attached = Editing.AlpineAttachedData.PruneDeleted(Rfl, deletedUids);
                attached?.Apply();

                maintenance = Editing.MovingGroupMaintenance.ApplyMemberDeletion(Rfl, deletedUids);
                RefreshObjects();

                // A deleted object's tags go with it (restored on undo), so a later object that is
                // ever given the same UID cannot inherit them.
                droppedTags = GedObjectMetadataService.DropTagsOfDeleted(Rfl, deletedUids, IsUidInUse);
            },
            () =>
            {
                if (droppedTags is { } tags)
                {
                    GedObjectMetadataService.RestoreDroppedTags(Rfl, tags);
                    droppedTags = null;
                }

                if (maintenance is { } snap)
                {
                    Editing.MovingGroupMaintenance.Revert(Rfl, snap);
                    maintenance = null;
                }

                attached?.Revert();
                attached = null;

                foreach (var (list, model, index, section) in Enumerable.Reverse(captured))
                {
                    list.Insert(Math.Clamp(index, 0, list.Count), model);
                    section.Dirty = true;
                }

                RefreshObjects();
            }));
    }

    // ---- Placement (undoable, dirties the target section) ---------------------

    /// <summary>
    /// Places a new object of <paramref name="kind"/> at <paramref name="pos"/>,
    /// creating its section if the level lacks one. Undo-able as one entry;
    /// returns the resulting <see cref="LevelObject"/> handle.
    /// </summary>
    public LevelObject? PlaceObject(LevelObjectKind kind, Vec3 pos, string? className = null) =>
        PlaceObject(kind, pos, className, itemCount: null);

    /// <summary>
    /// As <see cref="PlaceObject(LevelObjectKind, Vec3, string?)"/>; a new item's count is
    /// <paramref name="itemCount"/> when given, else <see cref="ItemDefaultCount"/>'s value for its class,
    /// else the blueprint's 1.
    /// </summary>
    public LevelObject? PlaceObject(LevelObjectKind kind, Vec3 pos, string? className, int? itemCount)
    {
        int uid = AllocateUid();
        ObjectBlueprint bp = ObjectFactory.Build(kind, uid, pos, className);
        if (bp.Model is Item item && (itemCount ?? ItemDefaultCount?.Invoke(item.Header.ClassName)) is int count)
        {
            item.Count = count;
        }

        return PlaceBlueprint(bp, $"Place {kind}");
    }

    /// <summary>
    /// The default count of a newly placed item of a class — the host's <c>items.tbl</c> <c>$Count</c>
    /// (<see cref="GameCatalogs.ItemCount"/>), the value RED gives a new item: an Assault Rifle pickup
    /// holds 42 rounds, a Medical Kit 25 health. Null (no install mounted) keeps the blueprint's 1.
    /// </summary>
    public Func<string, int?>? ItemDefaultCount { get; set; }

    /// <summary>Places a pre-built blueprint (used by the palette and the round-trip harness).</summary>
    public LevelObject? PlaceBlueprint(ObjectBlueprint bp, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(bp);

        // A missing section is created inside the command (and removed again on undo), so undoing the
        // first placement of a kind leaves the file byte-identical.
        RflSection section = Rfl.FindOrPrepareSection(bp.Section, bp.CreateSection, out bool created);
        IRflSectionContent content = section.Content!;

        // An Alpine chunk kept raw (a layout Glacier cannot read exactly) is written back verbatim: a placed object
        // would be lost on save, so placement into it is refused.
        if (content is IRetainedChunk { IsReadOnly: true } retained)
        {
            throw new InvalidOperationException(
                $"This level's {SectionIds.Describe(section.TypeId)} chunk is kept unchanged ({retained.ReadOnlyReason}), so no {bp.Kind} can be added to it.");
        }

        Undo.Execute(new RelayCommand(description ?? $"Place {bp.Kind}",
            () =>
            {
                AttachSection(section, created);
                bp.Append(content);
                section.Dirty = true;
                RefreshObjects();
            },
            () =>
            {
                bp.Remove(content);
                section.Dirty = true;
                DetachSection(section, created);
                RefreshObjects();
            }));

        return FindByUid(bp.Uid);
    }

    /// <summary>
    /// Creates the level's single Player Start at <paramref name="pos"/> (identity orientation
    /// unless <paramref name="rotation"/> is given) as one undo entry; undo removes the section.
    /// If a Player Start already exists this returns it unchanged — a level only ever has one.
    /// The RFL writer recomputes the header's player_start_offset from the section on save, so a
    /// level authored this way spawns the player here instead of in the void.
    /// </summary>
    public LevelObject CreatePlayerStart(Vec3 pos, Mat3? rotation = null)
    {
        LevelObject? existing = _objects.FirstOrDefault(o => o.Kind == LevelObjectKind.PlayerStart);
        if (existing is not null)
        {
            return existing;
        }

        var section = new RflSection((uint)SectionType.PlayerStart, Array.Empty<byte>())
        {
            Content = new PlayerStartSection { Position = pos, Rotation = rotation ?? Mat3.Identity },
        };

        Undo.Execute(new RelayCommand("Create Player Start",
            () =>
            {
                Rfl.InsertSection(section);
                section.Dirty = true;
                RefreshObjects();
            },
            () =>
            {
                Rfl.Sections.Remove(section);
                RefreshObjects();
            }));

        return _objects.First(o => o.Kind == LevelObjectKind.PlayerStart);
    }

    /// <summary>Places a new event of the given schema at <paramref name="pos"/> (undo-able).</summary>
    public LevelObject? PlaceEvent(EventSchema schema, Vec3 pos, bool sampleValues = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        int uid = AllocateUid();
        RflEvent ev = sampleValues
            ? EventFactory.CreateSample(schema, uid, pos, Rfl.Header.Version)
            : EventFactory.Create(schema, uid, pos, Rfl.Header.Version);

        RflSection section = Rfl.FindOrPrepareSection(SectionType.Events, () => new EventsSection(), out bool created);
        var events = (EventsSection)section.Content!;
        Undo.Execute(new RelayCommand($"Place {schema.ClassName}",
            () =>
            {
                AttachSection(section, created);
                events.Events.Add(ev);
                section.Dirty = true;
                RefreshObjects();
            },
            () =>
            {
                events.Events.Remove(ev);
                section.Dirty = true;
                DetachSection(section, created);
                RefreshObjects();
            }));

        return FindByUid(uid);
    }

    /// <summary>Inserts a section an edit created (inside that edit's command); no-op for an existing one.</summary>
    private void AttachSection(RflSection section, bool created)
    {
        if (created && !Rfl.Sections.Contains(section))
        {
            Rfl.InsertSection(section);
        }
    }

    /// <summary>Removes a section the undone edit created, once it holds nothing again.</summary>
    private void DetachSection(RflSection section, bool created)
    {
        if (created)
        {
            Rfl.Sections.Remove(section);
        }
    }

    // ---- Convert clutter/entity → Mesh object (Alpine "To Mesh Object") -------

    /// <summary>The outcome of a clutter/entity → mesh conversion, for the caller's status report.</summary>
    public sealed class MeshConversionReport
    {
        public IReadOnlyList<int> NewMeshUids { get; init; } = Array.Empty<int>();

        public int ConvertedCount { get; init; }

        public int CoronaCount { get; init; }

        public int ThrusterCount { get; init; }

        public int ClutterCount { get; init; }

        /// <summary>Source UIDs left in place because deleting them would empty a moving group.</summary>
        public IReadOnlyList<int> SkippedSoleGroupUids { get; init; } = Array.Empty<int>();
    }

    /// <summary>
    /// Converts placed clutter/entity objects into Alpine Mesh objects, inheriting each class's
    /// destructibility and spawning its child coronas / thruster meshes, as ONE undo transaction
    /// (create the mesh objects + children, remove the sources). Sources that are the sole member
    /// of a moving group are left in place (Alpine's guard). Selects the new mesh objects and
    /// returns a report. Mirrors editor_patch/alpine_obj.cpp:1461-1678.
    /// </summary>
    public MeshConversionReport ConvertObjectsToMesh(
        IEnumerable<LevelObject> sources,
        ClutterCatalog? clutter,
        EntityCatalog? entities,
        IMeshTagSource? tagSource = null,
        Func<string, GlareDef?>? glareLookup = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceList = sources.Where(ObjectToMeshConverter.CanConvert).ToList();

        var converted = new List<(LevelObject Src, MeshConversionPlan Plan)>();
        var newMeshUids = new List<int>();
        int coronaCount = 0, thrusterCount = 0, clutterCount = 0;
        foreach (LevelObject src in sourceList)
        {
            MeshConversionPlan? plan = ObjectToMeshConverter.BuildPlan(src, clutter, entities, tagSource, glareLookup);
            if (plan is null)
            {
                continue;
            }

            plan.Mesh.Uid = AllocateUid();
            newMeshUids.Add(plan.Mesh.Uid);
            foreach (AlpineMeshObject tm in plan.ThrusterMeshes)
            {
                tm.Uid = AllocateUid();
            }

            foreach (AlpineCoronaObject co in plan.Coronas)
            {
                co.Uid = AllocateUid();
            }

            coronaCount += plan.Coronas.Count;
            thrusterCount += plan.ThrusterMeshes.Count;
            clutterCount += plan.InheritedClutter ? 1 : 0;
            converted.Add((src, plan));
        }

        if (converted.Count == 0)
        {
            return new MeshConversionReport();
        }

        RflSection meshSection = Rfl.GetOrCreateSection(SectionType.AlpineMeshObjects, () => new AlpineMeshObjectsSection());
        var meshContent = (AlpineMeshObjectsSection)meshSection.Content!;

        RflSection? coronaSection = null;
        AlpineCoronaObjectsSection? coronaContent = null;
        if (coronaCount > 0)
        {
            coronaSection = Rfl.GetOrCreateSection(SectionType.AlpineCoronaObjects, () => new AlpineCoronaObjectsSection());
            coronaContent = (AlpineCoronaObjectsSection)coronaSection.Content!;
        }

        var meshesToAdd = converted
            .SelectMany(c => new[] { c.Plan.Mesh }.Concat(c.Plan.ThrusterMeshes))
            .ToList();
        var coronasToAdd = converted.SelectMany(c => c.Plan.Coronas).ToList();

        // Source removals, minus sole moving-group members (which we leave in place, Alpine's guard).
        var skipped = new List<int>();
        var removals = new List<(System.Collections.IList List, object Model, int Index, RflSection Section)>();
        foreach ((LevelObject src, _) in converted)
        {
            if (IsSoleMovingGroupMember(src.Uid))
            {
                skipped.Add(src.Uid);
                continue;
            }

            if (src.OwningList is { } list && src.IndexInSection is int idx && idx >= 0)
            {
                removals.Add((list, src.Model, idx, src.Section));
            }
        }

        var captured = removals.OrderByDescending(t => t.Index).ToArray();

        Undo.Execute(new RelayCommand(
            $"Convert {converted.Count} object(s) to mesh",
            () =>
            {
                foreach (AlpineMeshObject m in meshesToAdd)
                {
                    meshContent.Meshes.Add(m);
                }

                if (coronaContent is not null)
                {
                    foreach (AlpineCoronaObject co in coronasToAdd)
                    {
                        coronaContent.Coronas.Add(co);
                    }
                }

                foreach (var (list, model, _, section) in captured)
                {
                    list.Remove(model);
                    section.Dirty = true;
                }

                meshSection.Dirty = true;
                if (coronaSection is not null)
                {
                    coronaSection.Dirty = true;
                }

                RefreshObjects();
            },
            () =>
            {
                foreach (AlpineMeshObject m in meshesToAdd)
                {
                    meshContent.Meshes.Remove(m);
                }

                if (coronaContent is not null)
                {
                    foreach (AlpineCoronaObject co in coronasToAdd)
                    {
                        coronaContent.Coronas.Remove(co);
                    }
                }

                foreach (var (list, model, index, section) in Enumerable.Reverse(captured))
                {
                    list.Insert(Math.Clamp(index, 0, list.Count), model);
                    section.Dirty = true;
                }

                meshSection.Dirty = true;
                if (coronaSection is not null)
                {
                    coronaSection.Dirty = true;
                }

                RefreshObjects();
            }));

        SelectMany(newMeshUids.Select(FindByUid).Where(o => o is not null).Select(o => o!));

        return new MeshConversionReport
        {
            NewMeshUids = newMeshUids,
            ConvertedCount = converted.Count,
            CoronaCount = coronaCount,
            ThrusterCount = thrusterCount,
            ClutterCount = clutterCount,
            SkippedSoleGroupUids = skipped,
        };
    }

    /// <summary>True when <paramref name="uid"/> is the only member of some moving group (deleting it
    /// would leave the group empty — Alpine's <c>is_sole_moving_group_member</c> guard).</summary>
    private bool IsSoleMovingGroupMember(int uid)
    {
        foreach (RflSection s in Rfl.Sections)
        {
            if (s.Content is GroupsSection gs)
            {
                foreach (Group g in gs.Groups)
                {
                    if (g.IsMoving != 0 && g.Objects.Contains(uid) && g.Objects.Count + g.Brushes.Count == 1)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // ---- Property edits -------------------------------------------------------

    /// <summary>
    /// Executes a reversible edit that sets a value through <paramref name="apply"/>
    /// and restores <paramref name="oldValue"/> on undo, dirtying
    /// <paramref name="section"/>. <paramref name="coalesceKey"/> merges rapid
    /// edits (a slider drag) into one undo entry.
    /// </summary>
    public void EditValue<T>(
        RflSection section, string description, T oldValue, T newValue, Action<T> apply, string? coalesceKey = null)
    {
        Undo.Execute(new RelayCommand(description,
            () =>
            {
                apply(newValue);
                section.Dirty = true;
            },
            () =>
            {
                apply(oldValue);
                section.Dirty = true;
            },
            coalesceKey));
    }

    // ---- Save -----------------------------------------------------------------

    /// <summary>
    /// True when the level cannot be saved: it was written by a newer Alpine Faction than Glacier
    /// knows (RFL version above 306). It is open read-only; every save path refuses it.
    /// </summary>
    public bool IsSaveBlocked => Rfl.IsSaveBlocked;

    /// <summary>The user-facing reason saving is refused, or null when the level can be saved.</summary>
    public string? SaveBlockedReason => Rfl.SaveBlockedReason;

    /// <summary>
    /// The save-log lines the last <see cref="Save"/> / <see cref="SaveAs"/> produced (the v306 upgrade's
    /// fog-near-clip reset and Looping notice, a pre-release props rewrite); empty for most saves.
    /// </summary>
    public IReadOnlyList<string> LastSaveNotes { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Serializes the document to <paramref name="path"/> as an Alpine v306 file
    /// (GED's format policy — <see cref="RflFile.UpgradeToAlpine"/>). A real save
    /// updates the timestamp; a loaded pre-306 level is upgraded in place so the
    /// on-disk file, and the in-memory document from here on, are v306.
    /// </summary>
    /// <exception cref="LevelSaveBlockedException">The level is newer than Glacier writes (<see cref="IsSaveBlocked"/>).</exception>
    public void Save(string path, bool updateTimestamp = true)
    {
        ThrowIfSaveBlocked();
        string? orientationNote = RepairBrushOrientation();
        SyncStaleMoverCopies();
        Editing.AlpineGeoableState.ReconcileTableFromBrushFlags(Rfl);
        Editing.MovingGroupMirror.Reconcile(Rfl);
        Editing.LevelInfoReconciler.ReconcileHasMovers(Rfl);
        bool upgraded = Rfl.Header.Version != RflFile.AlpineSaveVersion;
        LastSaveNotes = WithNote(WithNote(Rfl.UpgradeToAlpine(), orientationNote), SurfaceCapNote());
        Rfl.Save(path, updateTimestamp);
        Path = path;
        NoteStalePackfile(path);
        if (upgraded)
        {
            // The header version (and, for v180 sources, the geometry sections)
            // changed; rebuild object handles against the upgraded model.
            RefreshObjects();
        }

        MarkSaved();
    }

    /// <summary>
    /// Serializes to bytes (used by autosave / tests). Pure: it does NOT refresh stale mover copies
    /// (a caller writing a file the game or a later session will read calls
    /// <see cref="SyncStaleMoverCopies"/> first), so it stays usable as a state fingerprint and from
    /// the crash path. Like every save it mirrors the movers into the editor groups section
    /// (<see cref="Editing.MovingGroupMirror.Reconcile"/>), so the bytes are what RED needs to see them.
    /// </summary>
    public byte[] SaveToBytes(bool updateTimestamp = false)
    {
        Editing.AlpineGeoableState.ReconcileTableFromBrushFlags(Rfl);
        Editing.MovingGroupMirror.Reconcile(Rfl);
        Editing.LevelInfoReconciler.ReconcileHasMovers(Rfl);
        return Rfl.Save(updateTimestamp);
    }

    /// <summary>
    /// Compatibility analysis for the given target (not a save gate — GED always
    /// saves Alpine v306). Alpine always passes; the stock target enumerates the
    /// Alpine-only features that would not survive on stock RF. Retained as
    /// infrastructure for scripting; the Level Properties dialog no longer surfaces it.
    /// </summary>
    public Editing.FeatureGateReport EvaluateSaveTarget(Editing.SaveTarget target) =>
        Editing.FeatureGate.Evaluate(Rfl, target);

    /// <summary>
    /// Saves the document to a new <paramref name="path"/> as an Alpine v306 file.
    /// Identical policy to <see cref="Save(string, bool)"/> — GED writes v306 always
    /// (<see cref="RflFile.UpgradeToAlpine"/>); a loaded pre-306 level is upgraded in
    /// place and the in-memory document becomes v306.
    /// </summary>
    /// <exception cref="LevelSaveBlockedException">The level is newer than Glacier writes (<see cref="IsSaveBlocked"/>).</exception>
    public void SaveAs(string path, bool updateTimestamp = true)
    {
        ThrowIfSaveBlocked();
        string? orientationNote = RepairBrushOrientation();
        SyncStaleMoverCopies();
        Editing.AlpineGeoableState.ReconcileTableFromBrushFlags(Rfl);
        Editing.MovingGroupMirror.Reconcile(Rfl);
        Editing.LevelInfoReconciler.ReconcileHasMovers(Rfl);
        LastSaveNotes = WithNote(WithNote(Rfl.UpgradeToAlpine(), orientationNote), SurfaceCapNote());
        Rfl.Save(path, updateTimestamp);
        Path = path;
        NoteStalePackfile(path);
        RefreshObjects();
        MarkSaved();
    }

    /// <summary>
    /// The save safety net (<see cref="Editing.BrushOrientation.RepairLevel"/>): an air brush stored with its
    /// faces wound outward — written by an older Glacier or another tool; RED never stores one and would
    /// rebuild it inside-out — is re-stored inward before the write. Runs before the mover-copy sync so a
    /// repaired mover member's copy is refreshed too. RED-made levels are never changed (byte-identical
    /// saves). Returns the save-log line, or null when nothing needed repair.
    /// </summary>
    private string? RepairBrushOrientation()
    {
        IReadOnlyList<int> repaired = Editing.BrushOrientation.RepairLevel(Rfl);
        return Editing.BrushOrientation.RepairNote(repaired);
    }

    /// <summary>
    /// The stale-packfile note of the last <see cref="Save"/> / <see cref="SaveAs"/> (also in <see cref="LastSaveNotes"/>):
    /// a packfile in the level's folder that still carries an older copy of the level, which the game would load instead
    /// (<see cref="Packaging.StalePackfileCheck"/>). Null when there is none.
    /// </summary>
    public string? LastStalePackfileNote { get; private set; }

    private void NoteStalePackfile(string path)
    {
        LastStalePackfileNote = Packaging.StalePackfileCheck.NoteFor(path);
        LastSaveNotes = WithNote(LastSaveNotes, LastStalePackfileNote);
    }

    /// <summary>
    /// The save-log warning for a level whose compiled geometry reached the lightmap surface cap (lit faces left with no
    /// surface), or null (see <see cref="Compiler.SurfaceBudgetState"/>).
    /// </summary>
    private string? SurfaceCapNote()
    {
        Geometry? g = null;
        foreach (RflSection s in Rfl.Sections)
        {
            if (s.Content is IO.Rfl.Sections.GeometrySection geo)
            {
                g = geo.Geometry;
                break;
            }
        }

        return Compiler.SurfaceBudgetState.FromGeometry(g).Message();
    }

    private static IReadOnlyList<string> WithNote(IReadOnlyList<string> notes, string? note) =>
        note is null ? notes : new List<string>(notes) { note };

    private void ThrowIfSaveBlocked()
    {
        if (SaveBlockedReason is { } reason)
        {
            throw new LevelSaveBlockedException(reason);
        }
    }

    /// <summary>Records the current undo position as the clean baseline.</summary>
    public void MarkSaved()
    {
        _savedPosition = Undo.Position;
        _externalDirty = false;
        DirtyChanged?.Invoke();
    }
}
