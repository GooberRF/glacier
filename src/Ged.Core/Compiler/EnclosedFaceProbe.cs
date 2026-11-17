using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Tells whether a detail face can be seen at all: its front (a sample a little in front of every corner, edge and
/// fan triangle) lies in solid by the build's own fold — the time-ordered structural brushes (the world starts solid,
/// air carves, solid fills), then the air portal brushes, then the opaque detail solids filling what is left. A face
/// whose whole front is solid is enclosed: nobody can stand where it is seen from. Organic rock made of overlapping
/// convex chunks is mostly such faces (the sides, tops and backs of chunks inside their neighbours or the wall).
/// <see cref="SurfaceBuilder"/> asks it only when a level's lit faces would need more lightmap surfaces than a level
/// holds, so enclosed faces give theirs up before visible ones lose theirs; levels under the cap (every stock level)
/// are built exactly as RED builds them.
/// </summary>
internal sealed class EnclosedFaceProbe
{
    private const float Cell = 8f;
    private const float Nudge = 0.02f;

    private readonly List<(BrushVolume V, bool Air)> _structural = new();
    private readonly List<BrushVolume> _portals = new();
    private readonly List<BrushVolume> _details = new();
    private readonly Dictionary<long, List<int>> _sGrid = new(), _pGrid = new(), _dGrid = new();

    /// <param name="brushes">The static brushes in time order (movers already left out).</param>
    /// <param name="seeThrough">Detail brushes that do not hide what is behind them (an alpha, holed or invisible face).</param>
    /// <param name="skip">Brushes the build leaves out of the fold (terrain chunks).</param>
    internal EnclosedFaceProbe(IReadOnlyList<Brush> brushes, IReadOnlySet<int> seeThrough, IReadOnlySet<int>? skip)
    {
        foreach (Brush b in brushes)
        {
            if (skip is not null && skip.Contains(b.Uid))
            {
                continue;
            }

            var flags = (BrushFlags)b.Flags;
            bool air = (flags & BrushFlags.Air) != 0;
            List<CsgFace> faces = BrushWorld.ToOutwardWorldFaces(b);
            if (faces.Count < 4)
            {
                continue;
            }

            BrushVolume v = BrushVolume.From(0, air, faces);
            if ((flags & BrushFlags.Portal) != 0)
            {
                if (air)
                {
                    Add(_pGrid, _portals.Count, v);
                    _portals.Add(v);
                }
            }
            else if ((flags & (BrushFlags.Detail | BrushFlags.Geoable)) != 0)
            {
                if (!air && !seeThrough.Contains(b.Uid))
                {
                    Add(_dGrid, _details.Count, v);
                    _details.Add(v);
                }
            }
            else
            {
                Add(_sGrid, _structural.Count, v);
                _structural.Add((v, air));
            }
        }
    }

    private static long CellOf(float v) => (long)Math.Clamp(Math.Floor(v / Cell), -1_000_000d, 1_000_000d);

    private static long Key(long cx, long cz) => ((cx + 1_048_576L) << 22) | (cz + 1_048_576L);

    private static void Add(Dictionary<long, List<int>> grid, int index, BrushVolume v)
    {
        if (!(v.Min.X <= v.Max.X && v.Min.Z <= v.Max.Z))
        {
            return;
        }

        for (long cx = CellOf(v.Min.X); cx <= CellOf(v.Max.X); cx++)
        {
            for (long cz = CellOf(v.Min.Z); cz <= CellOf(v.Max.Z); cz++)
            {
                long k = Key(cx, cz);
                if (!grid.TryGetValue(k, out List<int>? l))
                {
                    l = new List<int>(4);
                    grid[k] = l;
                }

                l.Add(index);
            }
        }
    }

    private static List<int>? At(Dictionary<long, List<int>> grid, Vec3 p) =>
        grid.TryGetValue(Key(CellOf(p.X), CellOf(p.Z)), out List<int>? l) ? l : null;

    /// <summary>True when the point is in solid by the fold (indices in a cell list are in time order).</summary>
    internal bool Solid(Vec3 p)
    {
        bool solid = true;
        if (At(_sGrid, p) is { } s)
        {
            foreach (int i in s)
            {
                if (_structural[i].V.Contains(p))
                {
                    solid = !_structural[i].Air;
                }
            }
        }

        if (solid && At(_pGrid, p) is { } q)
        {
            foreach (int i in q)
            {
                if (_portals[i].Contains(p))
                {
                    solid = false;
                    break;
                }
            }
        }

        if (!solid && At(_dGrid, p) is { } d)
        {
            foreach (int i in d)
            {
                if (_details[i].Contains(p))
                {
                    return true;
                }
            }
        }

        return solid;
    }

    /// <summary>True when the whole front of the face lies in solid: no one can see it.</summary>
    internal bool Enclosed(CsgFace f)
    {
        int n = f.Vertices.Count;
        if (n < 3)
        {
            return false;
        }

        Vec3 normal = f.Plane.Normal;
        Vec3 c = default;
        foreach (CsgVertex v in f.Vertices)
        {
            c = c.Add(v.Position);
        }

        c = c.Scale(1f / n);
        Vec3 off = normal.Scale(Nudge);
        bool Front(Vec3 p) => Solid(p.Add(off));
        Vec3 Toward(Vec3 p, float t) => p.Add(c.Sub(p).Scale(t));

        if (!Front(c))
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            Vec3 a = f.Vertices[i].Position, b = f.Vertices[(i + 1) % n].Position;
            Vec3 mid = a.Add(b).Scale(0.5f);
            if (!Front(Toward(a, 0.03f)) || !Front(Toward(mid, 0.03f)) || !Front(c.Add(a).Add(b).Scale(1f / 3f)))
            {
                return false;
            }
        }

        return true;
    }
}
