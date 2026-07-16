using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// End-to-end offscreen render of a real corpus level in every render mode. Uses
/// the real RF install for textures when present (falls back to white geometry
/// otherwise), asserts each frame is non-trivial, and writes PNGs to
/// tests/artifacts for human inspection. Skips gracefully when the corpus or a
/// D3D device is unavailable.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class CorpusOffscreenTests
{
    private const int Size = 512;

    [Fact]
    public void Dm01_RendersEveryMode()
    {
        string? path = RenderTestSupport.CorpusFile("dm01.rfl");
        if (path is null)
        {
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        AssetVfs? vfs = RenderTestSupport.RfInstall is null ? null : GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            RflFile file = RflFile.Load(path);
            RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());
            Vector3 center = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
            GridBuilder.Append(scene, center, 40f, 2f, 0.8f, scene.Bounds.P1.Y);

            var camera = new Camera { Projection = CameraProjection.Perspective };
            camera.Frame(scene.Bounds);

            RenderMode[] modes =
            {
                RenderMode.JustTextures,
                RenderMode.TexturesAndLightmaps,
                RenderMode.JustLightmaps,
                RenderMode.RoomColors,
                RenderMode.Wireframe,
                RenderMode.SeeThrough,
            };

            foreach (RenderMode mode in modes)
            {
                byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs, camera, mode, Size, Size);
                bool nonTrivial = RenderTestSupport.IsNonTrivial(pixels, out int distinct);
                Assert.True(nonTrivial, $"Mode {mode} produced a trivial image (distinct colors: {distinct}).");

                File.WriteAllBytes(
                    Path.Combine(RenderTestSupport.ArtifactsDir, $"dm01_{mode}.png"),
                    PngWriter.Encode(Size, Size, pixels));
            }
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    private static Vector3 ToVec(Ged.Core.Model.Vec3 v) => new(v.X, v.Y, v.Z);
}
