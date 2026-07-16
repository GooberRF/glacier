using System.IO;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.IO.Tex;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Item 3 — the transform-drag indicator LABEL (distance / degrees / scale %) must render on the
/// depth-disabled on-top channel like the gizmo/indicator lines, so it is never hidden behind
/// geometry between the camera and the pivot. Proves it with an opaque wall standing between the
/// camera and the label: an <see cref="Billboard.OnTop"/> label shows THROUGH the wall; the same
/// label without the flag is occluded.
/// </summary>
public sealed class OnTopLabelRenderTests
{
    private const int W = 512;
    private const int H = 512;

    [Fact]
    public void OnTop_Label_Renders_Over_A_Wall_Between_Camera_And_Pivot()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return; // no D3D11 device — skip gracefully
        }

        var camera = new Camera { Position = Vector3.Zero, AspectRatio = (float)W / H };
        camera.LookAt(Vector3.Zero, new Vector3(0, 0, 1)); // look down +Z at the wall (z=5) and label (z=10)

        byte[] onTop = Render(gd, camera, onTop: true);
        byte[] normal = Render(gd, camera, onTop: false);

        // The label glyphs are tinted magenta (a marker the white wall + dark background never
        // produce) so the count isolates the label from everything else.
        int onTopGlyph = CountMagenta(onTop);
        int normalGlyph = CountMagenta(normal);

        // The on-top label shows its glyph pixels over the wall...
        Assert.True(onTopGlyph > 30, $"on-top label must be visible over the wall ({onTopGlyph} glyph px)");
        // ...while a plain depth-tested label at the same spot is occluded by the wall.
        Assert.True(normalGlyph < onTopGlyph / 4,
            $"a normal (depth-tested) label must be occluded by the wall (onTop={onTopGlyph}, normal={normalGlyph})");

        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "transform_indicators");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "label_ontop_over_wall.png"), PngWriter.Encode(W, H, onTop));
        File.WriteAllBytes(Path.Combine(dir, "label_occluded_by_wall.png"), PngWriter.Encode(W, H, normal));
    }

    private static byte[] Render(GraphicsDevice gd, Camera camera, bool onTop)
    {
        RenderScene scene = RenderTestSupport.QuadScene(); // an opaque wall quad facing the camera at z=5
        (int w, int h, byte[] rgba) = LabelBitmap.Render("12.0 M", scale: 3, pad: 2);
        const string key = "$test:onTopLabel";
        scene.InlineTextures[key] = new InlineTexture(w, h, rgba);
        scene.Billboards.Add(new Billboard(
            BillboardKind.Vertex, new Vector3(0, 0, 10f), 1.4f, Palette.Rgba(255, 0, 255), default,
            TextureName: key, Aspect: h > 0 ? w / (float)h : 1f, OnTop: onTop));

        return OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, W, H);
    }

    private static int CountMagenta(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] > 200 && rgba[i + 1] < 80 && rgba[i + 2] > 200)
            {
                count++;
            }
        }

        return count;
    }
}
