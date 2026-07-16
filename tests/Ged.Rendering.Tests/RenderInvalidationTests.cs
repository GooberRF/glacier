using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// RENDERED-level regression tests (the class the earlier model-level
/// tests missed): render the brush-overlay scene to pixels and assert the image actually
/// changes when the render-affecting state changes (clipped-face filter, selection), and that
/// a brush's faces render TEXTURED (not white) when a VFS is mounted. These guard the
/// scene → GPU → pixels path that the "green-but-meaningless" model assertions never exercised.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class RenderInvalidationTests
{
    private static Brush Box(int uid, Vec3 pos) => new()
    {
        Uid = uid,
        Position = pos,
        Rotation = Mat3.Identity,
        Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "Rck_Default.tga"),
    };

    private static Camera FrontCamera() =>
        Cam(new Vector3(0f, 1.5f, -6f), new Vector3(0f, 0f, 0f));

    private static Camera Cam(Vector3 eye, Vector3 target)
    {
        var c = new Camera { Position = eye, AspectRatio = 320f / 240f };
        c.LookAt(eye, target);
        return c;
    }

    private static byte[] RenderOverlay(GraphicsDevice gd, RenderScene scene, AssetVfs? vfs, Camera cam) =>
        OffscreenRenderer.Render(gd, scene, vfs, cam, RenderMode.JustTextures, 320, 240);

    private static bool PixelsDiffer(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return true;
        }

        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                changed++;
            }
        }

        return changed > a.Length / 500; // > ~0.2% of channel bytes differ
    }

    // ---- 0i: the "Show Clipped Brush Faces" filter changes the rendered overlay ----

    [Fact]
    public void Clipped_Face_Filter_Changes_The_Rendered_Overlay()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        Brush box = Box(1, Vec3.Zero);

        // OFF (default): a survival map that hides half the faces — the overlay shows less.
        var survival = new System.Collections.Generic.Dictionary<int, bool[]> { [1] = new[] { false, false, false, true, true, true } };
        var sceneOff = new RenderScene();
        BrushEmitter.Append(sceneOff, new[] { box }, BrushPickGranularity.Brush, survivingFaces: survival);

        // ON: no survival map — every face draws.
        var sceneOn = new RenderScene();
        BrushEmitter.Append(sceneOn, new[] { box }, BrushPickGranularity.Brush, survivingFaces: null);

        Camera cam = FrontCamera();
        byte[] off = RenderOverlay(gd, sceneOff, null, cam);
        byte[] on = RenderOverlay(gd, sceneOn, null, cam);

        Assert.True(PixelsDiffer(off, on),
            "toggling Show Clipped Brush Faces must change the rendered image (it did not — the filter never reached the render).");
    }

    // ---- 0j: selecting a different brush changes the rendered overlay ----

    [Fact]
    public void Selecting_A_Different_Brush_Changes_The_Render()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var a = Box(1, new Vec3(-1.5f, 0, 0));
        var b = Box(2, new Vec3(1.5f, 0, 0));
        var brushes = new[] { a, b };

        var sceneA = new RenderScene();
        BrushEmitter.Append(sceneA, brushes, BrushPickGranularity.Brush, selectedBrushes: new[] { 1 });
        var sceneB = new RenderScene();
        BrushEmitter.Append(sceneB, brushes, BrushPickGranularity.Brush, selectedBrushes: new[] { 2 });

        Camera cam = Cam(new Vector3(0f, 2.5f, -7f), Vector3.Zero);
        byte[] selA = RenderOverlay(gd, sceneA, null, cam);
        byte[] selB = RenderOverlay(gd, sceneB, null, cam);

        Assert.True(PixelsDiffer(selA, selB),
            "selecting a different brush must change the rendered highlight (it did not — the selection never reached the render).");
    }

    // ---- 0k: a brush's faces render TEXTURED (not white) with a mounted VFS ----

    [Fact]
    public void Brush_Overlay_Renders_Textured_When_A_Vfs_Is_Mounted()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null || RenderTestSupport.RfInstall is null)
        {
            return; // needs both a device and a real install to prove texture binding
        }

        using AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);

        Brush box = Box(1, Vec3.Zero);
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush, solidFill: true);

        Camera cam = FrontCamera();
        byte[] textured = RenderOverlay(gd, scene, vfs, cam);   // Rck_Default.tga bound
        byte[] untextured = RenderOverlay(gd, scene, null, cam); // no VFS → white fallback

        // If the overlay renderer binds the face texture, the two images differ — the brush
        // face shows the texture, not a flat white fill.
        Assert.True(PixelsDiffer(textured, untextured),
            "the brush overlay rendered identically with and without a VFS — face textures are not bound in the overlay path.");
    }
}
