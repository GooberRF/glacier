using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Model;

namespace Ged.Rendering.Scene;

/// <summary>
/// Builds the "Draw Decals" viewport preview: an editor visualization of RF's runtime decal
/// projection. Each decal's texture is projected along the decal's forward axis onto the compiled
/// static geometry that faces it — world faces clipped to the oriented decal box (extents), with
/// UVs derived from the decal's right/up axes — and emitted as an alpha-blended, depth-biased
/// overlay pass. Pure CPU logic (no GPU dependency), recomputed only on a scene/decal rebuild.
/// </summary>
public static class DecalProjectionBuilder
{
    // How far (world units) each projected vertex is lifted along the receiving face normal so the
    // overlay renders just in front of the surface instead of z-fighting it (RF uses a GPU zbias).
    private const float DepthBias = 0.02f;

    // A face receives the projection only when it faces back toward the decal (its outward normal
    // opposes the decal's forward/projection direction) by more than this cosine — so the decal
    // lands on the surfaces it aims at, not on grazing or back-facing geometry.
    private const float FacingCosine = 0.1f;

    /// <summary>
    /// Appends one alpha-pass batch group per decal texture: the decal's bitmap projected onto the
    /// static faces inside its box. Faces flagged portal/invisible and back-facing faces are skipped.
    /// </summary>
    public static void Append(RenderScene scene, Geometry geo, IReadOnlyList<Decal> decals)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(geo);
        ArgumentNullException.ThrowIfNull(decals);

        // Merge decals that share both texture and alpha into one batch (fewer draws).
        var batches = new Dictionary<(string Tex, int Alpha), GeometryBatch>();

        foreach (Decal d in decals)
        {
            AppendDecal(scene, geo, d, batches);
        }
    }

    private static void AppendDecal(
        RenderScene scene,
        Geometry geo,
        Decal d,
        Dictionary<(string, int), GeometryBatch> batches)
    {
        Vec3 ext = d.Extents.LengthSquared() > 1e-4f ? d.Extents : new Vec3(1f, 1f, 0.2f);
        if (ext.X <= 1e-3f || ext.Y <= 1e-3f)
        {
            return;
        }

        Mat3 rot = d.Header.Rotation;
        var center = new Vector3(d.Header.Position.X, d.Header.Position.Y, d.Header.Position.Z);
        Vector3 right = SafeNormal(rot.Right, Vector3.UnitX);
        Vector3 up = SafeNormal(rot.Up, Vector3.UnitY);
        Vector3 forward = SafeNormal(rot.Forward, Vector3.UnitZ);
        float hx = ext.X * 0.5f;
        float hy = ext.Y * 0.5f;
        float hz = ext.Z * 0.5f;

        int alpha = d.Alpha > 0 ? Math.Clamp(d.Alpha, 0, 255) : 255;
        var key = (d.Texture ?? string.Empty, alpha);
        GeometryBatch? batch = null; // created lazily so an empty projection adds nothing

        foreach (Face f in geo.Faces)
        {
            if (f.IsPortalFace || ((FaceFlags)f.Flags & FaceFlags.IsInvisible) != 0 || f.Vertices.Count < 3)
            {
                continue;
            }

            // Only receive the projection on surfaces facing back toward the decal.
            var n = new Vector3(f.Plane.Normal.X, f.Plane.Normal.Y, f.Plane.Normal.Z);
            if (n.LengthSquared() < 1e-8f)
            {
                continue;
            }

            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, forward) > -FacingCosine)
            {
                continue;
            }

            // World polygon (static geometry is identity-space).
            var poly = new List<Vector3>(f.Vertices.Count);
            foreach (FaceVertex fv in f.Vertices)
            {
                if (fv.Index >= 0 && fv.Index < geo.Vertices.Count)
                {
                    Vec3 v = geo.Vertices[fv.Index];
                    poly.Add(new Vector3(v.X, v.Y, v.Z));
                }
            }

            if (poly.Count < 3)
            {
                continue;
            }

            // Clip to the three oriented slabs of the decal box (right/up = footprint, forward = depth).
            poly = ClipSlab(poly, right, center, hx);
            if (poly.Count < 3)
            {
                continue;
            }

            poly = ClipSlab(poly, up, center, hy);
            if (poly.Count < 3)
            {
                continue;
            }

            poly = ClipSlab(poly, forward, center, hz);
            if (poly.Count < 3)
            {
                continue;
            }

            batch ??= GetBatch(scene, batches, key);
            EmitPolygon(batch, poly, center, right, up, n, ext.X, ext.Y);
        }
    }

    private static GeometryBatch GetBatch(
        RenderScene scene,
        Dictionary<(string, int), GeometryBatch> batches,
        (string Tex, int Alpha) key)
    {
        if (!batches.TryGetValue(key, out GeometryBatch? batch))
        {
            batch = new GeometryBatch(key.Tex, -1, RenderPass.Alpha)
            {
                // White RGB so the decal texture shows true colour; alpha scales the blend.
                Tint = new Vector4(1f, 1f, 1f, key.Alpha / 255f),
            };
            batches[key] = batch;
            scene.Batches.Add(batch);
        }

        return batch;
    }

    private static void EmitPolygon(
        GeometryBatch batch,
        List<Vector3> poly,
        Vector3 center,
        Vector3 right,
        Vector3 up,
        Vector3 faceNormal,
        float extX,
        float extY)
    {
        uint white = Palette.Rgba(255, 255, 255, 255);
        Vector3 lift = faceNormal * DepthBias; // depth-bias off the surface toward its visible side
        int baseVertex = batch.Vertices.Count;
        foreach (Vector3 p in poly)
        {
            Vector3 rel = p - center;
            float u = (Vector3.Dot(right, rel) / extX) + 0.5f; // right/up span the box footprint 0..1
            float v = 0.5f - (Vector3.Dot(up, rel) / extY);    // flip V: texture top = +up (top-origin bitmap)
            batch.Vertices.Add(new WorldVertex
            {
                Position = p + lift,
                Normal = faceNormal,
                TexCoord = new Vector2(u, v),
                LightmapCoord = Vector2.Zero,
                Color = white,
                PickId = 0,
            });
        }

        for (int i = 1; i < poly.Count - 1; i++)
        {
            batch.Indices.Add((uint)baseVertex);
            batch.Indices.Add((uint)(baseVertex + i));
            batch.Indices.Add((uint)(baseVertex + i + 1));
        }
    }

    /// <summary>Clips a convex polygon to the slab |dot(axis, p − center)| ≤ half (two half-spaces).</summary>
    private static List<Vector3> ClipSlab(List<Vector3> poly, Vector3 axis, Vector3 center, float half)
    {
        float c = Vector3.Dot(axis, center);
        poly = ClipHalf(poly, axis, c + half);   // dot(axis, p) ≤ c + half
        if (poly.Count < 3)
        {
            return poly;
        }

        return ClipHalf(poly, -axis, -c + half); // dot(−axis, p) ≤ −c + half  ⇔  dot(axis, p) ≥ c − half
    }

    /// <summary>Sutherland–Hodgman half-space clip keeping vertices where dot(normal, p) ≤ d.</summary>
    private static List<Vector3> ClipHalf(List<Vector3> poly, Vector3 normal, float d)
    {
        var outp = new List<Vector3>(poly.Count + 4);
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % n];
            float da = Vector3.Dot(normal, a) - d;
            float db = Vector3.Dot(normal, b) - d;
            bool aIn = da <= 0f;
            bool bIn = db <= 0f;
            if (aIn)
            {
                outp.Add(a);
            }

            if (aIn != bIn)
            {
                float t = da / (da - db);
                outp.Add(a + ((b - a) * t));
            }
        }

        return outp;
    }

    private static Vector3 SafeNormal(Vec3 v, Vector3 fallback)
    {
        var w = new Vector3(v.X, v.Y, v.Z);
        return w.LengthSquared() > 1e-8f ? Vector3.Normalize(w) : fallback;
    }
}
