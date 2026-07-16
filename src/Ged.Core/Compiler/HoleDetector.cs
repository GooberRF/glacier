using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Leak / hole detection over compiled geometry: a watertight level has every
/// non-portal edge shared by exactly two faces. An edge used by a single face is
/// an open boundary — a hole the room flood would leak through to the void. The
/// midpoints of such edges are returned as clickable hole locations for the
/// Check-for-Holes tool.
/// <para>
/// Excluded, on BOTH the recompile and RED's original, are the face classes that
/// never close the room-sealing manifold:
/// <list type="bullet">
/// <item><b>Detail</b> sheets (glass, gratings, flat panels) — no manifold loop.</item>
/// <item><b>Liquid surfaces</b> (RF flag 0x0004). Binary-verified against RED.exe's
/// originals: a mode-6 water surface is a self-contained sub-manifold — on
/// dmabruptdecay its 118 liquid faces pair 322 edges liquid-to-liquid with <b>zero</b>
/// paired to a wall and zero open (RED emits the surface double-sided). Liquid edges
/// therefore never coincide with wall fragments and a liquid sheet cannot leak a room,
/// so counting its boundary as a "hole" is a false positive. (GED emits the surface
/// single-sided — a deliberate design — so its liquid edges are unpaired; that is a
/// cosmetic double-siding difference from RED, not a room leak.)</item>
/// </list>
/// </para>
/// </summary>
public static class HoleDetector
{
    /// <summary>Returns the world-space midpoints of open (non-manifold-boundary) edges.</summary>
    public static List<Vec3> Detect(Geometry g)
    {
        var count = new Dictionary<(int, int), int>();
        var sample = new Dictionary<(int, int), Vec3>();

        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || (f.PortalIndexPlus2 >= 2))
            {
                continue; // portal faces are intentional membranes, not walls
            }

            if (((FaceFlags)f.Flags & (FaceFlags.IsDetail | FaceFlags.LiquidSurface)) != 0)
            {
                // Detail sheets and liquid surfaces never close the room-sealing manifold
                // (liquid is a self-contained sub-manifold — RED pairs it 0 edges to walls).
                continue;
            }

            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int b = f.Vertices[(i + 1) % n].Index;
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
                if (!sample.ContainsKey(key) && a < g.Vertices.Count && b < g.Vertices.Count)
                {
                    sample[key] = Vec3Math.Lerp(g.Vertices[a], g.Vertices[b], 0.5f);
                }
            }
        }

        var holes = new List<Vec3>();
        foreach ((var key, int c) in count)
        {
            if (c == 1 && sample.TryGetValue(key, out Vec3 mid))
            {
                holes.Add(mid);
            }
        }

        return holes;
    }
}
