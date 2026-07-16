using System;
using System.IO;
using System.Linq;
using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// The four playtest button icons (Item 5): a green play glyph, doubled for the
/// multiplayer variants, plus an amber diamond badge for the "…from camera"
/// variants. Verifies they render, are distinct artwork, and dumps them side by
/// side to <c>tests/artifacts/play_icons.png</c> for inspection.
/// </summary>
public sealed class PlayIconArtifactTests
{
    private static readonly PlayIcon[] Icons =
    {
        PlayIcon.Level, PlayIcon.FromCamera, PlayIcon.Multi, PlayIcon.MultiFromCamera,
    };

    private static int OpaquePixels(byte[] rgba)
    {
        int n = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 0)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void Icons_Render_And_Are_Distinct()
    {
        byte[][] imgs = Icons.Select(i => PlayIconRenderer.Render(i, 24)).ToArray();

        foreach (byte[] img in imgs)
        {
            Assert.True(OpaquePixels(img) > 0, "icon drew nothing");
        }

        // All four are distinct artwork.
        for (int i = 0; i < imgs.Length; i++)
        {
            for (int j = i + 1; j < imgs.Length; j++)
            {
                Assert.False(imgs[i].AsSpan().SequenceEqual(imgs[j]), $"icons {Icons[i]} and {Icons[j]} are identical");
            }
        }

        // The diamond badge strictly adds coverage over the corresponding non-badge glyph.
        Assert.True(OpaquePixels(imgs[1]) > OpaquePixels(imgs[0]));  // FromCamera > Level
        Assert.True(OpaquePixels(imgs[3]) > OpaquePixels(imgs[2]));  // MultiFromCamera > Multi
    }

    [Fact]
    public void Dumps_Play_Icons_Artifact()
    {
        const int cell = 48;
        const int pad = 10;
        int w = (cell * Icons.Length) + (pad * (Icons.Length + 1));
        int h = cell + (pad * 2);
        var canvas = new byte[w * h * 4];

        // Toolbar-ish dark background.
        for (int p = 0; p < w * h; p++)
        {
            canvas[(p * 4) + 0] = 0x28;
            canvas[(p * 4) + 1] = 0x2A;
            canvas[(p * 4) + 2] = 0x30;
            canvas[(p * 4) + 3] = 0xFF;
        }

        for (int k = 0; k < Icons.Length; k++)
        {
            byte[] icon = PlayIconRenderer.Render(Icons[k], cell);
            int ox = pad + (k * (cell + pad));
            int oy = pad;
            for (int y = 0; y < cell; y++)
            {
                for (int x = 0; x < cell; x++)
                {
                    int si = ((y * cell) + x) * 4;
                    float a = icon[si + 3] / 255f;
                    if (a <= 0f)
                    {
                        continue;
                    }

                    int di = ((((oy + y) * w) + ox + x) * 4);
                    for (int c = 0; c < 3; c++)
                    {
                        canvas[di + c] = (byte)((icon[si + c] * a) + (canvas[di + c] * (1f - a)));
                    }
                }
            }
        }

        Directory.CreateDirectory(RenderTestSupport.ArtifactsDir);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "play_icons.png"), PngWriter.Encode(w, h, canvas));
    }
}
