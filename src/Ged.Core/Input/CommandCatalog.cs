using System.Collections.Generic;

namespace Ged.Core.Input;

/// <summary>Stable command ids, referenced by the App when binding execution.</summary>
public static class CommandIds
{
    public const string FileNew = "file.new";
    public const string FileOpen = "file.open";
    public const string FileSave = "file.save";
    public const string FileSaveAs = "file.saveAs";
    public const string FilePlayLevel = "file.playLevel";
    public const string FilePlayFromCamera = "file.playFromCamera";
    public const string FilePlayMulti = "file.playMulti";
    public const string FilePlayMultiFromCamera = "file.playMultiFromCamera";
    public const string FileDialogueText = "file.dialogueText";
    public const string FilePackfile = "file.packfile";
    public const string FileImportMesh = "file.importMesh";
    public const string FileExportMesh = "file.exportMesh";
    public const string FileExportGltf = "file.exportGltf";
    public const string FileExportObj = "file.exportObj";
    public const string FileExportVrml = "file.exportVrml";
    public const string FileSaveAsPrefab = "file.saveAsPrefab";

    public const string ToolsVerifyTextures = "tools.verifyTextures";
    public const string ToolsReloadTextures = "tools.reloadTextures";
    public const string ToolsReloadMeshes = "tools.reloadMeshes";
    public const string ToolsLibraryHealth = "tools.libraryHealth";

    public const string EditUndo = "edit.undo";
    public const string EditRedo = "edit.redo";
    public const string EditCut = "edit.cut";
    public const string EditCopy = "edit.copy";
    public const string EditPaste = "edit.paste";
    public const string EditDelete = "edit.delete";
    public const string EditMorph = "edit.morph";
    public const string EditProperties = "edit.properties";
    public const string EditLevelProperties = "edit.levelProperties";

    public const string SelectInvert = "select.invert";
    public const string SelectByUid = "select.byUid";
    public const string SelectAll = "select.all";
    public const string SelectGrow = "select.grow";
    public const string SelectSameTexture = "select.sameTexture";

    public const string VisHideSelected = "visibility.hideSelected";
    public const string VisUnhideBrushes = "visibility.unhideBrushes";
    public const string VisHideObjects = "visibility.hideObjects";
    public const string VisUnhideObjects = "visibility.unhideObjects";
    public const string VisInvertHidden = "visibility.invertHidden";
    public const string VisHideExcept = "visibility.hideExcept";
    public const string VisUnhideExcept = "visibility.unhideExcept";
    public const string VisLock = "visibility.lock";
    public const string VisUnlockAll = "visibility.unlockAll";

    public const string ModeBrush = "mode.brush";
    public const string ModeFace = "mode.face";
    public const string ModeEdge = "mode.edge";
    public const string ModeVertex = "mode.vertex";
    public const string ModeTexture = "mode.texture";
    public const string ModeObject = "mode.object";
    public const string ModeGroup = "mode.group";

    // Edge mode operators.
    public const string EdgeBevel = "edge.bevel";
    public const string EdgeExtrude = "edge.extrude";
    public const string EdgeCollapse = "edge.collapse";
    public const string EdgeLoopSelect = "edge.loopSelect";
    public const string EdgeRingSelect = "edge.ringSelect";
    public const string EdgeToVerts = "edge.toVerts";
    public const string EdgeToFaces = "edge.toFaces";

    public const string ViewMaximize = "view.maximizeViewport";
    public const string ViewResetLayout = "view.resetLayout";
    public const string ViewCyclePanes = "view.cyclePanes";
    public const string ViewCyclePanesBack = "view.cyclePanesBack";
    public const string ViewToggleCoordSpace = "view.toggleCoordSpace";
    public const string ViewShowLinks = "view.showLinks";
    public const string ViewShowAllRanges = "view.showAllRanges";
    public const string ViewDisableBackfaceCulling = "view.disableBackfaceCulling";
    public const string View1Pane = "view.layout1";
    public const string View2Pane = "view.layout2";
    public const string View4Pane = "view.layout4";
    public const string ViewPortalFacesNone = "view.portalFacesNone";
    public const string ViewPortalFacesSeeThru = "view.portalFacesSeeThru";
    public const string ViewPortalFacesOpaque = "view.portalFacesOpaque";
    public const string ViewIsolateSelection = "view.isolateSelection";
    public const string ViewToggleAnnotations = "view.toggleAnnotations";

    public const string GridBrightness = "grid.brightnessCycle";
    public const string GridSizeUp = "grid.sizeUp";
    public const string GridSizeDown = "grid.sizeDown";
    public const string GridRotationUp = "grid.rotationUp";
    public const string GridRotationDown = "grid.rotationDown";
    public const string ToggleSnap = "grid.toggleSnap";

    public const string BuildGeometry = "build.geometry";
    public const string BuildLightmapUvs = "build.lightmapUvs";
    public const string BuildRelight = "build.relight";
    public const string BuildLightingNoShadows = "build.lightingNoShadows";
    public const string BuildMapsAndLight = "build.mapsAndLight";
    public const string BuildMapsAndLightNoShadows = "build.mapsAndLightNoShadows";
    public const string BuildRemoveLightmaps = "build.removeLightmaps";
    public const string BuildCalcPaths = "build.calcPaths";

    public const string CameraGotoPlayerStart = "camera.gotoPlayerStart";
    public const string CameraTeleportXyz = "camera.teleportXyz";
    public const string CameraOrientAxis = "camera.orientAxis";
    public const string CameraTeleportToObject = "camera.teleportToObject";
    public const string CameraScrollMode = "camera.scrollMode";
    public const string CameraZoomIn = "camera.zoomIn";
    public const string CameraZoomOut = "camera.zoomOut";
    public const string CameraOrthoZoomIn = "camera.orthoZoomIn";
    public const string CameraOrthoZoomOut = "camera.orthoZoomOut";
    public const string CameraPitchUp = "camera.pitchUp";
    public const string CameraPitchDown = "camera.pitchDown";
    public const string CameraHeadingLeft = "camera.headingLeft";
    public const string CameraHeadingRight = "camera.headingRight";
    public const string CameraBankLeft = "camera.bankLeft";
    public const string CameraBankRight = "camera.bankRight";
    public const string CameraSlideLeft = "camera.slideLeft";
    public const string CameraSlideRight = "camera.slideRight";
    public const string CameraUp = "camera.up";
    public const string CameraDown = "camera.down";

    public const string TransformMove = "transform.move";
    public const string TransformRotate = "transform.rotate";

    public const string BrushSnapCutter = "brush.snapCutter";
    public const string BrushCreate = "brush.create";
    public const string BrushStretch = "brush.stretch";
    public const string BrushMoveCenters = "brush.moveCenters";
    public const string BrushReorient = "brush.reorient";
    public const string BrushClip = "brush.clip";
    public const string BrushSnapGrid = "brush.snapGrid";
    public const string BrushDraw = "brush.draw";

    public const string FaceExtrude = "face.extrude";
    public const string FaceBevel = "face.bevel";
    public const string TexReselect = "texture.reselect";
    public const string TexMapBox = "texture.mapBox";
    public const string TexMapPlanar = "texture.mapPlanar";
    public const string TexMapCylinder = "texture.mapCylinder";
    public const string TexSnapMap = "texture.snapMap";
    public const string TexFlipX = "texture.flipX";
    public const string TexFlipY = "texture.flipY";
    public const string TexUvCopy = "texture.uvCopy";
    public const string TexUvPaste = "texture.uvPaste";
    public const string TexApply = "texture.apply";
    public const string TexPick = "texture.pick";
    public const string TexUvUnwrap = "texture.uvUnwrap";
    public const string TexGrow = "texture.grow";

    public const string EditClipDialog = "edit.clipDialog";
    public const string GizmoMove = "gizmo.move";
    public const string GizmoRotate = "gizmo.rotate";
    public const string GizmoScale = "gizmo.scale";
    public const string GizmoNone = "gizmo.none";
    public const string GizmoLocalWorld = "gizmo.localWorld";

    public const string ObjStaple = "object.staple";
    public const string ObjReorient = "object.reorient";
    public const string ObjPlaceCursor = "object.placeCursor";
    public const string ObjSnapToCamera = "object.snapToCamera";
    public const string ObjLink = "object.link";
    public const string ObjBackLink = "object.backLink";
    public const string ObjBreakLink = "object.breakLink";
    public const string ObjEditLinks = "object.editLinks";
    public const string ObjNavConnect = "object.navConnect";
    public const string ObjWaypoints = "object.waypoints";
    public const string ObjConvertToMesh = "object.convertToMesh";

    public const string ToolRuler = "tool.ruler";
    public const string ToolSelect = "tool.select";
    public const string AnnotationsClear = "annotations.clear";

    public const string AppCommandPalette = "app.commandPalette";
    public const string AppSettings = "app.settings";
    public const string HelpTopics = "help.topics";
    public const string HelpAbout = "help.about";

    public const string ScriptConsole = "script.console";
    public const string ScriptEditor = "script.editor";
    public const string ScriptNew = "script.new";
    public const string ScriptRunFile = "script.runFile";
    public const string ScriptReload = "script.reload";
    public const string ScriptApiReference = "script.apiReference";
}

/// <summary>One row of the command catalog: metadata plus each preset's gesture.</summary>
public readonly record struct CommandSpec(
    string Id,
    string Name,
    string Category,
    CommandScope Scope,
    bool Implemented,
    string? RedGesture,
    string? ModernGesture,
    CommandScope? SecondaryScope = null,
    bool HeldKey = false);

/// <summary>
/// The single source of truth for every editor command and its default gesture in
/// the "RED Classic" and "Modern" presets. RED Classic reproduces the full stock
/// hotkey table (research/red-stock-inventory.md §11). Every command now has a live
/// implementation (<see cref="CommandSpec.Implemented"/> is true for all rows); the
/// dispatcher's "not available" path is retained only as a safety net.
/// </summary>
public static class CommandCatalog
{
    private static readonly CommandSpec[] Specs =
    {
        // ---- File ----
        new(CommandIds.FileNew, "New Level", "File", CommandScope.Global, true, "Ctrl+N", "Ctrl+N"),
        new(CommandIds.FileOpen, "Open…", "File", CommandScope.Global, true, "Ctrl+O", "Ctrl+O"),
        new(CommandIds.FileSave, "Save", "File", CommandScope.Global, true, "Ctrl+S", "Ctrl+S"),
        new(CommandIds.FileSaveAs, "Save As…", "File", CommandScope.Global, true, null, "Ctrl+Shift+S"),
        new(CommandIds.FilePlayLevel, "Play Level", "File", CommandScope.Global, true, "F7", null),
        new(CommandIds.FilePlayFromCamera, "Play Level from Camera", "File", CommandScope.Global, true, "F8", null),
        // Alpine muscle memory (the project owner authored Alpine): F9 = Play in Multi,
        // F10 = Play in Multi (camera) in BOTH presets. Open Dialogue Text is menu-only.
        new(CommandIds.FilePlayMulti, "Play in Multi", "File", CommandScope.Global, true, "F9", "F9"),
        new(CommandIds.FilePlayMultiFromCamera, "Play in Multi from Camera", "File", CommandScope.Global, true, "F10", "F10"),
        new(CommandIds.FileDialogueText, "Open Dialogue Text", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FilePackfile, "Create Level Packfile", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FileImportMesh, "Import Mesh…", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FileExportMesh, "Export Selection To Mesh…", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FileExportGltf, "Export Level as glTF…", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FileExportObj, "Export Level as OBJ…", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FileExportVrml, "Export as VRML…", "File", CommandScope.Global, true, null, null),
        new(CommandIds.FileSaveAsPrefab, "Save Selection As Prefab…", "File", CommandScope.Global, true, null, null),
        new(CommandIds.ToolsVerifyTextures, "Verify All Textures", "Tools", CommandScope.Global, true, null, null),
        new(CommandIds.ToolsReloadTextures, "Reload Textures", "Tools", CommandScope.Global, true, null, null),
        new(CommandIds.ToolsReloadMeshes, "Reload Meshes", "Tools", CommandScope.Global, true, null, null),
        new(CommandIds.ToolsLibraryHealth, "Library Health Report", "Tools", CommandScope.Global, true, null, null),

        // ---- Edit ----
        new(CommandIds.EditUndo, "Undo", "Edit", CommandScope.Global, true, "Ctrl+Z", "Ctrl+Z"),
        new(CommandIds.EditRedo, "Redo", "Edit", CommandScope.Global, true, "Ctrl+Y", "Ctrl+Y"),
        new(CommandIds.EditCut, "Cut", "Edit", CommandScope.Global, true, "Ctrl+X", "Ctrl+X"),
        new(CommandIds.EditCopy, "Copy", "Edit", CommandScope.Global, true, "Ctrl+C", "Ctrl+C"),
        new(CommandIds.EditPaste, "Paste", "Edit", CommandScope.Global, true, "Ctrl+V", "Ctrl+V"),
        new(CommandIds.EditDelete, "Delete", "Edit", CommandScope.Global, true, "Delete", "Delete"),
        new(CommandIds.EditMorph, "Morph", "Edit", CommandScope.Global, true, "Ctrl+M", null),
        new(CommandIds.EditProperties, "Properties", "Edit", CommandScope.Global, true, "Ctrl+P", "F2"),
        new(CommandIds.EditLevelProperties, "Level Properties", "Edit", CommandScope.Global, true, null, null),

        // ---- Selection ----
        new(CommandIds.SelectInvert, "Invert Selection", "Selection", CommandScope.Global, true, "I", "Ctrl+I"),
        new(CommandIds.SelectByUid, "Select By UID", "Selection", CommandScope.Global, true, "U", "U"),
        new(CommandIds.SelectAll, "Select All", "Selection", CommandScope.Global, true, null, "Ctrl+A"),
        new(CommandIds.SelectGrow, "Grow Selection", "Selection", CommandScope.Face, true, "Shift+S", null),
        // Shift+D selects same-texture faces in Texture mode AND Face mode (item 4).
        new(CommandIds.SelectSameTexture, "Select Same Texture", "Selection", CommandScope.Face, true, "Shift+D", null),

        // ---- Visibility ----
        new(CommandIds.VisHideSelected, "Hide Selected", "Visibility", CommandScope.Global, true, "H", "H"),
        new(CommandIds.VisUnhideBrushes, "Unhide All Brushes", "Visibility", CommandScope.Global, true, "Shift+H", null),
        new(CommandIds.VisHideObjects, "Hide All Objects", "Visibility", CommandScope.Global, true, "W", null),
        new(CommandIds.VisUnhideObjects, "Unhide All Objects", "Visibility", CommandScope.Global, true, "Shift+W", "Alt+H"),
        new(CommandIds.VisInvertHidden, "Invert Hidden", "Visibility", CommandScope.Global, true, "Ctrl+H", null),
        new(CommandIds.VisHideExcept, "Hide All But Clutter/Entities", "Visibility", CommandScope.Object, true, "X", null),
        new(CommandIds.VisUnhideExcept, "Unhide All But Clutter/Entities", "Visibility", CommandScope.Object, true, "Shift+X", null),
        new(CommandIds.VisLock, "Lock Selected", "Visibility", CommandScope.Global, true, "Q", null),
        new(CommandIds.VisUnlockAll, "Unlock All", "Visibility", CommandScope.Global, true, "Shift+Q", null),

        // ---- Modes ----
        new(CommandIds.ModeBrush, "Brush Mode", "Mode", CommandScope.Global, true, "Shift+B", "1"),
        new(CommandIds.ModeFace, "Face Mode", "Mode", CommandScope.Global, true, "Shift+F", "2"),
        new(CommandIds.ModeEdge, "Edge Mode", "Mode", CommandScope.Global, true, "Shift+E", "Shift+E"),
        new(CommandIds.ModeVertex, "Vertex Mode", "Mode", CommandScope.Global, true, "Shift+V", "3"),
        new(CommandIds.ModeTexture, "Texture / UV Tools (Face)", "Mode", CommandScope.Global, true, "Shift+T", "4"),
        new(CommandIds.ModeObject, "Object Mode", "Mode", CommandScope.Global, true, "Shift+O", "5"),
        new(CommandIds.ModeGroup, "Group Mode", "Mode", CommandScope.Global, true, "Shift+G", "6"),

        // ---- View / layout ----
        // TAB is the sole maximize/restore toggle in both presets (Alpine/RED parity);
        // F4/F5 are intentionally free. Reset Viewport Layout stays reachable (menu +
        // command palette) but unbound — it restores the default 4-pane arrangement,
        // distinct from TAB un-maximizing the active pane.
        new(CommandIds.ViewMaximize, "Maximize Viewport", "View", CommandScope.Global, true, "Tab", "Tab"),
        new(CommandIds.ViewResetLayout, "Reset Viewport Layout", "View", CommandScope.Global, true, null, null),
        new(CommandIds.ViewCyclePanes, "Cycle Pane Layout", "View", CommandScope.Global, true, "F6", null),
        new(CommandIds.ViewCyclePanesBack, "Cycle Pane Layout (Back)", "View", CommandScope.Global, true, "Shift+F6", null),
        new(CommandIds.ViewToggleCoordSpace, "Toggle Global/Local Coords", "View", CommandScope.Global, true, "G", null),
        new(CommandIds.ViewShowLinks, "Toggle Show Links", "View", CommandScope.Global, true, null, null),
        new(CommandIds.ViewShowAllRanges, "Toggle Show All Ranges", "View", CommandScope.Global, true, null, null),
        new(CommandIds.ViewDisableBackfaceCulling, "Toggle Backface Culling", "View", CommandScope.Global, true, null, null),
        new(CommandIds.View1Pane, "1 Pane", "View", CommandScope.Global, true, null, null),
        new(CommandIds.View2Pane, "2 Panes", "View", CommandScope.Global, true, null, null),
        new(CommandIds.View4Pane, "4 Panes", "View", CommandScope.Global, true, null, null),
        // Portal-face draw modes (stock View menu three-way; bindable, no default key).
        new(CommandIds.ViewPortalFacesNone, "Don't Draw Portal Faces", "View", CommandScope.Global, true, null, null),
        new(CommandIds.ViewPortalFacesSeeThru, "Draw See-thru Portal Faces", "View", CommandScope.Global, true, null, null),
        new(CommandIds.ViewPortalFacesOpaque, "Draw Non-see-thru Portal Faces", "View", CommandScope.Global, true, null, null),
        // Isolate Selection (B6). Shift+I is free in both presets; bound in Modern, left
        // unbound in RED Classic (bindable + View menu) to stay close to the stock table.
        new(CommandIds.ViewIsolateSelection, "Isolate Selection", "View", CommandScope.Global, true, null, "Shift+I"),
        // Measure/annotate (B7): show/hide dimension annotations. Menu + bindable, no default key.
        new(CommandIds.ViewToggleAnnotations, "Show Annotations", "View", CommandScope.Global, true, null, null),

        // ---- Grid ----
        new(CommandIds.GridBrightness, "Cycle Grid Brightness", "Grid", CommandScope.Global, true, "\\", "\\"),
        new(CommandIds.GridSizeUp, "Grid Size Up", "Grid", CommandScope.Global, true, "]", "]"),
        new(CommandIds.GridSizeDown, "Grid Size Down", "Grid", CommandScope.Global, true, "[", "["),
        new(CommandIds.GridRotationUp, "Rotation Step Up", "Grid", CommandScope.Global, true, "Shift+]", null),
        new(CommandIds.GridRotationDown, "Rotation Step Down", "Grid", CommandScope.Global, true, "Shift+[", null),
        // Magnet snap for mouse-driven transforms. Unbound by default (bindable +
        // command palette + toolbar magnet); Alt during a drag temporarily inverts it.
        new(CommandIds.ToggleSnap, "Toggle Snap (Magnet)", "Grid", CommandScope.Global, true, null, null),

        // ---- Build ----
        new(CommandIds.BuildGeometry, "Build Geometry", "Build", CommandScope.Global, true, "Space", "Ctrl+B"),
        new(CommandIds.BuildLightmapUvs, "Calculate Lightmaps", "Build", CommandScope.Global, true, "L", "L"),
        new(CommandIds.BuildRelight, "Calculate Lighting", "Build", CommandScope.Global, true, "Shift+L", "Shift+L"),
        new(CommandIds.BuildLightingNoShadows, "Calculate Lighting (No Shadows)", "Build", CommandScope.Global, true, null, null),
        new(CommandIds.BuildMapsAndLight, "Calculate Maps and Light", "Build", CommandScope.Global, true, null, null),
        new(CommandIds.BuildMapsAndLightNoShadows, "Calculate Maps and Light (No Shadows)", "Build", CommandScope.Global, true, null, null),
        new(CommandIds.BuildRemoveLightmaps, "Remove Lightmaps", "Build", CommandScope.Global, true, null, null),
        new(CommandIds.BuildCalcPaths, "Calculate Nav Paths", "Build", CommandScope.Global, true, "Shift+Space", null),

        // ---- Camera ----
        new(CommandIds.CameraGotoPlayerStart, "Go To Player Start", "Camera", CommandScope.Viewport, true, "Home", "Home"),
        new(CommandIds.CameraTeleportXyz, "Teleport To XYZ", "Camera", CommandScope.Viewport, true, "T", null),
        new(CommandIds.CameraOrientAxis, "Orient To World Axis", "Camera", CommandScope.Viewport, true, "C", null),
        new(CommandIds.CameraTeleportToObject, "Frame Selection", "Camera", CommandScope.Viewport, true, "Ctrl+T", "F"),
        new(CommandIds.CameraScrollMode, "Toggle Scroll Mode", "Camera", CommandScope.Viewport, true, "End", null),
        // Continuous camera movement (HeldKey): driven per-frame by the scheme poller from
        // fixed held keys, NOT the dispatcher — excluded from the command palette (one-shot
        // invocation can't reproduce a held movement) but kept in Settings ▸ Input for visibility.
        new(CommandIds.CameraZoomIn, "Zoom In", "Camera", CommandScope.Viewport, true, "A", null, HeldKey: true),
        new(CommandIds.CameraZoomOut, "Zoom Out", "Camera", CommandScope.Viewport, true, "Z", null, HeldKey: true),
        new(CommandIds.CameraOrthoZoomIn, "Ortho Zoom In", "Camera", CommandScope.Viewport, true, "Plus", null, HeldKey: true),
        new(CommandIds.CameraOrthoZoomOut, "Ortho Zoom Out", "Camera", CommandScope.Viewport, true, "Minus", null, HeldKey: true),
        new(CommandIds.CameraPitchUp, "Pitch Up", "Camera", CommandScope.Viewport, true, "Numpad8", null, HeldKey: true),
        new(CommandIds.CameraPitchDown, "Pitch Down", "Camera", CommandScope.Viewport, true, "Numpad2", null, HeldKey: true),
        new(CommandIds.CameraHeadingLeft, "Heading Left", "Camera", CommandScope.Viewport, true, "Numpad4", null, HeldKey: true),
        new(CommandIds.CameraHeadingRight, "Heading Right", "Camera", CommandScope.Viewport, true, "Numpad6", null, HeldKey: true),
        new(CommandIds.CameraBankLeft, "Bank Left", "Camera", CommandScope.Viewport, true, "Numpad7", null),
        new(CommandIds.CameraBankRight, "Bank Right", "Camera", CommandScope.Viewport, true, "Numpad9", null),
        new(CommandIds.CameraSlideLeft, "Slide Left", "Camera", CommandScope.Viewport, true, "Numpad1", null, HeldKey: true),
        new(CommandIds.CameraSlideRight, "Slide Right", "Camera", CommandScope.Viewport, true, "Numpad3", null, HeldKey: true),
        new(CommandIds.CameraUp, "Move Up", "Camera", CommandScope.Viewport, true, "NumpadPlus", null, HeldKey: true),
        new(CommandIds.CameraDown, "Move Down", "Camera", CommandScope.Viewport, true, "NumpadEnter", null, HeldKey: true),

        // ---- Transform ----
        // "Move/Rotate Tool" activate the move/rotate manipulator (the first-class
        // gizmo); the M/R keys also arm the RED keyboard nudge in-viewport (M/R+arrows,
        // M/N+LMB drag) which the viewport intercepts before dispatch — so both the
        // classic keyboard transform and the tool selection stay reachable.
        new(CommandIds.TransformMove, "Move Tool", "Transform", CommandScope.Global, true, "M", "G"),
        new(CommandIds.TransformRotate, "Rotate Tool", "Transform", CommandScope.Global, true, "R", "R"),

        // ---- Brush ----
        new(CommandIds.BrushSnapCutter, "Snap Cutter To Camera", "Brush", CommandScope.Brush, true, "B", null),
        new(CommandIds.BrushCreate, "Create Brush", "Brush", CommandScope.Brush, true, null, null),
        new(CommandIds.BrushStretch, "Stretch (numeric)", "Brush", CommandScope.Brush, true, "D", null),
        new(CommandIds.BrushMoveCenters, "Move Brush Centers", "Brush", CommandScope.Brush, true, "Ctrl+D", null),
        new(CommandIds.BrushReorient, "Reorient", "Brush", CommandScope.Brush, true, "O", null),
        new(CommandIds.BrushClip, "Clip", "Brush", CommandScope.Brush, true, "X", null),
        new(CommandIds.BrushSnapGrid, "Snap To Grid", "Brush", CommandScope.Brush, true, "Ctrl+G", null),
        // Item 8: the interactive three-stage draw-brush. Unbound in both presets
        // (toolbar / Brush panel / palette; rebindable in Settings ▸ Input).
        new(CommandIds.BrushDraw, "Draw Brush", "Brush", CommandScope.Brush, true, null, null),

        // ---- Face / Vertex / Texture ----
        new(CommandIds.FaceExtrude, "Extrude", "Face", CommandScope.Face, true, "Ctrl+E", null),
        new(CommandIds.FaceBevel, "Bevel", "Face", CommandScope.Face, true, "B", null),

        // Edge mode operators. Edge-scoped, so gestures may reuse Face/Vertex keys.
        new(CommandIds.EdgeBevel, "Edge Bevel", "Edge", CommandScope.Edge, true, "B", null),
        new(CommandIds.EdgeExtrude, "Edge Extrude", "Edge", CommandScope.Edge, true, "Ctrl+E", null),
        new(CommandIds.EdgeCollapse, "Edge Collapse", "Edge", CommandScope.Edge, true, null, null),
        new(CommandIds.EdgeLoopSelect, "Select Edge Loop", "Edge", CommandScope.Edge, true, null, null),
        new(CommandIds.EdgeRingSelect, "Select Edge Ring", "Edge", CommandScope.Edge, true, null, null),
        new(CommandIds.EdgeToVerts, "Edges → Vertices", "Edge", CommandScope.Edge, true, null, null),
        new(CommandIds.EdgeToFaces, "Edges → Faces", "Edge", CommandScope.Edge, true, null, null),
        // The texture/UV workflow is now Face-scoped (Texture mode merged into
        // Face mode's Texture/UV tab). Two gestures deliberately collide with existing Face
        // geometry ops — the geometry op keeps the binding, the texture variant loses its
        // gesture and is reached from the Texture/UV tab's toolbar button:
        //   Ctrl+E → Extrude (Planar Map is a toolbar button),
        //   Shift+S → Grow Selection (Grow Faces To Brush is a toolbar button).
        new(CommandIds.TexReselect, "Reselect Previous Faces", "Texture", CommandScope.Face, true, "Backspace", null),
        new(CommandIds.TexMapBox, "Box Map", "Texture", CommandScope.Face, true, "Ctrl+Q", null),
        new(CommandIds.TexMapPlanar, "Planar Map", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexMapCylinder, "Cylinder Map", "Texture", CommandScope.Face, true, "Ctrl+W", null),
        new(CommandIds.TexSnapMap, "Snap Map To Grid", "Texture", CommandScope.Face, true, "Ctrl+G", null),

        // Flip (H/V) and UV copy/paste (Ctrl+C/V) reuse gestures owned by Global
        // commands; they route through the Face-mode Texture/UV-tab context in the App
        // (no separate gesture, so the shipped presets stay conflict-free) plus panel buttons.
        new(CommandIds.TexFlipX, "Flip Map X", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexFlipY, "Flip Map Y", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexUvCopy, "Copy UVs", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexUvPaste, "Paste UVs", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexApply, "Apply Texture", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexPick, "Pick Texture (eyedropper)", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexUvUnwrap, "UV Unwrap Editor", "Texture", CommandScope.Face, true, null, null),
        new(CommandIds.TexGrow, "Grow Faces To Brush", "Texture", CommandScope.Face, true, null, null),

        // ---- Clip / gizmos ----
        new(CommandIds.EditClipDialog, "Clip (two-point plane)", "Brush", CommandScope.Brush, true, null, null),
        new(CommandIds.GizmoMove, "Move Gizmo", "Transform", CommandScope.Global, true, null, null),
        new(CommandIds.GizmoRotate, "Rotate Gizmo", "Transform", CommandScope.Global, true, null, null),
        new(CommandIds.GizmoScale, "Scale Gizmo", "Transform", CommandScope.Global, true, null, null),
        new(CommandIds.GizmoNone, "Toggle Gizmo", "Transform", CommandScope.Global, true, null, null),
        new(CommandIds.GizmoLocalWorld, "Gizmo Local/World", "Transform", CommandScope.Global, true, null, null),

        // ---- Object ----
        new(CommandIds.ObjStaple, "Staple To Face", "Object", CommandScope.Object, true, "S", null),
        new(CommandIds.ObjReorient, "Reorient Object", "Object", CommandScope.Object, true, "O", null),
        new(CommandIds.ObjPlaceCursor, "Place At Cursor", "Object", CommandScope.Object, true, "P", null),
        new(CommandIds.ObjSnapToCamera, "Snap To Camera", "Object", CommandScope.Object, true, "Shift+S", null),
        new(CommandIds.ObjLink, "Link", "Object", CommandScope.Object, true, "K", null),
        new(CommandIds.ObjBackLink, "Back Link", "Object", CommandScope.Object, true, "Ctrl+K", null),
        new(CommandIds.ObjBreakLink, "Break Link", "Object", CommandScope.Object, true, "Shift+K", null),
        new(CommandIds.ObjEditLinks, "Edit Links", "Object", CommandScope.Object, true, "Ctrl+L", null),
        new(CommandIds.ObjNavConnect, "Cycle Nav Connection", "Object", CommandScope.Object, true, "J", null),
        new(CommandIds.ObjWaypoints, "Waypoint List", "Object", CommandScope.Object, true, "Ctrl+W", null),
        new(CommandIds.ObjConvertToMesh, "Convert To Mesh Object", "Object", CommandScope.Object, true, null, null),

        // ---- Measure / annotate (B7) ----
        new(CommandIds.ToolSelect, "Select Tool", "Tools", CommandScope.Global, true, null, null),
        new(CommandIds.ToolRuler, "Ruler (measure)", "Tools", CommandScope.Global, true, null, null),
        new(CommandIds.AnnotationsClear, "Clear Annotations", "Tools", CommandScope.Global, true, null, null),

        // ---- App ----
        new(CommandIds.AppCommandPalette, "Command Palette", "App", CommandScope.Global, true, "Ctrl+Shift+P", "Ctrl+Shift+P"),
        new(CommandIds.AppSettings, "Settings…", "App", CommandScope.Global, true, null, "Ctrl+,"),
        new(CommandIds.HelpTopics, "Help Topics", "App", CommandScope.Global, true, "F1", "F1"),
        new(CommandIds.HelpAbout, "About Glacier", "App", CommandScope.Global, true, null, null),

        // ---- Scripts (Lua) ----
        new(CommandIds.ScriptConsole, "Focus Script Console", "Scripts", CommandScope.Global, true, null, "Ctrl+OemTilde"),
        new(CommandIds.ScriptEditor, "Script Editor…", "Scripts", CommandScope.Global, true, null, null),
        new(CommandIds.ScriptNew, "New Script…", "Scripts", CommandScope.Global, true, null, null),
        new(CommandIds.ScriptRunFile, "Run Script File…", "Scripts", CommandScope.Global, true, null, null),
        new(CommandIds.ScriptReload, "Reload Scripts Library", "Scripts", CommandScope.Global, true, null, null),
        new(CommandIds.ScriptApiReference, "Scripting API Reference", "Scripts", CommandScope.Global, true, null, null),
    };

    public const string RedClassic = "RED Classic";
    public const string Modern = "Modern";

    public static IReadOnlyList<CommandSpec> All => Specs;

    public static IReadOnlyList<string> PresetNames { get; } = new[] { RedClassic, Modern };

    /// <summary>Builds a registry populated with every command definition.</summary>
    public static CommandRegistry BuildRegistry()
    {
        var registry = new CommandRegistry();
        foreach (CommandSpec s in Specs)
        {
            registry.Register(new CommandDefinition
            {
                Id = s.Id,
                DisplayName = s.Name,
                Category = s.Category,
                Scope = s.Scope,
                SecondaryScope = s.SecondaryScope,
                Implemented = s.Implemented,
                HeldKey = s.HeldKey,
            });
        }

        return registry;
    }

    /// <summary>Builds the base gesture map for a named preset.</summary>
    public static Dictionary<string, KeyGesture> BuildPreset(string presetName)
    {
        var map = new Dictionary<string, KeyGesture>();
        foreach (CommandSpec s in Specs)
        {
            string? text = presetName == Modern ? s.ModernGesture : s.RedGesture;
            if (text is not null && KeyGesture.TryParse(text, out KeyGesture g))
            {
                map[s.Id] = g;
            }
        }

        return map;
    }
}
