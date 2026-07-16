namespace Ged.Core.Model;

/// <summary>
/// Liquid properties for a liquid room-effect (RFL
/// <c>room_effect_liquid_properties</c>). Field order differs from the
/// compiled-room liquid block, so the two are distinct types.
/// </summary>
public sealed class RoomEffectLiquidProperties
{
    /// <summary>1 none, 2 calm, 3 choppy.</summary>
    public int Waveform { get; set; }

    public float Depth { get; set; }

    public string SurfaceTexture { get; set; } = string.Empty;

    public RfColor LiquidColor { get; set; }

    public float Visibility { get; set; }

    /// <summary>1 water, 2 lava, 3 acid.</summary>
    public int LiquidType { get; set; }

    public byte ContainsPlankton { get; set; }

    public int TexturePixelsPerMeterU { get; set; }

    public int TexturePixelsPerMeterV { get; set; }

    public float TextureAngleDegrees { get; set; }

    public Uv TextureScrollRate { get; set; }
}
