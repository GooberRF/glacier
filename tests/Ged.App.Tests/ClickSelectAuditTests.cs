using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Services;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Click-select audit of every object kind, end to end through the real pipeline: a level with a built floor,
/// one object of each kind standing ON that floor (the way place-at-cursor drops them), Object mode entered
/// through the mode chokepoint, the real <see cref="EditorSession.BuildScene"/>, the real GPU id-buffer pick
/// (<see cref="SceneRenderer.RenderPick"/>) aimed at the object's icon from an ordinary elevated view, and
/// the same chip-gated document-selection route <c>MainWindow.OnPicked</c> takes.
/// <para>
/// Two defects made icons unclickable while marquee (CPU) selection still worked: the pick pass drew the
/// billboards with the world's back-face-culling state (the colour pass resets to no culling first), so
/// every counter-clockwise glyph quad was culled from the id buffer unless the last drawn mesh happened to be
/// double-sided; and an icon whose object stands on a floor had its centre pixel exactly at the floor's depth
/// (and its lower half behind it), so the floor face won the click. Waypoint helpers, respawn points, the
/// Player Start and vehicle factories (all ground objects) were the visible casualties.
/// </para>
/// </summary>
public sealed class ClickSelectAuditTests
{
    private static GraphicsDevice? TryDevice()
    {
        try
        {
            return new GraphicsDevice(GraphicsBackend.Direct3D11);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A new level carved by one 120 × 10 × 120 m air room whose floor is the plane y = 0.</summary>
    private static EditorSession FloorLevel()
    {
        var session = new EditorSession();
        session.NewLevel();
        session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 120, Height = 10, Depth = 120, Air = true },
            new Vec3(0, 5f, 0), Mat3.Identity);
        return session;
    }

    private static void Build(EditorSession session)
    {
        EditorDocument doc = session.Document!;
        var options = new CompileOptions { Alpine = doc.Rfl.Context.IsAlpine, SharedBsp = true, BuildSurfaces = true, FixTJoints = true };
        CompiledLevel result = GeometryBuildService.Build(doc.Rfl, options);
        GeometryBuildService.Apply(doc.Rfl, result);
        doc.RefreshObjects();
    }

    [AvaloniaFact]
    public void Every_Object_Kind_Standing_On_A_Floor_Is_Click_Selectable()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return; // no GPU device in this environment
        }

        EditorSession session = FloorLevel();
        EditorDocument doc = session.Document!;

        // One of every placeable kind, on the floor, 4 m apart.
        var expected = new List<(string Label, LevelObject Object)>();
        float x = -50f;
        Vec3 Next()
        {
            x += 4f;
            return new Vec3(x, 0f, 0f);
        }

        foreach (PlaceableObjectType t in ObjectFactory.Palette)
        {
            LevelObject o = doc.PlaceObject(t.Kind, Next()) ?? throw new InvalidOperationException($"{t.Kind} not placed");
            expected.Add((t.Kind.ToString(), o));
        }

        EventSchema schema = EventSchemaCatalog.All.First();
        expected.Add(("Event", doc.PlaceEvent(schema, Next())!));

        ObjectHeader node = new CutsceneService(doc).AddNode(Next(), Mat3.Identity);
        expected.Add(("CutscenePathNode", doc.FindByUid(node.Uid)!));

        // The Player Start, moved onto the floor.
        LevelObject start = doc.Objects.Single(o => o.Kind == LevelObjectKind.PlayerStart);
        var ps = (Ged.Core.IO.Rfl.Sections.PlayerStartSection)start.Model;
        ps.Position = Next();
        expected.Add(("PlayerStart", start));

        // A mover: its keyframes are on-top glyphs.
        Vec3 moverAt = Next();
        int box = session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 3, Height = 1, Depth = 3 }, moverAt.Add(new Vec3(0, 3, 0)), Mat3.Identity);
        var movers = new MoverService(doc);
        Group group = movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        movers.AddKeyframe(group, Next(), Mat3.Identity);
        foreach (Keyframe k in group.MovingData!.Keyframes)
        {
            expected.Add(($"Keyframe {k.Uid}", doc.FindByUid(k.Uid)!));
        }

        Build(session);
        session.BrushEditor.SetMode(EditMode.Object);
        session.SyncSelectionToKinds(SelectionFilter.PrimaryKindFor(EditMode.Object));
        RenderScene scene = session.BuildScene();

        const int W = 320, H = 240;
        using var renderer = new SceneRenderer(gd);
        using var pickTarget = gd.CreatePickTarget(W, H);
        using var gpu = new GpuScene(gd, scene, null);
        var cam = new Ged.Rendering.Camera { AspectRatio = (float)W / H };

        // The floor the objects stand on is really in the id buffer (so the icons compete with it).
        cam.LookAt(new Vector3(0f, 2f, 26f), new Vector3(0f, 0f, 30f));
        Assert.Equal(PickKind.Face, renderer.RenderPick(cam, gpu, pickTarget, W / 2, H / 2).Kind);

        var failures = new List<string>();
        foreach ((string label, LevelObject o) in expected)
        {
            Vector3 p = PositionOf(scene, o.Uid) ?? throw new InvalidOperationException($"{label} has no icon");

            // An ordinary editing view: 4 m back, 2 m up, looking at the icon.
            cam.LookAt(p + new Vector3(0f, 2f, -4f), p);
            PickId hit = renderer.RenderPick(cam, gpu, pickTarget, W / 2, H / 2);
            LevelObject? picked = session.ObjectForPick(hit);
            if (picked is null || picked.Uid != o.Uid)
            {
                failures.Add($"{label}: id buffer returned {hit.Kind}:{hit.Index}");
                continue;
            }

            // Route it exactly as OnPicked's document path does in Object mode.
            doc.ClearSelection();
            bool allowed = PickGate.AllowsDocumentSelect(session.ActiveSelectKinds, hit.Kind, picked.Kind == LevelObjectKind.Mover);
            if (!allowed || !session.Selection.SelectObject(picked, additive: false) || !doc.IsSelected(picked))
            {
                failures.Add($"{label}: the pick did not select it (gate {allowed})");
            }
        }

        // The mover itself (its geometry, a level object in Object mode), clicked beside the start keyframe icon.
        LevelObject mover = doc.Objects.Single(o => o.Kind == LevelObjectKind.Mover);
        Vector3 moverFace = new(moverAt.X + 1.1f, moverAt.Y + 3f, moverAt.Z - 1.5f);
        cam.LookAt(moverFace + new Vector3(0f, 0.5f, -3f), moverFace);
        PickId moverHit = renderer.RenderPick(cam, gpu, pickTarget, W / 2, H / 2);
        LevelObject? moverPicked = session.ObjectForPick(moverHit);
        if (moverPicked?.Uid != mover.Uid
            || !PickGate.AllowsDocumentSelect(session.ActiveSelectKinds, moverHit.Kind, moverPicked.Kind == LevelObjectKind.Mover))
        {
            failures.Add($"Mover: id buffer returned {moverHit.Kind}:{moverHit.Index}");
        }

        Assert.True(failures.Count == 0, "Not click-selectable:\n" + string.Join("\n", failures));
        Assert.True(expected.Count >= ObjectFactory.Palette.Count + 4);
    }

    /// <summary>The icon depth pull never lets an object behind a real wall take the click.</summary>
    [AvaloniaFact]
    public void An_Object_Behind_A_Wall_Is_Not_Clickable_Through_It()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        EditorSession session = FloorLevel();
        EditorDocument doc = session.Document!;
        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0f, 1f, 20f))!;

        // A 1 m solid wall, its near face 2.5 m in front of the light.
        session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 10, Height = 8, Depth = 1 }, new Vec3(0f, 4f, 17f), Mat3.Identity);
        Build(session);
        session.BrushEditor.SetMode(EditMode.Object);
        session.SyncSelectionToKinds(SelectionFilter.PrimaryKindFor(EditMode.Object));
        RenderScene scene = session.BuildScene();

        const int W = 320, H = 240;
        using var renderer = new SceneRenderer(gd);
        using var pickTarget = gd.CreatePickTarget(W, H);
        using var gpu = new GpuScene(gd, scene, null);
        var cam = new Ged.Rendering.Camera { AspectRatio = (float)W / H };
        cam.LookAt(new Vector3(0f, 1.5f, 10f), new Vector3(0f, 1f, 20f));
        PickId hit = renderer.RenderPick(cam, gpu, pickTarget, W / 2, H / 2);
        Assert.Equal(PickKind.Face, hit.Kind);
        Assert.NotEqual(light.Uid, session.ObjectForPick(hit)?.Uid);
    }

    /// <summary>The billboard of a UID (its icon position).</summary>
    private static Vector3? PositionOf(RenderScene scene, int uid)
    {
        foreach (Billboard b in scene.Billboards)
        {
            if (b.PickId.Kind == PickKind.Object && b.PickId.Index == (uid & 0x0FFFFFFF))
            {
                return b.Position;
            }
        }

        return null;
    }
}
