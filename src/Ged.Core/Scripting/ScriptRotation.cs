using System;
using System.Globalization;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The one rotation representation of the script API: yaw / pitch / roll in degrees.
/// <c>yaw</c> turns about +Y (0 faces +Z, positive turns toward +X); <c>pitch</c> positive tilts
/// the facing direction up; <c>roll</c> positive tilts the right vector up. The matrix is
/// <c>Compose(RotationY(yaw), Compose(RotationX(-pitch), RotationZ(roll)))</c>. Also exposes the
/// resulting basis (forward / right / up) so a script can reason about facing directly.
/// </summary>
[ScriptDoc("A rotation in degrees: yaw about +Y (0 faces +Z, + turns toward +X), pitch (+ tilts up), roll (+ tilts right side up), plus its basis vectors.", Since = 2)]
public sealed class ScriptRotation
{
    /// <summary>Above this |pitch| the decomposition reports roll = 0 and folds it into yaw (gimbal lock).</summary>
    private const double GimbalPitch = 89.9;

    internal ScriptRotation(Mat3 matrix)
    {
        Matrix = matrix;
        (Yaw, Pitch, Roll) = ToEuler(matrix);
    }

    /// <summary>The orientation matrix this record describes.</summary>
    internal Mat3 Matrix { get; }

    [ScriptDoc("Degrees about +Y; 0 faces +Z, positive turns toward +X.", Since = 2)]
    public double Yaw { get; }

    [ScriptDoc("Degrees; positive tilts the facing direction up.", Since = 2)]
    public double Pitch { get; }

    [ScriptDoc("Degrees; positive tilts the right vector up.", Since = 2)]
    public double Roll { get; }

    [ScriptDoc("The facing direction (unit vec): the orientation's Forward row.", Since = 2)]
    public Vec3 Forward => Matrix.Forward;

    [ScriptDoc("The right direction (unit vec): the orientation's Right row.", Since = 2)]
    public Vec3 Right => Matrix.Right;

    [ScriptDoc("The up direction (unit vec): the orientation's Up row (a corona aims along this).", Since = 2)]
    public Vec3 Up => Matrix.Up;

    /// <summary>Builds the orientation for <paramref name="yaw"/>/<paramref name="pitch"/>/<paramref name="roll"/> degrees.</summary>
    internal static Mat3 FromEuler(double yaw, double pitch, double roll)
    {
        const double Deg = Math.PI / 180.0;
        Mat3 inner = Mat3Math.Compose(Mat3Math.RotationX((float)(-pitch * Deg)), Mat3Math.RotationZ((float)(roll * Deg)));
        return Mat3Math.Compose(Mat3Math.RotationY((float)(yaw * Deg)), inner).Orthonormalize();
    }

    /// <summary>
    /// Decomposes an orientation into yaw/pitch/roll degrees (rounded to 1e-4°, -0 normalised):
    /// <c>pitch = asin(F.y)</c>, <c>yaw = atan2(F.x, F.z)</c>, <c>roll = atan2(R.y, U.y)</c>; at
    /// |pitch| &gt; 89.9° roll is reported as 0 and folded into yaw.
    /// </summary>
    internal static (double Yaw, double Pitch, double Roll) ToEuler(Mat3 m)
    {
        const double Rad = 180.0 / Math.PI;
        Vec3 f = m.Forward;
        Vec3 r = m.Right;
        Vec3 u = m.Up;
        double pitch = Math.Asin(Math.Clamp((double)f.Y, -1.0, 1.0)) * Rad;
        double yaw;
        double roll;
        if (Math.Abs(pitch) > GimbalPitch)
        {
            // Facing straight up/down: forward carries no heading, so read it from the (horizontal) right vector.
            yaw = Math.Atan2(-(double)r.Z, r.X) * Rad;
            roll = 0;
        }
        else
        {
            yaw = Math.Atan2(f.X, f.Z) * Rad;
            roll = Math.Atan2(r.Y, u.Y) * Rad;
        }

        return (Clean(yaw), Clean(pitch), Clean(roll));
    }

    /// <summary>
    /// Converts a rotation-like value: a <c>rotation</c> record, a table
    /// <c>{yaw=, pitch=, roll=}</c> (missing = 0), or <c>{forward = vec, up = vec}</c>
    /// (orthonormalised: Right = Up × Forward, Up = Forward × Right). Unknown keys are rejected.
    /// </summary>
    internal static Mat3 FromArgs(string api, string what, object? value)
    {
        switch (value)
        {
            case ScriptRotation rot:
                return rot.Matrix;
            case ScriptArgs t when t.Items.Count == 0 && (t.Has("forward") || t.Has("up")):
                t.RejectUnknown($"{api} {what}", ScriptOptionSets.BasisRotation);
                if (!t.Has("forward") || !t.Has("up"))
                {
                    throw new ScriptApiException($"{api}: '{what}' as a basis needs both 'forward' and 'up'.",
                        "Or use {yaw=, pitch=, roll=} degrees.");
                }

                return FromBasis(api, what, ScriptArgs.ToVec(api, $"{what}.forward", t.Named["forward"]), ScriptArgs.ToVec(api, $"{what}.up", t.Named["up"]));
            case ScriptArgs t:
                t.RejectUnknown($"{api} {what}", ScriptOptionSets.EulerRotation);
                return FromEuler(t.Number(api, "yaw", 0), t.Number(api, "pitch", 0), t.Number(api, "roll", 0));
            default:
                throw new ScriptApiException(
                    $"{api}: '{what}' must be a rotation, {{yaw=, pitch=, roll=}} or {{forward=, up=}} (got {ScriptArgs.LuaTypeOf(value)}).",
                    "Degrees, e.g. rot = {yaw = 90} or rot = ged.rot(90).");
        }
    }

    /// <summary>Orthonormalises a forward/up pair into an orientation.</summary>
    internal static Mat3 FromBasis(string api, string what, Vec3 forward, Vec3 up)
    {
        if (forward.LengthSquared() < 1e-12f || up.LengthSquared() < 1e-12f)
        {
            throw new ScriptApiException($"{api}: '{what}' forward and up must be non-zero.");
        }

        Vec3 f = forward.Normalized();
        Vec3 right = up.Cross(f);
        if (right.LengthSquared() < 1e-10f)
        {
            throw new ScriptApiException($"{api}: '{what}' forward and up must not be parallel.");
        }

        right = right.Normalized();
        Vec3 u = f.Cross(right).Normalized();
        return new Mat3(f, right, u);
    }

    private static double Clean(double degrees)
    {
        double v = Math.Round(degrees, 4);
        if (v <= -180.0)
        {
            v += 360.0;
        }

        return v == 0 ? 0 : v; // normalise -0
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"rotation(yaw={ScriptArgs.Format(Yaw)}, pitch={ScriptArgs.Format(Pitch)}, roll={ScriptArgs.Format(Roll)})");
}
