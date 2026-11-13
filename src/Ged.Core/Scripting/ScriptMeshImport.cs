using System.Linq;

namespace Ged.Core.Scripting;

/// <summary>The result of <c>level.import_mesh</c> (Lua type <c>mesh_import</c>).</summary>
[ScriptDoc("A mesh import: the new detail brushes (or the Mesh object), their group, triangle and material counts, textures and the .v3m written.", Since = 2)]
public sealed class ScriptMeshImport
{
    private readonly string[] _textures;
    private readonly string[] _missing;

    internal ScriptMeshImport(ScriptBrushQuery brushes, ScriptObjectHandle? obj, ScriptGroupHandle? group, int triangles, int groups,
        string[] textures, string[] missing, string? file)
    {
        Brushes = brushes;
        Object = obj;
        Group = group;
        Triangles = triangles;
        Groups = groups;
        _textures = textures;
        _missing = missing;
        File = file;
    }

    [ScriptDoc("The new detail brushes (brush_query, one per material group; empty for as = \"mesh\").", Since = 2)]
    public ScriptBrushQuery Brushes { get; }

    [ScriptDoc("The new Mesh object (as = \"mesh\"), else nil.", Since = 2)]
    public ScriptObjectHandle? Object { get; }

    [ScriptDoc("The group holding the brushes when there are two or more, else nil.", Since = 2)]
    public ScriptGroupHandle? Group { get; }

    [ScriptDoc("The number of triangles in the model.", Since = 2)]
    public int Triangles { get; }

    [ScriptDoc("The number of material groups in the model.", Since = 2)]
    public int Groups { get; }

    [ScriptDoc("The texture names the model's materials reference (string[], sorted).", Since = 2)]
    public string[] Textures => _textures.ToArray();

    [ScriptDoc("The referenced textures the mounted library does not have (string[]; empty without an install).", Since = 2)]
    public string[] MissingTextures => _missing.ToArray();

    [ScriptDoc("The .v3m file written (as = \"mesh\"), else nil.", Since = 2)]
    public string? File { get; }

    public override string ToString() => Object is not null ? $"mesh_import(object, {Triangles} tris)" : $"mesh_import({Brushes.Count} brush(es), {Triangles} tris)";
}
