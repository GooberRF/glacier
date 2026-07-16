using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Brush-level transforms: translate, rotate about a pivot, snap to grid,
/// reorient (bake rotation), move the origin without moving geometry, and
/// numeric stretch. World geometry is <c>pos + rot.Transform(local)</c>, so a
/// transform touches either the stored pose or the local pool as appropriate.
/// All mutate the brush in place; callers wrap them in undo commands.
/// </summary>
public static class BrushTransform
{
    /// <summary>The world-space position of a brush's local vertex.</summary>
    public static Vec3 WorldVertex(Brush b, Vec3 local) => b.Position.Add(b.Rotation.Transform(local));

    /// <summary>The world-space centroid of the brush's geometry.</summary>
    public static Vec3 WorldCentroid(Brush b) => WorldVertex(b, GeometryUtil.Centroid(b.Geometry.Vertices));

    /// <summary>Translates the brush by a world delta.</summary>
    public static void Move(Brush b, Vec3 worldDelta) => b.Position = b.Position.Add(worldDelta);

    /// <summary>Snaps the brush origin to the grid.</summary>
    public static void SnapPositionToGrid(Brush b, float grid) => b.Position = TransformMath.Snap(b.Position, grid);

    /// <summary>
    /// Snaps every brush vertex so its world position lands on the grid, storing
    /// the result back in local space (stock Ctrl+G).
    /// </summary>
    public static void SnapVerticesToGrid(Brush b, float grid)
    {
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            Vec3 world = WorldVertex(b, b.Geometry.Vertices[i]);
            Vec3 snapped = TransformMath.Snap(world, grid);
            b.Geometry.Vertices[i] = b.Rotation.InverseTransform(snapped.Sub(b.Position));
        }

        GeometryUtil.RecomputeAllPlanes(b.Geometry);
    }

    /// <summary>Rotates the brush about a world pivot by a world rotation.</summary>
    public static void RotateAboutPivot(Brush b, Mat3 rotation, Vec3 pivot)
    {
        b.Position = pivot.Add(rotation.Transform(b.Position.Sub(pivot)));
        b.Rotation = Mat3Math.Compose(rotation, b.Rotation).Orthonormalize();
    }

    /// <summary>Rotates the brush about its own origin.</summary>
    public static void Rotate(Brush b, Mat3 rotation) => RotateAboutPivot(b, rotation, b.Position);

    /// <summary>
    /// Bakes the current rotation into local geometry and resets the stored
    /// rotation to identity, keeping the brush in place (stock O reorient: the
    /// brush's frame realigns to the world axes without the shape moving).
    /// </summary>
    public static void Reorient(Brush b)
    {
        Mat3 rot = b.Rotation;
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            b.Geometry.Vertices[i] = rot.Transform(b.Geometry.Vertices[i]);
        }

        b.Rotation = Mat3.Identity;
        GeometryUtil.RecomputeAllPlanes(b.Geometry);
    }

    /// <summary>
    /// Moves the brush origin to <paramref name="newWorldOrigin"/> while keeping
    /// the geometry fixed in world space (stock Ctrl+D move centers).
    /// </summary>
    public static void MoveCenter(Brush b, Vec3 newWorldOrigin)
    {
        Vec3 shiftLocal = b.Rotation.InverseTransform(b.Position.Sub(newWorldOrigin));
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            b.Geometry.Vertices[i] = b.Geometry.Vertices[i].Add(shiftLocal);
        }

        b.Position = newWorldOrigin;
        GeometryUtil.RecomputeAllPlanes(b.Geometry);
    }

    /// <summary>Recenters the brush origin onto its geometry centroid (geometry unmoved).</summary>
    public static void RecenterToCentroid(Brush b) => MoveCenter(b, WorldCentroid(b));

    /// <summary>The current local bounding-box size (Width=X, Height=Y, Depth=Z).</summary>
    public static Vec3 Dimensions(Brush b)
    {
        Aabb bounds = GeometryUtil.LocalBounds(b.Geometry);
        return bounds.P2.Sub(bounds.P1);
    }

    /// <summary>
    /// Numeric stretch: rescales the local geometry about its bounding-box centre
    /// so the brush's dimensions become (w, h, d). Zero targets leave that axis
    /// unchanged; degenerate current extents are left alone.
    /// </summary>
    public static void StretchToDimensions(Brush b, float w, float h, float d)
    {
        Aabb bounds = GeometryUtil.LocalBounds(b.Geometry);
        Vec3 size = bounds.P2.Sub(bounds.P1);
        Vec3 centre = bounds.P1.Add(size.Scale(0.5f));
        var factor = new Vec3(
            Factor(size.X, w),
            Factor(size.Y, h),
            Factor(size.Z, d));
        ScaleLocalAbout(b, factor, centre);
    }

    /// <summary>Multiplies the local geometry by a per-axis factor about its bounding-box centre.</summary>
    public static void ScaleBy(Brush b, Vec3 factor)
    {
        Aabb bounds = GeometryUtil.LocalBounds(b.Geometry);
        Vec3 centre = bounds.P1.Add(bounds.P2.Sub(bounds.P1).Scale(0.5f));
        ScaleLocalAbout(b, factor, centre);
    }

    private static void ScaleLocalAbout(Brush b, Vec3 factor, Vec3 centre)
    {
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            Vec3 rel = b.Geometry.Vertices[i].Sub(centre);
            b.Geometry.Vertices[i] = centre.Add(new Vec3(rel.X * factor.X, rel.Y * factor.Y, rel.Z * factor.Z));
        }

        GeometryUtil.RecomputeAllPlanes(b.Geometry);
        GeometryUtil.AssignAllPlanarUv(b.Geometry);
    }

    private static float Factor(float current, float target) =>
        target > 1e-4f && current > 1e-4f ? target / current : 1f;

    /// <summary>The world-space pivot (centroid of centroids) for a multi-brush selection.</summary>
    public static Vec3 SelectionPivot(IReadOnlyList<Brush> brushes)
    {
        if (brushes.Count == 0)
        {
            return default;
        }

        var sum = new Vec3(0, 0, 0);
        foreach (Brush b in brushes)
        {
            sum = sum.Add(WorldCentroid(b));
        }

        return sum.Scale(1f / brushes.Count);
    }
}
