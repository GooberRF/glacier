using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Builds the geometry face-scroll table: one entry per scrolling source face,
/// keyed by the compiled face id (RF matches scroll velocities to faces by id).
/// Split fragments share their source face id, so an entry is emitted once per
/// distinct scrolling face id.
/// </summary>
public static class FaceScrollTable
{
    public static List<FaceScrollData> Build(List<CsgFace> faces, IReadOnlyList<Brush> brushes)
    {
        var seen = new HashSet<int>();
        var table = new List<FaceScrollData>();
        foreach (CsgFace f in faces)
        {
            if (f.Scroll is not Uv v || !seen.Add(f.FaceId))
            {
                continue;
            }

            table.Add(new FaceScrollData { FaceId = f.FaceId, UVelocity = v.U, VVelocity = v.V });
        }

        return table;
    }
}
