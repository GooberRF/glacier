namespace Ged.Core.Model;

/// <summary>
/// A geoable-brush entry in alpine_level_properties. brush_uid is editor-only;
/// room_uid is used by the game.
/// </summary>
public sealed class AlpineGeoableEntry
{
    public int BrushUid { get; set; }

    public int RoomUid { get; set; }
}

/// <summary>
/// A breakable-brush entry in alpine_level_properties. The material byte packs a
/// 0-6 material index (bits 0-6) and a no_debris flag (bit 7).
/// </summary>
public sealed class AlpineBreakableEntry
{
    public int BrushUid { get; set; }

    public int RoomUid { get; set; }

    public byte Material { get; set; }
}
