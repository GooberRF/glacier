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

    /// <summary>
    /// True for a pick-only batch: its triangles are rasterized into the id-buffer pick pass
    /// (so the geometry is selectable) but SKIPPED in the colour pass (nothing visible is drawn).
    /// Used to keep whole brushes selectable in Group mode and authored portal brushes selectable
    /// under View ▸ Portal Faces = Don't Draw, without drawing any fill.
    /// </summary>
    public bool PickOnly { get; set; }

    /// <summary>True for a show-sky editor-aid batch: a flat semitransparent sky-blue quad
    /// (texture dropped) marking faces flagged <c>show_sky</c>, drawn with a "SHOW SKY" label.</summary>
    public bool IsSky { get; set; }

    /// <summary>The UID of the object this batch draws (an Alpine terrain surface), or 0 for level geometry: hiding the object culls it.</summary>
    public int OwnerUid { get; set; }

    public List<WorldVertex> Vertices { get; } = new();

    public List<uint> Indices { get; } = new();

    public bool HasLightmap => LightmapIndex >= 0;
}

/// <summary>
/// An Alpine terrain's top surface drawn with the layer blend (the terrain shader): an indexed grid of its non-hole cells,
/// the two RGBA8 weight maps, up to 8 layer textures with their UV scales and triplanar flags, up to 4 overlays with their
/// coverage map, and the baked light chart when one applies. Mirrors what Alpine Faction's Direct3D 11 terrain renderer
/// binds (common/src/gr_d3d11/gr_d3d11_terrain.cpp:231-419) and samples (resources/shaders/standard_ps.hlsl:1169-1560).
/// </summary>
public sealed class TerrainDraw
{
    /// <summary>The terrain's UID (hiding the object culls it).</summary>
    public int Uid { get; init; }

    /// <summary>Grid vertices: position on the heightfield, the heightmap normal, the room colour and the terrain's pick id.</summary>
    public List<WorldVertex> Vertices { get; } = new();

    public List<uint> Indices { get; } = new();

    /// <summary>The min-X / min-Z grid corner.</summary>
    public Vector3 Origin { get; init; }

    /// <summary>The footprint along X and Z.</summary>
    public Vector2 Extent { get; init; }

    /// <summary>The layer count (1..8): weights of layers past it do not count.</summary>
    public int LayerCount { get; init; }

    /// <summary>Layer textures (8 entries; unused ones repeat layer 0, as the game binds a missing layer).</summary>
    public string[] Layers { get; } = new string[8];

    public float[] LayerUvScale { get; } = new float[8];

    public bool[] LayerTriplanar { get; } = new bool[8];

    /// <summary>Weight texels across X and Z.</summary>
    public int WeightWidth { get; init; }

    public int WeightHeight { get; init; }

    /// <summary>Weight map 0 (layers 0-3) and 1 (layers 4-7), RGBA8.</summary>
    public byte[] Weights0 { get; init; } = Array.Empty<byte>();

    public byte[] Weights1 { get; init; } = Array.Empty<byte>();

    /// <summary>Overlay textures (0..4).</summary>
    public List<string> Overlays { get; } = new();

    public float[] OverlayUvScale { get; } = new float[4];

    public bool[] OverlayTriplanar { get; } = new bool[4];

    /// <summary>Overlay coverage (RGBA8 laid out as a weight map), or null without overlays.</summary>
    public byte[]? OverlayCoverage { get; init; }

    /// <summary>The terrain's baked light chart, RGB8 <see cref="ChartWidth"/> x <see cref="ChartHeight"/>, or null.</summary>
    public byte[]? Chart { get; set; }

    public int ChartWidth { get; set; }

    public int ChartHeight { get; set; }

    /// <summary>World X / Z of chart texel (0, 0)'s corner.</summary>
    public Vector2 ChartOrigin { get; set; }

    public float ChartTexelSize { get; set; } = 1f;

    /// <summary>Drawn at full brightness, with no light.</summary>
    public bool Fullbright { get; init; }
}

/// <summary>Every placed instance of one terrain decoration mesh (instance world matrices), drawn as one merged mesh.</summary>
public sealed class DecorationDraw
{
    public required string MeshFilename { get; init; }

    public required PickId PickId { get; init; }

    public List<Matrix4x4> Worlds { get; } = new();
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

    /// <summary>Alpine terrain top surfaces, drawn with the layer blend.</summary>
    public List<TerrainDraw> Terrains { get; } = new();

    /// <summary>Alpine terrain decorations, one merged draw per terrain and mesh.</summary>
    public List<DecorationDraw> Decorations { get; } = new();

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

    public int TotalVertexCount => Batches.Sum(b => b.Vertices.Count) + Terrains.Sum(t => t.Vertices.Count);

    public int TotalTriangleCount => (Batches.Sum(b => b.Indices.Count) + Terrains.Sum(t => t.Indices.Count)) / 3;
}
