using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Undo-safe editing of user-defined groups (RFL <c>groups</c> 0x3000000): create
/// from a selection, dissolve (permanent or temporary + reconnect), add/remove
/// members, rename, lock/unlock (session), select-group, deep-duplicate (fresh
/// UIDs + remapped intra-group links), and Alpine group Mirror of the members'
/// brushes and objects together. Master groups (auto per type) are computed by the
/// caller from the object list; moving groups live in <see cref="MoverService"/>.
/// </summary>
public sealed class GroupService
{
    private readonly EditorDocument _doc;
    private readonly HashSet<Group> _locked = new();
    private readonly List<Group> _temporary = new();

    public GroupService(EditorDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    /// <summary>Every user-defined group, in file order.</summary>
    public IReadOnlyList<Group> Groups => GroupsContent()?.Groups ?? (IReadOnlyList<Group>)Array.Empty<Group>();

    /// <summary>Groups temporarily dissolved this session (restorable via <see cref="Reconnect"/>).</summary>
    public IReadOnlyList<Group> Temporary => _temporary;

    public bool IsLocked(Group group) => _locked.Contains(group);

    // ---- create / dissolve ----------------------------------------------------

    /// <summary>Creates a named user-defined group from the given brush and object UIDs.</summary>
    public Group CreateGroup(string name, IEnumerable<int> brushUids, IEnumerable<int> objectUids)
    {
        var group = new Group
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Group" : name,
            Brushes = brushUids.Distinct().ToList(),
            Objects = objectUids.Distinct().ToList(),
        };

        (GroupsSection content, RflSection host) = EnsureGroups();
        _doc.Undo.Execute(new RelayCommand($"Create group \"{group.Name}\"",
            () => { EnsurePresent(host); content.Groups.Add(group); host.Dirty = true; _doc.NotifyLinksChanged(); },
            () => { content.Groups.Remove(group); host.Dirty = true; _doc.NotifyLinksChanged(); }));
        return group;
    }

    /// <summary>Permanently dissolves a group (its members remain in the level).</summary>
    public void Dissolve(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        GroupsSection? content = GroupsContent();
        if (content is null)
        {
            return;
        }

        int index = content.Groups.IndexOf(group);
        if (index < 0)
        {
            return;
        }

        RflSection host = GroupsHost();
        _doc.Undo.Execute(new RelayCommand($"Dissolve group \"{group.Name}\"",
            () => { content.Groups.Remove(group); host.Dirty = true; _doc.NotifyLinksChanged(); },
            () => { content.Groups.Insert(Math.Clamp(index, 0, content.Groups.Count), group); host.Dirty = true; _doc.NotifyLinksChanged(); }));
    }

    /// <summary>
    /// Temporarily dissolves a group: removes it from the persisted section but
    /// stashes it so <see cref="Reconnect"/> can restore it this session. Not on
    /// the undo stack (it is a session toggle).
    /// </summary>
    public void DissolveTemporary(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        GroupsSection? content = GroupsContent();
        if (content is null || !content.Groups.Remove(group))
        {
            return;
        }

        _temporary.Add(group);
        GroupsHost().Dirty = true;
        _doc.NotifyLinksChanged();
    }

    /// <summary>Restores a temporarily dissolved group.</summary>
    public void Reconnect(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_temporary.Remove(group))
        {
            return;
        }

        (GroupsSection content, RflSection host) = EnsureGroups();
        EnsurePresent(host);
        content.Groups.Add(group);
        host.Dirty = true;
        _doc.NotifyLinksChanged();
    }

    // ---- membership / rename / lock -------------------------------------------

    public void AddMembers(Group group, IEnumerable<int> brushUids, IEnumerable<int> objectUids)
    {
        ArgumentNullException.ThrowIfNull(group);
        var addBrushes = brushUids.Where(u => !group.Brushes.Contains(u)).Distinct().ToList();
        var addObjects = objectUids.Where(u => !group.Objects.Contains(u)).Distinct().ToList();
        if (addBrushes.Count + addObjects.Count == 0)
        {
            return;
        }

        RflSection host = GroupsHost();
        _doc.Undo.Execute(new RelayCommand("Add to group",
            () => { group.Brushes.AddRange(addBrushes); group.Objects.AddRange(addObjects); host.Dirty = true; },
            () => { addBrushes.ForEach(u => group.Brushes.Remove(u)); addObjects.ForEach(u => group.Objects.Remove(u)); host.Dirty = true; }));
    }

    public void RemoveMembers(Group group, IEnumerable<int> uids)
    {
        ArgumentNullException.ThrowIfNull(group);
        var set = uids.ToHashSet();
        var removedBrushes = group.Brushes.Where(set.Contains).ToList();
        var removedObjects = group.Objects.Where(set.Contains).ToList();
        if (removedBrushes.Count + removedObjects.Count == 0)
        {
            return;
        }

        RflSection host = GroupsHost();
        _doc.Undo.Execute(new RelayCommand("Remove from group",
            () => { removedBrushes.ForEach(u => group.Brushes.Remove(u)); removedObjects.ForEach(u => group.Objects.Remove(u)); host.Dirty = true; },
            () => { group.Brushes.AddRange(removedBrushes); group.Objects.AddRange(removedObjects); host.Dirty = true; }));
    }

    public void Rename(Group group, string name)
    {
        ArgumentNullException.ThrowIfNull(group);
        string old = group.Name;
        string next = string.IsNullOrWhiteSpace(name) ? old : name;
        if (next == old)
        {
            return;
        }

        RflSection host = GroupsHost();
        _doc.Undo.Execute(new RelayCommand("Rename group",
            () => { group.Name = next; host.Dirty = true; },
            () => { group.Name = old; host.Dirty = true; }));
    }

    public void SetLocked(Group group, bool locked)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (locked)
        {
            _locked.Add(group);
        }
        else
        {
            _locked.Remove(group);
        }
    }

    /// <summary>Selects a group's member objects in the document (brushes are handled by the caller).</summary>
    public void SelectGroup(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var objects = group.Objects.Select(_doc.FindByUid).Where(o => o is not null).Select(o => o!).ToList();
        _doc.SelectMany(objects);
    }

    // ---- duplicate (deep copy + link remap) -----------------------------------

    /// <summary>
    /// Deep-duplicates a group: clones each member brush and object with a fresh
    /// UID, remaps intra-group links onto the clones (links out of the group stay
    /// pointing at the originals), and adds a new group over the clones. One undo
    /// entry. Returns the new group.
    /// </summary>
    public Group Duplicate(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _doc.Rfl.ParseAllKnownSections();
        BrushesSection? brushes = BrushesContent();
        RflSection? brushHost = BrushesHost();

        var remap = new Dictionary<int, int>();
        var brushClones = new List<Brush>();
        var objectClones = new List<(IList List, object Clone, RflSection Section)>();

        if (brushes is not null)
        {
            foreach (int uid in group.Brushes)
            {
                Brush? src = brushes.Brushes.FirstOrDefault(b => b.Uid == uid);
                if (src is null)
                {
                    continue;
                }

                Brush clone = GeometryClone.Deep(src);
                clone.Uid = _doc.AllocateUid();
                clone.State = BrushState.Normal;
                remap[uid] = clone.Uid;
                brushClones.Add(clone);
            }
        }

        foreach (int uid in group.Objects)
        {
            LevelObject? lo = _doc.FindByUid(uid);
            if (lo?.OwningList is not { } list)
            {
                continue;
            }

            object clone = lo.CloneModel();
            int newUid = _doc.AllocateUid();
            ObjectUid.Set(clone, newUid);
            remap[uid] = newUid;
            objectClones.Add((list, clone, lo.Section));
        }

        var newGroup = new Group
        {
            Name = group.Name + " copy",
            Brushes = group.Brushes.Where(remap.ContainsKey).Select(u => remap[u]).ToList(),
            Objects = group.Objects.Where(remap.ContainsKey).Select(u => remap[u]).ToList(),
        };

        // Remap intra-group links onto the clones.
        foreach (var (_, clone, _) in objectClones)
        {
            if (LinksOfModel(clone) is { } links)
            {
                for (int i = 0; i < links.Count; i++)
                {
                    if (remap.TryGetValue(links[i], out int mapped))
                    {
                        links[i] = mapped;
                    }
                }
            }
        }

        (GroupsSection groupsContent, RflSection groupsHost) = EnsureGroups();
        var dirtyObjectSections = objectClones.Select(c => c.Section).Distinct().ToList();

        _doc.Undo.Execute(new RelayCommand($"Duplicate group \"{group.Name}\"",
            () =>
            {
                if (brushHost is not null)
                {
                    foreach (Brush b in brushClones)
                    {
                        brushes!.Brushes.Add(b);
                    }

                    brushHost.Dirty = true;
                }

                foreach (var (list, clone, _) in objectClones)
                {
                    list.Add(clone);
                }

                foreach (RflSection s in dirtyObjectSections)
                {
                    s.Dirty = true;
                }

                EnsurePresent(groupsHost);
                groupsContent.Groups.Add(newGroup);
                groupsHost.Dirty = true;
                _doc.RefreshObjects();
            },
            () =>
            {
                if (brushHost is not null)
                {
                    foreach (Brush b in brushClones)
                    {
                        brushes!.Brushes.Remove(b);
                    }

                    brushHost.Dirty = true;
                }

                foreach (var (list, clone, _) in objectClones)
                {
                    list.Remove(clone);
                }

                foreach (RflSection s in dirtyObjectSections)
                {
                    s.Dirty = true;
                }

                groupsContent.Groups.Remove(newGroup);
                groupsHost.Dirty = true;
                _doc.RefreshObjects();
            }));

        return newGroup;
    }

    // ---- mirror (brushes + objects together) ----------------------------------

    /// <summary>
    /// Alpine group Mirror across world axis (0=X,1=Y,2=Z): reflects the member
    /// brushes and objects together about their shared world-centroid pivot. One
    /// undo entry; brush geometry and object pose are snapshotted for exact undo.
    /// </summary>
    public void MirrorGroup(Group group, int axis) =>
        MirrorMembers(group.Brushes, group.Objects, axis, $"Mirror group \"{group.Name}\"");

    /// <summary>Mirrors an ad-hoc brush+object selection (Group-mode Mirror on a raw selection).</summary>
    public void MirrorMembers(IEnumerable<int> brushUids, IEnumerable<int> objectUids, int axis, string description = "Mirror")
    {
        _doc.Rfl.ParseAllKnownSections();
        BrushesSection? brushes = BrushesContent();
        var brushList = brushUids
            .Select(u => brushes?.Brushes.FirstOrDefault(b => b.Uid == u))
            .Where(b => b is not null).Select(b => b!).ToList();
        var objectList = objectUids.Select(_doc.FindByUid).Where(o => o is not null).Select(o => o!).ToList();

        if (brushList.Count + objectList.Count == 0)
        {
            return;
        }

        // Shared pivot = centroid of all member world positions.
        Vec3 sum = Vec3.Zero;
        int count = 0;
        foreach (Brush b in brushList)
        {
            sum = sum.Add(BrushTransform.WorldCentroid(b));
            count++;
        }

        foreach (LevelObject o in objectList)
        {
            sum = sum.Add(o.Position);
            count++;
        }

        Vec3 pivot = count > 0 ? sum.Scale(1f / count) : Vec3.Zero;

        // Snapshot for undo.
        var brushBefore = brushList.Select(GeometryClone.Deep).ToList();
        var objectBefore = objectList.Select(o => (o, Pos: o.Position, Rot: RotationOf(o.Model))).ToList();
        RflSection? brushHost = BrushesHost();
        var objectSections = objectList.Select(o => o.Section).Distinct().ToList();

        void Apply()
        {
            foreach (Brush b in brushList)
            {
                GroupMirror.MirrorBrush(b, pivot, axis);
            }

            foreach (LevelObject o in objectList)
            {
                GroupMirror.MirrorObjectModel(o.Model, pivot, axis);
            }

            if (brushHost is not null)
            {
                brushHost.Dirty = true;
            }

            foreach (RflSection s in objectSections)
            {
                s.Dirty = true;
            }
        }

        void Revert()
        {
            for (int i = 0; i < brushList.Count; i++)
            {
                RestoreBrush(brushList[i], brushBefore[i]);
            }

            foreach (var (o, pos, rot) in objectBefore)
            {
                o.Position = pos;
                if (rot is Mat3 m)
                {
                    SetRotation(o.Model, m);
                }
            }

            if (brushHost is not null)
            {
                brushHost.Dirty = true;
            }

            foreach (RflSection s in objectSections)
            {
                s.Dirty = true;
            }
        }

        _doc.Undo.Execute(new RelayCommand(description, Apply, Revert));
    }

    // ---- helpers --------------------------------------------------------------

    private static List<int>? LinksOfModel(object m) => m switch
    {
        Trigger t => t.Links,
        RflEvent e => e.Links,
        Clutter c => c.Links,
        NavPoint n => n.Links,
        _ => null,
    };

    private static Mat3? RotationOf(object model)
    {
        System.Reflection.PropertyInfo? p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is null)
        {
            return null;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            return (Mat3)p.GetValue(model)!;
        }

        if (p.PropertyType == typeof(Mat3?) && p.GetValue(model) is Mat3 m)
        {
            return m;
        }

        return null;
    }

    private static void SetRotation(object model, Mat3 value)
    {
        System.Reflection.PropertyInfo? p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is null || !p.CanWrite)
        {
            return;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            p.SetValue(model, value);
        }
        else if (p.PropertyType == typeof(Mat3?))
        {
            p.SetValue(model, (Mat3?)value);
        }
    }

    private static void RestoreBrush(Brush target, Brush source)
    {
        target.Position = source.Position;
        target.Rotation = source.Rotation;
        target.Geometry = GeometryClone.Deep(source.Geometry);
    }

    private GroupsSection? GroupsContent()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Groups && s.Content is GroupsSection g)
            {
                return g;
            }
        }

        return null;
    }

    private RflSection GroupsHost()
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Groups)
            {
                return s;
            }
        }

        return EnsureGroups().Host;
    }

    private (GroupsSection Content, RflSection Host) EnsureGroups()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.Groups, () => new GroupsSection(SectionType.Groups));
        return ((GroupsSection)host.Content!, host);
    }

    private BrushesSection? BrushesContent()
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is BrushesSection b)
            {
                return b;
            }
        }

        return null;
    }

    private RflSection? BrushesHost()
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is BrushesSection)
            {
                return s;
            }
        }

        return null;
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
}
