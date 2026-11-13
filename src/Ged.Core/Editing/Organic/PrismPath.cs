using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>Options of <see cref="PrismPath.Build"/> (ORGANIC-GEOMETRY-SPEC §3.3).</summary>
public sealed class PassageOptions
{
    /// <summary>The floor path: 2 to 256 points at one floor height (after rounding to 1/16 m).</summary>
    public IReadOnlyList<Vec3d> Path { get; init; } = Array.Empty<Vec3d>();

    /// <summary>The clear width, metres.</summary>
    public double Width { get; init; } = 4;

    /// <summary>The clear height, metres (rounded to 1/16 m).</summary>
    public double Height { get; init; } = 4;

    /// <summary>Textures, tile and flags (Air is forced on; Detail is the caller's choice).</summary>
    public HullBrushParams Brush { get; init; } = new();

    /// <summary>Structural brushes already in the level that every prism is audited against.</summary>
    public IReadOnlyList<AuditShape> Existing { get; init; } = Array.Empty<AuditShape>();
}

/// <summary>
/// The mitred prism path behind <c>geo.passage</c> (<c>kit.passage</c>): a clean corridor on any heading.
/// Each path point gets a ring on two VERTICAL columns, <c>P ± (w / cos(β/2))·m</c> with <c>m</c> the
/// horizontal bisector normal and <c>β</c> the turn (ends: the leg's own normal). A column's x, z are
/// snapped once and used for both the floor and the ceiling corner, so every ring is an exactly planar
/// vertical quad; each leg is the hull of its two rings — a prism with exact horizontal floor and ceiling,
/// exact vertical walls, and end faces bit-identical to its neighbours'. Consecutive prisms abut on that
/// shared face (W4), exactly like grid boxes: no overlap, no jitter. Each prism is still audited
/// (<see cref="HullAudit"/>) against the level's structural brushes and the prisms before it, so a corner or
/// wall that lands just off an existing wall is reported instead of leaking.
/// </summary>
public static class PrismPath
{
    /// <summary>The sharpest turn a passage takes at one point, degrees.</summary>
    public const double MaxTurn = 120;

    /// <summary>The shortest side wall a prism may have, metres (the hull builder's minimum extent).</summary>
    public const double MinWall = 0.05;

    /// <summary>Builds the prisms; null with <paramref name="error"/> on failure.</summary>
    public static TubeResult? Build(PassageOptions o, out GeoEditError? error)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!(o.Width > 0.05) || !(o.Width <= 1024))
        {
            error = new GeoEditError($"'width' must be greater than 0.05 and at most 1024 metres (got {OrganicPath.F(o.Width)}).", null);
            return null;
        }

        double height = OrganicPath.RoundQ(o.Height);
        if (!(height >= OrganicPath.Q) || !(height <= 1024))
        {
            error = new GeoEditError($"'height' must be 0.0625 to 1024 metres (got {OrganicPath.F(o.Height)}).", null);
            return null;
        }

        OrganicPath? path = OrganicPath.Create(o.Path, 0.25, MaxTurn, out error);
        if (path is null)
        {
            return null;
        }

        double y = path.Points[0].Y;
        for (int i = 1; i < path.Points.Length; i++)
        {
            if (path.Points[i].Y != y)
            {
                error = new GeoEditError(
                    $"path point {i + 1} is at y = {OrganicPath.F(path.Points[i].Y)} but the passage floor is at y = {OrganicPath.F(y)}; a passage is level.",
                    "Use kit.cave for a climbing path, or kit.stairs / kit.ramp between two passages.");
                return null;
            }
        }

        double w = o.Width / 2;
        int m = path.Points.Length;
        var left = new Vec3d[m];
        var right = new Vec3d[m];
        for (int i = 0; i < m; i++)
        {
            Vec3d t = path.Heading(i);
            Vec3d n = OrganicPath.Right(t);
            double off = w / Math.Cos(path.Turn(i) * Math.PI / 360);
            Vec3d p = path.Points[i];
            left[i] = new Vec3d(OrganicPath.SnapG(p.X - (off * n.X)), y, OrganicPath.SnapG(p.Z - (off * n.Z)));
            right[i] = new Vec3d(OrganicPath.SnapG(p.X + (off * n.X)), y, OrganicPath.SnapG(p.Z + (off * n.Z)));
        }

        double top = y + height;
        HullBrushParams bp = HullChain.Clone(o.Brush);
        bp.Detail = o.Brush.Detail;
        var result = new TubeResult { Length = path.Length };
        var placed = new List<AuditShape>(o.Existing);
        for (int j = 0; j < path.Legs; j++)
        {
            Vec3d u = path.Dir[j];
            double l0 = left[j + 1].Sub(left[j]).Dot(u), r0 = right[j + 1].Sub(right[j]).Dot(u);
            if (l0 < MinWall || r0 < MinWall)
            {
                double need = path.Run[j] - Math.Min(l0, r0) + MinWall;
                error = new GeoEditError(
                    $"the leg between points {j + 1} and {j + 2} is too short for its turns: the mitred corners of a {OrganicPath.F(o.Width)} m passage cross over.",
                    $"Lengthen the leg to at least {OrganicPath.F(need)} m, soften the turns, or narrow the passage.");
                return null;
            }

            var pts = new List<Vec3d>
            {
                left[j], right[j], left[j] with { Y = top }, right[j] with { Y = top },
                left[j + 1], right[j + 1], left[j + 1] with { Y = top }, right[j + 1] with { Y = top },
            };
            HullBrush.TryBuild(pts, bp, 0, out Brush? brush, out HullResult? hull, out HullBrushError? err);
            if (err is not null || brush is null || hull is null || hull.Vertices.Count != 8 || hull.Faces.Count != 6 || !CompilerFit.Fits(brush))
            {
                error = new GeoEditError($"could not build the prism between points {j + 1} and {j + 2}{(err is null ? string.Empty : $" ({err.Message})")}.",
                    "Lengthen the leg or soften the turns.");
                return null;
            }

            AuditShape shape = AuditShape.FromHull(hull, $"prism {j + 1}");
            IReadOnlyList<AuditConflict> conflicts = HullAudit.Conflicts(placed, shape);
            if (conflicts.Count == 0)
            {
                conflicts = HullAudit.Folds(placed, shape);
            }

            if (conflicts.Count > 0)
            {
                error = new GeoEditError(
                    $"the prism between points {j + 1} and {j + 2} would meet {conflicts[0].PlacedLabel} almost but not exactly ({conflicts[0]}); the compiler leaks at such near-misses.",
                    "Move the path so the passage's walls, floor and corners either line up exactly with the existing brush or stay at least 5 mm clear of it.");
                return null;
            }

            placed.Add(shape);
            result.Brushes.Add(brush);
            result.FloorFaces.Add(new[] { left[j], right[j], right[j + 1], left[j + 1] });
        }

        result.AirCount = result.Brushes.Count;
        for (int i = 0; i < m; i++)
        {
            result.Stations.Add(new TubeStation(path.Points[i], y, o.Width, height, OrganicPath.Yaw(path.Heading(i))));
        }

        result.ComputeBounds();
        return result;
    }
}
