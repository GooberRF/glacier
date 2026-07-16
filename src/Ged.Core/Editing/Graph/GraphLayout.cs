using System.Collections.Generic;

namespace Ged.Core.Editing.Graph;

/// <summary>
/// A per-level record of node positions for a graph view, keyed by object UID.
/// Persisted to the <c>&lt;level&gt;.gedlayout.json</c> sidecar (never inside the
/// RFL, never packed). A UID absent from the layout is auto-placed.
/// </summary>
public sealed class GraphLayout
{
    private readonly Dictionary<int, GraphNodePos> _positions = new();

    /// <summary>Layout schema version (bumped if the on-disk shape changes).</summary>
    public int Version { get; set; } = 1;

    public int Count => _positions.Count;

    public IReadOnlyDictionary<int, GraphNodePos> Positions => _positions;

    public bool Has(int uid) => _positions.ContainsKey(uid);

    public bool TryGet(int uid, out double x, out double y)
    {
        if (_positions.TryGetValue(uid, out GraphNodePos p))
        {
            x = p.X;
            y = p.Y;
            return true;
        }

        x = y = 0;
        return false;
    }

    public void Set(int uid, double x, double y) => _positions[uid] = new GraphNodePos(x, y);

    public void Remove(int uid) => _positions.Remove(uid);

    public void Clear() => _positions.Clear();
}

/// <summary>A single node's persisted position.</summary>
public readonly record struct GraphNodePos(double X, double Y);
