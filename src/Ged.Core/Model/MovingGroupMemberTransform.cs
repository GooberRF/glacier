namespace Ged.Core.Model;

/// <summary>Per-member transform of a moving group (RFL <c>moving_group_member_transform</c>).</summary>
public sealed class MovingGroupMemberTransform
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }
}
