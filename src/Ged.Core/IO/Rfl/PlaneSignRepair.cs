using System;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl;

/// <summary>
/// Load-time defect repair for mover face-plane offsets whose sign was inverted by an earlier
/// Glacier build.
/// <para>
/// Red Faction's stored-plane convention is <c>Normal·X + Offset == 0</c>, i.e.
/// <c>Offset = -(Normal·pointOnPlane)</c>. An earlier revision of the editor's raw-brush
/// plane routine stored <c>+(Normal·pointOnPlane)</c>. RF builds a mover's collision hull
/// directly from these authored face planes (it renders from the vertices but classifies
/// collision space with the stored plane), so a mover level saved with the inverted sign has a
/// corrupt collision hull even though the mover animates correctly. Because Glacier preserves
/// planes read from disk, such a level would keep the corruption across a load/re-save in a
/// fixed build unless the inverted planes are corrected on load. This runs on the movers section
/// (<see cref="Sections.MoversSection"/>); static brushes are intentionally left untouched — RF
/// never reads their stored planes (the CSG compiler recomputes them), so a stale sign there is
/// inert, and rewriting it would break byte-identity for levels this editor already saved.
/// </para>
/// <para>
/// The predicate is a general, self-verifying defect test, not a level- or version-specific
/// hack: for each face it recomputes both candidate offsets from the face's own geometry and
/// only rewrites a plane that <b>definitely</b> matches the inverted convention (stored offset
/// sits on <c>+(n·c)</c> and is far from <c>-(n·c)</c>). RED-authored planes already satisfy
/// <c>-(n·v)</c>, so the predicate never fires on them and their bytes round-trip unchanged.
/// Near-origin faces (where <c>+(n·c) ≈ -(n·c) ≈ 0</c>) are sign-ambiguous but inherently
/// harmless, so they are left untouched.
/// </para>
/// </summary>
public static class PlaneSignRepair
{
    /// <summary>
    /// Minimum separation <c>|(+n·c) − (−n·c)| = 2|n·c|</c> (metres) for a face to be considered
    /// unambiguously signed. Below this the plane passes near the local origin and either sign is
    /// harmless, so it is left exactly as stored (preserves byte-identity of near-origin faces).
    /// </summary>
    private const float MinSeparation = 2e-3f;

    /// <summary>Absolute float-noise cushion added to the relative match band.</summary>
    private const float MatchBand = 1e-4f;

    /// <summary>
    /// Corrects any definitely sign-inverted face planes in one brush/mover geometry.
    /// Returns the number of faces whose plane was rewritten (0 for RED-authored geometry).
    /// </summary>
    public static int RepairBrushGeometry(Geometry g)
    {
        ArgumentNullException.ThrowIfNull(g);
        int repaired = 0;
        foreach (Face f in g.Faces)
        {
            if (TryRepairFace(g, f))
            {
                repaired++;
            }
        }

        return repaired;
    }

    private static bool TryRepairFace(Geometry g, Face f)
    {
        if (f.Vertices.Count < 3)
        {
            return false;
        }

        Vec3 n = f.Plane.Normal;
        if (n.LengthSquared() < 1e-12f)
        {
            return false; // degenerate normal — nothing meaningful to test
        }

        // Reference point = the face-corner centroid, matching GeometryUtil.RecomputePlane.
        var sum = new Vec3(0, 0, 0);
        foreach (FaceVertex fv in f.Vertices)
        {
            if (fv.Index < 0 || fv.Index >= g.Vertices.Count)
            {
                return false; // malformed index — do not touch this face
            }

            sum = sum.Add(g.Vertices[fv.Index]);
        }

        Vec3 c = sum.Scale(1f / f.Vertices.Count);
        float ndotc = n.Dot(c);
        float offGood = -ndotc; // RF convention: Normal·X + Offset == 0
        float offBad = ndotc;   // the inverted sign an old Glacier build wrote
        float separation = MathF.Abs(offGood - offBad); // == 2|n·c|
        if (separation <= MinSeparation)
        {
            return false; // near-origin: sign-ambiguous and harmless — leave stored bytes intact
        }

        float stored = f.Plane.Offset;
        float errGood = MathF.Abs(stored - offGood);
        float errBad = MathF.Abs(stored - offBad);

        // Rewrite only when the stored offset unambiguously matches the inverted value and is far
        // from the correct one. A correct RED plane has errBad == separation (never < band), so this
        // can never fire on it; a genuinely inverted plane has errBad ≈ 0.
        if (errBad < errGood && errBad <= (0.05f * separation) + MatchBand)
        {
            f.Plane = new RfPlane(n, offGood);
            return true;
        }

        return false;
    }
}
