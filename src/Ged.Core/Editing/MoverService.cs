using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Undo-safe editing of moving groups (RFL <c>moving_groups</c> 0x3000) and their
/// mover brushes (RFL <c>movers</c> 0x2000): build a mover from selected brushes
/// and objects, place / move / delete keyframes, edit keyframe properties, set the
/// movement type / door / sound fields, and the Alpine <c>Hold Open</c> flag that
/// persists first-keyframe UIDs into <c>alpine_level_properties</c>. Pure of UI.
/// </summary>
public sealed class MoverService
{
    private readonly EditorDocument _doc;

    public MoverService(EditorDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    /// <summary>Every moving group in the level, in file order.</summary>
    public IReadOnlyList<Group> Movers => MovingGroupsContent()?.Groups ?? (IReadOnlyList<Group>)Array.Empty<Group>();

    /// <summary>The mover brushes (movers section), in file order.</summary>
    public IReadOnlyList<Brush> MoverBrushes => MoversContent()?.Movers ?? (IReadOnlyList<Brush>)Array.Empty<Brush>();

    /// <summary>The moving group that lists <paramref name="memberUid"/> as a brush or object member, if any.</summary>
    public Group? FindGroupForMember(int memberUid) =>
        Movers.FirstOrDefault(g => g.Brushes.Contains(memberUid) || g.Objects.Contains(memberUid));

    /// <summary>The keyframe with the given UID across all movers, plus its owning group.</summary>
    public (Group Group, Keyframe Keyframe)? FindKeyframe(int keyframeUid)
    {
        foreach (Group g in Movers)
        {
            Keyframe? k = g.MovingData?.Keyframes.FirstOrDefault(kf => kf.Uid == keyframeUid);
            if (k is not null)
            {
                return (g, k);
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a moving group from the given brush UIDs (moved out of the static
    /// <c>brushes</c> section into <c>movers</c>) and object UIDs. Seeds a single
    /// gold start keyframe at the members' centroid. Returns the new group.
    /// </summary>
    public Group CreateMover(IReadOnlyCollection<int> brushUids, IReadOnlyCollection<int> objectUids, string name = "Mover")
    {
        ArgumentNullException.ThrowIfNull(brushUids);
        ArgumentNullException.ThrowIfNull(objectUids);

        BrushesSection? brushes = BrushesContent();
        (MoversSection movers, RflSection moversHost) = EnsureMovers();
        (GroupsSection groups, RflSection groupsHost) = EnsureMovingGroups();

        // The brushes that become movers (removed from static geometry).
        var movingBrushes = brushes?.Brushes.Where(b => brushUids.Contains(b.Uid)).ToList() ?? new List<Brush>();

        var group = new Group { Name = name, IsMoving = 1, MovingData = new MovingGroupData() };
        var memberTransforms = new List<MovingGroupMemberTransform>();
        Vec3 sum = Vec3.Zero;
        int count = 0;
        foreach (Brush b in movingBrushes)
        {
            group.Brushes.Add(b.Uid);
            memberTransforms.Add(new MovingGroupMemberTransform { Uid = b.Uid, Position = b.Position, Rotation = b.Rotation });
            sum = sum.Add(b.Position);
            count++;
        }

        foreach (int uid in objectUids)
        {
            LevelObject? o = _doc.FindByUid(uid);
            if (o is null)
            {
                continue;
            }

            group.Objects.Add(uid);
            memberTransforms.Add(new MovingGroupMemberTransform { Uid = uid, Position = o.Position, Rotation = Mat3.Identity });
            sum = sum.Add(o.Position);
            count++;
        }

        group.MovingData.MemberTransforms = memberTransforms;

        Vec3 origin = count > 0 ? sum.Scale(1f / count) : Vec3.Zero;
        int startKeyframeUid = _doc.AllocateUid();
        group.MovingData.Keyframes.Add(new Keyframe { Uid = startKeyframeUid, Position = origin, Rotation = Mat3.Identity });
        group.MovingData.StartingKeyframe = 0;
        group.MovingData.MovementType = 1; // one_way

        _doc.Undo.Execute(new RelayCommand($"Create mover \"{name}\"",
            () =>
            {
                EnsureSectionPresent(moversHost);
                EnsureSectionPresent(groupsHost);
                foreach (Brush b in movingBrushes)
                {
                    brushes?.Brushes.Remove(b);
                    if (!movers.Movers.Contains(b))
                    {
                        movers.Movers.Add(b);
                    }
                }

                groups.Groups.Add(group);
                MarkDirty(moversHost, groupsHost);
                MarkBrushesDirty();
                _doc.RefreshObjects();
            },
            () =>
            {
                groups.Groups.Remove(group);
                foreach (Brush b in movingBrushes)
                {
                    movers.Movers.Remove(b);
                    brushes?.Brushes.Add(b);
                }

                MarkDirty(moversHost, groupsHost);
                MarkBrushesDirty();
                _doc.RefreshObjects();
            }));

        return group;
    }

    /// <summary>Appends a keyframe (a position/orientation waypoint) to a mover.</summary>
    public Keyframe AddKeyframe(Group group, Vec3 position, Mat3 rotation)
    {
        ArgumentNullException.ThrowIfNull(group);
        MovingGroupData data = group.MovingData ??= new MovingGroupData();
        var keyframe = new Keyframe { Uid = _doc.AllocateUid(), Position = position, Rotation = rotation };
        RflSection host = MovingGroupsHost();
        _doc.Undo.Execute(new RelayCommand("Add keyframe",
            () => { data.Keyframes.Add(keyframe); host.Dirty = true; _doc.NotifyLinksChanged(); },
            () => { data.Keyframes.Remove(keyframe); host.Dirty = true; _doc.NotifyLinksChanged(); }));
        return keyframe;
    }

    /// <summary>Removes a keyframe from a mover (keeping at least the start keyframe is the caller's job).</summary>
    public void RemoveKeyframe(Group group, Keyframe keyframe)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(keyframe);
        MovingGroupData? data = group.MovingData;
        if (data is null)
        {
            return;
        }

        int index = data.Keyframes.IndexOf(keyframe);
        if (index < 0)
        {
            return;
        }

        RflSection host = MovingGroupsHost();
        _doc.Undo.Execute(new RelayCommand("Remove keyframe",
            () => { data.Keyframes.Remove(keyframe); host.Dirty = true; _doc.NotifyLinksChanged(); },
            () => { data.Keyframes.Insert(Math.Clamp(index, 0, data.Keyframes.Count), keyframe); host.Dirty = true; _doc.NotifyLinksChanged(); }));
    }

    /// <summary>Applies a reversible edit to a keyframe's fields (used by the Keyframe Properties inspector).</summary>
    public void EditKeyframe(Keyframe keyframe, string description, Action<Keyframe> apply, Action<Keyframe> revert, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        RflSection host = MovingGroupsHost();
        _doc.Undo.Execute(new RelayCommand(description,
            () => { apply(keyframe); host.Dirty = true; },
            () => { revert(keyframe); host.Dirty = true; },
            coalesceKey));
    }

    /// <summary>Applies a reversible edit to a mover's motion fields (movement type, door, sounds).</summary>
    public void EditMover(MovingGroupData data, string description, Action<MovingGroupData> apply, Action<MovingGroupData> revert)
    {
        ArgumentNullException.ThrowIfNull(data);
        RflSection host = MovingGroupsHost();
        _doc.Undo.Execute(new RelayCommand(description,
            () => { apply(data); host.Dirty = true; },
            () => { revert(data); host.Dirty = true; }));
    }

    /// <summary>
    /// Alpine <c>Hold Open</c>: toggles whether a mover's first keyframe UID is
    /// persisted in <c>alpine_level_properties</c> hold_open list. Creating the
    /// section on demand (chunk version 4).
    /// </summary>
    public bool IsHoldOpen(Group group)
    {
        int? firstUid = group.MovingData?.Keyframes.FirstOrDefault()?.Uid;
        return firstUid is int uid && AlpineLevelPropsContent()?.HoldOpenKeyframeUids.Contains(uid) == true;
    }

    public void SetHoldOpen(Group group, bool holdOpen)
    {
        ArgumentNullException.ThrowIfNull(group);
        Keyframe? first = group.MovingData?.Keyframes.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        (AlpineLevelPropertiesSection alp, RflSection host) = EnsureAlpineLevelProps();
        int uid = first.Uid;
        bool has = alp.HoldOpenKeyframeUids.Contains(uid);
        if (has == holdOpen)
        {
            return;
        }

        _doc.Undo.Execute(new RelayCommand(holdOpen ? "Enable Hold Open" : "Disable Hold Open",
            () =>
            {
                EnsureSectionPresent(host);
                if (holdOpen)
                {
                    alp.HoldOpenKeyframeUids.Add(uid);
                }
                else
                {
                    alp.HoldOpenKeyframeUids.Remove(uid);
                }

                host.Dirty = true;
            },
            () =>
            {
                if (holdOpen)
                {
                    alp.HoldOpenKeyframeUids.Remove(uid);
                }
                else
                {
                    alp.HoldOpenKeyframeUids.Add(uid);
                }

                host.Dirty = true;
            }));
    }

    // ---- section plumbing -----------------------------------------------------

    private BrushesSection? BrushesContent() => FindContent<BrushesSection>();

    private MoversSection? MoversContent() => FindContent<MoversSection>();

    private GroupsSection? MovingGroupsContent()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.MovingGroups && s.Content is GroupsSection g)
            {
                return g;
            }
        }

        return null;
    }

    private AlpineLevelPropertiesSection? AlpineLevelPropsContent() => FindContent<AlpineLevelPropertiesSection>();

    private RflSection MovingGroupsHost()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.MovingGroups)
            {
                return s;
            }
        }

        return EnsureMovingGroups().Host;
    }

    private (MoversSection Content, RflSection Host) EnsureMovers()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.Movers, () => new MoversSection());
        return ((MoversSection)host.Content!, host);
    }

    private (GroupsSection Content, RflSection Host) EnsureMovingGroups()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.MovingGroups, () => new GroupsSection(SectionType.MovingGroups));
        return ((GroupsSection)host.Content!, host);
    }

    private (AlpineLevelPropertiesSection Content, RflSection Host) EnsureAlpineLevelProps()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.AlpineLevelProperties,
            () => new AlpineLevelPropertiesSection { Version = 4 });
        var alp = (AlpineLevelPropertiesSection)host.Content!;
        if (alp.Version < 4)
        {
            alp.Version = 4;
        }

        return (alp, host);
    }

    private void EnsureSectionPresent(RflSection host)
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

    private void MarkBrushesDirty()
    {
        if (FindHost<BrushesSection>() is { } h)
        {
            h.Dirty = true;
        }
    }

    private static void MarkDirty(params RflSection[] sections)
    {
        foreach (RflSection s in sections)
        {
            s.Dirty = true;
        }
    }

    private T? FindContent<T>()
        where T : class, IRflSectionContent
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }

    private RflSection? FindHost<T>()
        where T : class, IRflSectionContent
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is T)
            {
                return s;
            }
        }

        return null;
    }
}
