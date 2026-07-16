using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>Deep-copy helpers for brush geometry so edits, undo and paste never alias shared state.</summary>
public static class GeometryClone
{
    /// <summary>A deep clone of a brush geometry (textures, vertex pool, faces + face vertices).</summary>
    public static Geometry Deep(Geometry g)
    {
        var clone = new Geometry
        {
            Unknown1 = g.Unknown1,
            Modifiability = g.Modifiability,
            Name = g.Name,
            ModifiabilityOld = g.ModifiabilityOld,
            PreB4UnknownCount = g.PreB4UnknownCount,
            PreB4UnknownBytes = (byte[])g.PreB4UnknownBytes.Clone(),
        };
        clone.Textures.AddRange(g.Textures);
        clone.Vertices.AddRange(g.Vertices);

        foreach (Face f in g.Faces)
        {
            var nf = new Face
            {
                Plane = f.Plane,
                Texture = f.Texture,
                SurfaceIndex = f.SurfaceIndex,
                FaceId = f.FaceId,
                Reserved1A = f.Reserved1A,
                Reserved1B = f.Reserved1B,
                PortalIndexPlus2 = f.PortalIndexPlus2,
                Flags = f.Flags,
                Reserved2 = f.Reserved2,
                SmoothingGroups = f.SmoothingGroups,
                RoomIndex = f.RoomIndex,
            };
            foreach (FaceVertex fv in f.Vertices)
            {
                nf.Vertices.Add(new FaceVertex
                {
                    Index = fv.Index,
                    TextureCoords = fv.TextureCoords,
                    LightmapCoords = fv.LightmapCoords,
                });
            }

            clone.Faces.Add(nf);
        }

        // Brush geometry carries no rooms/surfaces/portals; copy any anyway for safety.
        clone.FaceScrollData.AddRange(g.FaceScrollData);
        clone.LegacyFaceScrollData.AddRange(g.LegacyFaceScrollData);
        return clone;
    }

    /// <summary>A deep clone of a brush (fresh geometry; same UID unless the caller reassigns).</summary>
    public static Brush Deep(Brush b) => new()
    {
        Uid = b.Uid,
        Position = b.Position,
        Rotation = b.Rotation,
        Geometry = Deep(b.Geometry),
        Flags = b.Flags,
        Life = b.Life,
        State = b.State,
    };
}
