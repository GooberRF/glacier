using System.Diagnostics;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Rhi;
using Ged.Rendering.Scene;

namespace Ged.Rendering.Graphics;

/// <summary>A single uploaded geometry batch ready to draw.</summary>
internal sealed class GpuBatch
{
    public IGpuBuffer VertexBuffer = null!;
    public IGpuBuffer IndexBuffer = null!;
    public int IndexCount;
    public RenderPass Pass;
    public bool HasLightmap;
    public IGpuTexture Diffuse = null!;
    public IGpuTexture Lightmap = null!;
    public System.Numerics.Vector2 Scroll;
    public System.Numerics.Vector4 Tint = System.Numerics.Vector4.One;
    public bool IsPortal;
    public bool PickOnly;
}

/// <summary>The blend/pass a mesh draw uses (VFX effects map onto Alpha/Additive; V3M stays Opaque).</summary>
internal enum MeshDrawBlend
{
    Opaque,
    Alpha,
    Additive,
}

/// <summary>A single uploaded mesh draw (one V3M batch instance).</summary>
internal sealed class GpuMesh
{
    public IGpuBuffer VertexBuffer = null!;
    public IGpuBuffer IndexBuffer = null!;
    public int IndexCount;
    public IGpuTexture Diffuse = null!;

    /// <summary>Blend/pass for this draw. Opaque for V3M/V3C; Alpha/Additive for VFX effects.</summary>
    public MeshDrawBlend Blend;

    /// <summary>Render unlit at full brightness (VFX fullbright / self-illuminated).</summary>
    public bool Fullbright;

    /// <summary>Per-draw colour/opacity tint (VFX opacity + color-only material); white for V3M.</summary>
    public Vector4 Tint = Vector4.One;

    /// <summary>
    /// The instance world matrix, stored UN-transposed. The shaders use
    /// mul(Matrix, column) and HLSL's column-major cbuffer read of a row-major
    /// System.Numerics matrix already supplies the transpose — the same
    /// convention as ViewProj. Uploading a pre-transposed matrix here moves the
    /// translation into the w row and collapses meshes toward the world origin.
    /// </summary>
    public Matrix4x4 World;
    public uint PickId;

    /// <summary>
    /// True when this draw holds the V3M double-sided triangles (per-triangle flag
    /// 0x20): it must render with culling off even when the global solid pass culls.
    /// </summary>
    public bool DoubleSided;
}

/// <summary>
/// The GPU-resident form of a <see cref="RenderScene"/>: vertex/index buffers,
/// resolved diffuse and lightmap textures, billboard and line buffers, and mesh
/// draws. Texture and mesh names are resolved through the <see cref="AssetVfs"/>;
/// missing assets fall back to a white texture so nothing crashes.
/// </summary>
public sealed class GpuScene : IDisposable
{
    private static readonly HashSet<string> WarnedMeshes = new();

    private readonly GraphicsDevice _gd;
    private readonly List<GpuBatch> _batches = new();
    private readonly List<GpuMesh> _meshes = new();
    private readonly Dictionary<string, IGpuTexture> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IGpuTexture> _ownedTextures = new();
    private readonly List<IGpuTexture> _lightmapTextures = new();

    private IGpuBuffer? _billboardVb;
    private IGpuBuffer? _billboardIb;
    private int _billboardIndexCount;
    private IGpuBuffer? _billboardOnTopVb;
    private IGpuBuffer? _billboardOnTopIb;
    private int _billboardOnTopIndexCount;
    private readonly List<TexturedBillboardGroup> _particleGroups = new();
    private readonly List<TexturedBillboardGroup> _onTopGroups = new();
    private readonly HashSet<string> _missingTextures = new(StringComparer.OrdinalIgnoreCase);
    private IGpuBuffer? _lineVb;
    private int _lineVertexCount;
    private readonly IReadOnlyDictionary<string, InlineTexture>? _inline;

    public GpuScene(GraphicsDevice gd, RenderScene scene, AssetVfs? vfs)
    {
        ArgumentNullException.ThrowIfNull(gd);
        ArgumentNullException.ThrowIfNull(scene);
        _gd = gd;
        _inline = scene.InlineTextures.Count > 0 ? scene.InlineTextures : null;

        UploadLightmaps(scene);
        UploadBatches(scene, vfs);
        UploadBillboards(scene, vfs);
        UploadLines(scene);
        UploadMeshes(scene, vfs);
    }

    // Exposed as the concrete List (not IReadOnlyList) so the per-frame SceneRenderer foreach uses
    // List's struct enumerator instead of boxing an interface enumerator every frame (render-hot-path
    // GC pressure → intermittent camera-orbit hitches). Consumers are internal + read-only in practice.
    internal List<GpuBatch> Batches => _batches;

    internal List<GpuMesh> Meshes => _meshes;

    internal IGpuBuffer? BillboardVertexBuffer => _billboardVb;

    internal IGpuBuffer? BillboardIndexBuffer => _billboardIb;

    internal int BillboardIndexCount => _billboardIndexCount;

    /// <summary>Atlas-glyph billboards flagged on-top (drawn with the depth test disabled).</summary>
    internal IGpuBuffer? BillboardOnTopVertexBuffer => _billboardOnTopVb;

    internal IGpuBuffer? BillboardOnTopIndexBuffer => _billboardOnTopIb;

    internal int BillboardOnTopIndexCount => _billboardOnTopIndexCount;

    /// <summary>Particle billboards grouped by their resolved authored bitmap (drawn textured).</summary>
    internal IReadOnlyList<TexturedBillboardGroup> ParticleGroups => _particleGroups;

    /// <summary>Textured billboards flagged on-top (transform-indicator labels), drawn depth-disabled.</summary>
    internal IReadOnlyList<TexturedBillboardGroup> OnTopGroups => _onTopGroups;

    internal IGpuBuffer? LineVertexBuffer => _lineVb;

    internal int LineVertexCount => _lineVertexCount;

    private void UploadLightmaps(RenderScene scene)
    {
        foreach (Lightmap page in scene.Lightmaps)
        {
            byte[] rgba = ExpandRgbToRgba(page.Pixels, page.Width, page.Height);
            _lightmapTextures.Add(_gd.CreateTexture(Math.Max(1, page.Width), Math.Max(1, page.Height), rgba));
        }
    }

    private void UploadBatches(RenderScene scene, AssetVfs? vfs)
    {
        foreach (GeometryBatch batch in scene.Batches)
        {
            if (batch.Indices.Count == 0)
            {
                continue;
            }

            var gpu = new GpuBatch
            {
                VertexBuffer = _gd.CreateVertexBuffer<WorldVertex>(batch.Vertices.ToArray()),
                IndexBuffer = _gd.CreateIndexBuffer(batch.Indices.ToArray()),
                IndexCount = batch.Indices.Count,
                Pass = batch.Pass,
                HasLightmap = batch.HasLightmap && batch.LightmapIndex < _lightmapTextures.Count,
                Diffuse = ResolveTexture(batch.TextureName, vfs),
                Scroll = new System.Numerics.Vector2(batch.ScrollU, batch.ScrollV),
                Tint = batch.Tint,
                IsPortal = batch.IsPortal,
                PickOnly = batch.PickOnly,
            };

            gpu.Lightmap = gpu.HasLightmap
                ? _lightmapTextures[batch.LightmapIndex]
                : _gd.Textures.NeutralLightmap;
            _batches.Add(gpu);
        }
    }

    private void UploadBillboards(RenderScene scene, AssetVfs? vfs)
    {
        if (scene.Billboards.Count == 0)
        {
            return;
        }

        // Atlas billboards (object glyphs + particles whose authored bitmap does not
        // resolve) share one buffer sampled from the icon atlas. Particle billboards
        // that carry a resolvable authored bitmap are grouped per texture and drawn
        // over the full 0..1 UV — see DrawBillboards. Billboards flagged OnTop (the
        // transform-drag indicator labels) go into PARALLEL sets so the renderer can
        // draw them depth-disabled after the normal pass (never occluded).
        var atlasVerts = new List<BillboardVertex>(scene.Billboards.Count * 4);
        var atlasIndices = new List<uint>(scene.Billboards.Count * 6);
        var atlasOnTopVerts = new List<BillboardVertex>();
        var atlasOnTopIndices = new List<uint>();
        Dictionary<string, (IGpuTexture Srv, List<BillboardVertex> V, List<uint> I)>? groups = null;
        Dictionary<string, (IGpuTexture Srv, List<BillboardVertex> V, List<uint> I)>? onTopGroups = null;

        foreach (Billboard b in scene.Billboards)
        {
            IGpuTexture? textured = null;
            if (!string.IsNullOrEmpty(b.TextureName))
            {
                textured = TryResolveTexture(b.TextureName, vfs);
            }

            if (textured is { } srv)
            {
                Dictionary<string, (IGpuTexture Srv, List<BillboardVertex> V, List<uint> I)> bucket =
                    b.OnTop
                        ? onTopGroups ??= new(StringComparer.OrdinalIgnoreCase)
                        : groups ??= new(StringComparer.OrdinalIgnoreCase);
                if (!bucket.TryGetValue(b.TextureName!, out var g))
                {
                    g = (srv, new List<BillboardVertex>(), new List<uint>());
                    bucket[b.TextureName!] = g;
                }

                AppendQuad(g.V, g.I, b, 0f, 0f, 1f, 1f);
            }
            else
            {
                (float u0, float v0, float u1, float v1) = IconAtlas.Rect(b.Icon);
                if (b.OnTop)
                {
                    AppendQuad(atlasOnTopVerts, atlasOnTopIndices, b, u0, v0, u1, v1);
                }
                else
                {
                    AppendQuad(atlasVerts, atlasIndices, b, u0, v0, u1, v1);
                }
            }
        }

        if (atlasIndices.Count > 0)
        {
            _billboardVb = _gd.CreateVertexBuffer<BillboardVertex>(atlasVerts.ToArray());
            _billboardIb = _gd.CreateIndexBuffer(atlasIndices.ToArray());
            _billboardIndexCount = atlasIndices.Count;
        }

        if (atlasOnTopIndices.Count > 0)
        {
            _billboardOnTopVb = _gd.CreateVertexBuffer<BillboardVertex>(atlasOnTopVerts.ToArray());
            _billboardOnTopIb = _gd.CreateIndexBuffer(atlasOnTopIndices.ToArray());
            _billboardOnTopIndexCount = atlasOnTopIndices.Count;
        }

        AppendGroups(groups, _particleGroups);
        AppendGroups(onTopGroups, _onTopGroups);
    }

    private void AppendGroups(
        Dictionary<string, (IGpuTexture Srv, List<BillboardVertex> V, List<uint> I)>? groups,
        List<TexturedBillboardGroup> target)
    {
        if (groups is null)
        {
            return;
        }

        foreach ((IGpuTexture srv, List<BillboardVertex> v, List<uint> i) in groups.Values)
        {
            target.Add(new TexturedBillboardGroup
            {
                Texture = srv,
                VertexBuffer = _gd.CreateVertexBuffer<BillboardVertex>(v.ToArray()),
                IndexBuffer = _gd.CreateIndexBuffer(i.ToArray()),
                IndexCount = i.Count,
            });
        }
    }

    // Emits one camera-facing quad. The quad's local +Y (corner.y = +s) is displaced
    // along CameraUp — the SCREEN-TOP edge — so it must sample the top of the source
    // (v0), matching the top-origin atlas/bitmap RGBA; the reverse renders upside-down
    // (see IconAtlasRenderTests).
    private static void AppendQuad(List<BillboardVertex> verts, List<uint> indices, Billboard b, float u0, float v0, float u1, float v1)
    {
        uint pick = b.PickId.Encode();
        float s = b.Size;
        float sx = s * (b.Aspect > 0f ? b.Aspect : 1f); // wide labels use Aspect > 1 (B7)
        uint baseIndex = (uint)verts.Count;
        verts.Add(Corner(b, -sx, -s, u0, v1, pick));
        verts.Add(Corner(b, sx, -s, u1, v1, pick));
        verts.Add(Corner(b, sx, s, u1, v0, pick));
        verts.Add(Corner(b, -sx, s, u0, v0, pick));
        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
    }

    private static BillboardVertex Corner(Billboard b, float cx, float cy, float u, float v, uint pick) => new()
    {
        Center = b.Position,
        Corner = new Vector2(cx, cy),
        TexCoord = new Vector2(u, v),
        Color = b.Tint,
        PickId = pick,
    };

    private void UploadLines(RenderScene scene)
    {
        if (scene.Lines.Count == 0)
        {
            return;
        }

        var verts = new List<LineVertex>(scene.Lines.Count * 2);
        foreach (LineSegment seg in scene.Lines)
        {
            verts.Add(new LineVertex(seg.A, seg.Color));
            verts.Add(new LineVertex(seg.B, seg.Color));
        }

        _lineVb = _gd.CreateVertexBuffer<LineVertex>(verts.ToArray());
        _lineVertexCount = verts.Count;
    }

    private void UploadMeshes(RenderScene scene, AssetVfs? vfs)
    {
        if (vfs is null)
        {
            return;
        }

        foreach (MeshInstance instance in scene.Meshes)
        {
            V3dFile? file;
            try
            {
                file = vfs.LoadMesh(instance.MeshFilename);
            }
            catch (Exception)
            {
                file = null;
            }

            if (file is null)
            {
                continue;
            }

            Matrix4x4 world = instance.World;
            uint pick = instance.PickId.Encode();

            foreach (V3dSubmesh submesh in file.Submeshes)
            {
                if (submesh.Lods.Count == 0)
                {
                    continue;
                }

                V3dLod lod = submesh.Lods[0];
                foreach (V3dBatch batch in lod.Batches)
                {
                    BuildMeshBatch(submesh, lod, batch, instance, world, pick, vfs);
                }
            }
        }
    }

    private void BuildMeshBatch(
        V3dSubmesh submesh,
        V3dLod lod,
        V3dBatch batch,
        MeshInstance instance,
        Matrix4x4 world,
        uint pick,
        AssetVfs vfs)
    {
        if (batch.NumTriangles <= 0 || batch.Positions.Length <= 0)
        {
            return;
        }

        // The vertex arrays are stored at their allocated length, which can exceed
        // num_vertices (the engine over-allocates and triangles index into the full
        // allocation). Upload the whole vertex buffer — uploading only num_vertices
        // left triangles referencing the extra vertices pointing past the buffer,
        // which the GPU read as (0,0,0) and collapsed the mesh onto the origin.
        int vc = batch.Positions.Length;
        int validTris = Math.Min(batch.NumTriangles, batch.Triangles.Length);

        // Split by the per-triangle double-sided flag (0x20): single-sided triangles are
        // back-face culled with the world; double-sided triangles must render both faces,
        // so they go into a separate, cull-off draw. Meshes with no double-sided triangles
        // (the common case, incl. everything GED authors) still produce exactly one draw.
        var single = new List<uint>(validTris * 3);
        List<uint>? doubleSided = null;
        for (int t = 0; t < validTris; t++)
        {
            V3dTriangle tri = batch.Triangles[t];
            if (tri.I0 >= vc || tri.I1 >= vc || tri.I2 >= vc)
            {
                WarnOnce(instance.MeshFilename,
                    $"mesh '{instance.MeshFilename}' has a triangle index out of range " +
                    $"({tri.I0},{tri.I1},{tri.I2} >= vertex count {vc}); skipping batch.");
                return;
            }

            List<uint> target = (tri.Flags & V3dTriangle.DoubleSided) != 0
                ? (doubleSided ??= new List<uint>())
                : single;
            target.Add(tri.I0);
            target.Add(tri.I1);
            target.Add(tri.I2);
        }

        var verts = new MeshVertex[vc];
        for (int i = 0; i < vc; i++)
        {
            Vec3 p = batch.Positions[i];
            Vec3 n = i < batch.Normals.Length ? batch.Normals[i] : new Vec3(0f, 1f, 0f);
            Uv uv = i < batch.TexCoords.Length ? batch.TexCoords[i] : default;
            verts[i] = new MeshVertex
            {
                Position = new Vector3(p.X, p.Y, p.Z),
                Normal = new Vector3(n.X, n.Y, n.Z),
                TexCoord = new Vector2(uv.U, uv.V),
            };
        }

        string texName = submesh.ResolveBatchTexture(lod, batch);
        if (instance.TextureOverrides is not null &&
            batch.TextureIndex >= 0 && batch.TextureIndex < lod.Textures.Count &&
            instance.TextureOverrides.TryGetValue(lod.Textures[batch.TextureIndex].Id, out string? ov))
        {
            texName = ov;
        }

        // Effect (VFX) material state carried through the V3d adapter: blend/pass,
        // fullbright, and a colour/opacity tint. V3M/V3C batches leave these at the
        // opaque, lit, white defaults, so their draws are unchanged.
        MeshDrawBlend blend = batch.Blend switch
        {
            V3dBatchBlend.Additive => MeshDrawBlend.Additive,
            V3dBatchBlend.Alpha => MeshDrawBlend.Alpha,
            _ => MeshDrawBlend.Opaque,
        };
        Vector3 tintRgb = batch.SolidColor is { } c
            ? new Vector3(c.R / 255f, c.G / 255f, c.B / 255f)
            : Vector3.One;
        float op = Math.Clamp(batch.Opacity, 0f, 1f);
        Vector4 tint = blend switch
        {
            // Additive (dst=ONE) ignores dest alpha, so fold opacity into brightness.
            MeshDrawBlend.Additive => new Vector4(tintRgb * op, 1f),
            MeshDrawBlend.Alpha => new Vector4(tintRgb, op),
            _ => new Vector4(tintRgb, 1f),
        };

        // The diffuse texture is cache-owned (not disposed per-mesh), so the two draws can
        // share it; each draw owns its own vertex/index buffers.
        void Emit(List<uint> indexList, bool isDoubleSided)
        {
            if (indexList.Count == 0)
            {
                return;
            }

            _meshes.Add(new GpuMesh
            {
                VertexBuffer = _gd.CreateVertexBuffer<MeshVertex>(verts),
                IndexBuffer = _gd.CreateIndexBuffer(indexList.ToArray()),
                IndexCount = indexList.Count,
                Diffuse = ResolveTexture(texName, vfs),
                World = world,
                PickId = pick,
                DoubleSided = isDoubleSided,
                Blend = blend,
                Fullbright = batch.Unlit,
                Tint = tint,
            });
        }

        Emit(single, isDoubleSided: false);
        if (doubleSided is not null)
        {
            Emit(doubleSided, isDoubleSided: true);
        }
    }

    /// <summary>Logs a mesh geometry problem once per mesh so a bad asset is visible, not silent.</summary>
    private static void WarnOnce(string meshFilename, string message)
    {
        bool first;
        lock (WarnedMeshes)
        {
            first = WarnedMeshes.Add(meshFilename);
        }

        if (first)
        {
            Trace.TraceWarning("[GED.Rendering] " + message);
            Console.Error.WriteLine("[GED.Rendering] " + message);
        }
    }

    private IGpuTexture ResolveTexture(string? name, AssetVfs? vfs)
    {
        if (string.IsNullOrEmpty(name) || vfs is null)
        {
            return _gd.Textures.White;
        }

        if (_textureCache.TryGetValue(name, out IGpuTexture? cached))
        {
            return cached;
        }

        IGpuTexture srv = _gd.Textures.White;
        try
        {
            DecodedTexture? decoded = vfs.LoadTexture(name);
            if (decoded is not null)
            {
                TextureImage img = decoded.Primary;
                srv = _gd.CreateTexture(img.Width, img.Height, img.Pixels);
                _ownedTextures.Add(srv);
            }
        }
        catch (Exception)
        {
            srv = _gd.Textures.White;
        }

        _textureCache[name] = srv;
        return srv;
    }

    /// <summary>
    /// Resolves + caches a texture like <see cref="ResolveTexture"/>, but returns
    /// null (rather than the white fallback) when the VFS cannot load it, so
    /// particle billboards can fall back to the soft-sprite atlas cell.
    /// </summary>
    private IGpuTexture? TryResolveTexture(string? name, AssetVfs? vfs)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (_textureCache.TryGetValue(name, out IGpuTexture? cached))
        {
            return cached;
        }

        // CPU-supplied inline bitmaps (annotation labels, B7) resolve without a VFS.
        if (_inline is not null && _inline.TryGetValue(name, out InlineTexture inl) && inl.Rgba.Length > 0)
        {
            IGpuTexture made = _gd.CreateTexture(Math.Max(1, inl.Width), Math.Max(1, inl.Height), inl.Rgba);
            _ownedTextures.Add(made);
            _textureCache[name] = made;
            return made;
        }

        if (vfs is null || _missingTextures.Contains(name))
        {
            return null;
        }

        try
        {
            DecodedTexture? decoded = vfs.LoadTexture(name);
            if (decoded is not null)
            {
                TextureImage img = decoded.Primary;
                IGpuTexture srv = _gd.CreateTexture(img.Width, img.Height, img.Pixels);
                _ownedTextures.Add(srv);
                _textureCache[name] = srv;
                return srv;
            }
        }
        catch (Exception)
        {
            // fall through to miss
        }

        _missingTextures.Add(name);
        return null;
    }

    private static byte[] ExpandRgbToRgba(byte[] rgb, int width, int height)
    {
        int pixels = Math.Max(1, width) * Math.Max(1, height);
        var rgba = new byte[pixels * 4];
        int n = Math.Min(pixels, rgb.Length / 3);
        for (int i = 0; i < n; i++)
        {
            rgba[(i * 4) + 0] = rgb[(i * 3) + 0];
            rgba[(i * 4) + 1] = rgb[(i * 3) + 1];
            rgba[(i * 4) + 2] = rgb[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        for (int i = n; i < pixels; i++)
        {
            rgba[(i * 4) + 3] = 255;
        }

        return rgba;
    }

    public void Dispose()
    {
        foreach (GpuBatch b in _batches)
        {
            b.VertexBuffer.Dispose();
            b.IndexBuffer.Dispose();
        }

        foreach (GpuMesh m in _meshes)
        {
            m.VertexBuffer.Dispose();
            m.IndexBuffer.Dispose();
        }

        foreach (IGpuTexture t in _ownedTextures)
        {
            t.Dispose();
        }

        foreach (IGpuTexture t in _lightmapTextures)
        {
            t.Dispose();
        }

        foreach (TexturedBillboardGroup g in _particleGroups)
        {
            // The Texture is owned by _ownedTextures/_textureCache; only the
            // per-group geometry buffers are owned here.
            g.VertexBuffer.Dispose();
            g.IndexBuffer.Dispose();
        }

        foreach (TexturedBillboardGroup g in _onTopGroups)
        {
            g.VertexBuffer.Dispose();
            g.IndexBuffer.Dispose();
        }

        _billboardVb?.Dispose();
        _billboardIb?.Dispose();
        _billboardOnTopVb?.Dispose();
        _billboardOnTopIb?.Dispose();
        _lineVb?.Dispose();
    }
}

/// <summary>A set of textured particle billboards sharing one resolved bitmap.</summary>
internal sealed class TexturedBillboardGroup
{
    public IGpuTexture Texture = null!;
    public IGpuBuffer VertexBuffer = null!;
    public IGpuBuffer IndexBuffer = null!;
    public int IndexCount;
}
