using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;

namespace Ged.App.Panels;

/// <summary>
/// The surface the dock panels use to reach the open document and ask the shell to
/// react (rebuild the scene, refresh selection highlights, frame an object). The
/// <see cref="MainWindow"/> implements it.
/// </summary>
internal interface IEditorHost
{
    EditorDocument? Document { get; }

    /// <summary>The brush-editing service for the open document (brush inspector), or null.</summary>
    BrushEditor? BrushEditor { get; }

    /// <summary>The mandatory mode/chip-gated entry point for all selection mutations. Panels
    /// must select through this, never through Document/BrushEditor directly (which is enforced
    /// at compile time — those select primitives are internal to the core).</summary>
    SelectionRouter Selection { get; }

    CommandDispatcher Dispatcher { get; }

    /// <summary>Rebuilds the render scene from the document (after visibility/structural edits).</summary>
    void RequestSceneRebuild();

    /// <summary>Refreshes the selection highlight overlay across viewports.</summary>
    void RefreshSelectionOverlay();

    /// <summary>Frames an object in the active viewport ("Jump To" / double-click).</summary>
    void FrameObject(LevelObject o);

    /// <summary>Frames a brush by UID in the perspective viewport (Layers-panel double-click).</summary>
    void FrameBrush(int uid);

    /// <summary>Switches to Face mode with the Texture/UV tab focused (the merged texture tools).</summary>
    void FocusTextureTools();

    /// <summary>Arms the texture eyedropper: the next viewport click on a brush face samples its
    /// texture name and invokes <paramref name="onSampled"/> (item 6). Consumes that click.</summary>
    void ArmTextureEyedropper(System.Action<string> onSampled);

    /// <summary>Points the active viewport at an object ("View From").</summary>
    void ViewFromObject(LevelObject o);

    /// <summary>The measurement annotation currently highlighted from the Outliner, or null.</summary>
    int? SelectedAnnotationId { get; }

    /// <summary>Highlights (and frames) a measurement annotation from the Outliner, or clears with null.</summary>
    void SelectAnnotation(int? id);

    /// <summary>Removes a measurement annotation (undoable) and refreshes.</summary>
    void DeleteAnnotation(int id);

    /// <summary>The world point to drop a new object at (a few metres in front of the active camera).</summary>
    Ged.Core.Model.Vec3 PlacementPoint { get; }

    /// <summary>The link service bound to the current document, or null when no level is open.</summary>
    LinkService? Links { get; }

    /// <summary>The prefab-instance lineage service for the current document, or null.</summary>
    PrefabInstanceService? PrefabInstances { get; }

    /// <summary>The light's projection-cookie filename (item 4), or null when it has none.</summary>
    string? GetLightCookie(int lightUid);

    /// <summary>The light's projection-cookie sharpness (item 6): 1.0 = crisp, 0.0 = blurred.</summary>
    float GetLightCookieSharpness(int lightUid);

    /// <summary>Sets (or clears with null) a light's projection cookie, undoably (item 4).</summary>
    void SetLightCookie(int lightUid, string? cookieFile);

    /// <summary>Sets a light's projection-cookie sharpness, undoably (item 6); no-op with no cookie.</summary>
    void SetLightCookieSharpness(int lightUid, float sharpness);

    /// <summary>Opens a file picker for a cookie image and returns the chosen VFS name, or null if cancelled.</summary>
    System.Threading.Tasks.Task<string?> PickCookieImageAsync();

    /// <summary>Orphans a prefab instance (drops the lineage record; members stay) and refreshes.</summary>
    void OrphanPrefabInstance(int instanceId);

    /// <summary>Selects every member (brushes + objects) of a prefab instance.</summary>
    void SelectPrefabInstanceMembers(int instanceId);

    /// <summary>Selects, frames and rebuilds after a palette placement.</summary>
    void OnObjectPlaced(LevelObject? placed);

    /// <summary>Places an object of the given kind/class at the camera (palette double-click).</summary>
    void PlaceFromPalette(LevelObjectKind kind, string? className);

    /// <summary>Moves the level's (unique) Player Start to the current placement point, undoably.</summary>
    void MovePlayerStartHere();

    /// <summary>Places an event of the given schema at the camera (palette double-click).</summary>
    void PlaceEventFromPalette(Ged.Core.Tables.EventSchema schema);

    /// <summary>Catalog class names for a placeable kind (entity/clutter/item), or empty.</summary>
    System.Collections.Generic.IReadOnlyList<string> ClassNamesFor(LevelObjectKind kind);

    /// <summary>
    /// The clutter palette's subcategory tree from the clutter table's <c>$RFE Level1/Level2</c>
    /// tags (Furniture, Computers, Natural ▸ Plants …), alphabetical at every level. An empty
    /// root when no install/catalog is mounted (item 1).
    /// </summary>
    Ged.Core.Editing.PaletteCategoryNode ClutterCategoryTree();

    /// <summary>
    /// The entity palette's subcategory tree from the entity table's <c>$RFE Level1/Level2</c>
    /// tags (Ultor, Robots, Vehicles, Creatures, Miners …), alphabetical at every level;
    /// editor-internal entities (<c>$RFE Level1: "Ignore"</c>) are excluded. An empty root when
    /// no install/catalog is mounted (item 1).
    /// </summary>
    Ged.Core.Editing.PaletteCategoryNode EntityCategoryTree();

    /// <summary>Plays a VFS-resolved WAV preview (ambient-sound Play); returns false when unsupported (non-wav / no VFS).</summary>
    bool PlaySoundPreview(string fileName);

    /// <summary>Stops any ambient-sound preview.</summary>
    void StopSoundPreview();

    /// <summary>Skin names for the given clutter class from the catalog (door-lock / skins dropdown), or empty.</summary>
    System.Collections.Generic.IReadOnlyList<string> ClutterSkins(string className);

    /// <summary>Loads a cache-backed mesh thumbnail for a palette class row into the image (blank if none).</summary>
    void LoadClassThumbnail(LevelObjectKind kind, string? className, Avalonia.Controls.Image img);

    /// <summary>The level file name used as the dependency-graph root label and packfile name.</summary>
    string LevelLabel { get; }

    /// <summary>Scans the open level's dependencies against the mounted VFS (null when no level / no VFS).</summary>
    System.Threading.Tasks.Task<Ged.Core.Packaging.DependencyScanResult?> ScanDependenciesAsync();

    /// <summary>Builds a packfile plan from a scan (default output path + level name), or null.</summary>
    Ged.Core.Packaging.PackfileBuildPlan? CreatePackfilePlan(Ged.Core.Packaging.DependencyScanResult scan);

    /// <summary>Opens the Create-Level-Packfile dialog pre-populated with the given plan.</summary>
    System.Threading.Tasks.Task OpenPackfileAsync(Ged.Core.Packaging.PackfileBuildPlan plan);
}
