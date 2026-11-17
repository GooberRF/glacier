using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>The outcome of a keyframe propagation: what changed, or why nothing was done.</summary>
/// <param name="Movers">Movers whose chain settings were written.</param>
/// <param name="Keyframes">Keyframes whose own settings were written (Propagate Properties only).</param>
/// <param name="Refusal">RED's refusal message when the source cannot propagate, else null.</param>
public sealed record MoverPropagationResult(int Movers, int Keyframes, string? Refusal)
{
    public bool Refused => Refusal is not null;
}

/// <summary>
/// RED's keyframe-dialog Propagate Sounds and Propagate Properties buttons, reproduced from RED.exe (handlers at
/// 0x00466980 and 0x00466C20, message map at 0x00559600/0x00559618). Both key on the source keyframe's script name:
/// every mover with a keyframe of the same name (compared case-insensitively, RED's <c>_stricmp</c> at 0x00526A20)
/// takes the source's values. RED refuses a keyframe with the default script name "Keyframe" (string 0x00579EEC)
/// and a multi-keyframe dialog (0x00579EB4). Glacier also refuses an empty name, which every keyframe it creates has.
/// <list type="bullet">
/// <item>Propagate Sounds writes the four sounds and their volumes to each such mover.</item>
/// <item>Propagate Properties writes the chain flags (Is Door, Rotate in Place, Starts Moving Backwards, Use Travel
/// Time as Velocity, Force Orient, No Player Collide), Movement Type and Starting Keyframe to each such mover, and the
/// keyframe fields (pause, departing / returning travel, acceleration, deceleration, trigger event UID, both item UIDs,
/// degrees about axis) to every keyframe of that name.</item>
/// </list>
/// Each propagation is one undo step. RED's Stop Sounds button (0x00467000) only stops the dialog's sound preview; it
/// edits nothing.
/// </summary>
public static class MoverPropagation
{
    /// <summary>RED's default keyframe script name, which it will not propagate from.</summary>
    public const string DefaultScriptName = "Keyframe";

    /// <summary>The movers holding a keyframe named like <paramref name="source"/>, or a refusal.</summary>
    public static (IReadOnlyList<Group> Movers, string? Refusal) Targets(MoverService movers, Keyframe source)
    {
        ArgumentNullException.ThrowIfNull(movers);
        ArgumentNullException.ThrowIfNull(source);
        string name = source.ScriptName ?? string.Empty;
        if (name.Length == 0)
        {
            return (Array.Empty<Group>(), "Will not propagate from a keyframe without a script name.");
        }

        if (string.Equals(name, DefaultScriptName, StringComparison.OrdinalIgnoreCase))
        {
            return (Array.Empty<Group>(), "Will not propagate from a keyframe with the default script name.");
        }

        var list = movers.Movers
            .Where(g => g.MovingData?.Keyframes.Any(k => Named(k, name)) == true)
            .ToList();
        return (list, null);
    }

    /// <summary>Propagate Sounds (RED.exe 0x00466980): the source mover's four sounds and volumes.</summary>
    public static MoverPropagationResult PropagateSounds(MoverService movers, Keyframe source)
    {
        (IReadOnlyList<Group> targets, string? refusal) = Targets(movers, source);
        if (refusal is not null)
        {
            return new MoverPropagationResult(0, 0, refusal);
        }

        MovingGroupData from = movers.FindKeyframe(source.Uid)?.Group.MovingData
            ?? throw new InvalidOperationException($"Keyframe {source.Uid} is not on a mover.");
        var snapshot = targets.Select(g => (Data: g.MovingData!, Old: Sounds.Of(g.MovingData!))).ToList();
        Sounds value = Sounds.Of(from);
        Apply(movers.Document, "Propagate sounds",
            () => snapshot.ForEach(s => value.To(s.Data)),
            () => snapshot.ForEach(s => s.Old.To(s.Data)));
        return new MoverPropagationResult(targets.Count, 0, null);
    }

    /// <summary>Propagate Properties (RED.exe 0x00466C20): the chain settings and this keyframe's own settings.</summary>
    public static MoverPropagationResult PropagateProperties(MoverService movers, Keyframe source)
    {
        (IReadOnlyList<Group> targets, string? refusal) = Targets(movers, source);
        if (refusal is not null)
        {
            return new MoverPropagationResult(0, 0, refusal);
        }

        MovingGroupData from = movers.FindKeyframe(source.Uid)?.Group.MovingData
            ?? throw new InvalidOperationException($"Keyframe {source.Uid} is not on a mover.");
        Chain chain = Chain.Of(from);
        KeyTiming timing = KeyTiming.Of(source);
        var chains = targets.Select(g => (Data: g.MovingData!, Old: Chain.Of(g.MovingData!))).ToList();
        var keys = targets
            .SelectMany(g => g.MovingData!.Keyframes)
            .Where(k => Named(k, source.ScriptName))
            .Select(k => (Key: k, Old: KeyTiming.Of(k)))
            .ToList();
        Apply(movers.Document, "Propagate properties",
            () =>
            {
                chains.ForEach(c => chain.To(c.Data));
                keys.ForEach(k => timing.To(k.Key));
            },
            () =>
            {
                chains.ForEach(c => c.Old.To(c.Data));
                keys.ForEach(k => k.Old.To(k.Key));
            });
        return new MoverPropagationResult(targets.Count, keys.Count, null);
    }

    private static bool Named(Keyframe k, string name) =>
        string.Equals(k.ScriptName ?? string.Empty, name, StringComparison.OrdinalIgnoreCase);

    private static void Apply(EditorDocument doc, string description, Action apply, Action revert)
    {
        RflSection? host = null;
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.MovingGroups)
            {
                host = s;
                break;
            }
        }

        doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                apply();
                if (host is not null)
                {
                    host.Dirty = true;
                }

                doc.RefreshObjects();
            },
            () =>
            {
                revert();
                if (host is not null)
                {
                    host.Dirty = true;
                }

                doc.RefreshObjects();
            }));
    }

    private readonly record struct Sounds(string Start, float StartVol, string Loop, float LoopVol, string Stop, float StopVol, string Close, float CloseVol)
    {
        public static Sounds Of(MovingGroupData d) =>
            new(d.StartSound, d.StartVol, d.LoopingSound, d.LoopingVol, d.StopSound, d.StopVol, d.CloseSound, d.CloseVol);

        public void To(MovingGroupData d)
        {
            d.StartSound = Start;
            d.StartVol = StartVol;
            d.LoopingSound = Loop;
            d.LoopingVol = LoopVol;
            d.StopSound = Stop;
            d.StopVol = StopVol;
            d.CloseSound = Close;
            d.CloseVol = CloseVol;
        }
    }

    private readonly record struct Chain(byte Door, byte Rotate, byte Backwards, byte Velocity, byte Orient, byte NoCollide, int Movement, int Start)
    {
        public static Chain Of(MovingGroupData d) =>
            new(d.IsDoor, d.RotateInPlace, d.StartsBackwards, d.UseTravelTimeAsSpeed, d.ForceOrient, d.NoPlayerCollide, d.MovementType, d.StartingKeyframe);

        public void To(MovingGroupData d)
        {
            d.IsDoor = Door;
            d.RotateInPlace = Rotate;
            d.StartsBackwards = Backwards;
            d.UseTravelTimeAsSpeed = Velocity;
            d.ForceOrient = Orient;
            d.NoPlayerCollide = NoCollide;
            d.MovementType = Movement;
            d.StartingKeyframe = Start;
        }
    }

    private readonly record struct KeyTiming(float Pause, float Depart, float Return, float Accel, float Decel, int Event, int Item1, int Item2, float Degrees)
    {
        public static KeyTiming Of(Keyframe k) =>
            new(k.PauseTime, k.DepartTravelTime, k.ReturnTravelTime, k.AccelTime, k.DecelTime, k.EventUid, k.ItemUid1, k.ItemUid2, k.DegreesAboutAxis);

        public void To(Keyframe k)
        {
            k.PauseTime = Pause;
            k.DepartTravelTime = Depart;
            k.ReturnTravelTime = Return;
            k.AccelTime = Accel;
            k.DecelTime = Decel;
            k.EventUid = Event;
            k.ItemUid1 = Item1;
            k.ItemUid2 = Item2;
            k.DegreesAboutAxis = Degrees;
        }
    }
}
