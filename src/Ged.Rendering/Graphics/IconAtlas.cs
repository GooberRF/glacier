using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.IO.Tex;

namespace Ged.Rendering.Graphics;

/// <summary>
/// The editor's object-icon set, laid out as cells in the procedurally generated
/// <see cref="IconAtlas"/>. Each value is the atlas cell index. These are ORIGINAL
/// icons drawn from simple strokes/shapes at startup — no game resources are used.
/// </summary>
public enum EditorIcon
{
    Disc = 0,            // particles / generic soft sprite (no outline)
    Light = 1,           // bulb
    LightEditorOnly = 2, // outlined bulb
    AmbientSound = 3,    // speaker
    Event = 4,           // gear
    Trigger = 5,         // lever
    PlayerStart = 6,     // play flag
    Respawn = 7,         // flag
    ParticleEmitter = 8, // spray
    BoltEmitter = 9,     // zigzag
    NavPoint = 10,       // node dot
    Waypoint = 11,       // path dots
    CutsceneCamera = 12, // camera
    PathNode = 13,       // small node
    Decal = 14,          // picture frame
    PushRegion = 15,     // arrow
    ClimbRegion = 16,    // ladder
    GasRegion = 17,      // cloud
    GeoRegion = 18,      // crater
    RoomEffect = 19,     // waves
    Eax = 20,            // ear/wave
    Target = 21,         // crosshair
    Keyframe = 22,       // diamond
    Note = 23,           // note page
    Corona = 24,         // starburst
    Bag = 25,            // bag
    Entity = 26,         // person
    Clutter = 27,        // box
    Item = 28,           // pickup star
    Generic = 29,        // dot
    KeyframeSilver = 30, // hollow diamond — a non-start (silver) mover keyframe
}

/// <summary>
/// A small original icon atlas drawn procedurally into an RGBA texture at startup:
/// an 8×8 grid of 32-px cells, each a white silhouette with a 1-px dark outline in
/// the RGB channels and coverage in alpha. The billboard shader multiplies RGB by
/// the per-object tint, so the white core takes the object-type colour while the
/// dark outline stays dark for contrast on bright surfaces. Deliberately authored
/// (line/shape rasterization) so no copyrighted game art is copied.
/// </summary>
public static class IconAtlas
{
    public const int Cell = 32;
    public const int Cols = 8;
    public const int Rows = 8;
    public const int Width = Cols * Cell;
    public const int Height = Rows * Cell;

    /// <summary>The atlas UV sub-rect (u0, v0, u1, v1) for an icon cell.</summary>
    public static (float U0, float V0, float U1, float V1) Rect(int icon)
    {
        int col = icon % Cols;
        int row = icon / Cols;
        float u0 = (col * Cell) / (float)Width;
        float v0 = (row * Cell) / (float)Height;
        return (u0, v0, u0 + (Cell / (float)Width), v0 + (Cell / (float)Height));
    }

    /// <summary>Builds the full RGBA atlas image (top-left origin, 4 bytes/px).</summary>
    public static byte[] Build()
    {
        var pixels = new byte[Width * Height * 4];
        foreach (EditorIcon icon in Enum.GetValues<EditorIcon>())
        {
            PaintCell(pixels, icon);
        }

        return pixels;
    }

    /// <summary>
    /// RED's own object-icon TGA file names (in <c>ui.vpp</c> / <c>alpinefaction.vpp</c>),
    /// discovered empirically by enumerating the packfiles. Mapping documented in
    /// <c>docs/research/red-object-icons.md</c>. Only categories with a genuine RED
    /// icon appear; the rest keep the GED-drawn cell. GED never ships these bitmaps —
    /// they are read from the user's own game files through the VFS.
    /// </summary>
    public static IReadOnlyDictionary<EditorIcon, string> OriginalFileNames { get; } =
        new Dictionary<EditorIcon, string>
        {
            [EditorIcon.Light] = "Icon_Light.tga",
            [EditorIcon.LightEditorOnly] = "Icon_Light_Editor_only.tga",
            [EditorIcon.AmbientSound] = "Icon_Ambient.tga",
            [EditorIcon.Event] = "Icon_Event.tga",
            [EditorIcon.Trigger] = "Icon_Trigger.tga",
            [EditorIcon.PlayerStart] = "Icon_SinglePlayerStart.tga",
            [EditorIcon.Respawn] = "Icon_MultiPlayerStart.tga",
            [EditorIcon.ParticleEmitter] = "Icon_ParticleEmitter.tga",
            [EditorIcon.BoltEmitter] = "Icon_BoltEmitter.tga",
            [EditorIcon.NavPoint] = "Icon_Waypoint.tga",
            [EditorIcon.Waypoint] = "Icon_Waypoint.tga",
            [EditorIcon.CutsceneCamera] = "Icon_CameraPosition.tga",
            [EditorIcon.PathNode] = "Icon_CutscenePathNode.tga",
            [EditorIcon.Decal] = "Icon_Decal.tga",
            [EditorIcon.ClimbRegion] = "Icon_ClimbRegion.tga",
            [EditorIcon.GasRegion] = "Icon_GasRegion.tga",
            [EditorIcon.GeoRegion] = "Icon_GeoRegion.tga",
            [EditorIcon.RoomEffect] = "Icon_RoomFX.tga",
            [EditorIcon.Eax] = "Icon_EAX.tga",
            [EditorIcon.Target] = "Icon_Target.tga",
            [EditorIcon.Keyframe] = "Icon_Keyframe_Gold.tga",
            [EditorIcon.KeyframeSilver] = "Icon_Keyframe_Silver.tga",
            [EditorIcon.Note] = "Icon_AFNote.tga",
            [EditorIcon.Corona] = "Icon_AFCorona.tga",
        };

    /// <summary>
    /// Builds the atlas preferring RED's original icon bitmaps: for each mapped icon
    /// <paramref name="loadOriginal"/> may return the decoded (RGBA) bitmap, which is
    /// nearest-scaled into its cell over the GED base. Any icon that does not resolve
    /// keeps its GED-drawn cell (per-icon graceful fallback). The particle disc is
    /// always the GED soft sprite.
    /// </summary>
    public static byte[] Compose(Func<EditorIcon, TextureImage?> loadOriginal) =>
        Compose(loadOriginal, out _);

    /// <summary>
    /// <see cref="Compose(Func{EditorIcon, TextureImage?})"/>, additionally reporting each
    /// resolved original's height/width ASPECT RATIO. The atlas cells are square, so a
    /// non-square original (RED ships two: <c>Icon_MultiPlayerStart.tga</c> at 32×64 → 2.0
    /// and <c>Icon_Keyframe_Gold.tga</c> at 64×32 → 0.5) gets distorted by the cell blit;
    /// the billboard emission uses these ratios to size the quad so the icon renders at its
    /// true aspect (standard width, height scaled). Icons absent from the result (unresolved,
    /// or GED-drawn — square by design) render square as before.
    /// </summary>
    public static byte[] Compose(
        Func<EditorIcon, TextureImage?> loadOriginal,
        out IReadOnlyDictionary<EditorIcon, float> heightOverWidth)
    {
        ArgumentNullException.ThrowIfNull(loadOriginal);
        byte[] pixels = Build();
        var aspects = new Dictionary<EditorIcon, float>();

        foreach ((EditorIcon icon, string _) in OriginalFileNames)
        {
            TextureImage? src = loadOriginal(icon);
            if (src is not null && src.Width > 0 && src.Height > 0)
            {
                BlitScaled(pixels, icon, src);
                aspects[icon] = src.Height / (float)src.Width;
            }
        }

        heightOverWidth = aspects;
        return pixels;
    }

    private static void BlitScaled(byte[] pixels, EditorIcon icon, TextureImage src)
    {
        int col = (int)icon % Cols;
        int row = (int)icon / Cols;
        int ox = col * Cell;
        int oy = row * Cell;

        for (int y = 0; y < Cell; y++)
        {
            int sy = Math.Min(src.Height - 1, y * src.Height / Cell);
            for (int x = 0; x < Cell; x++)
            {
                int sx = Math.Min(src.Width - 1, x * src.Width / Cell);
                (byte r, byte g, byte b, byte a) = src.GetPixel(sx, sy);
                int dst = (((oy + y) * Width) + ox + x) * 4;
                pixels[dst] = r;
                pixels[dst + 1] = g;
                pixels[dst + 2] = b;
                pixels[dst + 3] = a;
            }
        }
    }

    private static void PaintCell(byte[] pixels, EditorIcon icon)
    {
        var ink = new float[Cell * Cell]; // shape coverage 0..1
        var p = new Painter(ink);
        DrawIcon(p, icon);

        int col = (int)icon % Cols;
        int row = (int)icon / Cols;
        int ox = col * Cell;
        int oy = row * Cell;
        bool outline = icon != EditorIcon.Disc; // particles: soft sprite, no dark rim

        for (int y = 0; y < Cell; y++)
        {
            for (int x = 0; x < Cell; x++)
            {
                float fill = ink[(y * Cell) + x];
                float alpha = fill;
                float core = 1f;

                if (outline)
                {
                    float dil = Dilate(ink, x, y);
                    alpha = dil;
                    core = dil > 1e-3f ? Math.Clamp(fill / dil, 0f, 1f) : 0f;
                }

                if (alpha <= 1e-3f)
                {
                    continue;
                }

                // Core = white (tinted at draw time), rim = dark.
                byte rgb = (byte)Math.Clamp(30f + (core * 225f), 0f, 255f);
                int dst = (((oy + y) * Width) + ox + x) * 4;
                pixels[dst] = rgb;
                pixels[dst + 1] = rgb;
                pixels[dst + 2] = rgb;
                pixels[dst + 3] = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
            }
        }
    }

    /// <summary>Coverage after a ~1.4-px dilation, for the dark outline ring.</summary>
    private static float Dilate(float[] ink, int x, int y)
    {
        float max = 0f;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if ((dx * dx) + (dy * dy) > 3)
                {
                    continue; // roughly a radius-1.4 disc
                }

                int nx = x + dx;
                int ny = y + dy;
                if (nx >= 0 && nx < Cell && ny >= 0 && ny < Cell)
                {
                    max = MathF.Max(max, ink[(ny * Cell) + nx]);
                }
            }
        }

        return max;
    }

    private static void DrawIcon(Painter p, EditorIcon icon)
    {
        switch (icon)
        {
            case EditorIcon.Disc:
                p.SoftDisc(16, 16, 15);
                break;
            case EditorIcon.Light:
                p.Disc(16, 13, 7);
                p.Rect(13, 19, 6, 5, fill: true);
                p.Line(13, 24, 19, 24, 2.4f);
                break;
            case EditorIcon.LightEditorOnly:
                p.Ring(16, 13, 7, 2f);
                p.Rect(13, 19, 6, 5, fill: false, 1.8f);
                break;
            case EditorIcon.AmbientSound:
                p.Poly(new[] { V(7, 13), V(12, 13), V(17, 8), V(17, 24), V(12, 19), V(7, 19) }, fill: true);
                p.Arc(19, 16, 5, -0.7f, 0.7f, 1.8f);
                p.Arc(19, 16, 8, -0.7f, 0.7f, 1.8f);
                break;
            case EditorIcon.Event:
                p.Ring(16, 16, 6, 3f);
                for (int i = 0; i < 8; i++)
                {
                    float a = i / 8f * MathF.Tau;
                    p.Line(16 + (MathF.Cos(a) * 6), 16 + (MathF.Sin(a) * 6), 16 + (MathF.Cos(a) * 10), 16 + (MathF.Sin(a) * 10), 2.4f);
                }

                break;
            case EditorIcon.Trigger:
                p.Rect(9, 20, 14, 5, fill: true);
                p.Line(16, 22, 22, 9, 2.6f);
                p.Disc(22, 9, 3);
                break;
            case EditorIcon.PlayerStart:
                p.Poly(new[] { V(11, 8), V(24, 16), V(11, 24) }, fill: true);
                break;
            case EditorIcon.Respawn:
                p.Line(10, 7, 10, 26, 2.4f);
                p.Poly(new[] { V(10, 7), V(23, 11), V(10, 16) }, fill: true);
                break;
            case EditorIcon.ParticleEmitter:
                p.Line(9, 23, 16, 12, 2.2f);
                for (int i = 0; i < 5; i++)
                {
                    float a = (-0.9f + (i * 0.45f));
                    p.Disc(16 + (MathF.Cos(a) * 8), 12 - (MathF.Sin(a) * 2) + (i % 2), 1.6f);
                }

                break;
            case EditorIcon.BoltEmitter:
                p.Poly(new[] { V(9, 24), V(15, 15), V(12, 15), V(20, 6), V(16, 14), V(20, 14), V(11, 26) }, fill: false, 2.2f);
                break;
            case EditorIcon.NavPoint:
                p.Ring(16, 16, 7, 2.2f);
                p.Disc(16, 16, 3);
                break;
            case EditorIcon.Waypoint:
                p.Disc(9, 22, 2.4f);
                p.Disc(16, 16, 2.4f);
                p.Disc(23, 10, 2.4f);
                p.Line(9, 22, 23, 10, 1.2f);
                break;
            case EditorIcon.CutsceneCamera:
                p.Rect(8, 12, 12, 9, fill: true);
                p.Poly(new[] { V(20, 14), V(25, 11), V(25, 21), V(20, 19) }, fill: true);
                p.Disc(24, 10, 1.6f);
                break;
            case EditorIcon.PathNode:
                p.Poly(new[] { V(16, 10), V(22, 16), V(16, 22), V(10, 16) }, fill: false, 2f);
                break;
            case EditorIcon.Decal:
                p.Rect(8, 8, 16, 16, fill: false, 2.2f);
                p.Poly(new[] { V(11, 21), V(15, 15), V(18, 18), V(21, 13), V(21, 21) }, fill: true);
                break;
            case EditorIcon.PushRegion:
                p.Line(16, 24, 16, 9, 2.6f);
                p.Poly(new[] { V(16, 6), V(22, 14), V(10, 14) }, fill: true);
                break;
            case EditorIcon.ClimbRegion:
                p.Line(11, 7, 11, 25, 2.2f);
                p.Line(21, 7, 21, 25, 2.2f);
                for (int r = 0; r < 4; r++)
                {
                    p.Line(11, 9 + (r * 5), 21, 9 + (r * 5), 1.8f);
                }

                break;
            case EditorIcon.GasRegion:
                p.Disc(12, 18, 5);
                p.Disc(20, 18, 5);
                p.Disc(16, 14, 6);
                p.Rect(12, 18, 8, 5, fill: true);
                break;
            case EditorIcon.GeoRegion:
                p.Ring(16, 17, 8, 2.2f);
                p.Ring(16, 17, 4, 1.8f);
                p.Line(16, 5, 16, 9, 2f);
                break;
            case EditorIcon.RoomEffect:
                for (int r = 0; r < 3; r++)
                {
                    p.Arc(16, 26, 6 + (r * 5), -2.4f, -0.7f, 1.8f);
                }

                break;
            case EditorIcon.Eax:
                p.Arc(14, 16, 8, -1.6f, 1.6f, 2.2f);
                p.Disc(14, 16, 2.4f);
                break;
            case EditorIcon.Target:
                p.Ring(16, 16, 8, 2f);
                p.Line(16, 4, 16, 12, 1.8f);
                p.Line(16, 20, 16, 28, 1.8f);
                p.Line(4, 16, 12, 16, 1.8f);
                p.Line(20, 16, 28, 16, 1.8f);
                break;
            case EditorIcon.Keyframe:
                p.Poly(new[] { V(16, 7), V(24, 16), V(16, 25), V(8, 16) }, fill: true);
                break;
            case EditorIcon.KeyframeSilver:
                // A non-start keyframe: a SOLID filled diamond, exactly like the gold start diamond —
                // matching RED's Icon_Keyframe_Silver.tga, which (verified by extracting it from ui.vpp)
                // is a FILLED diamond identical in shape to the gold one and separated from it purely by
                // COLOUR, not by fill. Restyled from the old HOLLOW outline that read as an empty /
                // invalid marker. The gold/silver distinction is carried by the billboard tint (warm vs
                // neutral grey), and RED's own gold/silver TGAs replace both when original icons are on.
                p.Poly(new[] { V(16, 7), V(24, 16), V(16, 25), V(8, 16) }, fill: true);
                break;
            case EditorIcon.Note:
                p.Rect(9, 7, 14, 18, fill: false, 2f);
                p.Line(12, 12, 20, 12, 1.4f);
                p.Line(12, 16, 20, 16, 1.4f);
                p.Line(12, 20, 17, 20, 1.4f);
                break;
            case EditorIcon.Corona:
                p.Disc(16, 16, 3.5f);
                for (int i = 0; i < 8; i++)
                {
                    float a = i / 8f * MathF.Tau;
                    p.Line(16 + (MathF.Cos(a) * 5), 16 + (MathF.Sin(a) * 5), 16 + (MathF.Cos(a) * 11), 16 + (MathF.Sin(a) * 11), 1.8f);
                }

                break;
            case EditorIcon.Bag:
                p.Poly(new[] { V(10, 12), V(22, 12), V(24, 25), V(8, 25) }, fill: true);
                p.Arc(16, 12, 5, -MathF.PI, 0f, 2f);
                break;
            case EditorIcon.Entity:
                p.Disc(16, 10, 4);
                p.Poly(new[] { V(10, 26), V(12, 16), V(20, 16), V(22, 26) }, fill: true);
                break;
            case EditorIcon.Clutter:
                p.Poly(new[] { V(8, 12), V(16, 8), V(24, 12), V(16, 16) }, fill: false, 1.8f);
                p.Poly(new[] { V(8, 12), V(8, 22), V(16, 26), V(16, 16) }, fill: false, 1.8f);
                p.Poly(new[] { V(24, 12), V(24, 22), V(16, 26), V(16, 16) }, fill: false, 1.8f);
                break;
            case EditorIcon.Item:
                p.Poly(Star(16, 16, 10, 4.5f, 5), fill: true);
                break;
            default:
                p.Disc(16, 16, 4);
                break;
        }
    }

    private static Vector2 V(float x, float y) => new(x, y);

    private static Vector2[] Star(float cx, float cy, float outer, float inner, int points)
    {
        var pts = new Vector2[points * 2];
        for (int i = 0; i < points * 2; i++)
        {
            float r = (i % 2 == 0) ? outer : inner;
            float a = (i / (float)(points * 2) * MathF.Tau) - (MathF.PI / 2);
            pts[i] = new Vector2(cx + (MathF.Cos(a) * r), cy + (MathF.Sin(a) * r));
        }

        return pts;
    }

    /// <summary>A tiny anti-aliased coverage rasterizer for one 32×32 cell.</summary>
    private sealed class Painter
    {
        private readonly float[] _ink;

        public Painter(float[] ink) => _ink = ink;

        public void Disc(float cx, float cy, float r)
        {
            Fill((x, y) => r + 0.5f - Dist(x, y, cx, cy));
        }

        /// <summary>A soft radial sprite (solid to 0.72r, feathered to the rim) — the particle look.</summary>
        public void SoftDisc(float cx, float cy, float r)
        {
            Fill((x, y) =>
            {
                float d = Dist(x, y, cx, cy) / r;
                float t = Math.Clamp((d - 0.72f) / 0.28f, 0f, 1f);
                return 1f - (t * t * (3f - (2f * t)));
            });
        }

        public void Ring(float cx, float cy, float r, float w)
        {
            float h = w * 0.5f;
            Fill((x, y) => h + 0.5f - MathF.Abs(Dist(x, y, cx, cy) - r));
        }

        public void Line(float x0, float y0, float x1, float y1, float w)
        {
            float h = w * 0.5f;
            Fill((x, y) => h + 0.5f - SegDist(x, y, x0, y0, x1, y1));
        }

        public void Arc(float cx, float cy, float r, float a0, float a1, float w)
        {
            float h = w * 0.5f;
            Fill((x, y) =>
            {
                float ang = MathF.Atan2(y - cy, x - cx);
                float lo = MathF.Min(a0, a1);
                float hi = MathF.Max(a0, a1);
                if (ang < lo || ang > hi)
                {
                    return -1f;
                }

                return h + 0.5f - MathF.Abs(Dist(x, y, cx, cy) - r);
            });
        }

        public void Rect(float x, float y, float w, float h, bool fill, float stroke = 2f)
        {
            if (fill)
            {
                Fill((px, py) =>
                {
                    float dx = MathF.Min(px - x, x + w - px);
                    float dy = MathF.Min(py - y, y + h - py);
                    return MathF.Min(dx, dy) + 0.5f;
                });
            }
            else
            {
                Line(x, y, x + w, y, stroke);
                Line(x + w, y, x + w, y + h, stroke);
                Line(x + w, y + h, x, y + h, stroke);
                Line(x, y + h, x, y, stroke);
            }
        }

        public void Poly(Vector2[] pts, bool fill, float stroke = 2f)
        {
            if (fill)
            {
                Fill((x, y) => PointInPoly(pts, x, y) ? 1f : 0.5f - MinEdgeDist(pts, x, y));
            }
            else
            {
                for (int i = 0; i < pts.Length; i++)
                {
                    Vector2 a = pts[i];
                    Vector2 b = pts[(i + 1) % pts.Length];
                    Line(a.X, a.Y, b.X, b.Y, stroke);
                }
            }
        }

        private void Fill(Func<float, float, float> coverage)
        {
            for (int y = 0; y < Cell; y++)
            {
                for (int x = 0; x < Cell; x++)
                {
                    float c = Math.Clamp(coverage(x, y), 0f, 1f);
                    int i = (y * Cell) + x;
                    if (c > _ink[i])
                    {
                        _ink[i] = c;
                    }
                }
            }
        }

        private static float Dist(float x, float y, float cx, float cy) =>
            MathF.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));

        private static float SegDist(float px, float py, float x0, float y0, float x1, float y1)
        {
            float dx = x1 - x0;
            float dy = y1 - y0;
            float len2 = (dx * dx) + (dy * dy);
            float t = len2 > 1e-6f ? Math.Clamp((((px - x0) * dx) + ((py - y0) * dy)) / len2, 0f, 1f) : 0f;
            float qx = x0 + (t * dx);
            float qy = y0 + (t * dy);
            return Dist(px, py, qx, qy);
        }

        private static bool PointInPoly(Vector2[] v, float px, float py)
        {
            bool inside = false;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
            {
                if (((v[i].Y > py) != (v[j].Y > py)) &&
                    (px < ((v[j].X - v[i].X) * (py - v[i].Y) / (v[j].Y - v[i].Y)) + v[i].X))
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static float MinEdgeDist(Vector2[] v, float px, float py)
        {
            float min = float.MaxValue;
            for (int i = 0; i < v.Length; i++)
            {
                Vector2 a = v[i];
                Vector2 b = v[(i + 1) % v.Length];
                min = MathF.Min(min, SegDist(px, py, a.X, a.Y, b.X, b.Y));
            }

            return min;
        }
    }
}
