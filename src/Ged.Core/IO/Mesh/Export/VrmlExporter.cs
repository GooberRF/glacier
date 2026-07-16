using System;
using System.Globalization;
using System.Text;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Export;

/// <summary>
/// Exports an <see cref="ImportedModel"/> to VRML 2.0 (VRML97, <c>.wrl</c>) — the
/// stock RED "Export as VRML" File-menu parity. One <c>Shape</c> per texture group
/// (an <c>IndexedFaceSet</c> with per-vertex coords + texCoords and a textured
/// <c>Appearance</c>). RF is converted by negating Z with reversed winding, matching
/// the other exporters. Minimal but valid UTF-8 VRML.
/// </summary>
public static class VrmlExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Export(ImportedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var sb = new StringBuilder();
        sb.Append("#VRML V2.0 utf8\n");
        sb.Append("# Glacier VRML export\n\n");
        sb.Append("Transform {\n  children [\n");

        foreach (ImportedGroup group in model.Groups)
        {
            if (group.Positions.Count == 0 || group.Indices.Count < 3)
            {
                continue;
            }

            WriteShape(sb, group);
        }

        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static void WriteShape(StringBuilder sb, ImportedGroup group)
    {
        bool hasUvs = group.HasTexCoords;
        sb.Append("    Shape {\n");
        sb.Append("      appearance Appearance {\n");
        sb.Append("        material Material { diffuseColor 0.8 0.8 0.8 }\n");
        if (!string.IsNullOrWhiteSpace(group.Texture))
        {
            sb.Append($"        texture ImageTexture {{ url \"{Escape(group.Texture)}\" }}\n");
        }

        sb.Append("      }\n");
        sb.Append("      geometry IndexedFaceSet {\n");
        sb.Append("        solid FALSE\n");

        // Coordinates (RF -> VRML: negate Z).
        sb.Append("        coord Coordinate {\n          point [\n");
        foreach (Vec3 p in group.Positions)
        {
            sb.Append("            ").Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').Append(F(-p.Z)).Append(",\n");
        }

        sb.Append("          ]\n        }\n");

        // Coord indices (reverse winding, -1 terminates each face).
        sb.Append("        coordIndex [\n");
        for (int i = 0; i + 2 < group.Indices.Count; i += 3)
        {
            sb.Append("          ")
                .Append(group.Indices[i]).Append(' ')
                .Append(group.Indices[i + 2]).Append(' ')
                .Append(group.Indices[i + 1]).Append(" -1,\n");
        }

        sb.Append("        ]\n");

        if (hasUvs)
        {
            sb.Append("        texCoord TextureCoordinate {\n          point [\n");
            foreach (Uv uv in group.TexCoords)
            {
                sb.Append("            ").Append(F(uv.U)).Append(' ').Append(F(uv.V)).Append(",\n");
            }

            sb.Append("          ]\n        }\n");

            sb.Append("        texCoordIndex [\n");
            for (int i = 0; i + 2 < group.Indices.Count; i += 3)
            {
                sb.Append("          ")
                    .Append(group.Indices[i]).Append(' ')
                    .Append(group.Indices[i + 2]).Append(' ')
                    .Append(group.Indices[i + 1]).Append(" -1,\n");
            }

            sb.Append("        ]\n");
        }

        sb.Append("      }\n    }\n");
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string F(float v) => v.ToString("0.######", Inv);
}
