namespace Ged.Core.Model;

/// <summary>
/// Mover-wide path settings of one moving group (0x0AFBAE0E mover record; common/include/common/mover_path.h:51-63,
/// 213-230), keyed by the group's first keyframe UID. Alpine stores only non-default groups.
/// </summary>
public sealed class AlpineMoverPathMover
{
    public int FirstKeyframeUid { get; set; }

    /// <summary>0 none, 1 follow path, 2 keyframe rotation.</summary>
    public byte OrientMode { get; set; }

    /// <summary>0x1 riders turn with the mover, 0x2 riders detach on leave.</summary>
    public byte RiderFlags { get; set; }

    /// <summary>The two padding bytes after the flags, as stored little-endian (Alpine writes 0).</summary>
    public ushort Padding { get; set; }
}

/// <summary>
/// Curve settings of one mover keyframe (0x0AFBAE0E keyframe record; mover_path.h:124-132, 186-208). Handles
/// are offsets from the keyframe. Alpine stores only non-default keyframes.
/// </summary>
public sealed class AlpineMoverPathKeyframe
{
    public int KeyframeUid { get; set; }

    /// <summary>0 straight, 1 curved.</summary>
    public byte SegmentMode { get; set; }

    /// <summary>0 automatic, 1 aligned, 2 free.</summary>
    public byte TangentMode { get; set; }

    /// <summary>0 none, 1 in, 2 out, 3 in and out.</summary>
    public byte Ease { get; set; }

    /// <summary>Low two bits: the segment's orient mode (0 = the mover's); the other bits are reserved.</summary>
    public byte SegmentOrient { get; set; }

    public Vec3 InHandle { get; set; }

    public Vec3 OutHandle { get; set; }
}
