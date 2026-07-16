using System.Numerics;
using Ged.Core.Model;
using Ged.Rendering.Picking;

namespace Ged.Rendering.Scene;

/// <summary>
/// One draw batch of static/mover geometry: all triangles that share a diffuse
/// texture, a lightmap page and a render pass. Triangulated as fans on the CPU.
/// </summary>
public sealed class GeometryBatch
{
    public GeometryBatch(string textureName, int lightmapIndex, RenderPass pass)
    {
        TextureName = textureName;
        LightmapIndex = lightmapIndex;
        Pass = pass;
    }

    /// <summary>Diffuse texture reference (bare name as stored in the geometry texture table).</summary>
    public string TextureName { get; }

    /// <summary>Index into <see cref="RenderScene.Lightmaps"/>, or -1 if the batch has no lightmap.</summary>
    public int LightmapIndex { get; }

    public RenderPass Pass { get; }

    /// <summary>Per-batch diffuse-UV scroll velocity (from the face scroll table); the
    /// shader offsets the diffuse UV by <c>Scroll * time</c>. Zero for non-scrolling batches.</summary>
    public float ScrollU { get; set; }

    public float ScrollV { get; set; }

    /// <summary>Per-draw tint (RGBA, 0–1) multiplied into the output; alpha scales blend.
    /// White (1,1,1,1) = untinted. Used to draw portal faces in the portal-brush colour.</summary>
    public Vector4 Tint { get; set; } = Vector4.One;

    /// <summary>True for a portal-face batch (drawn only when a portal draw mode is on; kept pickable).</summary>
    public bool IsPortal { get; set; }

    /// <summary>True for a show-sky editor-aid batch: a flat semitransparent sky-blue quad
    /// (texture dropped) marking faces flagged <c>show_sky</c>, drawn with a "SHOW SKY" label.</summary>
    public bool IsSky { get; set; }

    public List<WorldVertex> Vertices { get; } = new();

    public List<uint> Indices { get; } = new();

    public bool HasLightmap => LightmapIndex >= 0;
}

/// <summary>A placed V3M/V3C mesh (Alpine mesh object, entity, clutter or item).</summary>
public sealed class MeshInstance
{
    public required string MeshFilename { get; init; }

    public Matrix4x4 World { get; init; } = Matrix4x4.Identity;

    public PickId PickId { get; init; }

    /// <summary>Optional per-slot diffuse overrides (Alpine mesh objects); slot -&gt; file name.</summary>
    public IReadOnlyDictionary<int, string>? TextureOverrides { get; init; }
}

/// <summary>
/// A camera-facing billboard glyph for a point object. <see cref="Icon"/> selects
/// the atlas cell (default <c>0</c> = the soft disc used for particles). When
/// <see cref="TextureName"/> is set (particle-emitter previews), the GPU layer
/// resolves that bitmap through the VFS and draws the quad textured with it over
/// the full 0..1 UV, tinted by <see cref="Tint"/>; if the bitmap cannot be
/// resolved the billboard falls back to its <see cref="Icon"/> atlas cell.
/// <para><see cref="OnTop"/> billboards (the transform-drag indicator labels) draw in a
/// second pass with the depth test disabled — like the gizmo overlay lines — so they are
/// never occluded by geometry between the camera and their anchor. They are excluded from
/// the pick pass.</para>
/// </summary>
public readonly record struct Billboard(
    BillboardKind Kind,
    Vector3 Position,
    float Size,
    uint Tint,
    PickId PickId,
    int Icon = 0,
    string? TextureName = null,
    float Aspect = 1f,
    bool OnTop = false);

/// <summary>A single world-space line segment (grid, link, range, outline, wireframe).</summary>
public readonly record struct LineSegment(Vector3 A, Vector3 B, uint Color);

/// <summary>
/// A CPU-generated RGBA texture supplied inline with the scene (keyed by a synthetic
/// name a <see cref="Billboard.TextureName"/> references), so a billboard can be drawn
/// with a bitmap that is not in the VFS — e.g. the measurement/dimension label textures
/// rasterized CPU-side (feature 4 / B7).
/// </summary>
public readonly record struct InlineTexture(int Width, int Height, byte[] Rgba);

/// <summary>
/// The renderable scene: CPU-side batches, mesh placements, billboards and
/// lines built from a parsed level, plus the lightmap atlas pages. This is pure
/// data with no GPU dependency, so it is fully unit-testable. The GPU layer
/// (<c>GpuScene</c>) resolves texture/mesh names against the VFS and uploads it.
/// </summary>
public sealed class RenderScene
{
    public List<GeometryBatch> Batches { get; } = new();

    public List<MeshInstance> Meshes { get; } = new();

    public List<Billboard> Billboards { get; } = new();

    public List<LineSegment> Lines { get; } = new();

    /// <summary>CPU-generated billboard textures keyed by synthetic name (annotation labels, B7).</summary>
    public Dictionary<string, InlineTexture> InlineTextures { get; } = new();

    /// <summary>Lightmap atlas pages referenced by <see cref="GeometryBatch.LightmapIndex"/>.</summary>
    public IReadOnlyList<Lightmap> Lightmaps { get; set; } = Array.Empty<Lightmap>();

    /// <summary>World-space bounds of all static geometry (used to frame the camera).</summary>
    public Aabb Bounds { get; set; }

    /// <summary>A reasonable starting camera position/target derived from the level.</summary>
    public Vector3 SuggestedCameraPosition { get; set; }

    public Vector3 SuggestedCameraTarget { get; set; }

    public int TotalVertexCount => Batches.Sum(b => b.Vertices.Count);

    public int TotalTriangleCount => Batches.Sum(b => b.Indices.Count) / 3;
}
