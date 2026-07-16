using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Mover authoring gates: build a moving group from brushes through the service,
/// place keyframes with full properties, flag Alpine Hold Open, then save/reload
/// and assert both the <c>movers</c> (0x2000) and <c>moving_groups</c> (0x3000)
/// sections round-trip structurally, plus undo restores the pre-mover state.
/// </summary>
public class MoverServiceTests
{
    private static (EditorDocument Doc, int B1, int B2) NewLevelWithTwoBrushes()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "mover.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);
        var editor = new BrushEditor(doc);
        int b1 = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        int b2 = editor.CreateBrush(new BrushCreateParams(), new Vec3(4, 0, 0), Mat3.Identity);
        return (doc, b1, b2);
    }

    [Fact]
    public void Build_Mover_With_Keyframes_Round_Trips_Both_Sections()
    {
        var (doc, b1, b2) = NewLevelWithTwoBrushes();
        var movers = new MoverService(doc);

        Group group = movers.CreateMover(new[] { b1, b2 }, System.Array.Empty<int>(), "Elevator");

        // The two brushes moved from the static brushes section into movers.
        Assert.Equal(2, movers.MoverBrushes.Count);
        Assert.Empty(FindBrushes(doc)?.Brushes ?? new System.Collections.Generic.List<Brush>());
        Assert.Equal(new[] { b1, b2 }, group.Brushes);

        // Second keyframe at a raised position with a full property set.
        Keyframe start = group.MovingData!.Keyframes[0];
        Keyframe top = movers.AddKeyframe(group, new Vec3(2, 8, 0), Mat3.Identity);
        movers.EditKeyframe(top, "props", k =>
        {
            k.DepartTravelTime = 3.5f;
            k.PauseTime = 1.25f;
            k.AccelTime = 0.5f;
            k.DecelTime = 0.75f;
            k.DegreesAboutAxis = 90f;
            k.EventUid = 42;
        }, k => { });

        movers.EditMover(group.MovingData!, "type", d =>
        {
            d.MovementType = 3; // ping_pong_infinite
            d.IsDoor = 1;
            d.StartSound = "door_open.wav";
            d.StopSound = "door_close.wav";
        }, d => { });

        movers.SetHoldOpen(group, true);
        Assert.True(movers.IsHoldOpen(group));

        // ---- save + reload -----------------------------------------------------
        byte[] saved = doc.SaveToBytes();
        var reloaded = RflFile.Load(saved);
        reloaded.ParseAllKnownSections();

        MoversSection movSec = reloaded.Sections.Select(s => s.Content).OfType<MoversSection>().Single();
        Assert.Equal(2, movSec.Movers.Count);

        GroupsSection mgSec = reloaded.Sections
            .Where(s => s.TypeId == (uint)SectionType.MovingGroups)
            .Select(s => (GroupsSection)s.Content!).Single();
        Group g2 = Assert.Single(mgSec.Groups);
        Assert.Equal("Elevator", g2.Name);
        Assert.Equal(1, g2.IsMoving);
        Assert.Equal(2, g2.MovingData!.Keyframes.Count);
        Assert.Equal(new[] { b1, b2 }, g2.Brushes);
        Assert.Equal(2, g2.MovingData.MemberTransforms.Count);

        Keyframe k2 = g2.MovingData.Keyframes[1];
        Assert.Equal(top.Uid, k2.Uid);
        Assert.Equal(3.5f, k2.DepartTravelTime);
        Assert.Equal(1.25f, k2.PauseTime);
        Assert.Equal(0.5f, k2.AccelTime);
        Assert.Equal(0.75f, k2.DecelTime);
        Assert.Equal(90f, k2.DegreesAboutAxis);
        Assert.Equal(42, k2.EventUid);
        Assert.Equal(3, g2.MovingData.MovementType);
        Assert.Equal(1, g2.MovingData.IsDoor);
        Assert.Equal("door_open.wav", g2.MovingData.StartSound);

        // Hold Open persisted the first keyframe UID into alpine_level_properties.
        AlpineLevelPropertiesSection alp = reloaded.Sections
            .Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
        Assert.Contains(start.Uid, alp.HoldOpenKeyframeUids);

        // Re-save is byte-stable.
        Assert.Equal(saved, reloaded.Save());
    }

    [Fact]
    public void Undo_Create_Mover_Restores_The_Static_Brushes()
    {
        var (doc, b1, b2) = NewLevelWithTwoBrushes();
        var movers = new MoverService(doc);
        movers.CreateMover(new[] { b1, b2 }, System.Array.Empty<int>(), "M");

        Assert.Equal(2, movers.MoverBrushes.Count);
        doc.Undo.Undo();

        Assert.Empty(movers.MoverBrushes);
        Assert.Equal(2, FindBrushes(doc)!.Brushes.Count);
        Assert.Empty(movers.Movers);
    }

    private static BrushesSection? FindBrushes(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        return doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault();
    }
}
