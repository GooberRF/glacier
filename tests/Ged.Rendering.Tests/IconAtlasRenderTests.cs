using System;
using System.IO;
using System.Numerics;
using Ged.Core.IO.Tex;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Gates for the original per-type object-icon atlas: the atlas builds to the
/// expected size and is non-blank, a legend PNG is dumped, and a scene of distinct
/// icon billboards renders (≥8 icon types visible).
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class IconAtlasRenderTests
{
    [Fact]
    public void Atlas_Builds_And_Legend_Is_Dumped()
    {
        byte[] atlas = IconAtlas.Build();
        Assert.Equal(IconAtlas.Width * IconAtlas.Height * 4, atlas.Length);

        // Non-blank: some pixels have coverage.
        int opaque = 0;
        for (int i = 3; i < atlas.Length; i += 4)
        {
            if (atlas[i] > 40)
            {
                opaque++;
            }
        }

        Assert.True(opaque > 500, $"atlas looks blank ({opaque} inked pixels).");

        // Composite over mid-grey so the white-core/dark-rim icons are visible as a legend.
        var legend = new byte[atlas.Length];
        for (int i = 0; i < atlas.Length; i += 4)
        {
            float a = atlas[i + 3] / 255f;
            for (int c = 0; c < 3; c++)
            {
                legend[i + c] = (byte)Math.Clamp((atlas[i + c] * a) + (90 * (1 - a)), 0, 255);
            }

            legend[i + 3] = 255;
        }

        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "icon_atlas_legend.png"),
            PngWriter.Encode(IconAtlas.Width, IconAtlas.Height, legend));
    }

    [Fact]
    public void Distinct_Icons_Render_In_A_Scene()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var scene = new RenderScene();
        (EditorIcon icon, uint tint)[] set =
        {
            (EditorIcon.Light, Palette.Rgba(255, 230, 120)),
            (EditorIcon.AmbientSound, Palette.Rgba(120, 200, 255)),
            (EditorIcon.Event, Palette.Rgba(255, 150, 90)),
            (EditorIcon.Trigger, Palette.Rgba(200, 160, 255)),
            (EditorIcon.PlayerStart, Palette.Rgba(120, 255, 140)),
            (EditorIcon.ParticleEmitter, Palette.Rgba(255, 200, 80)),
            (EditorIcon.BoltEmitter, Palette.Rgba(140, 180, 255)),
            (EditorIcon.NavPoint, Palette.Rgba(255, 255, 255)),
            (EditorIcon.CutsceneCamera, Palette.Rgba(220, 220, 220)),
            (EditorIcon.Target, Palette.Rgba(255, 120, 120)),
            (EditorIcon.Entity, Palette.Rgba(180, 255, 180)),
            (EditorIcon.Item, Palette.Rgba(255, 240, 140)),
        };

        for (int i = 0; i < set.Length; i++)
        {
            float gx = ((i % 4) - 1.5f) * 1.6f;
            float gy = ((i / 4) - 1f) * 1.6f;
            scene.Billboards.Add(new Billboard(
                BillboardKind.Other, new Vector3(gx, gy, 6f), 0.6f, set[i].tint, PickId.None, (int)set[i].icon));
        }

        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        byte[] px = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, 640, 480);

        Assert.True(RenderTestSupport.IsNonTrivial(px, out int distinct), $"icon scene was trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "object_icons.png"),
            PngWriter.Encode(640, 480, px));
    }

    [Fact]
    public void Composited_Original_Icon_Renders_Right_Side_Up()
    {
        // Regression for the billboard V-flip: a synthetic "original" icon whose TOP
        // half is opaque red and bottom half transparent must render with its opaque
        // pixels in the TOP half of the glyph's screen footprint. Before the fix the
        // atlas was sampled bottom-up, so text/asymmetric glyphs (DECAL, the Teleport
        // T, the light bulb) rendered upside-down.
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // Build the atlas with the Trigger cell replaced by a top=red / bottom=clear marker.
        const int n = 32;
        var marker = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = ((y * n) + x) * 4;
                bool top = y < n / 2;
                marker[i] = 255;
                marker[i + 1] = 0;
                marker[i + 2] = 0;
                marker[i + 3] = top ? (byte)255 : (byte)0;
            }
        }

        byte[] atlas = IconAtlas.Compose(icon =>
            icon == EditorIcon.Trigger ? new TextureImage(n, n, marker) : null);
        gd.SetIconAtlas(atlas);

        var scene = new RenderScene();
        scene.Billboards.Add(new Billboard(
            BillboardKind.Other, new Vector3(0, 0, 6f), 1.2f,
            Palette.Rgba(255, 255, 255, 255), PickId.None, (int)EditorIcon.Trigger));

        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        const int w = 256, h = 256;
        byte[] px = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, w, h);
        gd.SetIconAtlas(IconAtlas.Build());

        // Count opaque-red pixels in the top vs bottom half of the image.
        int topRed = 0, botRed = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                if (px[i] > 150 && px[i + 1] < 90 && px[i + 2] < 90)
                {
                    if (y < h / 2)
                    {
                        topRed++;
                    }
                    else
                    {
                        botRed++;
                    }
                }
            }
        }

        Assert.True(topRed > 200, $"expected the red marker in the top half; topRed={topRed}");
        Assert.True(topRed > botRed * 4,
            $"glyph rendered upside-down: topRed={topRed} botRed={botRed}");
    }

    [Fact]
    public void Original_Icons_From_Install_Render()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null || RenderTestSupport.RfInstall is null)
        {
            return; // needs a device + a real RF install (ui.vpp)
        }

        using var vfs = Ged.Core.Assets.GameMount.Mount(RenderTestSupport.RfInstall);

        // Compose the atlas from RED's original bitmaps (per-icon fallback to GED).
        int resolved = 0;
        byte[] atlas = IconAtlas.Compose(icon =>
        {
            if (!IconAtlas.OriginalFileNames.TryGetValue(icon, out string? name))
            {
                return null;
            }

            try
            {
                Ged.Core.IO.Tex.TextureImage? img = vfs.LoadTexture(name)?.Primary;
                if (img is not null)
                {
                    resolved++;
                }

                return img;
            }
            catch
            {
                return null;
            }
        });

        if (resolved == 0)
        {
            return; // this install lacks the icon TGAs — nothing to assert
        }

        gd.SetIconAtlas(atlas);

        // Untinted (white) object glyphs so the original colours pass through.
        var scene = new RenderScene();
        EditorIcon[] icons =
        {
            EditorIcon.Light, EditorIcon.AmbientSound, EditorIcon.Trigger, EditorIcon.Target,
            EditorIcon.ParticleEmitter, EditorIcon.BoltEmitter, EditorIcon.NavPoint, EditorIcon.Decal,
        };
        uint white = Palette.Rgba(255, 255, 255, 255);
        for (int i = 0; i < icons.Length; i++)
        {
            float gx = ((i % 4) - 1.5f) * 1.6f;
            float gy = ((i / 4) - 0.5f) * 1.6f;
            scene.Billboards.Add(new Billboard(BillboardKind.Other, new Vector3(gx, gy, 6f), 0.7f, white, PickId.None, (int)icons[i]));
        }

        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        byte[] px = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, 640, 480);
        Assert.True(RenderTestSupport.IsNonTrivial(px, out _));
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "object_icons_original.png"),
            PngWriter.Encode(640, 480, px));

        // Restore the GED default atlas so other GPU-collection tests are unaffected.
        gd.SetIconAtlas(IconAtlas.Build());
    }
}
