namespace Ged.Core.Model;

/// <summary>
/// 16-bit face bitfield as stored in RF1 geometry. Bits 0x0300 encode the
/// 2-bit lightmap resolution.
/// </summary>
[Flags]
public enum FaceFlags : ushort
{
    None = 0x0000,
    ShowSky = 0x0001,
    Mirrored = 0x0002,
    LiquidSurface = 0x0004,
    IsDetail = 0x0008,
    ScrollTexture = 0x0010,
    FullBright = 0x0020,
    HasAlpha = 0x0040,
    HasHoles = 0x0080,
    LightmapResolutionMask = 0x0300,
    IsInvisible = 0x2000,
}
