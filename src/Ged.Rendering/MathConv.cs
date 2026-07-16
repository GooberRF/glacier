using System.Numerics;
using Silk.NET.Maths;

namespace Ged.Rendering;

/// <summary>Conversions between Silk.NET.Maths types (used for camera math) and
/// System.Numerics types (used for blittable vertex/constant-buffer data).</summary>
internal static class MathConv
{
    public static Vector3 ToNumerics(this Vector3D<float> v) => new(v.X, v.Y, v.Z);

    public static Vector3D<float> ToSilk(this Vector3 v) => new(v.X, v.Y, v.Z);

    public static Matrix4x4 ToNumerics(this Matrix4X4<float> m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}
