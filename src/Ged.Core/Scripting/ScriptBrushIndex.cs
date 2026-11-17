using System;
using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The run's spatial index over the level's brushes: every brush's world bounds, a plan (XZ) grid of them, and
/// each brush's point-in-volume classifier, all built once per brush change (<see cref="BrushEditor.ChangeStamp"/>)
/// instead of per call. level.brushes_in, brush.bounds and brush:contains read it; the kit's ground and ceiling
/// probes ask them thousands of times against levels of ten thousand organic rock brushes, where a scan of every
/// brush's vertices per call dominated a build. Answers are exactly the uncached ones (the same bounds, the same
/// volume, hits in list order).
/// </summary>
internal sealed class ScriptBrushIndex
{
    private const float Cell = 8f;
    private const int MaxCellsPerBrush = 1024;

    private readonly BrushEditor _brushes;
    private IReadOnlyList<Brush>? _list;
    private int _count = -1;
    private long _stamp = -1;
    private ScriptAabb[] _bounds = Array.Empty<ScriptAabb>();
    private BrushVolume?[] _volumes = Array.Empty<BrushVolume?>();
    private bool[] _volumeMade = Array.Empty<bool>();
    private readonly Dictionary<Brush, int> _position = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, List<int>> _grid = new();
    private readonly List<int> _wide = new();
    private int[] _seen = Array.Empty<int>();
    private int _visit;

    internal ScriptBrushIndex(BrushEditor brushes) => _brushes = brushes;

    private void Ensure()
    {
        IReadOnlyList<Brush> list = _brushes.Brushes;
        if (ReferenceEquals(list, _list) && list.Count == _count && _stamp == _brushes.ChangeStamp)
        {
            return;
        }

        int n = list.Count;
        _bounds = new ScriptAabb[n];
        _volumes = new BrushVolume?[n];
        _volumeMade = new bool[n];
        _seen = new int[n];
        _visit = 0;
        _position.Clear();
        _grid.Clear();
        _wide.Clear();
        _gridBuilt = false;
        _queries = 0;
        for (int i = 0; i < n; i++)
        {
            Brush b = list[i];
            _bounds[i] = ScriptBrushHandle.WorldBounds(b);
            _position.TryAdd(b, i);
        }

        _list = list;
        _count = n;
        _stamp = _brushes.ChangeStamp;
    }

    // The plan grid, built on the second box query at one change stamp (a script that adds a brush between every
    // query pays one bounds pass per query, as the plain scan did, and no grid).
    private bool _gridBuilt;
    private int _queries;

    private void BuildGrid()
    {
        _gridBuilt = true;
        for (int i = 0; i < _count; i++)
        {
            ScriptAabb bb = _bounds[i];
            if (!(bb.Min.X <= bb.Max.X && bb.Min.Z <= bb.Max.Z))
            {
                _wide.Add(i); // (NaN bounds: never a hit, kept off the grid)
                continue;
            }

            long cx0 = CellOf(bb.Min.X), cx1 = CellOf(bb.Max.X), cz0 = CellOf(bb.Min.Z), cz1 = CellOf(bb.Max.Z);
            if ((cx1 - cx0 + 1) * (cz1 - cz0 + 1) > MaxCellsPerBrush)
            {
                _wide.Add(i);
                continue;
            }

            for (long cx = cx0; cx <= cx1; cx++)
            {
                for (long cz = cz0; cz <= cz1; cz++)
                {
                    long key = Key(cx, cz);
                    if (!_grid.TryGetValue(key, out List<int>? cell))
                    {
                        cell = new List<int>(4);
                        _grid[key] = cell;
                    }

                    cell.Add(i);
                }
            }
        }
    }

    private static long CellOf(float v)
    {
        double c = Math.Floor(v / Cell);
        return (long)Math.Clamp(c, -1_000_000d, 1_000_000d);
    }

    private static long Key(long cx, long cz) => ((cx + 1_048_576L) << 22) | (cz + 1_048_576L);

    /// <summary>True when the index was built for the brushes as they stand.</summary>
    private bool Current
    {
        get
        {
            IReadOnlyList<Brush> list = _brushes.Brushes;
            return ReferenceEquals(list, _list) && list.Count == _count && _stamp == _brushes.ChangeStamp;
        }
    }

    /// <summary>
    /// The brush's world bounds: from the index while it is current, else computed alone (a script that adds a brush
    /// and reads its bounds must not rebuild the whole index each time).
    /// </summary>
    internal ScriptAabb BoundsOf(Brush b)
    {
        ScriptAabb got = Current && _position.TryGetValue(b, out int i) ? _bounds[i] : ScriptBrushHandle.WorldBounds(b);
        if (Environment.GetEnvironmentVariable("GED_INDEX_VERIFY") == "1")
        {
            ScriptAabb want = ScriptBrushHandle.WorldBounds(b);
            if (!got.Min.Equals(want.Min) || !got.Max.Equals(want.Max))
            {
                Console.Error.WriteLine($"BOUNDS MISMATCH brush {b.Uid} stamp {_brushes.ChangeStamp}");
            }
        }

        return got;
    }

    /// <summary>
    /// The brush's point classifier (<see cref="BrushVolume"/> over its outward world faces), or null when it has no
    /// faces; cached while the index is current.
    /// </summary>
    internal BrushVolume? VolumeOf(Brush b)
    {
        if (!Current || !_position.TryGetValue(b, out int i))
        {
            return Make(b);
        }

        if (!_volumeMade[i])
        {
            _volumes[i] = Make(b);
            _volumeMade[i] = true;
        }

        return _volumes[i];
    }

    private static BrushVolume? Make(Brush b)
    {
        List<CsgFace> faces = BrushWorld.ToOutwardWorldFaces(b);
        return faces.Count == 0 ? null : BrushVolume.From(0, (b.Flags & (uint)BrushFlags.Air) != 0, faces);
    }

    /// <summary>The UIDs of the brushes whose world bounds intersect the box (touching counts), in list (time) order.</summary>
    internal List<int> UidsIn(Vec3 min, Vec3 max)
    {
        List<int> got = UidsInCore(min, max);
        if (Environment.GetEnvironmentVariable("GED_INDEX_VERIFY") == "1")
        {
            var want = new List<int>();
            foreach (Brush b in _brushes.Brushes)
            {
                if (Hits(ScriptBrushHandle.WorldBounds(b), min, max))
                {
                    want.Add(b.Uid);
                }
            }

            if (!System.Linq.Enumerable.SequenceEqual(got, want))
            {
                Console.Error.WriteLine($"INDEX MISMATCH box {min}..{max}: got {got.Count} want {want.Count} stamp {_brushes.ChangeStamp}");
            }
        }

        return got;
    }

    private List<int> UidsInCore(Vec3 min, Vec3 max)
    {
        Ensure();
        if (!_gridBuilt && ++_queries >= 2)
        {
            BuildGrid();
        }

        var hits = new List<int>();
        IReadOnlyList<Brush> list = _list!;
        long cx0 = CellOf(min.X), cx1 = CellOf(max.X), cz0 = CellOf(min.Z), cz1 = CellOf(max.Z);
        long cells = (cx1 - cx0 + 1) * (cz1 - cz0 + 1);
        if (!_gridBuilt || !(min.X <= max.X && min.Z <= max.Z) || cells > _grid.Count || cells > _count)
        {
            // A box over most of the level: the plain scan is cheaper than the cells.
            for (int i = 0; i < _count; i++)
            {
                if (Hits(_bounds[i], min, max))
                {
                    hits.Add(list[i].Uid);
                }
            }

            return hits;
        }

        if (++_visit == int.MaxValue)
        {
            Array.Clear(_seen);
            _visit = 1;
        }

        var found = new List<int>();
        void Take(int i)
        {
            if (_seen[i] != _visit)
            {
                _seen[i] = _visit;
                if (Hits(_bounds[i], min, max))
                {
                    found.Add(i);
                }
            }
        }

        for (long cx = cx0; cx <= cx1; cx++)
        {
            for (long cz = cz0; cz <= cz1; cz++)
            {
                if (_grid.TryGetValue(Key(cx, cz), out List<int>? cell))
                {
                    foreach (int i in cell)
                    {
                        Take(i);
                    }
                }
            }
        }

        foreach (int i in _wide)
        {
            Take(i);
        }

        found.Sort();
        foreach (int i in found)
        {
            hits.Add(list[i].Uid);
        }

        return hits;
    }

    private static bool Hits(ScriptAabb bb, Vec3 min, Vec3 max) =>
        bb.Min.X <= max.X && bb.Max.X >= min.X &&
        bb.Min.Y <= max.Y && bb.Max.Y >= min.Y &&
        bb.Min.Z <= max.Z && bb.Max.Z >= min.Z;
}
