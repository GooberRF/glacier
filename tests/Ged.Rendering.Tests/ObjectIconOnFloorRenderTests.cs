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
/// An object icon whose object stands on a floor: the glyph draws over the floor it stands on and the click at its centre
/// picks the object.
/// Object glyphs take their depth from a point pulled toward the camera by the glyph's half-height, so the floor the object
/// stands on no longer wins the glyph's centre pixel (and, seen from near floor level, no longer hides its lower half); the pick pass rasterizes the glyph quads
/// with culling off (as the colour pass does) and accepts the whole square of an object glyph. Writes a close-up artifact.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class ObjectIconOnFloorRenderTests
{
    [Fact]
    public void An_Icon_Standing_On_A_Floor_Draws_Whole_And_Picks_At_Its_Centre()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var scene = new RenderScene();
        Brush floor = BrushFactory.Create(new BrushCreateParams { Width = 12, Height = 0.5f, Depth = 12 }, 1);
        floor.Position = new Vec3(0, -0.25f, 0); // top face at y = 0
        BrushEmitter.Append(scene, new[] { floor }, BrushPickGranularity.Brush, solidFill: true);

        var respawn = new PickId(PickKind.Object, 7);
        var helper = new PickId(PickKind.Object, 8);
        scene.Billboards.Add(new Billboard(BillboardKind.Respawn, new Vector3(-1f, 0f, 0f), 0.4f, Palette.Rgba(255, 255, 255), respawn,
            (int)SceneBuilder.IconForKind(BillboardKind.Respawn)));
        scene.Billboards.Add(new Billboard(BillboardKind.WaypointHelper, new Vector3(1f, 0f, 0f), 0.4f, Palette.Rgba(255, 255, 255), helper,
            (int)SceneBuilder.IconForKind(BillboardKind.WaypointHelper)));

        const int W = 640, H = 480;
        var camera = new Camera { AspectRatio = (float)W / H };
        Vector3 eye = new(0f, 1.2f, -3.5f), target = Vector3.Zero;
        camera.LookAt(eye, target);
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, W, H);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "object_icons_on_floor.png"), PngWriter.Encode(W, H, pixels));

        using var renderer = new SceneRenderer(gd);
        using var pickTarget = gd.CreatePickTarget(W, H);
        using var gpu = new GpuScene(gd, scene, null);
        foreach ((Vector3 at, PickId id) in new[] { (new Vector3(-1f, 0f, 0f), respawn), (new Vector3(1f, 0f, 0f), helper) })
        {
            Assert.True(camera.WorldToScreen(at, W, H, out Vector2 px));

            // The centre (on the floor plane) and a point just below it, inside the floor, both pick the object.
            Assert.Equal(id, renderer.RenderPick(camera, gpu, pickTarget, (int)px.X, (int)px.Y));
            Assert.True(camera.WorldToScreen(at - new Vector3(0f, 0.1f, 0f), W, H, out Vector2 low));
            Assert.Equal(id, renderer.RenderPick(camera, gpu, pickTarget, (int)px.X, (int)low.Y));
        }
    }
}
