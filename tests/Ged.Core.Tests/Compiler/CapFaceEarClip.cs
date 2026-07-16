using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// TEST ORACLE — a clean-room reimplementation of the cap-face triangulator RF runs in-game when a
/// geoable/breakable brush is dug (geomod / rock-debris capping). Reimplemented FROM THE SPEC in
/// Alpine Faction's <c>game_patch/misc/destruction.cpp</c> (<c>ear_clip_triangulate</c> +
/// <c>add_cap_faces_from_loop</c>) — MPL source read for behaviour ONLY, never copied. The point is to
/// detect, on GED's compiled output faces, the exact degeneracies that make the game's ear clip stall
/// with the console warning "[CapFace] Ear clip stuck: remaining=N of M" that Goober saw.
/// <para>
/// The game builds a boundary loop from the extracted piece's faces (shared <c>GVertex</c>s), computes a
/// Newell best-fit normal, builds a right/forward frame, projects the loop to 2D, and ear-clips. A
/// compiled face whose own vertex loop already stalls the ear clip (collinear runs, repeated vertices,
/// near-degenerate ears) is exactly the geometry that reaches those cap loops, so we probe each output
/// face's loop with the identical algorithm.
/// </para>
/// </summary>
internal static class CapFaceEarClip
{
    /// <summary>Outcome of triangulating one loop with the game's exact ear-clip.</summary>
    public enum Outcome
    {
        /// <summary>Fully triangulated (n-2 triangles) — the game caps this loop cleanly.</summary>
        Ok,

        /// <summary>2D area under 1e-10 — the game logs "[CapFace] Degenerate polygon" and emits nothing.</summary>
        Degenerate,

        /// <summary>Ear clip stalled — the game logs "[CapFace] Ear clip stuck: remaining=N of M".</summary>
        Stuck,
    }

    public readonly record struct Probe(
        Outcome Outcome,
        int Vertices,
        int Remaining,
        int CollinearVertices,
        int RepeatedVertices);

    /// <summary>Projects a world loop to 2D exactly as <c>add_cap_faces_from_loop</c> does, then runs the
    /// game's <c>ear_clip_triangulate</c>. Also reports collinear-vertex and repeated-vertex counts, which
    /// are the two degeneracies that stall the clip.</summary>
    public static Probe ProbeLoop(IReadOnlyList<Vec3> loop)
    {
        int n = loop.Count;
        if (n < 3)
        {
            return new Probe(Outcome.Degenerate, n, n, 0, 0);
        }

        // --- Newell best-fit normal (add_cap_faces_from_loop) --------------------------------------
        var normal = Vec3.Zero;
        for (int i = 0; i < n; i++)
        {
            Vec3 c = loop[i];
            Vec3 d = loop[(i + 1) % n];
            normal = normal.Add(new Vec3(
                (c.Y - d.Y) * (c.Z + d.Z),
                (c.Z - d.Z) * (c.X + d.X),
                (c.X - d.X) * (c.Y + d.Y)));
        }

        if (normal.Length() < 1e-8f)
        {
            return new Probe(Outcome.Degenerate, n, n, 0, 0);
        }

        normal = normal.Normalized();

        // --- 2D frame on the cap plane (right/forward) ---------------------------------------------
        Vec3 right = MathF.Abs(normal.Y) < 0.9f
            ? new Vec3(0f, 1f, 0f).Cross(normal)
            : new Vec3(1f, 0f, 0f).Cross(normal);
        right = right.Normalized();
        Vec3 forward = normal.Cross(right);

        var px = new float[n];
        var py = new float[n];
        for (int i = 0; i < n; i++)
        {
            px[i] = loop[i].Dot(right);
            py[i] = loop[i].Dot(forward);
        }

        int collinear = CountCollinearVertices(px, py, n);
        int repeated = CountRepeatedVertices(px, py, n);
        (Outcome outcome, int remaining) = EarClip(px, py, n);
        return new Probe(outcome, n, remaining, collinear, repeated);
    }

    /// <summary>The game's ear_clip_triangulate, faithfully ported: returns the outcome and how many
    /// vertices were still un-clipped when it stalled (== n if it completed / went degenerate).</summary>
    private static (Outcome, int) EarClip(float[] px, float[] py, int n)
    {
        var poly = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            poly.Add(i);
        }

        float area = 0f;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            area += px[poly[i]] * py[poly[j]];
            area -= px[poly[j]] * py[poly[i]];
        }

        if (area < 0f)
        {
            poly.Reverse();
            area = -area;
        }

        if (area < 1e-10f)
        {
            return (Outcome.Degenerate, n);
        }

        float Cross2d(int a, int b, int c) =>
            ((px[b] - px[a]) * (py[c] - py[a])) - ((py[b] - py[a]) * (px[c] - px[a]));

        bool PointInTri(int p, int a, int b, int c)
        {
            float d1 = ((px[p] - px[b]) * (py[a] - py[b])) - ((px[a] - px[b]) * (py[p] - py[b]));
            float d2 = ((px[p] - px[c]) * (py[b] - py[c])) - ((px[b] - px[c]) * (py[p] - py[c]));
            float d3 = ((px[p] - px[a]) * (py[c] - py[a])) - ((px[c] - px[a]) * (py[p] - py[a]));
            const float eps = 1e-6f;
            bool hasNeg = (d1 < -eps) || (d2 < -eps) || (d3 < -eps);
            bool hasPos = (d1 > eps) || (d2 > eps) || (d3 > eps);
            return !(hasNeg && hasPos);
        }

        int remaining = n;
        int maxIter = n * n;
        while (remaining > 3 && maxIter-- > 0)
        {
            bool foundEar = false;
            for (int i = 0; i < remaining; i++)
            {
                int prevI = ((i - 1) + remaining) % remaining;
                int nextI = (i + 1) % remaining;
                int a = poly[prevI], b = poly[i], c = poly[nextI];

                if (Cross2d(a, b, c) <= 1e-8f)
                {
                    continue; // reflex or collinear tip — never a convex ear
                }

                bool hasInterior = false;
                for (int k = 0; k < remaining; k++)
                {
                    if (k == prevI || k == i || k == nextI)
                    {
                        continue;
                    }

                    if (PointInTri(poly[k], a, b, c))
                    {
                        hasInterior = true;
                        break;
                    }
                }

                if (hasInterior)
                {
                    continue;
                }

                poly.RemoveAt(i);
                remaining--;
                foundEar = true;
                break;
            }

            if (!foundEar)
            {
                return (Outcome.Stuck, remaining);
            }
        }

        return (Outcome.Ok, n);
    }

    /// <summary>Count of loop corners that lie (near-)collinear on the segment of their two neighbours —
    /// a redundant / on-edge vertex the ear clip can never use as a tip.</summary>
    private static int CountCollinearVertices(float[] px, float[] py, int n)
    {
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            int p = ((i - 1) + n) % n;
            int q = (i + 1) % n;
            float cross = ((px[i] - px[p]) * (py[q] - py[p])) - ((py[i] - py[p]) * (px[q] - px[p]));
            float ax = px[q] - px[p], ay = py[q] - py[p];
            float span = MathF.Sqrt((ax * ax) + (ay * ay));
            // Perpendicular distance from corner i to the p→q line = |cross| / span. Under ~0.1 mm ⇒ collinear.
            if (span > 1e-6f && MathF.Abs(cross) / span < 1e-4f)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Count of corners whose 2D position coincides (within RED's 1e-4 weld) with an earlier corner.</summary>
    private static int CountRepeatedVertices(float[] px, float[] py, int n)
    {
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (MathF.Abs(px[i] - px[j]) <= 1e-4f && MathF.Abs(py[i] - py[j]) <= 1e-4f)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>Builds a face's world-space loop from a compiled <see cref="Geometry"/> (pool-indexed).</summary>
    public static List<Vec3> LoopOf(Geometry g, Face f)
    {
        var loop = new List<Vec3>(f.Vertices.Count);
        foreach (FaceVertex fv in f.Vertices)
        {
            if (fv.Index >= 0 && fv.Index < g.Vertices.Count)
            {
                loop.Add(g.Vertices[fv.Index]);
            }
        }

        return loop;
    }
}
