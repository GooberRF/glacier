using System;

namespace Ged.Core.Model;

/// <summary>
/// Pure vector arithmetic for <see cref="Vec3"/>. Kept out of the record type so
/// the serialized model stays a plain data carrier, while brush editing,
/// primitive generation and transforms have a small, tested math surface.
/// </summary>
public static class Vec3Math
{
    public static Vec3 Add(this Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 Sub(this Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 Scale(this Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 Mul(this Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static Vec3 Negate(this Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static float Dot(this Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vec3 Cross(this Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    public static float LengthSquared(this Vec3 a) => a.Dot(a);

    public static float Length(this Vec3 a) => MathF.Sqrt(a.LengthSquared());

    public static float Distance(this Vec3 a, Vec3 b) => a.Sub(b).Length();

    /// <summary>Returns the unit-length vector, or the input unchanged when it is (near) zero.</summary>
    public static Vec3 Normalized(this Vec3 a)
    {
        float len = a.Length();
        return len > 1e-12f ? a.Scale(1f / len) : a;
    }

    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a.Add(b.Sub(a).Scale(t));

    public static Vec3 Min(Vec3 a, Vec3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

    public static Vec3 Max(Vec3 a, Vec3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

    /// <summary>Component of <paramref name="a"/> along axis index (0=X,1=Y,2=Z).</summary>
    public static float Component(this Vec3 a, int axis) => axis switch
    {
        0 => a.X,
        1 => a.Y,
        _ => a.Z,
    };

    /// <summary>Returns a copy with the given axis component replaced.</summary>
    public static Vec3 WithComponent(this Vec3 a, int axis, float value) => axis switch
    {
        0 => a with { X = value },
        1 => a with { Y = value },
        _ => a with { Z = value },
    };

    /// <summary>True when the two vectors are equal within <paramref name="epsilon"/> per component.</summary>
    public static bool ApproxEquals(this Vec3 a, Vec3 b, float epsilon = 1e-4f) =>
        MathF.Abs(a.X - b.X) <= epsilon &&
        MathF.Abs(a.Y - b.Y) <= epsilon &&
        MathF.Abs(a.Z - b.Z) <= epsilon;
}
