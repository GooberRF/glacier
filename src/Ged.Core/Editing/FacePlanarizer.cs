using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// RED-parity edit-time planarity guard. When a vertex transform is applied to a brush in the
/// editor, any face whose corners no longer lie on a single plane is fan-triangulated so brush
/// faces stay flat after the edit — the behavior RED exhibits when you drag one corner of a cube
/// and the three faces meeting at that corner split into triangles.
/// <para>
/// RE provenance (RED.exe 1.20na, ghidraRF — see docs/research/compiler-parity-notes.md):
/// binary-backed — RED's universal face finalizer <c>FUN_0048a8b0</c> re-derives each face's plane
/// by Newell's method and its whole geometry pipeline ties at the 1e-4 epsilon
/// <c>0x38d1b717</c> (used here as <see cref="PlanarityTolerance"/>); the vertex-transform commit is
/// applied on drag release (the modify commands run the tool's modal-drag method, then the shared
/// finalize once — <c>FUN_0043ac20</c>/<c>FUN_0043b080</c>/<c>FUN_0043b520</c> → <c>FUN_0043d210</c>).
/// Spec-backed (owner-described behavior; the vertex-tool's apply is a virtual dispatch not
/// statically resolvable, and RED's generic finalizer stores a best-fit plane rather than
/// triangulating): the triangulation itself, its fan-from-vertex-0 method, and the per-corner UV /
/// property carry. RED's manual editor command <c>u"Triangulate selected faces"</c> confirms
/// triangulation is RED's own operation on brush faces.
/// </para>
/// <para>
/// WATERTIGHT BY CONSTRUCTION: the fan reuses the face's existing pool vertices (no new points), so
/// every shared edge with a neighbour keeps its endpoints. Planar faces are never touched, so a
/// coplanar-preserving move (a slide along the face plane) leaves the face intact.
/// </para>
/// </summary>
public static class FacePlanarizer
{
    /// <summary>
    /// RED's geometry epsilon (<c>0x38d1b717</c> ≈ 1e-4 m / 0.1 mm) — the tie band RED's whole
    /// geometry pipeline uses. A face is "bent" once any corner is farther than this from the
    /// face's best-fit plane.
    /// </summary>
    public const float PlanarityTolerance = 1e-4f;

    /// <summary>
    /// Recomputes every face's plane (Newell best-fit) and fan-triangulates any candidate face bent
    /// beyond <paramref name="tolerance"/>, carrying the source face's texture, flags, smoothing
    /// group, room/surface bindings and each corner's UVs onto every resulting triangle. Planar
    /// faces (and existing triangles) are left intact. When <paramref name="movedVertices"/> is given,
    /// only faces that reference one of those pool indices are eligible to split — so a pre-existing
    /// non-planar face the edit did not touch is left alone; when null, every face is eligible.
    /// Returns the number of source faces that were triangulated.
    /// </summary>
    public static int Planarize(Geometry g, IReadOnlyCollection<int>? movedVertices = null, float tolerance = PlanarityTolerance)
    {
        var outFaces = new List<Face>(g.Faces.Count);
        int triangulated = 0;
        int nextId = GeometryUtil.NextFaceId(g);

        foreach (Face f in g.Faces)
        {
            int n = f.Vertices.Count;
            if (n < 3)
            {
                outFaces.Add(f);
                continue;
            }

            List<Vec3> corners = GeometryUtil.Corners(g, f);
            Vec3 normal = GeometryUtil.Normal(corners);
            Vec3 centroid = GeometryUtil.Centroid(corners);
            f.Plane = new RfPlane(normal, -normal.Dot(centroid)); // refresh best-fit plane (RF convention n·X+offset=0, matches RecomputeAllPlanes)

            bool eligible = n >= 4
                && normal.LengthSquared() >= 1e-12f
                && (movedVertices is null || TouchesMoved(f, movedVertices))
                && MaxDeviation(corners, normal, centroid) > tolerance;
            if (!eligible)
            {
                outFaces.Add(f);
                continue;
            }

            // Fan from vertex 0 — preserves winding (each triangle faces the same way as the source).
            for (int i = 1; i + 1 < n; i++)
            {
                Face tri = CloneProps(f);
                tri.FaceId = nextId++;
                tri.Vertices = new List<FaceVertex>(3)
                {
                    CloneCorner(f.Vertices[0]),
                    CloneCorner(f.Vertices[i]),
                    CloneCorner(f.Vertices[i + 1]),
                };
                Vec3 tn = GeometryUtil.Normal(new[]
                {
                    g.Vertices[tri.Vertices[0].Index], g.Vertices[tri.Vertices[1].Index], g.Vertices[tri.Vertices[2].Index],
                });
                Vec3 tc = GeometryUtil.Centroid(new[]
                {
                    g.Vertices[tri.Vertices[0].Index], g.Vertices[tri.Vertices[1].Index], g.Vertices[tri.Vertices[2].Index],
                });
                tri.Plane = new RfPlane(tn, -tn.Dot(tc));
                outFaces.Add(tri);
            }

            triangulated++;
        }

        if (triangulated > 0)
        {
            g.Faces = outFaces;
        }

        return triangulated;
    }

    private static bool TouchesMoved(Face f, IReadOnlyCollection<int> moved)
    {
        foreach (FaceVertex fv in f.Vertices)
        {
            if (moved.Contains(fv.Index))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Max perpendicular distance of any corner from the unit-normal plane through the centroid.</summary>
    private static float MaxDeviation(IReadOnlyList<Vec3> corners, Vec3 unitNormal, Vec3 centroid)
    {
        float max = 0f;
        foreach (Vec3 p in corners)
        {
            float d = System.MathF.Abs(unitNormal.Dot(p.Sub(centroid)));
            if (d > max)
            {
                max = d;
            }
        }

        return max;
    }

    private static Face CloneProps(Face f) => new()
    {
        Texture = f.Texture,
        SurfaceIndex = f.SurfaceIndex,
        Reserved1A = f.Reserved1A,
        Reserved1B = f.Reserved1B,
        PortalIndexPlus2 = f.PortalIndexPlus2,
        Flags = f.Flags,
        Reserved2 = f.Reserved2,
        SmoothingGroups = f.SmoothingGroups,
        RoomIndex = f.RoomIndex,
    };

    private static FaceVertex CloneCorner(FaceVertex v) => new()
    {
        Index = v.Index,
        TextureCoords = v.TextureCoords,
        LightmapCoords = v.LightmapCoords,
    };
}
