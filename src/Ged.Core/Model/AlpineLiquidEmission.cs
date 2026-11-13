namespace Ged.Core.Model;

/// <summary>
/// The baked glow of one Liquid Room room effect (0x0AFBAE0F; record editor_patch/liquid_emission.cpp:100-142,
/// defaults liquid_emission.h:18-31). Keyed by the room effect's UID; Alpine stores only non-default effects.
/// </summary>
public sealed class AlpineLiquidEmission
{
    public int RoomEffectUid { get; set; }

    /// <summary>0 off, 1 up (lights what is above the surface), 2 up and down.</summary>
    public byte Mode { get; set; }

    public byte ColorR { get; set; } = 255;

    public byte ColorG { get; set; } = 96;

    public byte ColorB { get; set; } = 32;

    public float Intensity { get; set; } = 1f;

    /// <summary>How far from the surface the light reaches.</summary>
    public float Distance { get; set; } = 8f;
}
