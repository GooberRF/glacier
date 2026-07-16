using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Derives the cutting plane for the stock two-point Clip tool: two points picked
/// in a viewport define a line in the view plane, and the third axis (the plane's
/// extrusion direction) comes from the view direction. Pure and testable; the App
/// feeds the result to <see cref="BrushOps.Clip"/>.
/// </summary>
public static class ClipPlanes
{
    /// <summary>
    /// The plane through <paramref name="a"/> and <paramref name="b"/> extruded
    /// along <paramref name="viewDirection"/> (the axis perpendicular to the
    /// viewport). Returns the plane point (a) and unit normal; the normal is
    /// <c>(b-a) × viewDir</c>. When the inputs are degenerate the normal falls back
    /// to +Z so the caller can still report a friendly error.
    /// </summary>
    public static (Vec3 Point, Vec3 Normal) FromTwoPoints(Vec3 a, Vec3 b, Vec3 viewDirection)
    {
        Vec3 edge = b.Sub(a);
        Vec3 normal = edge.Cross(viewDirection);
        if (normal.LengthSquared() < 1e-8f)
        {
            return (a, new Vec3(0, 0, 1));
        }

        return (a, normal.Normalized());
    }
}
