using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Turns the compiler's world-space faces (with a shared vertex pool and
/// resolved room/surface bindings) into a serialisable <see cref="Geometry"/>:
/// texture table, vertex pool, faces (planes, flags, ids, room + surface
/// indices, per-corner UVs and optional lightmap UVs), rooms, subroom lists,
/// portals, surfaces, and the face-scroll table.
/// </summary>
public sealed class GeometryAssembler
{
    public Geometry Assemble(
        List<CsgFace> faces,
        List<int[]> facePoolIndices,
        List<Vec3> pool,
        RoomBuildResult rooms,
        List<Portal> portals,
        List<Surface> surfaces,
        List<FaceScrollData> scroll)
    {
        var g = new Geometry();
        g.Vertices.AddRange(pool);
        g.Rooms.AddRange(rooms.Rooms);
        g.SubroomLists.AddRange(rooms.SubroomLists);
        g.Portals.AddRange(portals);
        g.Surfaces.AddRange(surfaces);
        g.FaceScrollData.AddRange(scroll);

        var texIndex = new Dictionary<string, int>();

        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace cf = faces[fi];
            int[] idx = facePoolIndices[fi];

            int texture;
            if (cf.IsPortal || string.IsNullOrEmpty(cf.Texture))
            {
                texture = -1;
            }
            else if (!texIndex.TryGetValue(cf.Texture, out texture))
            {
                texture = g.Textures.Count;
                g.Textures.Add(cf.Texture);
                texIndex[cf.Texture] = texture;
            }

            var face = new Face
            {
                Plane = new RfPlane(cf.Plane.Normal, cf.Plane.Offset),
                Texture = texture,
                SurfaceIndex = cf.SurfaceIndex,
                FaceId = cf.FaceId,
                Reserved1A = -1,
                Reserved1B = -1,
                PortalIndexPlus2 = cf.PortalIndexPlus2,
                Flags = cf.Flags,
                Reserved2 = 0,
                SmoothingGroups = cf.SmoothingGroups,
                RoomIndex = cf.RoomIndex,
            };

            bool hasLm = cf.SurfaceIndex >= 0 && (cf.SurfaceIndex & 0xFFFF) != 0xFFFF && cf.LightmapUvs is not null;
            for (int v = 0; v < cf.Vertices.Count; v++)
            {
                var fv = new FaceVertex
                {
                    Index = idx[v],
                    TextureCoords = cf.Vertices[v].Uv,
                };
                if (hasLm && v < cf.LightmapUvs!.Length)
                {
                    fv.LightmapCoords = cf.LightmapUvs[v];
                }

                face.Vertices.Add(fv);
            }

            g.Faces.Add(face);
        }

        return g;
    }
}
