using System;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>Grid and rotation-step snapping math shared by keyboard transforms and gizmos.</summary>
public static class TransformMath
{
    /// <summary>Snaps a scalar to the nearest multiple of <paramref name="grid"/> (no-op for grid ≤ 0).</summary>
    public static float Snap(float value, float grid) =>
        grid > 1e-6f ? MathF.Round(value / grid) * grid : value;

    /// <summary>Snaps each component of a vector to the grid.</summary>
    public static Vec3 Snap(Vec3 v, float grid) =>
        new(Snap(v.X, grid), Snap(v.Y, grid), Snap(v.Z, grid));

    /// <summary>Snaps an angle (radians) to the nearest multiple of <paramref name="stepDegrees"/>.</summary>
    public static float SnapAngle(float radians, float stepDegrees)
    {
        if (stepDegrees <= 1e-6f)
        {
            return radians;
        }

        float step = stepDegrees * MathF.PI / 180f;
        return MathF.Round(radians / step) * step;
    }

    /// <summary>The world-space unit axis for an axis index (0=X, 1=Y, 2=Z).</summary>
    public static Vec3 Axis(int axis) => axis switch
    {
        0 => new Vec3(1, 0, 0),
        1 => new Vec3(0, 1, 0),
        _ => new Vec3(0, 0, 1),
    };

    public static float DegToRad(float degrees) => degrees * MathF.PI / 180f;
}
