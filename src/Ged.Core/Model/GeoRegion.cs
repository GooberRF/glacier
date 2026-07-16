namespace Ged.Core.Model;

/// <summary>A geo region (RFL <c>geo_region</c>): shape + geomod hardness / ice / shallow flags.</summary>
public sealed class GeoRegion
{
    public const ushort FlagIsSphere = 0x02;
    public const ushort FlagIsBox = 0x04;
    public const ushort FlagHiddenInEditor = 0x08;
    public const ushort FlagUseShallowGeomods = 0x20;
    public const ushort FlagIsIce = 0x40;

    public int Uid { get; set; }

    /// <summary>16-bit geo_region_flags bitfield.</summary>
    public ushort Flags { get; set; }

    /// <summary>Hardness 0-100.</summary>
    public ushort Hardness { get; set; }

    /// <summary>Present iff <see cref="FlagUseShallowGeomods"/> is set.</summary>
    public float? ShallowGeomodDepth { get; set; }

    public Vec3 Position { get; set; }

    /// <summary>Present iff <see cref="FlagIsBox"/> is set.</summary>
    public Mat3? Rotation { get; set; }

    public float? Width { get; set; }

    public float? Height { get; set; }

    public float? Depth { get; set; }

    /// <summary>Present iff <see cref="FlagIsSphere"/> is set.</summary>
    public float? Radius { get; set; }

    public bool IsBox => (Flags & FlagIsBox) != 0;

    public bool IsSphere => (Flags & FlagIsSphere) != 0;

    public bool IsIce => (Flags & FlagIsIce) != 0;
}
