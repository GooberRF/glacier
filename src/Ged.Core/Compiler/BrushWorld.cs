using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Transforms a brush's editable, brush-local <see cref="Geometry"/> into
/// world-space compiler polygons. RED applies the brush transform (position +
/// forward/right/up basis) before CSG, so all boolean work happens in world
/// space. Planes are recomputed from the world winding in RF's stored
/// convention, making the compiler independent of whatever plane sign the source
/// brush happened to carry.
/// </summary>
public static class BrushWorld
{
    /// <summary>World position of a brush-local point: <c>pos + x·Right + y·Up + z·Forward</c>.</summary>
    public static Vec3 ToWorld(Mat3 r, Vec3 p, Vec3 local) => p.Add(r.Transform(local));

    /// <summary>
    /// Emits one <see cref="CsgFace"/> per brush face (≥3 vertices), assigning a
    /// stable sequential face id starting at <paramref name="faceIdStart"/>.
    /// Returns the faces and the next free face id. Faces keep their authored
    /// outward winding; texture names, flags and smoothing groups are carried
    /// through. Portal faces (texture &lt; 0) get an empty texture name and are
    /// flagged as portals.
    /// </summary>
    public static List<CsgFace> ToWorldFaces(Brush brush, int faceIdStart, out int nextFaceId)
    {
        var result = new List<CsgFace>();
        Geometry g = brush.Geometry;
        Mat3 rot = brush.Rotation;
        Vec3 pos = brush.Position;
        bool isAir = (brush.Flags & (uint)BrushFlags.Air) != 0;
        int faceId = faceIdStart;

        // Local face-id → authored scroll velocity, for faces flagged scrolling.
        Dictionary<int, Uv>? scrollByLocalId = null;
        if (g.FaceScrollData.Count > 0)
        {
            scrollByLocalId = new Dictionary<int, Uv>();
            foreach (FaceScrollData s in g.FaceScrollData)
            {
                scrollByLocalId[s.FaceId] = new Uv(s.UVelocity, s.VVelocity);
            }
        }

        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                faceId++;
                continue;
            }

            var verts = new List<CsgVertex>(f.Vertices.Count);
            foreach (FaceVertex fv in f.Vertices)
            {
                Vec3 local = fv.Index >= 0 && fv.Index < g.Vertices.Count ? g.Vertices[fv.Index] : default;
                verts.Add(new CsgVertex(ToWorld(rot, pos, local), fv.TextureCoords));
            }

            bool portal = f.Texture < 0;
            string tex = !portal && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty;

            Uv? scroll = null;
            if (scrollByLocalId is not null &&
                ((FaceFlags)f.Flags & FaceFlags.ScrollTexture) != 0 &&
                scrollByLocalId.TryGetValue(f.FaceId, out Uv sv))
            {
                scroll = sv;
            }

            result.Add(new CsgFace
            {
                Vertices = verts,
                Plane = CsgPlane.FromPolygon(verts),
                Texture = tex,
                Flags = f.Flags,
                SmoothingGroups = f.SmoothingGroups,
                FaceId = faceId,
                SourceBrushUid = brush.Uid,
                FromAir = isAir,
                IsPortal = portal,
                Scroll = scroll,
            });
            faceId++;
        }

        nextFaceId = faceId;
        return result;
    }

    /// <summary>Total number of face-id slots a brush consumes (all faces, including tiny ones).</summary>
    public static int FaceIdSlots(Brush brush) => brush.Geometry.Faces.Count;
}
