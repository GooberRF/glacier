using System.Numerics;
using System.Runtime.InteropServices;

namespace Ged.Rendering.Graphics;

/// <summary>Per-frame/per-pass shader constants (HLSL cbuffer b0, 160 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 16, Size = 160)]
internal struct FrameConstants
{
    /// <summary>Transposed world-to-clip matrix (column-major for HLSL).</summary>
    public Matrix4x4 ViewProj;

    /// <summary>Camera right axis (xyz), used to expand billboards.</summary>
    public Vector4 CameraRight;

    /// <summary>Camera up axis (xyz), used to expand billboards.</summary>
    public Vector4 CameraUp;

    /// <summary>Camera world position (xyz).</summary>
    public Vector4 CameraPos;

    /// <summary>x = global alpha, y = lightmap scale (2.0), z = render-mode branch, w unused.</summary>
    public Vector4 Params;

    /// <summary>Distance-fog colour (xyz) and enable flag (w: 1 = on).</summary>
    public Vector4 FogColor;

    /// <summary>x = fog start distance, y = fog end (far-clip) distance.</summary>
    public Vector4 FogParams;
}

/// <summary>An Alpine terrain draw's constants (HLSL cbuffer b2, 176 bytes; field order as Shaders.TerrainCb).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 16, Size = 176)]
internal struct TerrainConstants
{
    /// <summary>xyz origin (min-X / min-Z corner), w layer count.</summary>
    public Vector4 Origin;

    /// <summary>xy footprint along X and Z, zw weight map size in texels.</summary>
    public Vector4 Extent;

    public Vector4 Uv0;
    public Vector4 Uv1;
    public Vector4 Tri0;
    public Vector4 Tri1;
    public Vector4 OverlayUv;
    public Vector4 OverlayTri;
    public Vector4 OverlayOn;

    /// <summary>xy chart origin (x, z), z texel size, w 1 when a chart applies.</summary>
    public Vector4 Chart;

    /// <summary>xy chart texels, z 1 = fullbright.</summary>
    public Vector4 ChartSize;
}

/// <summary>Per-draw shader constants (HLSL cbuffer b1, 96 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 16, Size = 96)]
internal struct DrawConstants
{
    /// <summary>Transposed world matrix for meshes; identity for pre-transformed geometry.</summary>
    public Matrix4x4 World;

    /// <summary>Per-draw RGBA multiply.</summary>
    public Vector4 Tint;

    /// <summary>Constant pick id used by the object/mesh pick pass.</summary>
    public uint PickId;

    /// <summary>1 = sample the bound lightmap; 0 = treat lightmap as neutral (0.5).</summary>
    public uint HasLightmap;

    /// <summary>Diffuse-UV scroll velocity for this batch; the shader adds Scroll * time.</summary>
    public Vector2 Scroll;
}
