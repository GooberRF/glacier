using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Render evidence for the mover editing visuals: a dragged curve handle (the curve through the moved handle, the
/// aligned opposite handle, and the white drag highlight) and a mid-preview frame (the yellow ghost of the mover's
/// brush along its curved path, away from its rest pose). Pixel-gated: the highlight and ghost colours must be present
/// where they belong. Skips without a D3D11 device. Set GED_P2_EVIDENCE to a folder to copy the PNGs there.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class MoverEditingRenderTests
{
    private const int W = 640;
    private const int H = 480;

    private static (EditorDocument Doc, Group Mover, Brush Box) CurvedMover()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15, 12, 0, 0));
        var be = new BrushEditor(doc);
        int box = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "cart");
        movers.AddKeyframe(g, new Vec3(10, 0, 0), Mat3.Identity);
        movers.AddKeyframe(g, new Vec3(10, 0, 10), Mat3.Identity);
        InspectorField segment = MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == "path_segment");
        foreach (Keyframe k in g.MovingData!.Keyframes)
        {
            InspectorAccess.Set(doc, doc.FindByUid(k.Uid)!, segment, 1);
        }

        return (doc, g, be.FindBrush(box)!);
    }

    [Fact]
    public void Dragged_Curve_Handle_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        (EditorDocument doc, Group g, Brush box) = CurvedMover();
        Keyframe k1 = g.MovingData!.Keyframes[1];

        // The drag's write: the out handle of the middle keyframe pulled 4 m up and 2 m along +X (Aligned mirrors it).
        InspectorField outHandle = MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == "out_handle");
        InspectorAccess.Set(doc, doc.FindByUid(k1.Uid)!, outHandle, new Vec3(2, 4, 3));
        AlpineMoverPathKeyframe data = AlpineAttachedData.KeyframeData(doc.Rfl, k1.Uid);
        Assert.Equal(1, (int)data.TangentMode);

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush, solidFill: true);
        uint red = Palette.Rgba(255, 0, 0);
        AlpineObjectGizmos.AddMoverPaths(scene.Lines, doc.Rfl, new[] { g }, red);
        AlpineObjectGizmos.AddMoverPathHandles(scene.Lines, doc.Rfl, g);
        int baseline = scene.Lines.Count;

        // The dragged handle, drawn white as the viewport draws it while it is held.
        Vector3 kf = V(k1.Position);
        Vector3 handle = V(k1.Position.Add(data.OutHandle));
        uint white = Palette.Rgba(255, 255, 255);
        scene.Lines.Add(new LineSegment(kf, handle, white));
        foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            scene.Lines.Add(new LineSegment(handle - (axis * 0.22f), handle + (axis * 0.22f), white));
        }

        byte[] px = Render(gd, scene, new Vector3(-2, 16, -9), new Vector3(7, 1, 5), "mover_handle_drag.png");
        Assert.True(Count(px, 255, 255, 255) > 30, "the white drag highlight is drawn");
        Assert.True(Count(px, 255, 200, 0) > 30, "the orange handles are drawn");
        Assert.True(Count(px, 255, 0, 0) > 100, "the red curved path is drawn");
        Assert.True(scene.Lines.Count > baseline);
    }

    [Fact]
    public void Mid_Preview_Ghost_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        (EditorDocument doc, Group g, Brush box) = CurvedMover();
        InspectorAccess.Set(new MoverService(doc), g, MoverInspectorSchema.MoverPathFields.Single(f => f.EffectiveKey == "path_orientation"), 1);
        var playback = new MoverPlayback(MoverPlaybackPath.From(doc.Rfl, g)!);
        playback.PlayPause();
        playback.Tick(1.5f); // half way along the second (curved) segment, turned to follow it

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush, solidFill: true);
        AlpineObjectGizmos.AddMoverPaths(scene.Lines, doc.Rfl, new[] { g }, Palette.Rgba(255, 0, 0));
        scene.Lines.AddRange(OverlayBuilder.PosedMoverGhost(new[] { box }, playback.Pose()));
        foreach (Vector3 t in playback.TimingTicks())
        {
            scene.Lines.Add(new LineSegment(t - new Vector3(0.08f, 0, 0), t + new Vector3(0.08f, 0, 0), Palette.Rgba(120, 220, 255)));
        }

        byte[] px = Render(gd, scene, new Vector3(5, 18, -6), new Vector3(6, 0, 5), "mover_preview_mid.png");
        List<(int X, int Y)> ghost = Pixels(px, 255, 255, 0);
        Assert.True(ghost.Count > 60, "the yellow ghost is drawn");

        // The ghost is near the middle of the second segment (x ≈ 10, z ≈ 5), well away from the box at the origin.
        Vector3 at = playback.Pose().Now;
        Assert.True(at.X > 9f && at.Z > 3f && at.Z < 7f, $"mid-preview pose {at}");
        double ghostX = ghost.Average(p => p.X);
        Assert.True(ghostX > W * 0.5, $"the ghost is drawn right of centre, toward the second segment ({ghostX:0})");
    }

    private static byte[] Render(GraphicsDevice gd, RenderScene scene, Vector3 from, Vector3 to, string file)
    {
        var camera = new Camera { Position = from };
        camera.LookAt(from, to);
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, W, H);
        Assert.Equal(W * H * 4, pixels.Length);
        byte[] png = PngWriter.Encode(W, H, pixels);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, file), png);
        if (Environment.GetEnvironmentVariable("GED_P2_EVIDENCE") is { Length: > 0 } evidence && Directory.Exists(evidence))
        {
            File.WriteAllBytes(Path.Combine(evidence, file), png);
        }

        return pixels;
    }

    private static List<(int X, int Y)> Pixels(byte[] px, byte r, byte g, byte b)
    {
        var hits = new List<(int, int)>();
        for (int i = 0; i < W * H; i++)
        {
            if (px[i * 4] == r && px[(i * 4) + 1] == g && px[(i * 4) + 2] == b)
            {
                hits.Add((i % W, i / W));
            }
        }

        return hits;
    }

    private static int Count(byte[] px, byte r, byte g, byte b) => Pixels(px, r, g, b).Count;

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}
