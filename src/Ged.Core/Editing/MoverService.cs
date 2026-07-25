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
        (MoversSection movers, RflSection moversHost) = EnsureMovers();
        (GroupsSection groups, RflSection groupsHost) = EnsureMovingGroups();

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

        _doc.Undo.Execute(new RelayCommand($"Create mover \"{name}\"",
            () =>
            {
                EnsureSectionPresent(moversHost);
                EnsureSectionPresent(groupsHost);
                foreach (Brush copy in moverCopies)
                {
                    if (!movers.Movers.Contains(copy))
                    {
                        movers.Movers.Add(copy);
                    }
                }

                groups.Groups.Add(group);
                MarkDirty(moversHost, groupsHost);
                _doc.RefreshObjects();
            },
            () =>
            {
                groups.Groups.Remove(group);
                foreach (Brush copy in moverCopies)
                {
                    movers.Movers.Remove(copy);
                }

                MarkDirty(moversHost, groupsHost);
                _doc.RefreshObjects();
            }));

        return group;
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
    /// A keyframe seeded with RED's "no triggered event / no items" sentinels: <c>event_uid</c>,
    /// <c>item_uid1</c> and <c>item_uid2</c> all <b>-1</b>, matching every RED-authored keyframe and
    /// RF.exe's own read defaults (<c>FUN_00463820</c> reads them with default <c>0xffffffff</c>).
    /// The model default of 0 would make a fresh keyframe read as if it triggered the object with
    /// UID 0.
    /// </summary>
    private static Keyframe NewKeyframe(int uid, Vec3 position, Mat3 rotation) => new()
    {
        Uid = uid,
        Position = position,
        Rotation = rotation,
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

        (MoversSection movers, RflSection moversHost) = EnsureMovers();
        RflSection movingHost = MovingGroupsHost();
        int groupIndex = mg.Groups.IndexOf(group);
        var memberUids = new HashSet<int>(group.Brushes);
        var removedMovers = movers.Movers
            .Select((m, i) => (Brush: m, Index: i))
            .Where(t => memberUids.Contains(t.Brush.Uid))
            .OrderByDescending(t => t.Index)
            .ToList();

        _doc.Undo.Execute(new RelayCommand($"Dissolve mover \"{group.Name}\"",
            () =>
            {
                foreach (var (b, _) in removedMovers)
                {
                    movers.Movers.Remove(b);
                }

                mg.Groups.Remove(group);
                MarkDirty(moversHost, movingHost);
                _doc.RefreshObjects();
                _doc.NotifyLinksChanged();
            },
            () =>
            {
                mg.Groups.Insert(Math.Clamp(groupIndex, 0, mg.Groups.Count), group);
                foreach (var (b, index) in Enumerable.Reverse(removedMovers))
                {
                    movers.Movers.Insert(Math.Clamp(index, 0, movers.Movers.Count), b);
                }

                MarkDirty(moversHost, movingHost);
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
        return missing.Count;
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

                host.Dirty = true;
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
