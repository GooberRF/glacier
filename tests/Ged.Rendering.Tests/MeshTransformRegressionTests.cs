using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.Tables;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Regression guard for the mesh world-matrix upload convention: a V3M instance
/// must render AT its placed position. When the world matrix is uploaded with the
/// wrong transpose the translation lands in the w row and meshes collapse toward
/// the world origin (the "meshes stretched to 0,0,0" bug) — this test catches that
/// by verifying that the pixels at an instance's location change when mesh
/// rendering is removed from the scene.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class MeshTransformRegressionTests
{
    private const int Size = 512;

    [Fact]
    public void MeshInstances_RenderAtTheirPlacedPosition()
    {
        string? path = RenderTestSupport.CorpusFile("dm02.rfl");
        if (path is null || RenderTestSupport.RfInstall is null)
        {
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            var options = new SceneBuildOptions
            {
                Entities = TryLoad(vfs, "entity.tbl", EntityCatalog.Load),
                Clutter = TryLoad(vfs, "clutter.tbl", ClutterCatalog.Load),
                Items = TryLoad(vfs, "items.tbl", ItemCatalog.Load),
                // Objects must stay on (mesh instances are emitted from object
                // sections); their billboards persist in both renders and cancel
                // out in the diff. Lines would only add noise.
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            };

            RflFile file = RflFile.Load(path);
            RenderScene scene = SceneBuilder.Build(file, options);
            Assert.True(scene.Meshes.Count > 0, "expected catalog-resolved mesh instances in dm02");

            MeshInstance mi = scene.Meshes[0];
            var center = new Vector3(mi.World.M41, mi.World.M42, mi.World.M43);
            var camera = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            camera.LookAt(center + new Vector3(1.5f, 1.0f, -1.5f), center);

            byte[] withMeshes = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, Size, Size);
            scene.Meshes.Clear();
            byte[] withoutMeshes = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, Size, Size);

            // The mesh sits at the look-at point, so the central window must differ
            // between the two renders. A collapsed/mistransformed mesh leaves the
            // center identical (it rendered somewhere else, or nowhere).
            int diff = CenterDiffCount(withMeshes, withoutMeshes, Size, window: Size / 3);
            Assert.True(diff > 200, $"mesh did not render at its placed position (center diff pixels: {diff})");
        }
        finally
        {
            vfs.Dispose();
        }
    }

    private static int CenterDiffCount(byte[] a, byte[] b, int size, int window)
    {
        int start = (size - window) / 2;
        int count = 0;
        for (int y = start; y < start + window; y++)
        {
            for (int x = start; x < start + window; x++)
            {
                int i = ((y * size) + x) * 4;
                int delta = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                if (delta > 12)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static T? TryLoad<T>(AssetVfs vfs, string name, Func<byte[], T> parse)
        where T : class
    {
        try
        {
            byte[]? data = vfs.ReadFile(name);
            return data is null ? null : parse(data);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
