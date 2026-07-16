using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>Which snap targets are active (composable; B1 magnet split-button).</summary>
[Flags]
public enum SnapKinds
{
    None = 0,
    Grid = 1,
    Vertices = 2,
    Midpoints = 4,
    Faces = 8,

    /// <summary>Any geometry target (vertex / midpoint / face).</summary>
    Geometry = Vertices | Midpoints | Faces,

    /// <summary>The default: grid + vertices on.</summary>
    Default = Grid | Vertices,
}

/// <summary>A resolved snap target: the world point to lock onto and which kind produced it.</summary>
public readonly record struct SnapResult(SnapKinds Kind, Vec3 Position);

/// <summary>A face for face-plane snapping: a world-space convex polygon and its plane.</summary>
public readonly struct SnapFace
{
    public SnapFace(IReadOnlyList<Vec3> polygon, Vec3 normal, float offset)
    {
        Polygon = polygon;
        Normal = normal;
        Offset = offset;
    }

    public IReadOnlyList<Vec3> Polygon { get; }

    public Vec3 Normal { get; }

    public float Offset { get; }
}

/// <summary>
/// A spatial hash of a level's snap candidates (compiled + brush vertices, edge
/// midpoints, and face planes) for B1 snap-to-geometry. Built once per build /
/// edit-invalidate; queried each drag frame for the nearest target to the manipulated
/// point within a small world radius (derived from ~8&#160;screen&#160;px by the caller).
/// Priority: vertex &gt; midpoint &gt; face (grid is the caller's fallback when none hit).
/// </summary>
public sealed class GeometrySnapIndex
{
    private readonly float _cell;
    private readonly Dictionary<(int, int, int), List<Point>> _points = new();
    private readonly Dictionary<(int, int, int), List<int>> _faceCells = new();
    private readonly List<SnapFace> _faces;

    private readonly record struct Point(Vec3 Pos, SnapKinds Kind);

    private GeometrySnapIndex(float cell, List<SnapFace> faces)
    {
        _cell = cell;
        _faces = faces;
    }

    /// <summary>Number of indexed point candidates (vertices + midpoints), for tests/diagnostics.</summary>
    public int PointCount { get; private set; }

    /// <summary>Number of indexed faces.</summary>
    public int FaceCount => _faces.Count;

    /// <summary>
    /// Builds the index from world-space vertices, edges (index pairs into
    /// <paramref name="vertices"/>) and faces. <paramref name="cellSize"/> is the hash
    /// cell edge; it should be ≥ the largest query radius used (queries scan the cells a
    /// radius box overlaps). Duplicate vertex positions are de-duplicated.
    /// </summary>
    public static GeometrySnapIndex Build(
        IReadOnlyList<Vec3> vertices,
        IEnumerable<(int A, int B)> edges,
        IReadOnlyList<SnapFace> faces,
        float cellSize = 2f)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(faces);
        float cell = cellSize > 1e-3f ? cellSize : 2f;
        var index = new GeometrySnapIndex(cell, new List<SnapFace>(faces));

        var seen = new HashSet<(int, int, int)>();
        for (int i = 0; i < vertices.Count; i++)
        {
            index.AddVertex(vertices[i], seen);
        }

        var midSeen = new HashSet<(int, int, int)>();
        foreach ((int a, int b) in edges)
        {
            if (a < 0 || b < 0 || a >= vertices.Count || b >= vertices.Count || a == b)
            {
                continue;
            }

            Vec3 mid = vertices[a].Add(vertices[b]).Scale(0.5f);
            if (midSeen.Add(Quantize(mid)))
            {
                index.AddPoint(mid, SnapKinds.Midpoints);
            }
        }

        for (int fi = 0; fi < index._faces.Count; fi++)
        {
            index.IndexFace(fi);
        }

        return index;
    }

    /// <summary>
    /// The nearest enabled snap target to <paramref name="query"/> within
    /// <paramref name="radius"/>, or null when none is in range. Vertices win over
    /// midpoints, midpoints over faces (a vertex within radius always beats a nearer
    /// face). Only kinds set in <paramref name="enabled"/> are considered.
    /// </summary>
    public SnapResult? Query(Vec3 query, float radius, SnapKinds enabled)
    {
        if (radius <= 0f)
        {
            return null;
        }

        float r2 = radius * radius;

        SnapResult? Nearest(SnapKinds kind)
        {
            if ((enabled & kind) == 0)
            {
                return null;
            }

            float best = r2;
            Vec3 bestPos = default;
            bool found = false;
            foreach (Point p in PointsNear(query, radius))
            {
                if (p.Kind != kind)
                {
                    continue;
                }

                float d2 = p.Pos.Sub(query).LengthSquared();
                if (d2 <= best)
                {
                    best = d2;
                    bestPos = p.Pos;
                    found = true;
                }
            }

            return found ? new SnapResult(kind, bestPos) : null;
        }

        // Priority: vertex > midpoint > face.
        if (Nearest(SnapKinds.Vertices) is { } v)
        {
            return v;
        }

        if (Nearest(SnapKinds.Midpoints) is { } m)
        {
            return m;
        }

        if ((enabled & SnapKinds.Faces) != 0 && NearestFace(query, radius) is { } f)
        {
            return f;
        }

        return null;
    }

    private SnapResult? NearestFace(Vec3 query, float radius)
    {
        float best = radius;
        Vec3 bestPos = default;
        bool found = false;
        var tested = new HashSet<int>();
        foreach (int fi in FacesNear(query, radius))
        {
            if (!tested.Add(fi))
            {
                continue;
            }

            SnapFace face = _faces[fi];
            float signed = face.Normal.Dot(query) + face.Offset;
            float dist = MathF.Abs(signed);
            if (dist > best)
            {
                continue;
            }

            Vec3 proj = query.Sub(face.Normal.Scale(signed));
            if (InsidePolygon(face, proj) && dist <= best)
            {
                best = dist;
                bestPos = proj;
                found = true;
            }
        }

        return found ? new SnapResult(SnapKinds.Faces, bestPos) : null;
    }

    private static bool InsidePolygon(SnapFace face, Vec3 p)
    {
        IReadOnlyList<Vec3> poly = face.Polygon;
        int n = poly.Count;
        if (n < 3)
        {
            return false;
        }

        const float eps = -1e-3f;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = poly[i];
            Vec3 b = poly[(i + 1) % n];
            Vec3 edge = b.Sub(a);
            Vec3 toP = p.Sub(a);
            // Inside a convex polygon: (edge × toP) agrees with the outward normal.
            if (edge.Cross(toP).Dot(face.Normal) < eps)
            {
                return false;
            }
        }

        return true;
    }

    private void AddPoint(Vec3 p, SnapKinds kind)
    {
        (int, int, int) cell = CellOf(p);
        if (!_points.TryGetValue(cell, out List<Point>? list))
        {
            list = new List<Point>();
            _points[cell] = list;
        }

        list.Add(new Point(p, kind));
        PointCount++;
    }

    /// <summary>Adds a vertex, de-duplicated by ~1&#160;mm-quantized position so a shared corner is stored once.</summary>
    private void AddVertex(Vec3 p, HashSet<(int, int, int)> seen)
    {
        if (seen.Add(Quantize(p)))
        {
            AddPoint(p, SnapKinds.Vertices);
        }
    }

    private static (int, int, int) Quantize(Vec3 p) =>
        ((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f));

    private void IndexFace(int fi)
    {
        SnapFace face = _faces[fi];
        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in face.Polygon)
        {
            min = Vec3Math.Min(min, v);
            max = Vec3Math.Max(max, v);
        }

        (int x0, int y0, int z0) = CellOf(min);
        (int x1, int y1, int z1) = CellOf(max);
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    var key = (x, y, z);
                    if (!_faceCells.TryGetValue(key, out List<int>? list))
                    {
                        list = new List<int>();
                        _faceCells[key] = list;
                    }

                    list.Add(fi);
                }
            }
        }
    }

    private IEnumerable<Point> PointsNear(Vec3 query, float radius)
    {
        (int x0, int y0, int z0) = CellOf(new Vec3(query.X - radius, query.Y - radius, query.Z - radius));
        (int x1, int y1, int z1) = CellOf(new Vec3(query.X + radius, query.Y + radius, query.Z + radius));
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (_points.TryGetValue((x, y, z), out List<Point>? list))
                    {
                        foreach (Point p in list)
                        {
                            yield return p;
                        }
                    }
                }
            }
        }
    }

    private IEnumerable<int> FacesNear(Vec3 query, float radius)
    {
        (int x0, int y0, int z0) = CellOf(new Vec3(query.X - radius, query.Y - radius, query.Z - radius));
        (int x1, int y1, int z1) = CellOf(new Vec3(query.X + radius, query.Y + radius, query.Z + radius));
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (_faceCells.TryGetValue((x, y, z), out List<int>? list))
                    {
                        foreach (int fi in list)
                        {
                            yield return fi;
                        }
                    }
                }
            }
        }
    }

    private (int, int, int) CellOf(Vec3 p) =>
        ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));
}
