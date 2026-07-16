using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Texture-UV mapping operators for Texture mode: box / planar / cylinder
/// projection at a pixels-per-meter scale, plus snap-to-grid, resize, flip,
/// scale/offset and UV copy/paste. All pure and unit-tested; the App wraps them in
/// undo-able edits on the owning brushes.
/// </summary>
/// <remarks>
/// <para><b>Convention.</b> RF stores per-vertex texture UVs in <em>tile</em> units:
/// an integer U step is one full horizontal repeat of the texture. A face's world
/// footprint of one tile therefore measures <c>texWidthPx / pixelsPerMeter</c>
/// metres across and <c>texHeightPx / pixelsPerMeter</c> metres down, so</para>
/// <code>U = worldU_metres * pixelsPerMeter / texWidthPx
/// V = -worldV_metres * pixelsPerMeter / texHeightPx</code>
/// <para>V is negated so +V points down in texture space, matching stock RED and the
/// planar-UV default used at brush creation (<see cref="GeometryUtil.AssignPlanarUv"/>).
/// The projection axes come from the face normal's dominant axis
/// (<see cref="GeometryUtil.DominantProjection"/>): +Z-facing maps (X,Y); +X-facing
/// maps (Z,Y); +Y-facing maps (X,Z). This mirrors the geometry compiler's
/// dominant-axis / pixels-per-meter surface derivation (docs/research/
/// red-geometry-compiler.md §B.6), and the game consumes the per-vertex UVs
/// verbatim. Mapping is computed in the brush's local vertex space (equal to world
/// space for an un-rotated brush at the origin).</para>
/// </remarks>
public static class UvOps
{
    /// <summary>Default pixels-per-meter for texture application (stock RED default).</summary>
    public const float DefaultPixelsPerMeter = 256f;

    /// <summary>Fallback texture size when the real bitmap dimensions are unknown.</summary>
    public const int DefaultTextureSize = 256;

    /// <summary>[ALPINE] Maximum pixels-per-meter accepted when applying a map.</summary>
    public const float MaxPixelsPerMeter = 8192f;

    /// <summary>Clamps a pixels-per-meter value to the valid (Alpine) range.</summary>
    public static float ClampPpm(float ppm) => Math.Clamp(ppm, 0.001f, MaxPixelsPerMeter);

    // ---- Projection mapping ---------------------------------------------------

    /// <summary>
    /// Box-maps a single face: projects along the face normal's dominant axis, so
    /// each face gets an axis-aligned tiling in its own plane.
    /// </summary>
    public static void BoxMap(Geometry g, Face f, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(f.Plane.Normal);
        Project(g, f, uAxis, vAxis, pixelsPerMeter, texWidthPx, texHeightPx);
    }

    /// <summary>Box-maps every listed face independently.</summary>
    public static void BoxMap(Geometry g, IEnumerable<int> faceIndices, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        foreach (int i in faceIndices)
        {
            if (i >= 0 && i < g.Faces.Count)
            {
                BoxMap(g, g.Faces[i], pixelsPerMeter, texWidthPx, texHeightPx);
            }
        }
    }

    /// <summary>
    /// Planar-maps a set of faces onto one shared projection plane (chosen from
    /// <paramref name="referenceNormal"/>), giving continuous UVs across the faces —
    /// the decal / multi-face map.
    /// </summary>
    public static void PlanarMap(Geometry g, IEnumerable<int> faceIndices, Vec3 referenceNormal, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(referenceNormal);
        foreach (int i in faceIndices)
        {
            if (i >= 0 && i < g.Faces.Count)
            {
                Project(g, g.Faces[i], uAxis, vAxis, pixelsPerMeter, texWidthPx, texHeightPx);
            }
        }
    }

    /// <summary>
    /// Cylinder-maps a face by wrapping around <paramref name="axis"/> (0=X,1=Y,2=Z):
    /// U follows the arc (angle × the face's mean radius), V follows the axis height.
    /// </summary>
    public static void CylinderMap(Geometry g, Face f, int axis, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        (int p, int q) = PerpendicularAxes(axis);
        float scaleU = pixelsPerMeter / Math.Max(1, texWidthPx);
        float scaleV = pixelsPerMeter / Math.Max(1, texHeightPx);

        // Mean radius over the face's corners, so a true cylinder maps to a uniform arc.
        float radius = 0f;
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 pos = g.Vertices[fv.Index];
            radius += MathF.Sqrt((pos.Component(p) * pos.Component(p)) + (pos.Component(q) * pos.Component(q)));
        }

        radius = f.Vertices.Count > 0 ? radius / f.Vertices.Count : 0f;

        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 pos = g.Vertices[fv.Index];
            float angle = MathF.Atan2(pos.Component(q), pos.Component(p));
            float u = angle * radius * scaleU;
            float v = -pos.Component(axis) * scaleV;
            fv.TextureCoords = new Uv(u, v);
        }
    }

    /// <summary>Cylinder-maps every listed face around the shared axis.</summary>
    public static void CylinderMap(Geometry g, IEnumerable<int> faceIndices, int axis, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        foreach (int i in faceIndices)
        {
            if (i >= 0 && i < g.Faces.Count)
            {
                CylinderMap(g, g.Faces[i], axis, pixelsPerMeter, texWidthPx, texHeightPx);
            }
        }
    }

    private static void Project(Geometry g, Face f, int uAxis, int vAxis, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        float scaleU = pixelsPerMeter / Math.Max(1, texWidthPx);
        float scaleV = pixelsPerMeter / Math.Max(1, texHeightPx);
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 pos = g.Vertices[fv.Index];
            fv.TextureCoords = new Uv(pos.Component(uAxis) * scaleU, -pos.Component(vAxis) * scaleV);
        }
    }

    private static (int P, int Q) PerpendicularAxes(int axis) => axis switch
    {
        0 => (1, 2),
        1 => (2, 0),
        _ => (0, 1),
    };

    // ---- Whole-face UV edits --------------------------------------------------

    /// <summary>Snaps every UV component of a face to the nearest multiple of <paramref name="step"/>.</summary>
    public static void SnapToGrid(Face f, float step)
    {
        if (step <= 1e-6f)
        {
            return;
        }

        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = new Uv(
                MathF.Round(fv.TextureCoords.U / step) * step,
                MathF.Round(fv.TextureCoords.V / step) * step);
        }
    }

    /// <summary>Scales a face's UVs about their centroid (Resize Map / S+drag).</summary>
    public static void Scale(Face f, float scaleU, float scaleV)
    {
        Uv c = Centroid(f);
        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = new Uv(
                c.U + ((fv.TextureCoords.U - c.U) * scaleU),
                c.V + ((fv.TextureCoords.V - c.V) * scaleV));
        }
    }

    /// <summary>Offsets a face's UVs by (du, dv).</summary>
    public static void Offset(Face f, float du, float dv)
    {
        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = new Uv(fv.TextureCoords.U + du, fv.TextureCoords.V + dv);
        }
    }

    /// <summary>Mirrors a face's UVs in U about their centroid (Flip X).</summary>
    public static void FlipU(Face f)
    {
        Uv c = Centroid(f);
        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = new Uv((2f * c.U) - fv.TextureCoords.U, fv.TextureCoords.V);
        }
    }

    /// <summary>Mirrors a face's UVs in V about their centroid (Flip Y).</summary>
    public static void FlipV(Face f)
    {
        Uv c = Centroid(f);
        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = new Uv(fv.TextureCoords.U, (2f * c.V) - fv.TextureCoords.V);
        }
    }

    /// <summary>The centroid of a face's UVs.</summary>
    public static Uv Centroid(Face f)
    {
        if (f.Vertices.Count == 0)
        {
            return default;
        }

        float u = 0f, v = 0f;
        foreach (FaceVertex fv in f.Vertices)
        {
            u += fv.TextureCoords.U;
            v += fv.TextureCoords.V;
        }

        return new Uv(u / f.Vertices.Count, v / f.Vertices.Count);
    }

    // ---- Fit to tile (combined-bbox, aspect-preserving) -----------------------

    /// <summary>
    /// The affine that maps a face selection's combined UV bounding box into the base
    /// [0,1] tile: subtract the box minimum, scale, then centre. When
    /// <see cref="ScaleU"/> / <see cref="ScaleV"/> are equal the fit is uniform
    /// (aspect-preserving) so a square face fills the tile edge-to-edge and a circle
    /// keeps its shape. A zero scale on an axis (a degenerate, zero-extent axis) maps that
    /// axis to the tile centre (0.5).
    /// </summary>
    public readonly record struct UvFitTransform(float MinU, float MinV, float ScaleU, float ScaleV, float OffsetU, float OffsetV)
    {
        /// <summary>The identity fit (used when there is nothing to fit).</summary>
        public static UvFitTransform Identity => new(0f, 0f, 1f, 1f, 0f, 0f);

        /// <summary>Maps a UV through the fit transform.</summary>
        public Uv Apply(Uv uv) => new(
            ScaleU > 0f ? ((uv.U - MinU) * ScaleU) + OffsetU : 0.5f,
            ScaleV > 0f ? ((uv.V - MinV) * ScaleV) + OffsetV : 0.5f);
    }

    /// <summary>
    /// Item 4 — Fit: computes the transform that stretches the COMBINED UV bounding box of
    /// <paramref name="faces"/> to fit exactly inside one [0,1] tile. Aspect-preserving by
    /// default: a uniform scale makes the larger bbox dimension span exactly 1.0 and centres
    /// the shorter dimension within [0,1] (so squares fill the tile and circles stay circles).
    /// Set <paramref name="preserveAspect"/> false to stretch each axis independently.
    /// Combined across the whole selection, so a multi-face selection fits as one bbox.
    /// </summary>
    public static UvFitTransform ComputeFitTransform(IEnumerable<Face> faces, bool preserveAspect = true)
    {
        ArgumentNullException.ThrowIfNull(faces);
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        int n = 0;
        foreach (Face f in faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                Uv uv = fv.TextureCoords;
                minU = MathF.Min(minU, uv.U);
                maxU = MathF.Max(maxU, uv.U);
                minV = MathF.Min(minV, uv.V);
                maxV = MathF.Max(maxV, uv.V);
                n++;
            }
        }

        if (n == 0)
        {
            return UvFitTransform.Identity;
        }

        float du = maxU - minU;
        float dv = maxV - minV;
        float su = du > 1e-6f ? 1f / du : 0f;
        float sv = dv > 1e-6f ? 1f / dv : 0f;
        if (preserveAspect)
        {
            float s = MathF.Min(su > 0f ? su : float.MaxValue, sv > 0f ? sv : float.MaxValue);
            if (s == float.MaxValue)
            {
                s = 0f;
            }

            su = sv = s;
        }

        float offU = (1f - (du * su)) * 0.5f;
        float offV = (1f - (dv * sv)) * 0.5f;
        return new UvFitTransform(minU, minV, su, sv, offU, offV);
    }

    /// <summary>Applies a precomputed <see cref="UvFitTransform"/> to every corner of a face.</summary>
    public static void ApplyFit(Face f, UvFitTransform t)
    {
        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = t.Apply(fv.TextureCoords);
        }
    }

    /// <summary>
    /// Fit — stretches the UVs of <paramref name="faces"/> so their combined bounding box
    /// fills one [0,1] tile (see <see cref="ComputeFitTransform"/>). Convenience wrapper for
    /// tests and non-undo callers; the editor computes the transform once and applies it
    /// per-face through the undo system so the whole selection is one undo step.
    /// </summary>
    public static void FitFacesToTile(IReadOnlyCollection<Face> faces, bool preserveAspect = true)
    {
        UvFitTransform t = ComputeFitTransform(faces, preserveAspect);
        foreach (Face f in faces)
        {
            ApplyFit(f, t);
        }
    }

    // ---- Copy / paste ---------------------------------------------------------

    /// <summary>Snapshots a face's per-corner UVs (Ctrl+C in Texture mode).</summary>
    public static Uv[] Copy(Face f) => f.Vertices.Select(v => v.TextureCoords).ToArray();

    /// <summary>
    /// Pastes copied UVs onto a face (Ctrl+V). When the corner counts match the UVs
    /// are copied 1:1; otherwise each corner takes the nearest source UV by index so
    /// a differently-tessellated face still receives a sensible mapping.
    /// </summary>
    public static bool Paste(Face f, IReadOnlyList<Uv> uvs)
    {
        if (uvs is null || uvs.Count == 0 || f.Vertices.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < f.Vertices.Count; i++)
        {
            int src = uvs.Count == f.Vertices.Count ? i : Math.Min(i, uvs.Count - 1);
            f.Vertices[i].TextureCoords = uvs[src];
        }

        return true;
    }
}
