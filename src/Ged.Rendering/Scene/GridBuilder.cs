using System.Numerics;

namespace Ged.Rendering.Scene;

/// <summary>Appends a configurable reference grid (line segments) to a scene.</summary>
public static class GridBuilder
{
    /// <summary>
    /// Adds an XZ-plane grid centered on <paramref name="center"/>, with a major
    /// line every ten cells. Brightness scales the default grey.
    /// </summary>
    public static void Append(
        RenderScene scene,
        Vector3 center,
        float halfExtent,
        float spacing,
        float brightness = 1f,
        float y = 0f)
    {
        if (spacing <= 0f || halfExtent <= 0f)
        {
            return;
        }

        byte minor = (byte)Math.Clamp(70f * brightness, 0f, 255f);
        byte major = (byte)Math.Clamp(130f * brightness, 0f, 255f);
        uint minorColor = Palette.Rgba(minor, minor, minor, 180);
        uint majorColor = Palette.Rgba(major, major, major, 220);

        int steps = (int)MathF.Ceiling(halfExtent / spacing);
        float cx = MathF.Round(center.X / spacing) * spacing;
        float cz = MathF.Round(center.Z / spacing) * spacing;

        for (int i = -steps; i <= steps; i++)
        {
            float o = i * spacing;
            uint c = i % 10 == 0 ? majorColor : minorColor;
            float x = cx + o;
            float z = cz + o;
            scene.Lines.Add(new LineSegment(
                new Vector3(x, y, cz - halfExtent), new Vector3(x, y, cz + halfExtent), c));
            scene.Lines.Add(new LineSegment(
                new Vector3(cx - halfExtent, y, z), new Vector3(cx + halfExtent, y, z), c));
        }
    }
}
