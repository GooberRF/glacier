using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// RED-parity mover / group / keyframe workflow — the tester's nine reports encoded as headless
/// gates (docs/research/red-mover-workflow-spec.md, Part B). Covers: the phantom fix (keyframes are
/// immediately selectable objects), keyframe-delete safety (never removes the brush), the keyframe
/// floor (last-keyframe delete dissolves to static), member-deletion scrub + empty-group pruning, the
/// stored-twice invariant + its load-time repair, moving-group Dissolve, group-lock enforcement, and
/// the group member-click escalation (unit selection).
/// </summary>
public sealed class MoverWorkflowTests
{
    private static EditorDocument NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "mw.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static (EditorDocument Doc, BrushEditor Be, MoverService Mv, int B) SingleBrushMover()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int b = be.CreateBrush(new BrushCreateParams { Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 0, 0), Mat3.Identity);
        var mv = new MoverService(doc);
        mv.CreateMover(new[] { b }, Array.Empty<int>(), "Lift");
        return (doc, be, mv, b);
    }

    private static BrushesSection Brushes(EditorDocument doc) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().First();

    private static MoversSection? Movers(EditorDocument doc) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<MoversSection>().FirstOrDefault();

    private static GroupsSection? MovingGroups(EditorDocument doc) =>
        doc.Rfl.Sections.Where(s => s.TypeId == (uint)SectionType.MovingGroups)
            .Select(s => (GroupsSection?)s.Content).FirstOrDefault();

    // (7) Add Keyframe drops a keyframe that is IMMEDIATELY a selectable object, not a phantom.
    [Fact]
    public void Added_Keyframe_Is_Immediately_A_Resolvable_Selectable_Object()
    {
        var (doc, _, mv, _) = SingleBrushMover();
        Group group = mv.Movers.Single();

        Keyframe kf = mv.AddKeyframe(group, new Vec3(0, 8, 0), Mat3.Identity);

        LevelObject? o = doc.FindByUid(kf.Uid);
        Assert.NotNull(o);
        Assert.Equal(LevelObjectKind.Keyframe, o!.Kind);
        Assert.Contains(doc.Objects, x => x.Uid == kf.Uid && x.Kind == LevelObjectKind.Keyframe);
    }

    // (3) Deleting a keyframe removes only the keyframe path node — never the member brush.
    [Fact]
    public void Deleting_A_Keyframe_Never_Deletes_The_Brush()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        Group group = mv.Movers.Single();
        Keyframe extra = mv.AddKeyframe(group, new Vec3(0, 8, 0), Mat3.Identity);

        mv.RemoveKeyframe(group, extra);

        Assert.DoesNotContain(group.MovingData!.Keyframes, k => k.Uid == extra.Uid);
        Assert.NotNull(be.FindBrush(b));                 // world brush untouched
        Assert.Contains(Movers(doc)!.Movers, m => m.Uid == b); // mover copy untouched
    }

    // (3) Deleting a keyframe OBJECT via the generic delete path is also safe (routes through the floor).
    [Fact]
    public void Deleting_A_Keyframe_Object_Via_DeleteSelection_Is_Safe()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        Group group = mv.Movers.Single();
        Keyframe extra = mv.AddKeyframe(group, new Vec3(0, 8, 0), Mat3.Identity);
        var router = new SelectionRouter(() => doc, () => be, () => SelectKinds.Objects);

        router.SelectObject(doc.FindByUid(extra.Uid)!);
        doc.DeleteSelection();

        Assert.DoesNotContain(group.MovingData!.Keyframes, k => k.Uid == extra.Uid);
        Assert.NotNull(be.FindBrush(b));
        Assert.Single(group.MovingData.Keyframes); // still the gold start
    }

    // (3/5) Deleting the LAST keyframe dissolves the mover back to static (RED keeps >= 1 for a live mover).
    [Fact]
    public void Removing_The_Last_Keyframe_Dissolves_The_Mover_To_Static()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        Group group = mv.Movers.Single();
        Keyframe start = group.MovingData!.Keyframes.Single();

        mv.RemoveKeyframe(group, start);

        Assert.Empty(mv.Movers);                          // moving group gone
        Assert.Empty(Movers(doc)!.Movers);                // no orphan mover copy
        Assert.NotNull(be.FindBrush(b));                  // member survives as a world brush
    }

    // (1/4/9) After Create Mover the member brush is a normal, selectable world brush again.
    [Fact]
    public void Mover_Brush_Is_Selectable_In_Brush_Mode()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        var router = new SelectionRouter(() => doc, () => be, () => SelectKinds.Brushes);

        Assert.NotNull(be.FindBrush(b));                  // present in the editable brushes section
        Assert.True(router.SelectBrush(b));               // and the router selects it
        Assert.Contains(b, be.SelectedBrushes);
    }

    // (2) Dissolving a moving group returns everything to editable static geometry.
    [Fact]
    public void Dissolve_Returns_Members_To_Editable_Static()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        Group group = mv.Movers.Single();

        mv.DissolveMover(group);

        Assert.Empty(mv.Movers);
        Assert.Empty(Movers(doc)!.Movers);
        Assert.NotNull(be.FindBrush(b));                  // world brush stays, now unbound
    }

    // (5/6) Deleting a mover's member brush scrubs the group and prunes it when it empties.
    [Fact]
    public void Deleting_The_Sole_Member_Brush_Prunes_The_Moving_Group()
    {
        var (doc, be, mv, b) = SingleBrushMover();

        be.DeleteBrushes(new[] { b });

        Assert.Empty(mv.Movers);                          // no orphan empty moving group
        Assert.Empty(Movers(doc)!.Movers);                // its mover copy went too
        Assert.Null(be.FindBrush(b));
    }

    [Fact]
    public void Deleting_One_Of_Two_Member_Brushes_Scrubs_But_Keeps_The_Group()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int b1 = be.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        int b2 = be.CreateBrush(new BrushCreateParams(), new Vec3(4, 0, 0), Mat3.Identity);
        var mv = new MoverService(doc);
        Group group = mv.CreateMover(new[] { b1, b2 }, Array.Empty<int>(), "Elevator");

        be.DeleteBrushes(new[] { b1 });

        Assert.Single(mv.Movers);
        Assert.DoesNotContain(b1, group.Brushes);
        Assert.Contains(b2, group.Brushes);
        Assert.DoesNotContain(group.MovingData!.MemberTransforms, t => t.Uid == b1);
        Assert.DoesNotContain(Movers(doc)!.Movers, m => m.Uid == b1);
        Assert.Contains(Movers(doc)!.Movers, m => m.Uid == b2);
    }

    // (6) No operation sequence leaves a 0-keyframe or empty moving group behind.
    [Fact]
    public void No_Zero_Keyframe_Or_Empty_Group_Survives()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        Group group = mv.Movers.Single();
        mv.AddKeyframe(group, new Vec3(0, 8, 0), Mat3.Identity);

        be.DeleteBrushes(new[] { b }); // delete the only member

        Assert.All(MovingGroups(doc)?.Groups ?? new List<Group>(), g =>
        {
            Assert.True(g.MovingData is { Keyframes.Count: > 0 });
            Assert.True(g.Brushes.Count + g.Objects.Count > 0);
        });
        Assert.Empty(mv.Movers);
    }

    // Undo restores a delete-driven prune exactly (the maintenance folds into the same undo entry).
    [Fact]
    public void Undo_Restores_A_Pruned_Mover_Exactly()
    {
        var (doc, be, mv, b) = SingleBrushMover();
        Group group = mv.Movers.Single();
        int keyframes = group.MovingData!.Keyframes.Count;

        be.DeleteBrushes(new[] { b });
        Assert.Empty(mv.Movers);

        doc.Undo.Undo();

        Assert.Single(mv.Movers);
        Assert.NotNull(be.FindBrush(b));
        Assert.Contains(Movers(doc)!.Movers, m => m.Uid == b);
        Assert.Equal(keyframes, mv.Movers.Single().MovingData!.Keyframes.Count);
    }

    // (8) A locked group refuses member selection with a lock hint.
    [Fact]
    public void Locked_Group_Members_Refuse_Selection_With_A_Hint()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int b = be.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        var groups = new GroupService(doc, be);
        Group g = groups.CreateGroup("locked", new[] { b }, Array.Empty<int>());

        groups.SetLocked(g, true);

        Assert.True(be.IsBrushLocked(b)); // propagated to the brush lock (one observable behaviour)
        var router = new SelectionRouter(() => doc, () => be, () => SelectKinds.Brushes);
        bool hinted = false;
        router.LockBlocked += () => hinted = true;
        Assert.False(router.SelectBrush(b));
        Assert.True(hinted);
        Assert.DoesNotContain(b, be.SelectedBrushes);

        groups.SetLocked(g, false);
        Assert.False(be.IsBrushLocked(b));
        Assert.True(router.SelectBrush(b));
    }

    // Escalation core: selecting a group as a UNIT selects every member brush AND object.
    [Fact]
    public void SelectGroupUnit_Selects_Every_Member()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int b = be.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(3, 0, 0))!;
        var groups = new GroupService(doc, be);
        Group g = groups.CreateGroup("unit", new[] { b }, new[] { obj.Uid });
        doc.RefreshObjects();

        var router = new SelectionRouter(() => doc, () => be, () => SelectKinds.Groups);
        Assert.True(router.SelectGroupUnit(g.Brushes.Concat(g.Objects).ToList()));

        Assert.Contains(b, be.SelectedBrushes);
        Assert.Contains(doc.Selection, o => o.Uid == obj.Uid);
    }

    // (Item 2.3) A trigger that RIDES the mover as a member gets NO real RFL link written to its Links
    // list by Create Mover — the member -> keyframe relationship is structural (derived), never a stored
    // link — so it can never behave like an auto-created trigger link.
    [Fact]
    public void Create_Mover_With_A_Trigger_Member_Writes_No_Real_Link()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int b = be.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        LevelObject trig = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!;
        doc.RefreshObjects();
        var mv = new MoverService(doc);

        Group group = mv.CreateMover(new[] { b }, new[] { trig.Uid }, "DoorTrigger");

        // The trigger is a member (it rides the mover) ...
        Assert.Contains(trig.Uid, group.Objects);
        // ... but its persisted Links list stays empty — Create Mover wrote no auto link.
        Assert.Empty(((Trigger)trig.Model).Links);

        // The member -> start-keyframe association is NOT a viewport line (RED draws only the sequence
        // chain), so it never reads as an auto-created trigger link in the viewport ...
        Assert.DoesNotContain(MovingGroupLinks.SequenceEdges(new[] { group }), e => e.From == trig.Uid || e.To == trig.Uid);
        // ... it surfaces only as a structural edge for the Link Graph (drawn dashed / distinctly).
        Assert.Contains(MovingGroupLinks.Edges(new[] { group }), e => e.From == trig.Uid);
    }

    // (R1) A freshly created GED mover matches RED.exe's fresh-mover defaults field-for-field
    // (RED.exe FUN_00416000 @0x00416000: movement_type=1, all four sound volumes=1.0; RF.exe
    // FUN_00463820 reads keyframe event/item uids with default -1). Pinned parity gate: every field
    // matches RED's authored defaults. Explicitly REFUTES the "no_player_collide is always set"
    // hypothesis — GED writes 0 (player collides), the same value RED authors for its lifts.
    [Fact]
    public void Fresh_Mover_Fields_Match_RED_Authored_Defaults()
    {
        var (_, _, mv, _) = SingleBrushMover();
        MovingGroupData d = mv.Movers.Single().MovingData!;

        Assert.Equal(1, d.MovementType);              // one_way (RED *(data+0x20)=1)
        Assert.Equal(1f, d.StartVol);                 // RED writes 1.0 for every volume (0x3f800000)
        Assert.Equal(1f, d.LoopingVol);
        Assert.Equal(1f, d.StopVol);
        Assert.Equal(1f, d.CloseVol);
        Assert.Equal((byte)0, d.NoPlayerCollide);     // player collides — "always set" hypothesis refuted
        Assert.Equal((byte)0, d.IsDoor);
        Assert.Equal((byte)0, d.RotateInPlace);
        Assert.Equal((byte)0, d.StartsBackwards);
        Assert.Equal((byte)0, d.UseTravelTimeAsSpeed);
        Assert.Equal((byte)0, d.ForceOrient);
        Assert.Equal(0, d.StartingKeyframe);

        Keyframe start = d.Keyframes.Single();
        Assert.Equal(-1, start.EventUid);             // "no triggered event / item" sentinels
        Assert.Equal(-1, start.ItemUid1);
        Assert.Equal(-1, start.ItemUid2);
    }

    // (R1) Every subsequently-added keyframe carries the same RED "none" sentinels, so a fresh
    // keyframe never reads as if it triggered the object with UID 0.
    [Fact]
    public void Added_Keyframe_Carries_RED_None_Sentinels()
    {
        var (_, _, mv, _) = SingleBrushMover();
        Keyframe k = mv.AddKeyframe(mv.Movers.Single(), new Vec3(0, 8, 0), Mat3.Identity);
        Assert.Equal(-1, k.EventUid);
        Assert.Equal(-1, k.ItemUid1);
        Assert.Equal(-1, k.ItemUid2);
    }

    // (R3) Linking a trigger to a mover BRUSH stores the mover's START KEYFRAME uid — the uid RF.exe
    // resolves a trigger link to (FUN_00469250 @0x00469250: *(mover+0x20)=keyframe[0].uid), matching
    // every RED-authored dmabrupt trigger (54->49, 93->10320, 264->266, 10182->10180). It must NEVER
    // store the brush uid, which RF cannot resolve to a mover ("trigger fires, nothing moves").
    [Fact]
    public void Trigger_To_Mover_Brush_Link_Stores_Start_Keyframe_Uid()
    {
        var (doc, _, mv, b) = SingleBrushMover();
        Group g = mv.Movers.Single();
        int startKf = g.MovingData!.Keyframes[g.MovingData.StartingKeyframe].Uid;
        LevelObject trig = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(3, 0, 0))!;
        doc.RefreshObjects();
        LevelObject moverObj = doc.FindByUid(b)!;     // a mover brush projects as a Mover object (brush uid)
        Assert.Equal(LevelObjectKind.Mover, moverObj.Kind);

        var links = new LinkService(doc);
        LinkResult r = links.LinkOneToMany(trig, new[] { moverObj });

        Assert.True(r.Ok);
        List<int> stored = ((Trigger)trig.Model).Links;
        Assert.Contains(startKf, stored);
        Assert.DoesNotContain(b, stored);             // never the brush uid
    }

    // (R3) A link straight to a keyframe object is stored unchanged (already the correct shape).
    [Fact]
    public void Trigger_To_Keyframe_Link_Is_Stored_Unchanged()
    {
        var (doc, _, mv, _) = SingleBrushMover();
        Keyframe start = mv.Movers.Single().MovingData!.Keyframes.Single();
        LevelObject trig = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(3, 0, 0))!;
        doc.RefreshObjects();

        var links = new LinkService(doc);
        links.LinkOneToMany(trig, new[] { doc.FindByUid(start.Uid)! });

        Assert.Contains(start.Uid, ((Trigger)trig.Model).Links);
    }

    // (round-trip) The RED-authentic fresh-mover defaults survive save/reload byte-stable.
    [Fact]
    public void Fresh_Mover_Defaults_Round_Trip_Byte_Stable()
    {
        var (doc, _, _, _) = SingleBrushMover();
        byte[] saved = doc.SaveToBytes();
        RflFile reloaded = RflFile.Load(saved);
        Assert.Equal(saved, reloaded.Save());
    }

    // (stuck-to-mover) Member transforms are captured RELATIVE to the start keyframe (RED's
    // moving_group_member_transform "applied to keyframe to get member transform"): RF reconstructs
    // member_world = keyframe + member_transform. Storing the ABSOLUTE member position instead
    // displaces the mover's collision by the keyframe position — the player gets pinned/stuck on
    // contact and the mover reads as having no collision where drawn. Brushes offset from the origin
    // (a single brush AT the origin hides the bug, since offset == absolute there).
    [Fact]
    public void Member_Transforms_Are_Relative_To_The_Start_Keyframe()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int b1 = be.CreateBrush(new BrushCreateParams { Width = 2, Height = 2, Depth = 2 }, new Vec3(10, 5, 0), Mat3.Identity);
        int b2 = be.CreateBrush(new BrushCreateParams { Width = 2, Height = 2, Depth = 2 }, new Vec3(14, 5, 0), Mat3.Identity);
        var mv = new MoverService(doc);
        Group g = mv.CreateMover(new[] { b1, b2 }, Array.Empty<int>(), "Lift");
        MovingGroupData d = g.MovingData!;
        Vec3 start = d.Keyframes[d.StartingKeyframe].Position;

        foreach (int uid in new[] { b1, b2 })
        {
            Brush brush = be.FindBrush(uid)!;
            MovingGroupMemberTransform mt = d.MemberTransforms.Single(m => m.Uid == uid);
            Assert.True(Near(start.Add(mt.Position), brush.Position),   // reconstructs true world pose
                $"reconstructed {start.Add(mt.Position)} != brush {brush.Position}");
            Assert.False(Near(mt.Position, brush.Position),             // NOT the absolute position
                "member transform must be relative to the keyframe, not absolute");
        }
    }

    private static bool Near(Vec3 a, Vec3 b) =>
        System.MathF.Abs(a.X - b.X) < 0.01f && System.MathF.Abs(a.Y - b.Y) < 0.01f && System.MathF.Abs(a.Z - b.Z) < 0.01f;

    // (has_movers) The editor save path reconciles level_info.has_movers from the movers section, the
    // way RED writes it (1 when movers exist, else 0). GED shipped 0 with movers present — the
    // movtest4 repro (has_movers=0, 1 mover) vs RED-authored dmabrupt (has_movers=1, 8 movers).
    [Fact]
    public void Editor_Save_Reconciles_Has_Movers_From_The_Movers_Section()
    {
        var (doc, _, mv, _) = SingleBrushMover();
        doc.Rfl.Sections.Insert(0, new RflSection((uint)SectionType.LevelInfo, Array.Empty<byte>())
        {
            Content = LevelInfoSection.CreateDefault(System.DateTime.Now), // HasMovers defaults 0
            Dirty = true,
        });

        // A GED-authored mover level: save flips has_movers to 1.
        Assert.Equal((byte)1, LevelInfo(RflFile.Load(doc.SaveToBytes())).HasMovers);

        // Dissolve the mover → movers section empties → has_movers reconciles back to 0.
        mv.DissolveMover(mv.Movers.Single());
        Assert.Equal((byte)0, LevelInfo(RflFile.Load(doc.SaveToBytes())).HasMovers);
    }

    private static LevelInfoSection LevelInfo(RflFile r)
    {
        r.ParseAllKnownSections();
        return r.Sections.Select(s => s.Content).OfType<LevelInfoSection>().First();
    }

    // (use-key trigger) A freshly placed trigger defaults its attach / use-clutter / airlock UID
    // slots to -1 ("none") like RED — not 0, which reads as "attached to / requires object UID 0".
    // Verified against RED dmabrupt use-key trigger 54 (-1/-1/-1) vs GED-authored movtest4 (0/0/0).
    [Fact]
    public void Fresh_Trigger_Uses_Minus_One_None_Sentinels()
    {
        EditorDocument doc = NewLevel();
        var trig = (Trigger)doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!.Model;
        Assert.Equal(-1, trig.AttachedToUid);
        Assert.Equal(-1, trig.UseClutterUid);
        Assert.Equal(-1, trig.AirlockRoomUid);
    }

    // (4) MIGRATION: a level authored with the old broken shape (brush only in movers) is repaired on
    //     load; a correctly stored-twice level is untouched (byte-safe).
    [Fact]
    public void Repair_Restores_A_Broken_Mover_Brush_To_The_World()
    {
        var (doc, be, mv, b) = SingleBrushMover();

        // Simulate the OLD broken shape: strip the editable copy out of the brushes section, leaving the
        // brush only in movers.
        Brushes(doc).Brushes.RemoveAll(x => x.Uid == b);
        doc.RefreshObjects();
        Assert.Null(be.FindBrush(b));

        int restored = mv.RepairStoredTwiceInvariant();

        Assert.Equal(1, restored);
        Assert.NotNull(be.FindBrush(b));                  // editable world copy is back
        Assert.Contains(Movers(doc)!.Movers, m => m.Uid == b);
    }

    [Fact]
    public void Repair_Is_A_NoOp_On_A_Correct_Stored_Twice_Level()
    {
        var (doc, _, mv, _) = SingleBrushMover();

        Assert.Equal(0, mv.RepairStoredTwiceInvariant());
        Assert.Equal(0, mv.RepairStoredTwiceInvariant()); // idempotent
    }

    // (round-trip) A created mover saves/loads/re-saves byte-stable AND the compiler excludes it from
    // the static fold by UID.
    [Fact]
    public void Created_Mover_Round_Trips_Byte_Stable_And_Compiles_Excluded()
    {
        var (doc, _, _, b) = SingleBrushMover();

        HashSet<int> moverUids = MoverBrushes.CollectMoverUids(doc.Rfl);
        Assert.Contains(b, moverUids);
        List<Brush> staticBrushes = MoverBrushes.StaticWorldBrushes(doc.Rfl);
        Assert.DoesNotContain(staticBrushes, x => x.Uid == b); // excluded from the static fold

        byte[] saved = doc.SaveToBytes();
        RflFile reloaded = RflFile.Load(saved);
        reloaded.ParseAllKnownSections();

        // Stored twice: the brush is present in BOTH sections on disk.
        Assert.Contains(reloaded.Sections.Select(s => s.Content).OfType<BrushesSection>().Single().Brushes, x => x.Uid == b);
        Assert.Contains(reloaded.Sections.Select(s => s.Content).OfType<MoversSection>().Single().Movers, x => x.Uid == b);

        Assert.Equal(saved, reloaded.Save());
    }
}
