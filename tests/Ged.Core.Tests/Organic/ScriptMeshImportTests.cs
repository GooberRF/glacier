using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// <c>level.import_mesh</c> (ORGANIC-GEOMETRY-SPEC §2.5): the OBJ fixtures through Lua (cube → one detail
/// brush with 12 triangle faces; scale, axis, pos, rot), the read policy (resolve → authorise → read; a
/// refused path is never read; no policy ⇒ not available), the destructive <c>as = "mesh"</c> write
/// (refused in Dry-Run, never overwrites different bytes), determinism and an exact undo.
/// </summary>
public sealed class ScriptMeshImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ged-import-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _authorised = new();

    public ScriptMeshImportTests()
    {
        Directory.CreateDirectory(_dir);
        foreach (string f in new[] { "cube.obj", "cube.mtl", "pyramid.obj", "pyramid.mtl" })
        {
            File.Copy(TestPaths.Fixture("mesh", f), Path.Combine(_dir, f));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private string Lua(string file) => Path.Combine(_dir, file).Replace('\\', '/');

    /// <summary>Services whose read policy allows only <see cref="_dir"/> and records every authorised path.</summary>
    private ScriptServices Services(EditorDocument doc, string? install = null) => ScriptTestFixture.Services(doc) with
    {
        InstallDirectory = install,
        ImportFileAccess = path =>
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(_dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ScriptApiException($"level.import_mesh: refusing to read '{full}'.", "Allowed: the test folder.");
            }

            _authorised.Add(full);
            return full;
        },
    };

    private (EditorDocument Doc, ScriptRunResult Result, ScriptLog Log) Run(string script, bool dryRun = false, string? install = null, EditorDocument? doc = null)
    {
        doc ??= LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, script, dryRun: dryRun, services: d => Services(d, install));
        return (doc, r, log);
    }

    [Fact]
    public void Obj_Cube_Becomes_One_Detail_Brush_With_Twelve_Faces()
    {
        (EditorDocument doc, ScriptRunResult r, ScriptLog log) = Run(
            $"local m = level.import_mesh{{path = '{Lua("cube.obj")}', pos = {{4, 1, 0}}, tags = {{src = 'cube'}}}}\n" +
            "assert(m.brushes.count == 1 and m.triangles == 12 and m.groups == 1)\n" +
            "assert(m.object == nil and m.group == nil and m.file == nil)\n" +
            "assert(m.textures[1] == 'crate01.tga' and #m.missing_textures == 0)\n" +
            "local b = m.brushes:get(1)\nassert(b.detail and not b.air)\nassert(b:tags().src == 'cube')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(1, r.UndoNodesAdded);
        Brush b = Assert.Single(new BrushEditor(doc).Brushes);
        Assert.Equal(12, b.Geometry.Faces.Count);
        Assert.All(b.Geometry.Faces, f => Assert.Equal(3, f.Vertices.Count));
        Assert.Equal(new Vec3(4, 1, 0), b.Position);
        Assert.True((b.Flags & (uint)BrushFlags.Detail) != 0);
        Assert.Equal(new[] { Path.Combine(_dir, "cube.obj") }, _authorised);
        Assert.DoesNotContain(log.Entries, e => e.Level == ScriptLogLevel.Warning);
    }

    [Fact]
    public void Scale_Axis_And_Rot_Apply()
    {
        (EditorDocument doc, ScriptRunResult r, _) = Run(
            $"level.import_mesh{{path = '{Lua("pyramid.obj")}', scale = 2, axis = 'z_up', rot = {{yaw = 90}}, name = 'pyr'}}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Brush b = Assert.Single(new BrushEditor(doc).Brushes);
        Assert.NotEqual(Mat3.Identity, b.Rotation);

        // Same model without options: the scaled + Z-up result has its local extents doubled and Y/Z swapped.
        (EditorDocument plain, ScriptRunResult r2, _) = Run($"level.import_mesh{{path = '{Lua("pyramid.obj")}'}}");
        Assert.True(r2.Success, r2.Error?.ToDisplayString());
        Aabb a = GeometryUtil.LocalBounds(new BrushEditor(plain).Brushes.Single().Geometry);
        Aabb s = GeometryUtil.LocalBounds(b.Geometry);
        Assert.Equal((a.P2.Y - a.P1.Y) * 2, s.P2.Z - s.P1.Z, 4);
        Assert.Equal((a.P2.Z - a.P1.Z) * 2, s.P2.Y - s.P1.Y, 4);
    }

    [Fact]
    public void Import_Is_Deterministic_And_Undo_Restores_Bytes()
    {
        string script = $"level.import_mesh{{path = '{Lua("cube.obj")}', pos = {{1, 2, 3}}}}";
        (EditorDocument a, _, _) = Run(script);
        (EditorDocument b, _, _) = Run(script);
        Assert.Equal(a.SaveToBytes(updateTimestamp: false), b.SaveToBytes(updateTimestamp: false));

        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        Assert.True(Run(script, doc: doc).Result.Success);
        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void A_Path_Outside_The_Allowed_Folders_Is_Refused_Before_Reading()
    {
        string outside = Path.Combine(Path.GetTempPath(), "ged-outside-" + Guid.NewGuid().ToString("N") + ".obj");
        File.Copy(TestPaths.Fixture("mesh", "cube.obj"), outside);
        try
        {
            (EditorDocument doc, ScriptRunResult r, _) = Run($"level.import_mesh{{path = '{outside.Replace('\\', '/')}'}}");
            Assert.False(r.Success);
            Assert.StartsWith("level.import_mesh: refusing to read", r.Error!.Message);
            Assert.Empty(_authorised);
            Assert.Empty(new BrushEditor(doc).Brushes);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Without_A_Read_Policy_Import_Is_Not_Available()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, $"level.import_mesh{{path = '{Lua("cube.obj")}'}}");
        Assert.False(r.Success);
        Assert.Equal("level.import_mesh: file import is not available in this host.", r.Error!.Message);
    }

    [Theory]
    [InlineData("{path = 'x.stl'}", "level.import_mesh: 'x.stl' is not a supported model file.", "Use one of: .obj, .gltf, .glb, .fbx, .dae.")]
    [InlineData("{path = 'cube.obj', as = 'brush'}", "level.import_mesh: unknown 'as' value 'brush'.", "Valid values: detail, mesh.")]
    [InlineData("{path = 'cube.obj', scale = 0}", "level.import_mesh: 'scale' must be 0.0001 to 10000 (got 0).", "Nearest valid value: 0.0001.")]
    [InlineData("{path = 'cube.obj', axis = 'yup'}", "level.import_mesh: unknown axis 'yup'.", "Did you mean: z_up?")]
    [InlineData("{pos = {0, 0, 0}}", "level.import_mesh: 'path' is required.", "The model file, e.g. path = \"rock.obj\" (relative to the level's folder, then <scripts>/import).")]
    public void Errors_Have_A_Message_And_Hint(string table, string message, string? hint)
    {
        (_, ScriptRunResult r, _) = Run("level.import_mesh" + table);
        Assert.False(r.Success);
        Assert.Equal(message, r.Error!.Message);
        Assert.Equal(hint, r.Error.Hint);
        Assert.Empty(_authorised);
    }

    [Fact]
    public void Relative_Paths_Resolve_Against_The_Level_Folder()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        doc.Path = Path.Combine(_dir, "level.rfl");
        (_, ScriptRunResult r, _) = Run("level.import_mesh{path = 'cube.obj'}", doc: doc);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(new[] { Path.Combine(_dir, "cube.obj") }, _authorised);
    }

    [Fact]
    public void As_Mesh_Writes_A_V3m_Once_And_Is_Refused_In_Dry_Run()
    {
        string install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        string script = $"local m = level.import_mesh{{path = '{Lua("cube.obj")}', as = 'mesh', name = 'crate box', pos = {{0, 1, 0}}}}\n" +
                        "assert(m.object ~= nil and m.brushes.count == 0)\nassert(m.object.kind == 'MeshObject')\nreturn m.file";
        string v3m = Path.Combine(install, "user_maps", "meshes", "crate_box.v3m");

        (_, ScriptRunResult dry, _) = Run(script, dryRun: true, install: install);
        Assert.False(dry.Success);
        Assert.Contains("dry-run", dry.Error!.Message);
        Assert.False(File.Exists(v3m));

        (EditorDocument doc, ScriptRunResult r, _) = Run(script, install: install);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.True(File.Exists(v3m));
        byte[] bytes = File.ReadAllBytes(v3m);
        Assert.Contains(doc.Objects, o => o.Kind == LevelObjectKind.MeshObject);

        // Identical bytes: allowed again (no write). Different content under the same name: refused.
        Assert.True(Run(script, install: install).Result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(v3m));
        (_, ScriptRunResult clash, _) = Run(script.Replace("cube.obj", "pyramid.obj"), install: install);
        Assert.False(clash.Success);
        Assert.Equal("level.import_mesh: user_maps/meshes/crate_box.v3m already exists with different content; it is never overwritten.", clash.Error!.Message);
        Assert.Equal("Pass a different name.", clash.Error.Hint);
        Assert.Equal(bytes, File.ReadAllBytes(v3m));
    }
}
