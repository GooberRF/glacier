using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Closest-edge-to-ray picking for Edge mode. Edges are drawn as untagged
/// line segments (not in the GPU id buffer), so edge picking is a CPU ray test: the world-space
/// edge nearest the cursor ray within a pixel-radius tolerance and closest to the camera wins.
/// Reuses the gizmo's ray↔segment closest-approach math. Pure and unit-testable.
/// </summary>
public static class EdgePicker
{
    /// <summary>
    /// Picks the edge whose world-space segment lies within <paramref name="tol"/> of the ray and
    /// has the smallest (nearest) ray parameter, or null when none is in range.
    /// </summary>
    public static BrushEdge? Pick(IEnumerable<(BrushEdge Edge, Vec3 A, Vec3 B)> worldEdges, Vec3 rayOrigin, Vec3 rayDir, float tol)
    {
        BrushEdge? best = null;
        float bestT = float.MaxValue;
        foreach ((BrushEdge edge, Vec3 a, Vec3 b) in worldEdges)
        {
            if (GizmoPicker.SegmentHit(a, b, rayOrigin, rayDir, tol, out float t) && t >= 0f && t < bestT)
            {
                bestT = t;
                best = edge;
            }
        }

        return best;
    }
}
