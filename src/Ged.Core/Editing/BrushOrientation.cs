using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>Which way a brush's stored faces wind, measured from the faces themselves.</summary>
public enum BrushWinding
{
    /// <summary>The faces' right-hand (plane) normals point out of the enclosed volume.</summary>
    Outward,

    /// <summary>The faces' right-hand (plane) normals point into the enclosed volume.</summary>
    Inward,

    /// <summary>No enclosed volume to measure (a flat or degenerate brush, e.g. a single face).</summary>
    Undetermined,
}

/// <summary>
/// How RED stores a brush's face orientation, and the helpers that keep Glacier's brushes in that form.
/// <para>
/// RED.exe (1.20, ghidraRF) consumes a brush's stored faces as-is during the boolean build: an AIR brush
/// (brush op-type 1, RFL flag 0x02 — <c>FUN_0044d870</c> / <c>FUN_0044d830</c>) is folded into the
/// world with its faces unchanged (<c>GeoBuild_Driver</c> @ 0x004399b0 → <c>FUN_004a93d0</c> mode 3, and
/// mode 4 for an air portal brush), while a SOLID brush is cloned and every face reversed first
/// (<c>FUN_00490a10</c> → <c>FUN_0048d490</c>) before mode 1. The world solid's faces point into open
/// space, so an air brush must be stored with its faces wound <b>inward</b> and a solid brush
/// <b>outward</b>; an air brush stored outward is rebuilt inside-out. RED's primitives are generated
/// inward (e.g. <c>FUN_004a5020</c>) and a new brush defaults to Air (<c>BrushNode_construct</c>
/// @ 0x0044d5a0 sets op-type 1). The brush panel's Air/Solid combo (<c>0x0043ed00</c> →
/// <c>0x0043a7a0</c>) sets the op-type of each selected brush whose op-type changes and reverses all of
/// its faces; the Properties dialog's Portal / Detail toggles (<c>0x00402560</c>, <c>0x0043f980</c>) only
/// set the flag byte and never touch the faces.
/// </para>
/// <para>
/// Stored RED levels agree: every air brush (plain and portal, &gt;12 000 across the corpus) is inward;
/// solid brushes are outward. Solid portal brushes are mostly inward but RED also stores them outward
/// (it never re-orients a portal), so Glacier creates them inward and otherwise leaves them as authored.
/// </para>
/// </summary>
public static class BrushOrientation
{
    /// <summary>A brush whose |signed volume| is below this fraction of its bounding box diagonal cubed is flat.</summary>
    private const double FlatFraction = 1e-6;

    /// <summary>
    /// True when RED requires these flags' faces to be stored inward: every air brush (portal or not).
    /// This is the invariant the save repair, the linter and the edit pipeline enforce.
    /// </summary>
    public static bool RequiresInward(uint flags) => (flags & (uint)BrushFlags.Air) != 0;

    /// <summary>
    /// True when a NEW brush with these flags is created inward, the way RED makes it: air brushes and
    /// portal brushes (RED's portal brushes start from its inward primitives).
    /// </summary>
    public static bool CreatesInward(uint flags) => (flags & (uint)(BrushFlags.Air | BrushFlags.Portal)) != 0;

    /// <summary>
    /// Measures which way <paramref name="g"/>'s faces wind in its own (local) space: the sign of the
    /// volume the faces enclose about the vertex mean (exact for a closed shell, a robust majority for an
    /// almost-closed one). Flat geometry is <see cref="BrushWinding.Undetermined"/>.
    /// </summary>
    public static BrushWinding MeasureLocal(Geometry g)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (g.Vertices.Count < 4 || g.Faces.Count < 2)
        {
            return BrushWinding.Undetermined;
        }

        double cx = 0, cy = 0, cz = 0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (Vec3 v in g.Vertices)
        {
            cx += v.X;
            cy += v.Y;
            cz += v.Z;
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            minZ = Math.Min(minZ, v.Z);
            maxX = Math.Max(maxX, v.X);
            maxY = Math.Max(maxY, v.Y);
            maxZ = Math.Max(maxZ, v.Z);
        }

        cx /= g.Vertices.Count;
        cy /= g.Vertices.Count;
        cz /= g.Vertices.Count;
        double volume6 = 0;
        foreach (Face f in g.Faces)
        {
            List<FaceVertex> fv = f.Vertices;
            if (fv.Count < 3 || !InPool(g, fv))
            {
                continue;
            }

            Vec3 p0 = g.Vertices[fv[0].Index];
            double ax = p0.X - cx, ay = p0.Y - cy, az = p0.Z - cz;
            for (int i = 1; i + 1 < fv.Count; i++)
            {
                Vec3 p1 = g.Vertices[fv[i].Index];
                Vec3 p2 = g.Vertices[fv[i + 1].Index];
                double bx = p1.X - cx, by = p1.Y - cy, bz = p1.Z - cz;
                double qx = p2.X - cx, qy = p2.Y - cy, qz = p2.Z - cz;
                volume6 += (ax * ((by * qz) - (bz * qy))) + (ay * ((bz * qx) - (bx * qz))) + (az * ((bx * qy) - (by * qx)));
            }
        }

        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        double diag = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        double scale = diag * diag * diag;
        if (!(scale > 0) || Math.Abs(volume6) <= FlatFraction * scale)
        {
            return BrushWinding.Undetermined;
        }

        return volume6 > 0 ? BrushWinding.Outward : BrushWinding.Inward;
    }

    /// <summary>
    /// Which way <paramref name="b"/>'s faces wind in the world, as RED's build sees them: the local
    /// measurement, mirrored when the brush's orientation matrix is a reflection.
    /// </summary>
    public static BrushWinding Measure(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        BrushWinding local = MeasureLocal(b.Geometry);
        if (local == BrushWinding.Undetermined || Determinant(b.Rotation) >= 0f)
        {
            return local;
        }

        return local == BrushWinding.Outward ? BrushWinding.Inward : BrushWinding.Outward;
    }

    /// <summary>
    /// +1 when <paramref name="g"/>'s stored face normals point out of its volume, −1 when they point in.
    /// Multiply a stored (or corner-derived) face normal by this to get the geometric outward normal.
    /// Flat geometry falls back to the flag convention (−1 for an air brush, as RED stores it).
    /// </summary>
    public static float OutwardSign(Geometry g, uint flags) => MeasureLocal(g) switch
    {
        BrushWinding.Outward => 1f,
        BrushWinding.Inward => -1f,
        _ => RequiresInward(flags) ? -1f : 1f,
    };

    /// <summary>
    /// True when <paramref name="b"/>'s faces, transformed to the world, wind inward (measured, including a
    /// reflecting orientation; a flat brush follows the flag convention).
    /// </summary>
    public static bool WorldWindsInward(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return Measure(b) switch
        {
            BrushWinding.Inward => true,
            BrushWinding.Outward => false,
            _ => RequiresInward(b.Flags),
        };
    }

    /// <summary><see cref="OutwardSign(Geometry, uint)"/> for a brush.</summary>
    public static float OutwardSign(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return OutwardSign(b.Geometry, b.Flags);
    }

    /// <summary>
    /// Reverses every face of <paramref name="g"/>: the corner order (each corner keeps its UVs) and the
    /// stored plane (normal and offset negated, so <c>Normal·X + Offset == 0</c> still holds). This is
    /// RED's <c>FUN_00490a10</c> applied to a stored brush.
    /// </summary>
    public static void Reverse(Geometry g)
    {
        ArgumentNullException.ThrowIfNull(g);
        foreach (Face f in g.Faces)
        {
            f.Vertices.Reverse();
            f.Plane = new RfPlane(f.Plane.Normal.Negate(), -f.Plane.Offset);
        }
    }

    /// <summary>
    /// Brings a NEWLY created brush to RED's stored form: an air or portal brush whose faces wind outward
    /// is reversed (<see cref="CreatesInward"/>). Solid brushes and flat brushes are left as built.
    /// Returns true when the faces were reversed.
    /// </summary>
    public static bool ApplyCreationConvention(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (!CreatesInward(b.Flags) || Measure(b) != BrushWinding.Outward)
        {
            return false;
        }

        Reverse(b.Geometry);
        return true;
    }

    /// <summary>
    /// The save repair and edit-pipeline invariant: an AIR brush whose faces wind outward (which RED never
    /// stores — it would rebuild the brush inside-out) is reversed. Solid brushes, solid portal brushes and
    /// flat brushes are never touched. Returns true when the faces were reversed.
    /// </summary>
    public static bool RepairAir(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (!RequiresInward(b.Flags) || Measure(b) != BrushWinding.Outward)
        {
            return false;
        }

        Reverse(b.Geometry);
        return true;
    }

    /// <summary>
    /// Applies RED's Air/Solid toggle to a brush whose flags just changed from <paramref name="oldFlags"/>:
    /// when the air bit changed, the faces are reversed (RED <c>0x0043a7a0</c>) unless they already wind
    /// the way the new state is stored (inward for air, outward for solid) — so a brush authored the
    /// unusual way round is never turned inside out. A flat brush is always reversed, as RED does.
    /// Portal / detail toggles never re-orient (RED's Properties dialog leaves the faces alone).
    /// Returns true when the faces were reversed.
    /// </summary>
    public static bool ApplyAirToggle(Brush b, uint oldFlags)
    {
        ArgumentNullException.ThrowIfNull(b);
        bool wasAir = RequiresInward(oldFlags);
        bool isAir = RequiresInward(b.Flags);
        if (wasAir == isAir)
        {
            return false;
        }

        BrushWinding now = Measure(b);
        BrushWinding target = isAir ? BrushWinding.Inward : BrushWinding.Outward;
        if (now == target)
        {
            return false;
        }

        Reverse(b.Geometry);
        return true;
    }

    /// <summary>
    /// The edit pipeline's post-edit step for one brush: applies the Air/Solid toggle when the air bit
    /// changed, then the air invariant (<see cref="RepairAir"/>) so an operator that rebuilt an air
    /// brush's faces outward leaves it stored the way RED needs. Returns true when the faces were reversed.
    /// </summary>
    public static bool AfterEdit(Brush b, uint oldFlags)
    {
        bool flipped = ApplyAirToggle(b, oldFlags);
        return RepairAir(b) || flipped;
    }

    /// <summary>
    /// The save safety net: reverses every air brush in the level's brushes section that is stored
    /// outward (a level written by an older Glacier or another tool), marking the section for
    /// re-serialization. RED-made levels never store such a brush, so they are untouched and their bytes
    /// round-trip unchanged. Returns the UIDs of the repaired brushes (empty when nothing changed).
    /// </summary>
    public static IReadOnlyList<int> RepairLevel(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        var repaired = new List<int>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is not BrushesSection bs)
            {
                continue;
            }

            int before = repaired.Count;
            foreach (Brush b in bs.Brushes)
            {
                if (RepairAir(b))
                {
                    repaired.Add(b.Uid);
                }
            }

            if (repaired.Count > before)
            {
                s.Dirty = true;
            }
        }

        return repaired;
    }

    /// <summary>The save-log line for <see cref="RepairLevel"/>'s result, or null when nothing was repaired.</summary>
    public static string? RepairNote(IReadOnlyList<int> repairedUids)
    {
        ArgumentNullException.ThrowIfNull(repairedUids);
        if (repairedUids.Count == 0)
        {
            return null;
        }

        const int Listed = 12;
        string list = string.Join(", ", repairedUids.Count <= Listed ? repairedUids : repairedUids.Take(Listed));
        string more = repairedUids.Count > Listed ? $", … (+{repairedUids.Count - Listed} more)" : string.Empty;
        return repairedUids.Count == 1
            ? $"Re-oriented 1 air brush that was stored inside-out for RED (brush {list}): air brushes are now stored the way RED stores them, so the level rebuilds correctly in RED."
            : $"Re-oriented {repairedUids.Count} air brushes that were stored inside-out for RED (brushes {list}{more}): air brushes are now stored the way RED stores them, so the level rebuilds correctly in RED.";
    }

    /// <summary>The determinant of the brush orientation (negative for a reflection).</summary>
    public static float Determinant(Mat3 m) => m.Right.Dot(m.Up.Cross(m.Forward));

    private static bool InPool(Geometry g, List<FaceVertex> fv)
    {
        foreach (FaceVertex v in fv)
        {
            if (v.Index < 0 || v.Index >= g.Vertices.Count)
            {
                return false;
            }
        }

        return true;
    }
}
