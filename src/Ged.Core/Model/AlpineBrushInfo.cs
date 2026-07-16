namespace Ged.Core.Model;

/// <summary>
/// Alpine per-brush metadata carried in an .rfg group (chunk 0x0AFBAE05):
/// marks a brush (by its index within the group) as geoable and/or breakable
/// and records its breakable material.
/// </summary>
public sealed class AlpineBrushInfo
{
    public uint BrushIndex { get; set; }

    /// <summary>bit0 = geoable, bit1 = breakable.</summary>
    public byte Flags { get; set; }

    public byte Material { get; set; }

    public bool IsGeoable => (Flags & 0x1) != 0;

    public bool IsBreakable => (Flags & 0x2) != 0;
}
