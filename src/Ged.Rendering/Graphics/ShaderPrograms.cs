using Ged.Rendering.Rhi;

namespace Ged.Rendering.Graphics;

/// <summary>The four viewport shader programs (world geometry, meshes, billboards, lines).</summary>
internal sealed class ShaderPrograms : IDisposable
{
    public IShaderProgram World = null!;
    public IShaderProgram Mesh = null!;
    public IShaderProgram Billboard = null!;
    public IShaderProgram Line = null!;

    public static ShaderPrograms Build(IRenderDevice device)
    {
        return new ShaderPrograms
        {
            World = device.CreateShaderProgram(new ShaderProgramDesc
            {
                Name = "World", Source = Shaders.World, VertexLayout = WorldLayout,
            }),
            Mesh = device.CreateShaderProgram(new ShaderProgramDesc
            {
                Name = "Mesh", Source = Shaders.Mesh, VertexLayout = MeshLayout,
            }),
            Billboard = device.CreateShaderProgram(new ShaderProgramDesc
            {
                Name = "Billboard", Source = Shaders.Billboard, VertexLayout = BillboardLayout,
            }),
            Line = device.CreateShaderProgram(new ShaderProgramDesc
            {
                Name = "Line", Source = Shaders.Line, VertexLayout = LineLayout,
            }),
        };
    }

    // Vertex layouts. The ORDINAL order is the GL attribute-location order (L2),
    // so a GLSL 330 vertex shader must declare inputs `layout(location = N) in …`
    // in this same order. Offsets match the interleaved structs in Vertices.cs.
    private static readonly VertexAttribute[] WorldLayout =
    {
        new("POSITION", 0, VertexAttributeFormat.Float3, 0),
        new("NORMAL", 0, VertexAttributeFormat.Float3, 12),
        new("TEXCOORD", 0, VertexAttributeFormat.Float2, 24),
        new("TEXCOORD", 1, VertexAttributeFormat.Float2, 32),
        new("COLOR", 0, VertexAttributeFormat.UNorm8x4, 40),
        new("PICKID", 0, VertexAttributeFormat.UInt32, 44),
    };

    private static readonly VertexAttribute[] MeshLayout =
    {
        new("POSITION", 0, VertexAttributeFormat.Float3, 0),
        new("NORMAL", 0, VertexAttributeFormat.Float3, 12),
        new("TEXCOORD", 0, VertexAttributeFormat.Float2, 24),
    };

    private static readonly VertexAttribute[] BillboardLayout =
    {
        new("POSITION", 0, VertexAttributeFormat.Float3, 0),
        new("TEXCOORD", 0, VertexAttributeFormat.Float2, 12),
        new("TEXCOORD", 1, VertexAttributeFormat.Float2, 20),
        new("COLOR", 0, VertexAttributeFormat.UNorm8x4, 28),
        new("PICKID", 0, VertexAttributeFormat.UInt32, 32),
    };

    private static readonly VertexAttribute[] LineLayout =
    {
        new("POSITION", 0, VertexAttributeFormat.Float3, 0),
        new("COLOR", 0, VertexAttributeFormat.UNorm8x4, 12),
    };

    public void Dispose()
    {
        Line.Dispose();
        Billboard.Dispose();
        Mesh.Dispose();
        World.Dispose();
    }
}
