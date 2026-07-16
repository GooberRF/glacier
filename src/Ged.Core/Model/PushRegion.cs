namespace Ged.Core.Model;

/// <summary>A push region (RFL <c>push_region</c>).</summary>
public sealed class PushRegion
{
    public ObjectHeader Header { get; set; } = new();

    /// <summary>1 sphere, 2 axis_aligned_box, 3 oriented_box.</summary>
    public int Shape { get; set; }

    /// <summary>Present for non-sphere shapes.</summary>
    public Vec3? Extents { get; set; }

    /// <summary>Present for the sphere shape.</summary>
    public float? Radius { get; set; }

    public float Strength { get; set; }

    /// <summary>16-bit push_region_flags bitfield.</summary>
    public ushort Flags { get; set; }

    public ushort Turbulence { get; set; }
}
