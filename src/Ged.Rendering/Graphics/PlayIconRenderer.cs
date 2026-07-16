using System;
using System.Numerics;

namespace Ged.Rendering.Graphics;

/// <summary>The four playtest launch buttons, distinguished by procedural badges.</summary>
public enum PlayIcon
{
    /// <summary>Play Level — a single play glyph.</summary>
    Level,

    /// <summary>Play Level from Camera — a single play glyph with a diamond badge.</summary>
    FromCamera,

    /// <summary>Play in Multi — a doubled play glyph.</summary>
    Multi,

    /// <summary>Play in Multi from Camera — a doubled play glyph with a diamond badge.</summary>
    MultiFromCamera,
}

/// <summary>
/// Draws the four playtest toolbar icons procedurally into RGBA images — no game art.
/// The convention: a green play glyph (<see cref="PlayIcon.Level"/>), doubled for the
/// multiplayer variants (<see cref="PlayIcon.Multi"/>), plus a small amber diamond
/// badge for the "…from camera" variants (<see cref="PlayIcon.FromCamera"/> /
/// <see cref="PlayIcon.MultiFromCamera"/>). Each shape gets a dark rim so it reads on
/// both light and dark toolbars. Anti-aliased by 4×4 supersampling.
/// </summary>
public static class PlayIconRenderer
{
    /// <summary>Default icon size in pixels.</summary>
    public const int Size = 24;

    private static readonly Vector3 Play = new(0x46, 0xC8, 0x6E);  // green
    private static readonly Vector3 Badge = new(0xF2, 0xB6, 0x3C); // amber
    private static readonly Vector3 Rim = new(0x10, 0x18, 0x12);   // near-black rim

    /// <summary>Renders one icon as a top-left-origin RGBA image (4 bytes/px).</summary>
    public static byte[] Render(PlayIcon icon, int size = Size)
    {
        if (size < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        bool doubled = icon is PlayIcon.Multi or PlayIcon.MultiFromCamera;
        bool badge = icon is PlayIcon.FromCamera or PlayIcon.MultiFromCamera;

        // Play triangle(s), in unit [0,1] space (scaled to size at sample time).
        Vector2[][] triangles = doubled
            ? new[]
            {
                Tri(0.14f, 0.20f, 0.14f, 0.80f, 0.52f, 0.50f),
                Tri(0.46f, 0.20f, 0.46f, 0.80f, 0.84f, 0.50f),
            }
            : new[] { Tri(0.26f, 0.18f, 0.26f, 0.82f, 0.80f, 0.50f) };

        // Diamond badge lower-right (unit space): centre + L1 radius.
        var badgeCenter = new Vector2(0.76f, 0.76f);
        const float badgeR = 0.20f;

        var px = new byte[size * size * 4];
        const int ss = 4; // 4×4 supersampling
        float inv = 1f / (size * ss);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float fillPlay = 0f, rimPlay = 0f, fillBadge = 0f, rimBadge = 0f;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        float u = ((x * ss) + sx + 0.5f) * inv;
                        float v = ((y * ss) + sy + 0.5f) * inv;
                        var pt = new Vector2(u, v);

                        foreach (Vector2[] t in triangles)
                        {
                            if (InTriangle(t, pt, 0f))
                            {
                                fillPlay += 1f;
                            }

                            if (InTriangle(t, pt, 0.055f))
                            {
                                rimPlay += 1f;
                            }
                        }

                        if (badge)
                        {
                            float d = MathF.Abs(pt.X - badgeCenter.X) + MathF.Abs(pt.Y - badgeCenter.Y);
                            if (d <= badgeR)
                            {
                                fillBadge += 1f;
                            }

                            if (d <= badgeR + 0.06f)
                            {
                                rimBadge += 1f;
                            }
                        }
                    }
                }

                float samples = ss * ss;
                var color = Vector3.Zero;
                float alpha = 0f;

                // Rim under fill: play glyph first, badge on top (badge sits at a corner).
                Blend(ref color, ref alpha, Rim, Math.Clamp(rimPlay / samples, 0f, 1f));
                Blend(ref color, ref alpha, Play, Math.Clamp(fillPlay / samples, 0f, 1f));
                Blend(ref color, ref alpha, Rim, Math.Clamp(rimBadge / samples, 0f, 1f));
                Blend(ref color, ref alpha, Badge, Math.Clamp(fillBadge / samples, 0f, 1f));

                int i = ((y * size) + x) * 4;
                px[i] = (byte)Math.Clamp(color.X, 0f, 255f);
                px[i + 1] = (byte)Math.Clamp(color.Y, 0f, 255f);
                px[i + 2] = (byte)Math.Clamp(color.Z, 0f, 255f);
                px[i + 3] = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
            }
        }

        return px;
    }

    /// <summary>Source-over compositing of a straight-alpha colour over the accumulator.</summary>
    private static void Blend(ref Vector3 dst, ref float dstA, Vector3 src, float srcA)
    {
        if (srcA <= 0f)
        {
            return;
        }

        dst = (dst * (1f - srcA)) + (src * srcA);
        dstA = dstA + (srcA * (1f - dstA));
    }

    private static Vector2[] Tri(float ax, float ay, float bx, float by, float cx, float cy) =>
        new[] { new Vector2(ax, ay), new Vector2(bx, by), new Vector2(cx, cy) };

    /// <summary>Point in a triangle, optionally dilated outward by <paramref name="grow"/>.</summary>
    private static bool InTriangle(Vector2[] t, Vector2 p, float grow)
    {
        if (grow <= 0f)
        {
            return SameSide(t[0], t[1], t[2], p) && SameSide(t[1], t[2], t[0], p) && SameSide(t[2], t[0], t[1], p);
        }

        Vector2 c = (t[0] + t[1] + t[2]) / 3f;
        float scale = 1f + (grow * 3f);
        var g = new[]
        {
            c + ((t[0] - c) * scale),
            c + ((t[1] - c) * scale),
            c + ((t[2] - c) * scale),
        };
        return SameSide(g[0], g[1], g[2], p) && SameSide(g[1], g[2], g[0], p) && SameSide(g[2], g[0], g[1], p);
    }

    private static bool SameSide(Vector2 a, Vector2 b, Vector2 refPt, Vector2 p)
    {
        float cross1 = ((b.X - a.X) * (refPt.Y - a.Y)) - ((b.Y - a.Y) * (refPt.X - a.X));
        float cross2 = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
        return cross1 * cross2 >= 0f;
    }
}
