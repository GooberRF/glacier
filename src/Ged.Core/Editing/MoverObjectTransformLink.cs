using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Keeps a mover's two copies together when the WHOLE mover object is moved or rotated. A mover brush
/// is stored twice — its member brush in <c>brushes</c> and its copy in <c>movers</c> (see
/// <see cref="Compiler.MoverBrushes"/>) — and the Object-mode <see cref="LevelObjectKind.Mover"/>
/// handle is the movers-section copy. Every whole-object transform path (gizmo move / rotate,
/// nudge, set coordinates, snap, staple, morph, reorient, Lua <c>obj:set_pos</c> / <c>move</c> /
/// rotation setters, query moves, the automation bridge) edits that copy's position or rotation
/// through an undoable command, so this runs as the document's <see cref="UndoStack.CommandRunner"/>
/// and, after a command has changed a copy's transform while leaving its member brush alone, carries
/// the member along by the same rigid motion — in the SAME undo step:
/// <list type="bullet">
/// <item>the member brush gets the copy's new pose (exactly, when it sat at the copy's old pose —
/// the normal, in-sync case — otherwise by the same rigid delta, so a member already edited in Brush
/// mode keeps that edit);</item>
/// <item>the moving group's member transform for that brush is recomputed relative to the start
/// keyframe exactly as <see cref="MoverService.SyncMemberCopies"/> derives it (member position −
/// start-keyframe position; the brush's own rotation), so the game places the mover where it was
/// moved to;</item>
/// <item>the member's copy-taken snapshot (<see cref="MoverCopyTracker"/>) moves by the same delta,
/// so an untouched member stays untouched (its copy is not resynced) and a member with pending
/// Brush-mode edits stays stale (its copy still gets them on the next sync).</item>
/// </list>
/// Keyframes are separate path objects and stay where they are — the same outcome as moving the
/// member brush in Brush mode and syncing. Undo restores the member pose, the member transform and the
/// snapshot to their exact previous values; redo re-applies the exact post-move values. A command
/// that moves the member itself (a sync, a prefab/unit transform that carries both) or moves the copy
/// onto the member's current pose is left alone.
/// </summary>
public sealed class MoverObjectTransformLink
{
    private readonly EditorDocument _doc;

    public MoverObjectTransformLink(EditorDocument doc)
    {
        _doc = doc;
    }

    /// <summary>Runs <paramref name="command"/> once and returns the command to record (see the class summary).</summary>
    public IDocumentCommand Run(IDocumentCommand command)
    {
        MoversSection? movers = Parsed<MoversSection>(out _);
        if (movers is null || movers.Movers.Count == 0)
        {
            command.Do();
            return command;
        }

        var copiesBefore = movers.Movers.Select(m => (Copy: m, m.Position, m.Rotation)).ToList();
        Dictionary<int, Brush> members = MemberBrushes(copiesBefore.Select(c => c.Copy.Uid));
        var membersBefore = members.ToDictionary(kv => kv.Key, kv => (kv.Value.Position, kv.Value.Rotation));

        command.Do();

        // A command that swapped the movers section out (a build) moved no mover object.
        if (!ReferenceEquals(Parsed<MoversSection>(out _), movers))
        {
            return command;
        }

        List<Carry>? carries = null;
        foreach ((Brush copy, Vec3 oldPos, Mat3 oldRot) in copiesBefore)
        {
            if ((copy.Position.Equals(oldPos) && copy.Rotation.Equals(oldRot)) ||
                !members.TryGetValue(copy.Uid, out Brush? member) || !movers.Movers.Contains(copy))
            {
                continue;
            }

            (Vec3 memberPos, Mat3 memberRot) = membersBefore[copy.Uid];
            bool memberMoved = !member.Position.Equals(memberPos) || !member.Rotation.Equals(memberRot);
            bool alreadyThere = member.Position.Equals(copy.Position) && member.Rotation.Equals(copy.Rotation);
            if (memberMoved || alreadyThere)
            {
                continue;
            }

            (carries ??= new List<Carry>()).Add(new Carry(copy.Uid, member, oldPos, oldRot, copy.Position, copy.Rotation));
        }

        if (carries is null)
        {
            return command;
        }

        LinkedState before = Capture(carries);
        foreach (Carry c in carries)
        {
            (c.Member.Position, c.Member.Rotation) = c.Apply(c.Member.Position, c.Member.Rotation);
            if (_doc.MoverCopies.Snapshot(c.Uid) is { } snap)
            {
                Brush moved = GeometryClone.Deep(snap);
                (moved.Position, moved.Rotation) = c.Apply(snap.Position, snap.Rotation);
                _doc.MoverCopies.SetSnapshot(c.Uid, moved);
            }

            if (MemberTransform(c.Uid, out MovingGroupMemberTransform? t, out Vec3 start))
            {
                t.Position = c.Member.Position.Sub(start);
                t.Rotation = c.Member.Rotation;
            }
        }

        LinkedState after = Capture(carries);
        MarkHostsDirty();
        return new LinkedCommand(command,
            () => { after.Restore(_doc.MoverCopies); MarkHostsDirty(); },
            () => { before.Restore(_doc.MoverCopies); MarkHostsDirty(); });
    }

    /// <summary>One copy whose transform the command changed, and the rigid motion that carries its member.</summary>
    private sealed class Carry
    {
        public Carry(int uid, Brush member, Vec3 oldPos, Mat3 oldRot, Vec3 newPos, Mat3 newRot)
        {
            Uid = uid;
            Member = member;
            OldPos = oldPos;
            OldRot = oldRot;
            NewPos = newPos;
            NewRot = newRot;
        }

        public int Uid { get; }

        public Brush Member { get; }

        public Vec3 OldPos { get; }

        public Mat3 OldRot { get; }

        public Vec3 NewPos { get; }

        public Mat3 NewRot { get; }

        /// <summary>
        /// The pose <paramref name="pos"/>/<paramref name="rot"/> moved by the copy's motion: a pose at
        /// the copy's old pose lands EXACTLY on its new one (no float round-trip), anything else is
        /// moved by the same rigid delta (rotation about the copy's old position, then translation).
        /// </summary>
        public (Vec3 Position, Mat3 Rotation) Apply(Vec3 pos, Mat3 rot)
        {
            if (pos.Equals(OldPos) && rot.Equals(OldRot))
            {
                return (NewPos, NewRot);
            }

            if (NewRot.Equals(OldRot))
            {
                return (pos.Add(NewPos.Sub(OldPos)), rot);
            }

            Mat3 delta = Mat3Math.Compose(NewRot, OldRot.Transpose());
            return (NewPos.Add(delta.Transform(pos.Sub(OldPos))), Mat3Math.Compose(delta, rot).Orthonormalize());
        }
    }

    /// <summary>The exact member poses, member transforms and snapshots at one moment, restorable verbatim.</summary>
    private sealed class LinkedState
    {
        public List<(Brush Member, Vec3 Position, Mat3 Rotation)> Members { get; } = new();

        public List<(MovingGroupMemberTransform Transform, Vec3 Position, Mat3 Rotation)> Transforms { get; } = new();

        public List<(int Uid, Brush? Snapshot)> Snapshots { get; } = new();

        public void Restore(MoverCopyTracker tracker)
        {
            foreach ((Brush b, Vec3 p, Mat3 r) in Members)
            {
                b.Position = p;
                b.Rotation = r;
            }

            foreach ((MovingGroupMemberTransform t, Vec3 p, Mat3 r) in Transforms)
            {
                t.Position = p;
                t.Rotation = r;
            }

            foreach ((int uid, Brush? snap) in Snapshots)
            {
                tracker.SetSnapshot(uid, snap);
            }
        }
    }

    private LinkedState Capture(List<Carry> carries)
    {
        var state = new LinkedState();
        foreach (Carry c in carries)
        {
            state.Members.Add((c.Member, c.Member.Position, c.Member.Rotation));
            state.Snapshots.Add((c.Uid, _doc.MoverCopies.Snapshot(c.Uid)));
            if (MemberTransform(c.Uid, out MovingGroupMemberTransform? t, out _))
            {
                state.Transforms.Add((t, t.Position, t.Rotation));
            }
        }

        return state;
    }

    /// <summary>
    /// The member transform of brush <paramref name="uid"/> in its moving group, and that group's start
    /// keyframe position (the origin member transforms are relative to). False when the brush is in no
    /// moving group, the group has no keyframes, or it carries no transform for the brush.
    /// </summary>
    private bool MemberTransform(int uid, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MovingGroupMemberTransform? transform, out Vec3 start)
    {
        transform = null;
        start = default;
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId != (uint)SectionType.MovingGroups || s.Content is not GroupsSection groups)
            {
                continue;
            }

            foreach (Group g in groups.Groups)
            {
                if (g.IsMoving == 0 || g.MovingData is not { Keyframes.Count: > 0 } data || !g.Brushes.Contains(uid))
                {
                    continue;
                }

                transform = data.MemberTransforms.FirstOrDefault(t => t.Uid == uid);
                start = data.Keyframes[System.Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1)].Position;
                return transform is not null;
            }
        }

        return false;
    }

    /// <summary>The first brushes-section brush for each of <paramref name="uids"/> (the member brushes).</summary>
    private Dictionary<int, Brush> MemberBrushes(IEnumerable<int> uids)
    {
        var wanted = new HashSet<int>(uids);
        var found = new Dictionary<int, Brush>();
        if (Parsed<BrushesSection>(out _) is not { } brushes)
        {
            return found;
        }

        foreach (Brush b in brushes.Brushes)
        {
            if (wanted.Contains(b.Uid) && found.TryAdd(b.Uid, b) && found.Count == wanted.Count)
            {
                break;
            }
        }

        return found;
    }

    private void MarkHostsDirty()
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is BrushesSection || s.TypeId == (uint)SectionType.MovingGroups)
            {
                s.Dirty = true;
            }
        }
    }

    /// <summary>
    /// The first already-parsed section of content type <typeparamref name="T"/> (an unparsed section
    /// cannot have been edited, so nothing is forced to parse on every command).
    /// </summary>
    private T? Parsed<T>(out RflSection? host)
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is T t)
            {
                host = s;
                return t;
            }
        }

        host = null;
        return null;
    }

    /// <summary>The original command plus the linked member state it moved, applied and reversed together.</summary>
    private sealed class LinkedCommand : IDocumentCommand
    {
        private readonly IDocumentCommand _inner;
        private readonly System.Action _applyLinked;
        private readonly System.Action _revertLinked;

        public LinkedCommand(IDocumentCommand inner, System.Action applyLinked, System.Action revertLinked)
        {
            _inner = inner;
            _applyLinked = applyLinked;
            _revertLinked = revertLinked;
        }

        public string Description => _inner.Description;

        public string? CoalesceKey => _inner.CoalesceKey;

        public void Do()
        {
            _inner.Do();
            _applyLinked();
        }

        public void Undo()
        {
            _revertLinked();
            _inner.Undo();
        }
    }
}
