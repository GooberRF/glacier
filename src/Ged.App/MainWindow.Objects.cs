using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.Model;
using Ged.Core.Tables;
using Vec3 = Ged.Core.Model.Vec3;

namespace Ged.App;

/// <summary>
/// Object-mode: palette placement, link gestures (K / Ctrl+K / Shift+K / Ctrl+L),
/// and the object gestures (snap-to-camera, set-coords, teleport-camera, morph).
/// Placement drops at the active camera (stock double-click semantics).
/// </summary>
public sealed partial class MainWindow
{
    private LinkService? _links;
    private NavGraphService? _navGraph;
    private PrefabInstanceService? _prefabInstances;
    private Ged.Core.Editing.GedObjectMetadataService? _metadata;

    public LinkService? Links => _links;

    /// <summary>The prefab-instance lineage service for the open document, or null.</summary>
    public PrefabInstanceService? PrefabInstances => _prefabInstances;

    /// <summary>The brush-editing service (IEditorHost — drives the brush inspector).</summary>
    public Ged.Core.Editing.BrushEditor? BrushEditor => _session.BrushEditor;

    // ---- Item 4: light projection cookies (object-metadata chunk) -------------

    /// <summary>The light's projection-cookie filename (item 4), or null.</summary>
    public string? GetLightCookie(int lightUid) => _metadata?.Cookie(lightUid);

    /// <summary>The light's projection-cookie sharpness (item 6): 1.0 = crisp, 0.0 = blurred.</summary>
    public float GetLightCookieSharpness(int lightUid) =>
        _metadata?.CookieSharpness(lightUid) ?? Ged.Core.Editing.GedObjectMetadataService.DefaultSharpness;

    /// <summary>
    /// Sets (or clears with null/blank) a light's projection cookie, undoably. The edit marks the
    /// document dirty, which flags lighting-dirty so Preview Lighting / the next bake pick it up.
    /// A pre-existing sharpness (item 6) is preserved when the filename changes.
    /// </summary>
    public void SetLightCookie(int lightUid, string? cookieFile)
    {
        if (_metadata is null)
        {
            return;
        }

        string? file = string.IsNullOrWhiteSpace(cookieFile) ? null : cookieFile.Trim();
        _metadata.SetCookie(lightUid, file, _metadata.CookieSharpness(lightUid));
        _properties.Refresh();
    }

    /// <summary>Sets a light's projection-cookie sharpness (item 6), undoably; no-op when it has no cookie.</summary>
    public void SetLightCookieSharpness(int lightUid, float sharpness)
    {
        if (_metadata is null)
        {
            return;
        }

        _metadata.SetCookieSharpness(lightUid, Math.Clamp(sharpness, 0f, 1f));
    }

    /// <summary>Opens a file picker for a cookie image and returns the chosen VFS-relative name (leaf), or null.</summary>
    public async Task<string?> PickCookieImageAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a projection-cookie image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Cookie image (greyscale mask)")
                {
                    Patterns = new[] { "*.tga", "*.png", "*.jpg", "*.jpeg", "*.dds", "*.vbm" },
                },
            },
        });

        // Cookies resolve through the VFS (the game never needs them, so loose files are fine); store
        // the leaf name so it resolves like any other texture reference.
        return files.Count > 0 ? System.IO.Path.GetFileName(files[0].Name) : null;
    }

    /// <summary>
    /// Mesh-object Browse picker (item 10): opens a .v3m/.v3c/.vfx file picker (starting in the
    /// meshes dir when it exists) and returns the chosen leaf filename with legacy v3d→v3m / vcm→v3c
    /// fixup, or null on cancel. The mesh resolves through the VFS by leaf name like any mesh ref.
    /// </summary>
    public async Task<string?> PickMeshFileAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Choose a mesh (.v3m / .v3c / .vfx)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mesh files") { Patterns = new[] { "*.v3m", "*.v3c", "*.vfx" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        };

        string meshesDir = MeshesOutputDir();
        if (System.IO.Directory.Exists(meshesDir) &&
            await StorageProvider.TryGetFolderFromPathAsync(meshesDir) is { } start)
        {
            options.SuggestedStartLocation = start;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0)
        {
            return null;
        }

        string leaf = System.IO.Path.GetFileName(files[0].Name);
        if (leaf.EndsWith(".v3d", StringComparison.OrdinalIgnoreCase))
        {
            leaf = leaf[..^4] + ".v3m";
        }
        else if (leaf.EndsWith(".vcm", StringComparison.OrdinalIgnoreCase))
        {
            leaf = leaf[..^4] + ".v3c";
        }

        return leaf;
    }

    /// <summary>Plays a VFS-resolved WAV (ambient-sound Play) through the platform
    /// audio-preview backend; false for non-wav or unresolved.</summary>
    public bool PlaySoundPreview(string fileName)
    {
        StopSoundPreview();
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            byte[]? bytes = System.IO.File.Exists(fileName)
                ? System.IO.File.ReadAllBytes(fileName)
                : _session.Vfs?.ReadFile(fileName);
            if (bytes is null)
            {
                return false;
            }

            return Ged.App.Services.AudioPreview.Current.Play(bytes);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void StopSoundPreview() => Ged.App.Services.AudioPreview.Current.Stop();

    /// <summary>
    /// Skin texture variants for a clutter class: resolves the class's V3D via the
    /// catalog and enumerates its material diffuse maps (the mesh's skins). Returns
    /// empty when no install is mounted or the mesh can't be read — the inspector
    /// then offers free-text entry.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> ClutterSkins(string className)
    {
        if (_session.Vfs is not { } vfs || _session.Clutter?.Find(className)?.V3dFilename is not string mesh
            || string.IsNullOrWhiteSpace(mesh))
        {
            return Array.Empty<string>();
        }

        try
        {
            byte[]? data = vfs.ReadFile(mesh);
            if (data is null)
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(mesh);
                data = vfs.ReadFile(baseName + ".v3m") ?? vfs.ReadFile(baseName + ".v3c");
            }

            if (data is null)
            {
                return Array.Empty<string>();
            }

            var skins = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Ged.Core.IO.Mesh.V3dFile v3d = Ged.Core.IO.Mesh.V3dReader.Read(data);
            foreach (Ged.Core.IO.Mesh.V3dSubmesh sm in v3d.Submeshes)
            {
                foreach (Ged.Core.IO.Mesh.V3dMaterial m in sm.Materials)
                {
                    if (!string.IsNullOrWhiteSpace(m.DiffuseMapName) && seen.Add(m.DiffuseMapName))
                    {
                        skins.Add(m.DiffuseMapName);
                    }
                }
            }

            return skins;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// A few metres in front of the active perspective camera (stock "place at
    /// camera"). Uses <see cref="ViewportGrid.CameraSurface"/> — NOT the raw active
    /// pane, which is whatever pane the pointer last crossed on its way to the
    /// palette (usually an ortho pane whose camera is a pan center, not an eye).
    /// </summary>
    public Vec3 PlacementPoint
    {
        get
        {
            Vector3 p = ViewportGrid.PlaceAtCameraPoint(_viewportGrid.CameraSurface);
            return new Vec3(p.X, p.Y, p.Z);
        }
    }

    public System.Collections.Generic.IReadOnlyList<string> ClassNamesFor(LevelObjectKind kind) =>
        _session.ClassNames(kind);

    /// <summary>The clutter palette subcategory tree from the mounted clutter catalog (item 1).</summary>
    public Ged.Core.Editing.PaletteCategoryNode ClutterCategoryTree() =>
        _session.Clutter?.BuildPaletteTree() ?? Ged.Core.Editing.PaletteCategoryNode.Empty;

    /// <summary>The entity palette subcategory tree from the mounted entity catalog (item 1).</summary>
    public Ged.Core.Editing.PaletteCategoryNode EntityCategoryTree() =>
        _session.Entities?.BuildPaletteTree() ?? Ged.Core.Editing.PaletteCategoryNode.Empty;

    /// <summary>
    /// Moves the (unique) Player Start to the placement point, undoably (item 16). When the level
    /// has no Player Start yet (every from-scratch level used to lack one), this CREATES one at the
    /// placement point instead of no-opping — so a level can always be given a valid spawn in one
    /// click rather than being stuck spawning the player in the void.
    /// </summary>
    public void MovePlayerStartHere()
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        Vec3 p = PlacementPoint;
        LevelObject? start = Document.Objects.FirstOrDefault(o => o.Kind == LevelObjectKind.PlayerStart);
        if (start is null)
        {
            LevelObject created = Document.CreatePlayerStart(p);
            _session.Selection.SelectObject(created);
            AfterMutation();
            _dispatcher.ShowMessage("Created Player Start here.");
            return;
        }

        Vec3 old = start.Position;
        Document.EditValue(start.Section, "Move Player Start", old, p, v => start.Position = v);
        _session.Selection.SelectObject(start);
        AfterMutation();
        _dispatcher.ShowMessage("Moved Player Start here.");
    }

    public void OnObjectPlaced(LevelObject? placed)
    {
        if (placed is not null)
        {
            _session.Selection.SelectObject(placed);
        }

        AfterMutation();
        _linkGraph.Refresh();
        if (placed is not null)
        {
            // The object appears at its placement point; the camera stays put. Placing must
            // never yank/frame the view (a jarring jump every time you drop an object) — the
            // user frames explicitly via Outliner "Jump To" / Ctrl+T when they want to.
            _dispatcher.ShowMessage($"Placed {placed.Kind} (uid {placed.Uid}).");
        }
    }

    private void BindObjectCommands()
    {
        _dispatcher.Bind(CommandIds.ObjLink, LinkSelected, () => Document?.Selection.Count >= 2);
        _dispatcher.Bind(CommandIds.ObjBackLink, BackLinkSelected, () => Document?.Selection.Count >= 2);
        _dispatcher.Bind(CommandIds.ObjBreakLink, BreakLinksSelected, () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.ObjEditLinks, () => _ = EditLinksAsync(), () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.ObjSnapToCamera, SnapSelectedToCamera, () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.ObjStaple, StapleSelected, () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.ObjReorient, ReorientSelectedObjects, () => Document?.Selection.Count > 0);
        _dispatcher.Bind(CommandIds.ObjPlaceCursor, PlaceAtCursor, () => Document is not null);
        _dispatcher.Bind(CommandIds.EditMorph, () => _ = MorphAsync(), () => Document?.Selection.Count == 2);
        _dispatcher.Bind(CommandIds.ObjNavConnect, CycleNavConnection, () => SelectedNavPointPair() is not null);
        _dispatcher.Bind(CommandIds.ObjWaypoints, () => _ = ShowWaypointDialogAsync(), () => Document is not null);
        _dispatcher.Bind(CommandIds.ObjConvertToMesh, ConvertSelectionToMesh,
            () => Document?.Selection.Any(ObjectToMeshConverter.CanConvert) == true);
        _dispatcher.Bind(CommandIds.FileDialogueText, () => _ = OpenDialogueTextAsync(), () => Document is not null);
    }

    // ---- Convert clutter/entity → Mesh object (Alpine "To Mesh Object") -------

    /// <summary>
    /// Converts the selected clutter/entity objects into Alpine Mesh objects (gap-inventory item 3):
    /// inherits each class's destructibility and spawns its child coronas / thruster meshes. The
    /// mesh tag points are read from the mounted VFS (V3D prop points), the coronas from effects.tbl
    /// glares. One undo step; a status toast reports what was created + any sole-group skips.
    /// </summary>
    private void ConvertSelectionToMesh()
    {
        if (Document is null)
        {
            return;
        }

        var sources = Document.Selection.Where(ObjectToMeshConverter.CanConvert).ToList();
        if (sources.Count == 0)
        {
            _dispatcher.ShowMessage("Select a clutter or entity object to convert to a mesh.");
            return;
        }

        if (_session.Clutter is null && _session.Entities is null)
        {
            _dispatcher.ShowMessage("No clutter/entity tables are loaded — mount an RF install first.");
            return;
        }

        IMeshTagSource? tags = _session.Vfs is { } vfs
            ? new V3dMeshTagSource(name => ReadMeshBytes(vfs, name))
            : null;
        Func<string, GlareDef?>? glareLookup = _session.Glares is { } g ? g.Find : null;

        EditorDocument.MeshConversionReport report =
            Document.ConvertObjectsToMesh(sources, _session.Clutter, _session.Entities, tags, glareLookup);

        if (report.ConvertedCount == 0)
        {
            _dispatcher.ShowMessage("Nothing converted (no mesh filename for the selected class in the loaded tables).");
            return;
        }

        AfterMutation();

        var parts = new List<string> { $"{report.ConvertedCount} object(s) → Mesh" };
        if (report.ClutterCount > 0)
        {
            parts.Add($"{report.ClutterCount} destructible");
        }

        if (report.CoronaCount > 0)
        {
            parts.Add($"{report.CoronaCount} corona(s)");
        }

        if (report.ThrusterCount > 0)
        {
            parts.Add($"{report.ThrusterCount} thruster(s)");
        }

        string msg = "Converted " + string.Join(", ", parts) + ".";
        if (report.SkippedSoleGroupUids.Count > 0)
        {
            msg += $" Kept UID(s) {string.Join(", ", report.SkippedSoleGroupUids)} in place (sole moving-group members).";
        }

        _dispatcher.ShowMessage(msg);
    }

    /// <summary>Reads a mesh file's bytes from the VFS, retrying with .v3m/.v3c when the class table's
    /// filename lacks (or carries a legacy) extension.</summary>
    private static byte[]? ReadMeshBytes(Ged.Core.Assets.AssetVfs vfs, string mesh)
    {
        byte[]? data = vfs.ReadFile(mesh);
        if (data is null)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(mesh);
            data = vfs.ReadFile(baseName + ".v3m") ?? vfs.ReadFile(baseName + ".v3c");
        }

        return data;
    }

    // ---- Palette placement ----------------------------------------------------

    /// <summary>Places an object of the given kind at the camera; wired from the palette.</summary>
    public void PlaceFromPalette(LevelObjectKind kind, string? className)
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        try
        {
            _lastPlacedKind = kind;
            _lastPlacedClass = className;
            OnObjectPlaced(Document.PlaceObject(kind, SnapPlacement(PlacementPoint), className));
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Place failed: {ex.Message}");
        }
    }

    /// <summary>Places an event of the given schema at the camera; wired from the palette.</summary>
    public void PlaceEventFromPalette(EventSchema schema)
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        OnObjectPlaced(Document.PlaceEvent(schema, PlacementPoint));
    }

    // ---- Links ----------------------------------------------------------------

    private (LevelObject? Primary, List<LevelObject> Others) SplitSelection()
    {
        List<LevelObject> sel = Document?.Selection.ToList() ?? new();
        if (sel.Count == 0)
        {
            return (null, new List<LevelObject>());
        }

        LevelObject primary = sel[0];
        return (primary, sel.Skip(1).ToList());
    }

    private void LinkSelected()
    {
        if (_links is null)
        {
            return;
        }

        var (primary, rest) = SplitSelection();
        if (primary is null || rest.Count == 0)
        {
            _dispatcher.ShowMessage("Select the source, then Ctrl-click targets, then press K.");
            return;
        }

        LinkResult r = _links.LinkOneToMany(primary, rest);
        _dispatcher.ShowMessage(r.Ok ? $"Linked {primary.DisplayName} → {rest.Count} object(s)." : r.Message);
        AfterLinkChange();
    }

    private void BackLinkSelected()
    {
        if (_links is null)
        {
            return;
        }

        var (primary, rest) = SplitSelection();
        if (primary is null || rest.Count == 0)
        {
            return;
        }

        LinkResult r = _links.BackLink(primary, rest);
        _dispatcher.ShowMessage(r.Ok ? $"Back-linked {rest.Count} object(s) → {primary.DisplayName}." : r.Message);
        AfterLinkChange();
    }

    private void BreakLinksSelected()
    {
        if (_links is null || Document is null)
        {
            return;
        }

        bool any = _links.BreakAllLinks(Document.Selection.ToList());
        _dispatcher.ShowMessage(any ? "Broke links on selection." : "No links to break.");
        AfterLinkChange();
    }

    private void AfterLinkChange()
    {
        RebuildScene();
        _linkGraph.Refresh();
        _properties.Refresh();
    }

    private async Task EditLinksAsync()
    {
        var (primary, _) = SplitSelection();
        if (primary is null || _links is null || Document is null)
        {
            return;
        }

        if (LinkModel.LinksOf(primary) is null)
        {
            _dispatcher.ShowMessage(LinkRules.OriginatorMessage);
            return;
        }

        await new LinksDialog(Document, _links, primary, u => { AfterLinkChange(); FrameUid(u); }).ShowDialog(this);
        AfterLinkChange();
    }

    private void FrameUid(int uid)
    {
        if (Document?.FindByUid(uid) is { } o)
        {
            _session.Selection.SelectObject(o);
            FrameObject(o);
        }
    }

    // ---- Nav graph (J connect / Calculate Nav Paths / Waypoint List) ----------

    /// <summary>The two selected nav points (UID-ordered) when exactly two are selected, else null.</summary>
    private (LevelObject A, LevelObject B)? SelectedNavPointPair()
    {
        if (Document is null || Document.Selection.Count != 2)
        {
            return null;
        }

        var navs = Document.Selection
            .Where(o => o.Kind == LevelObjectKind.NavPoint)
            .OrderBy(o => o.Uid)
            .ToList();
        return navs.Count == 2 ? (navs[0], navs[1]) : null;
    }

    /// <summary>Stock J: cycle the connection between the two selected nav points.</summary>
    private void CycleNavConnection()
    {
        if (_navGraph is null)
        {
            return;
        }

        if (SelectedNavPointPair() is not (LevelObject a, LevelObject b))
        {
            _dispatcher.ShowMessage("Select exactly two nav points, then press J to cycle their connection.");
            return;
        }

        NavGraphService.ConnectionState state = _navGraph.CycleConnection(a, b);
        AfterLinkChange();
        _dispatcher.ShowMessage($"Nav connection {a.Uid}{ConnGlyph(state)}{b.Uid}.");
    }

    private static string ConnGlyph(NavGraphService.ConnectionState state) => state switch
    {
        NavGraphService.ConnectionState.Forward => " → ",
        NavGraphService.ConnectionState.Backward => " ← ",
        NavGraphService.ConnectionState.Both => " ↔ ",
        _ => " ╱ ", // none
    };

    /// <summary>Stock "Calculate Nav Paths": auto-connect same-type nav points within a radius.</summary>
    private async Task CalculateNavPathsAsync()
    {
        if (Document is null || _navGraph is null)
        {
            return;
        }

        int navCount = Document.Objects.Count(o => o.Kind == LevelObjectKind.NavPoint);
        if (navCount < 2)
        {
            _dispatcher.ShowMessage("Calculate Nav Paths: place at least two nav points first.");
            return;
        }

        string? text = await InputDialog.ShowAsync(this, "Calculate Nav Paths",
            "Connect same-type nav points within this distance (world units):", "20");
        if (text is null)
        {
            return;
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float dist) || dist <= 0f)
        {
            _dispatcher.ShowMessage("Enter a positive distance.");
            return;
        }

        int added = _navGraph.CalculatePaths(dist);
        AfterLinkChange();
        _dispatcher.ShowMessage(added > 0
            ? $"Calculated nav paths: added {added} connection(s)."
            : "No new nav connections within that distance.");
    }

    /// <summary>Stock "Waypoint List": manage the level's named waypoint lists.</summary>
    private async Task ShowWaypointDialogAsync()
    {
        if (Document is null || _navGraph is null)
        {
            return;
        }

        await new WaypointListDialog(this, _navGraph, Document).ShowDialog(this);
        AfterMutation();
    }

    // ---- Object gestures ------------------------------------------------------

    private void SnapSelectedToCamera()
    {
        if (Document is null || Document.Selection.Count == 0)
        {
            return;
        }

        Vec3 p = PlacementPoint;
        using (Document.Undo.BeginTransaction("Snap to camera"))
        {
            foreach (LevelObject o in Document.Selection.ToList())
            {
                Vec3 old = o.Position;
                Document.EditValue(o.Section, "Snap to camera", old, p, v => o.Position = v);
            }
        }

        AfterMutation();
    }

    private LevelObjectKind? _lastPlacedKind;
    private string? _lastPlacedClass;

    private void StapleSelected()
    {
        if (Document is null || Document.Selection.Count == 0)
        {
            return;
        }

        if (!_session.TryFaceHit(_lastPick, out Vec3 point, out Vec3 normal))
        {
            _dispatcher.ShowMessage("Staple: click a face first (the last picked face is the staple target).");
            return;
        }

        Mat3 frame = OrientationFromNormal(normal);
        using (Document.Undo.BeginTransaction("Staple to face"))
        {
            foreach (LevelObject o in Document.Selection.ToList())
            {
                Vec3 old = o.Position;
                Document.EditValue(o.Section, "Staple", old, point, v => o.Position = v);
                SetModelRotation(o.Model, frame);
                o.MarkDirty();
            }
        }

        AfterMutation();
        _dispatcher.ShowMessage("Stapled to face + oriented to normal.");
    }

    private void ReorientSelectedObjects()
    {
        if (Document is null || Document.Selection.Count == 0)
        {
            return;
        }

        using (Document.Undo.BeginTransaction("Reorient objects"))
        {
            foreach (LevelObject o in Document.Selection.ToList())
            {
                Mat3 old = GetModelRotation(o.Model) ?? Mat3.Identity;
                Document.EditValue(o.Section, "Reorient", old, Mat3.Identity, v => SetModelRotation(o.Model, v));
            }
        }

        AfterMutation();
        _dispatcher.ShowMessage("Reoriented objects to world axes.");
    }

    private void PlaceAtCursor()
    {
        if (Document is null)
        {
            return;
        }

        if (_lastPlacedKind is not LevelObjectKind kind)
        {
            _dispatcher.ShowMessage("Place at cursor (P): place one from the palette first, then P repeats it at the picked point.");
            return;
        }

        Vec3 point = _session.TryFaceHit(_lastPick, out Vec3 hit, out _) ? hit : PlacementPoint;
        OnObjectPlaced(Document.PlaceObject(kind, SnapPlacement(point), _lastPlacedClass));
    }

    private async Task OpenDialogueTextAsync()
    {
        if (Document?.Path is not string rflPath)
        {
            _dispatcher.ShowMessage("Save the level first — dialogue text is <levelname>.txt next to the .rfl.");
            return;
        }

        try
        {
            string txt = System.IO.Path.ChangeExtension(rflPath, ".txt");
            if (!System.IO.File.Exists(txt))
            {
                await System.IO.File.WriteAllTextAsync(txt,
                    $"; Dialogue / message text for {System.IO.Path.GetFileName(rflPath)}\r\n" +
                    "; One message per line; referenced by Message events (strings.tbl index).\r\n");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(txt) { UseShellExecute = true });
            _dispatcher.ShowMessage($"Opened {System.IO.Path.GetFileName(txt)} in the default editor.");
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Open Dialogue Text failed: {ex.Message}");
        }
    }

    /// <summary>Builds an orientation whose Up row is the face normal (staple standing frame).</summary>
    private static Mat3 OrientationFromNormal(Vec3 normal)
    {
        Vec3 up = normal.Normalized();
        Vec3 seed = MathF.Abs(up.Y) < 0.9f ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
        Vec3 right = seed.Cross(up).Normalized();
        Vec3 forward = up.Cross(right).Normalized();
        return new Mat3(forward, right, up);
    }

    private static Mat3? GetModelRotation(object model)
    {
        System.Reflection.PropertyInfo? p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is null)
        {
            return null;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            return (Mat3)p.GetValue(model)!;
        }

        return p.PropertyType == typeof(Mat3?) && p.GetValue(model) is Mat3 m ? m : null;
    }

    private static void SetModelRotation(object model, Mat3 value)
    {
        System.Reflection.PropertyInfo? p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is null || !p.CanWrite)
        {
            return;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            p.SetValue(model, value);
        }
        else if (p.PropertyType == typeof(Mat3?))
        {
            p.SetValue(model, (Mat3?)value);
        }
    }

    private async Task MorphAsync()
    {
        if (Document is null || Document.Selection.Count != 2)
        {
            _dispatcher.ShowMessage("Morph needs exactly two selected objects (stock Object/Group mode only).");
            return;
        }

        List<LevelObject> sel = Document.Selection.ToList();
        string? text = await InputDialog.ShowAsync(this, "Morph", "Blend t (0 = first .. 1 = second):", "0.5");
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
        {
            return;
        }

        Vec3 a = sel[0].Position;
        Vec3 b = sel[1].Position;
        var lerp = new Vec3(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));
        Document.EditValue(sel[0].Section, "Morph", a, lerp, v => sel[0].Position = v);
        AfterMutation();
        _dispatcher.ShowMessage($"Morphed {sel[0].DisplayName} to t={t:0.##}.");
    }

    // ---- Object-mode tool panel ----------------------------------------------

    private Control BuildObjectPanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };
        root.Children.Add(Header("Object Mode"));
        root.Children.Add(Note("Place from the Palette panel (double-click / Place at camera). Ctrl-click adds to selection."));

        root.Children.Add(Header("Links"));
        root.Children.Add(Row(Btn("Link (K)", LinkSelected), Btn("Back Link (Ctrl+K)", BackLinkSelected)));
        root.Children.Add(Row(Btn("Break Links (Shift+K)", BreakLinksSelected), Btn("Edit Links (Ctrl+L)", () => _ = EditLinksAsync())));

        root.Children.Add(Header("Gestures"));
        root.Children.Add(Row(Btn("Snap To Camera (Shift+S)", SnapSelectedToCamera), Btn("Set Coords (Shift+P)…", () => _ = SetCoordsAsync())));
        // Camera-to-object gestures: Teleport Cam frames the object; View From puts the
        // perspective camera at the object's pose (position + orientation).
        root.Children.Add(Row(Btn("Teleport Cam (Ctrl+T)", FrameSelection), Btn("View From", ViewFromSelection)));
        root.Children.Add(Row(Btn("Morph (Ctrl+M)…", () => _ = MorphAsync())));
        root.Children.Add(Note("Shortcuts: Staple (S), Nav-connect (J), Waypoint lists (Ctrl+W)."));
        return root;
    }

    /// <summary>Object-mode "View From" gesture: aims the perspective camera at the selected object's pose.</summary>
    private void ViewFromSelection()
    {
        LevelObject? o = Document?.Selection.FirstOrDefault();
        if (o is null)
        {
            _dispatcher.ShowMessage("Select an object first to View From it.");
            return;
        }

        ViewFromObject(o);
    }

    private async Task SetCoordsAsync()
    {
        LevelObject? o = Document?.Selection.FirstOrDefault();
        if (o is null || Document is null)
        {
            return;
        }

        Vec3 p = o.Position;
        string init = $"{p.X:0.###} {p.Y:0.###} {p.Z:0.###}";
        string? text = await InputDialog.ShowAsync(this, "Set Coordinates", "X Y Z (copy/paste-able):", init);
        string[] parts = (text ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            var nv = new Vec3(x, y, z);
            Document.EditValue(o.Section, "Set coordinates", p, nv, v => o.Position = v);
            AfterMutation();
        }
    }
}
