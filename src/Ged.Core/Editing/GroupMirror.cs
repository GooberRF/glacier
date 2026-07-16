using System;
using System.Reflection;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Alpine group Mirror (X/Y/Z): reflects brushes and objects together across the
/// axis-perpendicular plane through a shared pivot. Brush geometry is reflected in
/// world space and re-oriented outward so face plane normals stay valid; object
/// positions and orientation matrices are reflected so lights re-sync and
/// directional frames mirror correctly. Pure — the service wraps it in undo.
/// </summary>
public static class GroupMirror
{
    /// <summary>Reflects a world point across the plane through <paramref name="pivot"/> perpendicular to <paramref name="axis"/> (0=X,1=Y,2=Z).</summary>
    public static Vec3 ReflectPoint(Vec3 p, Vec3 pivot, int axis) =>
        p.WithComponent(axis, (2f * pivot.Component(axis)) - p.Component(axis));

    /// <summary>Reflects a world direction (negates the axis component; no translation).</summary>
    public static Vec3 ReflectDirection(Vec3 v, int axis) =>
        v.WithComponent(axis, -v.Component(axis));

    /// <summary>Reflects an orientation frame by reflecting each of its world basis rows.</summary>
    public static Mat3 ReflectFrame(Mat3 m, int axis) => new(
        ReflectDirection(m.Forward, axis),
        ReflectDirection(m.Right, axis),
        ReflectDirection(m.Up, axis));

    /// <summary>
    /// Mirrors a brush across the pivot plane: reflects every world vertex, moves
    /// the origin to its reflection, bakes the reflection into local geometry, then
    /// re-orients faces outward and recomputes planes/UVs.
    /// </summary>
    public static void MirrorBrush(Brush b, Vec3 pivot, int axis)
    {
        ArgumentNullException.ThrowIfNull(b);
        Vec3 newPos = ReflectPoint(b.Position, pivot, axis);
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            Vec3 world = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[i]));
            Vec3 reflected = ReflectPoint(world, pivot, axis);
            b.Geometry.Vertices[i] = b.Rotation.InverseTransform(reflected.Sub(newPos));
        }

        b.Position = newPos;
        BrushFactory.OrientOutward(b.Geometry);
        GeometryUtil.RecomputeAllPlanes(b.Geometry);
        GeometryUtil.AssignAllPlanarUv(b.Geometry);
    }

    /// <summary>
    /// Mirrors an object model: reflects its <c>Position</c> and, when present, its
    /// <c>Rotation</c>/<c>Orientation</c> matrix (handling the nullable event case).
    /// </summary>
    public static void MirrorObjectModel(object model, Vec3 pivot, int axis)
    {
        ArgumentNullException.ThrowIfNull(model);
        Type t = model.GetType();

        if (t.GetProperty("Position") is { CanRead: true, CanWrite: true } posProp &&
            posProp.PropertyType == typeof(Vec3))
        {
            var pos = (Vec3)posProp.GetValue(model)!;
            posProp.SetValue(model, ReflectPoint(pos, pivot, axis));
        }

        ReflectRotationProperty(model, t, "Rotation", axis);
        ReflectRotationProperty(model, t, "Orientation", axis);
    }

    private static void ReflectRotationProperty(object model, Type t, string name, int axis)
    {
        PropertyInfo? prop = t.GetProperty(name);
        if (prop is null || !prop.CanRead || !prop.CanWrite)
        {
            return;
        }

        if (prop.PropertyType == typeof(Mat3))
        {
            var m = (Mat3)prop.GetValue(model)!;
            prop.SetValue(model, ReflectFrame(m, axis));
        }
        else if (prop.PropertyType == typeof(Mat3?))
        {
            if (prop.GetValue(model) is Mat3 m)
            {
                prop.SetValue(model, ReflectFrame(m, axis));
            }
        }
    }
}
