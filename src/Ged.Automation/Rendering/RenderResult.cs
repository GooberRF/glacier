using System.Collections.Generic;

namespace Ged.Automation.Rendering;

// The JSON schema of the render command's result (camelCase, nulls omitted — AutomationJson). Positions
// are world metres [x, y, z] (left-handed: +X right, +Y up, +Z forward). Pixel coordinates are image
// pixels with (0, 0) at the TOP-LEFT corner of the image; a pixel's centre is at (+0.5, +0.5).

/// <summary><c>render</c>: the written image and everything needed to map its pixels to world coordinates.</summary>
public sealed class RenderResult
{
    /// <summary>The written PNG.</summary>
    public string Path { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>plan | top | bottom | front | back | left | right | iso | camera.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>orthographic | perspective.</summary>
    public string Projection { get; init; } = string.Empty;

    /// <summary>The requested mode (lit, textured, lightmaps, rooms, wireframe, seethrough).</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>The mode actually drawn (differs from <see cref="Mode"/> when lit falls back).</summary>
    public string ModeUsed { get; init; } = string.Empty;

    /// <summary>Why the drawn mode differs from the requested one, or a texture caveat.</summary>
    public string? ModeNote { get; init; }

    /// <summary>True when game textures were mounted (otherwise surfaces are untextured).</summary>
    public bool Textures { get; init; }

    public RenderCameraDto Camera { get; init; } = new();

    /// <summary>
    /// World metres per pixel: exact everywhere in an orthographic view; at the look-at point in a
    /// perspective view.
    /// </summary>
    public double WorldPerPixel { get; init; }

    /// <summary>Orthographic only: the world point at the image's top-left corner (pixel edge, not centre).</summary>
    public double[]? Origin { get; init; }

    /// <summary>
    /// Orthographic only: the world offset of one pixel step right (<c>x</c>) and one step down (<c>y</c>).
    /// <c>world = origin + px * pixelAxes.x + py * pixelAxes.y</c>, with pixel centres at px + 0.5, py + 0.5.
    /// </summary>
    public RenderPixelAxesDto? PixelAxes { get; init; }

    /// <summary>A one-line statement of the pixel ↔ world mapping for this image.</summary>
    public string Mapping { get; init; } = string.Empty;

    /// <summary>
    /// The world-to-clip matrix, 16 numbers row-major (row-vector convention): <c>[cx, cy, cz, cw] =
    /// [x, y, z, 1] · M</c>; then <c>px = (cx/cw · 0.5 + 0.5) · width</c>, <c>py = (0.5 − cy/cw · 0.5) · height</c>.
    /// Works for every view (the general projection, needed for perspective).
    /// </summary>
    public double[] ViewProjection { get; init; } = System.Array.Empty<double>();

    /// <summary>Plan only: the slice.</summary>
    public RenderPlanDto? Plan { get; init; }

    /// <summary>The world box the view was framed on.</summary>
    public RenderBoxDto Framing { get; init; } = new();

    /// <summary>What was framed: "level", "region", "room N", "uid N", or "camera".</summary>
    public string FramedOn { get; init; } = string.Empty;

    /// <summary>The overlay layers drawn.</summary>
    public List<string> Overlays { get; init; } = new();

    /// <summary>
    /// True when the editor's 3-D object icons (lights, spawns, events, triggers, keyframes, facing arrows) were drawn into
    /// a non-plan view; false for a clean view (objects with a game mesh still show as their mesh). Null on a plan.
    /// </summary>
    public bool? Icons { get; init; }

    /// <summary>
    /// What show-sky faces show in a camera view: "sky room N" (the level's sky room drawn from its centre, as the game
    /// draws it), "fill" (no sky room: a flat sky colour), or null (no show-sky face in view, or not a camera view).
    /// </summary>
    public string? Sky { get; init; }

    /// <summary>The legend: what each glyph / colour in the image means.</summary>
    public List<RenderLegendDto> Legend { get; init; } = new();

    /// <summary>Overlay layout diagnostics for tests (not serialized): the legend panel and every grid label.</summary>
    internal ((int X, int Y, int W, int H)? Legend, List<(string Text, (int X, int Y, int W, int H) Rect, bool Drawn)> GridLabels)? Layout { get; init; }

    /// <summary>Every object glyph drawn, with its pixel position (capped; see <see cref="ObjectsTruncated"/>).</summary>
    public List<RenderObjectDto>? Objects { get; init; }

    /// <summary>True when more glyphs were drawn than <see cref="Objects"/> lists.</summary>
    public bool? ObjectsTruncated { get; init; }

    /// <summary>The hole overlay: count of leak markers found by the sealed check (null when not requested).</summary>
    public int? Holes { get; init; }

    public RenderGeometryDto Geometry { get; init; } = new();

    /// <summary>The GPU device that drew the image (e.g. "Direct3D11 (hardware)").</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>Notes worth telling the user (e.g. that the geometry was rebuilt for this render).</summary>
    public List<string>? Notes { get; set; }

    /// <summary>The objects-listed cap.</summary>
    public const int ObjectsLimit = 1000;
}

/// <summary>The camera of a render.</summary>
public sealed class RenderCameraDto
{
    /// <summary>Perspective: the eye. Orthographic: the view centre.</summary>
    public double[] Position { get; init; } = System.Array.Empty<double>();

    /// <summary>The view direction (unit).</summary>
    public double[] Forward { get; init; } = System.Array.Empty<double>();

    /// <summary>The world direction of image-right (unit).</summary>
    public double[] Right { get; init; } = System.Array.Empty<double>();

    /// <summary>The world direction of image-up (unit).</summary>
    public double[] Up { get; init; } = System.Array.Empty<double>();

    /// <summary>Perspective: the look-at point.</summary>
    public double[]? Look { get; init; }

    /// <summary>Perspective: vertical field of view, degrees.</summary>
    public double? FovDegrees { get; init; }

    /// <summary>Perspective: horizontal field of view, degrees (from the vertical one and the image's aspect).</summary>
    public double? HFovDegrees { get; init; }

    /// <summary>Camera view with eyeHeight: the floor height the camera was placed above.</summary>
    public double? Floor { get; init; }

    /// <summary>Orthographic: half the visible world height, metres.</summary>
    public double? OrthoHalfHeight { get; init; }
}

/// <summary>Orthographic pixel step vectors.</summary>
public sealed class RenderPixelAxesDto
{
    /// <summary>World offset of one pixel to the right.</summary>
    public double[] X { get; init; } = System.Array.Empty<double>();

    /// <summary>World offset of one pixel down.</summary>
    public double[] Y { get; init; } = System.Array.Empty<double>();
}

/// <summary>A plan slice.</summary>
public sealed class RenderPlanDto
{
    /// <summary>The storey's floor height.</summary>
    public double Y { get; init; }

    /// <summary>Where <see cref="Y"/> came from ("--y", "floor under the player start", …).</summary>
    public string YSource { get; init; } = string.Empty;

    /// <summary>The cut plane: geometry above it is removed (y + thickness).</summary>
    public double Cut { get; init; }

    /// <summary>The lowest height drawn (y − below).</summary>
    public double Bottom { get; init; }

    /// <summary>Wall cut segments drawn (static geometry crossing the cut plane).</summary>
    public int WallSegments { get; init; }
}

/// <summary>An axis-aligned world box.</summary>
public sealed class RenderBoxDto
{
    public double[] Min { get; init; } = System.Array.Empty<double>();

    public double[] Max { get; init; } = System.Array.Empty<double>();
}

/// <summary>One legend entry.</summary>
public sealed class RenderLegendDto
{
    /// <summary>The glyph shape (disc, diamond, square, triangle, star, cross, ring, dot, line, hatch, arrow).</summary>
    public string Glyph { get; init; } = string.Empty;

    /// <summary>The colour, #RRGGBB.</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>What it marks.</summary>
    public string Meaning { get; init; } = string.Empty;

    /// <summary>How many were drawn (object kinds only).</summary>
    public int? Count { get; init; }
}

/// <summary>One drawn object glyph.</summary>
public sealed class RenderObjectDto
{
    public int Uid { get; init; }

    public string Kind { get; init; } = string.Empty;

    /// <summary>The glyph centre, image pixels (top-left origin).</summary>
    public double[] Pixel { get; init; } = System.Array.Empty<double>();

    /// <summary>The object's world position.</summary>
    public double[] Pos { get; init; } = System.Array.Empty<double>();

    /// <summary>The label drawn next to it (null when it did not fit or labels are off).</summary>
    public string? Label { get; init; }
}

/// <summary>The compiled geometry the image shows.</summary>
public sealed class RenderGeometryDto
{
    /// <summary>The level's compiled-geometry state before the render: none | preview | stale | built.</summary>
    public string Before { get; init; } = string.Empty;

    /// <summary>The build the render ran on its private copy: none | preview | sealed.</summary>
    public string BuiltForRender { get; init; } = "none";

    /// <summary>What the image shows: none | preview | stale | built | sealed.</summary>
    public string Rendered { get; init; } = string.Empty;
}
