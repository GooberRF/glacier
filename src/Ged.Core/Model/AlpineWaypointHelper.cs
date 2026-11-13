namespace Ged.Core.Model;

/// <summary>
/// An Alpine vehicle waypoint helper (0x0AFBAE0D; record common/include/common/alpine_waypoint_helper.h:44-98):
/// a navigation node for AI vehicles. No orientation and no script name.
/// </summary>
public sealed class AlpineWaypointHelper
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    /// <summary>0 air, 1 water, 2 ground.</summary>
    public byte HelperType { get; set; }

    /// <summary>0x1 = landing spot.</summary>
    public byte Flags { get; set; }

    /// <summary>0 jeep, 1 APC, 2 driller.</summary>
    public byte SizeClass { get; set; } = 2;

    public float Radius { get; set; } = 4f;

    /// <summary>Linked helper UIDs as stored (Alpine loads the first 64).</summary>
    public List<int> Links { get; set; } = new();
}
