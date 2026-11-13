using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>One face of an <see cref="AuditShape"/>: its world corners and the plane through them.</summary>
public sealed record AuditFace(Vec3d[] Points, Vec3d Normal, Vec3d Centroid);

/// <summary>
/// A brush's faces in world space for the near-coplanar audit (<see cref="HullAudit"/>), with the
/// axis-aligned bounds of its corners.
/// </summary>
public sealed class AuditShape
{
    private AuditShape(IReadOnlyList<AuditFace> faces, string label)
    {
        Faces = faces;
        Label = label;
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (AuditFace f in faces)
        {
            foreach (Vec3d p in f.Points)
            {
                for (int a = 0; a < 3; a++)
                {
                    lo[a] = Math.Min(lo[a], p[a]);
                    hi[a] = Math.Max(hi[a], p[a]);
                }
            }
        }

        Min = new Vec3d(lo[0], lo[1], lo[2]);
        Max = new Vec3d(hi[0], hi[1], hi[2]);
    }

    /// <summary>The faces (corner lists with their planes).</summary>
    public IReadOnlyList<AuditFace> Faces { get; }

    /// <summary>The bounds minimum of every corner.</summary>
    public Vec3d Min { get; }

    /// <summary>The bounds maximum of every corner.</summary>
    public Vec3d Max { get; }

    /// <summary>How conflicts name this shape (e.g. <c>segment 3</c> or <c>brush#12</c>).</summary>
    public string Label { get; }

    /// <summary>
    /// For an existing brush made by an organic generator (a cave, canyon, passage, pit or rock piece): the name
    /// of what it belongs to (its <c>kit.name</c>, or a generic description), so a failure against it can say so.
    /// Null for every other shape.
    /// </summary>
    public string? Organic { get; private init; }

    /// <summary>The faces of a hull (its corners are already world positions).</summary>
    public static AuditShape FromHull(HullResult hull, string label)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var faces = new List<AuditFace>(hull.Faces.Count);
        foreach (HullFace f in hull.Faces)
        {
            Vec3d[] pts = f.Corners.Select(i => hull.Vertices[i]).ToArray();
            faces.Add(new AuditFace(pts, f.Normal, Centroid(pts)));
        }

        return new AuditShape(faces, label);
    }

    /// <summary>
    /// The faces of an existing brush, from its world vertices (any rotation), planes by Newell's method, wound
    /// outward like a hull's — an air brush, which RED stores inward (<see cref="BrushOrientation"/>), is read
    /// with its corner order reversed.
    /// </summary>
    public static AuditShape FromBrush(Brush b, string label, string? organic = null)
    {
        ArgumentNullException.ThrowIfNull(b);
        Geometry g = b.Geometry;
        bool inward = BrushOrientation.OutwardSign(b) < 0f;
        var faces = new List<AuditFace>(g.Faces.Count);
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || f.Vertices.Any(v => v.Index < 0 || v.Index >= g.Vertices.Count))
            {
                continue;
            }

            Vec3d[] pts = f.Vertices.Select(v => HullDeform.World(b, g.Vertices[v.Index])).ToArray();
            if (inward)
            {
                Array.Reverse(pts);
            }

            (Vec3d n, Vec3d c) = ConvexHull3.NewellPlane(pts, Enumerable.Range(0, pts.Length).ToArray());
            if (n.Length > 0)
            {
                faces.Add(new AuditFace(pts, n, c));
            }
        }

        return new AuditShape(faces, label) { Organic = organic };
    }

    private static Vec3d Centroid(Vec3d[] pts)
    {
        double x = 0, y = 0, z = 0;
        foreach (Vec3d p in pts)
        {
            x += p.X;
            y += p.Y;
            z += p.Z;
        }

        return new Vec3d(x / pts.Length, y / pts.Length, z / pts.Length);
    }

    /// <summary>True when the two shapes' bounds, each inflated by <paramref name="pad"/> metres, intersect.</summary>
    internal bool Touches(AuditShape other, double pad) =>
        Min.X - pad <= other.Max.X + pad && other.Min.X - pad <= Max.X + pad &&
        Min.Y - pad <= other.Max.Y + pad && other.Min.Y - pad <= Max.Y + pad &&
        Min.Z - pad <= other.Max.Z + pad && other.Min.Z - pad <= Max.Z + pad;
}

/// <summary>What an <see cref="AuditConflict"/> found.</summary>
public enum AuditConflictKind
{
    /// <summary>A near-coplanar face pair, or a corner just off a face, edge or corner of the other shape.</summary>
    NearCoplanar,

    /// <summary>Two edges whose interiors pass just beside each other.</summary>
    Edges,

    /// <summary>Two faces meeting along an edge almost, but not exactly, flat.</summary>
    Fold,
}

/// <summary>One near-coplanar face pair: which placed shape, the two faces, and the largest vertex-to-plane distance.</summary>
/// <remarks>A vertex conflict (a corner of one shape just off a face of the other) has -1 as the face index of the side the
/// corner belongs to; a corner beside another corner has -1 for both.</remarks>
public sealed record AuditConflict(int PlacedIndex, string PlacedLabel, int PlacedFace, int CandidateFace, double Delta)
{
    /// <summary>What was found.</summary>
    public AuditConflictKind Kind { get; init; }

    public override string ToString() => Kind switch
    {
        AuditConflictKind.Edges => string.Create(CultureInfo.InvariantCulture, $"an edge of face {CandidateFace + 1} passes {Delta * 1000:0.###} mm beside an edge of {PlacedLabel} face {PlacedFace + 1}"),
        AuditConflictKind.Fold => PlacedIndex < 0
            ? string.Create(CultureInfo.InvariantCulture, $"{PlacedLabel} faces {PlacedFace + 1} and {CandidateFace + 1} fold by under {HullAudit.FoldDegrees.ToString(CultureInfo.InvariantCulture)}° ({Delta * 1000:0.###} mm)")
            : string.Create(CultureInfo.InvariantCulture, $"{PlacedLabel} face {PlacedFace + 1} / face {CandidateFace + 1} fold by under {HullAudit.FoldDegrees.ToString(CultureInfo.InvariantCulture)}° ({Delta * 1000:0.###} mm)"),
        _ => PlacedFace < 0 && CandidateFace < 0
            ? string.Create(CultureInfo.InvariantCulture, $"a corner {Delta * 1000:0.###} mm from a corner of {PlacedLabel}")
            : PlacedFace < 0
                ? string.Create(CultureInfo.InvariantCulture, $"a corner of {PlacedLabel} {Delta * 1000:0.###} mm off face {CandidateFace + 1}")
                : CandidateFace < 0
                    ? string.Create(CultureInfo.InvariantCulture, $"a corner {Delta * 1000:0.###} mm off {PlacedLabel} face {PlacedFace + 1}")
                    : string.Create(CultureInfo.InvariantCulture, $"{PlacedLabel} face {PlacedFace + 1} / face {CandidateFace + 1}: {Delta * 1000:0.###} mm off one plane"),
    };
}

/// <summary>
/// The near-coplanar audit (ORGANIC-GEOMETRY-SPEC §0.3 separation rule, §4.2). Two faces of different
/// generated brushes must be COINCIDENT (every corner of each within 1e-6 m of the other's plane — only
/// identical grid data achieves it) or clearly SEPARATE (normals more than 0.5° apart, or some corner at
/// least 5 mm off the other plane). The band in between is where RED's 1e-4 plane epsilon classifies the
/// same surface differently from the two sides, leaving duplicate coplanar floors, open edges or a
/// sub-millimetre solid membrane.
/// <para>
/// Two refinements keep the audit to that hazard (the literal pairwise test over-reports on deformer pieces):
/// a pair that shares an edge (two identical world corners) is two neighbours meeting along that edge — a
/// fold, not a double surface, which <see cref="Folds"/> checks — and a pair whose polygons do not overlap
/// when projected into their common plane never covers the same area, so neither can shadow the other. Only
/// near-coplanar pairs that cover a common patch of the plane are conflicts, and their separation is measured
/// over that common patch: two faces that cross each other at a shallow angle inside it conflict even when
/// their far corners are centimetres apart.
/// </para>
/// <para>
/// The same band applies to single corners and edges: a corner of one shape that lies within a face's outline
/// but between 1e-6 m and 5 mm off its plane is a conflict (a cave corner 1.95 mm off a room wall leaves
/// exactly those two open edges), and so is a corner in that band beside the interior of another shape's edge
/// (a near T-junction: a cave floor corner 0.96 mm beside a crossing cave's floor edge leaks), a corner in that
/// band of another shape's corner, and two edges whose interiors pass within that band of each other. A corner
/// ON the plane, edge or corner (shared or touching exactly) is allowed.
/// </para>
/// </summary>
public static class HullAudit
{
    /// <summary>Faces within this distance (metres) are the same plane.</summary>
    public const double CoincidentTolerance = 1e-6;

    /// <summary>Faces at least this far apart (metres) are clearly separate.</summary>
    public const double SeparationDistance = 0.005;

    /// <summary>The bounds padding (metres) for the broad phase.</summary>
    public const double BoundsPadding = 0.01;

    /// <summary>The overlap a projected pair needs (metres along a separating axis) to count as covering a common patch.</summary>
    public const double OverlapTolerance = 1e-6;

    /// <summary>cos 0.5°: normals closer than this (in either direction) are parallel for the audit.</summary>
    public static readonly double ParallelCos = Math.Cos(0.5 * Math.PI / 180);

    /// <summary>The conflicts between <paramref name="candidate"/> and each placed hull (spec signature).</summary>
    public static IReadOnlyList<AuditConflict> Conflicts(IReadOnlyList<HullResult> placed, HullResult candidate)
    {
        ArgumentNullException.ThrowIfNull(placed);
        return Conflicts(placed.Select((h, i) => AuditShape.FromHull(h, $"hull {i + 1}")).ToList(), AuditShape.FromHull(candidate, "candidate"));
    }

    /// <summary>
    /// The first of <paramref name="conflicts"/> in which a wall of <paramref name="candidate"/> nearly lines up with a
    /// wall of an existing organic piece (<see cref="AuditShape.Organic"/>): both faces are walls (steeper than
    /// <see cref="WallMinDegrees"/>) and their planes are within <see cref="ParallelDegrees"/> of parallel. That is
    /// what a new tube that starts inside another one on the same line runs into — its rough walls shadow the
    /// existing walls a few millimetres apart — and no seed fixes it. Null when the conflicts are anything else.
    /// </summary>
    public static AuditConflict? OrganicWallClash(IReadOnlyList<AuditShape> placed, AuditShape candidate, IReadOnlyList<AuditConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(conflicts);
        double parallel = Math.Cos(ParallelDegrees * Math.PI / 180);
        foreach (AuditConflict c in conflicts)
        {
            if (c.Kind == AuditConflictKind.Fold || c.PlacedIndex < 0 || c.PlacedIndex >= placed.Count || placed[c.PlacedIndex].Organic is null
                || c.PlacedFace < 0 || c.CandidateFace < 0)
            {
                continue;
            }

            Vec3d p = placed[c.PlacedIndex].Faces[c.PlacedFace].Normal.Normalized();
            Vec3d q = candidate.Faces[c.CandidateFace].Normal.Normalized();
            if (IsWall(p) && IsWall(q) && Math.Abs(p.Dot(q)) >= parallel)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>A face steeper than this (degrees from horizontal) counts as a wall for <see cref="OrganicWallClash"/>.</summary>
    public const double WallMinDegrees = 45;

    /// <summary>Two wall planes within this many degrees of parallel "line up" for <see cref="OrganicWallClash"/>.</summary>
    public const double ParallelDegrees = 15;

    private static bool IsWall(Vec3d unitNormal) => Math.Abs(unitNormal.Y) <= Math.Sin((90 - WallMinDegrees) * Math.PI / 180);

    /// <summary>The conflicts between <paramref name="candidate"/> and every placed shape whose padded bounds it touches.</summary>
    public static IReadOnlyList<AuditConflict> Conflicts(IReadOnlyList<AuditShape> placed, AuditShape candidate)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(candidate);
        var conflicts = new List<AuditConflict>();
        for (int s = 0; s < placed.Count; s++)
        {
            AuditShape other = placed[s];
            if (!other.Touches(candidate, BoundsPadding))
            {
                continue;
            }

            for (int i = 0; i < other.Faces.Count; i++)
            {
                for (int j = 0; j < candidate.Faces.Count; j++)
                {
                    if (Pair(other.Faces[i], candidate.Faces[j]) is double delta)
                    {
                        conflicts.Add(new AuditConflict(s, other.Label, i, j, delta));
                    }
                }
            }

            foreach (Vec3d v in Corners(candidate))
            {
                for (int i = 0; i < other.Faces.Count; i++)
                {
                    if (NearFace(v, other.Faces[i]) is double d)
                    {
                        conflicts.Add(new AuditConflict(s, other.Label, i, -1, d));
                    }
                }
            }

            foreach (Vec3d v in Corners(other))
            {
                for (int j = 0; j < candidate.Faces.Count; j++)
                {
                    if (NearFace(v, candidate.Faces[j]) is double d)
                    {
                        conflicts.Add(new AuditConflict(s, other.Label, -1, j, d));
                    }
                }
            }

            foreach ((AuditShape from, AuditShape to, bool fromCandidate) in new[] { (candidate, other, true), (other, candidate, false) })
            {
                List<(Vec3d A, Vec3d B, int Face)> edges = Edges(to);
                foreach (Vec3d v in Corners(from))
                {
                    foreach ((Vec3d a, Vec3d b, int face) in edges)
                    {
                        if (NearEdge(v, a, b) is double d)
                        {
                            conflicts.Add(fromCandidate ? new AuditConflict(s, other.Label, face, -1, d) : new AuditConflict(s, other.Label, -1, face, d));
                        }
                    }
                }
            }

            List<Vec3d> otherCorners = Corners(other).ToList();
            foreach (Vec3d v in Corners(candidate))
            {
                foreach (Vec3d w in otherCorners)
                {
                    if (NearCorner(v, w) is double d)
                    {
                        conflicts.Add(new AuditConflict(s, other.Label, -1, -1, d));
                    }
                }
            }

            List<(Vec3d A, Vec3d B, int Face)> otherEdges = Edges(other);
            foreach ((Vec3d a, Vec3d b, int cf) in Edges(candidate))
            {
                foreach ((Vec3d c, Vec3d e, int of) in otherEdges)
                {
                    if (NearEdges(a, b, c, e) is double d)
                    {
                        conflicts.Add(new AuditConflict(s, other.Label, of, cf, d) { Kind = AuditConflictKind.Edges });
                    }
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// The angle (degrees) below which two faces meeting along an edge, but not exactly coplanar, are a near-flat
    /// fold. It sits above the compiler's plane-merge angle (<c>PlaneRegistry</c> folds planes whose normals are
    /// within about 0.44° and offsets within 2 mm into one), so a fold is either exact or clearly separate planes.
    /// </summary>
    public const double FoldDegrees = 0.5;

    /// <summary>cos <see cref="FoldDegrees"/>.</summary>
    public static readonly double FoldCos = Math.Cos(FoldDegrees * Math.PI / 180);

    /// <summary>
    /// Near-flat folds: a face of <paramref name="candidate"/> and a face of a placed shape that share an edge,
    /// with normals within <see cref="FoldDegrees"/> of each other, yet are not exactly coplanar.
    /// The compiler handles such almost-merged pairs badly (tests/fixtures/organic/findings
    /// near-flat-wall-fold-leak.lua and sloped-near-flat-floor-fold-leak.lua), so generators re-draw them.
    /// </summary>
    public static IReadOnlyList<AuditConflict> Folds(IReadOnlyList<AuditShape> placed, AuditShape candidate)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(candidate);
        var conflicts = new List<AuditConflict>();
        for (int s = 0; s < placed.Count; s++)
        {
            AuditShape other = placed[s];
            if (!other.Touches(candidate, BoundsPadding))
            {
                continue;
            }

            for (int i = 0; i < other.Faces.Count; i++)
            {
                for (int j = 0; j < candidate.Faces.Count; j++)
                {
                    if (Fold(other.Faces[i], candidate.Faces[j]) is double d)
                    {
                        conflicts.Add(new AuditConflict(s, other.Label, i, j, d) { Kind = AuditConflictKind.Fold });
                    }
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Near-flat folds inside one shape: two of its faces that share an edge with normals within
    /// <see cref="FoldDegrees"/> of each other (the compiler would merge their planes just the same).
    /// </summary>
    public static IReadOnlyList<AuditConflict> SelfFolds(AuditShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var conflicts = new List<AuditConflict>();
        for (int i = 0; i < shape.Faces.Count; i++)
        {
            for (int j = i + 1; j < shape.Faces.Count; j++)
            {
                if (Fold(shape.Faces[i], shape.Faces[j]) is double d)
                {
                    conflicts.Add(new AuditConflict(-1, shape.Label, i, j, d) { Kind = AuditConflictKind.Fold });
                }
            }
        }

        return conflicts;
    }

    /// <summary>The fold's largest vertex-to-plane distance when two faces sharing an edge are nearly but not exactly coplanar, else null.</summary>
    private static double? Fold(AuditFace a, AuditFace b)
    {
        if (a.Normal.Dot(b.Normal) < FoldCos)
        {
            return null;
        }

        int shared = 0;
        foreach (Vec3d p in a.Points)
        {
            if (Array.IndexOf(b.Points, p) >= 0)
            {
                shared++;
            }
        }

        if (shared < 2)
        {
            return null;
        }

        double delta = 0;
        foreach (Vec3d p in a.Points)
        {
            delta = Math.Max(delta, Math.Abs(b.Normal.Dot(p.Sub(b.Centroid))));
        }

        foreach (Vec3d p in b.Points)
        {
            delta = Math.Max(delta, Math.Abs(a.Normal.Dot(p.Sub(a.Centroid))));
        }

        return delta > CoincidentTolerance ? delta : null;
    }

    private static IEnumerable<Vec3d> Corners(AuditShape shape) => shape.Faces.SelectMany(f => f.Points).Distinct();

    /// <summary>The distance between two corners of different shapes when it is in the band (a near-miss), else null.</summary>
    internal static double? NearCorner(Vec3d v, Vec3d w)
    {
        double d = v.Sub(w).Length;
        return d > CoincidentTolerance && d < SeparationDistance ? d : null;
    }

    /// <summary>
    /// The distance between edges a–b and c–e when their closest points lie in both edges' interiors (at least
    /// the separation distance from every end) and that distance is in the band — two edges that almost, but not
    /// exactly, cross or run together — else null.
    /// </summary>
    internal static double? NearEdges(Vec3d a, Vec3d b, Vec3d c, Vec3d e)
    {
        Vec3d d1 = b.Sub(a), d2 = e.Sub(c), r = a.Sub(c);
        double aa = d1.Dot(d1), ee = d2.Dot(d2);
        if (aa < 1e-12 || ee < 1e-12)
        {
            return null;
        }

        double m1 = SeparationDistance / Math.Sqrt(aa), m2 = SeparationDistance / Math.Sqrt(ee);
        double bb = d1.Dot(d2), cc = d1.Dot(r), ff = d2.Dot(r);
        double den = (aa * ee) - (bb * bb);
        if (den <= 1e-12 * aa * ee)
        {
            // Parallel: the lines' distance, where their interiors overlap.
            double ta = ff / ee, tb = (bb + ff) / ee;
            double t0 = Math.Max(m2, Math.Min(ta, tb)), t1 = Math.Min(1 - m2, Math.Max(ta, tb));
            if (t0 > t1)
            {
                return null;
            }

            double s0 = ((bb * ((t0 + t1) / 2)) - cc) / aa;
            if (s0 <= m1 || s0 >= 1 - m1)
            {
                return null;
            }

            double dp = a.Add(d1.Scale(s0)).Sub(c.Add(d2.Scale((t0 + t1) / 2))).Length;
            return dp > CoincidentTolerance && dp < SeparationDistance ? dp : null;
        }

        double s = ((bb * ff) - (cc * ee)) / den;
        double t = ((aa * ff) - (bb * cc)) / den;
        if (s <= m1 || s >= 1 - m1 || t <= m2 || t >= 1 - m2)
        {
            return null;
        }

        double dist = a.Add(d1.Scale(s)).Sub(c.Add(d2.Scale(t))).Length;
        return dist > CoincidentTolerance && dist < SeparationDistance ? dist : null;
    }

    private static List<(Vec3d A, Vec3d B, int Face)> Edges(AuditShape shape)
    {
        var seen = new HashSet<(Vec3d, Vec3d)>();
        var edges = new List<(Vec3d, Vec3d, int)>();
        for (int f = 0; f < shape.Faces.Count; f++)
        {
            Vec3d[] p = shape.Faces[f].Points;
            for (int i = 0; i < p.Length; i++)
            {
                Vec3d a = p[i], b = p[(i + 1) % p.Length];
                if (seen.Add((a, b)) && seen.Add((b, a)))
                {
                    edges.Add((a, b, f));
                }
            }
        }

        return edges;
    }

    /// <summary>
    /// The corner's distance from the edge when it lies in the band beside the edge's interior (a near
    /// T-junction: two shapes almost, but not exactly, sharing a point on that edge), else null.
    /// </summary>
    internal static double? NearEdge(Vec3d v, Vec3d a, Vec3d b)
    {
        Vec3d e = b.Sub(a);
        double len2 = e.Dot(e);
        if (len2 < 1e-12)
        {
            return null;
        }

        double t = v.Sub(a).Dot(e) / len2;
        double margin = SeparationDistance / Math.Sqrt(len2);
        if (t <= margin || t >= 1 - margin)
        {
            return null;
        }

        double d = v.Sub(a.Add(e.Scale(t))).Length;
        return d > CoincidentTolerance && d < SeparationDistance ? d : null;
    }

    /// <summary>The corner's distance from the face plane when it is inside the band and within the face's outline, else null.</summary>
    internal static double? NearFace(Vec3d v, AuditFace f)
    {
        double d = Math.Abs(f.Normal.Dot(v.Sub(f.Centroid)));
        if (d <= CoincidentTolerance || d >= SeparationDistance)
        {
            return null;
        }

        return InsideOutline(v, f.Points, f.Normal) ? d : null;
    }

    /// <summary>True when <paramref name="v"/>, projected along the face's dominant axis, lies strictly inside the convex outline.</summary>
    private static bool InsideOutline(Vec3d v, Vec3d[] poly, Vec3d n)
    {
        int drop = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
        (int u, int w) = drop == 0 ? (1, 2) : drop == 1 ? (2, 0) : (0, 1);
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            Vec3d a = poly[i], b = poly[(i + 1) % poly.Length];
            double eu = b[u] - a[u], ew = b[w] - a[w];
            double len = Math.Sqrt((eu * eu) + (ew * ew));
            if (len < 1e-12)
            {
                continue;
            }

            double cross = ((eu * (v[w] - a[w])) - (ew * (v[u] - a[u]))) / len;
            if (Math.Abs(cross) <= OverlapTolerance)
            {
                return false;
            }

            int s = Math.Sign(cross);
            if (sign == 0)
            {
                sign = s;
            }
            else if (s != sign)
            {
                return false;
            }
        }

        return sign != 0;
    }

    /// <summary>
    /// The separation of two near-parallel faces over the patch they both cover when the pair conflicts, else
    /// null: the two planes' distance is measured at every corner of the common patch (it varies linearly over
    /// it); faces closer than the band somewhere in the patch but not coincident everywhere conflict, and so do
    /// faces that cross inside it.
    /// </summary>
    internal static double? Pair(AuditFace a, AuditFace b)
    {
        if (Math.Abs(a.Normal.Dot(b.Normal)) < ParallelCos)
        {
            return null;
        }

        int shared = 0;
        foreach (Vec3d p in a.Points)
        {
            if (Array.IndexOf(b.Points, p) >= 0 && ++shared >= 2)
            {
                return null;
            }
        }

        if (!Overlap(a.Points, b.Points, a.Normal))
        {
            return null;
        }

        int drop = Dominant(a.Normal);
        (int u, int w) = Others(drop);
        List<(double U, double W)> patch = Clip(a.Points, b.Points, u, w);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        if (patch.Count >= 3 && Math.Abs(b.Normal[drop]) > 1e-9)
        {
            double scale = Math.Abs(a.Normal[drop]);
            foreach ((double pu, double pw) in patch)
            {
                double ha = Height(a, drop, u, w, pu, pw), hb = Height(b, drop, u, w, pu, pw);
                double h = (ha - hb) * scale;
                lo = Math.Min(lo, h);
                hi = Math.Max(hi, h);
            }
        }
        else
        {
            // Degenerate patch: fall back to the corner distances.
            foreach (Vec3d p in a.Points)
            {
                double h = b.Normal.Dot(p.Sub(b.Centroid)) * Math.Sign(a.Normal.Dot(b.Normal));
                lo = Math.Min(lo, h);
                hi = Math.Max(hi, h);
            }
        }

        double near = lo < 0 && hi > 0 ? 0 : Math.Min(Math.Abs(lo), Math.Abs(hi));
        double far = Math.Max(Math.Abs(lo), Math.Abs(hi));
        if (far <= CoincidentTolerance || near >= SeparationDistance)
        {
            return null;
        }

        return near > CoincidentTolerance ? near : Math.Min(far, SeparationDistance);
    }

    /// <summary>The face plane's coordinate along axis <paramref name="drop"/> at (<paramref name="pu"/>, <paramref name="pw"/>).</summary>
    private static double Height(AuditFace f, int drop, int u, int w, double pu, double pw) =>
        f.Centroid[drop] - (((f.Normal[u] * (pu - f.Centroid[u])) + (f.Normal[w] * (pw - f.Centroid[w]))) / f.Normal[drop]);

    private static int Dominant(Vec3d n) => Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;

    private static (int U, int W) Others(int drop) => drop == 0 ? (1, 2) : drop == 1 ? (2, 0) : (0, 1);

    /// <summary>The common patch of two convex polygons projected onto axes (<paramref name="u"/>, <paramref name="w"/>) (Sutherland–Hodgman).</summary>
    private static List<(double U, double W)> Clip(Vec3d[] subject, Vec3d[] clip, int u, int w)
    {
        var poly = subject.Select(p => (p[u], p[w])).ToList();
        var edge = clip.Select(p => (p[u], p[w])).ToList();
        double area = 0;
        for (int i = 0; i < edge.Count; i++)
        {
            (double x0, double y0) = edge[i];
            (double x1, double y1) = edge[(i + 1) % edge.Count];
            area += (x0 * y1) - (x1 * y0);
        }

        double orient = Math.Sign(area);
        if (orient == 0)
        {
            return new List<(double, double)>();
        }

        for (int i = 0; i < edge.Count && poly.Count > 0; i++)
        {
            (double ax, double ay) = edge[i];
            (double bx, double by) = edge[(i + 1) % edge.Count];
            double Side((double X, double Y) p) => orient * (((bx - ax) * (p.Y - ay)) - ((by - ay) * (p.X - ax)));
            var next = new List<(double, double)>(poly.Count + 1);
            for (int j = 0; j < poly.Count; j++)
            {
                (double X, double Y) p = poly[j], q = poly[(j + 1) % poly.Count];
                double sp = Side(p), sq = Side(q);
                if (sp >= 0)
                {
                    next.Add(p);
                }

                if ((sp >= 0) != (sq >= 0))
                {
                    double t = sp / (sp - sq);
                    next.Add((p.X + ((q.X - p.X) * t), p.Y + ((q.Y - p.Y) * t)));
                }
            }

            poly = next;
        }

        return poly;
    }

    /// <summary>True when two convex polygons, projected along their dominant normal axis, overlap with positive area (separating-axis test).</summary>
    internal static bool Overlap(Vec3d[] a, Vec3d[] b, Vec3d n)
    {
        int drop = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
        (int u, int w) = drop == 0 ? (1, 2) : drop == 1 ? (2, 0) : (0, 1);
        foreach (Vec3d[] poly in new[] { a, b })
        {
            for (int i = 0; i < poly.Length; i++)
            {
                Vec3d p = poly[i], q = poly[(i + 1) % poly.Length];
                double ax = -(q[w] - p[w]), ay = q[u] - p[u];
                double len = Math.Sqrt((ax * ax) + (ay * ay));
                if (len < 1e-12)
                {
                    continue;
                }

                (double lo1, double hi1) = Project(a, ax / len, ay / len, u, w);
                (double lo2, double hi2) = Project(b, ax / len, ay / len, u, w);
                if (hi1 <= lo2 + OverlapTolerance || hi2 <= lo1 + OverlapTolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (double, double) Project(Vec3d[] poly, double ax, double ay, int u, int w)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (Vec3d p in poly)
        {
            double d = (p[u] * ax) + (p[w] * ay);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }

        return (lo, hi);
    }
}
