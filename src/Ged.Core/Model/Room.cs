namespace Ged.Core.Model;

/// <summary>
/// A compiled geometry room. The eight state bytes are preserved as raw bytes
/// (they are logically 0/1 but stored exactly for lossless round-trips). The
/// optional eax / liquid / ambient blocks are present per the byte flags and
/// file version.
/// </summary>
public sealed class Room
{
    /// <summary>UID of the room-effect element, or a large sentinel (&gt; 0x70000000).</summary>
    public int Id { get; set; }

    public Aabb Aabb { get; set; }

    public byte IsSkyroom { get; set; }

    public byte IsCold { get; set; }

    public byte IsOutside { get; set; }

    public byte IsAirlock { get; set; }

    public byte IsLiquidRoom { get; set; }

    public byte HasAmbientLight { get; set; }

    public byte IsSubroom { get; set; }

    public byte HasAlpha { get; set; }

    /// <summary>-1.0f == infinite.</summary>
    public float Life { get; set; }

    /// <summary>EAX effect name; present only for versions &gt;= 0xB4.</summary>
    public string? EaxEffect { get; set; }

    /// <summary>Present iff <see cref="IsLiquidRoom"/> != 0.</summary>
    public RoomLiquidProperties? LiquidProperties { get; set; }

    /// <summary>Present iff <see cref="HasAmbientLight"/> != 0.</summary>
    public RfColor? AmbientColor { get; set; }
}
