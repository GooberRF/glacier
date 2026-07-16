using Ged.Core.Assets;
using Ged.Rendering.Graphics;
using Ged.Rendering.Rhi;
using Ged.Rendering.Scene;

namespace Ged.Rendering;

/// <summary>
/// Renders a scene to an offscreen RGBA image using the exact same code path as
/// a live viewport. Used by tests and (later) thumbnail generation. The returned
/// buffer is tightly packed RGBA8, top-left origin, <c>width*height*4</c> bytes.
/// </summary>
public static class OffscreenRenderer
{
    /// <summary>Renders one frame of <paramref name="scene"/> and returns the RGBA pixels.</summary>
    public static byte[] Render(
        GraphicsDevice gd,
        RenderScene scene,
        AssetVfs? vfs,
        Camera camera,
        RenderMode mode,
        int width,
        int height,
        FogSettings? fog = null,
        float time = 0f,
        bool disableBackfaceCulling = false)
    {
        ArgumentNullException.ThrowIfNull(gd);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        using IReadbackTarget surface = gd.CreateReadbackTarget(width, height);
        using var renderer = new SceneRenderer(gd);
        using var gpu = new GpuScene(gd, scene, vfs);

        if (fog is FogSettings f)
        {
            renderer.Fog = f;
        }

        renderer.Time = time;
        renderer.DisableBackfaceCulling = disableBackfaceCulling;

        camera.AspectRatio = (float)width / height;
        renderer.Render(camera, mode, gpu, surface);
        return surface.ReadPixels();
    }
}
