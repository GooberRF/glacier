using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Rendering.Scene;

/// <summary>What the Terrain Tools cursor shows at one moment.</summary>
public sealed class TerrainCursorState
{
    /// <summary>The surface point under the cursor (world), or null when the cursor is off the terrain.</summary>
    public Vector3? Hover { get; init; }

    /// <summary>A stroke is held (the rings draw red instead of yellow).</summary>
    public bool Stroking { get; init; }

    /// <summary>Bridge Points: the drag's two ends (world X, Z) while it is held.</summary>
    public (Vector2 A, Vector2 B)? Segment { get; init; }
}

/// <summary>
/// The Terrain Tools brush cursor, draped on the surface as Alpine RED draws it (editor_patch/terrain_paint.cpp:1272-1315,
/// 1344-1430, 2119-2174): the brush ring and, unless the falloff is Constant or the tool edits holes, the half-radius ring
/// (yellow, red while a stroke is held), a short tick up from the point under the cursor; a Bridge Points drag as a capsule
/// and its centre line; Geoable Chunks as chunk outlines (orange geoable, grey not, the hovered chunk yellow / red); the
/// mirror lines (cyan X, magenta Z) and the dimmed brush at each mirror image.
/// </summary>
public static class TerrainBrushCursor
{
    private static readonly uint Yellow = Palette.Rgba(255, 255, 0);
    private static readonly uint Red = Palette.Rgba(255, 0, 0);
    private static readonly uint DimYellow = Palette.Rgba(128, 128, 0);
    private static readonly uint DimRed = Palette.Rgba(128, 0, 0);
    private static readonly uint GeoOn = Palette.Rgba(255, 128, 0);
    private static readonly uint GeoOff = Palette.Rgba(128, 128, 128);
    private static readonly uint MirrorX = Palette.Rgba(0, 255, 255);
    private static readonly uint MirrorZ = Palette.Rgba(255, 0, 255);

    /// <summary>The cursor's line segments for <paramref name="t"/> under <paramref name="s"/>.</summary>
    public static List<LineSegment> Lines(AlpineTerrain t, TerrainBrushSettings s, TerrainCursorState state)
    {
        var lines = new List<LineSegment>();
        float lift = MathF.Max(0.05f, t.CellSize * 0.05f);
        TerrainMirror? mirror = s.MirrorFor(t);
        if (mirror is { } m)
        {
            AddMirrorLines(lines, t, m, lift);
        }

        if (s.Tool == TerrainTool.GeoableChunks)
        {
            if (t.Geoable)
            {
                AddGeoChunks(lines, t, lift, state);
            }

            return lines;
        }

        for (int image = mirror is not null ? TerrainBrush.MaxImages - 1 : 0; image >= 0; image--)
        {
            if (!TerrainBrush.ImageOn(mirror, image))
            {
                continue;
            }

            uint color = image == 0 ? (state.Stroking ? Red : Yellow) : (state.Stroking ? DimRed : DimYellow);
            if (state.Segment is { } seg && s.Tool == TerrainTool.BridgePoints)
            {
                Vector2 a = MirrorWorld(t, mirror, image, seg.A), b = MirrorWorld(t, mirror, image, seg.B);
                AddCapsule(lines, t, a, b, s.Radius, lift, color);
                if (s.Falloff != TerrainFalloff.Constant)
                {
                    AddCapsule(lines, t, a, b, s.Radius * 0.5f, lift, color);
                }

                if (image == 0)
                {
                    lines.Add(new LineSegment(Draped(t, a, lift), Draped(t, b, lift), Yellow));
                }

                continue;
            }

            if (state.Hover is not { } hover)
            {
                continue;
            }

            Vector2 c = MirrorWorld(t, mirror, image, new Vector2(hover.X, hover.Z));
            AddRing(lines, t, c, s.Radius, lift, color);
            if (!TerrainBrush.EditsHoles(s.Tool) && s.Falloff != TerrainFalloff.Constant)
            {
                AddRing(lines, t, c, s.Radius * 0.5f, lift, color);
            }
        }

        if (state.Hover is { } h)
        {
            float tick = MathF.Max(s.Radius * 0.08f, t.CellSize * 0.25f);
            lines.Add(new LineSegment(h + new Vector3(0, lift, 0), h + new Vector3(0, lift + tick, 0), state.Stroking ? Red : Yellow));
        }

        return lines;
    }

    private static Vector3 Draped(AlpineTerrain t, Vector2 p, float lift) => new(p.X, TerrainOps.HeightAt(t, p.X, p.Y) + lift, p.Y);

    private static void Path(List<LineSegment> lines, AlpineTerrain t, IReadOnlyList<Vector2> pts, float lift, uint color)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            lines.Add(new LineSegment(Draped(t, pts[i], lift), Draped(t, pts[i + 1], lift), color));
        }
    }

    private static void AddRing(List<LineSegment> lines, AlpineTerrain t, Vector2 c, float radius, float lift, uint color)
    {
        int segments = Math.Clamp((int)MathF.Ceiling(6.2831853f * radius / (t.CellSize * 0.5f)), 24, 256);
        var pts = new List<Vector2>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            float a = 6.2831853f * i / segments;
            pts.Add(new Vector2(c.X + (MathF.Cos(a) * radius), c.Y + (MathF.Sin(a) * radius)));
        }

        Path(lines, t, pts, lift, color);
    }

    private static void AddCapsule(List<LineSegment> lines, AlpineTerrain t, Vector2 a, Vector2 b, float radius, float lift, uint color)
    {
        Vector2 d = b - a;
        float length = d.Length();
        if (!(length > 0f))
        {
            AddRing(lines, t, a, radius, lift, color);
            return;
        }

        Vector2 u = d / length, n = new(-u.Y, u.X);
        float piece = t.CellSize * 0.5f;
        int arc = Math.Clamp((int)MathF.Ceiling(3.14159265f * radius / piece), 12, 128);
        int side = Math.Clamp((int)MathF.Ceiling(length / piece), 1, 256);
        var pts = new List<Vector2> { a + (n * radius) };
        for (int end = 0; end < 2; end++)
        {
            float sign = end == 0 ? 1f : -1f;
            Vector2 start = end == 0 ? a : b, cap = end == 0 ? b : a;
            for (int i = 1; i <= side; i++)
            {
                float f = sign * i / side;
                pts.Add(start + (sign * n * radius) + (d * f));
            }

            for (int i = 1; i <= arc; i++)
            {
                float ang = 3.14159265f * i / arc;
                float cc = sign * MathF.Cos(ang) * radius, ss = sign * MathF.Sin(ang) * radius;
                pts.Add(cap + (cc * n) + (ss * u));
            }
        }

        Path(lines, t, pts, lift, color);
    }

    private static Vector2 MirrorWorld(AlpineTerrain t, TerrainMirror? m, int k, Vector2 p)
    {
        if (m is not { } mm)
        {
            return p;
        }

        float cs = t.CellSize;
        return new Vector2(
            (k & 1) != 0 ? (2f * t.Origin.X) + (mm.X2 * cs) - p.X : p.X,
            (k & 2) != 0 ? (2f * t.Origin.Z) + (mm.Z2 * cs) - p.Y : p.Y);
    }

    private static void AddMirrorLines(List<LineSegment> lines, AlpineTerrain t, TerrainMirror m, float lift)
    {
        float ex = AlpineTerrainRules.Extent(t.Nx, t.CellSize), ez = AlpineTerrainRules.Extent(t.Nz, t.CellSize);
        int steps = Math.Max(t.Grid.CellsX, t.Grid.CellsZ) * 2;
        if (m.X)
        {
            float x = t.Origin.X + (m.X2 * 0.5f * t.CellSize);
            Path(lines, t, Enumerable.Range(0, steps + 1).Select(i => new Vector2(x, t.Origin.Z + (ez * i / steps))).ToList(), lift, MirrorX);
        }

        if (m.Z)
        {
            float z = t.Origin.Z + (m.Z2 * 0.5f * t.CellSize);
            Path(lines, t, Enumerable.Range(0, steps + 1).Select(i => new Vector2(t.Origin.X + (ex * i / steps), z)).ToList(), lift, MirrorZ);
        }
    }

    private static void AddGeoChunks(List<LineSegment> lines, AlpineTerrain t, float lift, TerrainCursorState state)
    {
        AlpineTerrainGrid g = t.Grid;
        int edge = Ged.Core.Compiler.TerrainChunkEmitter.EffectiveChunkCells(t);
        int n = AlpineTerrainRules.ChunkCount(g.CellsX, g.CellsZ, edge);
        const float inset = 0.2f;
        int hovered = state.Hover is { } h
            ? TerrainBrush.ChunkAt(g.CellsX, g.CellsZ, edge, (h.X - t.Origin.X) / t.CellSize, (h.Z - t.Origin.Z) / t.CellSize)
            : -1;
        for (int k = 0; k < n; k++)
        {
            Outline(lines, t, AlpineTerrainRules.ChunkRect(g.CellsX, g.CellsZ, edge, k), inset, lift, TerrainStroke.ChunkGeoable(g, k) ? GeoOn : GeoOff);
        }

        if (hovered >= 0)
        {
            Outline(lines, t, AlpineTerrainRules.ChunkRect(g.CellsX, g.CellsZ, edge, hovered), inset * 3f, lift, state.Stroking ? Red : Yellow);
        }
    }

    private static void Outline(List<LineSegment> lines, AlpineTerrain t, TerrainChunkRect r, float inset, float lift, uint color)
    {
        float cs = t.CellSize;
        float x0 = t.Origin.X + (r.X0 * cs) + inset, x1 = t.Origin.X + (r.X1 * cs) - inset;
        float z0 = t.Origin.Z + (r.Z0 * cs) + inset, z1 = t.Origin.Z + (r.Z1 * cs) - inset;
        int steps = Math.Max(1, Math.Max(r.X1 - r.X0, r.Z1 - r.Z0));
        var pts = new List<Vector2>();
        void Edge(Vector2 a, Vector2 b)
        {
            for (int i = 0; i < steps; i++)
            {
                pts.Add(Vector2.Lerp(a, b, (float)i / steps));
            }
        }

        Edge(new Vector2(x0, z0), new Vector2(x1, z0));
        Edge(new Vector2(x1, z0), new Vector2(x1, z1));
        Edge(new Vector2(x1, z1), new Vector2(x0, z1));
        Edge(new Vector2(x0, z1), new Vector2(x0, z0));
        pts.Add(new Vector2(x0, z0));
        Path(lines, t, pts, lift, color);
    }
}
