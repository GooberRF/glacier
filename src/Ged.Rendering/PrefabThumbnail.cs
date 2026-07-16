using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;

namespace Ged.Rendering;

/// <summary>
/// Renders a framed thumbnail of a prefab's brush geometry through the shared
/// offscreen viewport pipeline (the same path used for mesh thumbnails and tests),
/// returning a square PNG. Used by "Save Selection As Prefab" and the prefab
/// library grid.
/// </summary>
public static class PrefabThumbnail
{
    private static readonly uint Background = Palette.Rgba(38, 42, 50);

    /// <summary>Renders <paramref name="brushes"/> to a framed <paramref name="size"/>px PNG.</summary>
    public static byte[] Render(GraphicsDevice gd, AssetVfs? vfs, IReadOnlyList<Brush> brushes, int size = 128)
    {
        ArgumentNullException.ThrowIfNull(gd);
        ArgumentNullException.ThrowIfNull(brushes);

        var scene = new RenderScene();
        BrushEmitter.Append(scene, brushes, BrushPickGranularity.Brush, selectedBrushes: null, solidFill: true);

        if (scene.Batches.Count == 0)
        {
            return SolidPng(size, Background);
        }

        Camera camera = FramedCamera(Bounds(brushes));
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, size, size);
        return PngWriter.Encode(size, size, pixels);
    }

    private static Camera FramedCamera(Aabb bounds)
    {
        var min = new Vector3(bounds.P1.X, bounds.P1.Y, bounds.P1.Z);
        var max = new Vector3(bounds.P2.X, bounds.P2.Y, bounds.P2.Z);
        Vector3 center = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 0.001f);

        const float fov = 45f * (MathF.PI / 180f);
        Vector3 dir = Vector3.Normalize(new Vector3(0.85f, 0.65f, 1.0f));
        float dist = (radius / MathF.Sin(fov * 0.5f)) * 1.2f;

        var camera = new Camera
        {
            AspectRatio = 1f,
            Projection = CameraProjection.Perspective,
            FieldOfView = fov,
            NearPlane = MathF.Max(0.01f, dist - (radius * 2f)),
            FarPlane = dist + (radius * 3f),
        };
        camera.LookAt(center + (dir * dist), center);
        return camera;
    }

    private static Aabb Bounds(IReadOnlyList<Brush> brushes)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        bool any = false;
        foreach (Brush b in brushes)
        {
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                Vec3 w = b.Position.Add(b.Rotation.Transform(v));
                var p = new Vector3(w.X, w.Y, w.Z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }

        if (!any)
        {
            min = new Vector3(-1f);
            max = new Vector3(1f);
        }

        return new Aabb(new Vec3(min.X, min.Y, min.Z), new Vec3(max.X, max.Y, max.Z));
    }

    private static byte[] SolidPng(int size, uint rgba)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int o = i * 4;
            px[o] = (byte)(rgba & 0xFF);
            px[o + 1] = (byte)((rgba >> 8) & 0xFF);
            px[o + 2] = (byte)((rgba >> 16) & 0xFF);
            px[o + 3] = (byte)((rgba >> 24) & 0xFF);
        }

        return PngWriter.Encode(size, size, px);
    }
}
