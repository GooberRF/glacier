using System;
using System.IO;
using System.Linq;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>
/// The "import as Alpine mesh object" steps the editor's File ▸ Import Mesh and the script API
/// <c>level.import_mesh{as = "mesh"}</c> share: the .v3m file name, the output folder
/// (<c>&lt;root&gt;/user_maps/meshes</c>) and the V3M bytes. Placing the Mesh object and rescanning the
/// asset library stay with each caller.
/// </summary>
public static class MeshObjectImport
{
    /// <summary>The meshes output folder under a root (an RF install): <c>&lt;root&gt;/user_maps/meshes</c>.</summary>
    public static string MeshesDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return Path.Combine(root, "user_maps", "meshes");
    }

    /// <summary>A safe file stem: letters, digits, <c>_</c> and <c>-</c> kept, anything else becomes <c>_</c>; "mesh" when nothing is left.</summary>
    public static string SanitizeMeshName(string name)
    {
        string s = new string((name ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(s) ? "mesh" : s;
    }

    /// <summary>The V3M file bytes for a (transformed) model, named <paramref name="meshFileName"/> (e.g. <c>rock.v3m</c>).</summary>
    public static byte[] BuildV3m(ImportedModel model, string meshFileName)
    {
        ArgumentNullException.ThrowIfNull(model);
        return V3dWriter.Write(MeshImportPipeline.ToV3dFile(model, meshFileName));
    }
}
