namespace Ged.Core.Model;

/// <summary>An Alpine bag object (alpine_bag_objects, 0x0AFBAE04).</summary>
public sealed class AlpineBagObject
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; }
}
