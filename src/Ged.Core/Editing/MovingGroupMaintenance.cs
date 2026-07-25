using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Keeps the group / moving-group model coherent when members are deleted — the fix for the
/// tester's "orphan empty moving group", "0-keyframe mover", and "group points at a dead brush
/// UID" reports. After objects or brushes are removed from a level, this pass:
/// <list type="bullet">
/// <item>removes any <c>movers</c>-section copy whose UID was just deleted;</item>
/// <item>scrubs each group's brush/object member lists and moving-group member transforms of the
/// deleted UIDs, clamping the starting-keyframe index;</item>
/// <item>prunes groups that have become empty — a user group with no members, and a moving group
/// with no keyframes OR no members (which also removes its members' <c>movers</c>-section copies,
/// dissolving the mover back to static so nothing orphans).</item>
/// </list>
/// The mutation is captured in a <see cref="Snapshot"/> so the caller can fold it into the same
/// undo entry as the delete. A level with no groups/movers, or a delete that touches none of them,
/// is a no-op (returns null) and leaves every section byte-identical.
/// </summary>
public static class MovingGroupMaintenance
{
    /// <summary>Reversible record of the sections/groups a single maintenance pass mutated.</summary>
    public sealed class Snapshot
    {
        internal List<Brush>? MoversList;
        internal List<Brush>? MoversBefore;
        internal RflSection? MoversHost;

        internal List<Group>? MovingGroupsList;
        internal List<Group>? MovingGroupsBefore;
        internal RflSection? MovingGroupsHost;

        internal List<Group>? UserGroupsList;
        internal List<Group>? UserGroupsBefore;
        internal RflSection? UserGroupsHost;

        internal readonly List<GroupState> Groups = new();
    }

    internal readonly struct GroupState
    {
        public GroupState(Group group, List<int> brushes, List<int> objects, List<MovingGroupMemberTransform>? transforms, int startingKeyframe)
        {
            Group = group;
            Brushes = brushes;
            Objects = objects;
            Transforms = transforms;
            StartingKeyframe = startingKeyframe;
        }

        public Group Group { get; }

        public List<int> Brushes { get; }

        public List<int> Objects { get; }

        public List<MovingGroupMemberTransform>? Transforms { get; }

        public int StartingKeyframe { get; }
    }

    /// <summary>
    /// Applies the scrub + prune for the just-deleted UIDs. Returns a <see cref="Snapshot"/> when
    /// anything changed (the sections it touched are marked dirty), or null when nothing was affected.
    /// </summary>
    public static Snapshot? ApplyMemberDeletion(RflFile rfl, IReadOnlyCollection<int> deletedUids)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(deletedUids);
        if (deletedUids.Count == 0)
        {
            return null;
        }

        rfl.ParseAllKnownSections();
        var del = deletedUids as HashSet<int> ?? new HashSet<int>(deletedUids);

        (List<Brush>? movers, RflSection? moversHost) = FindMovers(rfl);
        (List<Group>? movingGroups, RflSection? movingHost) = FindGroups(rfl, SectionType.MovingGroups);
        (List<Group>? userGroups, RflSection? userHost) = FindGroups(rfl, SectionType.Groups);

        if (movers is null && movingGroups is null && userGroups is null)
        {
            return null;
        }

        var snap = new Snapshot
        {
            MoversList = movers,
            MoversBefore = movers is null ? null : new List<Brush>(movers),
            MoversHost = moversHost,
            MovingGroupsList = movingGroups,
            MovingGroupsBefore = movingGroups is null ? null : new List<Group>(movingGroups),
            MovingGroupsHost = movingHost,
            UserGroupsList = userGroups,
            UserGroupsBefore = userGroups is null ? null : new List<Group>(userGroups),
            UserGroupsHost = userHost,
        };

        foreach (Group g in AllGroups(movingGroups, userGroups))
        {
            snap.Groups.Add(new GroupState(
                g,
                new List<int>(g.Brushes),
                new List<int>(g.Objects),
                g.MovingData is { } d ? new List<MovingGroupMemberTransform>(d.MemberTransforms) : null,
                g.MovingData?.StartingKeyframe ?? 0));
        }

        bool changed = false;

        // 1) A deleted brush's movers-section copy goes with it.
        if (movers is not null && movers.RemoveAll(m => del.Contains(m.Uid)) > 0)
        {
            changed = true;
        }

        // 2) Scrub every group's membership + moving-group member transforms of the deleted UIDs.
        foreach (Group g in AllGroups(movingGroups, userGroups))
        {
            if (g.Brushes.RemoveAll(del.Contains) > 0)
            {
                changed = true;
            }

            if (g.Objects.RemoveAll(del.Contains) > 0)
            {
                changed = true;
            }

            if (g.MovingData is { } data)
            {
                if (data.MemberTransforms.RemoveAll(t => del.Contains(t.Uid)) > 0)
                {
                    changed = true;
                }

                int clamped = data.Keyframes.Count == 0 ? 0 : Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1);
                if (clamped != data.StartingKeyframe)
                {
                    data.StartingKeyframe = clamped;
                    changed = true;
                }
            }
        }

        // 3) Prune emptied groups. A moving group with no keyframes OR no members is dissolved back to
        //    static (its members' movers-section copies removed); a user group with no members is dropped.
        changed |= PruneGroups(movingGroups, movers, moving: true);
        changed |= PruneGroups(userGroups, movers, moving: false);

        if (!changed)
        {
            return null;
        }

        MarkDirty(moversHost, movingHost, userHost);
        return snap;
    }

    /// <summary>Reverses a prior <see cref="ApplyMemberDeletion"/> (called from the delete's undo).</summary>
    public static void Revert(RflFile rfl, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.MoversList is { } movers && snapshot.MoversBefore is { } moversBefore)
        {
            movers.Clear();
            movers.AddRange(moversBefore);
        }

        if (snapshot.MovingGroupsList is { } mg && snapshot.MovingGroupsBefore is { } mgBefore)
        {
            mg.Clear();
            mg.AddRange(mgBefore);
        }

        if (snapshot.UserGroupsList is { } ug && snapshot.UserGroupsBefore is { } ugBefore)
        {
            ug.Clear();
            ug.AddRange(ugBefore);
        }

        foreach (GroupState s in snapshot.Groups)
        {
            s.Group.Brushes.Clear();
            s.Group.Brushes.AddRange(s.Brushes);
            s.Group.Objects.Clear();
            s.Group.Objects.AddRange(s.Objects);
            if (s.Group.MovingData is { } data && s.Transforms is not null)
            {
                data.MemberTransforms.Clear();
                data.MemberTransforms.AddRange(s.Transforms);
                data.StartingKeyframe = s.StartingKeyframe;
            }
        }

        MarkDirty(snapshot.MoversHost, snapshot.MovingGroupsHost, snapshot.UserGroupsHost);
    }

    private static bool PruneGroups(List<Group>? groups, List<Brush>? movers, bool moving)
    {
        if (groups is null)
        {
            return false;
        }

        bool changed = false;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            Group g = groups[i];
            bool empty = g.Brushes.Count == 0 && g.Objects.Count == 0;
            bool noKeyframes = moving && (g.MovingData is null || g.MovingData.Keyframes.Count == 0);
            if (!(empty || noKeyframes))
            {
                continue;
            }

            // Dissolving a moving group returns its remaining member brushes to static — their
            // movers-section copies must go so nothing is left animating a phantom.
            if (moving && movers is not null && g.Brushes.Count > 0)
            {
                var memberUids = new HashSet<int>(g.Brushes);
                movers.RemoveAll(m => memberUids.Contains(m.Uid));
            }

            groups.RemoveAt(i);
            changed = true;
        }

        return changed;
    }

    private static IEnumerable<Group> AllGroups(List<Group>? moving, List<Group>? user)
    {
        if (moving is not null)
        {
            foreach (Group g in moving)
            {
                yield return g;
            }
        }

        if (user is not null)
        {
            foreach (Group g in user)
            {
                yield return g;
            }
        }
    }

    private static (List<Brush>? List, RflSection? Host) FindMovers(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Movers && s.Content is MoversSection m)
            {
                return (m.Movers, s);
            }
        }

        return (null, null);
    }

    private static (List<Group>? List, RflSection? Host) FindGroups(RflFile rfl, SectionType type)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)type && s.Content is GroupsSection g)
            {
                return (g.Groups, s);
            }
        }

        return (null, null);
    }

    private static void MarkDirty(params RflSection?[] sections)
    {
        foreach (RflSection? s in sections)
        {
            if (s is not null)
            {
                s.Dirty = true;
            }
        }
    }
}
