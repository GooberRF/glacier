using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Editing.Organic;

/// <summary>
/// Clears a near-flat fold (<see cref="HullAudit.Folds"/>, <see cref="HullAudit.SelfFolds"/>) from a hull built
/// between a fixed ring and a ring that is still being placed: one corner of the placed ring that a folded face
/// owns moves outward, in whole grid steps, just far enough that the fold opens past
/// <see cref="HullAudit.FoldDegrees"/> with a margin. Moving a corner outward only grows a convex hull, so the
/// hull stays convex and keeps its floor and headroom; the caller rebuilds and checks it again.
/// </summary>
internal static class FoldRepair
{
    /// <summary>The fold angle a repair aims for, degrees (a margin over <see cref="HullAudit.FoldDegrees"/>).</summary>
    private const double TargetDegrees = HullAudit.FoldDegrees + 1;

    /// <summary>The farthest one repair may move a corner, metres.</summary>
    private const double MaxMove = 0.25;

    /// <summary>
    /// Moves one corner of <paramref name="ring"/> to open <paramref name="fold"/> (found on
    /// <paramref name="shape"/>, the hull being checked, against <paramref name="placed"/> or itself).
    /// <paramref name="steps"/> gives each movable corner's unit moves (exact grid vectors; none for a corner that
    /// must stay). Of every corner of the two folded faces that the ring owns, the move of least length that opens
    /// the fold to the target, moving outward of the face(s) the corner belongs to, wins. False when none does.
    /// </summary>
    public static bool Push(List<Vec3d> ring, AuditConflict fold, AuditShape shape, IReadOnlyList<AuditShape> placed, Func<int, IReadOnlyList<Vec3d>> steps)
    {
        AuditFace mine = shape.Faces[fold.CandidateFace];
        AuditFace other = fold.PlacedIndex < 0 ? shape.Faces[fold.PlacedFace] : placed[fold.PlacedIndex].Faces[fold.PlacedFace];
        bool self = fold.PlacedIndex < 0;
        double target = Math.Cos(TargetDegrees * Math.PI / 180);

        int bestIndex = -1;
        Vec3d bestPoint = default;
        double bestLength = double.PositiveInfinity;
        for (int i = 0; i < ring.Count; i++)
        {
            Vec3d p = ring[i];
            bool inMine = Array.IndexOf(mine.Points, p) >= 0;
            bool inOther = self && Array.IndexOf(other.Points, p) >= 0;
            if (!inMine && !inOther)
            {
                continue;
            }

            // Outward: along the normal of the face(s) the corner belongs to.
            Vec3d outward = inMine && inOther ? mine.Normal.Add(other.Normal) : inMine ? mine.Normal : other.Normal;
            foreach (Vec3d s in steps(i))
            {
                if (s.Dot(outward) <= 1e-9)
                {
                    continue;
                }

                int most = (int)Math.Floor(MaxMove / s.Length);
                for (int k = 1; k <= most; k++)
                {
                    double length = s.Length * k;
                    if (length >= bestLength)
                    {
                        break;
                    }

                    var moved = new Vec3d(p.X + (s.X * k), p.Y + (s.Y * k), p.Z + (s.Z * k));
                    Vec3d na = inMine ? Normal(mine.Points, p, moved, mine.Normal) : mine.Normal;
                    Vec3d nb = inOther ? Normal(other.Points, p, moved, other.Normal) : other.Normal;
                    if (Math.Abs(na.Dot(nb)) <= target)
                    {
                        bestIndex = i;
                        bestPoint = moved;
                        bestLength = length;
                        break;
                    }
                }
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        ring[bestIndex] = bestPoint;
        return true;
    }

    /// <summary>The unit normal of <paramref name="face"/> with corner <paramref name="from"/> moved to <paramref name="to"/>, oriented like <paramref name="like"/>.</summary>
    private static Vec3d Normal(Vec3d[] face, Vec3d from, Vec3d to, Vec3d like)
    {
        Vec3d[] pts = face.Select(q => q == from ? to : q).ToArray();
        (Vec3d n, _) = ConvexHull3.NewellPlane(pts, Enumerable.Range(0, pts.Length).ToArray());
        return n.Dot(like) < 0 ? n.Scale(-1) : n;
    }
    /// <summary>
    /// A ring corner that turns the ring's outline by less than this (degrees) is nearly in line with its
    /// neighbours: every hull built on that ring then folds near-flat across it, whatever the other ring does.
    /// </summary>
    public const double MinRingTurnDegrees = 3;

    /// <summary>
    /// The smallest turn (degrees) at a corner of the convex outline of a planar ring given in its own 2-D frame
    /// (points inside the outline are not corners of any hull built on the ring, so they do not count).
    /// </summary>
    public static double SmallestTurn(IReadOnlyList<(double U, double V)> ring)
    {
        var pts = ring.Distinct().Select(p => new Vec3d(p.U, p.V, 0)).ToList();
        List<Vec3d> hull = HullDeform.ConvexCorners(pts, 0, 1);
        if (hull.Count < 3)
        {
            return 0;
        }

        double least = 180;
        for (int i = 0; i < hull.Count; i++)
        {
            Vec3d a = hull[(i + hull.Count - 1) % hull.Count], p = hull[i], b = hull[(i + 1) % hull.Count];
            double ax = p.X - a.X, ay = p.Y - a.Y, bx = b.X - p.X, by = b.Y - p.Y;
            double turn = Math.Abs(Math.Atan2((ax * by) - (ay * bx), (ax * bx) + (ay * by))) * 180 / Math.PI;
            least = Math.Min(least, turn);
        }

        return least;
    }
}
