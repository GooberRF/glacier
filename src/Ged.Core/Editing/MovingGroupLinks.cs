using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The link edges implied by a moving group's structure (RFL <c>moving_group_data</c>):
/// each member mover/object links to the group's starting keyframe, and the keyframes
/// chain in sequence order. These relationships are not stored as an object <c>Links</c>
/// array — they are derived from the group's keyframe list, member transforms and
/// <see cref="MovingGroupData.StartingKeyframe"/> — so both the link-graph panel and the
/// viewport link overlay resolve them through this one helper (identical edges in both).
/// Every edge is directed origin → destination (a member → its start keyframe; a keyframe
/// → the next keyframe), so an arrowhead at the destination end reads the motion order.
/// </summary>
public static class MovingGroupLinks
{
    /// <summary>Yields the directed (from-UID → to-UID) edges for every moving group.</summary>
    public static IEnumerable<(int From, int To)> Edges(IEnumerable<Group> groups)
    {
        if (groups is null)
        {
            yield break;
        }

        foreach (Group g in groups)
        {
            if (g.IsMoving == 0 || g.MovingData is not { } data || data.Keyframes.Count == 0)
            {
                continue;
            }

            int startIndex = data.StartingKeyframe;
            if (startIndex < 0 || startIndex >= data.Keyframes.Count)
            {
                startIndex = 0;
            }

            int startUid = data.Keyframes[startIndex].Uid;

            // Each member (mover brush or moved object) → the start keyframe. Prefer the
            // member-transform list (the authoritative member set); fall back to the group's
            // brush + object UID lists when it is empty.
            var seenMembers = new HashSet<int>();
            if (data.MemberTransforms.Count > 0)
            {
                foreach (MovingGroupMemberTransform m in data.MemberTransforms)
                {
                    if (m.Uid != startUid && seenMembers.Add(m.Uid))
                    {
                        yield return (m.Uid, startUid);
                    }
                }
            }
            else
            {
                foreach (int uid in g.Brushes)
                {
                    if (uid != startUid && seenMembers.Add(uid))
                    {
                        yield return (uid, startUid);
                    }
                }

                foreach (int uid in g.Objects)
                {
                    if (uid != startUid && seenMembers.Add(uid))
                    {
                        yield return (uid, startUid);
                    }
                }
            }

            // Keyframe sequence: keyframe[i] → keyframe[i+1] (the path order).
            for (int i = 0; i < data.Keyframes.Count - 1; i++)
            {
                int a = data.Keyframes[i].Uid;
                int b = data.Keyframes[i + 1].Uid;
                if (a != b)
                {
                    yield return (a, b);
                }
            }
        }
    }
}
