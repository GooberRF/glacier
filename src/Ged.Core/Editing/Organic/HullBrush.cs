using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>Parameters of a convex-hull brush.</summary>
public sealed class HullBrushParams
{
    /// <summary>Air (carves) instead of solid.</summary>
    public bool Air;

    /// <summary>Detail brush (never seals or splits rooms).</summary>
    public bool Detail;

    /// <summary>Geomod can destroy it (implies detail).</summary>
    public bool Geoable;

    /// <summary>Faces emit steam.</summary>
    public bool EmitsSteam;

    /// <summary>Hit points; -1 is infinite.</summary>
    public int Life = -1;

    /// <summary>
    /// The up-facing faces' texture. Roles are already resolved for a SOLID brush: for an air brush the
    /// caller swaps floor and ceiling (the player stands on an air brush's down-facing face).
    /// </summary>
    public string Floor = BrushCreateParams.StockFloorTexture;

    /// <summary>The side faces' texture.</summary>
    public string Wall = BrushCreateParams.StockWallTexture;

    /// <summary>The down-facing faces' texture (solid-brush role; see <see cref="Floor"/>).</summary>
    public string Ceiling = BrushCreateParams.StockCeilingTexture;

    /// <summary>Metres per texture repeat of the world box map.</summary>
    public float TileMetres = GeometryUtil.DefaultTileMetres;

    /// <summary>The grid the points are snapped to: a power of two from 1/1024 to 1 metre.</summary>
    public double Snap = 1.0 / 1024;

    /// <summary>True: a point that is not a hull corner (inside, duplicate after snapping, or removed as a sliver) is an error.</summary>
    public bool Strict;
}

/// <summary>What went wrong building a hull brush.</summary>
public enum HullBrushErrorKind
{
    /// <summary>Fewer than 4 or more than 256 points.</summary>
    PointCount,

    /// <summary>A point is not finite.</summary>
    NonFinite,

    /// <summary>A point lies farther than <see cref="OrganicLimits.MaxCoordinate"/> from the origin on some axis.</summary>
    OutOfRange,

    /// <summary>Fewer than 4 distinct points after snapping.</summary>
    TooFewDistinct,

    /// <summary>The points are (nearly) coplanar.</summary>
    Flat,

    /// <summary>The hull spans more than <see cref="OrganicLimits.MaxExtent"/>.</summary>
    TooLarge,

    /// <summary>The hull has too many vertices or faces.</summary>
    OverLimits,

    /// <summary>A point is not a hull corner and <see cref="HullBrushParams.Strict"/> is set.</summary>
    Strict,

    /// <summary>The snap grid is not a power of two from 1/1024 to 1.</summary>
    BadSnap,

    /// <summary>The tile size is out of range.</summary>
    BadTile,

    /// <summary>An internal failure of the hull builder.</summary>
    Internal,
}

/// <summary>
/// A hull-brush failure. <see cref="Message"/> is a complete sentence naming points the way a Lua
/// script lists them (1-based, <c>points[i]</c>); <see cref="Hint"/> says what to change (or null).
/// </summary>
public sealed record HullBrushError(HullBrushErrorKind Kind, string Message, string? Hint);

/// <summary>
/// Builds and rebuilds convex-hull brushes on the construction grid (ORGANIC-GEOMETRY-SPEC §0.3, §1.2):
/// points are snapped, hulled exactly (<see cref="ConvexHull3"/>), and stored unrotated with the
/// position at the hull's AABB centre snapped to 1/1024 m, so every local and world vertex is an exact
/// grid value in float32. Faces get role textures by outward normal and a world box map.
/// </summary>
public static class HullBrush
{
    /// <summary>The construction grid the brush position is snapped to (1/1024 m).</summary>
    public const double Grid = ConvexHull3.Grid;

    /// <summary>Builds a new hull brush from world points (see <see cref="TryBuild(IReadOnlyList{Vec3d}, HullBrushParams, int, out Brush?, out HullResult?, out HullBrushError?)"/>).</summary>
    public static OpResult TryBuild(IReadOnlyList<Vec3d> worldPoints, HullBrushParams p, int uid,
                                    out Brush? brush, out HullResult? hull)
    {
        OpResult r = TryBuild(worldPoints, p, uid, out brush, out hull, out HullBrushError? error);
        return error is null ? r : OpResult.Fail(error.Message);
    }

    /// <summary>
    /// Builds a hull brush: snap each point to <see cref="HullBrushParams.Snap"/> → drop exact duplicates
    /// (keep the first) → exact convex hull → pool = hull vertices minus the snapped AABB centre → role
    /// textures → planes (axis-aligned faces get exact axis planes) → world box map at
    /// <see cref="HullBrushParams.TileMetres"/> → flags. <paramref name="error"/> is set on failure.
    /// <paramref name="hull"/>'s <see cref="HullResult.SourceIndex"/>, <see cref="HullResult.SimplifiedSources"/>
    /// and <see cref="HullResult.InteriorPoints"/> refer to <paramref name="worldPoints"/> (duplicates after
    /// snapping count as interior); its vertices are the snapped world positions.
    /// </summary>
    public static OpResult TryBuild(IReadOnlyList<Vec3d> worldPoints, HullBrushParams p, int uid,
                                    out Brush? brush, out HullResult? hull, out HullBrushError? error)
    {
        ArgumentNullException.ThrowIfNull(worldPoints);
        ArgumentNullException.ThrowIfNull(p);
        brush = null;
        hull = Hull(worldPoints, p.Snap, p.TileMetres, p.Strict, out error);
        if (hull is null)
        {
            return OpResult.Fail(error!.Message);
        }

        Geometry g = BuildGeometry(hull, out Vec3d centre);
        BrushFactory.ApplyOrientationTextures(g, p.Floor, p.Wall, p.Ceiling);
        var flags = new BrushCreateParams { Air = p.Air, Detail = p.Detail, Geoable = p.Geoable, EmitsSteam = p.EmitsSteam }.ToFlags();
        brush = new Brush
        {
            Uid = uid,
            Position = ToVec3(centre),
            Rotation = Mat3.Identity,
            Geometry = g,
            Flags = flags,
            Life = p.Life,
            State = BrushState.Normal,
        };
        BoxMap(brush, p.TileMetres);
        return OpResult.Ok();
    }

    /// <summary>
    /// Replaces <paramref name="existing"/>'s geometry and position in place with the hull of
    /// <paramref name="worldPoints"/> (UID, flags, life, state and tags untouched; the rotation becomes
    /// identity). Each new face takes the texture and face flags of the old face whose world normal has
    /// the largest dot product with it (ties: lowest old face index); UVs are re-box-mapped at
    /// <paramref name="tileMetres"/> — hand-authored UVs are not preserved. On failure the brush is unchanged.
    /// </summary>
    public static OpResult TryRebuild(Brush existing, IReadOnlyList<Vec3d> worldPoints, float tileMetres, double snap) =>
        TryRebuild(existing, worldPoints, tileMetres, snap, out _);

    /// <summary>
    /// <see cref="TryRebuild(Brush, IReadOnlyList{Vec3d}, float, double)"/> that also reports the structured
    /// failure (message + hint) in <paramref name="error"/>.
    /// </summary>
    public static OpResult TryRebuild(Brush existing, IReadOnlyList<Vec3d> worldPoints, float tileMetres, double snap, out HullBrushError? error)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(worldPoints);
        HullResult? hull = Hull(worldPoints, snap, tileMetres, strict: false, out error);
        if (hull is null)
        {
            return OpResult.Fail(error!.Message);
        }

        Geometry old = existing.Geometry;

        // Match faces by their OUTWARD normals (the hull's are outward); RED stores an air brush's faces
        // inward, so the old corner normals are turned outward first and the rebuilt brush is stored back
        // the same way round.
        bool storedInward = BrushOrientation.MeasureLocal(old) == BrushWinding.Inward;
        float outward = storedInward ? -1f : 1f;
        var oldNormals = old.Faces.Select(f => f.Vertices.Count >= 3
            ? ToVec3d(existing.Rotation.Transform(GeometryUtil.Normal(GeometryUtil.Corners(old, f)).Scale(outward)))
            : default).ToArray();

        Geometry g = BuildGeometry(hull, out Vec3d centre);
        g.Name = old.Name;
        g.Unknown1 = old.Unknown1;
        g.Modifiability = old.Modifiability;
        g.ModifiabilityOld = old.ModifiabilityOld;
        g.Textures.Clear();
        for (int i = 0; i < g.Faces.Count; i++)
        {
            Face face = g.Faces[i];
            Vec3d n = hull.Faces[i].Normal;
            int best = -1;
            double bestDot = double.NegativeInfinity;
            for (int k = 0; k < oldNormals.Length; k++)
            {
                double d = n.Dot(oldNormals[k]);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = k;
                }
            }

            if (best < 0)
            {
                face.Texture = GeometryUtil.EnsureTexture(g, BrushCreateParams.DefaultTexture);
                continue;
            }

            Face source = old.Faces[best];
            face.Flags = source.Flags;
            face.Texture = source.Texture >= 0 && source.Texture < old.Textures.Count
                ? GeometryUtil.EnsureTexture(g, old.Textures[source.Texture])
                : -1;
            if (old.FaceScrollData.FirstOrDefault(s => s.FaceId == source.FaceId) is { } scroll)
            {
                g.FaceScrollData.Add(new FaceScrollData { FaceId = face.FaceId, UVelocity = scroll.UVelocity, VVelocity = scroll.VVelocity });
            }
        }

        if (g.Textures.Count == 0)
        {
            g.Textures.Add(BrushCreateParams.DefaultTexture);
        }

        if (storedInward)
        {
            BrushOrientation.Reverse(g);
        }

        existing.Geometry = g;
        existing.Position = ToVec3(centre);
        existing.Rotation = Mat3.Identity;
        BoxMap(existing, tileMetres);
        return OpResult.Ok();
    }

    // ---- shared steps ---------------------------------------------------------------------------

    /// <summary>Snaps, de-duplicates, validates and hulls the points; null + <paramref name="error"/> on failure.</summary>
    internal static HullResult? Hull(IReadOnlyList<Vec3d> worldPoints, double snap, float tileMetres, bool strict, out HullBrushError? error)
    {
        error = ValidateSnap(snap) ?? ValidateTile(tileMetres);
        if (error is not null)
        {
            return null;
        }

        int n = worldPoints.Count;
        if (n < OrganicLimits.MinInputPoints || n > OrganicLimits.MaxInputPoints)
        {
            error = new HullBrushError(HullBrushErrorKind.PointCount,
                $"'points' must list {OrganicLimits.MinInputPoints} to {OrganicLimits.MaxInputPoints} points (got {n}).",
                "Split a larger shape into several convex brushes.");
            return null;
        }

        var snapped = new Vec3d[n];
        for (int i = 0; i < n; i++)
        {
            Vec3d w = worldPoints[i];
            if (!double.IsFinite(w.X) || !double.IsFinite(w.Y) || !double.IsFinite(w.Z))
            {
                error = new HullBrushError(HullBrushErrorKind.NonFinite,
                    $"points[{i + 1}] must be a vec, {{x, y, z}} or {{x=, y=, z=}} of finite numbers.", null);
                return null;
            }

            if (Math.Abs(w.X) > OrganicLimits.MaxCoordinate || Math.Abs(w.Y) > OrganicLimits.MaxCoordinate || Math.Abs(w.Z) > OrganicLimits.MaxCoordinate)
            {
                error = new HullBrushError(HullBrushErrorKind.OutOfRange,
                    $"points[{i + 1}] is {Math.Max(Math.Abs(w.X), Math.Max(Math.Abs(w.Y), Math.Abs(w.Z))).ToString("G6", CultureInfo.InvariantCulture)} m from the origin on an axis; the limit is {OrganicLimits.MaxCoordinate} m.",
                    $"Keep generated geometry within ±{OrganicLimits.MaxCoordinate} m, where every grid point is exact.");
                return null;
            }

            snapped[i] = new Vec3d(SnapValue(w.X, snap), SnapValue(w.Y, snap), SnapValue(w.Z, snap));
        }

        // Exact duplicates after snapping keep the first occurrence.
        var firstOf = new Dictionary<Vec3d, int>();
        var duplicateOf = new int[n];
        var distinct = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (firstOf.TryGetValue(snapped[i], out int j))
            {
                duplicateOf[i] = j;
            }
            else
            {
                firstOf[snapped[i]] = i;
                duplicateOf[i] = -1;
                distinct.Add(i);
            }
        }

        if (distinct.Count < OrganicLimits.MinInputPoints)
        {
            error = new HullBrushError(HullBrushErrorKind.TooFewDistinct,
                $"only {distinct.Count} distinct points remain after snapping to {FormatSnap(snap)} m.",
                "Move points further apart, or pass a finer snap.");
            return null;
        }

        double extent = 0, thinnest = double.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            double lo = distinct.Min(i => snapped[i][a]), hi = distinct.Max(i => snapped[i][a]);
            extent = Math.Max(extent, hi - lo);
            thinnest = Math.Min(thinnest, hi - lo);
        }

        if (thinnest < ConvexHull3.MinExtent)
        {
            error = FlatError(thinnest);
            return null;
        }

        if (extent > OrganicLimits.MaxExtent)
        {
            error = new HullBrushError(HullBrushErrorKind.TooLarge,
                $"the hull spans {Format(extent)} m; the limit is {OrganicLimits.MaxExtent} m.", null);
            return null;
        }

        var input = distinct.Select(i => snapped[i]).ToList();
        HullResult? hull = ConvexHull3.Build(input, new HullOptions(), out HullFailure? failure);
        if (hull is null)
        {
            error = failure!.Kind switch
            {
                HullFailureKind.Flat => FlatError(failure.Thickness),
                HullFailureKind.Sliver => new HullBrushError(HullBrushErrorKind.Flat,
                    $"the points are too thin to make a solid: {SliverMessage(failure, distinct)}", "Move points further apart, at least 0.01 m off any plane."),
                _ => new HullBrushError(HullBrushErrorKind.Internal, failure.Message, null),
            };
            return null;
        }

        if (hull.Vertices.Count > OrganicLimits.MaxHullVertices || hull.Faces.Count > OrganicLimits.MaxHullFaces)
        {
            error = new HullBrushError(HullBrushErrorKind.OverLimits,
                $"the hull has {hull.Vertices.Count} vertices and {hull.Faces.Count} faces; the limit is {OrganicLimits.MaxHullVertices} vertices / {OrganicLimits.MaxHullFaces} faces.",
                "Use fewer points, or split the shape into several brushes.");
            return null;
        }

        // Map the hull's source indices (into the distinct list) back to the caller's points.
        int[] corners = hull.SourceIndex.Select(k => distinct[k]).ToArray();
        int[] removed = hull.SimplifiedSources.Select(k => distinct[k]).ToArray();
        if (strict)
        {
            var isCorner = new HashSet<int>(corners);
            var isRemoved = new HashSet<int>(removed);
            for (int i = 0; i < n; i++)
            {
                if (isCorner.Contains(i))
                {
                    continue;
                }

                string reason = duplicateOf[i] >= 0 ? $"it repeats points[{duplicateOf[i] + 1}]"
                    : isRemoved.Contains(i) ? "it made a sliver face"
                    : "it lies inside the hull";
                error = new HullBrushError(HullBrushErrorKind.Strict,
                    $"points[{i + 1}] is not a corner of the hull ({reason}).", "Remove it, or drop strict = true.");
                return null;
            }
        }

        return hull with { SourceIndex = corners, SimplifiedSources = removed, InteriorPoints = n - corners.Length - removed.Length };
    }

    /// <summary>The local geometry of a hull: pool = vertices − the snapped AABB centre (exact), one face per hull face.</summary>
    private static Geometry BuildGeometry(HullResult hull, out Vec3d centre)
    {
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (Vec3d v in hull.Vertices)
        {
            for (int a = 0; a < 3; a++)
            {
                lo[a] = Math.Min(lo[a], v[a]);
                hi[a] = Math.Max(hi[a], v[a]);
            }
        }

        centre = new Vec3d(SnapValue((lo[0] + hi[0]) / 2, Grid), SnapValue((lo[1] + hi[1]) / 2, Grid), SnapValue((lo[2] + hi[2]) / 2, Grid));
        var g = new Geometry { Name = "hull" };
        g.Textures.Add(BrushCreateParams.DefaultTexture);
        foreach (Vec3d v in hull.Vertices)
        {
            g.Vertices.Add(ToVec3(v.Sub(centre)));
        }

        for (int i = 0; i < hull.Faces.Count; i++)
        {
            var face = new Face { Texture = 0, SurfaceIndex = -1, RoomIndex = -1, FaceId = i };
            foreach (int c in hull.Faces[i].Corners)
            {
                face.Vertices.Add(new FaceVertex { Index = c });
            }

            g.Faces.Add(face);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        for (int i = 0; i < hull.Faces.Count; i++)
        {
            Vec3d n = hull.Faces[i].Normal;
            if (Math.Abs(n.X) == 1 || Math.Abs(n.Y) == 1 || Math.Abs(n.Z) == 1)
            {
                // Axis-exact face: store the exact axis plane (the corner coordinate is an exact grid value).
                Vec3 local = g.Vertices[hull.Faces[i].Corners[0]];
                var normal = new Vec3((float)n.X, (float)n.Y, (float)n.Z);
                g.Faces[i].Plane = new RfPlane(normal, -normal.Dot(local));
            }
        }

        return g;
    }

    /// <summary>World box map of every textured face at <paramref name="tileMetres"/> (the <c>brush:box_map{tile=}</c> mapping).</summary>
    private static void BoxMap(Brush b, float tileMetres)
    {
        Geometry g = b.Geometry;
        UvOps.BoxMap(g, Enumerable.Range(0, g.Faces.Count).Where(i => g.Faces[i].Texture >= 0 && !g.Faces[i].IsPortalFace),
            b.Rotation, b.Position, 1f / tileMetres, 1, 1);
    }

    // ---- validation and formatting --------------------------------------------------------------

    /// <summary>Null when <paramref name="snap"/> is a power of two from 1/1024 to 1, else the error (hint: the nearest valid value).</summary>
    public static HullBrushError? ValidateSnap(double snap)
    {
        if (double.IsFinite(snap) && snap > 0)
        {
            double k = Math.Log2(snap);
            if (k >= -10 && k <= 0 && Math.Round(k) == k && Math.ScaleB(1.0, (int)k) == snap)
            {
                return null;
            }
        }

        double nearest = !double.IsFinite(snap) || snap <= 0 ? OrganicLimits.MinSnap
            : Math.ScaleB(1.0, (int)Math.Clamp(Math.Round(Math.Log2(snap)), -10, 0));
        return new HullBrushError(HullBrushErrorKind.BadSnap,
            $"'snap' must be a power of two from 1/1024 to 1 (got {Format(snap)}).", $"Nearest valid value: {FormatSnap(nearest)}.");
    }

    /// <summary>Null when <paramref name="tile"/> is within 0.25 to 64 metres, else the error (hint: the nearest valid value).</summary>
    public static HullBrushError? ValidateTile(double tile)
    {
        if (double.IsFinite(tile) && tile >= OrganicLimits.MinTileMetres && tile <= OrganicLimits.MaxTileMetres)
        {
            return null;
        }

        double nearest = double.IsFinite(tile) ? Math.Clamp(tile, OrganicLimits.MinTileMetres, OrganicLimits.MaxTileMetres) : GeometryUtil.DefaultTileMetres;
        return new HullBrushError(HullBrushErrorKind.BadTile,
            $"'tile' must be between {Format(OrganicLimits.MinTileMetres)} and {Format(OrganicLimits.MaxTileMetres)} metres (got {Format(tile)}).",
            $"Nearest valid value: {Format(nearest)}.");
    }

    /// <summary>The hull's sliver failure in the caller's terms: its corner indices are into the de-duplicated
    /// point list, so they map back to the caller's 1-based <c>points[i]</c>.</summary>
    internal static string SliverMessage(HullFailure failure, List<int> distinct)
    {
        string[] names = failure.SourceIndices.Where(k => k >= 0 && k < distinct.Count).Select(k => $"points[{distinct[k] + 1}]").ToArray();
        if (names.Length != 3)
        {
            return "a face of the hull is a sliver.";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{names[0]}, {names[1]} and {names[2]} make a sliver face {failure.Thickness:0.######} m thin (the minimum is {new HullOptions().MinAltitude:0.######} m).");
    }

    private static HullBrushError FlatError(double thinnest) =>
        new(HullBrushErrorKind.Flat,
            $"the points are coplanar (thinnest extent {Format(thinnest)} m); a brush needs volume.",
            $"Add a point off the plane, at least {Format(ConvexHull3.MinExtent)} m away.");

    /// <summary>Rounds to the nearest multiple of <paramref name="step"/> (halves away from zero).</summary>
    public static double SnapValue(double v, double step) => (Math.Round(v / step, MidpointRounding.AwayFromZero) * step) + 0.0; // + 0.0: no negative zero

    /// <summary>A snap grid as written in messages: "1/1024" for fractions, else the number.</summary>
    public static string FormatSnap(double snap) =>
        snap > 0 && snap < 1 && Math.Round(1 / snap) == 1 / snap ? $"1/{(long)(1 / snap)}" : Format(snap);

    private static string Format(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    private static Vec3 ToVec3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static Vec3d ToVec3d(Vec3 v) => new(v.X, v.Y, v.Z);
}
