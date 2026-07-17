using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Parametric prefab instances — the GED lineage section round-trip, byte-identity
/// for levels without instances, place→propagate (members re-created, transform + external
/// inbound links preserved), orphan, and modified-flag detection.
/// </summary>
public sealed class PrefabInstanceTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    /// <summary>A prefab payload: Event(uid1) + Trigger(uid2) that links to the event (intra-prefab).</summary>
    private static RfgFile SamplePayload(float eventX = 5f)
    {
        var group = new RfgGroup { Name = "sample" };
        group.Events.Events.Add(new RflEvent { Uid = 1, ClassName = "Delay", Position = new Vec3(eventX, 0, 0) });
        group.Triggers.Triggers.Add(new Trigger { Uid = 2, Position = new Vec3(eventX + 1, 0, 0), Links = { 1 } });
        var file = new RfgFile { Version = 0xC8 };
        file.Groups.Add(group);
        return file;
    }

    // ---- Section round-trip ----

    [Fact]
    public void PrefabInstances_Section_Round_Trips()
    {
        var section = new GedPrefabInstancesSection();
        section.Instances.Add(new PrefabInstanceRecord
        {
            InstanceId = 3,
            PrefabName = "door",
            SourceHash = "abc123",
            MemberUids = new List<int> { 10, 11, 12 },
            PivotPosition = new Vec3(1, 2, 3),
            PivotRotation = Mat3.Identity,
            Modified = true,
        });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.GedPrefabInstances, Array.Empty<byte>()) { Content = section, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        byte[] bytes = rfl.Save();
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        GedPrefabInstancesSection? back = reloaded.Sections
            .Select(s => s.Content).OfType<GedPrefabInstancesSection>().FirstOrDefault();
        Assert.NotNull(back);
        PrefabInstanceRecord r = Assert.Single(back!.Instances);
        Assert.Equal(3, r.InstanceId);
        Assert.Equal("door", r.PrefabName);
        Assert.Equal("abc123", r.SourceHash);
        Assert.Equal(new[] { 10, 11, 12 }, r.MemberUids);
        Assert.Equal(new Vec3(1, 2, 3), r.PivotPosition);
        Assert.True(r.Modified);
    }

    [Fact]
    public void No_Section_Written_When_There_Are_No_Instances()
    {
        EditorDocument doc = EmptyDoc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        // Merely constructing the service and reading it must not add the section.
        var svc = new PrefabInstanceService(doc);
        Assert.False(svc.HasInstances);

        byte[] after = doc.SaveToBytes(updateTimestamp: false);
        Assert.Equal(before, after); // byte-identical: no editor-only section leaked in
    }

    // ---- Place + propagate ----

    [Fact]
    public void Place_Then_Propagate_Recreates_Members_Preserving_Transform_And_External_Links()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);
        RfgFile payload = SamplePayload(eventX: 5f);

        // Place via the tracked-instance path (import + record in one transaction). Member order =
        // Import's stable order (Events before Triggers): member[0] is the event, member[1] the trigger.
        PrefabInstanceRecord inst = svc.PlaceInstance(payload, "sample", "hash1", Vec3.Zero, Mat3.Identity);
        Assert.Equal(2, inst.MemberUids.Count);

        int oldEventUid = inst.MemberUids[0];
        Vec3 eventPosBefore = ((RflEvent)doc.FindByUid(oldEventUid)!.Model).Position;

        // An EXTERNAL trigger (not a member) that links to the instance's event member.
        var triggers = (TriggersSection)doc.Rfl.GetOrCreateSection(SectionType.Triggers, () => new TriggersSection()).Content!;
        int externalUid = doc.AllocateUid();
        triggers.Triggers.Add(new Trigger { Uid = externalUid, Links = { oldEventUid } });
        doc.RefreshObjects();

        // Propagate the (unchanged) payload to every instance of "sample".
        int n = svc.Propagate("sample", payload, "hash2", includeModified: false);
        Assert.Equal(1, n);

        // The instance was re-created with fresh member UIDs (none of the old ones).
        PrefabInstanceRecord after = Assert.Single(svc.Instances);
        Assert.Equal(2, after.MemberUids.Count);
        Assert.DoesNotContain(oldEventUid, after.MemberUids);
        Assert.Equal("hash2", after.SourceHash);
        Assert.False(after.Modified);

        // Transform preserved (unmoved instance, unchanged payload): the re-instantiated event sits
        // at exactly the same world position it was placed at.
        int newEventUid = after.MemberUids[0];
        Vec3 eventPosAfter = ((RflEvent)doc.FindByUid(newEventUid)!.Model).Position;
        Assert.True(eventPosBefore.ApproxEquals(eventPosAfter), $"{eventPosBefore} != {eventPosAfter}");

        // External inbound link preserved: the external trigger now points at the NEW event.
        var externalTrigger = (Trigger)doc.FindByUid(externalUid)!.Model;
        Assert.Equal(new[] { newEventUid }, externalTrigger.Links);
    }

    [Fact]
    public void Propagate_Poses_New_Payload_At_The_Instances_Moved_And_Rotated_Transform()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);

        // Place the instance at a non-trivial pivot.
        var placePivot = new Vec3(10, 20, 30);
        PrefabInstanceRecord inst = svc.PlaceInstance(SamplePayload(eventX: 5f), "sample", "h1", placePivot, Mat3.Identity);

        // The USER moves + rotates the WHOLE instance: its explicit pose record is the source of
        // truth (this is exactly what the gizmo/keyboard hooks and SetInstancePose drive).
        var movedPos = new Vec3(100, -5, 7);
        Mat3 movedRot = Mat3Math.RotationY(MathF.PI / 2f);
        Assert.True(svc.SetInstancePose(inst.InstanceId, movedPos, movedRot));

        // Propagate an EDITED payload (members at different local positions).
        RfgFile edited = SamplePayload(eventX: 7f); // event (7,0,0), trigger (8,0,0)
        int n = svc.Propagate("sample", edited, "h2", includeModified: false);
        Assert.Equal(1, n);

        PrefabInstanceRecord after = Assert.Single(svc.Instances);

        // (b) The whole group's world pose equals the user's moved/rotated pose — unchanged by propagation.
        Assert.True(after.PivotPosition.ApproxEquals(movedPos));
        Assert.True(after.PivotRotation.ApproxEquals(movedRot));

        // (a) Each member's pose RELATIVE to the pivot matches the NEW payload exactly, and the
        // assembled world pose = movedRot·local + movedPos. The payload is FIXED prefab-local space
        // (origin == pivot), so the pivot is the origin — never derived from the content's bounds.
        Vec3 prefabPivot = Vec3.Zero;
        var eventLocal = new Vec3(7, 0, 0);
        var triggerLocal = new Vec3(8, 0, 0);

        var newEvent = (RflEvent)doc.FindByUid(after.MemberUids[0])!.Model;
        var newTrigger = (Trigger)doc.FindByUid(after.MemberUids[1])!.Model;

        Vec3 expectedEvent = movedRot.Transform(eventLocal.Sub(prefabPivot)).Add(movedPos);
        Vec3 expectedTrigger = movedRot.Transform(triggerLocal.Sub(prefabPivot)).Add(movedPos);
        Assert.True(newEvent.Position.ApproxEquals(expectedEvent), $"event {newEvent.Position} != {expectedEvent}");
        Assert.True(newTrigger.Position.ApproxEquals(expectedTrigger), $"trigger {newTrigger.Position} != {expectedTrigger}");

        // Pivot-relative position recovers the new payload's authored layout exactly.
        Assert.True(movedRot.InverseTransform(newEvent.Position.Sub(movedPos)).ApproxEquals(eventLocal.Sub(prefabPivot)));

        // Orientation rides the group rotation: a member authored without rotation ends up oriented
        // to the instance rotation (pivot-relative orientation == identity == the new payload's).
        Assert.NotNull(newEvent.Rotation);
        Assert.True(newEvent.Rotation!.Value.ApproxEquals(movedRot), "member did not inherit the instance rotation");
        Mat3 pivotRelative = Mat3Math.Compose(movedRot.Transpose(), newEvent.Rotation!.Value);
        Assert.True(pivotRelative.ApproxEquals(Mat3.Identity));

        // The intra-prefab link was remapped to the new member UIDs (trigger → event).
        Assert.Equal(new[] { after.MemberUids[0] }, newTrigger.Links);
    }

    /// <summary>An events-only payload in FIXED prefab-local space (positions ARE local coords).</summary>
    private static RfgFile EventsPayload(params float[] localXs)
    {
        var group = new RfgGroup { Name = "p" };
        int uid = 1;
        foreach (float x in localXs)
        {
            group.Events.Events.Add(new RflEvent { Uid = uid++, ClassName = "Delay", Position = new Vec3(x, 0, 0) });
        }

        var file = new RfgFile { Version = 0xC8 };
        file.Groups.Add(group);
        return file;
    }

    // ---- Defect 1: a content edit that MOVES the pivot bbox never shifts untouched members --------

    [Fact]
    public void Propagation_Never_Derives_A_Pivot_From_Content_So_Untouched_Members_Do_Not_Shift()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);

        // Two instances of the same prefab (one member at local x=2) placed at different pivots.
        PrefabInstanceRecord a = svc.PlaceInstance(EventsPayload(2f), "p", "h1", new Vec3(10, 0, 0), Mat3.Identity);
        PrefabInstanceRecord b = svc.PlaceInstance(EventsPayload(2f), "p", "h1", new Vec3(50, 20, 0), Mat3.Identity);

        Vec3 aWorldBefore = ((RflEvent)doc.FindByUid(a.MemberUids[0])!.Model).Position; // (12,0,0)
        Vec3 bWorldBefore = ((RflEvent)doc.FindByUid(b.MemberUids[0])!.Model).Position; // (52,20,0)
        Vec3 aPoseBefore = a.PivotPosition;
        Vec3 bPoseBefore = b.PivotPosition;

        // The prefab is edited: the existing member keeps its local coord (2) and a NEW, far-off member
        // is added at local x=90 — this DRASTICALLY moves the content's bbox centre (2 → 46). Because
        // the payload is FIXED prefab-local (the App re-bases through the source pose), the shared
        // member's local coord is unchanged; a content-derived pivot (the old bug) would have shifted it.
        RfgFile edited = EventsPayload(2f, 90f);
        Assert.Equal(2, svc.Propagate("p", edited, "h2", includeModified: false));

        PrefabInstanceRecord a2 = svc.ById(a.InstanceId)!;
        PrefabInstanceRecord b2 = svc.ById(b.InstanceId)!;

        // Untouched member: EXACTLY unchanged world coords in BOTH instances; pose records unchanged.
        Assert.True(((RflEvent)doc.FindByUid(a2.MemberUids[0])!.Model).Position.ApproxEquals(aWorldBefore));
        Assert.True(((RflEvent)doc.FindByUid(b2.MemberUids[0])!.Model).Position.ApproxEquals(bWorldBefore));
        Assert.True(a2.PivotPosition.ApproxEquals(aPoseBefore));
        Assert.True(b2.PivotPosition.ApproxEquals(bPoseBefore));

        // The new member lands at its authored local offset (90) from EACH instance's pivot.
        Assert.True(((RflEvent)doc.FindByUid(a2.MemberUids[1])!.Model).Position.ApproxEquals(new Vec3(100, 0, 0)));
        Assert.True(((RflEvent)doc.FindByUid(b2.MemberUids[1])!.Model).Position.ApproxEquals(new Vec3(140, 20, 0)));
    }

    [Fact]
    public void Rebase_Through_A_Source_Pose_Recovers_Byte_Identical_Local_Coords()
    {
        // The App re-bases the exported (world-space) selection through the source instance's pose so
        // an untouched member keeps its ORIGINAL prefab-local coordinates. Verify that inverse.
        var payload = EventsPayload(2f, 5f); // local x = 2, 5
        var pivotPos = new Vec3(10, 3, -4);
        Mat3 pivotRot = Mat3Math.RotationY(0.7f);

        // Pose the payload to WORLD as a placed instance would (world = R·local + t)…
        RfgInterop.TransformInPlace(payload, pivotRot, pivotPos);
        Vec3 w0 = payload.Groups[0].Events.Events[0].Position;

        // …then re-base through that same pose (local = Rᵀ·(world − t)) — the coords return to (2,0,0)/(5,0,0).
        Mat3 rInv = pivotRot.Transpose();
        RfgInterop.TransformInPlace(payload, rInv, rInv.Transform(pivotPos).Scale(-1f));
        Assert.True(payload.Groups[0].Events.Events[0].Position.ApproxEquals(new Vec3(2, 0, 0)), $"got {payload.Groups[0].Events.Events[0].Position}");
        Assert.True(payload.Groups[0].Events.Events[1].Position.ApproxEquals(new Vec3(5, 0, 0)));
        Assert.False(w0.ApproxEquals(new Vec3(2, 0, 0))); // sanity: the world pose really did move it
    }

    [Fact]
    public void Whole_Instance_Rigid_Transform_Keeps_The_Pose_Record_Fresh()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);
        PrefabInstanceRecord inst = svc.PlaceInstance(SamplePayload(eventX: 5f), "sample", "h1", Vec3.Zero, Mat3.Identity);

        // A whole-instance translation (all members in the transformed set) updates the pose record…
        var delta = new Vec3(3, 4, 5);
        int moved = svc.ApplyRigidTransform(inst.MemberUids, Mat3.Identity, delta, Vec3.Zero);
        Assert.Equal(1, moved);
        Assert.True(svc.Instances[0].PivotPosition.ApproxEquals(delta));

        // …then a whole-instance rotation about a pivot composes into the pose orientation.
        Mat3 rot = Mat3Math.RotationY(MathF.PI / 2f);
        Assert.Equal(1, svc.ApplyRigidTransform(inst.MemberUids, rot, Vec3.Zero, Vec3.Zero));
        Assert.True(svc.Instances[0].PivotRotation.ApproxEquals(rot));

        // A PARTIAL cover (an individual member moved WITHIN the instance) never moves the pose.
        Vec3 poseBefore = svc.Instances[0].PivotPosition;
        Assert.Equal(0, svc.ApplyRigidTransform(new[] { inst.MemberUids[0] }, Mat3.Identity, new Vec3(99, 0, 0), Vec3.Zero));
        Assert.True(svc.Instances[0].PivotPosition.ApproxEquals(poseBefore));
    }

    // ---- Orphan ----

    [Fact]
    public void Orphan_Removes_The_Record_But_Keeps_The_Members()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);
        IReadOnlyList<int> placed = RfgInterop.Import(doc, SamplePayload(), Vec3.Zero);
        PrefabInstanceRecord inst = svc.RecordInstance("sample", "h", placed, Vec3.Zero, Mat3.Identity);

        Assert.True(svc.Orphan(inst.InstanceId));
        Assert.False(svc.HasInstances);
        // The members remain as plain independent content.
        Assert.All(placed, uid => Assert.NotNull(doc.FindByUid(uid)));
    }

    // ---- Modified flag ----

    [Fact]
    public void Modified_Member_Is_Detected_And_Skipped_By_Default()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);
        RfgFile payload = SamplePayload();
        IReadOnlyList<int> placed = RfgInterop.Import(doc, payload, Vec3.Zero);
        PrefabInstanceRecord inst = svc.RecordInstance("sample", "h1", placed, Vec3.Zero, Mat3.Identity);

        Assert.Null(svc.InstanceOfMember(999));
        Assert.Same(inst, svc.InstanceOfMember(placed[0]));

        Assert.True(svc.MarkMemberModified(placed[1]));
        Assert.True(svc.Instances[0].Modified);

        // Default propagation SKIPS the modified instance…
        Assert.Equal(0, svc.Propagate("sample", payload, "h2", includeModified: false));
        Assert.Equal("h1", svc.Instances[0].SourceHash);

        // …unless forced.
        Assert.Equal(1, svc.Propagate("sample", payload, "h2", includeModified: true));
        Assert.Equal("h2", svc.Instances[0].SourceHash);
        Assert.False(svc.Instances[0].Modified); // cleared by re-instantiation
    }

    [Fact]
    public void Placement_And_Propagation_Are_Undoable()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new PrefabInstanceService(doc);
        RfgFile payload = SamplePayload();

        using (doc.Undo.BeginTransaction("Place prefab"))
        {
            IReadOnlyList<int> placed = RfgInterop.Import(doc, payload, Vec3.Zero);
            svc.RecordInstance("sample", "h1", placed, Vec3.Zero, Mat3.Identity);
        }

        Assert.True(svc.HasInstances);
        Assert.Equal(2, doc.Objects.Count);

        doc.Undo.Undo(); // one entry removes both the members and the lineage record
        Assert.False(svc.HasInstances);
        Assert.Empty(doc.Objects);
    }
}
