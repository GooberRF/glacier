using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.App;

/// <summary>
/// File &gt; Import Mesh: loads an OBJ / FBX / glTF / DAE mesh (native OBJ parser or
/// Assimp), applies the chosen scale + axis conversion, and either adds one brush
/// per material group or writes a .v3m into <c>user_maps\meshes</c> and places a
/// Mesh object referencing it. Unmatched textures are reported (they surface later
/// in the packfile scanner as missing).
/// </summary>
public sealed partial class MainWindow
{
    private void InitImportExport()
    {
        _dispatcher.Bind(CommandIds.FileImportMesh, () => _ = ImportMeshAsync(), () => Document is not null);
        InitExport();
        InitPrefabs();
        InitPlaytest();
    }

    private async Task ImportMeshAsync()
    {
        if (Document is null || BrushEd is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Mesh",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mesh files")
                {
                    Patterns = new[] { "*.obj", "*.gltf", "*.glb", "*.fbx", "*.dae" },
                },
            },
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not string path)
        {
            return;
        }

        ImportedModel model;
        try
        {
            model = await Task.Run(() => MeshImporter.Load(path));
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Import failed: {ex.Message}");
            return;
        }

        if (model.Groups.Count == 0)
        {
            _dispatcher.ShowMessage("The mesh contained no triangles.");
            return;
        }

        MeshImportOptions? options = await Dialogs.ImportMeshDialog.ShowAsync(this, model.Format, Path.GetFileName(path));
        if (options is null)
        {
            return;
        }

        MeshImportPipeline.ApplyTransform(model, options);
        string unmatched = ReportUnmatchedTextures(model);

        try
        {
            if (options.Target == MeshImportTarget.MeshObject)
            {
                ImportAsMeshObject(model, path);
            }
            else
            {
                ImportAsBrushes(model);
            }

            _dispatcher.ShowMessage(
                $"Imported {Path.GetFileName(path)}: {model.TotalTriangles} tris, {model.Groups.Count} group(s).{unmatched}");
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Import failed: {ex.Message}");
        }
    }

    private void ImportAsBrushes(ImportedModel model)
    {
        Vec3 at = PlacementPoint;
        IReadOnlyList<Brush> brushes = MeshImportPipeline.ToBrushes(model, new MeshImportOptions(), () => Document!.AllocateUid());
        var uids = new List<int>();
        foreach (Brush b in brushes)
        {
            b.Position = at; // drop the imported model at the camera placement point
            BrushEd!.AddBrush(b, "Import mesh → brush");
            uids.Add(b.Uid);
        }

        BrushEd!.ClearSelection();
        foreach (int uid in uids)
        {
            _session.Selection.SelectBrush(uid, additive: true);
        }

        AfterMutation();
    }

    private void ImportAsMeshObject(ImportedModel model, string sourcePath)
    {
        string meshName = SanitizeMeshName(Path.GetFileNameWithoutExtension(sourcePath)) + ".v3m";
        string meshesDir = MeshesOutputDir();
        Directory.CreateDirectory(meshesDir);
        string outPath = Path.Combine(meshesDir, meshName);

        V3dFile v3d = MeshImportPipeline.ToV3dFile(model, meshName);
        File.WriteAllBytes(outPath, V3dWriter.Write(v3d));

        // Make the new mesh resolvable, then place a Mesh object referencing it.
        ReloadMeshes();
        PlaceFromPalette(LevelObjectKind.MeshObject, meshName);
    }

    /// <summary>Reports textures the imported model references that do not resolve in the VFS.</summary>
    private string ReportUnmatchedTextures(ImportedModel model)
    {
        if (_session.Vfs is not { } vfs)
        {
            return string.Empty;
        }

        var missing = model.ReferencedTextures
            .Where(t => !string.IsNullOrWhiteSpace(t) && vfs.ResolveTexture(t) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return missing.Count == 0
            ? string.Empty
            : $"  Unmatched textures (will be flagged when packing): {string.Join(", ", missing)}";
    }

    private string MeshesOutputDir()
    {
        string root = _session.RfInstallDir
            ?? (Document?.Path is { } lp ? Path.GetDirectoryName(lp) : null)
            ?? Environment.CurrentDirectory;
        return Path.Combine(root, "user_maps", "meshes");
    }

    private static string SanitizeMeshName(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int n = 0;
        foreach (char c in name)
        {
            buffer[n++] = char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_';
        }

        string s = new string(buffer[..n]).Trim('_');
        return string.IsNullOrEmpty(s) ? "mesh" : s;
    }
}
