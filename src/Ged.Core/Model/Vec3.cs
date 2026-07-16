namespace Ged.Core.Model;

/// <summary>
/// A 3D vector of single-precision floats, stored and serialized bit-exactly
/// (x, y, z) in little-endian order, matching the RFL <c>vec3</c> type.
/// </summary>
public record struct Vec3(float X, float Y, float Z)
{
    /// <summary>The zero vector (0, 0, 0).</summary>
    public static Vec3 Zero => new(0f, 0f, 0f);

    public override readonly string ToString() => $"({X}, {Y}, {Z})";
}
