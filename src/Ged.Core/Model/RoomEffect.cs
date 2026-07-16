namespace Ged.Core.Model;

/// <summary>
/// A room-effect marker (RFL <c>room_effect</c>): sky room, liquid room, or
/// ambient-light override. The effect-specific block precedes the common
/// object header in the stream.
/// </summary>
public sealed class RoomEffect
{
    /// <summary>1 sky_room, 2 liquid_room, 3 ambient_light, 4 none.</summary>
    public int EffectType { get; set; }

    /// <summary>Present iff <see cref="EffectType"/> == 3 (ambient_light).</summary>
    public RfColor? AmbientLightColor { get; set; }

    /// <summary>Present iff <see cref="EffectType"/> == 2 (liquid_room).</summary>
    public RoomEffectLiquidProperties? LiquidProperties { get; set; }

    public byte RoomIsCold { get; set; }

    public byte RoomIsOutside { get; set; }

    public byte RoomIsAirLock { get; set; }

    public ObjectHeader Header { get; set; } = new();
}
