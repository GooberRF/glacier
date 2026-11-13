namespace Ged.Core.Model;

/// <summary>
/// An Alpine directional light (0x0AFBAE0C; record common/include/common/alpine_dir_light.h:52-91): parallel
/// light travelling along <see cref="Mat3.Forward"/>, optionally bounded by a shape. Values are kept as stored.
/// </summary>
public sealed class AlpineDirectionalLight
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; } = Mat3.Identity;

    public string ScriptName { get; set; } = string.Empty;

    public RfColor Color { get; set; } = new(255, 255, 255, 255);

    public float Intensity { get; set; } = 1f;

    public byte InitiallyOn { get; set; } = 1;

    /// <summary>0 none, 1 box, 2 sphere, 3 cylinder.</summary>
    public byte Shape { get; set; }

    public float ExtentX { get; set; } = 10f;

    public float ExtentY { get; set; } = 10f;

    public float ExtentZ { get; set; } = 10f;

    public float BoxYaw { get; set; }

    public float Feather { get; set; }

    public float Spread { get; set; }

    public byte CastBakedShadows { get; set; } = 1;

    public byte LiquidOccludes { get; set; } = 1;

    public byte SkyPasses { get; set; } = 1;

    public byte OutsideCasts { get; set; } = 1;

    public byte AffectsMeshes { get; set; } = 1;

    /// <summary>0 = scaled by the lightmap luminance, 1 = everywhere.</summary>
    public byte MeshMode { get; set; }

    public byte AlwaysShowRange { get; set; }
}
