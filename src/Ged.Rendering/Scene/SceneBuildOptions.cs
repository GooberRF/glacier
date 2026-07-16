using System.Collections.Generic;
using Ged.Core.Tables;

namespace Ged.Rendering.Scene;

/// <summary>Toggles controlling what <see cref="SceneBuilder"/> emits.</summary>
public sealed class SceneBuildOptions
{
    /// <summary>
    /// When non-null, only static-geometry faces whose room index is in this set
    /// are emitted — the room-graph visibility filter for "Render Using Portals"
    /// (reachable rooms from the camera) and "Render Current Room Only" (one room).
    /// Movers/objects are unaffected. Null = draw every room (default).
    /// </summary>
    public HashSet<int>? VisibleRooms { get; set; }

    /// <summary>Draw each point object as a small bounding box outline (stock "Show objects as Bounding Boxes").</summary>
    public bool ShowBoundingBoxes { get; set; }

    /// <summary>Draw nav-point connection lines with direction arrows (stock "Show Path Node Connections").</summary>
    public bool ShowPathNodeConnections { get; set; }

    /// <summary>
    /// Portal-face draw mode (stock View menu three-way): None (default, hidden),
    /// SeeThru (translucent) or Opaque. Portal faces are emitted with the
    /// <see cref="PortalFaceColor"/> tint into the alpha pass (see-thru) or the
    /// opaque pass (non-see-thru).
    /// </summary>
    public PortalFaceDrawMode PortalFaces { get; set; } = PortalFaceDrawMode.None;

    /// <summary>Portal-face tint (RGB used; alpha is set by the draw mode). From the portal-brush element colour.</summary>
    public uint? PortalFaceColor { get; set; }

    /// <summary>
    /// Compat shim over <see cref="PortalFaces"/>: true = see-thru, false = none.
    /// Retained so existing boolean callers keep working.
    /// </summary>
    public bool IncludePortalFaces
    {
        get => PortalFaces != PortalFaceDrawMode.None;
        set => PortalFaces = value ? PortalFaceDrawMode.SeeThru : PortalFaceDrawMode.None;
    }

    /// <summary>
    /// Render faces flagged <c>show_sky</c> as the editor aid — a semitransparent
    /// sky-blue quad with a "SHOW SKY" label — instead of their wall texture. The host
    /// sets this always in the brush edit modes and, in object/group modes, to follow
    /// the "Draw Sky" setting. Off by default (plain compiled sky pass).
    /// </summary>
    public bool ShowSkyFaceAid { get; set; }

    /// <summary>Include faces flagged invisible. Off by default.</summary>
    public bool IncludeInvisibleFaces { get; set; }

    /// <summary>Include detail-brush faces. On by default.</summary>
    public bool IncludeDetailFaces { get; set; } = true;

    /// <summary>
    /// Emit the compiled <c>static_geometry</c> section. Off while editing brushes
    /// (the source brushes are rendered instead, avoiding z-fighting); the compiled
    /// preview returns after Build.
    /// </summary>
    public bool IncludeStaticGeometry { get; set; } = true;

    /// <summary>Render mover brushes at their keyframe-0 transform. On by default.</summary>
    public bool IncludeMovers { get; set; } = true;

    /// <summary>Emit object billboards / meshes. On by default.</summary>
    public bool IncludeObjects { get; set; } = true;

    /// <summary>Emit object link lines ("Show Links"). On by default.</summary>
    public bool IncludeLinks { get; set; } = true;

    /// <summary>
    /// Draw the in-viewport facing arrow for directional objects — oriented events (Teleport,
    /// Play_Vclip, Clone_Entity, Anchor_Marker_Orient, …), MP respawn points, the Player Start
    /// and directional coronas — the arrow Alpine RED renders for oriented objects. This is the
    /// single "Show Event Arrows" gate: it governs every directional facing arrow, since they are
    /// all the same orange shaft+head indicator. On by default; drawn with the object billboards.
    /// </summary>
    public bool EventFacingArrows { get; set; } = true;

    /// <summary>Directional-event facing-arrow colour (RGBA). Null = the OverlayBuilder default.</summary>
    public uint? EventArrowColor { get; set; }

    /// <summary>Emit light range spheres. On by default (still subject to the per-object range gate).</summary>
    public bool IncludeLightRanges { get; set; } = true;

    /// <summary>Emit region (geo/gas/push/climb) outlines. On by default (still subject to the per-object range gate).</summary>
    public bool IncludeRegionOutlines { get; set; } = true;

    /// <summary>
    /// Draw every object's range/region sphere unconditionally (global "Show all
    /// ranges" toggle). Default false — a range visualization is only drawn when its
    /// object is selected (<see cref="SelectedUids"/>), its stock "Always Show Range"
    /// flag is set, or this is true.
    /// </summary>
    public bool ShowAllRanges { get; set; }

    /// <summary>
    /// UIDs of the currently selected objects. A selected object always shows its
    /// range/region visualization even when <see cref="ShowAllRanges"/> is off. Null =
    /// nothing selected.
    /// </summary>
    public HashSet<int>? SelectedUids { get; set; }

    /// <summary>
    /// UIDs of currently selected decals. A selected decal gets ONE semi-transparent filled
    /// face (rendered like a flat portal-face quad) on the +forward side of its extents box —
    /// the side the projection aims at — while the rest of the box stays wireframe. Kept
    /// separate from <see cref="SelectedUids"/> so this niche highlight never re-enables the
    /// scene-baked range spheres (which are drawn by the lightweight selection overlay instead).
    /// Null / empty = no decal highlighted.
    /// </summary>
    public HashSet<int>? SelectedDecalUids { get; set; }

    /// <summary>
    /// "Draw Decals" (perspective-only, default OFF): project each decal's texture onto the
    /// static geometry it faces — world faces clipped to the decal box, UVs projected along the
    /// decal's forward axis, drawn as a depth-biased alpha overlay pass. Recomputed only on a
    /// scene/decal rebuild (never per frame).
    /// </summary>
    public bool DrawDecals { get; set; }

    /// <summary>World size (half-extent) of a billboard glyph.</summary>
    public float BillboardSize { get; set; } = 0.4f;

    /// <summary>Object-link line colour (RGBA), from the preferences element-colour set.</summary>
    public uint? LinkColor { get; set; }

    /// <summary>Bounding-box outline colour (RGBA).</summary>
    public uint? BoundingBoxColor { get; set; }

    /// <summary>Path-node connection colour (RGBA).</summary>
    public uint? PathNodeColor { get; set; }

    /// <summary>Region outline colour (RGBA).</summary>
    public uint? RegionColor { get; set; }

    /// <summary>
    /// Render object glyphs from RED's original (full-colour) icon bitmaps: object
    /// billboards are emitted untinted (white) so the atlas colour passes through.
    /// Particles keep their simulated colour. The atlas texture itself is swapped by
    /// the host (<see cref="Graphics.GraphicsDevice.SetIconAtlas"/>).
    /// </summary>
    public bool UseOriginalIcons { get; set; }

    /// <summary>
    /// Height/width aspect ratios of the resolved ORIGINAL icon bitmaps (from
    /// <see cref="Graphics.IconAtlas.Compose(System.Func{Graphics.EditorIcon, Ged.Core.IO.Tex.TextureImage?}, out System.Collections.Generic.IReadOnlyDictionary{Graphics.EditorIcon, float})"/>).
    /// Only consulted when <see cref="UseOriginalIcons"/> is on: a non-square original
    /// (e.g. RED's 32×64 <c>Icon_MultiPlayerStart.tga</c> → 2.0) renders its billboard at
    /// standard width with the height scaled to the true aspect, instead of squished into
    /// the square cell. Null / missing entries render square (the GED-drawn set is square
    /// by design).
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<Graphics.EditorIcon, float>? OriginalIconAspects { get; set; }

    /// <summary>Optional catalog for resolving entity classes to meshes.</summary>
    public EntityCatalog? Entities { get; set; }

    /// <summary>Optional catalog for resolving clutter classes to meshes.</summary>
    public ClutterCatalog? Clutter { get; set; }

    /// <summary>Optional catalog for resolving item classes to meshes.</summary>
    public ItemCatalog? Items { get; set; }
}
