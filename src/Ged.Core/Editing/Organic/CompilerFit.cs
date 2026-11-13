using System;
using Ged.Core.Compiler;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>
/// How well the compiler will see a generated brush's faces. The compiler recomputes every face plane from
/// its world corners (<see cref="BrushWorld.ToWorldFaces"/> → <see cref="CsgPlane.FromPolygon"/>: Newell's
/// method in single precision on absolute world coordinates), then classifies points against those planes
/// with a 1e-4 m band. A long, thin facet far from the origin gets a plane that its own corners miss by more
/// than the band; the per-brush BSP then never stops splitting that face (the build hangs) and nearby
/// classifications go wrong (holes). Generators therefore re-draw any piece whose faces the compiler would
/// fit worse than <see cref="Tolerance"/> — measured with the compiler's own code, so the guard tracks it
/// exactly (compiler finding: tests/fixtures/organic/findings/solver-hang-thin-facet.lua).
/// </summary>
public static class CompilerFit
{
    /// <summary>The largest corner-to-plane distance accepted, metres: half the compiler's 1e-4 m band.</summary>
    public const double Tolerance = 5e-5;

    /// <summary>The largest distance of any face corner from the plane the compiler computes for that face, metres.</summary>
    public static double WorstError(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        double worst = 0;
        foreach (CsgFace f in BrushWorld.ToWorldFaces(b, 0, out _))
        {
            foreach (CsgVertex v in f.Vertices)
            {
                worst = Math.Max(worst, Math.Abs(f.Plane.Distance(v.Position)));
            }
        }

        return worst;
    }

    /// <summary>True when every face of <paramref name="b"/> is fitted within <see cref="Tolerance"/>.</summary>
    public static bool Fits(Brush b) => WorstError(b) <= Tolerance;
}
