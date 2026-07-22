using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Rendering.Scene;

namespace Ged.App.Services;

/// <summary>
/// CPU nearest-vertex pick search: given the scene's registered brush vertices (world positions), a
/// pick ray, and a screen radius, returns the vertex whose screen-projected position is nearest the
/// ray within that radius. Recovers a near-miss on a tiny vertex dot in Vertex mode — it mirrors the
/// edge-pick CPU search and never consults the single-pixel id buffer, so it is immune to a brush
/// face occluding its own vertex's id (item 2).
/// </summary>
public static class VertexPickSearch
{
    /// <summary>
    /// The registered vertex nearest the ray within <paramref name="radiusPixels"/> screen pixels, or
    /// false. <paramref name="worldPerPixel"/> converts a world position to its metres-per-pixel at
    /// that depth (so the radius is a true on-screen distance). Vertices behind the ray origin are ignored.
    /// </summary>
    public static bool TryNearest(
        IReadOnlyList<BrushPickRegistry.VertexRef> vertices,
        Vector3 rayOrigin,
        Vector3 rayDir,
        Func<Vector3, float> worldPerPixel,
        float radiusPixels,
        out int brushUid,
        out int vertexIndex)
    {
        brushUid = vertexIndex = -1;
        float rr = Vector3.Dot(rayDir, rayDir);
        if (vertices.Count == 0 || rr < 1e-12f)
        {
            return false;
        }

        // The chosen vertex minimizes on-screen distance to the ray; but when two dots project to
        // nearly the same screen point (within TiePixels — e.g. a near and a far corner overlapping),
        // the one NEARER the camera wins. Otherwise clicking a dot could grab the vertex hidden
        // behind it (the same occlusion class that made the id-buffer pick unreliable — B2).
        const float tiePixels = 2f;
        float best = float.MaxValue;
        float bestT = float.MaxValue;
        foreach (BrushPickRegistry.VertexRef v in vertices)
        {
            float t = Vector3.Dot(v.World - rayOrigin, rayDir) / rr;
            if (t <= 0f)
            {
                continue; // behind the camera
            }

            Vector3 closest = rayOrigin + (rayDir * t);
            float worldDist = Vector3.Distance(v.World, closest);
            float wpp = worldPerPixel(v.World);
            float screenDist = wpp > 1e-9f ? worldDist / wpp : float.MaxValue;
            if (screenDist >= radiusPixels)
            {
                continue; // outside the pick radius
            }

            bool better = brushUid < 0
                || screenDist < best - tiePixels                        // clearly closer on screen
                || (screenDist <= best + tiePixels && t < bestT);       // screen tie → nearer camera
            if (better)
            {
                best = screenDist;
                bestT = t;
                brushUid = v.BrushUid;
                vertexIndex = v.VertexIndex;
            }
        }

        return brushUid >= 0;
    }
}
