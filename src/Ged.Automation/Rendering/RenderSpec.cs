using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Ged.Automation.Rendering;

/// <summary>Which view a render uses.</summary>
public enum RenderView
{
    /// <summary>Orthographic top-down floor-plan slice of one storey (the default).</summary>
    Plan,

    /// <summary>Orthographic, looking down -Y (no slicing).</summary>
    Top,

    /// <summary>Orthographic, looking up +Y.</summary>
    Bottom,

    /// <summary>Orthographic, looking along +Z.</summary>
    Front,

    /// <summary>Orthographic, looking along -Z.</summary>
    Back,

    /// <summary>Orthographic, looking along +X.</summary>
    Left,

    /// <summary>Orthographic, looking along -X.</summary>
    Right,

    /// <summary>A framed three-quarter perspective from above (south-west, looking north-east and down).</summary>
    Iso,

    /// <summary>A perspective camera at an explicit position and look-at point.</summary>
    Camera,
}

/// <summary>The CPU overlay layers a render can draw.</summary>
[Flags]
public enum RenderOverlays
{
    None = 0,

    /// <summary>Metric grid with coordinate labels (orthographic views).</summary>
    Grid = 1,

    /// <summary>Scale bar with a metres label (orthographic views).</summary>
    Scale = 2,

    /// <summary>Object glyphs by kind, with a legend.</summary>
    Objects = 4,

    /// <summary>UID (and short script name) labels next to glyphs.</summary>
    Labels = 8,

    /// <summary>Object link lines.</summary>
    Links = 16,

    /// <summary>Hole markers from a sealed hole check (runs a sealed compile; not applied).</summary>
    Holes = 32,

    /// <summary>The legend panel (view, scale, what each glyph means).</summary>
    Legend = 64,

    /// <summary>The overlays a plan view draws by default.</summary>
    PlanDefault = Grid | Scale | Objects | Labels | Links | Legend,
}

/// <summary>
/// A parsed <c>render</c> request (the <c>args</c> object of the automation request; the CLI maps its
/// flags onto it). Every field is validated here, so a bad request is a usage error before any
/// GPU or build work starts.
/// <para>args: <c>out</c> (.png, required), <c>view</c> (plan|top|bottom|front|back|left|right|iso|camera),
/// <c>y</c>, <c>thickness</c>, <c>below</c> (plan slice), <c>camera</c> / <c>look</c> ([x,y,z]) and
/// <c>fov</c> (vertical degrees) or <c>hfov</c> (horizontal degrees: the vertical field of view follows from the image's aspect),
/// <c>eyeHeight</c> (camera view: put the camera this many metres above the floor under it, keeping the pitch), <c>region</c> ([minx,miny,minz,maxx,maxy,maxz]) | <c>room</c> (index) |
/// <c>uid</c>, <c>mode</c> (lit|textured|lightmaps|rooms|wireframe|seethrough), <c>width</c>,
/// <c>height</c> (a fixed image size), <c>maxSide</c> (without width/height: the image's longest side, default
/// 1024; an orthographic view sizes the image to the framed geometry's or region's proportions), <c>overlays</c> (array of grid|scale|objects|labels|links|holes|legend, or "none"),
/// <c>icons</c> (true|false, or "icons" / "noicons" in the overlays list: the editor's 3-D object icons in a non-plan view;
/// default off for iso and camera views and whenever overlays is "none", on for the other orthographic views),
/// <c>build</c> (auto|preview|sealed|none).</para>
/// </summary>
public sealed class RenderSpec
{
    /// <summary>The largest accepted image edge.</summary>
    public const int MaxSize = 4096;

    /// <summary>The default image edge.</summary>
    public const int DefaultSize = 1024;

    /// <summary>The default plan slice height above the floor (metres): the cut plane.</summary>
    public const float DefaultThickness = 2.0f;

    /// <summary>How far below the floor height the plan slice still shows geometry (metres).</summary>
    public const float DefaultBelow = 1.0f;

    /// <summary>The default perspective field of view (degrees).</summary>
    public const float DefaultFov = 60f;

    public required string OutPath { get; init; }

    public RenderView View { get; init; } = RenderView.Plan;

    /// <summary>The plan's floor height (null = derived; see <see cref="LevelRenderer"/>).</summary>
    public float? PlanY { get; init; }

    public float Thickness { get; init; } = DefaultThickness;

    public float Below { get; init; } = DefaultBelow;

    public Vector3? CameraPosition { get; init; }

    public Vector3? CameraLook { get; init; }

    /// <summary>The perspective field of view (null = 60 for the camera view, 45 for iso).</summary>
    public float? FovDegrees { get; init; }

    /// <summary>
    /// The horizontal field of view in degrees (null = use <see cref="FovDegrees"/>). Given, the vertical field of view is
    /// derived from it and the image's aspect: vfov = 2·atan(tan(hfov / 2) · height / width).
    /// </summary>
    public float? HFovDegrees { get; init; }

    /// <summary>
    /// Camera view: the eye's height above the floor (null = the camera position as given). The camera's Y is the search
    /// start: the camera moves to the highest floor at or below it plus this height, and the look point moves by the
    /// same amount so the pitch is kept.
    /// </summary>
    public float? EyeHeight { get; init; }

    /// <summary>An explicit framing box (min, max).</summary>
    public (Vector3 Min, Vector3 Max)? Region { get; init; }

    public int? Room { get; init; }

    public int? Uid { get; init; }

    /// <summary>lit | textured | lightmaps | rooms | wireframe | seethrough.</summary>
    public string Mode { get; init; } = "lit";

    public int Width { get; init; } = DefaultSize;

    public int Height { get; init; } = DefaultSize;

    /// <summary>
    /// True when the caller fixed the image size (<c>width</c> / <c>height</c>). Otherwise <see cref="Width"/> =
    /// <see cref="Height"/> = the longest side, and a region framing on an orthographic view (plan, top, ...) narrows
    /// the shorter side to the region's proportions so the image shows the region rather than its surroundings.
    /// </summary>
    public bool SizeGiven { get; init; } = true;

    public RenderOverlays Overlays { get; init; }

    /// <summary>
    /// Draw the editor's 3-D object icons (lights, spawns, events, triggers, keyframes, facing arrows) in a non-plan view.
    /// Off gives a clean view: what the game draws (objects with a game mesh show as their mesh). Plans draw object glyphs
    /// as an overlay instead and ignore this.
    /// </summary>
    public bool Icons { get; init; }

    /// <summary>auto | preview | sealed | none.</summary>
    public string Build { get; init; } = "auto";

    /// <summary>The accepted mode names.</summary>
    public static readonly IReadOnlyList<string> Modes = new[] { "lit", "textured", "lightmaps", "rooms", "wireframe", "seethrough" };

    /// <summary>The accepted overlay names, in drawing order.</summary>
    public static readonly IReadOnlyList<string> OverlayNames = new[] { "grid", "scale", "objects", "labels", "links", "holes", "legend" };

    /// <summary>The accepted build policies.</summary>
    public static readonly IReadOnlyList<string> BuildPolicies = new[] { "auto", "preview", "sealed", "none" };

    /// <summary>Parses and validates a request's args (throws a usage <see cref="AutomationException"/>).</summary>
    public static RenderSpec FromArgs(JsonObject args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string outPath = Str(args, "out") ?? throw Usage("render needs an output image path ('out', a .png file).");
        if (!outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw Usage($"The render output must be a .png file: {outPath}");
        }

        Vector3? cam = Vec(args, "camera");
        Vector3? look = Vec(args, "look");
        string? viewName = Str(args, "view");
        RenderView view = viewName is null
            ? (cam is not null ? RenderView.Camera : RenderView.Plan)
            : ParseView(viewName);
        if (view == RenderView.Camera && (cam is null || look is null))
        {
            throw Usage("A camera view needs both a camera position and a look-at point (camera and look, as x,y,z).");
        }

        if (view != RenderView.Camera && (cam is not null || look is not null))
        {
            throw Usage("camera / look only apply to the camera view (omit --view, or use --view camera).");
        }

        if (cam is Vector3 c && look is Vector3 l && Vector3.DistanceSquared(c, l) < 1e-6f)
        {
            throw Usage("The camera position and look-at point must differ.");
        }

        float? y = Num(args, "y");
        float thickness = Num(args, "thickness") ?? DefaultThickness;
        float below = Num(args, "below") ?? DefaultBelow;
        if (view != RenderView.Plan && (y is not null || args.ContainsKey("thickness") || args.ContainsKey("below")))
        {
            throw Usage("y / thickness / below only apply to the plan view.");
        }

        if (thickness is <= 0f or > 1000f)
        {
            throw Usage("thickness must be between 0 and 1000 metres.");
        }

        if (below is < 0f or > 1000f)
        {
            throw Usage("below must be between 0 and 1000 metres.");
        }

        float? fov = Num(args, "fov");
        if (fov is < 5f or > 150f)
        {
            throw Usage("fov must be between 5 and 150 degrees.");
        }

        if (args.ContainsKey("fov") && view is not (RenderView.Camera or RenderView.Iso))
        {
            throw Usage("fov only applies to the camera and iso views.");
        }

        float? hfov = Num(args, "hfov");
        if (hfov is not null)
        {
            if (fov is not null)
            {
                throw Usage("fov (vertical) and hfov (horizontal) are alternatives: give one.");
            }

            if (hfov is < 10f or > 170f)
            {
                throw Usage("hfov must be between 10 and 170 degrees.");
            }

            if (view is not (RenderView.Camera or RenderView.Iso))
            {
                throw Usage("hfov only applies to the camera and iso views.");
            }
        }

        float? eyeHeight = Num(args, "eyeHeight");
        if (eyeHeight is not null)
        {
            if (view != RenderView.Camera)
            {
                throw Usage("eyeHeight only applies to the camera view.");
            }

            if (eyeHeight is < 0.05f or > 200f)
            {
                throw Usage("eyeHeight must be between 0.05 and 200 metres.");
            }
        }

        (Vector3, Vector3)? region = null;
        if (args["region"] is JsonNode rn)
        {
            float[] r = Floats(rn, "region", 6);
            var min = new Vector3(MathF.Min(r[0], r[3]), MathF.Min(r[1], r[4]), MathF.Min(r[2], r[5]));
            var max = new Vector3(MathF.Max(r[0], r[3]), MathF.Max(r[1], r[4]), MathF.Max(r[2], r[5]));
            region = (min, max);
        }

        int? room = Int(args, "room");
        int? uid = Int(args, "uid");
        int framings = (region is null ? 0 : 1) + (room is null ? 0 : 1) + (uid is null ? 0 : 1);
        if (framings > 1)
        {
            throw Usage("Give at most one of region, room, or uid.");
        }

        if (framings > 0 && view == RenderView.Camera)
        {
            throw Usage("region / room / uid frame the plan, ortho and iso views; a camera view is aimed with camera and look.");
        }

        if (room is < 0)
        {
            throw Usage("room must be a room index (0 or more).");
        }

        string mode = (Str(args, "mode") ?? "lit").ToLowerInvariant();
        mode = mode switch
        {
            "room_colors" or "roomcolors" => "rooms",
            "see_through" or "see-through" => "seethrough",
            "wire" => "wireframe",
            _ => mode,
        };
        if (!Modes.Contains(mode))
        {
            throw Usage($"Unknown mode '{mode}' (use {string.Join(", ", Modes)}).");
        }

        bool sizeGiven = args.ContainsKey("width") || args.ContainsKey("height");
        int maxSide = Int(args, "maxSide") ?? DefaultSize;
        int width = Int(args, "width") ?? (sizeGiven ? DefaultSize : maxSide);
        int height = Int(args, "height") ?? (sizeGiven ? DefaultSize : maxSide);
        if (width is < 16 or > MaxSize || height is < 16 or > MaxSize)
        {
            throw Usage($"The image size must be between 16 and {MaxSize} pixels on each side.");
        }

        RenderOverlays overlays = view == RenderView.Plan ? RenderOverlays.PlanDefault : RenderOverlays.None;
        bool overlaysNone = false;
        bool? icons = null;
        if (args["overlays"] is JsonNode on)
        {
            overlays = ParseOverlays(on);
            List<string> named = OverlayTokens(on);
            icons = named.Contains("icons") ? true : named.Contains("noicons") ? false : null;
            overlaysNone = overlays == RenderOverlays.None && icons is null;
        }

        if (Bool(args, "icons") is bool flag)
        {
            if (icons is bool listed && listed != flag)
            {
                throw Usage("icons and the overlays list disagree: give one.");
            }

            icons = flag;
        }

        if (icons is not null && view == RenderView.Plan)
        {
            throw Usage("icons apply to the orthographic, iso and camera views; a plan marks objects with overlay glyphs (overlays objects).");
        }

        // Clean by default where a reviewer looks at the level as a player would (iso, camera), and whenever every overlay
        // is turned off; the orthographic elevations keep the icons as the editor shows them.
        bool iconsOn = icons ?? (!overlaysNone && view is not (RenderView.Plan or RenderView.Iso or RenderView.Camera));

        string build = (Str(args, "build") ?? "auto").ToLowerInvariant();
        if (!BuildPolicies.Contains(build))
        {
            throw Usage($"Unknown build policy '{build}' (use {string.Join(", ", BuildPolicies)}).");
        }

        return new RenderSpec
        {
            OutPath = outPath,
            View = view,
            PlanY = y,
            Thickness = thickness,
            Below = below,
            CameraPosition = cam,
            CameraLook = look,
            FovDegrees = fov,
            HFovDegrees = hfov,
            EyeHeight = eyeHeight,
            Region = region,
            Room = room,
            Uid = uid,
            Mode = mode,
            Width = width,
            Height = height,
            SizeGiven = sizeGiven,
            Overlays = overlays,
            Icons = iconsOn,
            Build = build,
        };
    }

    /// <summary>The vertical field of view (degrees) that gives <paramref name="hfovDegrees"/> horizontally at <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static float VerticalFov(float hfovDegrees, int width, int height) =>
        2f * MathF.Atan(MathF.Tan(hfovDegrees * (MathF.PI / 360f)) * height / width) * (180f / MathF.PI);

    /// <summary>The horizontal field of view (degrees) of a vertical one at <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static float HorizontalFov(float vfovDegrees, int width, int height) =>
        2f * MathF.Atan(MathF.Tan(vfovDegrees * (MathF.PI / 360f)) * width / height) * (180f / MathF.PI);

    /// <summary>Parses a view name.</summary>
    public static RenderView ParseView(string name) => name.ToLowerInvariant() switch
    {
        "plan" => RenderView.Plan,
        "top" => RenderView.Top,
        "bottom" => RenderView.Bottom,
        "front" => RenderView.Front,
        "back" => RenderView.Back,
        "left" => RenderView.Left,
        "right" => RenderView.Right,
        "iso" => RenderView.Iso,
        "camera" => RenderView.Camera,
        _ => throw Usage($"Unknown view '{name}' (use plan, top, bottom, front, back, left, right, iso, or camera)."),
    };

    /// <summary>The names of an overlay list (lower case): a JSON array of names or a comma-separated string.</summary>
    private static List<string> OverlayTokens(JsonNode node) => (node switch
    {
        JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : throw Usage("overlays must be names.")),
        JsonValue v when v.TryGetValue(out string? s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => throw Usage("overlays must be a list of names."),
    }).Select(n => n.ToLowerInvariant()).ToList();

    /// <summary>
    /// Parses an overlay list: a JSON array of names, a comma-separated string, or "none". <c>icons</c> / <c>noicons</c>
    /// (the 3-D object icons of a non-plan view, <see cref="Icons"/>) are accepted and set no overlay layer.
    /// </summary>
    public static RenderOverlays ParseOverlays(JsonNode node)
    {
        IEnumerable<string> names = OverlayTokens(node);

        RenderOverlays result = RenderOverlays.None;
        bool legendNamed = false, noLegend = false;
        foreach (string raw in names)
        {
            string n = raw.ToLowerInvariant();
            legendNamed |= n == "legend";
            noLegend |= n == "nolegend";
            result |= n switch
            {
                "none" or "nolegend" or "icons" or "noicons" => RenderOverlays.None,
                "all" => RenderOverlays.PlanDefault | RenderOverlays.Holes,
                "grid" => RenderOverlays.Grid,
                "scale" => RenderOverlays.Scale,
                "objects" => RenderOverlays.Objects,
                "labels" or "uids" => RenderOverlays.Labels,
                "links" => RenderOverlays.Links,
                "holes" => RenderOverlays.Holes,
                "legend" => RenderOverlays.Legend,
                _ => throw Usage($"Unknown overlay '{raw}' (use {string.Join(", ", OverlayNames)}, nolegend, icons, noicons, or none)."),
            };
        }

        // The legend comes with any other overlay unless "nolegend" is given.
        if (noLegend)
        {
            result &= ~RenderOverlays.Legend;
        }
        else if (result != RenderOverlays.None && !legendNamed)
        {
            result |= RenderOverlays.Legend;
        }

        return result;
    }

    /// <summary>The overlay names set in <paramref name="o"/>, in drawing order.</summary>
    public static List<string> Names(RenderOverlays o)
    {
        var list = new List<string>();
        for (int i = 0; i < OverlayNames.Count; i++)
        {
            if (((int)o & (1 << i)) != 0)
            {
                list.Add(OverlayNames[i]);
            }
        }

        return list;
    }

    /// <summary>Parses "x,y,z" (the CLI form) into three floats.</summary>
    public static float[] ParseList(string text, string what, int count)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            throw Usage($"{what} needs {count} comma-separated numbers (got '{text}').");
        }

        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i]))
            {
                throw Usage($"{what}: '{parts[i]}' is not a number.");
            }
        }

        return values;
    }

    /// <summary>Reads any JSON number (parsed, or built in code from an int/long/float/double).</summary>
    internal static bool TryNumber(JsonValue v, out double d)
    {
        if (v.TryGetValue(out d))
        {
            return true;
        }

        if (v.TryGetValue(out int i))
        {
            d = i;
            return true;
        }

        if (v.TryGetValue(out long l))
        {
            d = l;
            return true;
        }

        if (v.TryGetValue(out float f))
        {
            d = f;
            return true;
        }

        return false;
    }

    private static AutomationException Usage(string message) => new(AutomationErrorCodes.Usage, message);

    private static string? Str(JsonObject a, string name)
    {
        if (a[name] is null)
        {
            return null;
        }

        return a[name] is JsonValue v && v.TryGetValue(out string? s) ? s : throw Usage($"Argument '{name}' must be a string.");
    }

    private static bool? Bool(JsonObject a, string name)
    {
        if (a[name] is null)
        {
            return null;
        }

        if (a[name] is JsonValue v)
        {
            if (v.TryGetValue(out bool b))
            {
                return b;
            }

            if (v.TryGetValue(out string? s) && s.ToLowerInvariant() is "true" or "false" or "on" or "off")
            {
                return s.ToLowerInvariant() is "true" or "on";
            }
        }

        throw Usage($"Argument '{name}' must be true or false.");
    }

    private static float? Num(JsonObject a, string name)
    {
        if (a[name] is null)
        {
            return null;
        }

        if (a[name] is JsonValue v && TryNumber(v, out double d) && double.IsFinite(d))
        {
            return (float)d;
        }

        throw Usage($"Argument '{name}' must be a number.");
    }

    private static int? Int(JsonObject a, string name)
    {
        if (a[name] is null)
        {
            return null;
        }

        if (a[name] is JsonValue v && TryNumber(v, out double d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue)
        {
            return (int)d;
        }

        throw Usage($"Argument '{name}' must be a whole number.");
    }

    private static Vector3? Vec(JsonObject a, string name)
    {
        if (a[name] is not JsonNode n)
        {
            return null;
        }

        float[] f = Floats(n, name, 3);
        return new Vector3(f[0], f[1], f[2]);
    }

    private static float[] Floats(JsonNode n, string name, int count)
    {
        if (n is JsonValue sv && sv.TryGetValue(out string? text))
        {
            return ParseList(text, name, count);
        }

        if (n is not JsonArray arr || arr.Count != count)
        {
            throw Usage($"Argument '{name}' must be {count} numbers.");
        }

        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (arr[i] is not JsonValue v || !TryNumber(v, out double d) || !double.IsFinite(d))
            {
                throw Usage($"Argument '{name}' must be {count} numbers.");
            }

            values[i] = (float)d;
        }

        return values;
    }
}
