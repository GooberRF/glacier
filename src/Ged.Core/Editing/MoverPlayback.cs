using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>An orientation as three world axes (right, up, forward); the RF convention of <see cref="Mat3"/>.</summary>
public readonly record struct MoverFrame(Vector3 R, Vector3 U, Vector3 F)
{
    public static MoverFrame Identity => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    public static MoverFrame FromMat3(Mat3 m) =>
        new(new Vector3(m.Right.X, m.Right.Y, m.Right.Z), new Vector3(m.Up.X, m.Up.Y, m.Up.Z), new Vector3(m.Forward.X, m.Forward.Y, m.Forward.Z));

    public Mat3 ToMat3() => new(new Vec3(F.X, F.Y, F.Z), new Vec3(R.X, R.Y, R.Z), new Vec3(U.X, U.Y, U.Z));
}

/// <summary>
/// A cubic Bézier mover segment with its arc-length table, so a mover covers equal distances in equal shares of its
/// progress. Reimplemented from Alpine's shared mover path rules (common/include/common/mover_path.h:322-398: 32-step
/// arc table, parameter by arc fraction).
/// </summary>
public sealed class MoverCurve
{
    public const int ArcSteps = 32;
    private readonly float[] _arc = new float[ArcSteps + 1];

    public MoverCurve(Vector3 a, Vector3 aOut, Vector3 b, Vector3 bIn)
    {
        P0 = a;
        P1 = a + aOut;
        P2 = b + bIn;
        P3 = b;
        Vector3 prev = a;
        float total = 0f;
        for (int i = 1; i <= ArcSteps; i++)
        {
            Vector3 p = Sample(i / (float)ArcSteps);
            total += (p - prev).Length();
            _arc[i] = total;
            prev = p;
        }

        Length = total;
    }

    public Vector3 P0 { get; }

    public Vector3 P1 { get; }

    public Vector3 P2 { get; }

    public Vector3 P3 { get; }

    public float Length { get; }

    public Vector3 Sample(float t)
    {
        float u = 1f - t;
        return (u * u * u * P0) + (3f * u * u * t * P1) + (3f * u * t * t * P2) + (t * t * t * P3);
    }

    public Vector3 Derivative(float t)
    {
        float u = 1f - t;
        return 3f * (((P1 - P0) * (u * u)) + ((P2 - P1) * (2f * u * t)) + ((P3 - P2) * (t * t)));
    }

    /// <summary>The curve parameter at <paramref name="fraction"/> of the curve's length.</summary>
    public float ParamAtFraction(float fraction)
    {
        float f = Math.Clamp(fraction, 0f, 1f);
        if (!(Length > 0f))
        {
            return f;
        }

        float target = f * Length;
        int step = 1;
        while (step < ArcSteps && _arc[step] < target)
        {
            step++;
        }

        float start = _arc[step - 1];
        float span = _arc[step] - start;
        float within = span > 0f ? (target - start) / span : 0f;
        return (step - 1 + within) / ArcSteps;
    }

    /// <summary>The direction of travel at <paramref name="fraction"/>; the chord where a zero-length handle leaves none.</summary>
    public Vector3 Direction(float fraction)
    {
        Vector3 d = MoverMath.Normalized(Derivative(ParamAtFraction(fraction)));
        return d == Vector3.Zero ? MoverMath.Normalized(P3 - P0) : d;
    }
}

/// <summary>Vector and frame helpers of the mover rules (mover_path.h:99-119, 457-559, 717-798).</summary>
public static class MoverMath
{
    public static Vector3 Normalized(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v * (1f / len) : Vector3.Zero;
    }

    /// <summary>An upright frame facing <paramref name="dir"/>; straight up or down, +Z stands in for up.</summary>
    public static MoverFrame Facing(Vector3 dir)
    {
        Vector3 f = Normalized(dir);
        if (f == Vector3.Zero)
        {
            return MoverFrame.Identity;
        }

        Vector3 r = Normalized(Vector3.Cross(Vector3.UnitY, f));
        if (r == Vector3.Zero)
        {
            r = Normalized(Vector3.Cross(Vector3.UnitZ, f));
        }

        return new MoverFrame(r, Vector3.Cross(f, r), f);
    }

    /// <summary>Expresses <paramref name="v"/> in <paramref name="from"/> and rebuilds it in <paramref name="to"/>.</summary>
    public static Vector3 Carry(MoverFrame from, MoverFrame to, Vector3 v) =>
        (to.R * Vector3.Dot(v, from.R)) + (to.U * Vector3.Dot(v, from.U)) + (to.F * Vector3.Dot(v, from.F));

    public static MoverFrame Carry(MoverFrame from, MoverFrame to, MoverFrame m) =>
        new(Carry(from, to, m.R), Carry(from, to, m.U), Carry(from, to, m.F));

    public static MoverFrame UpsideDown(MoverFrame m) => new(-m.R, -m.U, m.F);

    public static float RollBetween(Vector3 a, Vector3 b, Vector3 axis) =>
        MathF.Atan2(Vector3.Dot(Vector3.Cross(a, b), axis), Vector3.Dot(a, b));

    public static MoverFrame Rolled(MoverFrame m, float angle)
    {
        Vector3 r = (m.R * MathF.Cos(angle)) + (Vector3.Cross(m.F, m.R) * MathF.Sin(angle));
        return new MoverFrame(r, Vector3.Cross(m.F, r), m.F);
    }

    /// <summary><paramref name="m"/> turned by the least rotation bringing its forward to <paramref name="dir"/>.</summary>
    public static MoverFrame Transported(MoverFrame m, Vector3 dir)
    {
        Vector3 w = Vector3.Cross(m.F, dir);
        float c = Vector3.Dot(m.F, dir);
        if (w == Vector3.Zero && c > 0f)
        {
            return m;
        }

        Vector3 r = c < -0.9999f
            ? -m.R
            : (m.R * c) + Vector3.Cross(w, m.R) + (w * (Vector3.Dot(w, m.R) / (1f + c)));
        r = Normalized(r - (dir * Vector3.Dot(r, dir)));
        return r == Vector3.Zero ? Facing(dir) : new MoverFrame(r, Vector3.Cross(dir, r), dir);
    }

    public static bool IsVertical(Vector3 dir) => Normalized(Vector3.Cross(Vector3.UnitY, dir)) == Vector3.Zero;

    public static float TurnBetween(Vector3 a, Vector3 b) => MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));

    /// <summary>The spherical blend from <paramref name="a"/> to <paramref name="b"/> (keyframe-rotation facing).</summary>
    public static MoverFrame Slerp(MoverFrame a, MoverFrame b, float s)
    {
        Quaternion qa = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(ToMatrix(a)));
        Quaternion qb = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(ToMatrix(b)));
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(qa, qb, s));
        return new MoverFrame(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33));
    }

    /// <summary>
    /// The frame RF.exe aims a Force Orient mover along (0x004FCEA0, as Alpine RED's preview reproduces it,
    /// editor_patch/mover_path.cpp:1282-1292): straight up or down has its own fixed frame.
    /// </summary>
    public static MoverFrame StockForwardOrient(Vector3 dir)
    {
        const float Vertical = 1e-4f;
        Vector3 f = Normalized(dir);
        if (MathF.Abs(f.X) < Vertical && MathF.Abs(f.Z) < Vertical)
        {
            return f.Y < 0f
                ? new MoverFrame(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY)
                : new MoverFrame(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        }

        return Facing(f);
    }

    public static Vector3 RotatedAbout(Vector3 v, Vector3 axis, float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        return (v * c) + (Vector3.Cross(axis, v) * s) + (axis * (Vector3.Dot(axis, v) * (1f - c)));
    }

    private static Matrix4x4 ToMatrix(MoverFrame f) => new(
        f.R.X, f.R.Y, f.R.Z, 0f,
        f.U.X, f.U.Y, f.U.Z, 0f,
        f.F.X, f.F.Y, f.F.Z, 0f,
        0f, 0f, 0f, 1f);
}

/// <summary>
/// The mover timing rules the game plays, reimplemented from Alpine's shared header so the editor preview matches the
/// game (common/include/common/mover_path.h:877-1132; game use game_patch/object/mover.cpp:635-720).
/// </summary>
public static class MoverTiming
{
    /// <summary>Seconds a segment takes: the travel time, or distance ÷ speed when travel time is a velocity (mover_path.h:880-890).</summary>
    public static float TravelSeconds(float travelParam, float dist, bool timeIsVelocity)
    {
        const float Fallback = 0.001f;
        if (!(travelParam > 0f))
        {
            return Fallback;
        }

        if (timeIsVelocity)
        {
            return dist > 0f ? dist / travelParam : Fallback;
        }

        return travelParam;
    }

    /// <summary>The game raises a travel time to cover its ramps at level load, unless it is a speed (mover_path.h:893-896).</summary>
    public static float EffectiveTravelParam(float travelParam, float accel, float decel, bool timeIsVelocity) =>
        timeIsVelocity ? travelParam : MathF.Max(travelParam, accel + decel);

    /// <summary>A speed-driven segment keeps its ramps within 90% of its time (mover_path.h:898-910).</summary>
    public static void FitRamps(float travel, bool timeIsVelocity, ref float accel, ref float decel)
    {
        if (!timeIsVelocity || !(travel > 0f))
        {
            return;
        }

        float sum = accel + decel;
        float max = 0.9f * travel;
        if (sum > 0f && sum > max)
        {
            float scale = max / sum;
            accel *= scale;
            decel *= scale;
        }
    }

    /// <summary>Trapezoid progress in [0, 1] after <paramref name="t"/> of <paramref name="total"/> seconds (mover_path.h:914-968).</summary>
    public static float Trapezoid(float t, float total, float accel, float decel)
    {
        if (total <= 0f || t >= total)
        {
            return 1f;
        }

        if (t <= 0f)
        {
            return 0f;
        }

        if (accel <= 0f && decel <= 0f)
        {
            return t / total;
        }

        float denom = total - (0.5f * (accel + decel));
        if (denom <= 0f)
        {
            return t / total;
        }

        float vmax = 1f / denom;
        if (t < accel)
        {
            return 0.5f * (vmax / accel) * t * t;
        }

        if (t < total - decel)
        {
            return (0.5f * vmax * accel) + (vmax * (t - accel));
        }

        float u = total - t;
        return 1f - (0.5f * vmax * (u * u / decel));
    }

    /// <summary>The Alpine easing curves on top of the trapezoid (mover_path.h:970-998): in x², out x(2−x), in-out smoothstep.</summary>
    public static float Ease(byte ease, float s) => ease switch
    {
        1 => s * s,
        2 => s * (2f - s),
        3 => s * s * (3f - (2f * s)),
        _ => s,
    };

    public static float SegmentProgress(float t, float total, float accel, float decel, byte ease) =>
        Ease(ease, Trapezoid(t, total, accel, decel));

    /// <summary>The next keyframe after arriving at <paramref name="cur"/> (rf::MoverKeyframeMoveType rules, mover_path.h:1025-1132).</summary>
    public static (int Next, bool Forward) NextKeyframe(int movementType, int count, int from, int cur, bool forward)
    {
        int last = count - 1;
        int next = -1;
        switch (movementType)
        {
            case 1: // one way
                if (forward && cur < last)
                {
                    next = cur + 1;
                }
                else if (forward)
                {
                    forward = false;
                }
                else if (cur > 0)
                {
                    next = cur - 1;
                }
                else
                {
                    forward = true;
                }

                break;
            case 2: // ping pong once
            case 3: // ping pong infinite
                if (forward)
                {
                    if (cur < last)
                    {
                        next = cur + 1;
                    }
                    else
                    {
                        forward = false;
                        next = count > 1 ? cur - 1 : -1;
                    }
                }
                else if (cur > 0)
                {
                    next = cur - 1;
                }
                else
                {
                    forward = true;
                    next = movementType == 3 && count > 1 ? cur + 1 : -1;
                }

                break;
            case 4: // loop once
                if (forward && (from != last || cur != 0))
                {
                    next = cur < last ? cur + 1 : 0;
                }
                else if (!forward && (from != 0 || cur != last))
                {
                    next = cur > 0 ? cur - 1 : last;
                }

                break;
            case 5: // loop infinite
                next = forward ? (cur < last ? cur + 1 : 0) : (cur > 0 ? cur - 1 : last);
                break;
            case 6: // lift: stops at each keyframe until triggered again
                if (cur == last)
                {
                    forward = false;
                }
                else if (cur == 0)
                {
                    forward = true;
                }

                break;
        }

        return (next, forward);
    }

    /// <summary>The keyframe whose data shapes the segment between two keyframes, or -1 for a non-neighbour jump (mover_path.h:263-275).</summary>
    public static int SegmentOwner(int from, int to, int count, bool closingSegment)
    {
        if (to == from + 1)
        {
            return from;
        }

        if (from == to + 1)
        {
            return to;
        }

        if (closingSegment && ((from == count - 1 && to == 0) || (from == 0 && to == count - 1)))
        {
            return count - 1;
        }

        return -1;
    }
}

/// <summary>
/// A moving group's path as the game plays it, gathered once from the level: keyframe positions, orientations and
/// timing, the Alpine curve settings (honoured only with Legacy Movers off and no Rotate in Place, as the game does,
/// game_patch/object/mover.cpp:175-213), and the resolved curves.
/// </summary>
public sealed class MoverPlaybackPath
{
    private MoverPlaybackPath(Group group, Keyframe[] keys, AlpineMoverPathKeyframe[] data, bool paths, bool legacy, byte orientMode)
    {
        Group = group;
        MovingGroupData motion = group.MovingData!;
        Keys = keys;
        Count = keys.Length;
        Positions = new Vector3[Count];
        Orients = new MoverFrame[Count];
        for (int i = 0; i < Count; i++)
        {
            Positions[i] = new Vector3(keys[i].Position.X, keys[i].Position.Y, keys[i].Position.Z);
            Orients[i] = MoverFrame.FromMat3(keys[i].Rotation);
        }

        HasPaths = paths;
        Legacy = legacy;
        MovementType = motion.MovementType;
        RotateInPlace = motion.RotateInPlace != 0;
        ForceOrient = motion.ForceOrient != 0;
        UseTravelTimeAsVelocity = motion.UseTravelTimeAsSpeed != 0;
        StartsBackwards = motion.StartsBackwards != 0;
        StartingKeyframe = motion.StartingKeyframe;
        OrientMode = paths ? orientMode : (byte)0;
        Closing = MoverPathGeometry.HasClosingSegment(MovementType, Count);
        Eases = new byte[Count];
        SegmentFacing = new byte[Count];
        Curves = new MoverCurve?[Count];
        for (int i = 0; i < Count; i++)
        {
            // The game eases a segment whenever the level is not on legacy movers (mover.cpp:206-210).
            Eases[i] = legacy ? (byte)0 : data[i].Ease;
            SegmentFacing[i] = (byte)(data[i].SegmentOrient & 0x3);
        }

        if (paths && Count >= 2)
        {
            var positions = new Vec3[Count];
            for (int i = 0; i < Count; i++)
            {
                positions[i] = keys[i].Position;
            }

            (Vec3 In, Vec3 Out)[] handles = MoverPathGeometry.ResolveHandles(positions, data, Closing);
            for (int i = 0; i < Count; i++)
            {
                if (HasSegment(i) && data[i].SegmentMode == 1)
                {
                    int next = (i + 1) % Count;
                    Curves[i] = new MoverCurve(Positions[i], V(handles[i].Out), Positions[next], V(handles[next].In));
                }
            }
        }
    }

    public Group Group { get; }

    public Keyframe[] Keys { get; }

    public int Count { get; }

    public Vector3[] Positions { get; }

    public MoverFrame[] Orients { get; }

    /// <summary>True when the mover follows Alpine curves and facing (Legacy Movers off, no Rotate in Place).</summary>
    public bool HasPaths { get; }

    /// <summary>True when the level is on legacy movers (stock straight segments, no easing).</summary>
    public bool Legacy { get; }

    public int MovementType { get; }

    public bool RotateInPlace { get; }

    public bool ForceOrient { get; }

    public bool UseTravelTimeAsVelocity { get; }

    public bool StartsBackwards { get; }

    public int StartingKeyframe { get; }

    /// <summary>The mover's facing (0 none, 1 follow path, 2 keyframe rotation); none without paths.</summary>
    public byte OrientMode { get; }

    public bool Closing { get; }

    public byte[] Eases { get; }

    public byte[] SegmentFacing { get; }

    /// <summary>The curve of each owner's segment, or null when it is straight.</summary>
    public MoverCurve?[] Curves { get; }

    public bool HasSegment(int owner) => owner < Count - 1 || Closing;

    public bool SegmentCurved(int owner) => owner >= 0 && owner < Count && Curves[owner] is not null;

    /// <summary>The playback path of <paramref name="group"/>, or null when it is not a mover with keyframes.</summary>
    public static MoverPlaybackPath? From(RflFile rfl, Group group)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(group);
        if (group.MovingData is not { Keyframes.Count: > 0 } motion)
        {
            return null;
        }

        Keyframe[] keys = motion.Keyframes.ToArray();
        var data = new AlpineMoverPathKeyframe[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            data[i] = AlpineAttachedData.KeyframeData(rfl, keys[i].Uid);
        }

        AlpineLevelPropertiesSection? props = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is AlpineLevelPropertiesSection p)
            {
                props = p;
                break;
            }
        }

        bool legacy = props is null || props.LegacyMovers != 0;
        return new MoverPlaybackPath(group, keys, data, MoverPathGeometry.HasPaths(rfl, group), legacy,
            AlpineAttachedData.MoverSettings(rfl, group).OrientMode);
    }

    /// <summary>The point <paramref name="fraction"/> of the way (by length) along the segment owned by <paramref name="owner"/>.</summary>
    public Vector3 SegmentPoint(int owner, float fraction)
    {
        if (Curves[owner] is { } c)
        {
            return c.Sample(c.ParamAtFraction(fraction));
        }

        Vector3 a = Positions[owner];
        Vector3 b = Positions[(owner + 1) % Count];
        return a + ((b - a) * fraction);
    }

    /// <summary>The direction of travel at a spot (follow_direction, mover_path.h:430-455).</summary>
    internal Vector3 Direction(int owner, float fraction, int from, int to)
    {
        if (owner < 0)
        {
            Vector3 jump = MoverMath.Normalized(Positions[to] - Positions[from]);
            if (jump != Vector3.Zero)
            {
                return jump;
            }
        }

        if (owner >= 0 && Curves[owner] is { } curve)
        {
            Vector3 d = curve.Direction(fraction);
            if (d != Vector3.Zero)
            {
                return d;
            }
        }

        int first = owner < 0 ? from : owner;
        for (int i = 0; i < Count; i++)
        {
            int a = (first + i) % Count;
            Vector3 chord = MoverMath.Normalized(Positions[(a + 1) % Count] - Positions[a]);
            if (chord != Vector3.Zero)
            {
                return chord;
            }
        }

        return Vector3.Zero;
    }

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>Where a moving group is in its run: the segment it travels and how far, or the pause it holds (mover_path.cpp:875-889).</summary>
public struct MoverSimState : IEquatable<MoverSimState>
{
    public int From;
    public int To;
    public float T;
    public float Pause;
    public bool Forward;
    public bool LastLeg;

    public readonly bool Moving => To >= 0;

    public readonly bool Stopped => To < 0 && Pause <= 0f;

    public readonly bool Equals(MoverSimState o) =>
        From == o.From && To == o.To && T == o.T && Pause == o.Pause && Forward == o.Forward && LastLeg == o.LastLeg;

    public override readonly bool Equals(object? obj) => obj is MoverSimState s && Equals(s);

    public override readonly int GetHashCode() => HashCode.Combine(From, To, T, Pause, Forward, LastLeg);
}

/// <summary>
/// The pose a moving group's members take at a moment of the preview: translated from the first keyframe to
/// <see cref="Now"/> and, when the mover turns, carried from <see cref="FromFrame"/> to <see cref="ToFrame"/>
/// (Alpine RED's preview ghost, editor_patch/mover_path.cpp:1294-1373).
/// </summary>
public readonly record struct MoverPose(Vector3 Kf0, Vector3 Now, bool Turns, MoverFrame FromFrame, MoverFrame ToFrame, bool KeepOrient, bool Aimed, MoverFrame Aim)
{
    /// <summary>Where a member at rest pose (<paramref name="restPos"/>, <paramref name="restRot"/>) is at this pose.</summary>
    public (Vec3 Position, Mat3 Rotation) Place(Vec3 restPos, Mat3 restRot)
    {
        var rest = new Vector3(restPos.X, restPos.Y, restPos.Z);
        Vector3 pos = rest + (Now - Kf0);
        MoverFrame orient = MoverFrame.FromMat3(restRot);
        if (Turns)
        {
            pos = Now + MoverMath.Carry(FromFrame, ToFrame, rest - Kf0);
            if (!KeepOrient)
            {
                orient = MoverMath.Carry(FromFrame, ToFrame, orient);
            }
        }

        if (Aimed)
        {
            orient = Aim;
        }

        return (new Vec3(pos.X, pos.Y, pos.Z), orient.ToMat3());
    }
}

/// <summary>
/// Plays a moving group the way the game moves it, for the editor's mover preview: travel times (or speeds), pauses,
/// acceleration and deceleration ramps, Alpine easing, the movement type's keyframe order, Alpine curves and facing
/// (Follow Path, Keyframe Rotation), Rotate in Place about the first keyframe's up axis, and Force Orient. Reimplemented
/// from Alpine RED's preview (editor_patch/mover_path.cpp:873-1373) over the shared timing rules
/// (common/include/common/mover_path.h:877-1132). Pure: it never touches the document.
/// </summary>
public sealed class MoverPlayback
{
    private const int MaxPhases = 4096;
    private MoverSimState _runStart;
    private MoverFollowFrames? _follow;

    public MoverPlayback(MoverPlaybackPath path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        if (path.OrientMode != 0 || HasFollowSegment(path))
        {
            _follow = MoverFollowFrames.Build(path);
        }

        Reset();
    }

    public MoverPlaybackPath Path { get; }

    public MoverSimState State { get; private set; }

    /// <summary>Seconds into the current run (wraps for an endless run).</summary>
    public float RunTime { get; private set; }

    /// <summary>Seconds one run takes (one lap of an endless mover).</summary>
    public float RunLength { get; private set; }

    public bool Playing { get; private set; }

    /// <summary>True when the path has enough keyframes to play (two, or one for Rotate in Place).</summary>
    public bool CanPlay => Path.Count >= (Path.RotateInPlace ? 1 : 2);

    /// <summary>Back to rest at the starting keyframe (mover_path.cpp:1082-1093).</summary>
    public void Reset()
    {
        int count = Path.Count;

        // RED holds the starting keyframe one-based in memory (editor_patch/level.h:1662) and the RFL stores it
        // zero-based (stock levels, e.g. ctf01's blast doors, store 0 for the first keyframe).
        int start = Path.StartingKeyframe + 1;
        var sim = new MoverSimState
        {
            From = start > count ? 0 : Math.Max(start - 1, 0),
            To = -1,
            // Level load turns Starts Moving Backwards off for a rotating mover.
            Forward = !Path.StartsBackwards || Path.RotateInPlace,
        };
        sim.From = Math.Clamp(sim.From, 0, Math.Max(count - 1, 0));
        State = sim;
        _runStart = sim;
        RunTime = 0f;
        RunLength = 0f;
        Playing = false;
    }

    /// <summary>Play / pause; playing from rest starts a run (mover_path.cpp:1196-1208).</summary>
    public void PlayPause()
    {
        if (!CanPlay)
        {
            return;
        }

        Playing = !Playing;
        if (Playing && State.Stopped)
        {
            StartRun();
        }
    }

    /// <summary>Stops and returns to rest (mover_path.cpp:1210-1219).</summary>
    public void Stop() => Reset();

    /// <summary>Advances a playing preview by <paramref name="dt"/> seconds (mover_path.cpp:1140-1177).</summary>
    public void Tick(float dt)
    {
        if (!Playing || !CanPlay)
        {
            return;
        }

        MoverSimState sim = State;
        Advance(ref sim, dt);
        State = sim;
        RunTime += dt;
        if (sim.Stopped)
        {
            Playing = false;
            RunTime = RunLength;
        }
        else if (RunLength > 0f && RunTime >= RunLength)
        {
            RunTime -= RunLength;
        }
    }

    /// <summary>Jumps to <paramref name="fraction"/> of the run, paused (mover_path.cpp:1179-1194).</summary>
    public void Scrub(float fraction)
    {
        if (!CanPlay)
        {
            return;
        }

        if (State.Stopped && RunLength <= 0f)
        {
            StartRun();
        }

        Playing = false;
        RunTime = RunLength * Math.Clamp(fraction, 0f, 1f);
        MoverSimState sim = _runStart;
        Advance(ref sim, RunTime);
        State = sim;
    }

    /// <summary>The run fraction shown on the scrub bar.</summary>
    public float RunFraction => RunLength > 0f ? Math.Clamp(RunTime / RunLength, 0f, 1f) : 0f;

    /// <summary>A one-line status: moving / paused / stopped, the keyframe, and the run clock.</summary>
    public string Status
    {
        get
        {
            string state = State.Pause > 0f ? "Paused at keyframe" : State.Moving ? "Moving" : "Stopped";
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{state}: keyframe {State.From + 1}, {RunTime:0.00} / {RunLength:0.00} s");
        }
    }

    /// <summary>The members' pose now (ghost_pose, mover_path.cpp:1322-1373).</summary>
    public MoverPose Pose()
    {
        MoverSimState sim = State;
        Vector3 kf0 = Path.Count > 0 ? Path.Positions[0] : Vector3.Zero;
        if (Path.Count == 0)
        {
            return new MoverPose(kf0, kf0, false, MoverFrame.Identity, MoverFrame.Identity, false, false, MoverFrame.Identity);
        }

        if (Path.RotateInPlace)
        {
            (float travel, float accel, float decel, byte ease) = SpinTiming(sim);
            float s = sim.To < 0 ? 0f : MoverTiming.SegmentProgress(sim.T, travel, accel, decel, ease);
            float full = Path.Keys[0].DegreesAboutAxis * (MathF.PI / 180f);
            float angle = (sim.Forward ? s : 1f - s) * full;
            Vector3 axis = MoverMath.Normalized(Path.Orients[0].U);
            var to = new MoverFrame(
                MoverMath.RotatedAbout(Vector3.UnitX, axis, angle),
                MoverMath.RotatedAbout(Vector3.UnitY, axis, angle),
                MoverMath.RotatedAbout(Vector3.UnitZ, axis, angle));
            return new MoverPose(kf0, kf0, true, MoverFrame.Identity, to, Path.ForceOrient, false, MoverFrame.Identity);
        }

        float progress = 0f;
        Vector3 now = Path.Positions[sim.From];
        if (sim.To >= 0)
        {
            Segment seg = SegmentTiming(sim);
            progress = MoverTiming.SegmentProgress(sim.T, seg.Travel, seg.Accel, seg.Decel, seg.Ease);
            if (seg.Owner >= 0 && Path.Curves[seg.Owner] is { } curve)
            {
                now = curve.Sample(curve.ParamAtFraction(seg.Owner == sim.From ? progress : 1f - progress));
            }
            else
            {
                now = Path.Positions[sim.From] + ((Path.Positions[sim.To] - Path.Positions[sim.From]) * progress);
            }
        }

        if (Path.OrientMode != 0 && Path.HasPaths)
        {
            (int owner, float fraction) = Spot(sim.From, sim.To, progress);
            MoverFrame reference = FacingMode(0) == 1 && _follow is not null ? _follow.Reference : Path.Orients[0];
            MoverFrame pose;
            if (FacingMode(owner) == 1 && _follow is not null)
            {
                pose = _follow.Pose(owner, fraction, Path.Direction(owner, fraction, sim.From, sim.To));
            }
            else
            {
                pose = sim.To >= 0 ? MoverMath.Slerp(Path.Orients[sim.From], Path.Orients[sim.To], progress) : Path.Orients[sim.From];
            }

            return new MoverPose(kf0, now, true, reference, pose, false, false, MoverFrame.Identity);
        }

        if (Path.ForceOrient && sim.To >= 0)
        {
            MoverFrame aim = MoverMath.StockForwardOrient(Path.Positions[sim.To] - Path.Positions[sim.From]);
            return new MoverPose(kf0, now, false, MoverFrame.Identity, MoverFrame.Identity, false, true, aim);
        }

        return new MoverPose(kf0, now, false, MoverFrame.Identity, MoverFrame.Identity, false, false, MoverFrame.Identity);
    }

    /// <summary>
    /// Equal time steps along each segment of the forward run (Alpine RED's timing ticks, mover_path.cpp:297-321):
    /// bunched points show where the mover is slow. Empty for a mover without segments.
    /// </summary>
    public IReadOnlyList<Vector3> TimingTicks(float tickSeconds = 0.25f, int maxPerSegment = 64)
    {
        var ticks = new List<Vector3>();
        if (Path.RotateInPlace)
        {
            return ticks;
        }

        for (int owner = 0; owner < Path.Count; owner++)
        {
            if (!Path.HasSegment(owner) || Path.Count < 2)
            {
                continue;
            }

            Keyframe kf = Path.Keys[owner];
            float dist = Path.Curves[owner]?.Length ?? (Path.Positions[(owner + 1) % Path.Count] - Path.Positions[owner]).Length();
            bool velocity = Path.UseTravelTimeAsVelocity;
            float travel = MoverTiming.TravelSeconds(MoverTiming.EffectiveTravelParam(kf.DepartTravelTime, kf.AccelTime, kf.DecelTime, velocity), dist, velocity);
            float accel = kf.AccelTime;
            float decel = kf.DecelTime;
            MoverTiming.FitRamps(travel, velocity, ref accel, ref decel);
            int n = Math.Clamp((int)(travel / tickSeconds), 1, maxPerSegment);
            float step = travel / n;
            for (int i = 1; i < n; i++)
            {
                float s = MoverTiming.SegmentProgress(step * i, travel, accel, decel, Path.Eases[owner]);
                ticks.Add(Path.SegmentPoint(owner, s));
            }
        }

        return ticks;
    }

    // ---- simulation (mover_path.cpp:903-1042) --------------------------------------------------------------

    private readonly record struct Segment(int Owner, float Travel, float Accel, float Decel, byte Ease);

    private Segment SegmentTiming(MoverSimState sim)
    {
        int owner = MoverTiming.SegmentOwner(sim.From, sim.To, Path.Count, Path.Closing);
        float dist = owner >= 0 && Path.Curves[owner] is { } c
            ? c.Length
            : (Path.Positions[sim.To] - Path.Positions[sim.From]).Length();
        Keyframe departing = Path.Keys[sim.From];
        bool velocity = Path.UseTravelTimeAsVelocity;
        float param = sim.Forward ? departing.DepartTravelTime : departing.ReturnTravelTime;
        float travel = MoverTiming.TravelSeconds(MoverTiming.EffectiveTravelParam(param, departing.AccelTime, departing.DecelTime, velocity), dist, velocity);
        float accel = departing.AccelTime;
        float decel = departing.DecelTime;
        MoverTiming.FitRamps(travel, velocity, ref accel, ref decel);
        return new Segment(owner, travel, accel, decel, Path.Eases[sim.From]);
    }

    private (float Travel, float Accel, float Decel, byte Ease) SpinTiming(MoverSimState sim)
    {
        Keyframe kf = Path.Keys[0];
        float param = sim.Forward ? kf.DepartTravelTime : kf.ReturnTravelTime;
        float travel = MoverTiming.TravelSeconds(MoverTiming.EffectiveTravelParam(param, kf.AccelTime, kf.DecelTime, Path.UseTravelTimeAsVelocity), 1f, false);
        return (travel, kf.AccelTime, kf.DecelTime, Path.Eases[0]);
    }

    private float PhaseLeft(MoverSimState sim)
    {
        if (sim.Pause > 0f)
        {
            return sim.Pause;
        }

        if (sim.To < 0)
        {
            return 0f;
        }

        float travel = Path.RotateInPlace ? SpinTiming(sim).Travel : SegmentTiming(sim).Travel;
        return MathF.Max(travel - sim.T, 0f);
    }

    private void FinishPhase(ref MoverSimState sim)
    {
        if (sim.Pause > 0f)
        {
            sim.Pause = 0f;
            return;
        }

        int type = Path.MovementType;
        if (Path.RotateInPlace)
        {
            sim.T = 0f;
            if (type == 5)
            {
                return;
            }

            if (sim.Forward)
            {
                sim.Pause = Path.Keys[0].PauseTime;
            }

            sim.Forward = !sim.Forward;
            bool returns = type is 2 or 4;
            if (type == 1 || (returns && sim.LastLeg))
            {
                sim.To = -1;
                sim.LastLeg = false;
            }
            else if (returns)
            {
                sim.LastLeg = true;
            }

            return;
        }

        int cur = sim.To;
        (int next, bool forward) = MoverTiming.NextKeyframe(type, Path.Count, sim.From, cur, sim.Forward);
        sim.From = cur;
        sim.To = next;
        sim.Forward = forward;
        sim.T = 0f;

        // A one-way run stops at its end without its pause.
        sim.Pause = type == 1 && next < 0 ? 0f : Path.Keys[cur].PauseTime;
    }

    private void Advance(ref MoverSimState sim, float dt)
    {
        for (int i = 0; i < MaxPhases && (sim.To >= 0 || sim.Pause > 0f); i++)
        {
            float left = PhaseLeft(sim);
            if (dt < left)
            {
                if (sim.Pause > 0f)
                {
                    sim.Pause -= dt;
                }
                else
                {
                    sim.T += dt;
                }

                return;
            }

            dt -= left;
            FinishPhase(ref sim);
        }
    }

    private void Activate(ref MoverSimState sim)
    {
        if (sim.To >= 0)
        {
            return;
        }

        int count = Path.Count;
        if (Path.RotateInPlace)
        {
            sim.To = 0;
        }
        else if (sim.Forward)
        {
            sim.To = sim.From + 1 < count ? sim.From + 1 : 0;
        }
        else
        {
            sim.To = sim.From > 0 ? sim.From - 1 : count - 1;
        }
    }

    private float MeasureRun(MoverSimState sim)
    {
        MoverSimState start = sim;
        float total = 0f;
        for (int i = 0; i < MaxPhases && (sim.To >= 0 || sim.Pause > 0f); i++)
        {
            total += PhaseLeft(sim);
            FinishPhase(ref sim);
            if (sim.Equals(start))
            {
                break;
            }
        }

        return total;
    }

    private void StartRun()
    {
        MoverSimState sim = State;
        Activate(ref sim);
        State = sim;
        _runStart = sim;
        RunTime = 0f;
        RunLength = MeasureRun(sim);
    }

    // ---- facing (mover_path.h:800-875) ----------------------------------------------------------------------

    /// <summary>The facing of the segment <paramref name="owner"/> owns: its own choice once the mover faces at all.</summary>
    private int FacingMode(int owner)
    {
        int mover = Path.OrientMode;
        if (owner < 0 || owner >= Path.Count || !Path.HasSegment(owner) || mover == 0 || Path.SegmentFacing[owner] == 0)
        {
            return mover;
        }

        return Path.SegmentFacing[owner];
    }

    private static bool HasFollowSegment(MoverPlaybackPath path)
    {
        if (path.OrientMode == 0)
        {
            return false;
        }

        for (int i = 0; i < path.Count; i++)
        {
            if (path.HasSegment(i) && path.SegmentFacing[i] == 1)
            {
                return true;
            }
        }

        return false;
    }

    private (int Owner, float Fraction) Spot(int from, int to, float s)
    {
        int count = Path.Count;
        if (to >= 0)
        {
            int owner = MoverTiming.SegmentOwner(from, to, count, Path.Closing);
            return (owner, owner == from ? s : 1f - s);
        }

        return from < count - 1 || Path.Closing ? (from, 0f) : (from - 1, 1f);
    }
}

/// <summary>
/// Follow Path frames: a mover kept upright as it follows its path, rolling at most three times the path's own turn so
/// a vertical loop goes over the top inverted rather than flipping, and snapping at corners. Reimplemented from
/// common/include/common/mover_path.h:493-715.
/// </summary>
internal sealed class MoverFollowFrames
{
    private const float MaxRoll = 3f;
    private const float FullKink = 0.5f;
    private const float QuarterTurn = 1.5707964f;

    private readonly List<(MoverFrame Frame, int Level)[]> _segments = new();

    public MoverFrame Reference { get; private set; } = MoverFrame.Identity;

    private enum Join
    {
        None,
        Kink,
        Corner,
    }

    public static MoverFollowFrames Build(MoverPlaybackPath path)
    {
        var frames = new MoverFollowFrames();
        int count = path.Count;
        int segmentCount = path.Closing ? count : count - 1;
        if (count < 2)
        {
            return frames;
        }

        frames.Reference = MoverMath.Facing(path.Direction(0, 0f, 0, 0));
        var dirs = new List<Vector3>();
        var joins = new List<Join>();
        bool prevCurved = segmentCount > 0 && path.SegmentCurved(segmentCount - 1);
        for (int owner = 0; owner < segmentCount; owner++)
        {
            bool curved = path.SegmentCurved(owner);
            int n = curved ? (2 * MoverCurve.ArcSteps) + 1 : 2;
            frames._segments.Add(new (MoverFrame, int)[n]);
            for (int i = 0; i < n; i++)
            {
                dirs.Add(MoverMath.Normalized(path.Direction(owner, i / (float)(n - 1), 0, 0)));
                joins.Add(i > 0 ? Join.None : (curved || prevCurved ? Join.Kink : Join.Corner));
            }

            prevCurved = curved;
        }

        for (int k = 1; k < dirs.Count; k++)
        {
            if (joins[k] == Join.None && MoverMath.TurnBetween(dirs[k - 1], dirs[k]) >= FullKink)
            {
                joins[k] = Join.Kink;
            }
        }

        var slots = new List<(int Segment, int Index)>();
        for (int s = 0; s < frames._segments.Count; s++)
        {
            for (int i = 0; i < frames._segments[s].Length; i++)
            {
                slots.Add((s, i));
            }
        }

        (MoverFrame Frame, int Level) Get(int k) => frames._segments[slots[k].Segment][slots[k].Index];
        void Set(int k, (MoverFrame, int) v) => frames._segments[slots[k].Segment][slots[k].Index] = v;
        (MoverFrame, int) StepAt(MoverFrame prev, int k, int want, Join join) =>
            dirs[k] == Vector3.Zero ? (prev, 0) : Step(prev, dirs[k], want, join);

        int total = slots.Count;
        if (total > 0)
        {
            Set(0, (frames.Reference, MoverMath.IsVertical(dirs[0]) ? 0 : 1));
        }

        for (int k = 1; k < total; k++)
        {
            (MoverFrame prev, int level) = Get(k - 1);
            Set(k, StepAt(prev, k, joins[k] != Join.None ? level : 0, joins[k]));
        }

        // A lap comes round rolled from the reference: build its end back from the reference and take it from where
        // the two walks meet (mover_path.h:651-688).
        if (path.Closing && total > 2)
        {
            var back = new (MoverFrame Frame, int Level)[total];
            MoverFrame carried = frames.Reference;
            int joinAt = total - 2;
            float joinRoll = 0f;
            float slowest = -1f;
            for (int k = total - 1; k > 0; k--)
            {
                back[k] = StepAt(carried, k, Get(k).Level, joins[(k + 1) % total]);
                carried = back[k].Frame;
                if (back[k].Level != 0 && back[k].Level == Get(k).Level)
                {
                    for (int j = k + 1; j < total; j++)
                    {
                        Set(j, back[j]);
                    }

                    return frames;
                }

                if (k < total - 1)
                {
                    float roll = MoverMath.RollBetween(back[k].Frame.R, Get(k).Frame.R, back[k].Frame.F);
                    float rate = MathF.Abs(roll) / (total - 1 - k);
                    if (slowest < 0f || rate < slowest)
                    {
                        slowest = rate;
                        joinAt = k;
                        joinRoll = roll;
                    }
                }
            }

            var stepsAfter = new float[total];
            for (int j = total - 2; j >= joinAt; j--)
            {
                stepsAfter[j] = stepsAfter[j + 1] + (joins[j + 1] == Join.None ? 1f : 0f);
            }

            float span = MathF.Max(stepsAfter[joinAt], 1f);
            for (int j = joinAt + 1; j < total; j++)
            {
                Set(j, (MoverMath.Rolled(back[j].Frame, joinRoll * stepsAfter[j] / span), 0));
            }
        }

        return frames;
    }

    /// <summary>The pose at a spot heading along <paramref name="dir"/> (follow_pose, mover_path.h:694-715).</summary>
    public MoverFrame Pose(int owner, float fraction, Vector3 dir)
    {
        if (owner < 0 || owner >= _segments.Count || dir == Vector3.Zero)
        {
            return MoverMath.Facing(dir);
        }

        (MoverFrame Frame, int Level)[] samples = _segments[owner];
        int last = samples.Length - 1;
        if (last < 1)
        {
            return MoverMath.Facing(dir);
        }

        float f = fraction > 0f ? MathF.Min(fraction, 1f) : 0f;
        float x = f * last;
        int i = Math.Min((int)x, last - 1);
        (MoverFrame Frame, int Level) a = samples[i];
        (MoverFrame Frame, int Level) b = samples[i + 1];
        if (a.Level != 0 && a.Level == b.Level)
        {
            return a.Level > 0 ? MoverMath.Facing(dir) : MoverMath.UpsideDown(MoverMath.Facing(dir));
        }

        MoverFrame from = MoverMath.Transported(a.Frame, dir);
        MoverFrame to = MoverMath.Transported(b.Frame, dir);
        return MoverMath.Rolled(from, MoverMath.RollBetween(from.R, to.R, from.F) * (x - i));
    }

    private static (MoverFrame Frame, int Level) Step(MoverFrame prev, Vector3 dir, int want, Join join)
    {
        MoverFrame carried = MoverMath.Transported(prev, dir);
        if (MoverMath.IsVertical(dir))
        {
            return (carried, 0);
        }

        MoverFrame upright = MoverMath.Facing(dir);
        float roll = MoverMath.RollBetween(upright.R, carried.R, upright.F);
        int level = want != 0 ? want : (MathF.Abs(roll) <= QuarterTurn ? 1 : -1);
        MoverFrame target = level > 0 ? upright : MoverMath.UpsideDown(upright);
        float off = level > 0 ? roll : roll - MathF.CopySign(2f * QuarterTurn, roll);
        float turn = MoverMath.TurnBetween(prev.F, dir);
        float limit = (MaxRoll * turn) + 1e-4f;
        if (join == Join.Corner)
        {
            limit = 2f * QuarterTurn;
        }
        else if (join == Join.Kink)
        {
            limit = MathF.Max(limit, 2f * QuarterTurn * MathF.Min(turn / FullKink, 1f));
        }

        return MathF.Abs(off) <= limit
            ? (target, level)
            : (MoverMath.Rolled(target, off - MathF.CopySign(limit, off)), 0);
    }
}
