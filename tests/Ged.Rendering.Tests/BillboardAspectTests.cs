using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Original-icon billboard aspect correction. RED ships two NON-SQUARE object icons —
/// <c>Icon_MultiPlayerStart.tga</c> (32×64, h/w = 2.0) and <c>Icon_Keyframe_Gold.tga</c>
/// (64×32, h/w = 0.5); every other ui.vpp / alpinefaction.vpp icon is square. The square
/// atlas-cell blit distorts them, so <see cref="IconAtlas.Compose"/> reports each resolved
/// original's aspect and the billboard emission renders those quads at STANDARD WIDTH with
/// the HEIGHT scaled to the true ratio (respawn = 2× height). The quad expansion maps
/// <c>Billboard.Size</c> to the height half-extent and <c>Size × Aspect</c> to the width
/// half-extent, and the GPU pick pass rasterizes the same quads — so asserting Size/Aspect
/// asserts both the visual and the hit extent. Drawn glyphs (square by design) never change.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BillboardAspectTests
{
    private static RflFile LevelWithRespawn()
    {
        var rp = new MpRespawnPointsSection();
        rp.Points.Add(new MpRespawnPoint
        {
            Uid = 42,
            Position = new Vec3(1, 2, 3),
            Rotation = Mat3.Identity,
            ScriptName = "spawn",
            Team = 0,
            RedTeam = 1,
            BlueTeam = 1,
            Bot = 0,
        });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "aspect.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.MpRespawnPoints, Array.Empty<byte>()) { Content = rp, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static Billboard RespawnBillboard(RenderScene scene) =>
        scene.Billboards.Single(b => b.Kind == BillboardKind.Respawn);

    [Fact]
    public void Respawn_Under_Original_Icons_Renders_At_Twice_The_Height_Standard_Width()
    {
        RflFile file = LevelWithRespawn();
        const float size = 0.4f;

        // Drawn-glyph mode: the square GED cell renders as a square quad.
        RenderScene drawn = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = size });
        Billboard d = RespawnBillboard(drawn);
        Assert.Equal(size, d.Size);      // height half-extent (quad cy = ±Size)
        Assert.Equal(1f, d.Aspect);      // width half-extent = Size × Aspect = standard

        // Original-icons mode with the real MP-respawn ratio (32×64 → h/w = 2): the quad
        // doubles in height while the width stays the standard billboard size.
        RenderScene original = SceneBuilder.Build(file, new SceneBuildOptions
        {
            BillboardSize = size,
            UseOriginalIcons = true,
            OriginalIconAspects = new Dictionary<EditorIcon, float> { [EditorIcon.Respawn] = 2f },
        });
        Billboard o = RespawnBillboard(original);
        Assert.Equal(size * 2f, o.Size);              // 2× height vs drawn-glyph mode
        Assert.Equal(0.5f, o.Aspect);                 // width multiplier compensates…
        Assert.Equal(size, o.Size * o.Aspect, 5);     // …so the width half-extent stays standard
    }

    [Fact]
    public void Square_And_Unmapped_Icons_Stay_Square_Under_Original_Icons()
    {
        RflFile file = LevelWithRespawn();
        const float size = 0.4f;

        // Aspects resolved for OTHER icons (or missing entirely) leave the respawn square.
        RenderScene other = SceneBuilder.Build(file, new SceneBuildOptions
        {
            BillboardSize = size,
            UseOriginalIcons = true,
            OriginalIconAspects = new Dictionary<EditorIcon, float> { [EditorIcon.Light] = 1f },
        });
        Billboard b = RespawnBillboard(other);
        Assert.Equal(size, b.Size);
        Assert.Equal(1f, b.Aspect);

        // Aspects present but original icons OFF: the drawn set is square — never scaled.
        RenderScene off = SceneBuilder.Build(file, new SceneBuildOptions
        {
            BillboardSize = size,
            UseOriginalIcons = false,
            OriginalIconAspects = new Dictionary<EditorIcon, float> { [EditorIcon.Respawn] = 2f },
        });
        Billboard boff = RespawnBillboard(off);
        Assert.Equal(size, boff.Size);
        Assert.Equal(1f, boff.Aspect);
    }

    [Fact]
    public void Compose_Reports_The_Resolved_Originals_Height_Over_Width()
    {
        // An 8×16 stand-in for the respawn icon (h/w = 2) and a square 8×8 light.
        TextureImage tall = Solid(8, 16);
        TextureImage square = Solid(8, 8);

        IconAtlas.Compose(
            icon => icon switch
            {
                EditorIcon.Respawn => tall,
                EditorIcon.Light => square,
                _ => null,
            },
            out IReadOnlyDictionary<EditorIcon, float> aspects);

        Assert.Equal(2f, aspects[EditorIcon.Respawn]);
        Assert.Equal(1f, aspects[EditorIcon.Light]);
        Assert.False(aspects.ContainsKey(EditorIcon.Trigger)); // unresolved → absent → square
    }

    private static TextureImage Solid(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = 200;
        }

        return new TextureImage(w, h, px);
    }

    [Fact]
    public void Visual_Respawn_Quad_Doubles_In_Height_Not_Width()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // A solid magenta marker in the Respawn cell so the footprint is measurable.
        var marker = new byte[32 * 32 * 4];
        for (int i = 0; i < marker.Length; i += 4)
        {
            marker[i] = 255;
            marker[i + 3] = 255;
            marker[i + 2] = 255;
        }

        byte[] atlas = IconAtlas.Compose(icon =>
            icon == EditorIcon.Respawn ? new TextureImage(32, 32, marker) : null);
        gd.SetIconAtlas(atlas);

        // Left: the square quad (pre-fix). Right: the aspect-corrected quad the builder now
        // emits for the 32×64 original (Size × 2, Aspect ½) — same width, twice the height.
        uint white = Palette.Rgba(255, 255, 255, 255);
        var scene = new RenderScene();
        scene.Billboards.Add(new Billboard(
            BillboardKind.Respawn, new Vector3(-1.4f, 0, 6f), 0.6f, white, Picking.PickId.None, (int)EditorIcon.Respawn));
        scene.Billboards.Add(new Billboard(
            BillboardKind.Respawn, new Vector3(1.4f, 0, 6f), 0.6f * 2f, white, Picking.PickId.None, (int)EditorIcon.Respawn, Aspect: 0.5f));

        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        const int w = 512, h = 384;
        byte[] px = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, w, h);
        gd.SetIconAtlas(IconAtlas.Build());

        // Measure each half's magenta bounding box.
        (int W, int H) left = Footprint(px, w, h, 0, w / 2);
        (int W, int H) right = Footprint(px, w, h, w / 2, w);
        Assert.True(left.H > 10 && right.H > 10, $"markers not rendered (left {left}, right {right})");
        Assert.True(Math.Abs(right.W - left.W) <= 2, $"width changed: left {left.W}px vs right {right.W}px");
        float ratio = right.H / (float)left.H;
        Assert.True(Math.Abs(ratio - 2f) < 0.15f, $"height ratio {ratio:F2} (expected ≈2.0)");

        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(RenderTestSupport.ArtifactsDir, "respawn_icon_aspect.png"),
            PngWriter.Encode(w, h, px));
    }

    private static (int W, int H) Footprint(byte[] px, int w, int h, int x0, int x1)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = ((y * w) + x) * 4;
                if (px[i] > 150 && px[i + 1] < 90 && px[i + 2] > 150) // magenta
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? (0, 0) : (maxX - minX + 1, maxY - minY + 1);
    }
}
