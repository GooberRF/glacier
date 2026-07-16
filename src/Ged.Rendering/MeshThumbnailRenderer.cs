using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;

namespace Ged.Rendering;

/// <summary>
/// Renders a framed thumbnail of a V3M/V3C mesh. The GPU path (<see cref="Render"/>)
/// reuses the exact offscreen viewport pipeline — one mesh instance, LOD0, a
/// three-quarter camera framed to the mesh bounds — and returns a square PNG. The
/// CPU path (<see cref="RenderCpu"/>) is a flat-shaded software rasterizer used
/// when no D3D device is available. <see cref="GetOrRender"/> caches the PNG
/// through <see cref="ThumbnailCache"/> keyed by the file identity + mtime, so a
/// palette or asset-browser grid only pays the render cost once per mesh version.
/// </summary>
public static class MeshThumbnailRenderer
{
    private const int DefaultSize = 128;

    /// <summary>Background clear colour for a thumbnail (a neutral dark slate).</summary>
    private static readonly uint Background = Palette.Rgba(38, 42, 50);

    /// <summary>
    /// Renders <paramref name="meshFilename"/> (resolved through <paramref name="vfs"/>)
    /// to a framed <paramref name="size"/>px PNG using the GPU pipeline.
    /// </summary>
    public static byte[] Render(GraphicsDevice gd, AssetVfs vfs, string meshFilename, int size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(gd);
        ArgumentNullException.ThrowIfNull(vfs);

        V3dFile? mesh = vfs.LoadMesh(meshFilename);
        if (mesh is null)
        {
            return SolidPng(size, Background);
        }

        var scene = new RenderScene();
        scene.Meshes.Add(new MeshInstance
        {
            MeshFilename = meshFilename,
            World = Matrix4x4.Identity,
            PickId = new PickId(PickKind.Mesh, 0),
        });

        Camera camera = FramedCamera(mesh, size);
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, size, size);
        return PngWriter.Encode(size, size, pixels);
    }

    /// <summary>
    /// Renders a cached thumbnail for a mesh, keyed by <paramref name="cacheKey"/> and
    /// <paramref name="versionStamp"/> (typically the file path + its mtime/size). Uses
    /// the GPU when <paramref name="gd"/> is non-null, otherwise the CPU fallback.
    /// </summary>
    public static byte[] GetOrRender(
        ThumbnailCache cache,
        GraphicsDevice? gd,
        AssetVfs vfs,
        string meshFilename,
        string cacheKey,
        string versionStamp,
        int size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(vfs);

        return cache.GetOrCreateRaw($"mesh:{cacheKey}", $"{versionStamp}-{size}", () =>
        {
            if (gd is not null)
            {
                return Render(gd, vfs, meshFilename, size);
            }

            V3dFile? mesh = vfs.LoadMesh(meshFilename);
            return mesh is null ? SolidPng(size, Background) : RenderCpu(mesh, size);
        });
    }

    /// <summary>
    /// A flat-shaded software rasterization of the mesh's LOD0 into a
    /// <paramref name="size"/>px PNG. Used when no GPU device is available.
    /// </summary>
    public static byte[] RenderCpu(V3dFile mesh, int size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var color = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            WritePixel(color, i, Background);
        }

        var depth = new float[size * size];
        Array.Fill(depth, float.PositiveInfinity);

        Camera camera = FramedCamera(mesh, size);
        Matrix4x4 vp = camera.ViewProjectionMatrix;
        Vector3 lightDir = Vector3.Normalize(new Vector3(0.4f, 0.7f, -0.6f));

        foreach (V3dSubmesh submesh in mesh.Submeshes)
        {
            if (submesh.Lods.Count == 0)
            {
                continue;
            }

            V3dLod lod = submesh.Lods[0];
            foreach (V3dBatch batch in lod.Batches)
            {
                for (int t = 0; t < batch.NumTriangles; t++)
                {
                    V3dTriangle tri = batch.Triangles[t];
                    if (tri.I0 >= batch.NumVertices || tri.I1 >= batch.NumVertices || tri.I2 >= batch.NumVertices)
                    {
                        continue;
                    }

                    Vector3 p0 = ToVec(batch.Positions[tri.I0]);
                    Vector3 p1 = ToVec(batch.Positions[tri.I1]);
                    Vector3 p2 = ToVec(batch.Positions[tri.I2]);

                    Vector3 faceN = Vector3.Cross(p1 - p0, p2 - p0);
                    if (faceN.LengthSquared() < 1e-12f)
                    {
                        continue;
                    }

                    faceN = Vector3.Normalize(faceN);
                    float shade = 0.25f + 0.75f * MathF.Max(0f, Vector3.Dot(faceN, lightDir));
                    uint c = ShadeColor(shade);

                    RasterTriangle(color, depth, size, vp, p0, p1, p2, c);
                }
            }
        }

        return PngWriter.Encode(size, size, color);
    }

    // ---- internals ----

    private static Camera FramedCamera(V3dFile mesh, int size)
    {
        _ = size;
        Aabb bounds = MeshBounds(mesh);
        var min = new Vector3(bounds.P1.X, bounds.P1.Y, bounds.P1.Z);
        var max = new Vector3(bounds.P2.X, bounds.P2.Y, bounds.P2.Z);
        Vector3 center = (min + max) * 0.5f;
        Vector3 extent = (max - min) * 0.5f;
        float radius = MathF.Max(extent.Length(), 0.001f);

        // A three-quarter view from the front-upper-right octant. The horizontal
        // offset leans toward the larger of the X/Z footprint so the mesh's most
        // informative side (its widest face) turns toward the camera, and the whole
        // bounding sphere is framed with margin from the chosen field of view.
        const float fov = 45f * (MathF.PI / 180f);
        float xLean = extent.X >= extent.Z ? 0.75f : 1.0f;
        float zLean = extent.X >= extent.Z ? 1.0f : 0.75f;
        Vector3 dir = Vector3.Normalize(new Vector3(xLean, 0.6f, zLean));
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

    private static Aabb MeshBounds(V3dFile mesh)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        bool any = false;

        foreach (V3dSubmesh submesh in mesh.Submeshes)
        {
            foreach (V3dLod lod in submesh.Lods)
            {
                foreach (V3dBatch batch in lod.Batches)
                {
                    for (int i = 0; i < batch.NumVertices && i < batch.Positions.Length; i++)
                    {
                        Vector3 p = ToVec(batch.Positions[i]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        any = true;
                    }
                }

                break; // LOD0 only
            }
        }

        if (!any)
        {
            // Fall back to the submesh bounding boxes.
            foreach (V3dSubmesh submesh in mesh.Submeshes)
            {
                Aabb bb = submesh.BoundingBox;
                min = Vector3.Min(min, ToVec(bb.P1));
                max = Vector3.Max(max, ToVec(bb.P2));
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

    private static void RasterTriangle(
        byte[] color, float[] depth, int size, Matrix4x4 vp,
        Vector3 w0, Vector3 w1, Vector3 w2, uint c)
    {
        if (!Project(vp, w0, size, out Vector3 s0) ||
            !Project(vp, w1, size, out Vector3 s1) ||
            !Project(vp, w2, size, out Vector3 s2))
        {
            return;
        }

        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(s0.X, Math.Min(s1.X, s2.X))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(s0.X, Math.Max(s1.X, s2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(s0.Y, Math.Min(s1.Y, s2.Y))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(s0.Y, Math.Max(s1.Y, s2.Y))));

        float area = Edge(s0, s1, s2);
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector3(x + 0.5f, y + 0.5f, 0f);
                float w0b = Edge(s1, s2, p);
                float w1b = Edge(s2, s0, p);
                float w2b = Edge(s0, s1, p);
                if ((w0b < 0 || w1b < 0 || w2b < 0) && (w0b > 0 || w1b > 0 || w2b > 0))
                {
                    continue; // outside
                }

                float l0 = w0b / area;
                float l1 = w1b / area;
                float l2 = w2b / area;
                float z = (l0 * s0.Z) + (l1 * s1.Z) + (l2 * s2.Z);
                int idx = (y * size) + x;
                if (z < depth[idx])
                {
                    depth[idx] = z;
                    WritePixel(color, idx, c);
                }
            }
        }
    }

    private static bool Project(Matrix4x4 vp, Vector3 world, int size, out Vector3 screen)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), vp);
        if (clip.W <= 1e-5f)
        {
            screen = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float ndcZ = clip.Z / clip.W;
        screen = new Vector3((ndcX * 0.5f + 0.5f) * size, (1f - (ndcY * 0.5f + 0.5f)) * size, ndcZ);
        return true;
    }

    private static float Edge(Vector3 a, Vector3 b, Vector3 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static Vector3 ToVec(Vec3 v) => new(v.X, v.Y, v.Z);

    private static uint ShadeColor(float shade)
    {
        byte v = (byte)Math.Clamp(shade * 210f + 20f, 0f, 255f);
        return Palette.Rgba(v, v, (byte)Math.Clamp(v + 8, 0, 255));
    }

    private static byte[] SolidPng(int size, uint rgba)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            WritePixel(px, i, rgba);
        }

        return PngWriter.Encode(size, size, px);
    }

    private static void WritePixel(byte[] buffer, int pixelIndex, uint rgba)
    {
        int o = pixelIndex * 4;
        buffer[o] = (byte)(rgba & 0xFF);
        buffer[o + 1] = (byte)((rgba >> 8) & 0xFF);
        buffer[o + 2] = (byte)((rgba >> 16) & 0xFF);
        buffer[o + 3] = (byte)((rgba >> 24) & 0xFF);
    }
}
