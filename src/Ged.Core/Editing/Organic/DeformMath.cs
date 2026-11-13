using System;

namespace Ged.Core.Editing.Organic;

/// <summary>
/// The world-space, double-precision deformers behind <c>geo.twist</c>, <c>geo.bend</c> and
/// <c>geo.taper</c> in hull mode (ORGANIC-GEOMETRY-SPEC §2.3). They use the same formulas as the
/// editor's <see cref="Deformers"/> (twist ramps a rotation about the axis, bend wraps the length
/// axis into an arc, taper scales the cross-section linearly), but every parameter is fixed per
/// brush — the axis runs through the brush's world bounds centre and the ramp spans its world
/// extent — so the same point maps to bit-identical output in every piece that contains it.
/// </summary>
public sealed class DeformMath
{
    private DeformMath(int axis, int toward, double lo, double span, Vec3d centre, double degrees, double scale, DeformKind kind)
    {
        Axis = axis;
        Toward = toward;
        Lo = lo;
        Span = span;
        Centre = centre;
        Degrees = degrees;
        Scale = scale;
        Kind = kind;
        if (kind == DeformKind.Bend)
        {
            BendRadius = span / (degrees * Math.PI / 180.0);
        }
    }

    /// <summary>Which deformer this is.</summary>
    public DeformKind Kind { get; }

    /// <summary>The deform (twist / taper) or length (bend) axis: 0 = x, 1 = y, 2 = z.</summary>
    public int Axis { get; }

    /// <summary>The axis a bend curls toward (bend only; -1 otherwise).</summary>
    public int Toward { get; }

    /// <summary>The low end of the brush's world extent along <see cref="Axis"/>.</summary>
    public double Lo { get; }

    /// <summary>The brush's world extent along <see cref="Axis"/> (&gt; 0).</summary>
    public double Span { get; }

    /// <summary>The brush's world bounds centre (the deform axis runs through it).</summary>
    public Vec3d Centre { get; }

    /// <summary>Twist: rotation at the high end; bend: total arc. Degrees.</summary>
    public double Degrees { get; }

    /// <summary>Taper: cross-section scale at the high end (1 at the low end).</summary>
    public double Scale { get; }

    /// <summary>Bend: the radius of the neutral line (signed: negative curls the other way).</summary>
    public double BendRadius { get; }

    /// <summary>A twist about <paramref name="axis"/>: 0° at the low end, <paramref name="degrees"/> at the high end.</summary>
    public static DeformMath Twist(int axis, double lo, double span, Vec3d centre, double degrees) =>
        new(axis, -1, lo, span, centre, degrees, 1, DeformKind.Twist);

    /// <summary>A bend of the <paramref name="axis"/> extent into an arc of <paramref name="degrees"/> curling toward <paramref name="toward"/>.</summary>
    public static DeformMath Bend(int axis, int toward, double lo, double span, Vec3d centre, double degrees) =>
        new(axis, toward, lo, span, centre, degrees, 1, DeformKind.Bend);

    /// <summary>A taper along <paramref name="axis"/>: scale 1 at the low end, <paramref name="scale"/> at the high end.</summary>
    public static DeformMath Taper(int axis, double lo, double span, Vec3d centre, double scale) =>
        new(axis, -1, lo, span, centre, 0, scale, DeformKind.Taper);

    /// <summary>Deforms one world point.</summary>
    public Vec3d Apply(Vec3d p)
    {
        double[] v = { p.X, p.Y, p.Z };
        double[] c = { Centre.X, Centre.Y, Centre.Z };
        double t = (v[Axis] - Lo) / Span;
        switch (Kind)
        {
            case DeformKind.Twist:
            {
                // Right-handed rotation about +axis (Mat3Math.FromAxisAngle's convention, as Deformers.Twist).
                double angle = Degrees * t * Math.PI / 180.0;
                (int u, int w) = Others(Axis);
                double du = v[u] - c[u], dw = v[w] - c[w];
                double cos = Math.Cos(angle), sin = Math.Sin(angle);

                // Rodrigues about e_axis: for axis y, (x, z) → (c·x + s·z, −s·x + c·z); the cyclic
                // pair (u, w) = (z, x) for y, (y, z) for x, (x, y) for z gives the same right-handed turn.
                v[u] = c[u] + (cos * du) - (sin * dw);
                v[w] = c[w] + (sin * du) + (cos * dw);
                break;
            }

            case DeformKind.Taper:
            {
                double s = 1 + ((Scale - 1) * t);
                for (int a = 0; a < 3; a++)
                {
                    if (a != Axis)
                    {
                        v[a] = c[a] + ((v[a] - c[a]) * s);
                    }
                }

                break;
            }

            case DeformKind.Bend:
            {
                // Deformers.Bend's arc: s = arc length from the middle, h = offset from the neutral line.
                double mid = Lo + (Span / 2);
                double arc = v[Axis] - mid;
                double h = v[Toward] - c[Toward];
                double angle = arc / BendRadius;
                double r = BendRadius - h;
                v[Axis] = mid + (r * Math.Sin(angle));
                v[Toward] = c[Toward] + BendRadius - (r * Math.Cos(angle));
                break;
            }
        }

        return new Vec3d(v[0], v[1], v[2]);
    }

    /// <summary>The two axes other than <paramref name="axis"/>, ordered so (axis, u, w) is cyclic (right-handed).</summary>
    internal static (int U, int W) Others(int axis) => axis switch
    {
        0 => (1, 2),
        1 => (2, 0),
        _ => (0, 1),
    };
}

/// <summary>The hull-mode deformers.</summary>
public enum DeformKind
{
    /// <summary><c>geo.twist</c>.</summary>
    Twist,

    /// <summary><c>geo.bend</c>.</summary>
    Bend,

    /// <summary><c>geo.taper</c>.</summary>
    Taper,
}
