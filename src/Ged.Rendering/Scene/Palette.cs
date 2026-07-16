namespace Ged.Rendering.Scene;

/// <summary>
/// Deterministic colors for room-color mode, billboard tints and default line
/// colors. All colors are packed R8G8B8A8 (little-endian) matching
/// <see cref="WorldVertex.Color"/>.
/// </summary>
public static class Palette
{
    /// <summary>Packs (r,g,b,a) bytes into the R8G8B8A8 little-endian layout the shaders expect.</summary>
    public static uint Rgba(byte r, byte g, byte b, byte a = 255) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));

    /// <summary>A stable, well-spread flat color for a room index (room-color render mode).</summary>
    public static uint RoomColor(int roomIndex)
    {
        if (roomIndex < 0)
        {
            return Rgba(90, 90, 90);
        }

        // Golden-ratio hue stepping keeps adjacent rooms visually distinct.
        float hue = (roomIndex * 0.61803398875f) % 1.0f;
        (byte r, byte g, byte b) = HsvToRgb(hue, 0.55f, 0.95f);
        return Rgba(r, g, b);
    }

    /// <summary>The tint for a billboard category.</summary>
    public static uint BillboardTint(BillboardKind kind) => kind switch
    {
        BillboardKind.Light => Rgba(255, 236, 140),
        BillboardKind.Event => Rgba(120, 200, 255),
        BillboardKind.AmbientSound => Rgba(180, 140, 255),
        BillboardKind.Respawn => Rgba(120, 255, 150),
        BillboardKind.ParticleEmitter => Rgba(255, 160, 90),
        BillboardKind.BoltEmitter => Rgba(255, 120, 200),
        BillboardKind.NavPoint => Rgba(90, 220, 220),
        BillboardKind.PlayerStart => Rgba(255, 80, 80),
        BillboardKind.Target => Rgba(255, 210, 60),
        BillboardKind.Item => Rgba(120, 255, 220),
        BillboardKind.Clutter => Rgba(200, 200, 180),
        BillboardKind.Entity => Rgba(255, 140, 140),
        BillboardKind.CutsceneCamera => Rgba(160, 160, 255),
        BillboardKind.Region => Rgba(120, 255, 120),
        BillboardKind.Trigger => Rgba(255, 170, 90),
        BillboardKind.GasRegion => Rgba(150, 220, 120),
        BillboardKind.ClimbRegion => Rgba(120, 210, 180),
        BillboardKind.PushRegion => Rgba(120, 200, 255),
        BillboardKind.RoomEffect => Rgba(160, 200, 255),
        BillboardKind.Eax => Rgba(200, 160, 255),
        BillboardKind.PathNode => Rgba(140, 220, 140),
        BillboardKind.Decal => Rgba(220, 180, 140),
        BillboardKind.Keyframe => Rgba(255, 200, 120),
        BillboardKind.Corona => Rgba(255, 255, 200),
        BillboardKind.Note => Rgba(255, 240, 150),
        BillboardKind.Bag => Rgba(200, 170, 120),
        BillboardKind.Vertex => Rgba(255, 255, 255),
        _ => Rgba(200, 200, 200),
    };

    /// <summary>
    /// The wireframe colour of an editable brush, by state (RED's brush colour
    /// preferences): selected wins, then locked/portal/detail/air, else regular.
    /// </summary>
    public static uint BrushStateColor(uint flags, int state, bool selected)
    {
        if (selected)
        {
            return Rgba(255, 240, 60);
        }

        if (state == 2)
        {
            return Rgba(150, 150, 150); // locked
        }

        if ((flags & 0x01) != 0)
        {
            return Rgba(230, 120, 230); // portal
        }

        if ((flags & 0x20) != 0)
        {
            return Rgba(230, 150, 90); // geoable (Alpine)
        }

        if ((flags & 0x04) != 0)
        {
            return Rgba(120, 220, 120); // detail
        }

        if ((flags & 0x02) != 0)
        {
            return Rgba(120, 200, 230); // air
        }

        return Rgba(200, 210, 235); // regular
    }

    private static (byte R, byte G, byte B) HsvToRgb(float h, float s, float v)
    {
        float r = v, g = v, b = v;
        if (s > 0f)
        {
            h = (h - MathF.Floor(h)) * 6f;
            int i = (int)h;
            float f = h - i;
            float p = v * (1f - s);
            float q = v * (1f - (s * f));
            float t = v * (1f - (s * (1f - f)));
            (r, g, b) = i switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
        }

        return ((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f));
    }
}
