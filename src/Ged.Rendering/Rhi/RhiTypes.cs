namespace Ged.Rendering.Rhi;

/// <summary>Primitive assembly mode for a draw (matches the two topologies the renderer uses).</summary>
public enum PrimitiveTopology
{
    /// <summary>Independent triangles (3 indices/vertices each).</summary>
    TriangleList,

    /// <summary>Independent line segments (2 vertices each) — grid, links, wireframe overlay.</summary>
    LineList,
}

/// <summary>
/// A backend-neutral vertex attribute format. The D3D11 backend maps each to a
/// <c>DXGI</c> format; an OpenGL backend (L2) maps each to a <c>(size, gl-type,
/// normalized)</c> tuple. Only the formats the four vertex layouts actually use
/// are defined.
/// </summary>
public enum VertexAttributeFormat
{
    /// <summary>Two 32-bit floats (UV, billboard corner). DXGI R32G32_FLOAT / GL 2×GL_FLOAT.</summary>
    Float2,

    /// <summary>Three 32-bit floats (position, normal). DXGI R32G32B32_FLOAT / GL 3×GL_FLOAT.</summary>
    Float3,

    /// <summary>Four 32-bit floats. DXGI R32G32B32A32_FLOAT / GL 4×GL_FLOAT.</summary>
    Float4,

    /// <summary>Four normalized bytes, R8G8B8A8 (packed color). DXGI R8G8B8A8_UNORM / GL 4×GL_UNSIGNED_BYTE normalized.</summary>
    UNorm8x4,

    /// <summary>One unsigned 32-bit integer (pick id). DXGI R32_UINT / GL 1×GL_UNSIGNED_INT (glVertexAttribIPointer).</summary>
    UInt32,
}

/// <summary>
/// One element of a vertex layout: a shader input semantic, its byte offset in
/// the interleaved vertex, and its format. The D3D11 backend binds by
/// <see cref="Semantic"/>/<see cref="SemanticIndex"/>; an OpenGL backend binds by
/// the element's ORDINAL position in the layout array (i.e. attribute
/// <c>location = index</c>), so GLSL 330 shaders must declare
/// <c>layout(location = N) in …</c> in the same order as the array.
/// </summary>
public readonly record struct VertexAttribute(
    string Semantic,
    uint SemanticIndex,
    VertexAttributeFormat Format,
    uint Offset);

/// <summary>
/// Per-program shader source. The D3D11 backend compiles <see cref="Hlsl"/> at
/// runtime (entry points <c>VSMain</c> / <c>PSMain</c> / <c>PSPick</c>); an
/// OpenGL backend (L2) reads the GLSL 330 members, which live SIDE-BY-SIDE in
/// <c>Shaders.cs</c> and are null until that backend lands. Keeping both source
/// forms in one record is what lets L2 add GLSL without touching the D3D11 path.
/// </summary>
public sealed record RhiShaderSource
{
    /// <summary>HLSL source with <c>VSMain</c>/<c>PSMain</c> (and <c>PSPick</c> when <see cref="HasPick"/>).</summary>
    public required string Hlsl { get; init; }

    /// <summary>True when the program has an id-buffer pick pixel shader (<c>PSPick</c>).</summary>
    public required bool HasPick { get; init; }

    /// <summary>GLSL 330 vertex source (L2 fills this in <c>Shaders.cs</c>).</summary>
    public string? GlslVertex { get; init; }

    /// <summary>GLSL 330 shading fragment source (L2).</summary>
    public string? GlslFragment { get; init; }

    /// <summary>GLSL 330 id-buffer (pick) fragment source (L2); used only when <see cref="HasPick"/>.</summary>
    public string? GlslPickFragment { get; init; }
}

/// <summary>Everything a backend needs to build one <see cref="IShaderProgram"/>.</summary>
public sealed class ShaderProgramDesc
{
    /// <summary>A debug name (used in compile-error messages).</summary>
    public required string Name { get; init; }

    /// <summary>The program's shader source (HLSL now; GLSL side-by-side for L2).</summary>
    public required RhiShaderSource Source { get; init; }

    /// <summary>The interleaved vertex layout this program consumes.</summary>
    public required IReadOnlyList<VertexAttribute> VertexLayout { get; init; }
}
