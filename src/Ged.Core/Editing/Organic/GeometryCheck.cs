using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>
/// A brush shape audit. <see cref="Closed"/>: every undirected edge is used by exactly two faces, once
/// in each direction (Cairn LC071's rule, REFERENCE.md:2913-2914). <see cref="Outward"/>: closed and
/// wound the way RED stores the brush (<see cref="BrushOrientation"/>) — outward (positive enclosed volume)
/// for a solid brush, inward for an air brush; a solid portal brush may wind either way, as RED stores both.
/// <see cref="Convex"/>: closed and every vertex behind or on (≤ 1e-4 m) every face plane, whichever way the
/// faces wind. <see cref="Planar"/>: every corner within
/// <see cref="FacePlanarizer.PlanarityTolerance"/> of its face's plane. <see cref="Issues"/> are short
/// sentences in that order, at most 8.
/// </summary>
public sealed record GeometryReport(bool Closed, bool Outward, bool Convex, bool Planar, int Faces, int Vertices,
                                    int MaxFaceCorners, double MaxPlanarError, IReadOnlyList<string> Issues);

/// <summary>Checks a brush's geometry: closed, wound as RED stores it, convex, planar (see <see cref="GeometryReport"/>).</summary>
public static class GeometryCheck
{
    /// <summary>A vertex counts as on a face plane within this distance for the convexity test, metres.</summary>
    public const double ConvexTolerance = 1e-4;

    /// <summary>The most issue sentences a report carries.</summary>
    public const int MaxIssues = 8;

    /// <summary>
    /// Audits <paramref name="b"/>'s local geometry in double precision. The tests are invariant under the
    /// brush's rigid transform, so the local and world answers agree.
    /// </summary>
    public static GeometryReport Check(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        Geometry g = b.Geometry;
        var issues = new List<string>();
        void Issue(string s)
        {
            if (issues.Count < MaxIssues)
            {
                issues.Add(s);
            }
        }

        Vec3d V(int i) => new(g.Vertices[i].X, g.Vertices[i].Y, g.Vertices[i].Z);

        // ---- closed ----------------------------------------------------------------------------
        bool closed = g.Faces.Count >= 4;
        if (!closed)
        {
            Issue($"the brush has {g.Faces.Count} face(s); a closed brush needs at least 4.");
        }

        var directed = new Dictionary<(int, int), int>();
        for (int f = 0; f < g.Faces.Count; f++)
        {
            List<FaceVertex> fv = g.Faces[f].Vertices;
            if (fv.Count < 3)
            {
                closed = false;
                Issue($"face {f} has fewer than three corners.");
                continue;
            }

            if (fv.Any(v => v.Index < 0 || v.Index >= g.Vertices.Count))
            {
                closed = false;
                Issue($"face {f} references a vertex outside the pool.");
                continue;
            }

            for (int i = 0; i < fv.Count; i++)
            {
                (int u, int v) = (fv[i].Index, fv[(i + 1) % fv.Count].Index);
                if (u == v)
                {
                    closed = false;
                    Issue($"face {f} repeats vertex {u} at consecutive corners.");
                    continue;
                }

                directed[(u, v)] = directed.TryGetValue((u, v), out int n) ? n + 1 : 1;
            }
        }

        foreach ((int u, int v) in directed.Keys.Select(k => (Math.Min(k.Item1, k.Item2), Math.Max(k.Item1, k.Item2))).Distinct().OrderBy(k => k))
        {
            int fwd = directed.TryGetValue((u, v), out int a) ? a : 0;
            int back = directed.TryGetValue((v, u), out int c) ? c : 0;
            if (fwd == 1 && back == 1)
            {
                continue;
            }

            closed = false;
            int total = fwd + back;
            Issue(total == 1 ? $"edge {u}–{v} is open (only one face uses it)."
                : total == 2 ? $"edge {u}–{v} is used twice in the same direction (a face is flipped)."
                : $"edge {u}–{v} is shared by {total} faces.");
        }

        // ---- planes (Newell, double) ------------------------------------------------------------
        var planes = new (Vec3d N, Vec3d C)[g.Faces.Count];
        double maxPlanar = 0;
        int maxCorners = 0;
        var planarIssues = new List<string>();
        for (int f = 0; f < g.Faces.Count; f++)
        {
            List<FaceVertex> fv = g.Faces[f].Vertices;
            maxCorners = Math.Max(maxCorners, fv.Count);
            if (fv.Count < 3 || fv.Any(v => v.Index < 0 || v.Index >= g.Vertices.Count))
            {
                continue;
            }

            var pts = fv.Select(v => V(v.Index)).ToList();
            planes[f] = ConvexHull3.NewellPlane(pts, Enumerable.Range(0, pts.Count).ToArray());
            double err = pts.Max(p => Math.Abs(planes[f].N.Dot(p.Sub(planes[f].C))));
            maxPlanar = Math.Max(maxPlanar, err);
            if (err > FacePlanarizer.PlanarityTolerance)
            {
                planarIssues.Add(string.Create(CultureInfo.InvariantCulture, $"face {f} is not planar (a corner is {err:0.######} m off its plane)."));
            }
        }

        bool planar = planarIssues.Count == 0;

        // ---- outward ----------------------------------------------------------------------------
        double volume6 = 0;
        if (g.Vertices.Count > 0)
        {
            Vec3d r = V(0);
            foreach (Face face in g.Faces)
            {
                List<FaceVertex> fv = face.Vertices;
                if (fv.Count < 3 || fv.Any(v => v.Index < 0 || v.Index >= g.Vertices.Count))
                {
                    continue;
                }

                Vec3d a = V(fv[0].Index).Sub(r);
                for (int i = 1; i + 1 < fv.Count; i++)
                {
                    Vec3d p = V(fv[i].Index).Sub(r), q = V(fv[i + 1].Index).Sub(r);
                    volume6 += a.Dot(p.Cross(q));
                }
            }
        }

        // RED stores an air brush's faces inward and a solid brush's outward; a solid portal brush either way.
        bool air = BrushOrientation.RequiresInward(b.Flags);
        bool solidPortal = !air && (b.Flags & (uint)BrushFlags.Portal) != 0;
        bool outward = closed && (air ? volume6 < 0 : volume6 > 0 || (solidPortal && volume6 < 0));
        if (closed && !outward)
        {
            Issue(air
                ? "the faces wind outward (the enclosed volume is positive); RED stores an air brush's faces inward and would rebuild it inside-out."
                : "the faces wind inward (the enclosed volume is negative).");
        }

        // ---- convex (against the outward planes, whichever way the faces are stored) -------------------
        double toOutward = volume6 < 0 ? -1 : 1;
        bool convex = closed;
        if (closed)
        {
            var used = g.Faces.SelectMany(face => face.Vertices.Select(v => v.Index)).Distinct().OrderBy(i => i).ToList();
            for (int f = 0; f < g.Faces.Count && convex; f++)
            {
                foreach (int i in used)
                {
                    double d = toOutward * planes[f].N.Dot(V(i).Sub(planes[f].C));
                    if (d > ConvexTolerance)
                    {
                        convex = false;
                        Issue(string.Create(CultureInfo.InvariantCulture, $"vertex {i} is {d:0.######} m in front of face {f} (not convex)."));
                        break;
                    }
                }
            }
        }

        foreach (string s in planarIssues)
        {
            Issue(s);
        }

        return new GeometryReport(closed, outward, convex, planar, g.Faces.Count, g.Vertices.Count, maxCorners, maxPlanar, issues);
    }
}
