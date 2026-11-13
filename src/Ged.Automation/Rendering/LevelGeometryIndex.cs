using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Automation.Rendering;

/// <summary>
/// CPU-side queries over a level's compiled geometry for the render command: world bounds, the floor
/// under a point (for the plan's default height and per-object storey membership), the wall
/// segments where the compiled faces cross a horizontal cut plane (the plan's cut edges), and a
/// vertical ray probe that tells air from solid (the plan's "open to below" areas). Sky-room faces
/// (the level's skybox, usually far from the playable space) are left out of all of these. Read-only.
/// </summary>
internal sealed class LevelGeometryIndex
{
    /// <summary>A face counts as floor when its normal is within ~45 degrees of straight up.</summary>
    private const float FloorNormalY = 0.7f;

    private readonly List<FloorPoly> _floors = new();
    private readonly List<Vector3[]> _staticPolys = new();
    private readonly List<Vector3[]> _moverPolys = new();
    private readonly List<RayPoly> _horizontal = new();
    private readonly List<Vector3[]> _skyPolys = new();
    private Dictionary<long, List<int>>? _grid;
    private float _cell = 1f;

    public LevelGeometryIndex(Geometry? geometry, IReadOnlyList<Brush>? movers)
    {
        if (geometry is not null)
        {
            foreach (Face f in geometry.Faces)
            {
                if (f.IsPortalFace || f.Vertices.Count < 3)
                {
                    continue;
                }

                if ((uint)f.RoomIndex < (uint)geometry.Rooms.Count && geometry.Rooms[f.RoomIndex].IsSkyroom != 0)
                {
                    SkyFaces++;
                    continue;
                }

                var poly = new Vector3[f.Vertices.Count];
                bool ok = true;
                for (int i = 0; i < poly.Length; i++)
                {
                    int vi = f.Vertices[i].Index;
                    if ((uint)vi >= (uint)geometry.Vertices.Count)
                    {
                        ok = false;
                        break;
                    }

                    Vec3 v = geometry.Vertices[vi];
                    poly[i] = new Vector3(v.X, v.Y, v.Z);
                }

                if (!ok)
                {
                    continue;
                }

                _staticPolys.Add(poly);

                // show_sky and invisible faces are not drawn in views from outside the level, so they do not
                // frame it (a level enclosed in a sky or invisible shell would otherwise frame on the shell).
                if ((f.Flags & (ushort)(FaceFlags.ShowSky | FaceFlags.IsInvisible)) == 0)
                {
                    Grow(poly);
                }
                else
                {
                    _skyPolys.Add(poly);
                }

                bool liquid = (f.Flags & (ushort)FaceFlags.LiquidSurface) != 0;
                Vec3 n = f.Plane.Normal;
                if (!liquid && n.Y >= FloorNormalY)
                {
                    _floors.Add(new FloorPoly(poly, new Vector3(n.X, n.Y, n.Z)));
                }

                if (MathF.Abs(n.Y) >= 0.05f)
                {
                    _horizontal.Add(new RayPoly(new FloorPoly(poly, new Vector3(n.X, n.Y, n.Z)), n.Y > 0f));
                }
            }
        }

        if (Min.X > Max.X)
        {
            // Nothing but sky faces: frame on those.
            foreach (Vector3[] poly in _skyPolys)
            {
                Grow(poly);
            }
        }

        if (movers is not null)
        {
            foreach (Brush b in movers)
            {
                foreach (Face f in b.Geometry.Faces)
                {
                    if (f.Vertices.Count < 3)
                    {
                        continue;
                    }

                    var poly = new Vector3[f.Vertices.Count];
                    bool ok = true;
                    for (int i = 0; i < poly.Length; i++)
                    {
                        int vi = f.Vertices[i].Index;
                        if ((uint)vi >= (uint)b.Geometry.Vertices.Count)
                        {
                            ok = false;
                            break;
                        }

                        Vec3 w = BrushTransform.WorldVertex(b, b.Geometry.Vertices[vi]);
                        poly[i] = new Vector3(w.X, w.Y, w.Z);
                    }

                    if (ok)
                    {
                        _moverPolys.Add(poly);
                        Grow(poly);
                    }
                }
            }
        }
    }

    /// <summary>True when there is any compiled (static or mover) geometry.</summary>
    public bool HasGeometry => _staticPolys.Count > 0 || _moverPolys.Count > 0;

    /// <summary>The geometry bounds (meaningful only when <see cref="HasGeometry"/>).</summary>
    public Vector3 Min { get; private set; } = new(float.MaxValue);

    /// <summary>The geometry bounds (meaningful only when <see cref="HasGeometry"/>).</summary>
    public Vector3 Max { get; private set; } = new(float.MinValue);

    /// <summary>The number of floor-facing faces.</summary>
    public int FloorCount => _floors.Count;

    /// <summary>
    /// The drawn polygons (static and mover faces; sky-room, show-sky and invisible faces left out) that reach into the
    /// height band [<paramref name="bottom"/>, <paramref name="top"/>] (every polygon without a band).
    /// </summary>
    public IEnumerable<Vector3[]> Polygons(float bottom = float.MinValue, float top = float.MaxValue)
    {
        var sky = new HashSet<Vector3[]>(_skyPolys);
        foreach (Vector3[] poly in _staticPolys.Concat(_moverPolys))
        {
            if (sky.Contains(poly))
            {
                continue;
            }

            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (Vector3 v in poly)
            {
                minY = MathF.Min(minY, v.Y);
                maxY = MathF.Max(maxY, v.Y);
            }

            if (maxY >= bottom && minY <= top)
            {
                yield return poly;
            }
        }
    }

    /// <summary>Faces left out because they belong to a sky room.</summary>
    public int SkyFaces { get; private set; }

    /// <summary>
    /// The bounds of the geometry that reaches into the height band [<paramref name="bottom"/>,
    /// <paramref name="top"/>] (a plan slice's content), or null when nothing does.
    /// </summary>
    public (Vector3 Min, Vector3 Max)? BoundsInBand(float bottom, float top)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        bool any = false;
        var sky = new HashSet<Vector3[]>(_skyPolys);
        foreach (Vector3[] poly in _staticPolys.Concat(_moverPolys))
        {
            if (sky.Contains(poly))
            {
                continue;
            }

            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (Vector3 v in poly)
            {
                minY = MathF.Min(minY, v.Y);
                maxY = MathF.Max(maxY, v.Y);
            }

            if (maxY < bottom || minY > top)
            {
                continue;
            }

            foreach (Vector3 v in poly)
            {
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
            }

            any = true;
        }

        return any ? (lo, hi) : null;
    }

    /// <summary>
    /// Probes straight down from (<paramref name="x"/>, <paramref name="top"/>, <paramref name="z"/>):
    /// true when the first compiled face below faces up (the probe starts in air — e.g. over an
    /// opening to a lower floor), false when it faces down (the probe starts inside solid: the back of
    /// a ceiling below) or nothing is below (outside the level).
    /// </summary>
    public bool IsAirBelow(float x, float z, float top)
    {
        _grid ??= BuildGrid();
        if (!_grid.TryGetValue(Key(x, z), out List<int>? cell))
        {
            return false;
        }

        float bestY = float.MinValue;
        bool up = false;
        foreach (int i in cell)
        {
            RayPoly rp = _horizontal[i];
            FloorPoly f = rp.Poly;
            if (x < f.MinX || x > f.MaxX || z < f.MinZ || z > f.MaxZ || f.MinY > top || !f.ContainsXz(x, z))
            {
                continue;
            }

            float h = f.HeightAt(x, z);
            if (h <= top && h > bestY)
            {
                bestY = h;
                up = rp.FacesUp;
            }
        }

        return bestY > float.MinValue && up;
    }

    private Dictionary<long, List<int>> BuildGrid()
    {
        var grid = new Dictionary<long, List<int>>();
        if (_horizontal.Count == 0)
        {
            return grid;
        }

        float minX = _horizontal.Min(p => p.Poly.MinX), maxX = _horizontal.Max(p => p.Poly.MaxX);
        float minZ = _horizontal.Min(p => p.Poly.MinZ), maxZ = _horizontal.Max(p => p.Poly.MaxZ);
        _cell = MathF.Max(0.5f, MathF.Max(maxX - minX, maxZ - minZ) / 256f);
        for (int i = 0; i < _horizontal.Count; i++)
        {
            FloorPoly f = _horizontal[i].Poly;
            int x0 = Cell(f.MinX), x1 = Cell(f.MaxX), z0 = Cell(f.MinZ), z1 = Cell(f.MaxZ);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    long key = ((long)cx << 32) ^ (uint)cz;
                    if (!grid.TryGetValue(key, out List<int>? list))
                    {
                        grid[key] = list = new List<int>();
                    }

                    list.Add(i);
                }
            }
        }

        return grid;
    }

    private int Cell(float v) => (int)MathF.Floor(v / _cell);

    private long Key(float x, float z) => ((long)Cell(x) << 32) ^ (uint)Cell(z);

    /// <summary>
    /// The height of the highest floor face directly below (or within <paramref name="tolerance"/>
    /// above) <paramref name="p"/>, or null when no floor face covers its X/Z.
    /// </summary>
    public float? FloorBelow(Vector3 p, float tolerance = 0.25f)
    {
        float? best = null;
        foreach (FloorPoly f in _floors)
        {
            if (p.X < f.MinX || p.X > f.MaxX || p.Z < f.MinZ || p.Z > f.MaxZ || f.MinY > p.Y + tolerance)
            {
                continue;
            }

            if (!f.ContainsXz(p.X, p.Z))
            {
                continue;
            }

            float h = f.HeightAt(p.X, p.Z);
            if (h <= p.Y + tolerance && (best is null || h > best.Value))
            {
                best = h;
            }
        }

        return best;
    }

    /// <summary>The lowest floor height in the level, or null when there are no floor faces.</summary>
    public float? LowestFloor() => _floors.Count == 0 ? null : _floors.Min(f => f.MinY);

    /// <summary>
    /// The segments where faces cross the horizontal plane y = <paramref name="cut"/>: static geometry
    /// and (separately) movers at their keyframe-0 pose.
    /// </summary>
    public (List<(Vector3 A, Vector3 B)> Static, List<(Vector3 A, Vector3 B)> Movers) CutSegments(float cut) =>
        (Slice(_staticPolys, cut), Slice(_moverPolys, cut));

    private static List<(Vector3, Vector3)> Slice(List<Vector3[]> polys, float cut)
    {
        var segments = new List<(Vector3, Vector3)>();
        var hits = new List<Vector3>(8);
        foreach (Vector3[] poly in polys)
        {
            hits.Clear();
            for (int i = 0; i < poly.Length; i++)
            {
                Vector3 a = poly[i], b = poly[(i + 1) % poly.Length];
                bool aAbove = a.Y >= cut, bAbove = b.Y >= cut;
                if (aAbove == bAbove)
                {
                    continue;
                }

                float t = (cut - a.Y) / (b.Y - a.Y);
                hits.Add(a + ((b - a) * t));
            }

            if (hits.Count < 2)
            {
                continue;
            }

            if (hits.Count > 2)
            {
                // A non-convex polygon: pair the crossings in order along their spread.
                Vector3 dir = hits[^1] - hits[0];
                hits.Sort((p, q) => Vector3.Dot(p, dir).CompareTo(Vector3.Dot(q, dir)));
            }

            for (int i = 0; i + 1 < hits.Count; i += 2)
            {
                if (Vector3.DistanceSquared(hits[i], hits[i + 1]) > 1e-8f)
                {
                    segments.Add((hits[i], hits[i + 1]));
                }
            }
        }

        return segments;
    }

    private void Grow(Vector3[] poly)
    {
        foreach (Vector3 v in poly)
        {
            Min = Vector3.Min(Min, v);
            Max = Vector3.Max(Max, v);
        }
    }

    private readonly record struct RayPoly(FloorPoly Poly, bool FacesUp);

    private sealed class FloorPoly
    {
        private readonly Vector3[] _p;
        private readonly Vector3 _n;

        public FloorPoly(Vector3[] p, Vector3 n)
        {
            _p = p;
            _n = n;
            MinX = p.Min(v => v.X);
            MaxX = p.Max(v => v.X);
            MinZ = p.Min(v => v.Z);
            MaxZ = p.Max(v => v.Z);
            MinY = p.Min(v => v.Y);
        }

        public float MinX { get; }

        public float MaxX { get; }

        public float MinZ { get; }

        public float MaxZ { get; }

        public float MinY { get; }

        public bool ContainsXz(float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = _p.Length - 1; i < _p.Length; j = i++)
            {
                Vector3 a = _p[i], b = _p[j];
                if ((a.Z > z) != (b.Z > z) && x < ((b.X - a.X) * (z - a.Z) / (b.Z - a.Z)) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public float HeightAt(float x, float z)
        {
            Vector3 v0 = _p[0];
            return v0.Y - (((_n.X * (x - v0.X)) + (_n.Z * (z - v0.Z))) / _n.Y);
        }
    }
}
