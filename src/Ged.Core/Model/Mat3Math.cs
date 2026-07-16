using System;

namespace Ged.Core.Model;

/// <summary>
/// Pure rotation-matrix math for <see cref="Mat3"/>. A <see cref="Mat3"/> stores
/// three world-space basis rows — right, up, forward — that are the images of the
/// brush's local X, Y and Z axes. This matches RF/REDUX's world-from-local
/// transform exactly: <c>world = pos + local.X*Right + local.Y*Up + local.Z*Forward</c>
/// (validated against REDUX's RFGeometryParser and the compiled-geometry corpus).
/// Every helper honours that convention so brush transforms, the renderer, and
/// the geometry compiler all agree.
/// </summary>
public static class Mat3Math
{
    /// <summary>Maps a local vector into world space (no translation).</summary>
    public static Vec3 Transform(this Mat3 m, Vec3 local) =>
        m.Right.Scale(local.X).Add(m.Up.Scale(local.Y)).Add(m.Forward.Scale(local.Z));

    /// <summary>
    /// Maps a world vector back into local space, assuming <paramref name="m"/> is
    /// orthonormal (its transpose is its inverse): the dot with each basis row.
    /// </summary>
    public static Vec3 InverseTransform(this Mat3 m, Vec3 world) =>
        new(world.Dot(m.Right), world.Dot(m.Up), world.Dot(m.Forward));

    /// <summary>
    /// Composition: <c>Compose(a, b).Transform(v) == a.Transform(b.Transform(v))</c>.
    /// Applies <paramref name="inner"/> first, then <paramref name="outer"/>.
    /// </summary>
    public static Mat3 Compose(Mat3 outer, Mat3 inner) => new(
        outer.Transform(inner.Forward),
        outer.Transform(inner.Right),
        outer.Transform(inner.Up));

    /// <summary>
    /// The transpose, which is the inverse of an orthonormal rotation. Transform
    /// treats the rotation matrix R as having columns (Right, Up, Forward), so the
    /// transpose's columns are R's rows.
    /// </summary>
    public static Mat3 Transpose(this Mat3 m) => new(
        new Vec3(m.Right.Z, m.Up.Z, m.Forward.Z),
        new Vec3(m.Right.X, m.Up.X, m.Forward.X),
        new Vec3(m.Right.Y, m.Up.Y, m.Forward.Y));

    /// <summary>Rotation of <paramref name="radians"/> about a (normalized) world axis (Rodrigues).</summary>
    public static Mat3 FromAxisAngle(Vec3 axis, float radians)
    {
        Vec3 n = axis.Normalized();
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        float t = 1f - c;
        float x = n.X, y = n.Y, z = n.Z;

        // Columns of the Rodrigues matrix R = R·ex, R·ey, R·ez. Under the
        // (Right, Up, Forward) = (col0, col1, col2) convention, Transform(v) == R*v.
        var col0 = new Vec3((t * x * x) + c, (t * x * y) + (s * z), (t * x * z) - (s * y));
        var col1 = new Vec3((t * x * y) - (s * z), (t * y * y) + c, (t * y * z) + (s * x));
        var col2 = new Vec3((t * x * z) + (s * y), (t * y * z) - (s * x), (t * z * z) + c);
        return new Mat3(Forward: col2, Right: col0, Up: col1);
    }

    public static Mat3 RotationX(float radians) => FromAxisAngle(new Vec3(1, 0, 0), radians);

    public static Mat3 RotationY(float radians) => FromAxisAngle(new Vec3(0, 1, 0), radians);

    public static Mat3 RotationZ(float radians) => FromAxisAngle(new Vec3(0, 0, 1), radians);

    /// <summary>Re-orthonormalizes a matrix (Gram–Schmidt) to fight drift after many composed rotations.</summary>
    public static Mat3 Orthonormalize(this Mat3 m)
    {
        Vec3 f = m.Forward.Normalized();
        Vec3 r = m.Right.Sub(f.Scale(m.Right.Dot(f))).Normalized();
        Vec3 u = f.Cross(r);
        return new Mat3(f, r, u);
    }

    public static bool ApproxEquals(this Mat3 a, Mat3 b, float epsilon = 1e-4f) =>
        a.Forward.ApproxEquals(b.Forward, epsilon) &&
        a.Right.ApproxEquals(b.Right, epsilon) &&
        a.Up.ApproxEquals(b.Up, epsilon);
}
