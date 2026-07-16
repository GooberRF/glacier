using System.Numerics;
using System.Runtime.InteropServices;

namespace Ged.Rendering.Scene;

/// <summary>
/// A static-geometry / mover vertex. Positions are already in world space
/// (the compiler bakes them); movers carry an identity world matrix here and are
/// pre-transformed on the CPU. Layout is fixed and blittable so it uploads
/// directly to a D3D11 vertex buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WorldVertex
{
    public Vector3 Position;
    public Vector3 Normal;

    /// <summary>Diffuse texture UV.</summary>
    public Vector2 TexCoord;

    /// <summary>Lightmap atlas UV (already normalized into its page), or (0,0) when unused.</summary>
    public Vector2 LightmapCoord;

    /// <summary>Per-room flat color, packed R8G8B8A8 (little-endian: R | G&lt;&lt;8 | B&lt;&lt;16 | A&lt;&lt;24).</summary>
    public uint Color;

    /// <summary>Encoded <see cref="Picking.PickId"/> for this face.</summary>
    public uint PickId;

    public const int SizeInBytes = 48;
}

/// <summary>A V3M mesh vertex: position, normal and diffuse UV, transformed by a per-draw world matrix.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;

    public const int SizeInBytes = 32;
}

/// <summary>
/// A camera-facing billboard vertex. The vertex shader expands
/// <see cref="Center"/> along the camera right/up axes by <see cref="Corner"/>
/// (world half-extents), so all four corners of a sprite share a center.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BillboardVertex
{
    public Vector3 Center;

    /// <summary>Corner offset in world units along camera right (x) and up (y).</summary>
    public Vector2 Corner;

    public Vector2 TexCoord;

    /// <summary>Tint, packed R8G8B8A8.</summary>
    public uint Color;

    /// <summary>Encoded <see cref="Picking.PickId"/> for the owning object.</summary>
    public uint PickId;

    public const int SizeInBytes = 36;
}

/// <summary>A line vertex for the grid, links, ranges, region outlines and wireframes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LineVertex
{
    public Vector3 Position;

    /// <summary>Line color, packed R8G8B8A8.</summary>
    public uint Color;

    public LineVertex(Vector3 position, uint color)
    {
        Position = position;
        Color = color;
    }

    public const int SizeInBytes = 16;
}
