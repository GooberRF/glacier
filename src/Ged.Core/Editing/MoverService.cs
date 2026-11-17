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

    /// <summary>The document this service edits.</summary>
    public EditorDocument Document => _doc;

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
    /// Builds a moving group from the given brush UIDs and object UIDs, seeded with a single gold
    /// start keyframe. The member brushes STAY in the static <c>brushes</c> section (so they remain
    /// normal, fully-editable world brushes) and a second copy of each is added to the <c>movers</c>
    /// section — RED's "stored twice" invariant (see <see cref="Compiler.MoverBrushes"/>), which the
    /// compiler relies on to exclude them from the static fold by UID while RF.exe animates the movers
    /// copy. The gold start keyframe is seeded at the members' rest-pose CENTRE (see
    /// <see cref="MemberBoundsCenter(Group)"/>) — exactly what RED does: its keyframe maker
    /// <c>FUN_00416000</c> (RED.exe @0x00416000) copies the editor's group-pivot slot
    /// <c>this+0x234</c> verbatim into the new keyframe's position (@0x0041607a), and that slot was
    /// just filled by <c>FUN_004267d0</c> (@0x004267d0, its only caller) with the AABB centre of the
    /// group's members. So the keyframe is born lined up with the mover, never lifted and never at the
    /// camera. Returns the new group.
    /// </summary>
    public Group CreateMover(
        IReadOnlyCollection<int> brushUids,
        IReadOnlyCollection<int> objectUids,
        string name = "Mover")
    {
        ArgumentNullException.ThrowIfNull(brushUids);
        ArgumentNullException.ThrowIfNull(objectUids);

        BrushesSection? brushes = BrushesContent();

        // A missing movers / moving_groups section is prepared DETACHED and inserted by the command
        // (removed again on undo), so undoing the first mover on a level leaves the file byte-identical.
        (MoversSection movers, RflSection moversHost, bool moversCreated) = PrepareMovers();
        (GroupsSection groups, RflSection groupsHost, bool groupsCreated) = PrepareMovingGroups();

        // RED lists every mover in the editor groups section too, and only that copy tells RED the brushes
        // are a mover's (see MovingGroupMirror): the same instance goes into both lists.
        (GroupsSection editorGroups, RflSection editorHost, bool editorCreated) = PrepareEditorGroups();

        // The member brushes: they stay in the brushes section; independent copies go into movers.
        var movingBrushes = brushes?.Brushes.Where(b => brushUids.Contains(b.Uid)).ToList() ?? new List<Brush>();
        var moverCopies = movingBrushes.Select(GeometryClone.Deep).ToList();

        // RF builds a mover's collision hull from its stored face planes, so a mover must carry
        // correct RF-convention planes (Normal·X + Offset == 0). Recompute them on the copies: for a
        // normally-authored brush this reproduces the same planes, but it also heals a brush that came
        // from a level an earlier build wrote with an inverted plane sign, so converting it to a mover
        // never carries that defect into the collision geometry.
        foreach (Brush copy in moverCopies)
        {
            GeometryUtil.RecomputeAllPlanes(copy.Geometry);
        }

        var group = new Group { Name = name, IsMoving = 1, MovingData = NewMovingData() };

        // Gather the object members (and their world positions) up front so the start keyframe can be
        // seeded at the member bounds centre and every member transform captured RELATIVE to it.
        var objectMembers = new List<(int Uid, Vec3 Position)>();
        foreach (int uid in objectUids)
        {
            if (_doc.FindByUid(uid) is { } o)
            {
                objectMembers.Add((uid, o.Position));
            }
        }

        // RED seeds the gold start keyframe at the member bounds centre (FUN_004267d0 / FUN_00416000).
        Vec3 origin = BoundsCenter(movingBrushes.Select(b => b.Position).Concat(objectMembers.Select(m => m.Position)));

        // Each moving_group_member_transform stores the member's pose RELATIVE to the start keyframe —
        // rfl.ksy: "transform applied to keyframe to get each member transform", so RF reconstructs
        // member_world = keyframe ∘ member_transform. Verified against RED-authored dmabrupt: e.g.
        // lift002 member brush 10329 (world 5.5,-7,21) stores offset (0.75,-3.75,0) against start
        // keyframe 266 (world 4.75,-3.25,21). Writing the ABSOLUTE member position (the earlier bug)
        // makes RF place the member at keyframe+absolute — displaced by the keyframe position — so the
        // mover's collision sits far from the visible brush and the player is pinned/stuck on contact
        // and the mover appears to have no collision where it is drawn. The keyframe orientation is
        // identity, so the relative position is the plain world offset and a brush's relative rotation
        // is its own rotation.
        var memberTransforms = new List<MovingGroupMemberTransform>();
        foreach (Brush b in movingBrushes)
        {
            group.Brushes.Add(b.Uid);
            memberTransforms.Add(new MovingGroupMemberTransform { Uid = b.Uid, Position = b.Position.Sub(origin), Rotation = b.Rotation });
        }

        foreach ((int uid, Vec3 position) in objectMembers)
        {
            group.Objects.Add(uid);
            memberTransforms.Add(new MovingGroupMemberTransform { Uid = uid, Position = position.Sub(origin), Rotation = Mat3.Identity });
        }

        group.MovingData.MemberTransforms = memberTransforms;

        int startKeyframeUid = _doc.AllocateUid();
        group.MovingData.Keyframes.Add(NewKeyframe(startKeyframeUid, origin, Mat3.Identity));
        group.MovingData.StartingKeyframe = 0;
        group.MovingData.MovementType = 1; // one_way (RED.exe FUN_00416000 @0x0041607+: *(data+0x20)=1)

        // The copies are taken now: snapshot the members so a later edit marks this mover stale.
        CopyTaken taken = SnapshotMembers(movingBrushes);

        _doc.Undo.Execute(new RelayCommand($"Create mover \"{name}\"",
            () =>
            {
                EnsureSectionPresent(moversHost);
                EnsureSectionPresent(groupsHost);
                EnsureSectionPresent(editorHost);
                foreach (Brush copy in moverCopies)
                {
                    if (!movers.Movers.Contains(copy))
                    {
                        movers.Movers.Add(copy);
                    }
                }

                groups.Groups.Add(group);
                if (!editorGroups.Groups.Contains(group))
                {
                    editorGroups.Groups.Add(group);
                }

                taken.Apply(_doc.MoverCopies);
                MarkDirty(moversHost, groupsHost, editorHost);
                _doc.RefreshObjects();
            },
            () =>
            {
                taken.Revert(_doc.MoverCopies);
                groups.Groups.Remove(group);
                editorGroups.Groups.Remove(group);
                foreach (Brush copy in moverCopies)
                {
                    movers.Movers.Remove(copy);
                }

                MarkDirty(moversHost, groupsHost, editorHost);
                if (moversCreated)
                {
                    _doc.Rfl.Sections.Remove(moversHost);
                }

                if (groupsCreated)
                {
                    _doc.Rfl.Sections.Remove(groupsHost);
                }

                if (editorCreated)
                {
                    _doc.Rfl.Sections.Remove(editorHost);
                }

                _doc.RefreshObjects();
            }));

        return group;
    }

    /// <summary>
    /// Refreshes a mover's <c>movers</c>-section copies from its member brushes in the <c>brushes</c>
    /// section, and recomputes every member transform relative to the current start keyframe — the
    /// same derivation <see cref="CreateMover"/> uses (member position − start-keyframe position; a
    /// brush keeps its own rotation, an object member keeps its stored rotation). A copy whose content
    /// still matches its member (transform, flags, life, vertices, face corners, UVs, textures, face
    /// flags) is left untouched, so its baked lighting and build-assigned face ids survive and an
    /// unedited mover syncs to a no-op. A stale copy gets the member's Position / Rotation / Geometry
    /// (a deep clone, planes recomputed) / Flags / Life; a member with no copy gets one. The members
    /// are re-snapshotted in <see cref="EditorDocument.MoverCopies"/> (the copy is current again). One
    /// undo step that restores the previous copies, transforms and snapshots. Returns true when
    /// anything changed.
    /// </summary>
    public bool SyncMemberCopies(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.MovingData is not { } data)
        {
            return false;
        }

        BrushesSection? brushes = BrushesContent();
        var sources = new Dictionary<int, Brush>();
        foreach (Brush b in brushes?.Brushes ?? Enumerable.Empty<Brush>())
        {
            if (group.Brushes.Contains(b.Uid))
            {
                sources.TryAdd(b.Uid, b);
            }
        }

        (MoversSection movers, RflSection moversHost, bool moversCreated) = PrepareMovers();
        var refreshes = new List<(Brush Copy, Brush Fresh, (Vec3 Position, Mat3 Rotation, Geometry Geometry, uint Flags, int Life) Old)>();
        var added = new List<Brush>();
        foreach (int uid in group.Brushes)
        {
            if (!sources.TryGetValue(uid, out Brush? source))
            {
                continue;
            }

            Brush fresh = GeometryClone.Deep(source);
            GeometryUtil.RecomputeAllPlanes(fresh.Geometry);
            Brush? copy = movers.Movers.FirstOrDefault(m => m.Uid == uid);
            if (copy is null)
            {
                added.Add(fresh);
            }
            else if (!SameContent(copy, source))
            {
                refreshes.Add((copy, fresh, (copy.Position, copy.Rotation, copy.Geometry, copy.Flags, copy.Life)));
            }
        }

        // Member transforms, relative to the start keyframe (see CreateMover).
        Vec3 start = data.Keyframes.Count > 0
            ? data.Keyframes[Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1)].Position
            : MemberBoundsCenter(group);
        List<MovingGroupMemberTransform> oldTransforms = data.MemberTransforms;
        var newTransforms = new List<MovingGroupMemberTransform>();
        var seen = new HashSet<int>();
        foreach (MovingGroupMemberTransform t in oldTransforms)
        {
            seen.Add(t.Uid);
            newTransforms.Add(Relative(t.Uid, t.Rotation, sources, start) ?? new MovingGroupMemberTransform { Uid = t.Uid, Position = t.Position, Rotation = t.Rotation });
        }

        foreach (int uid in group.Brushes.Concat(group.Objects))
        {
            if (seen.Add(uid) && Relative(uid, Mat3.Identity, sources, start) is { } t)
            {
                newTransforms.Add(t);
            }
        }

        bool transformsChanged = newTransforms.Count != oldTransforms.Count ||
            newTransforms.Where((t, i) => t.Uid != oldTransforms[i].Uid || !t.Position.Equals(oldTransforms[i].Position) || !t.Rotation.Equals(oldTransforms[i].Rotation)).Any();
        if (refreshes.Count == 0 && added.Count == 0 && !transformsChanged)
        {
            return false;
        }

        RflSection movingHost = MovingGroupsHost();
        CopyTaken taken = SnapshotMembers(sources.Values);
        _doc.Undo.Execute(new RelayCommand($"Sync mover \"{group.Name}\"",
            () =>
            {
                if (moversCreated && !_doc.Rfl.Sections.Contains(moversHost))
                {
                    _doc.Rfl.InsertSection(moversHost);
                }

                taken.Apply(_doc.MoverCopies);

                foreach ((Brush copy, Brush fresh, _) in refreshes)
                {
                    copy.Position = fresh.Position;
                    copy.Rotation = fresh.Rotation;
                    copy.Geometry = fresh.Geometry;
                    copy.Flags = fresh.Flags;
                    copy.Life = fresh.Life;
                }

                movers.Movers.AddRange(added);
                data.MemberTransforms = newTransforms;
                MarkDirty(moversHost, movingHost);
                _doc.RefreshObjects();
            },
            () =>
            {
                taken.Revert(_doc.MoverCopies);
                foreach ((Brush copy, _, var old) in refreshes)
                {
                    copy.Position = old.Position;
                    copy.Rotation = old.Rotation;
                    copy.Geometry = old.Geometry;
                    copy.Flags = old.Flags;
                    copy.Life = old.Life;
                }

                foreach (Brush b in added)
                {
                    movers.Movers.Remove(b);
                }

                data.MemberTransforms = oldTransforms;
                MarkDirty(moversHost, movingHost);
                if (moversCreated)
                {
                    _doc.Rfl.Sections.Remove(moversHost);
                }

                _doc.RefreshObjects();
            }));
        return true;
    }

    /// <summary>A member's transform relative to <paramref name="start"/>, or null when the member no longer exists.</summary>
    private MovingGroupMemberTransform? Relative(int uid, Mat3 objectRotation, Dictionary<int, Brush> brushes, Vec3 start)
    {
        if (brushes.TryGetValue(uid, out Brush? b))
        {
            return new MovingGroupMemberTransform { Uid = uid, Position = b.Position.Sub(start), Rotation = b.Rotation };
        }

        return _doc.FindByUid(uid) is { } o && o.Kind != LevelObjectKind.Mover
            ? new MovingGroupMemberTransform { Uid = uid, Position = o.Position.Sub(start), Rotation = objectRotation }
            : null;
    }

    /// <summary>
    /// True when a movers-section copy still matches its member brush in everything an author edits:
    /// transform, flags (the editor-only geoable mirror excluded), life, vertex positions, face corner
    /// indices and UVs, texture names, face flags and smoothing groups. Build-owned state (planes,
    /// face ids, lightmap surfaces/coordinates, room indices) is ignored.
    /// </summary>
    internal static bool SameContent(Brush copy, Brush member)
    {
        const uint EditorOnly = (uint)BrushFlags.Geoable;
        if (!copy.Position.Equals(member.Position) || !copy.Rotation.Equals(member.Rotation) ||
            (copy.Flags & ~EditorOnly) != (member.Flags & ~EditorOnly) || copy.Life != member.Life)
        {
            return false;
        }

        Geometry a = copy.Geometry;
        Geometry b = member.Geometry;
        if (a.Vertices.Count != b.Vertices.Count || a.Faces.Count != b.Faces.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Vertices.Count; i++)
        {
            if (!a.Vertices[i].Equals(b.Vertices[i]))
            {
                return false;
            }
        }

        for (int f = 0; f < a.Faces.Count; f++)
        {
            Face fa = a.Faces[f];
            Face fb = b.Faces[f];
            if (fa.Flags != fb.Flags || fa.SmoothingGroups != fb.SmoothingGroups || fa.Vertices.Count != fb.Vertices.Count ||
                !string.Equals(TextureName(a, fa), TextureName(b, fb), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (int v = 0; v < fa.Vertices.Count; v++)
            {
                if (fa.Vertices[v].Index != fb.Vertices[v].Index || !fa.Vertices[v].TextureCoords.Equals(fb.Vertices[v].TextureCoords))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? TextureName(Geometry g, Face f) =>
        f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : null;

    /// <summary>
    /// Every mover with a member brush edited after its copy was taken (see
    /// <see cref="MoverCopyTracker"/>), with those member UIDs ascending, in file order.
    /// </summary>
    public IReadOnlyList<(Group Mover, IReadOnlyList<int> TouchedBrushUids)> StaleMovers()
    {
        var result = new List<(Group, IReadOnlyList<int>)>();
        IReadOnlyList<Group> movers = Movers;
        if (movers.Count == 0 || _doc.MoverCopies.TrackedCount == 0)
        {
            return result;
        }

        var brushes = new Dictionary<int, Brush>();
        foreach (Brush b in BrushesContent()?.Brushes ?? Enumerable.Empty<Brush>())
        {
            brushes.TryAdd(b.Uid, b);
        }

        foreach (Group g in movers)
        {
            if (g.MovingData is null)
            {
                continue;
            }

            var touched = g.Brushes.Distinct()
                .Where(uid => brushes.TryGetValue(uid, out Brush? b) && _doc.MoverCopies.IsTouched(b))
                .OrderBy(u => u).ToList();
            if (touched.Count > 0)
            {
                result.Add((g, touched));
            }
        }

        return result;
    }

    /// <summary>
    /// Refreshes (<see cref="SyncMemberCopies"/>) every mover in <see cref="StaleMovers"/> — optionally
    /// only those <paramref name="include"/> accepts — and returns the ones whose copies or member
    /// transforms actually changed. All refreshes are FOLDED into the latest undo step
    /// (<see cref="UndoStack.BeginFoldedTransaction"/>; inside an open transaction, such as a script
    /// run, they join it): undoing that step reverses the edit and the refresh together, so the
    /// member and its copy always undo as a pair and the author never has to undo an extra "sync"
    /// step a save or build added. Movers whose members were not touched are never visited.
    /// </summary>
    public IReadOnlyList<(Group Mover, IReadOnlyList<int> TouchedBrushUids)> SyncStaleCopies(Func<Group, bool>? include = null)
    {
        var stale = StaleMovers().Where(s => include is null || include(s.Mover)).ToList();
        var synced = new List<(Group, IReadOnlyList<int>)>();
        if (stale.Count == 0)
        {
            return synced;
        }

        UndoStack.Transaction tx = _doc.Undo.BeginFoldedTransaction("Sync mover copies");
        try
        {
            foreach ((Group g, IReadOnlyList<int> uids) in stale)
            {
                if (SyncMemberCopies(g))
                {
                    synced.Add((g, uids));
                }
            }
        }
        finally
        {
            tx.Commit();
        }

        return synced;
    }

    /// <summary>
    /// The current shape of a mover's brushes for display (the mover ghost): each member brush from the
    /// <c>brushes</c> section — the shape the author edits — or its <c>movers</c>-section copy when the
    /// member is absent there. Never stale, unlike the copies alone between an edit and the next
    /// save/build.
    /// </summary>
    public IReadOnlyList<Brush> CurrentMemberShapes(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var members = new HashSet<int>(group.Brushes);
        var shapes = new List<Brush>();
        var found = new HashSet<int>();
        foreach (Brush b in BrushesContent()?.Brushes ?? Enumerable.Empty<Brush>())
        {
            if (members.Contains(b.Uid) && found.Add(b.Uid))
            {
                shapes.Add(b);
            }
        }

        shapes.AddRange(MoverBrushes.Where(m => members.Contains(m.Uid) && found.Add(m.Uid)));
        return shapes;
    }

    /// <summary>
    /// Snapshots of <paramref name="members"/> as their copies are taken, plus the snapshots they
    /// replace — applied/reverted inside the owning command so undo keeps the tracker consistent.
    /// </summary>
    private CopyTaken SnapshotMembers(IEnumerable<Brush> members)
    {
        var entries = members
            .Select(b => (b.Uid, New: GeometryClone.Deep(b), Old: _doc.MoverCopies.Snapshot(b.Uid)))
            .ToList();
        return new CopyTaken(entries);
    }

    private sealed class CopyTaken
    {
        private readonly List<(int Uid, Brush New, Brush? Old)> _entries;

        public CopyTaken(List<(int Uid, Brush New, Brush? Old)> entries) => _entries = entries;

        public void Apply(MoverCopyTracker tracker)
        {
            foreach ((int uid, Brush fresh, _) in _entries)
            {
                tracker.SetSnapshot(uid, fresh);
            }
        }

        public void Revert(MoverCopyTracker tracker)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                tracker.SetSnapshot(_entries[i].Uid, _entries[i].Old);
            }
        }
    }

    /// <summary>
    /// RED's keyframe seed point: the CENTRE of the moving group's members' bounding box — the value
    /// RED computes in <c>FUN_004267d0</c> (RED.exe @0x004267d0, whose sole caller is the keyframe
    /// maker <c>FUN_00416000</c>). That routine walks the group's member brushes and objects,
    /// accumulates their AABB, halves <c>(min+max)</c> (the <c>0x3f000000</c> = 0.5f scale
    /// @0x00426977) and writes the centre to the editor's group-pivot slot <c>this+0x234</c>, which
    /// <c>FUN_00416000</c> copies straight into the new keyframe's position (@0x0041607a). So EVERY
    /// keyframe — the gold start and each silver waypoint — is born AT the mover's rest-pose centre and
    /// dragged out from there. It is NOT the camera and NOT a transform (the earlier spec reading was
    /// corrected 2026-07-24). Members contribute their world positions (brush pivots + object
    /// positions), matching RED's member-point AABB; reads live positions and falls back to the
    /// captured rest transforms when a member is only present in the movers section.
    /// </summary>
    public Vec3 MemberBoundsCenter(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var points = new List<Vec3>();

        BrushesSection? brushes = BrushesContent();
        if (brushes is not null && group.Brushes.Count > 0)
        {
            var members = new HashSet<int>(group.Brushes);
            points.AddRange(brushes.Brushes.Where(b => members.Contains(b.Uid)).Select(b => b.Position));
        }

        foreach (int uid in group.Objects)
        {
            if (_doc.FindByUid(uid) is { } o)
            {
                points.Add(o.Position);
            }
        }

        // Movers-only members (no static-brush copy resolved) fall back to the captured rest poses,
        // reconstructed as start-keyframe ∘ member-offset (member transforms are relative — see
        // CreateMover).
        if (points.Count == 0 && group.MovingData is { Keyframes.Count: > 0 } data)
        {
            Vec3 start = data.Keyframes[Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1)].Position;
            points.AddRange(data.MemberTransforms.Select(m => start.Add(m.Position)));
        }

        return BoundsCenter(points);
    }

    /// <summary>
    /// A moving-group data block seeded with RED.exe's fresh-mover defaults (verified against
    /// <c>FUN_00416000</c> @0x00416000, the RED keyframe maker that allocates the moving-data block):
    /// movement type <b>one_way</b> (<c>*(data+0x20)=1</c>) and all four sound volumes <b>1.0</b>
    /// (<c>0x3f800000</c> written to <c>+0x30/+0x3c/+0x48/+0x54</c>). The flag bytes (is_door …
    /// no_player_collide) default to 0 exactly as RED zeroes them. Writing 0-volume movers (the model
    /// default) would silence a mover's sounds relative to a RED-authored one.
    /// </summary>
    private static MovingGroupData NewMovingData() => new()
    {
        MovementType = 1,
        StartVol = 1f,
        LoopingVol = 1f,
        StopVol = 1f,
        CloseVol = 1f,
    };

    /// <summary>
    /// A keyframe seeded with RED.exe's fresh-keyframe defaults, verified against RED's keyframe
    /// constructor <c>FUN_00452770</c> (RED.exe @0x00452770; the 0xbc-byte object the keyframe maker
    /// <c>FUN_00416000</c> allocates). Its fields sit in RFL keyframe order after the script name:
    /// <c>+0x98</c> pause = 0, <c>+0x9c</c> depart (travel time to next) = <b>1.0</b> and
    /// <c>+0xa0</c> return travel = <b>1.0</b> (both <c>0x3f800000</c>), <c>+0xa4</c>/<c>+0xa8</c>
    /// accel/decel = 0, <c>+0xac..+0xb4</c> event / item1 / item2 UIDs = <b>-1</b>
    /// (<c>0xffffffff</c>), <c>+0xb8</c> degrees about axis = 0. The -1 sentinels also match RF.exe's
    /// read defaults (<c>FUN_00463820</c>); the model default of 0 would read as triggering UID 0, and
    /// 0-second travel times would make a fresh mover snap instead of travel.
    /// </summary>
    private static Keyframe NewKeyframe(int uid, Vec3 position, Mat3 rotation) => new()
    {
        Uid = uid,
        Position = position,
        Rotation = rotation,
        ScriptName = "Keyframe", // FUN_00452770 names every new keyframe "Keyframe" (s_Keyframe_005793b8 @0x005793b8)
        DepartTravelTime = 1f,
        ReturnTravelTime = 1f,
        EventUid = -1,
        ItemUid1 = -1,
        ItemUid2 = -1,
    };

    /// <summary>The centre of the axis-aligned bounding box of a set of points, or the origin if empty.</summary>
    private static Vec3 BoundsCenter(IEnumerable<Vec3> points)
    {
        bool any = false;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        foreach (Vec3 p in points)
        {
            any = true;
            minX = MathF.Min(minX, p.X);
            minY = MathF.Min(minY, p.Y);
            minZ = MathF.Min(minZ, p.Z);
            maxX = MathF.Max(maxX, p.X);
            maxY = MathF.Max(maxY, p.Y);
            maxZ = MathF.Max(maxZ, p.Z);
        }

        return any
            ? new Vec3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f)
            : Vec3.Zero;
    }

    /// <summary>
    /// Appends a keyframe (a position/orientation waypoint) to a mover. Re-projects the level objects
    /// so the new keyframe is IMMEDIATELY a selectable/draggable world object (not a phantom billboard)
    /// — mirroring <see cref="CreateMover"/> / <see cref="CutsceneService.AddNode"/>.
    /// </summary>
    public Keyframe AddKeyframe(Group group, Vec3 position, Mat3 rotation)
    {
        ArgumentNullException.ThrowIfNull(group);
        MovingGroupData data = group.MovingData ??= NewMovingData();
        var keyframe = NewKeyframe(_doc.AllocateUid(), position, rotation);
        RflSection host = MovingGroupsHost();
        _doc.Undo.Execute(new RelayCommand("Add keyframe",
            () => { data.Keyframes.Add(keyframe); host.Dirty = true; _doc.RefreshObjects(); _doc.NotifyLinksChanged(); },
            () => { data.Keyframes.Remove(keyframe); host.Dirty = true; _doc.RefreshObjects(); _doc.NotifyLinksChanged(); }));
        return keyframe;
    }

    /// <summary>
    /// Removes a keyframe from a mover. RED keeps at least one keyframe for a live mover; deleting the
    /// LAST keyframe therefore dissolves the mover back to static (born-with-its-first-keyframe
    /// symmetry — see <see cref="DissolveMover"/>). Deleting a keyframe never touches the member brush.
    /// Re-projects so the keyframe object disappears from the outliner/pick registry at once.
    /// </summary>
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

        // Last keyframe → the mover has no path left; dissolve it back to static rather than leave an
        // invalid 0-keyframe moving group behind.
        if (data.Keyframes.Count <= 1)
        {
            DissolveMover(group);
            return;
        }

        RflSection host = MovingGroupsHost();
        int oldStart = data.StartingKeyframe;
        int newStart = Math.Clamp(oldStart, 0, data.Keyframes.Count - 2);
        _doc.Undo.Execute(new RelayCommand("Remove keyframe",
            () => { data.Keyframes.Remove(keyframe); data.StartingKeyframe = newStart; host.Dirty = true; _doc.RefreshObjects(); _doc.NotifyLinksChanged(); },
            () => { data.Keyframes.Insert(Math.Clamp(index, 0, data.Keyframes.Count), keyframe); data.StartingKeyframe = oldStart; host.Dirty = true; _doc.RefreshObjects(); _doc.NotifyLinksChanged(); }));
    }

    /// <summary>
    /// Dissolves a moving group: removes its <c>movers</c>-section copies and drops the moving group,
    /// so its member brushes revert to ordinary, fully-editable static world brushes (they were kept in
    /// the <c>brushes</c> section all along — the stored-twice model). Discards the keyframes / member
    /// transforms with the group. One undo entry (RED §A.5).
    /// </summary>
    public void DissolveMover(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        GroupsSection? mg = MovingGroupsContent();
        if (mg is null || !mg.Groups.Contains(group))
        {
            return;
        }

        // A level whose movers section is absent has no copies to remove — never create it here.
        RflSection? moversHost = FindHost(SectionType.Movers);
        List<Brush> movers = (moversHost?.Content as MoversSection)?.Movers ?? new List<Brush>();
        RflSection movingHost = MovingGroupsHost();
        int groupIndex = mg.Groups.IndexOf(group);

        // The mover's editor-groups copy goes with it (see MovingGroupMirror), or RED would keep the mover.
        RflSection? editorHost = FindHost(SectionType.Groups);
        List<Group>? editorList = (editorHost?.Content as GroupsSection)?.Groups;
        int editorIndex = editorList?.IndexOf(group) ?? -1;
        var memberUids = new HashSet<int>(group.Brushes);
        var removedMovers = movers
            .Select((m, i) => (Brush: m, Index: i))
            .Where(t => memberUids.Contains(t.Brush.Uid))
            .OrderByDescending(t => t.Index)
            .ToList();

        _doc.Undo.Execute(new RelayCommand($"Dissolve mover \"{group.Name}\"",
            () =>
            {
                foreach (var (b, _) in removedMovers)
                {
                    movers.Remove(b);
                }

                mg.Groups.Remove(group);
                if (editorIndex >= 0)
                {
                    editorList!.Remove(group);
                    editorHost!.Dirty = true;
                }

                MarkDirty(movingHost);
                if (moversHost is not null)
                {
                    moversHost.Dirty = true;
                }

                _doc.RefreshObjects();
                _doc.NotifyLinksChanged();
            },
            () =>
            {
                mg.Groups.Insert(Math.Clamp(groupIndex, 0, mg.Groups.Count), group);
                if (editorIndex >= 0)
                {
                    editorList!.Insert(Math.Clamp(editorIndex, 0, editorList.Count), group);
                    editorHost!.Dirty = true;
                }

                foreach (var (b, index) in Enumerable.Reverse(removedMovers))
                {
                    movers.Insert(Math.Clamp(index, 0, movers.Count), b);
                }

                MarkDirty(movingHost);
                if (moversHost is not null)
                {
                    moversHost.Dirty = true;
                }

                _doc.RefreshObjects();
                _doc.NotifyLinksChanged();
            }));
    }

    /// <summary>
    /// Repairs a level authored with Glacier's old, broken mover shape (member brushes MOVED into
    /// <c>movers</c> only, absent from the <c>brushes</c> section) by re-adding an editable world copy
    /// of every such brush to the <c>brushes</c> section — restoring RED's stored-twice invariant so
    /// the brush is selectable/editable again and the compiler excludes it from the static fold by UID.
    /// Not undoable (a load-time data fix); marks the document dirty so a resave persists it. A
    /// correctly stored-twice level (every corpus / RED level, and every Glacier mover authored after
    /// this fix) has each mover UID already present in <c>brushes</c>, so nothing is added and the file
    /// stays byte-identical. Returns the number of brushes restored.
    /// </summary>
    public int RepairStoredTwiceInvariant()
    {
        MoversSection? movers = MoversContent();
        if (movers is null || movers.Movers.Count == 0)
        {
            return 0;
        }

        BrushesSection? brushes = BrushesContent();
        var present = new HashSet<int>(brushes?.Brushes.Select(b => b.Uid) ?? Enumerable.Empty<int>());
        var missing = movers.Movers.Where(m => !present.Contains(m.Uid)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.Brushes, () => new BrushesSection());
        var bs = (BrushesSection)host.Content!;
        foreach (Brush m in missing)
        {
            bs.Brushes.Add(GeometryClone.Deep(m));
        }

        host.Dirty = true;
        _doc.MarkDirty();
        _doc.RefreshObjects();

        // The restored members ARE their copies: snapshot them so only a later edit marks them stale.
        _doc.MoverCopies.TrackUntracked(_doc.Rfl);
        return missing.Count;
    }

    /// <summary>
    /// Repairs a level whose movers are missing from (or out of date in) the editor <c>groups</c> section —
    /// every level an earlier Glacier saved with a mover, and any RED level whose mover Glacier then edited
    /// — by rewriting that copy from <c>moving_groups</c> (<see cref="MovingGroupMirror.Reconcile"/>).
    /// Without it RED and Alpine RED do not see the mover and rebuild its brushes into the static world.
    /// Not undoable (a load-time data fix, like <see cref="RepairStoredTwiceInvariant"/>); marks the
    /// document dirty so a resave persists it. A RED-authored level already mirrors its movers, so nothing
    /// changes and the file stays byte-identical. Returns the number of movers repaired.
    /// </summary>
    public int RepairEditorGroupMirror()
    {
        if (!MovingGroupMirror.NeedsRepair(_doc.Rfl))
        {
            return 0;
        }

        int repaired = MovingGroupMirror.Reconcile(_doc.Rfl);
        _doc.MarkDirty();
        _doc.RefreshObjects();
        return repaired;
    }

    /// <summary>Applies a reversible edit to a keyframe's fields (used by the Keyframe Properties inspector).</summary>
    public void EditKeyframe(Keyframe keyframe, string description, Action<Keyframe> apply, Action<Keyframe> revert, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        RflSection host = MovingGroupsHost();
        _doc.Undo.Execute(new RelayCommand(description,
            () => { apply(keyframe); host.Dirty = true; _doc.RefreshObjects(); },
            () => { revert(keyframe); host.Dirty = true; _doc.RefreshObjects(); },
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

        // The section (when absent) is created inside the command, so undoing the first Hold Open on
        // a stock level removes it again and the file stays byte-identical.
        AlpinePropertiesEdit edit = AlpinePropertiesEdit.Begin(_doc);
        AlpineLevelPropertiesSection alp = edit.Content;
        int uid = first.Uid;
        bool has = alp.HoldOpenKeyframeUids.Contains(uid);
        if (has == holdOpen)
        {
            return;
        }

        _doc.Undo.Execute(new RelayCommand(holdOpen ? "Enable Hold Open" : "Disable Hold Open",
            () =>
            {
                edit.Attach();
                if (holdOpen)
                {
                    alp.HoldOpenKeyframeUids.Add(uid);
                }
                else
                {
                    alp.HoldOpenKeyframeUids.Remove(uid);
                }

                _doc.RefreshObjects();
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

                edit.Detach();
                _doc.RefreshObjects();
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

    /// <summary>The movers section, or a new DETACHED one (<c>Created</c>) the caller's command inserts.</summary>
    private (MoversSection Content, RflSection Host, bool Created) PrepareMovers()
    {
        RflSection host = _doc.Rfl.FindOrPrepareSection(SectionType.Movers, () => new MoversSection(), out bool created);
        return ((MoversSection)host.Content!, host, created);
    }

    /// <summary>The moving_groups section, or a new DETACHED one (<c>Created</c>) the caller's command inserts.</summary>
    private (GroupsSection Content, RflSection Host, bool Created) PrepareMovingGroups()
    {
        RflSection host = _doc.Rfl.FindOrPrepareSection(SectionType.MovingGroups, () => new GroupsSection(SectionType.MovingGroups), out bool created);
        return ((GroupsSection)host.Content!, host, created);
    }

    /// <summary>The editor groups section (0x3000000), or a new DETACHED one (<c>Created</c>) the caller's command inserts.</summary>
    private (GroupsSection Content, RflSection Host, bool Created) PrepareEditorGroups()
    {
        RflSection host = _doc.Rfl.FindOrPrepareSection(SectionType.Groups, () => new GroupsSection(SectionType.Groups), out bool created);
        return ((GroupsSection)host.Content!, host, created);
    }

    private (GroupsSection Content, RflSection Host) EnsureMovingGroups()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.MovingGroups, () => new GroupsSection(SectionType.MovingGroups));
        return ((GroupsSection)host.Content!, host);
    }

    private RflSection? FindHost(SectionType type)
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)type)
            {
                return s;
            }
        }

        return null;
    }

    private void EnsureSectionPresent(RflSection host)
    {
        if (!_doc.Rfl.Sections.Contains(host))
        {
            _doc.Rfl.InsertSection(host);
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
}
