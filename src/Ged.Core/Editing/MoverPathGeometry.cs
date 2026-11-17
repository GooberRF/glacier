using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>One segment of a mover path: from keyframe <see cref="Owner"/> to the next (or, closing, back to the first).</summary>
/// <param name="Owner">The keyframe index whose settings shape the segment (Alpine's segment owner, mover_path.h:262-275).</param>
/// <param name="FromUid">The owner keyframe's UID.</param>
/// <param name="ToUid">The UID of the keyframe the segment ends at.</param>
/// <param name="Curved">True for a cubic Bézier segment; false for a straight one.</param>
/// <param name="Closing">True for a loop's closing segment (last keyframe → first).</param>
/// <param name="P0">Segment start (the owner keyframe).</param>
/// <param name="P1">First control point (owner + its out handle); equals <paramref name="P0"/> on a straight segment.</param>
/// <param name="P2">Second control point (next keyframe + its in handle); equals <paramref name="P3"/> on a straight segment.</param>
/// <param name="P3">Segment end (the next keyframe).</param>
public readonly record struct MoverPathSegment(int Owner, int FromUid, int ToUid, bool Curved, bool Closing, Vec3 P0, Vec3 P1, Vec3 P2, Vec3 P3)
{
    /// <summary>The point at parameter <paramref name="t"/> in [0, 1] (the cubic Bézier, mover_path.h:300-311; a line when straight).</summary>
    public Vec3 Sample(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (!Curved)
        {
            return Vec3Math.Lerp(P0, P3, t);
        }

        float u = 1f - t;
        float b0 = u * u * u;
        float b1 = 3f * u * u * t;
        float b2 = 3f * u * t * t;
        float b3 = t * t * t;
        return new Vec3(
            (b0 * P0.X) + (b1 * P1.X) + (b2 * P2.X) + (b3 * P3.X),
            (b0 * P0.Y) + (b1 * P1.Y) + (b2 * P2.Y) + (b3 * P3.Y),
            (b0 * P0.Z) + (b1 * P1.Z) + (b2 * P2.Z) + (b3 * P3.Z));
    }
}

/// <summary>
/// The shape of an Alpine mover path, derived the way Alpine's shared header derives it so the viewport draws what the
/// game moves along (reimplemented from common/include/common/mover_path.h; behaviour, not code):
/// <list type="bullet">
/// <item>A path is drawn only when Legacy Movers is off and the mover does not Rotate in Place
/// (editor_patch/mover_path.cpp:62-65), and needs two or more keyframes (:399).</item>
/// <item>A loop (Loop Once / Loop Infinite) of three or more keyframes has a closing segment owned by the last keyframe
/// (mover_path.h:254-275).</item>
/// <item>A segment is curved when its owner keyframe's segment mode is Curved; Automatic tangents are a third of the
/// neighbouring span, along the neighbour chord when both sides curve, and along the straight neighbour when only one
/// does (resolve_handles, mover_path.h:281-313).</item>
/// <item>Curved segments are cubic Béziers from a keyframe through its out handle and the next keyframe's in handle
/// (make_curve, mover_path.h:333-352).</item>
/// </list>
/// </summary>
public static class MoverPathGeometry
{
    /// <summary>Loop Once / Loop Infinite (rf::MoverKeyframeMoveType 4, 5; mover_path.h:67-76, 249-252).</summary>
    public static bool IsLoop(int movementType) => movementType is 4 or 5;

    /// <summary>Only a loop of three or more keyframes has a closing segment of its own (mover_path.h:254-258).</summary>
    public static bool HasClosingSegment(int movementType, int count) => IsLoop(movementType) && count > 2;

    /// <summary>
    /// Whether Alpine gives a mover a path at all: Legacy Movers off (a level without the Alpine properties chunk has it
    /// on) and the mover not rotating in place (mover_path.cpp:62-65).
    /// </summary>
    public static bool HasPaths(RflFile rfl, Group group)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(group);
        AlpineLevelPropertiesSection? props = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is AlpineLevelPropertiesSection p)
            {
                props = p;
                break;
            }
        }

        return props is { LegacyMovers: 0 } && group.MovingData is { RotateInPlace: 0 };
    }

    /// <summary>
    /// The resolved in / out handles of every keyframe: stored for Aligned / Free tangents, computed for Automatic ones
    /// (mover_path.h:281-313).
    /// </summary>
    public static (Vec3 In, Vec3 Out)[] ResolveHandles(IReadOnlyList<Vec3> positions, IReadOnlyList<AlpineMoverPathKeyframe> data, bool closingSegment)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(data);
        int count = positions.Count;
        var handles = new (Vec3 In, Vec3 Out)[count];
        bool Curved(int owner) => data[owner].SegmentMode == 1;
        for (int i = 0; i < count; i++)
        {
            AlpineMoverPathKeyframe key = data[i];
            if (key.TangentMode != 0)
            {
                handles[i] = (key.InHandle, key.OutHandle);
                continue;
            }

            bool hasPrev = i > 0 || closingSegment;
            bool hasNext = i < count - 1 || closingSegment;
            int prev = (i + count - 1) % count;
            int next = (i + 1) % count;
            Vec3 toPrev = hasPrev ? positions[prev].Sub(positions[i]) : Vec3.Zero;
            Vec3 toNext = hasNext ? positions[next].Sub(positions[i]) : Vec3.Zero;
            bool prevCurved = hasPrev && Curved(prev);
            bool nextCurved = hasNext && Curved(i);

            Vec3 dir = Vec3.Zero;
            if (prevCurved && nextCurved)
            {
                dir = Normalized(toNext.Sub(toPrev));
            }
            else if (nextCurved)
            {
                dir = Normalized(hasPrev ? toPrev.Negate() : toNext);
            }
            else if (prevCurved)
            {
                dir = Normalized(hasNext ? toNext : toPrev.Negate());
            }

            handles[i] = (dir.Scale(toPrev.Length() / -3f), dir.Scale(toNext.Length() / 3f));
        }

        return handles;
    }

    /// <summary>
    /// The segments of a moving group's path, or an empty list when Alpine gives it none (see <see cref="HasPaths"/>,
    /// fewer than two keyframes). <paramref name="dataFor"/> returns a keyframe's curve settings by UID.
    /// </summary>
    public static IReadOnlyList<MoverPathSegment> Segments(Group group, Func<int, AlpineMoverPathKeyframe> dataFor)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(dataFor);
        var result = new List<MoverPathSegment>();
        if (group.MovingData is not { } motion || motion.Keyframes.Count < 2)
        {
            return result;
        }

        List<Keyframe> keys = motion.Keyframes;
        int count = keys.Count;
        var positions = new Vec3[count];
        var data = new AlpineMoverPathKeyframe[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = keys[i].Position;
            data[i] = dataFor(keys[i].Uid);
        }

        bool closing = HasClosingSegment(motion.MovementType, count);
        (Vec3 In, Vec3 Out)[] handles = ResolveHandles(positions, data, closing);
        for (int i = 0; i < count; i++)
        {
            bool hasSegment = i < count - 1 || closing;
            if (!hasSegment)
            {
                continue;
            }

            int next = (i + 1) % count;
            bool curved = data[i].SegmentMode == 1;
            Vec3 p0 = positions[i];
            Vec3 p3 = positions[next];
            result.Add(new MoverPathSegment(i, keys[i].Uid, keys[next].Uid, curved, i == count - 1,
                p0, curved ? p0.Add(handles[i].Out) : p0, curved ? p3.Add(handles[next].In) : p3, p3));
        }

        return result;
    }

    /// <summary>The segments of a moving group in <paramref name="rfl"/>, honouring <see cref="HasPaths"/>.</summary>
    public static IReadOnlyList<MoverPathSegment> Segments(RflFile rfl, Group group)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        if (!HasPaths(rfl, group))
        {
            return Array.Empty<MoverPathSegment>();
        }

        AlpineMoverPathsSection? paths = AlpineAttachedData.MoverPaths(rfl);
        var lookup = new Dictionary<int, AlpineMoverPathKeyframe>();
        if (paths is not null)
        {
            foreach (AlpineMoverPathKeyframe k in paths.Keyframes)
            {
                lookup[k.KeyframeUid] = k;
            }
        }

        return Segments(group, uid => lookup.TryGetValue(uid, out AlpineMoverPathKeyframe? k) ? k : new AlpineMoverPathKeyframe { KeyframeUid = uid });
    }

    /// <summary>
    /// The handles keyframe <paramref name="keyframeUid"/> has as drawn (resolved), for leaving Automatic: Alpine starts
    /// Aligned / Free handles where Automatic had them (mover_path.cpp:1704-1717, 560-567). Null when the keyframe is not
    /// on a path.
    /// </summary>
    public static (Vec3 In, Vec3 Out)? ResolvedHandles(RflFile rfl, Group group, int keyframeUid)
    {
        if (group.MovingData is not { } motion || motion.Keyframes.Count < 2)
        {
            return null;
        }

        int index = motion.Keyframes.FindIndex(k => k.Uid == keyframeUid);
        if (index < 0)
        {
            return null;
        }

        var positions = new Vec3[motion.Keyframes.Count];
        var data = new AlpineMoverPathKeyframe[motion.Keyframes.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = motion.Keyframes[i].Position;
            data[i] = AlpineAttachedData.KeyframeData(rfl, motion.Keyframes[i].Uid);
        }

        return ResolveHandles(positions, data, HasClosingSegment(motion.MovementType, positions.Length))[index];
    }

    private static Vec3 Normalized(Vec3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v.Scale(1f / len) : Vec3.Zero;
    }
}
