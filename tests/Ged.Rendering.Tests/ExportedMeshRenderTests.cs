using System.IO;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Mesh.Export;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Evidence + gate for brush→mesh ("To Mesh") export: exports two
/// textured box brushes to a .v3m via <see cref="BrushMeshExport"/>, renders the
/// result through the shared offscreen mesh path, and writes a framed PNG artifact.
/// Skips the GPU render gracefully when no D3D11 device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class ExportedMeshRenderTests
{
    private readonly ITestOutputHelper _out;

    public ExportedMeshRenderTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Exported_Mesh_Renders_To_Artifact()
    {
        if (RenderTestSupport.RepoRoot is null)
        {
            return;
        }

        if (RenderTestSupport.FixtureFile("tex", "mtl_bluefiller01.tga") is null ||
            RenderTestSupport.FixtureFile("tex", "Disk_P01.tga") is null)
        {
            return; // retail-derived fixtures not present
        }

        // Two textured box brushes -> one batched V3M.
        var brushes = new[]
        {
            Box("mtl_bluefiller01.tga", new Vec3(-1.2f, 0f, 0f)),
            Box("Disk_P01.tga", new Vec3(1.2f, 0f, 0f)),
        };
        V3dFile mesh = BrushMeshExport.ToV3d("exported.v3m", brushes, resetOrigin: true, out _);

        string meshDir = Path.Combine(Path.GetTempPath(), "ged_exportmesh_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(meshDir);
        try
        {
            File.WriteAllBytes(Path.Combine(meshDir, "exported.v3m"), V3dWriter.Write(mesh));

            // Round-trips as a valid V3M.
            V3dFile reparsed = V3dReader.Read(File.ReadAllBytes(Path.Combine(meshDir, "exported.v3m")));
            Assert.Equal(2, reparsed.Submeshes[0].Lods[0].Batches.Count);

            using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out string reason);
            if (gd is null)
            {
                _out.WriteLine($"No GPU device ({reason}); exported-mesh render skipped.");
                return;
            }

            var sources = new List<IAssetSource> { new DirectoryAssetSource(meshDir) };
            foreach (string dir in RenderTestSupport.FixtureDirs("tex"))
            {
                sources.Add(new DirectoryAssetSource(dir));
            }

            using var vfs = new AssetVfs(sources.ToArray());

            byte[] png = MeshThumbnailRenderer.Render(gd, vfs, "exported.v3m", size: 256);
            Assert.True(StbTextureDecoder.IsPng(png));
            Assert.True(RenderTestSupport.IsNonTrivial(ToRgba(StbTextureDecoder.Decode(png)), out int distinct),
                $"exported-mesh render was trivial ({distinct} colors).");

            string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "mesh");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "exported_mesh.png"), png);
            _out.WriteLine($"exported mesh: {distinct} colors");
        }
        finally
        {
            try
            {
                Directory.Delete(meshDir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    private static Brush Box(string texture, Vec3 position)
    {
        Geometry g = BrushFactory.Box(2f, 2f, 2f, 0, 0, 0, texture);
        return new Brush { Uid = 1, Position = position, Rotation = Mat3.Identity, Geometry = g };
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
