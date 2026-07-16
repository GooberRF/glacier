using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Export;

/// <summary>The two files an OBJ export produces: the .obj geometry and its .mtl material library.</summary>
public sealed record ObjOutput(string Obj, string Mtl);

/// <summary>
/// Exports an <see cref="ImportedModel"/> to Wavefront OBJ + MTL, one <c>usemtl</c>
/// group per texture. RF is converted by negating Z (matching the glTF exporter and
/// the pinned convention), and the winding is reversed to compensate. Adapted from
/// REDUX's ObjExporter (MIT).
/// </summary>
public static class ObjExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ObjOutput Export(ImportedModel model, string mtlFileName)
    {
        ArgumentNullException.ThrowIfNull(model);

        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        obj.Append("# Glacier OBJ export\n");
        obj.Append($"mtllib {mtlFileName}\n");

        var writtenMats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int vOffset = 1; // OBJ indices are 1-based
        int groupNo = 0;

        foreach (ImportedGroup group in model.Groups)
        {
            int vcount = group.Positions.Count;
            if (vcount == 0 || group.Indices.Count < 3)
            {
                continue;
            }

            bool hasUvs = group.HasTexCoords;
            bool hasNormals = group.HasNormals;
            string mat = MaterialName(group.Texture, groupNo++);

            obj.Append($"o group_{groupNo}\n");
            for (int i = 0; i < vcount; i++)
            {
                Vec3 p = group.Positions[i];
                obj.Append("v ").Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').Append(F(-p.Z)).Append('\n');
            }

            if (hasUvs)
            {
                foreach (Uv uv in group.TexCoords)
                {
                    obj.Append("vt ").Append(F(uv.U)).Append(' ').Append(F(uv.V)).Append('\n');
                }
            }

            if (hasNormals)
            {
                foreach (Vec3 n in group.Normals)
                {
                    obj.Append("vn ").Append(F(n.X)).Append(' ').Append(F(n.Y)).Append(' ').Append(F(-n.Z)).Append('\n');
                }
            }

            obj.Append($"usemtl {mat}\n");
            for (int i = 0; i + 2 < group.Indices.Count; i += 3)
            {
                // Reverse winding: a, c, b.
                int a = vOffset + group.Indices[i];
                int b = vOffset + group.Indices[i + 1];
                int c = vOffset + group.Indices[i + 2];
                obj.Append('f').Append(' ').Append(Corner(a, hasUvs, hasNormals))
                    .Append(' ').Append(Corner(c, hasUvs, hasNormals))
                    .Append(' ').Append(Corner(b, hasUvs, hasNormals)).Append('\n');
            }

            vOffset += vcount;

            if (writtenMats.Add(mat))
            {
                mtl.Append($"newmtl {mat}\n");
                mtl.Append("Ka 1.000 1.000 1.000\n");
                mtl.Append("Kd 0.800 0.800 0.800\n");
                mtl.Append("Ks 0.000 0.000 0.000\n");
                mtl.Append("d 1.0\nillum 1\n");
                if (!string.IsNullOrWhiteSpace(group.Texture))
                {
                    mtl.Append($"map_Kd {group.Texture}\n");
                }

                mtl.Append('\n');
            }
        }

        return new ObjOutput(obj.ToString(), mtl.ToString());
    }

    private static string Corner(int index, bool uv, bool normal)
    {
        if (uv && normal)
        {
            return $"{index}/{index}/{index}";
        }

        if (normal)
        {
            return $"{index}//{index}";
        }

        if (uv)
        {
            return $"{index}/{index}";
        }

        return index.ToString(Inv);
    }

    private static string MaterialName(string texture, int groupNo) =>
        string.IsNullOrWhiteSpace(texture) ? $"material_{groupNo}" : System.IO.Path.GetFileNameWithoutExtension(texture);

    private static string F(float v) => v.ToString("0.######", Inv);
}
