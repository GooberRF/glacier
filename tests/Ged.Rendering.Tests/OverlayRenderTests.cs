using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Offscreen-render evidence for the editing overlays: a mover path spline with a
/// time-scrubbed ghost, a group-mirrored room, and the shape overlays (nav disc,
/// decal box, cutscene camera cone). Each writes a PNG artifact and asserts the
/// image is non-trivial. Skips gracefully when no D3D11 device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class OverlayRenderTests
{
    [Fact]
    public void MoverPathAndGhost_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var scene = new RenderScene();
        Brush mover = Box(1, new Vec3(0, 0, 0), 4f);
        BrushEmitter.Append(scene, new[] { mover }, BrushPickGranularity.Brush, solidFill: true);

        var keyframes = new List<Vec3> { new(0, 0, 0), new(5, 4, 1), new(9, 1, 4), new(12, 3, 2) };
        scene.Lines.AddRange(OverlayBuilder.Path(keyframes, startIndex: 0));

        // Ghost of the mover geometry halfway along the path.
        Vec3 sampled = OverlayBuilder.SamplePath(keyframes, 0.5f);
        scene.Lines.AddRange(OverlayBuilder.MoverGhost(new[] { mover }, keyframes[0], sampled));

        RenderAndSave(gd, scene, new Vector3(6, 7, -7), new Vector3(6, 2, 2), "mover_path_ghost.png");
    }

    [Fact]
    public void GroupMirroredRoom_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var scene = new RenderScene();
        var brushes = new List<Brush>();

        // An asymmetric L of blocks (the original room fragment).
        brushes.Add(Box(1, new Vec3(-6, 0, 0), 2f));
        brushes.Add(Box(2, new Vec3(-6, 0, 3), 2f));
        brushes.Add(Box(3, new Vec3(-3, 0, 0), 2f));

        // Mirror clones across X through the origin (the world YZ plane).
        var mirrored = new List<Brush>();
        foreach (Brush b in brushes)
        {
            Brush clone = GeometryClone.Deep(b);
            clone.Uid = b.Uid + 100;
            GroupMirror.MirrorBrush(clone, Vec3.Zero, axis: 0);
            mirrored.Add(clone);
        }

        var all = new List<Brush>(brushes);
        all.AddRange(mirrored);
        BrushEmitter.Append(scene, all, BrushPickGranularity.Brush, solidFill: true);

        // The mirror plane (X = 0) as a faint quad outline.
        uint plane = Palette.Rgba(120, 120, 140);
        scene.Lines.Add(new LineSegment(new Vector3(0, -3, -3), new Vector3(0, 6, -3), plane));
        scene.Lines.Add(new LineSegment(new Vector3(0, 6, -3), new Vector3(0, 6, 6), plane));
        scene.Lines.Add(new LineSegment(new Vector3(0, 6, 6), new Vector3(0, -3, 6), plane));
        scene.Lines.Add(new LineSegment(new Vector3(0, -3, 6), new Vector3(0, -3, -3), plane));

        RenderAndSave(gd, scene, new Vector3(0, 12, -16), new Vector3(0, 1, 2), "group_mirror.png");
    }

    [Fact]
    public void ShapeOverlays_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var scene = new RenderScene();

        // A ground reference block so the overlays sit over geometry.
        BrushEmitter.Append(scene, new[] { Flat(1, new Vec3(0, -1, 4), 16f, 0.4f, 16f) }, BrushPickGranularity.Brush, solidFill: true);

        scene.Lines.AddRange(OverlayBuilder.Disc(new Vec3(-5, 0, 4), radius: 3f));
        scene.Lines.AddRange(OverlayBuilder.Box(new Vec3(0, 0, 4), Mat3.Identity, new Vec3(3, 3, 1)));
        scene.Lines.AddRange(OverlayBuilder.CameraCone(new Vec3(5, 1, 0), Mat3.Identity, fovDegrees: 50f, length: 5f));

        RenderAndSave(gd, scene, new Vector3(0, 10, -14), new Vector3(0, 0, 4), "shape_overlays.png");
    }

    private static void RenderAndSave(GraphicsDevice gd, RenderScene scene, Vector3 from, Vector3 to, string file)
    {
        var camera = new Camera { Position = from };
        camera.LookAt(from, to);
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);

        Assert.Equal(640 * 480 * 4, pixels.Length);
        Assert.True(RenderTestSupport.IsNonTrivial(pixels, out int distinct), $"{file} was trivial ({distinct} colors).");

        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, file), PngWriter.Encode(640, 480, pixels));
    }

    private static Brush Box(int uid, Vec3 pos, float size)
    {
        Brush b = BrushFactory.Create(new BrushCreateParams { Width = size, Height = size, Depth = size }, uid);
        b.Position = pos;
        return b;
    }

    private static Brush Flat(int uid, Vec3 pos, float w, float h, float d)
    {
        Brush b = BrushFactory.Create(new BrushCreateParams { Width = w, Height = h, Depth = d }, uid);
        b.Position = pos;
        return b;
    }
}
