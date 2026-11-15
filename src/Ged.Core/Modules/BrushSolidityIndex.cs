using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>
/// Point solidity over a level's brushes with RED's time-order CSG rule: the world starts solid, brushes apply in
/// list (time) order, and a point is solid iff the LAST brush containing it is solid — or no brush contains it.
/// Portal brushes count as air; mover brushes are excluded entirely; a brush with fewer than four faces (a flat
/// "plane" brush such as a grate) contains nothing. Containment is exact per brush (half-spaces for a convex brush,
/// ray parity for any other closed polyhedron), prefiltered by a uniform grid over the brushes' bounds.
/// </summary>
public sealed class BrushSolidityIndex
{
    private const int MaxCellsPerAxis = 96;
    private const int MaxCellsPerBrush = 4096;

    private readonly Entry[] _entries;
    private readonly Dictionary<int, int> _indexByUid = new();
    private readonly D3 _gridMin;
    private readonly double _cell;
    private readonly int _nx, _ny, _nz;
    private readonly int[][] _cells;
    private readonly int[] _big;

    private sealed record Entry(int Uid, WorldPolyhedron Shape, bool Air, bool Detail);

    /// <summary>Builds the index over <paramref name="brushes"/> (time order) leaving out <paramref name="moverUids"/>.</summary>
    public BrushSolidityIndex(IReadOnlyList<Brush> brushes, IReadOnlyCollection<int>? moverUids = null)
        : this(brushes, brushes.Select(WorldPolyhedron.FromBrush).ToList(), moverUids)
    {
    }

    /// <summary>Builds the index from brushes and their already-computed polyhedra (same order).</summary>
    public BrushSolidityIndex(IReadOnlyList<Brush> brushes, IReadOnlyList<WorldPolyhedron> shapes, IReadOnlyCollection<int>? moverUids = null)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(shapes);
        var entries = new List<Entry>(brushes.Count);
        for (int i = 0; i < brushes.Count; i++)
        {
            Brush b = brushes[i];
            if (moverUids is not null && moverUids.Contains(b.Uid))
            {
                continue;
            }

            bool air = (b.Flags & (uint)(BrushFlags.Air | BrushFlags.Portal)) != 0;
            bool detail = (b.Flags & (uint)BrushFlags.Detail) != 0;
            _indexByUid.TryAdd(b.Uid, entries.Count);
            entries.Add(new Entry(b.Uid, shapes[i], air, detail));
        }

        _entries = entries.ToArray();
        D3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        foreach (Entry e in _entries.Where(e => e.Shape.Closed))
        {
            min = D3.Min(min, e.Shape.Min);
            max = D3.Max(max, e.Shape.Max);
        }

        if (min.X > max.X)
        {
            min = max = D3.Zero;
        }

        D3 size = max - min;
        double longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        _cell = Math.Max(2.0, longest / MaxCellsPerAxis);
        _gridMin = min;
        _nx = Math.Max(1, (int)Math.Ceiling(size.X / _cell) + 1);
        _ny = Math.Max(1, (int)Math.Ceiling(size.Y / _cell) + 1);
        _nz = Math.Max(1, (int)Math.Ceiling(size.Z / _cell) + 1);
        var lists = new List<int>?[_nx * _ny * _nz];
        var big = new List<int>();
        for (int i = 0; i < _entries.Length; i++)
        {
            WorldPolyhedron s = _entries[i].Shape;
            if (!s.Closed)
            {
                continue;
            }

            (int x0, int y0, int z0) = CellOf(s.Min);
            (int x1, int y1, int z1) = CellOf(s.Max);
            long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cells > MaxCellsPerBrush)
            {
                big.Add(i);
                continue;
            }

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        (lists[Flat(x, y, z)] ??= new List<int>()).Add(i);
                    }
                }
            }
        }

        _cells = lists.Select(l => l?.ToArray() ?? Array.Empty<int>()).ToArray();
        _big = big.ToArray();
    }

    /// <summary>The number of indexed (non-mover) brushes.</summary>
    public int Count => _entries.Length;

    /// <summary>
    /// True when <paramref name="point"/> is solid. <paramref name="excludeUids"/> leaves those brushes out ("what would
    /// this spot be without them"); <paramref name="structuralOnly"/> ignores detail brushes.
    /// </summary>
    public bool IsSolid(Vec3 point, IReadOnlySet<int>? excludeUids = null, bool structuralOnly = false) =>
        IsSolid(D3.From(point), excludeUids, structuralOnly);

    /// <inheritdoc cref="IsSolid(Vec3, IReadOnlySet{int}?, bool)"/>
    public bool IsSolid(D3 p, IReadOnlySet<int>? excludeUids = null, bool structuralOnly = false)
    {
        int last = LastContaining(p, excludeUids, structuralOnly);
        return last < 0 || !_entries[last].Air;
    }

    /// <summary>The UID of the last (time-order) brush containing <paramref name="p"/> under the filters, or null.</summary>
    public int? LastBrushAt(D3 p, IReadOnlySet<int>? excludeUids = null, bool structuralOnly = false)
    {
        int last = LastContaining(p, excludeUids, structuralOnly);
        return last < 0 ? null : _entries[last].Uid;
    }

    private int LastContaining(D3 p, IReadOnlySet<int>? exclude, bool structuralOnly)
    {
        int[] cell = Array.Empty<int>();
        if (p.X >= _gridMin.X && p.Y >= _gridMin.Y && p.Z >= _gridMin.Z)
        {
            int x = (int)((p.X - _gridMin.X) / _cell), y = (int)((p.Y - _gridMin.Y) / _cell), z = (int)((p.Z - _gridMin.Z) / _cell);
            if (x < _nx && y < _ny && z < _nz)
            {
                cell = _cells[Flat(x, y, z)];
            }
        }

        int a = cell.Length - 1, b = _big.Length - 1;
        while (a >= 0 || b >= 0)
        {
            int idx;
            if (b < 0 || (a >= 0 && cell[a] > _big[b]))
            {
                idx = cell[a--];
            }
            else
            {
                idx = _big[b--];
            }

            Entry e = _entries[idx];
            if ((structuralOnly && e.Detail) || (exclude is not null && exclude.Contains(e.Uid)))
            {
                continue;
            }

            if (e.Shape.Contains(p))
            {
                return idx;
            }
        }

        return -1;
    }

    private (int X, int Y, int Z) CellOf(D3 p) => (
        Math.Clamp((int)((p.X - _gridMin.X) / _cell), 0, _nx - 1),
        Math.Clamp((int)((p.Y - _gridMin.Y) / _cell), 0, _ny - 1),
        Math.Clamp((int)((p.Z - _gridMin.Z) / _cell), 0, _nz - 1));

    private int Flat(int x, int y, int z) => (((x * _ny) + y) * _nz) + z;
}
