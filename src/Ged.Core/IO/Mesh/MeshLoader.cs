using Ged.Core.IO.Mesh.Vfx;

namespace Ged.Core.IO.Mesh;

/// <summary>
/// The single entry point that turns any GED-supported mesh container into the
/// <see cref="V3dFile"/> shape the render/thumbnail/dependency pipeline consumes.
/// It sniffs the file magic and dispatches: "VSFX" effect files are parsed with
/// <see cref="VfxReader"/> and adapted by <see cref="VfxToV3d"/>; everything else is
/// a V3M/V3C static or character mesh read by <see cref="V3dReader"/>.
/// </summary>
public static class MeshLoader
{
    /// <summary>Reads mesh bytes (V3M/V3C or VFX) into a <see cref="V3dFile"/>.</summary>
    public static V3dFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return VfxReader.IsVfx(data)
            ? VfxToV3d.Convert(VfxReader.Read(data))
            : V3dReader.Read(data);
    }

    /// <summary>
    /// Every bitmap this mesh file references. For VFX this spans mesh + global +
    /// particle materials (diffuse, vmix second map, reflection); for V3M/V3C it is
    /// the submesh material + LOD texture names.
    /// </summary>
    public static IReadOnlyList<string> ReferencedTextures(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (VfxReader.IsVfx(data))
        {
            return VfxReader.Read(data).ReferencedTextures();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        V3dFile mesh = V3dReader.Read(data);
        foreach (V3dSubmesh sm in mesh.Submeshes)
        {
            foreach (V3dMaterial mat in sm.Materials)
            {
                if (!string.IsNullOrWhiteSpace(mat.DiffuseMapName) && seen.Add(mat.DiffuseMapName))
                {
                    result.Add(mat.DiffuseMapName);
                }
            }

            foreach (V3dLod lod in sm.Lods)
            {
                foreach (V3dLodTexture t in lod.Textures)
                {
                    if (!string.IsNullOrWhiteSpace(t.Filename) && seen.Add(t.Filename))
                    {
                        result.Add(t.Filename);
                    }
                }
            }
        }

        return result;
    }
}
