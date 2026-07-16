namespace Ged.Rendering.Scene;

/// <summary>
/// The material pass a face is drawn in. Ordering matches the draw order the
/// renderer uses: opaque first (depth write on), then sky, then the blended
/// passes back-to-front-ish (liquid, alpha).
/// </summary>
public enum RenderPass
{
    /// <summary>Solid, depth-writing geometry.</summary>
    Opaque = 0,

    /// <summary>Sky-room faces (<c>show_sky</c>); drawn without depth write, behind the world.</summary>
    Sky = 1,

    /// <summary>Liquid surfaces (<c>liquid_surface</c>); alpha-blended.</summary>
    Liquid = 2,

    /// <summary>Alpha faces (<c>has_alpha</c>); alpha-blended.</summary>
    Alpha = 3,
}

/// <summary>
/// The stock View-menu three-way portal-face draw mode. Portal faces (texture
/// index −1) are normally hidden; the see-thru / non-see-thru options render them
/// with the portal-brush element tint so their placement is visible.
/// </summary>
public enum PortalFaceDrawMode
{
    /// <summary>Don't draw portal faces (default — RED parity).</summary>
    None = 0,

    /// <summary>Draw portal faces translucent (alpha-blended), so you can see through them.</summary>
    SeeThru = 1,

    /// <summary>Draw portal faces opaque, so they read as solid dividers.</summary>
    Opaque = 2,
}
