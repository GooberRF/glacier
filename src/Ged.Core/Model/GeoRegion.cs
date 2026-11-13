namespace Ged.Core.Model;

/// <summary>A geo region (RFL <c>geo_region</c>): shape + geomod hardness / ice / shallow flags.</summary>
public sealed class GeoRegion
{
    public const ushort FlagIsSphere = 0x02;
    public const ushort FlagIsBox = 0x04;
    public const ushort FlagHiddenInEditor = 0x08;
    public const ushort FlagUseShallowGeomods = 0x20;
    public const ushort FlagIsIce = 0x40;

    /// <summary>
    /// The engine treats the low three flag bits as one shape code, not as independent bits
    /// (RF.exe level_read_geo_regions @ 0x00461B70: <c>shape = flags &amp; 7</c>).
    /// </summary>
    public const ushort ShapeMask = 0x07;

    /// <summary>Shape code 2: sphere.</summary>
    public const int ShapeSphere = 2;

    /// <summary>
    /// Shape code 3: a two-point + radius volume. The engine reads and then discards it
    /// (level_read_geo_regions frees the region instead of registering it); kept so such
    /// records round-trip.
    /// </summary>
    public const int ShapeTwoPoint = 3;

    /// <summary>Shape code 4: oriented box.</summary>
    public const int ShapeBox = 4;

    public int Uid { get; set; }

    /// <summary>16-bit geo_region_flags bitfield.</summary>
    public ushort Flags { get; set; }

    /// <summary>Hardness 0-100.</summary>
    public ushort Hardness { get; set; }

    /// <summary>Present iff <see cref="FlagUseShallowGeomods"/> is set.</summary>
    public float? ShallowGeomodDepth { get; set; }

    /// <summary>Present for the sphere, box and two-point shapes (absent for any other shape code).</summary>
    public Vec3 Position { get; set; }

    /// <summary>Present iff <see cref="HasRotation"/>: always for a box, and for a sphere that uses shallow geomods.</summary>
    public Mat3? Rotation { get; set; }

    public float? Width { get; set; }

    public float? Height { get; set; }

    public float? Depth { get; set; }

    /// <summary>Present for the sphere and two-point shapes.</summary>
    public float? Radius { get; set; }

    /// <summary>Second point of a <see cref="ShapeTwoPoint"/> record.</summary>
    public Vec3? EndPoint { get; set; }

    /// <summary>The shape code (<c>Flags &amp; 7</c>).</summary>
    public int Shape => Flags & ShapeMask;

    public bool IsBox => Shape == ShapeBox;

    public bool IsSphere => Shape == ShapeSphere;

    public bool IsIce => (Flags & FlagIsIce) != 0;

    public bool UsesShallowGeomods => (Flags & FlagUseShallowGeomods) != 0;

    /// <summary>
    /// Whether the record stores an orientation: every box does, and so does a sphere with
    /// shallow geomods (RF.exe 0x00461B70 reads the matrix for shape 2 only when the shallow
    /// flag is set — stock levels carry it, e.g. a shallow-geomod sphere in a stock
    /// single-player level stores an identity matrix between its position and radius).
    /// </summary>
    public bool HasRotation => IsBox || (IsSphere && UsesShallowGeomods);
}
