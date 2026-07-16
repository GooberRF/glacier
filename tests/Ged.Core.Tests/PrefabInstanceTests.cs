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

        // Place: import the payload, then record the instance (order = Import's stable order:
        // Events before Triggers, so member[0] is the event, member[1] the trigger).
        IReadOnlyList<int> placed = RfgInterop.Import(doc, payload, Vec3.Zero);
        Assert.Equal(2, placed.Count);
        PrefabInstanceRecord inst = svc.RecordInstance("sample", "hash1", placed, Vec3.Zero, Mat3.Identity);

        int oldEventUid = placed[0];
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

        // Transform preserved: the re-instantiated event sits at the same world position.
        int newEventUid = after.MemberUids[0];
        Vec3 eventPosAfter = ((RflEvent)doc.FindByUid(newEventUid)!.Model).Position;
        Assert.Equal(eventPosBefore, eventPosAfter);

        // External inbound link preserved: the external trigger now points at the NEW event.
        var externalTrigger = (Trigger)doc.FindByUid(externalUid)!.Model;
        Assert.Equal(new[] { newEventUid }, externalTrigger.Links);
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
