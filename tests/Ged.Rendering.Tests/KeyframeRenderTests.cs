using System;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Keyframe billboards + link arrowheads in the built scene: mover keyframes emit the
/// gold-diamond billboard (pickable by their own UID), keyframe/mover link lines are
/// drawn, and every link line carries an arrowhead at its destination end.
/// </summary>
public sealed class KeyframeRenderTests
{
    private static EditorDocument NewAlpineDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "kfrender.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Keyframe_Billboards_Emitted_For_A_Mover_Level()
    {
        EditorDocument doc = NewAlpineDoc();
        var editor = new BrushEditor(doc);
        int b1 = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        int b2 = editor.CreateBrush(new BrushCreateParams(), new Vec3(4, 0, 0), Mat3.Identity);
        var movers = new MoverService(doc);
        Group group = movers.CreateMover(new[] { b1, b2 }, Array.Empty<int>(), "Elevator");
        Keyframe start = group.MovingData!.Keyframes[0];
        Keyframe top = movers.AddKeyframe(group, new Vec3(2, 8, 0), Mat3.Identity);

        RenderScene scene = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions());

        var keyframeBillboards = scene.Billboards.Where(b => b.Kind == BillboardKind.Keyframe).ToList();
        Assert.Equal(2, keyframeBillboards.Count);
        Assert.All(keyframeBillboards, b => Assert.Equal((int)EditorIcon.Keyframe, b.Icon));
        Assert.All(keyframeBillboards, b => Assert.Equal(PickKind.Object, b.PickId.Kind));

        // Each keyframe is pickable by its own UID.
        Assert.Contains(keyframeBillboards, b => b.PickId.Index == start.Uid);
        Assert.Contains(keyframeBillboards, b => b.PickId.Index == top.Uid);
    }

    [Fact]
    public void Mover_Level_Draws_Keyframe_Link_Lines()
    {
        EditorDocument doc = NewAlpineDoc();
        var editor = new BrushEditor(doc);
        int b1 = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        var movers = new MoverService(doc);
        Group group = movers.CreateMover(new[] { b1 }, Array.Empty<int>(), "Lift");
        movers.AddKeyframe(group, new Vec3(0, 8, 0), Mat3.Identity);

        RenderScene withLinks = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeLinks = true });
        RenderScene noLinks = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeLinks = false });

        // The member->start-keyframe line and the keyframe sequence line appear only when links are on.
        Assert.True(withLinks.Lines.Count > noLinks.Lines.Count);
    }

    [Fact]
    public void Every_Link_Line_Has_An_Arrowhead_At_The_Destination_End()
    {
        EditorDocument doc = NewAlpineDoc();
        LevelObject trigger = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!;
        LevelObject target = doc.PlaceObject(LevelObjectKind.Target, new Vec3(10, 0, 0))!;
        ((Trigger)trigger.Model).Links.Add(target.Uid);

        RenderScene scene = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeMovers = false });

        // One link => one base segment + two arrowhead segments, and nothing else in this level.
        Assert.Equal(3, scene.Lines.Count);

        // The base line runs source -> destination.
        Assert.Contains(scene.Lines, l => Near(l.A, Vector3.Zero) && Near(l.B, new Vector3(10, 0, 0)));

        // The two arrowhead wings share the tip, ~85% along the edge toward the destination.
        var tip = new Vector3(8.5f, 0, 0);
        Assert.Equal(2, scene.Lines.Count(l => Near(l.A, tip)));
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
}
