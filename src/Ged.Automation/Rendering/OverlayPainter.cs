using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering;

namespace Ged.Automation.Rendering;

/// <summary>
/// Paints the render overlays on the CPU, after the GPU frame (so the parity-gated GPU path never sees
/// them): plan cut edges, the metric grid with coordinate labels, the scale bar, object glyphs by kind,
/// UID labels, link lines, hole markers, the axis indicator and a legend. All positions come from the
/// same <see cref="Camera"/> that drew the frame (<see cref="Camera.WorldToScreen"/>), so a glyph sits
/// exactly where its object renders. Deterministic: objects are processed in a fixed (kind priority,
/// UID) order and label placement is greedy in that order.
/// </summary>
internal sealed class OverlayPainter
{
    private static readonly Rgba CutColor = new(12, 12, 14);
    private static readonly Rgba MoverCutColor = new(230, 90, 230);
    private static readonly Rgba GridColor = new(255, 255, 255, 46);
    private static readonly Rgba GridZeroColor = new(255, 255, 255, 110);
    private static readonly Rgba LinkColor = new(255, 220, 80, 190);
    private static readonly Rgba HoleColor = new(255, 40, 40);
    private static readonly Rgba TextColor = new(240, 240, 240);
    private static readonly Rgba SolidSwatch = new(36, 38, 44);

    private readonly RgbaCanvas _c;
    private readonly Camera _cam;
    private readonly RenderSpec _spec;
    private readonly bool _ortho;
    private readonly PlanSlice? _plan;
    private readonly EditorDocument _doc;
    private readonly LevelGeometryIndex _index;
    private readonly IReadOnlyList<Vec3>? _holes;
    private readonly float _u;
    private readonly int _ts;
    private readonly List<(int X, int Y, int W, int H)> _occupied = new();
    private readonly List<(int X, int Y, int W, int H)> _panels = new();
    private readonly List<RenderLegendDto> _baseLegend = new();

    public OverlayPainter(RgbaCanvas canvas, Camera camera, RenderSpec spec, bool ortho, PlanSlice? plan, EditorDocument doc, LevelGeometryIndex index, IReadOnlyList<Vec3>? holes)
    {
        _c = canvas;
        _cam = camera;
        _spec = spec;
        _ortho = ortho;
        _plan = plan;
        _doc = doc;
        _index = index;
        _holes = holes;
        int min = Math.Min(canvas.Width, canvas.Height);
        _u = MathF.Max(1f, MathF.Round(min / 512f));
        _ts = Math.Clamp((int)_u, 1, 4);
    }

    /// <summary>The overlay layers actually drawn.</summary>
    public RenderOverlays Drawn { get; private set; }

    /// <summary>Where the legend panel was drawn (null when it was not): layout diagnostics for tests.</summary>
    internal (int X, int Y, int W, int H)? LegendRect { get; private set; }

    /// <summary>Every grid label the grid wanted, where, and whether it was drawn: layout diagnostics for tests.</summary>
    internal List<(string Text, (int X, int Y, int W, int H) Rect, bool Drawn)> GridLabels { get; } = new();

    /// <summary>
    /// The strips along the top (x labels) and down the left (z labels) the grid labels occupy; corner panels are
    /// placed inside them, so the legend, scale bar and axis indicator never cover an axis label. Zero without a grid.
    /// </summary>
    private (int Top, int Left) _strips;

    /// <summary>The legend (also what the JSON result reports).</summary>
    public List<RenderLegendDto> Legend { get; } = new();

    /// <summary>Every object glyph drawn.</summary>
    public List<RenderObjectDto> Objects { get; } = new();

    private int W => _c.Width;

    private int H => _c.Height;

    private float Wpp => _cam.WorldPerPixel(Vector3.Zero, H);

    /// <summary>Plan base layer: wall cut edges (dark) and mover cuts (magenta) at the cut plane.</summary>
    public void DrawCutEdges(List<(Vector3 A, Vector3 B)> walls, List<(Vector3 A, Vector3 B)> movers, string? openBelowColor)
    {
        _baseLegend.Add(new RenderLegendDto { Glyph = "hatch", Color = Hex(SolidSwatch), Meaning = "solid / outside" });
        if (openBelowColor is not null)
        {
            _baseLegend.Add(new RenderLegendDto { Glyph = "fill", Color = openBelowColor, Meaning = "open to below (dimmed)" });
        }

        _baseLegend.Add(new RenderLegendDto { Glyph = "line", Color = Hex(CutColor), Meaning = $"wall cut at y={F(_plan!.Cut)}" });
        float t = MathF.Max(1.5f, _u * 1.25f);
        foreach ((Vector3 a, Vector3 b) in walls)
        {
            if (Project(a) is Vector2 pa && Project(b) is Vector2 pb)
            {
                _c.Line(pa, pb, CutColor, t);
            }
        }

        if (movers.Count > 0)
        {
            _baseLegend.Add(new RenderLegendDto { Glyph = "line", Color = Hex(MoverCutColor), Meaning = "mover at keyframe 0" });
            foreach ((Vector3 a, Vector3 b) in movers)
            {
                if (Project(a) is Vector2 pa && Project(b) is Vector2 pb)
                {
                    _c.Line(pa, pb, MoverCutColor, t + 0.5f);
                }
            }
        }
    }

    /// <summary>Draws every requested overlay, then the legend panel.</summary>
    public void DrawOverlays(List<string> titleLines)
    {
        RenderOverlays want = _spec.Overlays;
        bool any = want != RenderOverlays.None;
        Legend.AddRange(_baseLegend);
        List<Glyph> glyphs = (want & (RenderOverlays.Objects | RenderOverlays.Labels | RenderOverlays.Links)) != 0
            ? CollectGlyphs()
            : new List<Glyph>();

        Legend.AddRange(ObjectLegend(glyphs, want));
        List<Vector2> holePixels = new();
        if ((want & RenderOverlays.Holes) != 0)
        {
            foreach (Vec3 hole in _holes ?? Array.Empty<Vec3>())
            {
                if (Project(LevelRenderer.ToV(hole)) is Vector2 p)
                {
                    holePixels.Add(p);
                }
            }
        }

        if (!any)
        {
            return;
        }

        _strips = LabelStrips();
        // Corner allocation: the legend takes the emptiest corner; the scale bar and axis indicator
        // take the bottom corners that are left.
        (int lw, int lh, List<(string Text, RenderLegendDto? Entry)> lines) = LegendLayout(titleLines, Legend);
        int pad = 6 * _ts;
        string[] corners = { "tl", "tr", "bl", "br" };
        bool legend = (want & RenderOverlays.Legend) != 0;
        string legendCorner = legend
            ? corners.OrderBy(k => CornerScore(k, lw, lh, glyphs)).ThenBy(k => Array.IndexOf(corners, k)).First()
            : "none";
        (int lx, int ly) = Place(legendCorner, lw, lh, pad);
        if (legend)
        {
            _occupied.Add((lx, ly, lw, lh));
            LegendRect = (lx, ly, lw, lh);
        }

        var used = new HashSet<string> { legendCorner };
        string scaleCorner = new[] { "bl", "br", "tl", "tr" }.First(k => !used.Contains(k));
        used.Add(scaleCorner);
        string axisCorner = new[] { "br", "bl", "tr", "tl" }.First(k => !used.Contains(k));

        // Reserve the panels first, so grid and object labels keep clear of them.
        if (legend)
        {
            _panels.Add((lx, ly, lw, lh));
        }

        if ((want & RenderOverlays.Scale) != 0 && _ortho)
        {
            (_, _, int sw, int sh, _) = ScaleLayout();
            (int sx, int sy) = Place(scaleCorner, sw, sh, pad);
            _panels.Add((sx, sy, sw, sh));
            _occupied.Add((sx, sy, sw, sh));
        }

        int ab = AxisBox();
        (int ax, int ay) = Place(axisCorner, ab, ab, pad);
        _panels.Add((ax, ay, ab, ab));
        _occupied.Add((ax, ay, ab, ab));

        if ((want & RenderOverlays.Grid) != 0 && _ortho)
        {
            DrawGrid();
            Drawn |= RenderOverlays.Grid;
        }

        if ((want & RenderOverlays.Objects) != 0)
        {
            foreach (Glyph g in glyphs)
            {
                DrawExtents(g);
            }
        }

        if ((want & RenderOverlays.Links) != 0)
        {
            DrawLinks(glyphs);
            Drawn |= RenderOverlays.Links;
        }

        if ((want & RenderOverlays.Holes) != 0)
        {
            float r = 7f * _u;
            foreach (Vector2 p in holePixels)
            {
                _c.StrokeCircle(p, r, Rgba.Black, 3.5f * _u * 0.5f + 1.5f);
                _c.StrokeCircle(p, r, HoleColor, 1.5f * _u * 0.5f + 0.5f);
                _c.Line(p - new Vector2(r * 0.6f), p + new Vector2(r * 0.6f), HoleColor, _u);
                _c.Line(p + new Vector2(-r * 0.6f, r * 0.6f), p + new Vector2(r * 0.6f, -r * 0.6f), HoleColor, _u);
            }

            Drawn |= RenderOverlays.Holes;
        }

        if ((want & RenderOverlays.Objects) != 0)
        {
            // Least important first, so a spawn or the player start is never hidden under a light.
            foreach (Glyph g in Enumerable.Reverse(glyphs))
            {
                DrawGlyph(g.Style.Shape, g.Screen, Radius(g.Style), g.Style.Color);
                if (g.Facing is Vector2 dir)
                {
                    float r = Radius(g.Style);
                    Vector2 tip = g.Screen + (dir * r * 2.4f);
                    _c.Line(g.Screen + (dir * r), tip, Rgba.Black, (2.6f * _u * 0.5f) + 1.5f);
                    _c.Line(g.Screen + (dir * r), tip, g.Style.Color, (1.4f * _u * 0.5f) + 0.6f);
                }

                float rr = Radius(g.Style) + 1;
                _occupied.Add(((int)(g.Screen.X - rr), (int)(g.Screen.Y - rr), (int)(rr * 2) + 1, (int)(rr * 2) + 1));
            }

            Drawn |= RenderOverlays.Objects;
        }

        var labels = new Dictionary<Glyph, string>();
        if ((want & RenderOverlays.Labels) != 0)
        {
            foreach (Glyph g in glyphs)
            {
                if (g.Label is { Length: > 0 } text && PlaceLabel(g, text))
                {
                    labels[g] = text;
                }
            }

            Drawn |= RenderOverlays.Labels;
        }

        foreach (Glyph g in glyphs)
        {
            Objects.Add(new RenderObjectDto
            {
                Uid = g.Uid,
                Kind = g.Kind,
                Pixel = new[] { Math.Round(g.Screen.X, 1), Math.Round(g.Screen.Y, 1) },
                Pos = Dto.V(new Vec3(g.World.X, g.World.Y, g.World.Z)),
                Label = labels.TryGetValue(g, out string? l) ? l : null,
            });
        }

        if ((want & RenderOverlays.Scale) != 0 && _ortho)
        {
            DrawScaleBar(scaleCorner, pad);
            Drawn |= RenderOverlays.Scale;
        }

        DrawAxes(axisCorner, pad);
        if ((want & RenderOverlays.Legend) != 0)
        {
            DrawLegend(lx, ly, lw, lh, lines);
            Drawn |= RenderOverlays.Legend;
        }
    }

    /// <summary>The legend entries for the object glyphs, links and holes.</summary>
    private List<RenderLegendDto> ObjectLegend(List<Glyph> glyphs, RenderOverlays want)
    {
        var entries = new List<RenderLegendDto>();
        if ((want & RenderOverlays.Objects) != 0)
        {
            foreach (IGrouping<string, Glyph> g in glyphs.GroupBy(x => x.Style.Legend).OrderBy(g => g.Min(x => x.Style.Priority)).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                GlyphStyle s = g.First().Style;
                entries.Add(new RenderLegendDto { Glyph = s.Shape, Color = Hex(s.Color), Meaning = s.Legend, Count = g.Count() });
            }
        }

        if ((want & RenderOverlays.Links) != 0 && glyphs.Any(x => x.Links.Count > 0))
        {
            entries.Add(new RenderLegendDto { Glyph = "arrow", Color = Hex(LinkColor), Meaning = "link (to target)" });
        }

        if ((want & RenderOverlays.Holes) != 0)
        {
            entries.Add(new RenderLegendDto
            {
                Glyph = "ring",
                Color = Hex(HoleColor),
                Meaning = _holes is { Count: > 0 } ? "hole (sealed-build leak)" : "holes: none (watertight)",
                Count = _holes?.Count ?? 0,
            });
        }

        return entries;
    }

    /// <summary>
    /// The legend panel's size (and the corner padding) before anything is drawn, so an orthographic
    /// view can be framed with room for it. Counts every object in the slice whether or not it is on
    /// screen yet, and assumes the plan's "open to below" line — an upper bound.
    /// </summary>
    public (int W, int H, int Pad) EstimateLegend(List<string> titleLines, bool movers)
    {
        var entries = new List<RenderLegendDto>();
        if (_plan is PlanSlice plan)
        {
            entries.Add(new RenderLegendDto { Glyph = "hatch", Meaning = "solid / outside" });
            entries.Add(new RenderLegendDto { Glyph = "fill", Meaning = "open to below (dimmed)" });
            entries.Add(new RenderLegendDto { Glyph = "line", Meaning = $"wall cut at y={F(plan.Cut)}" });
            if (movers)
            {
                entries.Add(new RenderLegendDto { Glyph = "line", Meaning = "mover at keyframe 0" });
            }
        }

        RenderOverlays want = _spec.Overlays;
        List<Glyph> glyphs = (want & (RenderOverlays.Objects | RenderOverlays.Links)) != 0 ? CollectGlyphs(screenFilter: false) : new List<Glyph>();
        entries.AddRange(ObjectLegend(glyphs, want));
        (int w, int h, _) = LegendLayout(titleLines, entries);

        // The panel sits inside the grid's label strips: the level must clear the strip as well.
        (int top, int left) = LabelStrips();
        return (w + (2 * BitmapFont.Advance * _ts) + left, h + top, 6 * _ts);
    }

    // ---- glyph collection ------------------------------------------------------------------------

    private sealed record GlyphStyle(string Shape, Rgba Color, string Legend, int Priority, float Size, bool Labelled = true);

    private sealed class Glyph
    {
        public required int Uid { get; init; }

        public required string Kind { get; init; }

        public required GlyphStyle Style { get; init; }

        public required Vector3 World { get; init; }

        public required Vector2 Screen { get; init; }

        public Vector2? Facing { get; init; }

        public string? Label { get; init; }

        public LevelObject? Object { get; init; }

        public List<int> Links { get; init; } = new();
    }

    private List<Glyph> CollectGlyphs(bool screenFilter = true)
    {
        var list = new List<Glyph>();
        foreach (LevelObject o in _doc.Objects)
        {
            GlyphStyle? style = StyleFor(o);
            if (style is null)
            {
                continue;
            }

            Vector3 p = LevelRenderer.ToV(o.Position);
            if (!InSlice(p) || Project(p) is not Vector2 s || (screenFilter && !OnScreen(s)))
            {
                continue;
            }

            Vector2? facing = null;
            if (o.Kind is LevelObjectKind.PlayerStart or LevelObjectKind.MpRespawnPoint && ObjectRotation.Get(o.Model) is Mat3 rot)
            {
                Vector3 fwd = LevelRenderer.ToV(rot.Forward);
                if (Project(p + (fwd * 1f)) is Vector2 s2 && Vector2.DistanceSquared(s2, s) > 0.01f)
                {
                    facing = Vector2.Normalize(s2 - s);
                }
            }

            list.Add(new Glyph
            {
                Uid = o.Uid,
                Kind = Dto.Camel(o.Kind.ToString()),
                Style = style,
                World = p,
                Screen = s,
                Facing = facing,
                Label = style.Labelled ? LabelFor(o) : null,
                Object = o,
                Links = LinkModel.LinksOf(o) is { } links ? links.ToList() : new List<int>(),
            });
        }

        var prefabs = new PrefabInstanceService(_doc);
        foreach (PrefabInstanceRecord rec in prefabs.Instances)
        {
            Vector3 p = LevelRenderer.ToV(rec.PivotPosition);
            if (!InSlice(p) || Project(p) is not Vector2 s || (screenFilter && !OnScreen(s)))
            {
                continue;
            }

            string name = rec.PrefabName.Length > 14 ? rec.PrefabName[..14] : rec.PrefabName;
            list.Add(new Glyph
            {
                Uid = rec.InstanceId,
                Kind = "prefab",
                Style = PrefabStyle,
                World = p,
                Screen = s,
                Label = $"prefab {name}",
            });
        }

        return list
            .OrderBy(g => g.Style.Priority)
            .ThenBy(g => g.Uid)
            .ThenBy(g => g.Kind, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly GlyphStyle PrefabStyle = new("square-outline", new Rgba(190, 190, 255), "prefab instance", 6, 1.1f);

    private static GlyphStyle? StyleFor(LevelObject o) => o.Kind switch
    {
        LevelObjectKind.PlayerStart => new("disc", new Rgba(70, 230, 100), "player start", 0, 1.35f),
        LevelObjectKind.MpRespawnPoint when o.Model is MpRespawnPoint r => (r.RedTeam != 0, r.BlueTeam != 0) switch
        {
            (true, false) => new("disc", new Rgba(235, 60, 60), "MP spawn (red)", 1, 1f),
            (false, true) => new("disc", new Rgba(70, 140, 255), "MP spawn (blue)", 1, 1f),
            (true, true) => new("disc", new Rgba(200, 110, 255), "MP spawn (both teams)", 1, 1f),
            _ => new("disc", new Rgba(235, 235, 235), "MP spawn (any team)", 1, 1f),
        },
        LevelObjectKind.MpRespawnPoint => new("disc", new Rgba(235, 235, 235), "MP spawn (any team)", 1, 1f),
        LevelObjectKind.Item => new("diamond", new Rgba(255, 215, 40), "item", 2, 1.05f),
        LevelObjectKind.Trigger => new("square-outline", new Rgba(60, 225, 235), "trigger (+ extent)", 3, 1f),
        LevelObjectKind.Event => new("triangle", new Rgba(255, 140, 40), "event", 4, 1f),
        LevelObjectKind.Keyframe => new("square", new Rgba(235, 90, 210), "keyframe", 5, 0.8f),
        LevelObjectKind.Mover => new("diamond-outline", new Rgba(235, 90, 210), "mover", 5, 1f),
        LevelObjectKind.Entity => new("cross", new Rgba(255, 90, 90), "entity", 7, 1f),
        LevelObjectKind.Light => new("star", new Rgba(255, 245, 160), "light", 8, 0.9f, Labelled: false),
        LevelObjectKind.Clutter => new("square", new Rgba(165, 165, 165), "clutter", 9, 0.7f),
        LevelObjectKind.NavPoint => new("dot", new Rgba(110, 200, 120), "nav point", 11, 1f, Labelled: false),
        LevelObjectKind.RoomEffect or LevelObjectKind.Eax => null,
        _ => new("ring", new Rgba(185, 185, 205), "other object", 10, 0.8f),
    };

    private static string LabelFor(LevelObject o)
    {
        if (o.Kind == LevelObjectKind.PlayerStart)
        {
            return "START"; // the player start has no meaningful UID
        }

        string uid = o.Uid.ToString(CultureInfo.InvariantCulture);
        string name = o.ScriptName ?? string.Empty;
        if (name.Length == 0 || name.Length > 14 || name.Any(ch => ch is < ' ' or > '~')
            || string.Equals(name, o.Kind.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, o.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            return uid;
        }

        return $"{uid} {name}";
    }

    /// <summary>Plan: objects standing over this storey's floors (or within the slice when no floor is under them).</summary>
    private bool InSlice(Vector3 p)
    {
        if (_plan is not PlanSlice plan)
        {
            return true;
        }

        if (plan.Index.FloorBelow(p) is float floor)
        {
            return floor >= plan.Bottom - 0.05f && floor <= plan.Cut;
        }

        return p.Y >= plan.Bottom && p.Y <= plan.Cut + 2f;
    }

    private bool OnScreen(Vector2 s) => s.X >= -4 && s.Y >= -4 && s.X <= W + 4 && s.Y <= H + 4;

    private Vector2? Project(Vector3 world)
    {
        if (!_cam.WorldToScreen(world, W, H, out Vector2 s))
        {
            return null;
        }

        if (!_ortho)
        {
            // Behind the near plane (perspective).
            if (Vector3.Dot(world - _cam.Position, _cam.Forward) < _cam.NearPlane)
            {
                return null;
            }
        }

        return s;
    }

    private float Radius(GlyphStyle s) => ((2.6f * _u) + 2f) * s.Size;

    // ---- drawing ---------------------------------------------------------------------------------

    private void DrawGlyph(string shape, Vector2 c, float r, Rgba color)
    {
        float o = MathF.Max(1.2f, _u * 0.75f);
        switch (shape)
        {
            case "disc":
                _c.FillCircle(c, r + o, Rgba.Black);
                _c.FillCircle(c, r, color);
                break;
            case "ring":
                _c.StrokeCircle(c, r * 0.8f, Rgba.Black, (2f * o) + _u);
                _c.StrokeCircle(c, r * 0.8f, color, _u);
                break;
            case "diamond":
                _c.FillConvex(Diamond(c, r + (o * 1.4f)), Rgba.Black);
                _c.FillConvex(Diamond(c, r), color);
                break;
            case "diamond-outline":
                Outline(Diamond(c, r), color, o);
                break;
            case "square":
                _c.FillConvex(Square(c, (r * 0.8f) + o), Rgba.Black);
                _c.FillConvex(Square(c, r * 0.8f), color);
                break;
            case "square-outline":
                Outline(Square(c, r * 0.8f), color, o);
                break;
            case "triangle":
            {
                Vector2[] t = { new(c.X, c.Y - r), new(c.X + (r * 0.95f), c.Y + (r * 0.75f)), new(c.X - (r * 0.95f), c.Y + (r * 0.75f)) };
                Vector2[] tb = { new(c.X, c.Y - r - (o * 1.8f)), new(c.X + (r * 0.95f) + (o * 1.6f), c.Y + (r * 0.75f) + o), new(c.X - (r * 0.95f) - (o * 1.6f), c.Y + (r * 0.75f) + o) };
                _c.FillConvex(tb, Rgba.Black);
                _c.FillConvex(t, color);
                break;
            }

            case "star":
                for (int pass = 0; pass < 2; pass++)
                {
                    Rgba col = pass == 0 ? Rgba.Black : color;
                    float grow = pass == 0 ? o : 0f;
                    _c.FillCircle(c, (r * 0.45f) + grow, col);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * MathF.PI / 4f;
                        var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                        _c.Line(c + (d * r * 0.5f), c + (d * (r + grow)), col, MathF.Max(1f, _u * 0.6f) + (grow * 2f));
                    }
                }

                break;
            case "cross":
            {
                float k = r * 0.8f;
                _c.Line(c - new Vector2(k), c + new Vector2(k), Rgba.Black, (_u * 1.4f) + (2f * o));
                _c.Line(c + new Vector2(-k, k), c + new Vector2(k, -k), Rgba.Black, (_u * 1.4f) + (2f * o));
                _c.Line(c - new Vector2(k), c + new Vector2(k), color, _u * 1.4f);
                _c.Line(c + new Vector2(-k, k), c + new Vector2(k, -k), color, _u * 1.4f);
                break;
            }

            default: // dot
                _c.FillCircle(c, MathF.Max(1.6f, r * 0.32f) + 0.8f, Rgba.Black);
                _c.FillCircle(c, MathF.Max(1.6f, r * 0.32f), color);
                break;
        }
    }

    private void Outline(Vector2[] pts, Rgba color, float o)
    {
        for (int i = 0; i < pts.Length; i++)
        {
            _c.Line(pts[i], pts[(i + 1) % pts.Length], Rgba.Black, (_u * 1.2f) + (2f * o));
        }

        for (int i = 0; i < pts.Length; i++)
        {
            _c.Line(pts[i], pts[(i + 1) % pts.Length], color, _u * 1.2f);
        }
    }

    private static Vector2[] Diamond(Vector2 c, float r) => new[] { new Vector2(c.X, c.Y - r), new Vector2(c.X + r, c.Y), new Vector2(c.X, c.Y + r), new Vector2(c.X - r, c.Y) };

    private static Vector2[] Square(Vector2 c, float h) => new[] { new Vector2(c.X - h, c.Y - h), new Vector2(c.X + h, c.Y - h), new Vector2(c.X + h, c.Y + h), new Vector2(c.X - h, c.Y + h) };

    /// <summary>Trigger extents (box footprint / sphere radius) in views looking along the vertical.</summary>
    private void DrawExtents(Glyph g)
    {
        if (!_ortho || g.Object?.Model is not Trigger t || MathF.Abs(_cam.Forward.Y) < 0.99f)
        {
            return;
        }

        var col = new Rgba(60, 225, 235, 150);
        if (t.Shape == Trigger.ShapeSphere && t.SphereRadius is float radius && radius > 0f)
        {
            _c.StrokeCircle(g.Screen, radius / Wpp, col, MathF.Max(1f, _u * 0.6f));
        }
        else if (t.Shape == Trigger.ShapeBox && t.BoxWidth is float bw && t.BoxDepth is float bd)
        {
            Mat3 rot = t.Rotation ?? Mat3.Identity;
            Vector3 right = LevelRenderer.ToV(rot.Right) * (bw * 0.5f), fwd = LevelRenderer.ToV(rot.Forward) * (bd * 0.5f);
            Vector3 p = g.World;
            Vector3[] corners = { p - right - fwd, p + right - fwd, p + right + fwd, p - right + fwd };
            var pts = corners.Select(Project).ToArray();
            if (pts.All(x => x is not null))
            {
                for (int i = 0; i < 4; i++)
                {
                    _c.Line(pts[i]!.Value, pts[(i + 1) % 4]!.Value, col, MathF.Max(1f, _u * 0.6f));
                }
            }
        }
    }

    private void DrawLinks(List<Glyph> glyphs)
    {
        // Endpoints: drawn glyphs first, then any object / brush by UID (a link may leave the slice).
        var screen = new Dictionary<int, Vector2>();
        foreach (Glyph g in glyphs)
        {
            if (g.Kind != "prefab")
            {
                screen.TryAdd(g.Uid, g.Screen);
            }
        }

        Vector2? Endpoint(int uid)
        {
            if (screen.TryGetValue(uid, out Vector2 s))
            {
                return s;
            }

            LevelObject? o = _doc.Objects.FirstOrDefault(x => x.Uid == uid);
            if (o is not null)
            {
                return Project(LevelRenderer.ToV(o.Position));
            }

            Brush? b = LevelOperations.Brushes(_doc).FirstOrDefault(x => x.Uid == uid);
            return b is null ? null : Project(LevelRenderer.ToV(b.Position));
        }

        float t = MathF.Max(1f, _u * 0.75f);
        foreach (Glyph g in glyphs)
        {
            foreach (int target in g.Links)
            {
                if (Endpoint(target) is not Vector2 b)
                {
                    continue;
                }

                Vector2 a = g.Screen;
                Vector2 d = b - a;
                float len = d.Length();
                if (len < 2f)
                {
                    continue;
                }

                d /= len;
                Vector2 end = b - (d * (2.6f * _u + 2f));
                _c.Line(a, end, LinkColor, t);
                var n = new Vector2(-d.Y, d.X);
                float head = 3.5f * _u;
                _c.Line(end, end - (d * head) + (n * head * 0.6f), LinkColor, t);
                _c.Line(end, end - (d * head) - (n * head * 0.6f), LinkColor, t);
            }
        }
    }

    private bool PlaceLabel(Glyph g, string text)
    {
        int tw = BitmapFont.Measure(text, _ts), th = BitmapFont.LineHeight(_ts);
        float r = Radius(g.Style);
        int gap = 2 * _ts;
        var candidates = new (int X, int Y)[]
        {
            ((int)(g.Screen.X + r + gap), (int)(g.Screen.Y - (th / 2f))),
            ((int)(g.Screen.X - r - gap - tw), (int)(g.Screen.Y - (th / 2f))),
            ((int)(g.Screen.X - (tw / 2f)), (int)(g.Screen.Y - r - gap - th)),
            ((int)(g.Screen.X - (tw / 2f)), (int)(g.Screen.Y + r + gap)),
        };
        foreach ((int x, int y) in candidates)
        {
            var rect = (x - _ts, y - _ts, tw + (2 * _ts), th + (2 * _ts));
            if (x < 1 || y < 1 || x + tw >= W - 1 || y + th >= H - 1 || _occupied.Any(o => Overlaps(o, rect)))
            {
                continue;
            }

            _c.HaloText(x, y, text, TextColor, _ts);
            _occupied.Add(rect);
            return true;
        }

        return false;
    }

    private static bool Overlaps((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private void DrawGrid()
    {
        float wpp = Wpp;
        Vector3 right = _cam.Right, up = _cam.Up;
        float halfH = _cam.OrthoZoom, halfW = halfH * _cam.AspectRatio;
        Vector3 o = _cam.Position - (right * halfW) + (up * halfH);
        (string hName, int hi) = LevelRenderer.AxisName(right);
        (string vName, int vi) = LevelRenderer.AxisName(up);
        float step = GridStep();
        float stepPx = step / wpp;
        int th = BitmapFont.LineHeight(_ts);

        // Vertical lines: constant world coordinate along the image-right axis.
        float axH = Comp(right, hi) * wpp; // world units per pixel along that axis (signed)
        float startH = Comp(o, hi), endH = startH + (axH * W);
        DrawLines(Math.Min(startH, endH), Math.Max(startH, endH), v => (v - startH) / axH, vertical: true, char.ToLowerInvariant(hName[1]));

        float avV = -Comp(up, vi) * wpp; // world units per pixel down
        float startV = Comp(o, vi), endV = startV + (avV * H);
        DrawLines(Math.Min(startV, endV), Math.Max(startV, endV), v => (v - startV) / avV, vertical: false, char.ToLowerInvariant(vName[1]));

        void DrawLines(float lo, float hi2, Func<float, float> toPx, bool vertical, char axis)
        {
            int k0 = (int)MathF.Ceiling(lo / step), k1 = (int)MathF.Floor(hi2 / step);
            if (k1 - k0 > 400)
            {
                return;
            }

            int labelEvery = 1;
            int widest = BitmapFont.Measure($"{axis}=-{F(MathF.Max(MathF.Abs(lo), MathF.Abs(hi2)))}", _ts) + (6 * _ts);
            while (vertical && stepPx * labelEvery < widest && labelEvery < 64)
            {
                labelEvery *= 2;
            }

            while (!vertical && stepPx * labelEvery < th * 2.2f && labelEvery < 64)
            {
                labelEvery *= 2;
            }

            for (int k = k0; k <= k1; k++)
            {
                float v = k * step;
                float p = toPx(v);
                Rgba col = k == 0 ? GridZeroColor : GridColor;
                if (vertical)
                {
                    _c.Line(new Vector2(p, 0), new Vector2(p, H), col, 1f);
                }
                else
                {
                    _c.Line(new Vector2(0, p), new Vector2(W, p), col, 1f);
                }

                if (k % labelEvery == 0)
                {
                    string label = $"{axis}={F(v)}";
                    int lx = vertical ? (int)p + (2 * _ts) : 2 * _ts;
                    int ly = vertical ? 2 * _ts : (int)p + (2 * _ts);
                    var rect = (lx - _ts, ly - _ts, BitmapFont.Measure(label, _ts) + (2 * _ts), th + (2 * _ts));
                    bool clear = !_panels.Any(pr => Overlaps(pr, rect));
                    GridLabels.Add((label, rect, clear));
                    if (clear)
                    {
                        _c.HaloText(lx, ly, label, new Rgba(220, 220, 220), _ts);
                        _occupied.Add(rect);
                    }
                }
            }
        }
    }

    private static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    /// <summary>The grid spacing in metres: the first round step at least 34 units (scaled) apart on screen.</summary>
    private float GridStep()
    {
        float minPx = 34f * _u;
        float[] nice = { 0.25f, 0.5f, 1f, 2f, 5f, 10f, 20f, 25f, 50f, 100f, 200f, 250f, 500f, 1000f, 2000f, 5000f };
        return nice.FirstOrDefault(s => s / Wpp >= minPx, 10000f);
    }

    /// <summary>
    /// The label strips of the grid (see <see cref="_strips"/>): the height the labels along the top need and the
    /// width of the widest label down the left, with a little air. Zero when no grid is drawn.
    /// </summary>
    private (int Top, int Left) LabelStrips()
    {
        if (!_ortho || (_spec.Overlays & RenderOverlays.Grid) == 0)
        {
            return (0, 0);
        }

        float wpp = Wpp, step = GridStep();
        Vector3 up = _cam.Up;
        float halfH = _cam.OrthoZoom, halfW = halfH * _cam.AspectRatio;
        Vector3 o = _cam.Position - (_cam.Right * halfW) + (up * halfH);
        (string vName, int vi) = LevelRenderer.AxisName(up);
        float startV = Comp(o, vi), endV = startV + (-Comp(up, vi) * wpp * H);
        int k0 = (int)MathF.Ceiling(MathF.Min(startV, endV) / step), k1 = (int)MathF.Floor(MathF.Max(startV, endV) / step);
        char axis = char.ToLowerInvariant(vName[1]);
        int widest = 0;
        for (int k = k0; k <= k1 && k - k0 <= 400; k++)
        {
            widest = Math.Max(widest, BitmapFont.Measure($"{axis}={F(k * step)}", _ts));
        }

        return (BitmapFont.LineHeight(_ts) + (4 * _ts), widest + (4 * _ts));
    }

    /// <summary>The scale bar: its length (m), bar pixels, panel size and label.</summary>
    private (float Len, int BarPx, int W, int H, string Label) ScaleLayout()
    {
        float wpp = Wpp;
        float target = W * 0.2f * wpp;
        float len = 0.1f;
        foreach (float m in new[] { 0.1f, 0.2f, 0.5f, 1f, 2f, 5f, 10f, 20f, 25f, 50f, 100f, 200f, 250f, 500f, 1000f, 2000f, 5000f })
        {
            if (m <= target)
            {
                len = m;
            }
        }

        int barPx = (int)MathF.Round(len / wpp);
        int bh = (int)(3 * _u) + 1;
        string label = $"{F(len)} m";
        int th = BitmapFont.LineHeight(_ts);
        int bw = Math.Max(barPx, BitmapFont.Measure(label, _ts)) + (4 * _ts);
        int bhTotal = th + (3 * _ts) + bh + (4 * _ts);
        return (len, barPx, bw, bhTotal, label);
    }

    private int AxisBox() => (int)(30 * _u) + (2 * BitmapFont.LineHeight(_ts)) + (6 * _ts);

    private void DrawScaleBar(string corner, int pad)
    {
        (_, int barPx, int bw, int bhTotal, string label) = ScaleLayout();
        int bh = (int)(3 * _u) + 1;
        int th = BitmapFont.LineHeight(_ts);
        (int x, int y) = Place(corner, bw, bhTotal, pad);
        _c.FillRect(x, y, bw, bhTotal, new Rgba(0, 0, 0, 225));
        int bx = x + (2 * _ts), by = y + th + (4 * _ts);
        _c.FillRect(bx - 1, by - 1, barPx + 2, bh + 2, Rgba.Black);
        int halfBar = barPx / 2;
        _c.FillRect(bx, by, halfBar, bh, Rgba.White);
        _c.FillRect(bx + halfBar, by, barPx - halfBar, bh, new Rgba(90, 90, 90));
        _c.FillRect(bx, by - (2 * _ts), Math.Max(1, _ts), bh + (2 * _ts), Rgba.White);
        _c.FillRect(bx + barPx - Math.Max(1, _ts), by - (2 * _ts), Math.Max(1, _ts), bh + (2 * _ts), Rgba.White);
        _c.HaloText(bx, y + (2 * _ts), label, TextColor, _ts);
        _occupied.Add((x, y, bw, bhTotal));
    }

    private void DrawAxes(string corner, int pad)
    {
        int size = (int)(30 * _u);
        int th = BitmapFont.LineHeight(_ts);
        int box = AxisBox();
        (int x, int y) = Place(corner, box, box, pad);
        _c.FillRect(x, y, box, box, new Rgba(0, 0, 0, 225));
        var origin = new Vector2(x + (box / 2f), y + (box / 2f));
        if (_ortho)
        {
            Arrow(origin, new Vector2(1, 0), size * 0.5f, new Rgba(255, 110, 110), LevelRenderer.AxisName(_cam.Right).Name);
            Arrow(origin, new Vector2(0, -1), size * 0.5f, new Rgba(110, 160, 255), LevelRenderer.AxisName(_cam.Up).Name);
        }
        else
        {
            Vector3 at = _cam.Position + (_cam.Forward * 10f);
            (Vector3 Axis, Rgba Color, string Name)[] axes =
            {
                (Vector3.UnitX, new Rgba(255, 110, 110), "+X"),
                (Vector3.UnitY, new Rgba(120, 230, 120), "+Y"),
                (Vector3.UnitZ, new Rgba(110, 160, 255), "+Z"),
            };
            if (Project(at) is Vector2 a0)
            {
                foreach ((Vector3 axis, Rgba color, string name) in axes)
                {
                    if (Project(at + axis) is Vector2 a1 && Vector2.DistanceSquared(a0, a1) > 1e-4f)
                    {
                        Vector2 d = Vector2.Normalize(a1 - a0);
                        Arrow(origin, d, size * 0.5f, color, name);
                    }
                }
            }
        }

        _occupied.Add((x, y, box, box));

        void Arrow(Vector2 from, Vector2 dir, float len, Rgba color, string name)
        {
            Vector2 tip = from + (dir * len);
            float t = MathF.Max(1.2f, _u * 0.9f);
            _c.Line(from, tip, color, t);
            var n = new Vector2(-dir.Y, dir.X);
            float head = 4f * _u;
            _c.Line(tip, tip - (dir * head) + (n * head * 0.6f), color, t);
            _c.Line(tip, tip - (dir * head) - (n * head * 0.6f), color, t);
            int tw = BitmapFont.Measure(name, _ts);
            Vector2 lp = tip + (dir * (th * 0.9f));
            _c.HaloText((int)(lp.X - (tw / 2f)), (int)(lp.Y - (th / 2f)), name, color, _ts);
        }
    }

    private (int W, int H, List<(string Text, RenderLegendDto? Entry)> Lines) LegendLayout(List<string> title, List<RenderLegendDto> legend)
    {
        var lines = new List<(string, RenderLegendDto?)>();
        foreach (string t in title)
        {
            lines.Add((t, null));
        }

        foreach (RenderLegendDto e in legend)
        {
            lines.Add((e.Count is int n && e.Glyph is not ("hatch" or "line" or "fill") ? $"{e.Meaning} ({n})" : e.Meaning, e));
        }

        int lineH = BitmapFont.LineHeight(_ts) + (3 * _ts);
        int swatch = 10 * _ts;
        int w = 0;
        foreach ((string text, RenderLegendDto? e) in lines)
        {
            w = Math.Max(w, BitmapFont.Measure(text, _ts) + (e is null ? 0 : swatch + (3 * _ts)));
        }

        int pad = 4 * _ts;
        return (Math.Min(w + (2 * pad), W - 8), (lines.Count * lineH) + (2 * pad) - (3 * _ts), lines);
    }

    private void DrawLegend(int x, int y, int w, int h, List<(string Text, RenderLegendDto? Entry)> lines)
    {
        _c.FillRect(x, y, w, h, new Rgba(0, 0, 0, 225));
        _c.StrokeRect(x, y, w, h, new Rgba(255, 255, 255, 90));
        int pad = 4 * _ts;
        int lineH = BitmapFont.LineHeight(_ts) + (3 * _ts);
        int swatch = 10 * _ts;
        int cy = y + pad;
        foreach ((string text, RenderLegendDto? e) in lines)
        {
            int tx = x + pad;
            if (e is not null)
            {
                Rgba col = ParseHex(e.Color);
                var center = new Vector2(tx + (swatch / 2f), cy + (BitmapFont.LineHeight(_ts) / 2f));
                switch (e.Glyph)
                {
                    case "fill":
                        _c.FillRect(tx, cy, swatch, BitmapFont.LineHeight(_ts), col);
                        _c.StrokeRect(tx, cy, swatch, BitmapFont.LineHeight(_ts), new Rgba(150, 150, 170));
                        break;
                    case "hatch":
                        _c.FillRect(tx, cy, swatch, BitmapFont.LineHeight(_ts), col);
                        _c.StrokeRect(tx, cy, swatch, BitmapFont.LineHeight(_ts), new Rgba(90, 92, 104));
                        break;
                    case "line":
                        _c.FillRect(tx, (int)center.Y - _ts, swatch, Math.Max(2, 2 * _ts), col.A == 255 && col.R < 40 ? new Rgba(150, 150, 150) : col);
                        if (col.R < 40)
                        {
                            _c.FillRect(tx + 1, (int)center.Y - _ts + 1, swatch - 2, Math.Max(1, (2 * _ts) - 2), col);
                        }

                        break;
                    case "arrow":
                        _c.Line(new Vector2(tx, center.Y), new Vector2(tx + swatch, center.Y), col, MathF.Max(1f, _u * 0.75f));
                        _c.Line(new Vector2(tx + swatch, center.Y), new Vector2(tx + swatch - (3 * _ts), center.Y - (2 * _ts)), col, MathF.Max(1f, _u * 0.75f));
                        _c.Line(new Vector2(tx + swatch, center.Y), new Vector2(tx + swatch - (3 * _ts), center.Y + (2 * _ts)), col, MathF.Max(1f, _u * 0.75f));
                        break;
                    default:
                        DrawGlyph(e.Glyph, center, MathF.Min(swatch * 0.42f, (2.6f * _u) + 2f), col);
                        break;
                }

                tx += swatch + (3 * _ts);
            }

            _c.Text(tx, cy, text, TextColor, _ts);
            cy += lineH;
        }
    }

    /// <summary>A corner panel's top-left pixel: inside the grid's label strips, so it never covers an axis label.</summary>
    private (int X, int Y) Place(string corner, int w, int h, int pad)
    {
        (int top, int left) = _strips;
        (int x, int y) = corner switch
        {
            "tl" => (pad + left, pad + top),
            "tr" => (W - pad - w, pad + top),
            "bl" => (pad + left, H - pad - h),
            _ => (W - pad - w, H - pad - h),
        };
        return (Math.Max(0, Math.Min(x, W - w)), Math.Max(0, Math.Min(y, H - h)));
    }

    /// <summary>How much image content a corner rectangle would cover (lower = emptier).</summary>
    private long CornerScore(string corner, int w, int h, List<Glyph> glyphs)
    {
        (int x0, int y0) = Place(corner, w, h, 6 * _ts);
        long score = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + h); y += 2)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + w); x += 2)
            {
                if (IsContent(x, y))
                {
                    score++;
                }
            }
        }

        foreach (Glyph g in glyphs)
        {
            if (g.Screen.X >= x0 && g.Screen.X < x0 + w && g.Screen.Y >= y0 && g.Screen.Y < y0 + h)
            {
                score += 5000; // never cover a glyph if a corner without one exists
            }
        }

        return score;
    }

    private bool IsContent(int x, int y)
    {
        if (_plan is PlanSlice p)
        {
            return !p.Solid[(y * W) + x];
        }

        // The renderer's background is the editor grey (26, 28, 33).
        Rgba c = _c.Get(x, y);
        return Math.Abs(c.R - 26) > 3 || Math.Abs(c.G - 28) > 3 || Math.Abs(c.B - 33) > 3;
    }

    private static string Hex(Rgba c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Rgba ParseHex(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
