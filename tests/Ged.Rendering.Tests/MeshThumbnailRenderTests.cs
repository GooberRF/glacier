using System.IO;
using Ged.Core.Assets;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Evidence + gate for the mesh-thumbnail renderer: renders two fixture meshes
/// through the GPU offscreen path to framed 128px PNG artifacts, exercises the
/// ThumbnailCache round-trip (second call returns identical bytes), and validates
/// the CPU flat-shaded fallback with no device. Skips the GPU parts gracefully
/// when no D3D11 device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class MeshThumbnailRenderTests
{
    private static readonly string[] Meshes = { "wallcomputer1.v3m", "LightOfficeCan01.v3m" };

    private readonly ITestOutputHelper _out;

    public MeshThumbnailRenderTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Renders_Two_Mesh_Thumbnails_To_Artifacts()
    {
        if (RenderTestSupport.RepoRoot is null)
        {
            return;
        }

        if (RenderTestSupport.FixtureFile("mesh", "wallcomputer1.v3m") is null ||
            RenderTestSupport.FixtureFile("mesh", "LightOfficeCan01.v3m") is null)
        {
            return; // retail-derived mesh fixtures not present
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out string reason);
        if (gd is null)
        {
            _out.WriteLine($"No GPU device ({reason}); GPU thumbnail render skipped.");
            return;
        }

        using AssetVfs vfs = FixtureVfs();
        string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "mesh");
        Directory.CreateDirectory(outDir);

        foreach (string mesh in Meshes)
        {
            byte[] png = MeshThumbnailRenderer.Render(gd, vfs, mesh, size: 128);
            Assert.True(StbTextureDecoder.IsPng(png));
            DecodedTexture decoded = StbTextureDecoder.Decode(png);
            Assert.Equal(128, decoded.Primary.Width);
            Assert.Equal(128, decoded.Primary.Height);

            byte[] rgba = ToRgba(decoded);
            Assert.True(RenderTestSupport.IsNonTrivial(rgba, out int distinct),
                $"{mesh} thumbnail was trivial ({distinct} colors).");

            string path = Path.Combine(outDir, Path.GetFileNameWithoutExtension(mesh) + "_thumb.png");
            File.WriteAllBytes(path, png);
            _out.WriteLine($"{mesh}: {distinct} colors -> {path}");
        }
    }

    [Fact]
    public void ThumbnailCache_Reuses_Rendered_Mesh_Png()
    {
        if (RenderTestSupport.RepoRoot is null)
        {
            return;
        }

        if (RenderTestSupport.FixtureFile("mesh", "Disk.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);

        using AssetVfs vfs = FixtureVfs();
        string cacheDir = Path.Combine(Path.GetTempPath(), "ged_meshthumb_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ThumbnailCache(cacheDir, maxSize: 128);
            byte[] first = MeshThumbnailRenderer.GetOrRender(cache, gd, vfs, "Disk.v3m", "Disk.v3m", "v1");
            byte[] second = MeshThumbnailRenderer.GetOrRender(cache, gd, vfs, "Disk.v3m", "Disk.v3m", "v1");

            Assert.True(StbTextureDecoder.IsPng(first));
            Assert.Equal(first, second); // cache hit -> identical bytes
        }
        finally
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void Cpu_Fallback_Produces_A_NonTrivial_Raster()
    {
        if (RenderTestSupport.RepoRoot is null)
        {
            return;
        }

        string? meshPath = RenderTestSupport.FixtureFile("mesh", "wallcomputer1.v3m");
        if (meshPath is null)
        {
            return; // retail-derived mesh fixture not present
        }

        V3dFile mesh = V3dReader.Read(File.ReadAllBytes(meshPath));

        byte[] png = MeshThumbnailRenderer.RenderCpu(mesh, size: 128);
        Assert.True(StbTextureDecoder.IsPng(png));
        DecodedTexture decoded = StbTextureDecoder.Decode(png);
        Assert.True(RenderTestSupport.IsNonTrivial(ToRgba(decoded), out int distinct),
            $"CPU raster was trivial ({distinct} colors).");

        string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "mesh");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "wallcomputer1_cpu.png"), png);
    }

    private static AssetVfs FixtureVfs()
    {
        // Mount both fixture roots: committed tests/fixtures (synthetic) and research/fixtures
        // (retail-derived, kept out of the public repo).
        var sources = new List<IAssetSource>();
        foreach (string dir in RenderTestSupport.FixtureDirs("mesh"))
        {
            sources.Add(new DirectoryAssetSource(dir));
        }

        foreach (string dir in RenderTestSupport.FixtureDirs("tex"))
        {
            sources.Add(new DirectoryAssetSource(dir));
        }

        return new AssetVfs(sources.ToArray());
    }

    private static byte[] ToRgba(DecodedTexture tex)
    {
        var img = tex.Primary;
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
