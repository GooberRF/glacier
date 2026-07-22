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
/// verbatim. Projection is computed in WORLD space: each corner is transformed by its brush's
/// rotation/position before projecting, and the box dominant axis comes from the face's WORLD
/// normal, so mapping flows continuously across differently-oriented brushes exactly as RED does
/// (RED.exe FUN_00499820 / FUN_00499640 read the world vertex coordinates directly, with the
/// projection origin at the world origin; FUN_004b44b0 derives the axis from the world normal).
/// For an un-rotated brush at the origin world == local, so unrotated output is byte-identical to a
/// brush-local projection.</para>
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
    /// Box-maps a single face in WORLD space: transforms the face's corners to world by the brush
    /// <paramref name="rotation"/> / <paramref name="position"/>, chooses the projection plane from
    /// the dominant axis of the face's WORLD normal, and projects along it. Because both the axis and
    /// the corner coordinates are world-space (origin at the world origin, no per-brush/per-selection
    /// offset — RED.exe FUN_00499820), two coplanar faces on differently-rotated/positioned brushes map
    /// in the same direction and tile continuously across the seam. World == local for an un-rotated
    /// brush at the origin.
    /// </summary>
    public static void BoxMap(Geometry g, Face f, Mat3 rotation, Vec3 position, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(rotation.Transform(f.Plane.Normal));
        Project(g, f, rotation, position, uAxis, vAxis, pixelsPerMeter, texWidthPx, texHeightPx);
    }

    /// <summary>Box-maps a single face with no brush transform (identity rotation at the origin — world == local).</summary>
    public static void BoxMap(Geometry g, Face f, float pixelsPerMeter, int texWidthPx, int texHeightPx) =>
        BoxMap(g, f, Mat3.Identity, Vec3.Zero, pixelsPerMeter, texWidthPx, texHeightPx);

    /// <summary>Box-maps every listed face independently under one brush transform.</summary>
    public static void BoxMap(Geometry g, IEnumerable<int> faceIndices, Mat3 rotation, Vec3 position, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        foreach (int i in faceIndices)
        {
            if (i >= 0 && i < g.Faces.Count)
            {
                BoxMap(g, g.Faces[i], rotation, position, pixelsPerMeter, texWidthPx, texHeightPx);
            }
        }
    }

    /// <summary>Box-maps every listed face independently with no brush transform (world == local).</summary>
    public static void BoxMap(Geometry g, IEnumerable<int> faceIndices, float pixelsPerMeter, int texWidthPx, int texHeightPx) =>
        BoxMap(g, faceIndices, Mat3.Identity, Vec3.Zero, pixelsPerMeter, texWidthPx, texHeightPx);

    /// <summary>
    /// Planar-maps a set of faces onto one shared WORLD projection plane (chosen from the WORLD
    /// <paramref name="referenceNormal"/>), giving continuous UVs across the faces — the decal /
    /// multi-face map. Each corner is transformed to world by the brush <paramref name="rotation"/> /
    /// <paramref name="position"/> before projecting, so faces on differently-placed brushes share one
    /// continuous tiling. World == local for an un-rotated brush at the origin.
    /// </summary>
    public static void PlanarMap(Geometry g, IEnumerable<int> faceIndices, Mat3 rotation, Vec3 position, Vec3 referenceNormal, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(referenceNormal);
        foreach (int i in faceIndices)
        {
            if (i >= 0 && i < g.Faces.Count)
            {
                Project(g, g.Faces[i], rotation, position, uAxis, vAxis, pixelsPerMeter, texWidthPx, texHeightPx);
            }
        }
    }

    /// <summary>
    /// Planar-maps with no brush transform (identity rotation at the origin); <paramref name="referenceNormal"/>
    /// is taken as already world-space (world == local for an un-rotated brush at the origin).
    /// </summary>
    public static void PlanarMap(Geometry g, IEnumerable<int> faceIndices, Vec3 referenceNormal, float pixelsPerMeter, int texWidthPx, int texHeightPx) =>
        PlanarMap(g, faceIndices, Mat3.Identity, Vec3.Zero, referenceNormal, pixelsPerMeter, texWidthPx, texHeightPx);

    /// <summary>
    /// Cylinder-maps a face by wrapping around the WORLD <paramref name="axis"/> (0=X,1=Y,2=Z):
    /// corners are transformed to world by the brush <paramref name="rotation"/> /
    /// <paramref name="position"/> first, then U follows the arc (angle × the face's mean world radius
    /// about the axis) and V follows the world axis height. World == local for an un-rotated brush at
    /// the origin.
    /// </summary>
    public static void CylinderMap(Geometry g, Face f, Mat3 rotation, Vec3 position, int axis, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        (int p, int q) = PerpendicularAxes(axis);
        float scaleU = pixelsPerMeter / Math.Max(1, texWidthPx);
        float scaleV = pixelsPerMeter / Math.Max(1, texHeightPx);

        // Mean radius over the face's WORLD corners, so a true cylinder maps to a uniform arc.
        float radius = 0f;
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 pos = position.Add(rotation.Transform(g.Vertices[fv.Index]));
            radius += MathF.Sqrt((pos.Component(p) * pos.Component(p)) + (pos.Component(q) * pos.Component(q)));
        }

        radius = f.Vertices.Count > 0 ? radius / f.Vertices.Count : 0f;

        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 pos = position.Add(rotation.Transform(g.Vertices[fv.Index]));
            float angle = MathF.Atan2(pos.Component(q), pos.Component(p));
            float u = angle * radius * scaleU;
            float v = -pos.Component(axis) * scaleV;
            fv.TextureCoords = new Uv(u, v);
        }
    }

    /// <summary>Cylinder-maps a face with no brush transform (identity rotation at the origin — world == local).</summary>
    public static void CylinderMap(Geometry g, Face f, int axis, float pixelsPerMeter, int texWidthPx, int texHeightPx) =>
        CylinderMap(g, f, Mat3.Identity, Vec3.Zero, axis, pixelsPerMeter, texWidthPx, texHeightPx);

    /// <summary>Cylinder-maps every listed face around the shared world axis under one brush transform.</summary>
    public static void CylinderMap(Geometry g, IEnumerable<int> faceIndices, Mat3 rotation, Vec3 position, int axis, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        foreach (int i in faceIndices)
        {
            if (i >= 0 && i < g.Faces.Count)
            {
                CylinderMap(g, g.Faces[i], rotation, position, axis, pixelsPerMeter, texWidthPx, texHeightPx);
            }
        }
    }

    /// <summary>Cylinder-maps every listed face with no brush transform (world == local).</summary>
    public static void CylinderMap(Geometry g, IEnumerable<int> faceIndices, int axis, float pixelsPerMeter, int texWidthPx, int texHeightPx) =>
        CylinderMap(g, faceIndices, Mat3.Identity, Vec3.Zero, axis, pixelsPerMeter, texWidthPx, texHeightPx);

    /// <summary>
    /// Projects each corner of <paramref name="f"/> onto the (<paramref name="uAxis"/>,
    /// <paramref name="vAxis"/>) world axes at the pixels-per-meter scale, transforming each corner to
    /// world by <paramref name="rotation"/> / <paramref name="position"/> first. V is negated so +V
    /// points down, matching stock RED and the planar-UV default.
    /// </summary>
    private static void Project(Geometry g, Face f, Mat3 rotation, Vec3 position, int uAxis, int vAxis, float pixelsPerMeter, int texWidthPx, int texHeightPx)
    {
        float scaleU = pixelsPerMeter / Math.Max(1, texWidthPx);
        float scaleV = pixelsPerMeter / Math.Max(1, texHeightPx);
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 pos = position.Add(rotation.Transform(g.Vertices[fv.Index]));
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

    // ---- Fit to tile (WORLD-projection based) ---------------------------------

    /// <summary>
    /// A world-position→UV fit: planar-projects a face corner's <em>world</em> position onto the
    /// shared plane's two dominant axes (<see cref="UAxis"/> / <see cref="VAxis"/>, chosen by
    /// <see cref="GeometryUtil.DominantProjection"/> exactly as the box/planar map ops derive theirs),
    /// then normalises that projected coordinate into the base [0,1] tile. When <see cref="ScaleU"/> /
    /// <see cref="ScaleV"/> are equal the fit is uniform (world-aspect-preserving); a zero scale on an
    /// axis (a degenerate, zero-extent projected axis) maps that axis to the tile centre (0.5). V is
    /// negated before normalising, matching the map ops' +V-points-down convention so Fit and Planar
    /// agree on orientation. The caller supplies WORLD positions (<c>pos + rotation.Transform(local)</c>).
    /// </summary>
    public readonly record struct UvFitTransform(
        int UAxis, int VAxis, float MinU, float MinV, float ScaleU, float ScaleV, float OffsetU, float OffsetV)
    {
        /// <summary>The identity fit (used when there is nothing to fit): +Z projection, unit scale.</summary>
        public static UvFitTransform Identity => new(0, 1, 0f, 0f, 1f, 1f, 0f, 0f);

        /// <summary>Projects a WORLD-space vertex position to its fitted tile UV.</summary>
        public Uv Apply(Vec3 worldPos)
        {
            float pu = worldPos.Component(UAxis);
            float pv = -worldPos.Component(VAxis);
            return new Uv(
                ScaleU > 0f ? ((pu - MinU) * ScaleU) + OffsetU : 0.5f,
                ScaleV > 0f ? ((pv - MinV) * ScaleV) + OffsetV : 0.5f);
        }
    }

    /// <summary>The brush transform of one face for the world-space Fit: its rotation matrix and world position.</summary>
    public readonly record struct FitFace(Geometry Geometry, Face Face, Mat3 Rotation, Vec3 Position);

    /// <summary>
    /// Fit (WORLD-projection): planar-projects the selected faces onto one shared plane computed in
    /// WORLD space (the area-weighted average of each face's world normal — its Newell normal rotated
    /// by its brush's rotation; a single face uses its own plane) and normalises the combined WORLD
    /// footprint to one [0,1] tile. The projection axes come from
    /// <see cref="GeometryUtil.DominantProjection"/> of that world normal, so the texture spans the
    /// group by real geometry — two side-by-side brushes tile continuously instead of overlapping, and
    /// a rotated brush projects by its face's true world orientation. Aspect-preserving by default (a
    /// uniform scale makes the larger extent span 1.0 and centres the shorter); set
    /// <paramref name="preserveAspect"/> false to stretch each axis independently so a single
    /// axis-aligned quad maps corner-to-corner 1:1. For an un-rotated brush at the origin world == local.
    /// </summary>
    public static UvFitTransform ComputeFitTransform(IEnumerable<FitFace> faces, bool preserveAspect = true)
    {
        ArgumentNullException.ThrowIfNull(faces);
        List<FitFace> items = faces.ToList();

        // Shared plane: the area-weighted average WORLD normal (each face's Newell normal, whose
        // magnitude is twice its area, rotated into world by the brush rotation — a rotation preserves
        // length, so the area weighting survives — then summed).
        Vec3 sum = default;
        Vec3 firstNormal = new(0f, 0f, 1f);
        bool haveFirst = false;
        foreach (FitFace item in items)
        {
            Vec3 worldNrm = item.Rotation.Transform(NewellNormal(item.Geometry, item.Face));
            if (!haveFirst && worldNrm.LengthSquared() > 1e-20f)
            {
                firstNormal = worldNrm;
                haveFirst = true;
            }

            sum = sum.Add(worldNrm);
        }

        // Cancellation (e.g. opposed faces) or all-degenerate input falls back to the first real
        // world normal, then to +Z, so the axes stay finite and sane.
        Vec3 shared = sum.LengthSquared() > 1e-12f ? sum : (haveFirst ? firstNormal : new Vec3(0f, 0f, 1f));
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(shared);

        // Projected bounds across every corner's WORLD position (V negated to match the map ops).
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        int n = 0;
        foreach (FitFace item in items)
        {
            foreach (FaceVertex fv in item.Face.Vertices)
            {
                if (fv.Index < 0 || fv.Index >= item.Geometry.Vertices.Count)
                {
                    continue;
                }

                Vec3 world = item.Position.Add(item.Rotation.Transform(item.Geometry.Vertices[fv.Index]));
                float pu = world.Component(uAxis);
                float pv = -world.Component(vAxis);
                minU = MathF.Min(minU, pu);
                maxU = MathF.Max(maxU, pu);
                minV = MathF.Min(minV, pv);
                maxV = MathF.Max(maxV, pv);
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
        return new UvFitTransform(uAxis, vAxis, minU, minV, su, sv, offU, offV);
    }

    /// <summary>
    /// Adapter for callers with no brush transform (standalone geometry / tests): projects each face
    /// with an identity rotation at the origin, so world == local. Existing single-face expectations
    /// are unchanged by the world-space fit.
    /// </summary>
    public static UvFitTransform ComputeFitTransform(IEnumerable<(Geometry Geometry, Face Face)> faces, bool preserveAspect = true)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return ComputeFitTransform(faces.Select(t => new FitFace(t.Geometry, t.Face, Mat3.Identity, Vec3.Zero)), preserveAspect);
    }

    /// <summary>
    /// Re-projects every corner of <paramref name="f"/> through a precomputed <see cref="UvFitTransform"/>,
    /// transforming each corner into world space by the brush <paramref name="rotation"/> /
    /// <paramref name="position"/> first (the transform's projection basis is world-space).
    /// </summary>
    public static void ApplyFit(Geometry g, Face f, Mat3 rotation, Vec3 position, UvFitTransform t)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(f);
        foreach (FaceVertex fv in f.Vertices)
        {
            if (fv.Index >= 0 && fv.Index < g.Vertices.Count)
            {
                Vec3 world = position.Add(rotation.Transform(g.Vertices[fv.Index]));
                fv.TextureCoords = t.Apply(world);
            }
        }
    }

    /// <summary>Adapter: applies a fit to a face with no brush transform (identity rotation at the origin).</summary>
    public static void ApplyFit(Geometry g, Face f, UvFitTransform t) => ApplyFit(g, f, Mat3.Identity, Vec3.Zero, t);

    /// <summary>
    /// Fit — world-projects <paramref name="faces"/> onto their shared plane and normalises the WORLD
    /// footprint to one [0,1] tile (see <see cref="ComputeFitTransform(IEnumerable{FitFace},bool)"/>).
    /// Convenience wrapper for tests and non-undo callers; the editor computes the transform once and
    /// applies it per-face through the undo system so the whole selection is one undo step.
    /// </summary>
    public static void FitFacesToTile(IReadOnlyCollection<FitFace> faces, bool preserveAspect = true)
    {
        UvFitTransform t = ComputeFitTransform(faces, preserveAspect);
        foreach (FitFace item in faces)
        {
            ApplyFit(item.Geometry, item.Face, item.Rotation, item.Position, t);
        }
    }

    /// <summary>Adapter: fits standalone geometry (no brush transform) — world == local.</summary>
    public static void FitFacesToTile(IReadOnlyCollection<(Geometry Geometry, Face Face)> faces, bool preserveAspect = true) =>
        FitFacesToTile(faces.Select(t => new FitFace(t.Geometry, t.Face, Mat3.Identity, Vec3.Zero)).ToList(), preserveAspect);

    /// <summary>
    /// The area-weighted (Newell) normal of a face from its brush-local corner positions: its
    /// direction is the face normal and its magnitude is twice the polygon area, so summing these
    /// (rotated into world) across faces yields an area-weighted average normal. Degenerate faces contribute ~0.
    /// </summary>
    private static Vec3 NewellNormal(Geometry g, Face f)
    {
        Vec3 n = default;
        int count = f.Vertices.Count;
        for (int i = 0; i < count; i++)
        {
            int ia = f.Vertices[i].Index;
            int ib = f.Vertices[(i + 1) % count].Index;
            if (ia < 0 || ia >= g.Vertices.Count || ib < 0 || ib >= g.Vertices.Count)
            {
                continue;
            }

            Vec3 a = g.Vertices[ia];
            Vec3 b = g.Vertices[ib];
            n = new Vec3(
                n.X + ((a.Y - b.Y) * (a.Z + b.Z)),
                n.Y + ((a.Z - b.Z) * (a.X + b.X)),
                n.Z + ((a.X - b.X) * (a.Y + b.Y)));
        }

        return n;
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
