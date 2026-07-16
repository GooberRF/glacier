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
        Undo.Changed += () => DirtyChanged?.Invoke();
        RefreshObjects();

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
        int max = 0;
        foreach (LevelObject o in _objects)
        {
            _byUid[o.Uid] = o;
            max = Math.Max(max, o.Uid);
        }

        // Brushes are not projected as objects but still consume UIDs.
        foreach (RflSection section in Rfl.Sections)
        {
            if (section.Content is BrushesSection bs)
            {
                foreach (var b in bs.Brushes)
                {
                    max = Math.Max(max, b.Uid);
                }
            }
        }

        _nextUid = Math.Max(_nextUid, max + 1);
        _selection.RemoveWhere(o => !_byUid.TryGetValue(o.Uid, out LevelObject? cur) || !ReferenceEquals(cur, o));
        ObjectsChanged?.Invoke();
    }

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

    /// <summary>Stock <c>Q</c>: locks the selected objects (editor-session state).</summary>
    public void LockSelected()
    {
        foreach (LevelObject o in _selection)
        {
            _locked.Add(o.Uid);
        }

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
        }

        if (added.Count == 0)
        {
            return Array.Empty<int>();
        }

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

                RefreshObjects();
            },
            () =>
            {
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

        _selection.Clear();
        SelectionChanged?.Invoke();

        Undo.Execute(new RelayCommand($"{description} {captured.Length} object(s)",
            () =>
            {
                foreach (var (list, model, _, section) in captured)
                {
                    list.Remove(model);
                    section.Dirty = true;
                }

                RefreshObjects();
            },
            () =>
            {
                foreach (var (list, model, index, section) in captured.Reverse())
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
    public LevelObject? PlaceObject(LevelObjectKind kind, Vec3 pos, string? className = null)
    {
        int uid = AllocateUid();
        ObjectBlueprint bp = ObjectFactory.Build(kind, uid, pos, className);
        return PlaceBlueprint(bp, $"Place {kind}");
    }

    /// <summary>Places a pre-built blueprint (used by the palette and the round-trip harness).</summary>
    public LevelObject? PlaceBlueprint(ObjectBlueprint bp, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(bp);
        RflSection section = Rfl.GetOrCreateSection(bp.Section, bp.CreateSection);
        IRflSectionContent content = section.Content!;
        Undo.Execute(new RelayCommand(description ?? $"Place {bp.Kind}",
            () =>
            {
                bp.Append(content);
                section.Dirty = true;
                RefreshObjects();
            },
            () =>
            {
                bp.Remove(content);
                section.Dirty = true;
                RefreshObjects();
            }));

        return FindByUid(bp.Uid);
    }

    /// <summary>Places a new event of the given schema at <paramref name="pos"/> (undo-able).</summary>
    public LevelObject? PlaceEvent(EventSchema schema, Vec3 pos, bool sampleValues = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        int uid = AllocateUid();
        RflEvent ev = sampleValues
            ? EventFactory.CreateSample(schema, uid, pos, Rfl.Header.Version)
            : EventFactory.Create(schema, uid, pos, Rfl.Header.Version);

        RflSection section = Rfl.GetOrCreateSection(SectionType.Events, () => new EventsSection());
        var events = (EventsSection)section.Content!;
        Undo.Execute(new RelayCommand($"Place {schema.ClassName}",
            () =>
            {
                events.Events.Add(ev);
                section.Dirty = true;
                RefreshObjects();
            },
            () =>
            {
                events.Events.Remove(ev);
                section.Dirty = true;
                RefreshObjects();
            }));

        return FindByUid(uid);
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

                foreach (var (list, model, index, section) in captured.Reverse())
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
    /// Serializes the document to <paramref name="path"/> as an Alpine v305 file
    /// (GED's format policy — <see cref="RflFile.UpgradeToAlpine"/>). A real save
    /// updates the timestamp; a loaded pre-305 level is upgraded in place so the
    /// on-disk file, and the in-memory document from here on, are v305.
    /// </summary>
    public void Save(string path, bool updateTimestamp = true)
    {
        Editing.AlpineGeoableState.ReconcileTableFromBrushFlags(Rfl);
        bool upgraded = Rfl.Header.Version != RflFile.AlpineSaveVersion;
        Rfl.UpgradeToAlpine();
        Rfl.Save(path, updateTimestamp);
        Path = path;
        if (upgraded)
        {
            // The header version (and, for v180 sources, the geometry sections)
            // changed; rebuild object handles against the upgraded model.
            RefreshObjects();
        }

        MarkSaved();
    }

    /// <summary>Serializes to bytes (used by autosave / tests).</summary>
    public byte[] SaveToBytes(bool updateTimestamp = false)
    {
        Editing.AlpineGeoableState.ReconcileTableFromBrushFlags(Rfl);
        return Rfl.Save(updateTimestamp);
    }

    /// <summary>
    /// Compatibility analysis for the given target (not a save gate — GED always
    /// saves Alpine v305). Alpine always passes; the stock target enumerates the
    /// Alpine-only features that would not survive on stock RF. Retained as
    /// infrastructure (scripting + reserved for future &gt;305 version gating); the
    /// Level Properties dialog no longer surfaces it.
    /// </summary>
    public Editing.FeatureGateReport EvaluateSaveTarget(Editing.SaveTarget target) =>
        Editing.FeatureGate.Evaluate(Rfl, target);

    /// <summary>
    /// Saves the document to a new <paramref name="path"/> as an Alpine v305 file.
    /// Identical policy to <see cref="Save(string, bool)"/> — GED writes v305 always
    /// (<see cref="RflFile.UpgradeToAlpine"/>); a loaded pre-305 level is upgraded in
    /// place and the in-memory document becomes v305.
    /// </summary>
    public void SaveAs(string path, bool updateTimestamp = true)
    {
        Editing.AlpineGeoableState.ReconcileTableFromBrushFlags(Rfl);
        Rfl.UpgradeToAlpine();
        Rfl.Save(path, updateTimestamp);
        Path = path;
        RefreshObjects();
        MarkSaved();
    }

    /// <summary>Records the current undo position as the clean baseline.</summary>
    public void MarkSaved()
    {
        _savedPosition = Undo.Position;
        _externalDirty = false;
        DirtyChanged?.Invoke();
    }
}
