namespace Ged.Core.Model;

/// <summary>A climbing region (RFL <c>climbing_region</c>).</summary>
public sealed class ClimbingRegion
{
    public ObjectHeader Header { get; set; } = new();

    /// <summary>1 = ladder, 2 = chain_fence.</summary>
    public int RegionType { get; set; }

    public Vec3 Extents { get; set; }
}
