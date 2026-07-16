namespace Ged.Core.Model;

/// <summary>
/// Liquid properties embedded in a compiled <see cref="Room"/> (RFL
/// <c>room_liquid_properties</c>). Field order differs from the room-effect
/// liquid block, so the two are kept as distinct types.
/// </summary>
public sealed class RoomLiquidProperties
{
    public float Depth { get; set; }

    public RfColor Color { get; set; }

    public string SurfaceTexture { get; set; } = string.Empty;

    public float Visibility { get; set; }

    /// <summary>1 = water, 2 = lava, 3 = acid.</summary>
    public int LiquidType { get; set; }

    public int LiquidAlpha { get; set; }

    public byte ContainsPlankton { get; set; }

    public int TexturePixelsPerMeterU { get; set; }

    public int TexturePixelsPerMeterV { get; set; }

    public float TextureAngleRadians { get; set; }

    /// <summary>-1 none, 0 calm, 1 choppy.</summary>
    public int Waveform { get; set; }

    public Uv TextureScrollRate { get; set; }
}
