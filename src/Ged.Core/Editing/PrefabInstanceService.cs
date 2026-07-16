using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Tracks placed prefab instances over an <see cref="EditorDocument"/>: it owns
/// the GED-only <c>ged_prefab_instances</c> section, records an instance at placement, detects
/// which instance a member belongs to, flags an instance "modified" when a member is edited,
/// orphans an instance (dropping only the lineage record), and propagates a prefab edit by
/// re-instantiating each non-orphaned instance — preserving its transform and its members'
/// external inbound links via the stable member-index→UID order. Pure of UI, fully testable.
/// </summary>
public sealed class PrefabInstanceService
{
    private readonly EditorDocument _doc;

    public PrefabInstanceService(EditorDocument doc) => _doc = doc ?? throw new ArgumentNullException(nameof(doc));

    /// <summary>Raised after the instance set changes (record / orphan / propagate / modified flag).</summary>
    public event Action? InstancesChanged;

    /// <summary>The lineage records (empty when the level has no instances / no section).</summary>
    public IReadOnlyList<PrefabInstanceRecord> Instances => FindSection()?.Instances ?? (IReadOnlyList<PrefabInstanceRecord>)Array.Empty<PrefabInstanceRecord>();

    public bool HasInstances => Instances.Count > 0;

    /// <summary>The instance a UID belongs to (member lookup), or null.</summary>
    public PrefabInstanceRecord? InstanceOfMember(int uid) =>
        Instances.FirstOrDefault(r => r.MemberUids.Contains(uid));

    public PrefabInstanceRecord? ById(int instanceId) => Instances.FirstOrDefault(r => r.InstanceId == instanceId);

    private GedPrefabInstancesSection? FindSection()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is GedPrefabInstancesSection g)
            {
                return g;
            }
        }

        return null;
    }

    private (GedPrefabInstancesSection Section, RflSection Host) EnsureSection()
    {
        // Only create the section on first real use, so untouched levels stay byte-identical.
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.GedPrefabInstances, () => new GedPrefabInstancesSection());
        return ((GedPrefabInstancesSection)host.Content!, host);
    }

    private int AllocateInstanceId()
    {
        int max = 0;
        foreach (PrefabInstanceRecord r in Instances)
        {
            max = Math.Max(max, r.InstanceId);
        }

        return max + 1;
    }

    /// <summary>
    /// Records a placed instance (undoable). Call inside the same undo transaction as the
    /// placement import so undo removes both the members and the lineage record together.
    /// </summary>
    public PrefabInstanceRecord RecordInstance(string prefabName, string sourceHash, IReadOnlyList<int> memberUids, Vec3 pivot, Mat3 rotation)
    {
        (GedPrefabInstancesSection section, RflSection host) = EnsureSection();
        var record = new PrefabInstanceRecord
        {
            InstanceId = AllocateInstanceId(),
            PrefabName = prefabName,
            SourceHash = sourceHash,
            MemberUids = memberUids.ToList(),
            PivotPosition = pivot,
            PivotRotation = rotation,
            Modified = false,
        };

        _doc.Undo.Execute(new RelayCommand($"Record prefab instance '{prefabName}'",
            () => { EnsurePresent(host); if (!section.Instances.Contains(record)) { section.Instances.Add(record); } host.Dirty = true; InstancesChanged?.Invoke(); },
            () => { section.Instances.Remove(record); host.Dirty = true; InstancesChanged?.Invoke(); }));
        return record;
    }

    /// <summary>
    /// Orphans an instance: removes only the lineage record; its members stay as plain
    /// independent level content (undoable).
    /// </summary>
    public bool Orphan(int instanceId)
    {
        if (FindSection() is not { } section)
        {
            return false;
        }

        PrefabInstanceRecord? record = section.Instances.FirstOrDefault(r => r.InstanceId == instanceId);
        if (record is null)
        {
            return false;
        }

        RflSection host = HostOf(section);
        int index = section.Instances.IndexOf(record);
        _doc.Undo.Execute(new RelayCommand($"Orphan prefab instance '{record.PrefabName}'",
            () => { section.Instances.Remove(record); host.Dirty = true; InstancesChanged?.Invoke(); },
            () => { section.Instances.Insert(Math.Clamp(index, 0, section.Instances.Count), record); host.Dirty = true; InstancesChanged?.Invoke(); }));
        return true;
    }

    /// <summary>
    /// Flags the instance owning <paramref name="memberUid"/> as locally modified (badge). Set
    /// directly (not through undo) since it is a derived flag; returns whether an instance was flagged.
    /// </summary>
    public bool MarkMemberModified(int memberUid)
    {
        if (FindSection() is not { } section)
        {
            return false;
        }

        PrefabInstanceRecord? record = section.Instances.FirstOrDefault(r => r.MemberUids.Contains(memberUid));
        if (record is null || record.Modified)
        {
            return record is { Modified: true };
        }

        record.Modified = true;
        HostOf(section).Dirty = true;
        InstancesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Propagates a prefab edit to every non-orphaned instance of <paramref name="prefabName"/>:
    /// each instance's members are deleted and the (updated) payload is re-imported at the
    /// instance's transform, its intra-prefab links remapped (by <see cref="RfgInterop.Import"/>)
    /// and its members' external inbound links preserved via the stable member-index→UID map.
    /// Modified instances are skipped unless <paramref name="includeModified"/> is set. One undo
    /// entry per instance. Returns the number of instances propagated.
    /// </summary>
    public int Propagate(string prefabName, RfgFile payload, string newHash, bool includeModified)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (FindSection() is not { } section)
        {
            return 0;
        }

        // Snapshot the target records (propagation mutates the section as it goes).
        var targets = section.Instances
            .Where(r => string.Equals(r.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
            .Where(r => includeModified || !r.Modified)
            .ToList();

        int done = 0;
        foreach (PrefabInstanceRecord record in targets)
        {
            Reinstantiate(record, payload, newHash);
            done++;
        }

        return done;
    }

    private void Reinstantiate(PrefabInstanceRecord record, RfgFile payload, string newHash)
    {
        using UndoStack.Transaction tx = _doc.Undo.BeginTransaction($"Propagate '{record.PrefabName}' to instance {record.InstanceId}");

        var oldMembers = record.MemberUids.ToList();
        DeleteMembers(oldMembers);

        // Re-import the payload at the instance's pivot — Import returns the placed UIDs in the
        // SAME stable order the members were originally recorded in (its enumeration is fixed).
        IReadOnlyList<int> placed = RfgInterop.Import(_doc, payload, record.PivotPosition);

        // old member i → new member i (index-stable; extra old/new members simply have no pair).
        var map = new Dictionary<int, int>();
        int pairs = Math.Min(oldMembers.Count, placed.Count);
        for (int i = 0; i < pairs; i++)
        {
            map[oldMembers[i]] = placed[i];
        }

        RemapExternalInboundLinks(map, new HashSet<int>(placed));
        UpdateRecordMembers(record, placed, newHash);

        tx.Commit();
    }

    /// <summary>Removes member brushes + objects by UID as one undoable command (within the tx).</summary>
    private void DeleteMembers(IReadOnlyList<int> uids)
    {
        var want = new HashSet<int>(uids);
        var captured = new List<(IList List, object Model, int Index, RflSection Section)>();

        // Objects.
        foreach (LevelObject o in _doc.Objects)
        {
            if (want.Contains(o.Uid) && o.OwningList is { } list && o.IndexInSection >= 0)
            {
                captured.Add((list, o.Model, o.IndexInSection, o.Section));
            }
        }

        // Brushes.
        if (FindBrushes() is (BrushesSection bs, RflSection brushHost))
        {
            for (int i = 0; i < bs.Brushes.Count; i++)
            {
                if (want.Contains(bs.Brushes[i].Uid))
                {
                    captured.Add((bs.Brushes, bs.Brushes[i], i, brushHost));
                }
            }
        }

        if (captured.Count == 0)
        {
            return;
        }

        var descending = captured.OrderByDescending(c => c.Index).ToList();
        _doc.Undo.Execute(new RelayCommand($"Delete {descending.Count} instance member(s)",
            () =>
            {
                foreach (var (list, model, _, sec) in descending)
                {
                    list.Remove(model);
                    sec.Dirty = true;
                }

                _doc.RefreshObjects();
            },
            () =>
            {
                foreach (var (list, model, index, sec) in Enumerable.Reverse(descending))
                {
                    list.Insert(Math.Clamp(index, 0, list.Count), model);
                    sec.Dirty = true;
                }

                _doc.RefreshObjects();
            }));
    }

    /// <summary>
    /// Rewrites links from level objects OUTSIDE the instance that targeted an old member UID so
    /// they point at the re-instantiated member at the same member index (undoable).
    /// </summary>
    private void RemapExternalInboundLinks(Dictionary<int, int> map, HashSet<int> newMembers)
    {
        if (map.Count == 0)
        {
            return;
        }

        var edits = new List<(List<int> Links, List<int> Before, List<int> After, RflSection Section)>();
        foreach (LevelObject o in _doc.Objects)
        {
            if (newMembers.Contains(o.Uid) || LinkModel.LinksOf(o) is not { } links)
            {
                continue; // members themselves are handled by Import's intra-import remap
            }

            var before = new List<int>(links);
            var after = new List<int>(links);
            bool changed = false;
            for (int i = 0; i < after.Count; i++)
            {
                if (map.TryGetValue(after[i], out int mapped))
                {
                    after[i] = mapped;
                    changed = true;
                }
            }

            if (changed)
            {
                edits.Add((links, before, after, o.Section));
            }
        }

        if (edits.Count == 0)
        {
            return;
        }

        _doc.Undo.Execute(new RelayCommand($"Preserve {edits.Count} external link(s) to instance",
            () => { foreach (var e in edits) { Replace(e.Links, e.After); e.Section.Dirty = true; } _doc.NotifyLinksChanged(); },
            () => { foreach (var e in edits) { Replace(e.Links, e.Before); e.Section.Dirty = true; } _doc.NotifyLinksChanged(); }));
    }

    private void UpdateRecordMembers(PrefabInstanceRecord record, IReadOnlyList<int> newMembers, string newHash)
    {
        if (FindSection() is not { } section)
        {
            return;
        }

        RflSection host = HostOf(section);
        List<int> oldMembers = record.MemberUids;
        string oldHash = record.SourceHash;
        bool oldModified = record.Modified;
        var newList = newMembers.ToList();

        _doc.Undo.Execute(new RelayCommand("Update prefab instance members",
            () => { record.MemberUids = newList; record.SourceHash = newHash; record.Modified = false; host.Dirty = true; InstancesChanged?.Invoke(); },
            () => { record.MemberUids = oldMembers; record.SourceHash = oldHash; record.Modified = oldModified; host.Dirty = true; InstancesChanged?.Invoke(); }));
    }

    // ---- plumbing -------------------------------------------------------------

    private (BrushesSection, RflSection)? FindBrushes()
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                return (bs, s);
            }
        }

        return null;
    }

    private RflSection HostOf(GedPrefabInstancesSection content)
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (ReferenceEquals(s.Content, content))
            {
                return s;
            }
        }

        throw new InvalidOperationException("Prefab-instances section content is not attached to a section.");
    }

    private void EnsurePresent(RflSection host)
    {
        if (!_doc.Rfl.Sections.Contains(host))
        {
            int endIndex = _doc.Rfl.Sections.FindIndex(s => s.IsEnd);
            if (endIndex >= 0)
            {
                _doc.Rfl.Sections.Insert(endIndex, host);
            }
            else
            {
                _doc.Rfl.Sections.Add(host);
            }
        }
    }

    private static void Replace(List<int> list, List<int> contents)
    {
        list.Clear();
        list.AddRange(contents);
    }
}
