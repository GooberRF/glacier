namespace Ged.Core.Model;

/// <summary>Motion data for a moving group (RFL <c>moving_group_data</c>).</summary>
public sealed class MovingGroupData
{
    public List<Keyframe> Keyframes { get; set; } = new();

    public List<MovingGroupMemberTransform> MemberTransforms { get; set; } = new();

    public byte IsDoor { get; set; }

    public byte RotateInPlace { get; set; }

    public byte StartsBackwards { get; set; }

    public byte UseTravelTimeAsSpeed { get; set; }

    public byte ForceOrient { get; set; }

    public byte NoPlayerCollide { get; set; }

    /// <summary>1 one_way, 2 ping_pong_once, 3 ping_pong_infinite, 4 loop_once, 5 loop_infinite, 6 lift.</summary>
    public int MovementType { get; set; }

    public int StartingKeyframe { get; set; }

    public string StartSound { get; set; } = string.Empty;

    public float StartVol { get; set; }

    public string LoopingSound { get; set; } = string.Empty;

    public float LoopingVol { get; set; }

    public string StopSound { get; set; } = string.Empty;

    public float StopVol { get; set; }

    public string CloseSound { get; set; } = string.Empty;

    public float CloseVol { get; set; }
}
