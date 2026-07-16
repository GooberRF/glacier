namespace Ged.Core.Model;

/// <summary>
/// A 3x3 orientation matrix stored in RF's non-standard row order:
/// forward, right, up. Serialized as three consecutive <see cref="Vec3"/>s in
/// exactly that order (RFL <c>mat3</c> type).
/// </summary>
public record struct Mat3(Vec3 Forward, Vec3 Right, Vec3 Up)
{
    /// <summary>
    /// The no-rotation orientation as RF stores it: right = +X, up = +Y,
    /// forward = +Z. Under the local→world convention
    /// (<c>world = pos + x·Right + y·Up + z·Forward</c>) this maps a local point
    /// to itself, matching RED/REDUX. (The rows are the world basis of the brush's
    /// local X/Y/Z axes, so a brush's local Z runs along its forward vector.)
    /// </summary>
    public static Mat3 Identity => new(
        new Vec3(0f, 0f, 1f),
        new Vec3(1f, 0f, 0f),
        new Vec3(0f, 1f, 0f));
}
