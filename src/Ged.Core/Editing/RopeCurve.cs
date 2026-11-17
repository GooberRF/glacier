using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The resting shape of an Alpine rope: a catenary between the emitter and its target, a straight line when the rope
/// is no longer than the span, a fold straight down and back up when the anchors are vertically aligned, or a dangle
/// hanging straight down its own length when it has no target. Reimplemented from the behaviour of Alpine's shared
/// solver so the viewport shows what the game seeds its rope from (common/include/common/rope_curve.h:118-354;
/// the editor preview's rope length is span + slack, or the dangle length without a target, floored at 0.01 —
/// editor_patch/rope_emitter.cpp:506-533).
/// </summary>
public static class RopeCurve
{
    private const double MaxRopeLength = 100000.0;
    private const double MinHorizontalDistance = 1.0e-4;
    private const double TautEpsilon = 1.0e-5;
    private const float MinDangle = 0.01f;

    /// <summary>
    /// <paramref name="points"/> + 1 positions spaced evenly along the rope (Alpine draws <c>segments</c> + 1 points,
    /// rope_emitter.cpp:530-533). <paramref name="target"/> is the target's position, or null for a dangle.
    /// </summary>
    public static IReadOnlyList<Vec3> Polyline(Vec3 emitter, Vec3? target, float slack, float dangleLength, int segments)
    {
        int count = Math.Clamp(segments, 2, 64) + 1;
        float length;
        Vec3 b;
        if (target is { } t)
        {
            b = t;
            length = t.Sub(emitter).Length() + slack;
        }
        else
        {
            length = float.IsFinite(dangleLength) && dangleLength > 0f ? MathF.Min(dangleLength, (float)MaxRopeLength) : 0f;
            b = new Vec3(emitter.X, emitter.Y - length, emitter.Z);
        }

        if (!float.IsFinite(length) || length <= 0f)
        {
            length = MinDangle;
        }

        Curve c = Solve(emitter, b, length);
        var points = new List<Vec3>(count);
        for (int i = 0; i < count; i++)
        {
            double s = i == count - 1 ? c.Length : c.Length * i / (count - 1);
            points.Add(SampleAtArcLength(c, s));
        }

        return points;
    }

    private enum Kind
    {
        Taut,
        Vertical,
        Catenary,
    }

    private readonly record struct Curve(
        Vec3 A, Vec3 B, Kind Kind, double Length, Vec3 HorizDir, double Param, double U, double Phi, double FoldBottom);

    private static Curve Solve(Vec3 a, Vec3 b, float ropeLength)
    {
        double dx = (double)b.X - a.X;
        double dy = (double)b.Y - a.Y;
        double dz = (double)b.Z - a.Z;
        double horiz = Math.Sqrt((dx * dx) + (dz * dz));
        double span = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        double len = Math.Clamp(float.IsFinite(ropeLength) ? ropeLength : 0.0, 0.0, MaxRopeLength);
        Curve taut = new(a, b, Kind.Taut, span, default, 0, 0, 0, 0);
        if (!(len > span + TautEpsilon))
        {
            return taut;
        }

        if (horiz < MinHorizontalDistance)
        {
            double bottom = Math.Min(a.Y, b.Y) - ((len - Math.Abs(dy)) * 0.5);
            return double.IsFinite(bottom) ? taut with { Kind = Kind.Vertical, Length = len, FoldBottom = bottom } : taut;
        }

        double s = Math.Sqrt(Math.Max((len * len) - (dy * dy), 0.0));
        if (!(s > horiz))
        {
            return taut;
        }

        double u = SolveU(s / horiz);
        double param = u > 0.0 ? horiz / (2.0 * u) : 0.0;
        double phi = Math.Asinh(dy / s);
        if (!(param > 0.0) || !double.IsFinite(param) || !double.IsFinite(phi) || !double.IsFinite(u))
        {
            return taut;
        }

        return new Curve(a, b, Kind.Catenary, len, new Vec3((float)(dx / horiz), 0f, (float)(dz / horiz)), param, u, phi, 0);
    }

    private static Vec3 SampleAtArcLength(Curve c, double s)
    {
        if (!(c.Length > 0.0))
        {
            return c.A;
        }

        double sc = Math.Clamp(s, 0.0, c.Length);
        switch (c.Kind)
        {
            case Kind.Taut:
                return Vec3Math.Lerp(c.A, c.B, (float)(sc / c.Length));

            case Kind.Vertical:
            {
                double down = c.A.Y - c.FoldBottom;
                double y = sc <= down ? c.A.Y - sc : c.FoldBottom + (sc - down);
                float t = (float)(sc / c.Length);
                return new Vec3(c.A.X + ((c.B.X - c.A.X) * t), (float)y, c.A.Z + ((c.B.Z - c.A.Z) * t));
            }

            default:
            {
                double inner = (sc / c.Param) + Math.Sinh(c.Phi - c.U);
                double p = 0.5 * (c.U - c.Phi + Math.Asinh(inner));
                if (!double.IsFinite(p))
                {
                    p = 0.0;
                }

                p = Math.Clamp(p, 0.0, c.U);
                double q = 2.0 * c.Param * Math.Sinh(p);
                double x = 2.0 * c.Param * p;
                double y = q * Math.Sinh(p - c.U + c.Phi);
                var r = new Vec3((float)(c.A.X + (c.HorizDir.X * x)), (float)(c.A.Y + y), (float)(c.A.Z + (c.HorizDir.Z * x)));
                return float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Z) ? r : c.A;
            }
        }
    }

    /// <summary>Solves sinh(u)/u = r for u &gt; 0 by bracketed Newton (rope_curve.h:139-181).</summary>
    private static double SolveU(double r)
    {
        const double uMax = 40.0;
        if (!(r > 1.0) || !double.IsFinite(r))
        {
            return 0.0;
        }

        double hi = 1.0;
        while (hi < uMax && SinhOverU(hi) < r)
        {
            hi *= 2.0;
        }

        hi = Math.Min(hi, uMax);
        if (SinhOverU(hi) < r)
        {
            return hi;
        }

        double lo = 0.0;
        double u = 0.5 * (lo + hi);
        for (int i = 0; i < 80; i++)
        {
            double f = SinhOverU(u) - r;
            if (f > 0.0)
            {
                hi = u;
            }
            else
            {
                lo = u;
            }

            double d = DSinhOverU(u);
            double next = d > 0.0 ? u - (f / d) : 0.5 * (lo + hi);
            if (!double.IsFinite(next) || !(next > lo) || !(next < hi))
            {
                next = 0.5 * (lo + hi);
            }

            double delta = Math.Abs(next - u);
            u = next;
            if (delta <= 1.0e-12 * Math.Max(1.0, Math.Abs(u)))
            {
                break;
            }
        }

        return u;
    }

    private static double SinhOverU(double u)
    {
        if (Math.Abs(u) < 1.0e-4)
        {
            double u2 = u * u;
            return 1.0 + (u2 / 6.0) + (u2 * u2 / 120.0);
        }

        return Math.Sinh(u) / u;
    }

    private static double DSinhOverU(double u)
    {
        if (Math.Abs(u) < 1.0e-3)
        {
            return (u / 3.0) + (u * u * u / 30.0);
        }

        return ((u * Math.Cosh(u)) - Math.Sinh(u)) / (u * u);
    }
}
