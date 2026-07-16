using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Welds near-coincident world positions into a shared vertex pool using a
/// grid spatial hash, so the compiled geometry has one pool index per distinct
/// corner and faces that meet at an edge reference identical indices (a
/// precondition for edge-adjacency room building and t-joint fixing). The weld
/// tolerance matches RED's geometry epsilon.
/// </summary>
public sealed class VertexWelder
{
    private const float Eps = CsgPlane.OnPlaneEpsilon;
    private const float CellSize = 0.01f; // >> Eps so coincident points share/adjoin a cell
    private readonly Dictionary<(int, int, int), List<int>> _grid = new();

    public List<Vec3> Vertices { get; } = new();

    /// <summary>Returns the pool index for <paramref name="p"/>, reusing an existing near-match.</summary>
    public int Add(Vec3 p)
    {
        (int cx, int cy, int cz) = Cell(p);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (_grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                    {
                        foreach (int i in bucket)
                        {
                            if (Vertices[i].ApproxEquals(p, Eps))
                            {
                                return i;
                            }
                        }
                    }
                }
            }
        }

        int idx = Vertices.Count;
        Vertices.Add(p);
        if (!_grid.TryGetValue((cx, cy, cz), out List<int>? cell))
        {
            cell = new List<int>();
            _grid[(cx, cy, cz)] = cell;
        }

        cell.Add(idx);
        return idx;
    }

    private static (int, int, int) Cell(Vec3 p) =>
        ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize), (int)MathF.Floor(p.Z / CellSize));
}
