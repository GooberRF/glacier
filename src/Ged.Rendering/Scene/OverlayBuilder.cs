using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Model;

namespace Ged.Rendering.Scene;

/// <summary>
/// Builds viewport overlay line sets for the editing systems: mover path
/// splines + a time-scrubbed ghost of the mover geometry, cutscene path polylines
/// + camera frustum cones, nav-point radius discs and decal boxes. All output is
/// world-space <see cref="LineSegment"/>s the viewport (and offscreen renderer)
/// draws over the scene. Pure — no GPU dependency, fully testable.
/// </summary>
public static class OverlayBuilder
{
    private static readonly uint PathColor = Palette.Rgba(80, 200, 255);
    private static readonly uint GhostColor = Palette.Rgba(120, 255, 140);
    private static readonly uint StartColor = Palette.Rgba(255, 215, 0);
    private static readonly uint NodeColor = Palette.Rgba(230, 230, 230);
    private static readonly uint ConeColor = Palette.Rgba(255, 120, 200);
    private static readonly uint DiscColor = Palette.Rgba(100, 220, 120);
    private static readonly uint DecalColor = Palette.Rgba(255, 180, 60);
    private static readonly uint EventArrowColor = Palette.Rgba(255, 110, 40);

    /// <summary>
    /// The Catmull-Rom spline through the keyframe/node positions plus a cross
    /// marker at each (gold at <paramref name="startIndex"/>). Two or fewer points
    /// degrade to a straight segment.
    /// </summary>
    public static List<LineSegment> Path(IReadOnlyList<Vec3> points, int startIndex = 0, uint? color = null)
    {
        var lines = new List<LineSegment>();
        if (points.Count == 0)
        {
            return lines;
        }

        uint c = color ?? PathColor;
        if (points.Count >= 2)
        {
            const int samplesPerSegment = 12;
            Vector3 prev = V(points[0]);
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 p0 = V(points[Math.Max(0, i - 1)]);
                Vector3 p1 = V(points[i]);
                Vector3 p2 = V(points[i + 1]);
                Vector3 p3 = V(points[Math.Min(points.Count - 1, i + 2)]);
                for (int s = 1; s <= samplesPerSegment; s++)
                {
                    float t = (float)s / samplesPerSegment;
                    Vector3 pt = CatmullRom(p0, p1, p2, p3, t);
                    lines.Add(new LineSegment(prev, pt, c));
                    prev = pt;
                }
            }
        }

        for (int i = 0; i < points.Count; i++)
        {
            AddCross(lines, V(points[i]), 0.5f, i == startIndex ? StartColor : NodeColor);
        }

        return lines;
    }

    /// <summary>
    /// A wireframe ghost of the mover brushes at a point along their travel: every
    /// brush edge translated by <paramref name="sampled"/> − <paramref name="start"/>.
    /// </summary>
    public static List<LineSegment> MoverGhost(IEnumerable<Brush> moverBrushes, Vec3 start, Vec3 sampled)
    {
        var lines = new List<LineSegment>();
        Vector3 delta = V(sampled) - V(start);
        foreach (Brush b in moverBrushes)
        {
            AddBrushEdges(lines, b, delta, GhostColor);
        }

        return lines;
    }

    /// <summary>Samples a Catmull-Rom position at parameter <paramref name="t"/> in [0,1] over the whole path.</summary>
    public static Vec3 SamplePath(IReadOnlyList<Vec3> points, float t)
    {
        if (points.Count == 0)
        {
            return default;
        }

        if (points.Count == 1)
        {
            return points[0];
        }

        t = Math.Clamp(t, 0f, 1f);
        float scaled = t * (points.Count - 1);
        int i = Math.Min((int)scaled, points.Count - 2);
        float local = scaled - i;
        Vector3 p0 = V(points[Math.Max(0, i - 1)]);
        Vector3 p1 = V(points[i]);
        Vector3 p2 = V(points[i + 1]);
        Vector3 p3 = V(points[Math.Min(points.Count - 1, i + 2)]);
        Vector3 r = CatmullRom(p0, p1, p2, p3, local);
        return new Vec3(r.X, r.Y, r.Z);
    }

    /// <summary>A camera frustum cone glyph: apex at <paramref name="pos"/>, opening along the frame forward row.</summary>
    public static List<LineSegment> CameraCone(Vec3 pos, Mat3 rot, float fovDegrees = 45f, float length = 3f)
    {
        var lines = new List<LineSegment>();
        Vector3 apex = V(pos);
        Vector3 fwd = Vector3.Normalize(V(rot.Forward));
        Vector3 right = Vector3.Normalize(V(rot.Right));
        Vector3 up = Vector3.Normalize(V(rot.Up));
        if (fwd.LengthSquared() < 0.5f)
        {
            fwd = Vector3.UnitZ;
            right = Vector3.UnitX;
            up = Vector3.UnitY;
        }

        float half = MathF.Tan(fovDegrees * MathF.PI / 180f * 0.5f) * length;
        Vector3 center = apex + (fwd * length);
        Vector3[] corners =
        {
            center + (right * half) + (up * half),
            center - (right * half) + (up * half),
            center - (right * half) - (up * half),
            center + (right * half) - (up * half),
        };

        for (int i = 0; i < 4; i++)
        {
            lines.Add(new LineSegment(apex, corners[i], ConeColor));
            lines.Add(new LineSegment(corners[i], corners[(i + 1) % 4], ConeColor));
        }

        return lines;
    }

    /// <summary>A horizontal radius disc (nav-point coverage / light range flat ring).</summary>
    public static List<LineSegment> Disc(Vec3 center, float radius, int segments = 24, uint? color = null)
    {
        var lines = new List<LineSegment>();
        if (radius <= 0f)
        {
            return lines;
        }

        uint c = color ?? DiscColor;
        Vector3 o = V(center);
        Vector3 Prev(int k)
        {
            float a = k / (float)segments * MathF.Tau;
            return o + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
        }

        for (int k = 0; k < segments; k++)
        {
            lines.Add(new LineSegment(Prev(k), Prev(k + 1), c));
        }

        return lines;
    }

    /// <summary>An oriented box outline (decal projection volume).</summary>
    public static List<LineSegment> Box(Vec3 center, Mat3 rot, Vec3 extents, uint? color = null)
    {
        var lines = new List<LineSegment>();
        uint c = color ?? DecalColor;
        Vector3 o = V(center);
        Vector3 rx = V(rot.Right) * extents.X * 0.5f;
        Vector3 ry = V(rot.Up) * extents.Y * 0.5f;
        Vector3 rz = V(rot.Forward) * extents.Z * 0.5f;
        var corner = new Vector3[8];
        int idx = 0;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    corner[idx++] = o + (rx * x) + (ry * y) + (rz * z);
                }
            }
        }

        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 },
        };
        for (int e = 0; e < 12; e++)
        {
            lines.Add(new LineSegment(corner[edges[e, 0]], corner[edges[e, 1]], c));
        }

        return lines;
    }

    /// <summary>
    /// The facing indicator for a directional event (Teleport / Play_Vclip / Clone_Entity /
    /// Anchor_Marker_Orient / …): a shaft from the event position along its orientation's
    /// forward vector, capped with the shared <see cref="AddArrowHead"/> so it reads exactly
    /// like the nav-point and link arrows. <paramref name="length"/> is a fixed world size
    /// (keyed to the glyph scale), matching Alpine RED's fixed-size 3D event arrow
    /// (editor_patch/event.cpp:1249-1263). Returns an empty list for a zero-length arrow or
    /// a degenerate forward vector.
    /// </summary>
    public static List<LineSegment> EventFacingArrow(Vec3 position, Mat3 rotation, float length, uint? color = null)
        => EventFacingArrow(position, rotation.Forward, length, color);

    /// <summary>
    /// Facing arrow drawn along an explicit world <paramref name="direction"/> rather than an
    /// orientation's forward row. Used for object types whose meaningful projection axis is not
    /// the forward vector: an Alpine corona stores its cone/aim direction in the orientation's
    /// UP row (the forward/right rows carry the sprite's arbitrary in-plane spin), so drawing a
    /// corona arrow along forward reads sideways. Returns an empty list for a zero-length arrow
    /// or a degenerate direction vector.
    /// </summary>
    public static List<LineSegment> EventFacingArrow(Vec3 position, Vec3 direction, float length, uint? color = null)
    {
        var lines = new List<LineSegment>();
        if (length <= 1e-4f)
        {
            return lines;
        }

        Vector3 dir = V(direction);
        if (dir.LengthSquared() < 1e-8f)
        {
            return lines;
        }

        dir = Vector3.Normalize(dir);
        uint c = color ?? EventArrowColor;
        Vector3 from = V(position);
        Vector3 to = from + (dir * length);
        lines.Add(new LineSegment(from, to, c));
        AddArrowHead(lines, from, to, c);
        return lines;
    }

    private static void AddBrushEdges(List<LineSegment> lines, Brush b, Vector3 offset, uint color)
    {
        var seen = new HashSet<(int, int)>();
        foreach (Face f in b.Geometry.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int c = f.Vertices[(i + 1) % n].Index;
                var key = a < c ? (a, c) : (c, a);
                if (!seen.Add(key))
                {
                    continue;
                }

                Vector3 pa = World(b, b.Geometry.Vertices[a]) + offset;
                Vector3 pb = World(b, b.Geometry.Vertices[c]) + offset;
                lines.Add(new LineSegment(pa, pb, color));
            }
        }
    }

    private static Vector3 World(Brush b, Vec3 local)
    {
        Vec3 w = b.Position.Add(b.Rotation.Transform(local));
        return new Vector3(w.X, w.Y, w.Z);
    }

    /// <summary>
    /// Appends the two short angled segments of an arrowhead at the destination end of a
    /// link line. The tip sits ~85% along the edge (the established viewport idiom, matching
    /// the directional nav-point connection arrow) with world-space wings, so a link's
    /// destination handle is unambiguous. Shared by the baked link channel (SceneBuilder)
    /// and the selection-overlay link channel.
    /// </summary>
    public static void AddArrowHead(List<LineSegment> lines, Vector3 from, Vector3 to, uint color)
    {
        Vector3 dir = to - from;
        float len = dir.Length();
        if (len < 1e-3f)
        {
            return;
        }

        dir /= len;
        Vector3 up = MathF.Abs(dir.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 side = Vector3.Normalize(Vector3.Cross(dir, up));
        Vector3 tip = from + (dir * (len * 0.85f)); // near the destination end
        float wing = MathF.Min(0.4f, len * 0.15f);
        lines.Add(new LineSegment(tip, tip - (dir * wing) + (side * wing), color));
        lines.Add(new LineSegment(tip, tip - (dir * wing) - (side * wing), color));
    }

    private static void AddCross(List<LineSegment> lines, Vector3 c, float r, uint color)
    {
        lines.Add(new LineSegment(c - new Vector3(r, 0, 0), c + new Vector3(r, 0, 0), color));
        lines.Add(new LineSegment(c - new Vector3(0, r, 0), c + new Vector3(0, r, 0), color));
        lines.Add(new LineSegment(c - new Vector3(0, 0, r), c + new Vector3(0, 0, r), color));
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            ((-p0 + p2) * t) +
            (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2) +
            ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
    }

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}
