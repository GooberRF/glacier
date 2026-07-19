using System.IO;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Visual evidence + gate for VFX effect rendering: renders two structurally distinct
/// stock .vfx through the same offscreen mesh pipeline the viewport/thumbnails use —
/// an additive, fullbright thruster flame and the textured jeep vehicle — to 128px PNG
/// artifacts under tests/artifacts/vfx, asserting each is non-trivial. Skips gracefully
/// when no RF install or no GPU device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class VfxRenderTests
{
    private static readonly string[] Effects = { "grabber_thrusterfx.vfx", "jeep.vfx" };

    private readonly ITestOutputHelper _out;

    public VfxRenderTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Renders_Two_Vfx_Effects_To_Artifacts()
    {
        if (RenderTestSupport.RfInstall is null)
        {
            _out.WriteLine("No RF install; VFX render skipped.");
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out string reason);
        if (gd is null)
        {
            _out.WriteLine($"No GPU device ({reason}); VFX render skipped.");
            return;
        }

        using AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "vfx");
        Directory.CreateDirectory(outDir);

        int rendered = 0;
        foreach (string effect in Effects)
        {
            if (vfs.LoadMesh(effect) is null)
            {
                _out.WriteLine($"{effect}: not present in this install; skipping.");
                continue;
            }

            byte[] png = MeshThumbnailRenderer.Render(gd, vfs, effect, size: 128);
            Assert.True(StbTextureDecoder.IsPng(png));
            DecodedTexture decoded = StbTextureDecoder.Decode(png);
            Assert.Equal(128, decoded.Primary.Width);

            byte[] rgba = ToRgba(decoded);
            Assert.True(RenderTestSupport.IsNonTrivial(rgba, out int distinct),
                $"{effect} render was trivial ({distinct} colors).");

            string path = Path.Combine(outDir, Path.GetFileNameWithoutExtension(effect) + ".png");
            File.WriteAllBytes(path, png);
            _out.WriteLine($"{effect}: {distinct} colors -> {path}");
            rendered++;
        }

        Assert.True(rendered > 0, "no VFX effects were available to render");
    }

    private static byte[] ToRgba(DecodedTexture tex)
    {
        TextureImage img = tex.Primary;
        var rgba = new byte[img.Width * img.Height * 4];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                (byte r, byte g, byte b, byte a) = img.GetPixel(x, y);
                int o = ((y * img.Width) + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }

        return rgba;
    }
}
