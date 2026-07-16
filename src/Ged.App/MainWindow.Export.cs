using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Mesh.Export;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.App;

/// <summary>
/// File &gt; Export: brush selection → V3M ("To Mesh", Alpine parity, replace +
/// reset-origin) and whole-level export to glTF 2.0 / OBJ / VRML (REDUX-derived
/// exporters).
/// </summary>
public sealed partial class MainWindow
{
    private void InitExport()
    {
        _dispatcher.Bind(CommandIds.FileExportMesh, () => _ = ExportSelectionToMeshAsync(), () => (BrushEd?.SelectedBrushes.Count ?? 0) > 0);
        _dispatcher.Bind(CommandIds.FileExportGltf, () => _ = ExportLevelAsync(ExportFormat.Gltf), () => Document is not null);
        _dispatcher.Bind(CommandIds.FileExportObj, () => _ = ExportLevelAsync(ExportFormat.Obj), () => Document is not null);
        _dispatcher.Bind(CommandIds.FileExportVrml, () => _ = ExportLevelAsync(ExportFormat.Vrml), () => Document is not null);
    }

    private enum ExportFormat
    {
        Gltf,
        Obj,
        Vrml,
    }

    // ---- Brush → V3M ("To Mesh") ----------------------------------------------

    private async Task ExportSelectionToMeshAsync()
    {
        if (Document is null || BrushEd is null || BrushEd.SelectedBrushes.Count == 0)
        {
            _dispatcher.ShowMessage("Select one or more brushes first.");
            return;
        }

        // Options dialog (Alpine mesh_export.cpp:503-524): replace-with-mesh-object + reset-origin,
        // previously hard-wired on. Cancel aborts the whole conversion.
        Dialogs.ToMeshOptionsDialog.Result? chosen =
            await Dialogs.ToMeshOptionsDialog.ShowAsync(this, BrushEd.SelectedBrushes.Count);
        if (chosen is not { } opts)
        {
            return;
        }

        try
        {
            var uids = BrushEd.SelectedBrushes.ToList();
            var brushes = uids.Select(u => BrushEd.FindBrush(u)!).Where(b => b is not null).ToList();

            string meshName = SanitizeMeshName(Path.GetFileNameWithoutExtension(LevelFileName())) + "_mesh.v3m";
            V3dFile v3d = BrushMeshExport.ToV3d(meshName, brushes, opts.ResetOrigin, out Vec3 origin);

            string meshesDir = MeshesOutputDir();
            Directory.CreateDirectory(meshesDir);
            File.WriteAllBytes(Path.Combine(meshesDir, meshName), V3dWriter.Write(v3d));
            ReloadMeshes();

            if (opts.ReplaceWithMeshObject)
            {
                // Replace the source brushes with a Mesh object at the (reset or world) origin.
                BrushEd.DeleteBrushes(uids);
                LevelObject? placed = Document.PlaceObject(LevelObjectKind.MeshObject, origin, meshName);
                OnObjectPlaced(placed);
                _dispatcher.ShowMessage($"To Mesh: {brushes.Count} brush(es) → {meshName} (replaced with a Mesh object).");
            }
            else
            {
                _dispatcher.ShowMessage($"To Mesh: exported {brushes.Count} brush(es) → {meshName} (brushes kept).");
            }
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"To Mesh failed: {ex.Message}");
        }
    }

    // ---- Level → glTF / OBJ / VRML --------------------------------------------

    private async Task ExportLevelAsync(ExportFormat format)
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open a level first.");
            return;
        }

        ImportedModel model = GeometryExtract.FromLevelStatic(Document.Rfl);
        if (model.Groups.Count == 0)
        {
            // Fall back to the brush geometry when the level has not been compiled yet.
            model = GeometryExtract.FromBrushes(BrushEd?.Brushes ?? Array.Empty<Brush>());
        }

        if (model.Groups.Count == 0)
        {
            _dispatcher.ShowMessage("Nothing to export (build the geometry or add brushes first).");
            return;
        }

        (string ext, string label) = format switch
        {
            ExportFormat.Gltf => ("gltf", "glTF 2.0"),
            ExportFormat.Obj => ("obj", "Wavefront OBJ"),
            _ => ("wrl", "VRML 2.0"),
        };

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export Level as {label}",
            SuggestedFileName = Path.GetFileNameWithoutExtension(LevelFileName()),
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(label) { Patterns = new[] { "*." + ext } } },
        });

        if (file?.TryGetLocalPath() is not string path)
        {
            return;
        }

        try
        {
            switch (format)
            {
                case ExportFormat.Gltf:
                    string binName = Path.GetFileNameWithoutExtension(path) + ".bin";
                    GltfOutput gltf = GltfExporter.Export(model, binName);
                    File.WriteAllText(path, gltf.Json);
                    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, binName), gltf.Bin);
                    break;
                case ExportFormat.Obj:
                    string mtlName = Path.GetFileNameWithoutExtension(path) + ".mtl";
                    ObjOutput obj = ObjExporter.Export(model, mtlName);
                    File.WriteAllText(path, obj.Obj);
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, mtlName), obj.Mtl);
                    break;
                default:
                    File.WriteAllText(path, VrmlExporter.Export(model));
                    break;
            }

            _dispatcher.ShowMessage($"Exported {label}: {model.TotalTriangles} tris, {model.Groups.Count} material(s) → {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Export failed: {ex.Message}");
        }
    }
}
