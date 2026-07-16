namespace Ged.Core.Model;

/// <summary>A portal linking two rooms across a rectangle (RFL <c>portal</c>).</summary>
public sealed class Portal
{
    public int RoomIndex1 { get; set; }

    public int RoomIndex2 { get; set; }

    public Vec3 Point1 { get; set; }

    public Vec3 Point2 { get; set; }
}
