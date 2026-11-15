using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Ged.Automation.Rendering;

namespace Ged.Automation.Reference;

/// <summary>Where the reference tools may read and write (set by the host, never by a request).</summary>
public sealed class ReferenceIo
{
    /// <summary>Resolves a path the tools will write, or throws <c>path_not_allowed</c>.</summary>
    public required Func<string, string> RequireWrite { get; init; }

    /// <summary>Resolves a path the tools will read (images, the reference index).</summary>
    public Func<string, string> RequireRead { get; init; } = PathAllowList.Normalize;

    /// <summary>The reference set used when a request names none (null = the request must name one).</summary>
    public string? DefaultRefs { get; init; }

    /// <summary>Supplies an output image path when a request names none (null = the request must name one).</summary>
    public Func<string, string>? DefaultOut { get; init; }
}

/// <summary>A prepared comparison: the matched render to make, then <see cref="ReferenceCommand.FinishCompare"/>.</summary>
public sealed class ComparePlan
{
    internal ComparePlan(ReferenceStore store, ReferenceRecord record, JsonObject renderArgs, string sheetPath, int panelWidth, int panelHeight, double hfov)
    {
        Store = store;
        Record = record;
        RenderArgs = renderArgs;
        SheetPath = sheetPath;
        PanelWidth = panelWidth;
        PanelHeight = panelHeight;
        Hfov = hfov;
    }

    /// <summary>The <c>render</c> request's arguments (camera view, matched size and field of view; <c>out</c> = the raw render).</summary>
    public JsonObject RenderArgs { get; }

    /// <summary>Where the comparison sheet is written.</summary>
    public string SheetPath { get; }

    internal ReferenceStore Store { get; }

    internal ReferenceRecord Record { get; }

    internal int PanelWidth { get; }

    internal int PanelHeight { get; }

    internal double Hfov { get; }
}

/// <summary>
/// The <c>reference</c> command (glacier-cli <c>ref</c>, the MCP <c>reference_*</c> tools): register reference pictures
/// cut from concept images (<c>add</c>, <c>list</c>, <c>view</c>), give each a matched camera (<c>camera</c>: position,
/// look, horizontal field of view, eye height, camera-frame nudges, and single-view estimates from marked horizon,
/// vanishing point and known-size cues), render the level from that camera and compare (<c>compare</c>: a sheet of
/// reference | render over blend | edge overlay, plus numeric cues and hints), and convert counted units to metres
/// (<c>scale</c>). The reference set is sidecar data (a folder with <c>references.json</c> and PNGs); the level file is
/// never changed. Everything except the render itself runs here, without a GPU.
/// </summary>
public static class ReferenceCommand
{
    /// <summary>The command name.</summary>
    public const string Name = "reference";

    /// <summary>The default comparison panel width (pixels).</summary>
    public const int DefaultPanel = 640;

    /// <summary>The panel width limits.</summary>
    public const int MinPanel = 160, MaxPanel = 1536;

    /// <summary>The default longest side of a <c>view</c> image.</summary>
    public const int DefaultViewSide = 1200;

    /// <summary>The image file types a reference can be taken from.</summary>
    public static IReadOnlyList<string> ImageExtensions => RasterImage.Extensions;

    /// <summary>The operations.</summary>
    public static readonly IReadOnlyList<string> Ops = new[] { "add", "list", "view", "camera", "compare", "scale" };

    /// <summary>Runs every operation except <c>compare</c> (which needs a render: <see cref="PlanCompare"/>). Never throws.</summary>
    public static AutomationResponse Execute(JsonObject args, ReferenceIo io)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        try
        {
            string op = Str(args, "op") ?? throw Usage($"reference needs an operation: {string.Join(", ", Ops)}.");
            JsonObject result = op switch
            {
                "add" => Add(args, io),
                "list" => List(args, io),
                "view" => View(args, io),
                "camera" => Camera(args, io),
                "scale" => Scale(args),
                "compare" => throw Usage("compare renders the level: run it through the compare path (PlanCompare / FinishCompare)."),
                _ => throw Usage($"Unknown reference operation '{op}' (use {string.Join(", ", Ops)})."),
            };
            return new AutomationResponse { Ok = true, Command = Name, Result = result };
        }
        catch (AutomationException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
    }

    // ---- add -------------------------------------------------------------------------------------------

    private static JsonObject Add(JsonObject args, ReferenceIo io)
    {
        ReferenceStore store = Store(args, io);
        string id = Str(args, "id") ?? throw Usage("add needs an id for the reference.");
        ReferenceStore.CheckId(id);
        string source = io.RequireRead(Str(args, "image") ?? throw Usage("add needs the image to take the reference from."));
        RasterImage image = RasterImage.Load(source);
        int[]? crop = Ints(args, "crop", 4);
        int[]? snapped = null;
        if (crop is not null)
        {
            CheckCrop(crop, image);
            if (SnapRadius(args) is int radius)
            {
                snapped = SnapCrop(image, crop, radius);
            }
        }
        else if (args.ContainsKey("snap"))
        {
            throw Usage("snap refines a crop: give crop as well.");
        }

        int[]? used = snapped ?? crop;
        RasterImage picture = used is null ? image : image.Crop(used[0], used[1], used[2], used[3]);
        if (picture.Width < 16 || picture.Height < 16)
        {
            throw Usage($"The reference is {picture.Width}x{picture.Height} pixels: at least 16x16 is needed.");
        }

        ReferenceRecord? existing = store.References.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && Bool(args, "replace") != true)
        {
            throw Usage($"A reference '{existing.Id}' already exists in {store.Folder}: pass replace to overwrite it.");
        }

        var notes = new List<string>();
        var record = new ReferenceRecord
        {
            Id = existing?.Id ?? id,
            Area = Str(args, "area") ?? existing?.Area,
            Notes = Str(args, "notes") ?? existing?.Notes,
            Source = source,
            Crop = used,
            Image = (existing?.Id ?? id) + ".png",
            Width = picture.Width,
            Height = picture.Height,
            Camera = existing?.Camera,
            Marks = existing?.Marks,
        };
        if (Num(args, "hfov") is double hfov)
        {
            CheckHfov(hfov);
            if (record.Camera is not null)
            {
                record.Camera.Hfov = hfov;
            }
            else
            {
                notes.Add("hfov is stored with the camera: set it again with camera once the camera is set.");
            }
        }

        if (existing is not null && (existing.Camera is not null || existing.Marks is not null))
        {
            notes.Add("The reference was replaced; its camera and marks were kept (marks are fractions of the picture: check them with view if the crop changed).");
        }

        string imagePath = io.RequireWrite(store.ImagePath(record));
        io.RequireWrite(store.IndexPath);
        picture.SavePng(imagePath);
        if (existing is not null)
        {
            store.References[store.References.IndexOf(existing)] = record;
        }
        else
        {
            store.References.Add(record);
        }

        store.Save();
        if (picture.Width < 320)
        {
            notes.Add($"The picture is small ({picture.Width}x{picture.Height}): comparisons enlarge it, so judge forms and proportions rather than fine detail.");
        }

        return new JsonObject
        {
            ["op"] = "add",
            ["refs"] = store.Folder,
            ["reference"] = RecordNode(store, record),
            ["snapped"] = snapped is null ? null : Arr(snapped),
            ["requestedCrop"] = snapped is null ? null : Arr(crop!),
            ["next"] = "Check the crop with view (id), then set the camera (camera), then compare.",
            ["notes"] = notes.Count > 0 ? StrArr(notes) : null,
        };
    }

    private static int? SnapRadius(JsonObject args)
    {
        if (args["snap"] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out bool b))
        {
            return b ? 8 : null;
        }

        if (RenderSpec.TryNumber(v, out double d) && d is >= 1 and <= 64)
        {
            return (int)d;
        }

        if (v.TryGetValue(out string? s))
        {
            if (s is "true" or "on")
            {
                return 8;
            }

            if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int r) && r is >= 1 and <= 64)
            {
                return r;
            }
        }

        throw Usage("snap is true or a search radius in pixels (1-64).");
    }

    private static void CheckCrop(int[] crop, RasterImage image)
    {
        if (crop[2] < 16 || crop[3] < 16 || crop[0] < 0 || crop[1] < 0 || crop[0] + crop[2] > image.Width || crop[1] + crop[3] > image.Height)
        {
            throw Usage($"crop [x, y, w, h] = [{string.Join(", ", crop)}] must lie inside the {image.Width}x{image.Height} image and be at least 16x16.");
        }
    }

    /// <summary>
    /// Moves each side of <paramref name="crop"/> (within <paramref name="radius"/> pixels) onto the edge of the picture
    /// inside a panel of a composite image: of the clear straight edges parallel to the side (at least half as strong
    /// as the strongest there), the innermost. A frame line has two edges (background | line | picture), so this lands
    /// on the line's picture side and drops the frame. A side with no clear edge stays where it was.
    /// </summary>
    internal static int[] SnapCrop(RasterImage image, int[] crop, int radius)
    {
        float[] l = image.Luma();
        int w = image.Width, h = image.Height;
        int x0 = crop[0], y0 = crop[1], x1 = crop[0] + crop[2], y1 = crop[1] + crop[3];

        // The mean |difference| across the boundary between line b-1 and b, over the middle 80 % of the side.
        double Score(bool horizontal, int b, int from, int to)
        {
            int a0 = from + ((to - from) / 10), a1 = to - ((to - from) / 10);
            double s = 0;
            for (int a = a0; a < a1; a++)
            {
                s += horizontal
                    ? Math.Abs(l[(b * w) + a] - l[((b - 1) * w) + a])
                    : Math.Abs(l[(a * w) + b] - l[(a * w) + b - 1]);
            }

            return a1 > a0 ? s / (a1 - a0) : 0;
        }

        int Snap(bool horizontal, int at, int inward, int from, int to, int limit)
        {
            int lo = Math.Max(1, at - radius), hi = Math.Min(limit - 1, at + radius);
            var scores = new List<(int B, double Score)>();
            for (int b = lo; b <= hi; b++)
            {
                scores.Add((b, Score(horizontal, b, from, to)));
            }

            double best = scores.Max(c => c.Score);
            if (best < 0.08)
            {
                return at; // no clear frame edge: keep the side where it was
            }

            // The boundary b separates lines b-1 and b: the inside starts at b (top/left) or ends before b (bottom/right).
            IEnumerable<int> clear = scores.Where(c => c.Score >= best * 0.5).Select(c => c.B);
            return inward > 0 ? clear.Max() : clear.Min();
        }

        int top = Snap(true, y0, +1, x0, x1, h);
        int bottom = Snap(true, y1, -1, x0, x1, h);
        int left = Snap(false, x0, +1, y0, y1, w);
        int right = Snap(false, x1, -1, y0, y1, w);
        if (right - left < 16 || bottom - top < 16)
        {
            return (int[])crop.Clone();
        }

        return new[] { left, top, right - left, bottom - top };
    }

    // ---- list ------------------------------------------------------------------------------------------

    private static JsonObject List(JsonObject args, ReferenceIo io)
    {
        ReferenceStore store = Store(args, io, forRead: true);
        return new JsonObject
        {
            ["op"] = "list",
            ["refs"] = store.Folder,
            ["count"] = store.References.Count,
            ["references"] = new JsonArray(store.References.Select(r => (JsonNode?)RecordNode(store, r)).ToArray()),
        };
    }

    // ---- view ------------------------------------------------------------------------------------------

    private static JsonObject View(JsonObject args, ReferenceIo io)
    {
        string? id = Str(args, "id");
        string? image = Str(args, "image");
        if ((id is null) == (image is null))
        {
            throw Usage("view takes either image (a source picture, with a pixel grid for picking a crop) or id (a stored reference, with its marks).");
        }

        string outPath = OutPath(args, io, "view");
        int size = Int(args, "size") ?? DefaultViewSide;
        if (size is < 200 or > 4096)
        {
            throw Usage("size (the longest side of the view) must be between 200 and 4096 pixels.");
        }

        RasterImage sheet;
        var result = new JsonObject { ["op"] = "view" };
        if (image is not null)
        {
            string source = io.RequireRead(image);
            RasterImage picture = RasterImage.Load(source);
            int[]? crop = Ints(args, "crop", 4);
            int[]? snapped = null;
            if (crop is not null)
            {
                CheckCrop(crop, picture);
                if (SnapRadius(args) is int radius)
                {
                    snapped = SnapCrop(picture, crop, radius);
                }
            }

            string title = crop is null
                ? $"{Path.GetFileName(source)} {picture.Width}x{picture.Height}: grid in source pixels"
                : $"crop {string.Join(",", crop)}{(snapped is not null ? $" -> snapped {string.Join(",", snapped)} (green)" : string.Empty)}";
            sheet = ReferenceSheets.SourceGrid(picture, size, crop, snapped, title);
            result["kind"] = "source";
            result["image"] = source;
            result["imageSize"] = Arr(new[] { picture.Width, picture.Height });
            result["crop"] = crop is null ? null : Arr(crop);
            result["snapped"] = snapped is null ? null : Arr(snapped);
            result["read"] = "Grid labels are the source image's pixels: read a panel's left, top, right and bottom edges and pass crop = x, y, width, height to add (snap = true refines the edges onto the panel's frame).";
        }
        else
        {
            ReferenceStore store = Store(args, io, forRead: true);
            ReferenceRecord r = store.Get(id!);
            RasterImage picture = RasterImage.Load(io.RequireRead(store.ImagePath(r)));
            int width = Math.Min(size, r.Width >= r.Height ? size : (int)(size * r.Aspect));
            sheet = ReferenceSheets.MarkedReference(picture, Math.Max(200, width), r.Marks, $"{r.Id}{(r.Area is null ? string.Empty : ": " + r.Area)} ({r.Width}x{r.Height}); grid in fractions");
            result["kind"] = "reference";
            result["reference"] = RecordNode(store, r);
            result["read"] = "Grid labels are fractions of the picture (0 = left/top, 1 = right/bottom): give marks (horizon, vp, object, measure, ignore) to camera in these units.";
        }

        sheet.SavePng(io.RequireWrite(outPath));
        result["path"] = outPath;
        result["width"] = sheet.Width;
        result["height"] = sheet.Height;
        return result;
    }

    // ---- camera ----------------------------------------------------------------------------------------

    private static JsonObject Camera(JsonObject args, ReferenceIo io)
    {
        ReferenceStore store = Store(args, io);
        ReferenceRecord r = store.Get(Str(args, "id") ?? throw Usage("camera needs the reference id."));
        var notes = new List<string>();
        ReferenceCameraRecord? cam = ApplyCameraArgs(r.Camera, args, notes);

        // Marks.
        if (Bool(args, "clearMarks") == true)
        {
            r.Marks = null;
        }

        var marks = r.Marks ?? new ReferenceMarks();
        bool marked = false;
        void Mark(string key, Action<double[]> set, params int[] lengths)
        {
            if (Floats(args, key) is { } v)
            {
                if (lengths.Length > 0 && !lengths.Contains(v.Length) && !(lengths[0] < 0 && v.Length % -lengths[0] == 0 && v.Length > 0))
                {
                    throw Usage($"{key} has {v.Length} numbers; expected {string.Join(" or ", lengths.Select(n => n < 0 ? $"a multiple of {-n}" : n.ToString(CultureInfo.InvariantCulture)))}.");
                }

                if (key != "vpDir" && key != "object" && v.Any(f => f is < -0.5 or > 1.5))
                {
                    throw Usage($"{key} values are fractions of the picture (0..1 from the top-left).");
                }

                set(v);
                marked = true;
            }
        }

        Mark("horizon", v => marks.Horizon = v, 1, 4);
        Mark("vp", v => marks.Vp = v, 2);
        Mark("vpDir", v => marks.VpDir = v, 2);
        Mark("object", v => marks.Object = v, 4);
        Mark("measure", v => marks.Measure = v, -3);
        Mark("ignore", v => marks.Ignore = v, -4);
        if (marked || r.Marks is not null)
        {
            r.Marks = marks;
        }

        CameraEstimate? estimate = null;
        if (r.Marks is { } mk && (mk.Horizon is not null || mk.Vp is not null || mk.Object is not null || mk.Measure is not null))
        {
            double hfov = cam?.Hfov ?? ReferenceCamera.DefaultHfov;
            estimate = ReferenceCamera.Estimate(mk, r.Aspect, hfov, cam?.EyeHeight);
        }

        if (Bool(args, "applyEstimate") == true)
        {
            if (estimate is null)
            {
                throw Usage("applyEstimate needs marks to estimate from (horizon, vp, object).");
            }

            if (cam is null)
            {
                throw Usage("applyEstimate needs a camera position to apply the estimate to: give camera (x, y, z) too.");
            }

            Vector3 eye = V(cam.Position), dir = V(cam.Look) - eye;
            double heading = estimate.HeadingDeg ?? ReferenceCamera.Heading(dir);
            double pitch = estimate.PitchDeg ?? ReferenceCamera.Pitch(dir);
            cam.Look = D(ReferenceCamera.LookAlong(eye, heading, pitch));
            if (estimate.EyeHeight is double eh && eh > 0.05)
            {
                cam.EyeHeight = Math.Round(eh, 2);
            }

            notes.Add($"Applied the estimate: heading {heading:0.#}, pitch {pitch:0.#}{(estimate.EyeHeight is double e2 && e2 > 0.05 ? $", eye {e2:0.##} m above the floor" : string.Empty)}.");
        }

        r.Camera = cam;
        io.RequireWrite(store.IndexPath);
        store.Save();
        var result = new JsonObject
        {
            ["op"] = "camera",
            ["refs"] = store.Folder,
            ["id"] = r.Id,
            ["camera"] = cam is null ? null : CameraNode(cam, r.Aspect),
            ["marks"] = r.Marks is null ? null : AutomationJson.ToNode(r.Marks),
            ["estimate"] = estimate is null ? null : AutomationJson.ToNode(estimate),
            ["renderFlags"] = cam is null ? null : RenderFlags(cam, r),
            ["next"] = cam is null
                ? "Set the camera: camera = x, y, z and look = x, y, z (or heading / pitch), then compare."
                : "compare renders the level from this camera beside the reference.",
            ["notes"] = notes.Count > 0 ? StrArr(notes) : null,
        };
        return result;
    }

    /// <summary>Applies camera / look / heading / pitch / move / turn / eyeHeight / hfov from <paramref name="args"/> to a copy of <paramref name="start"/>.</summary>
    internal static ReferenceCameraRecord? ApplyCameraArgs(ReferenceCameraRecord? start, JsonObject args, List<string> notes)
    {
        ReferenceCameraRecord? cam = start is null ? null : new ReferenceCameraRecord
        {
            Position = (double[])start.Position.Clone(),
            Look = (double[])start.Look.Clone(),
            Hfov = start.Hfov,
            EyeHeight = start.EyeHeight,
        };
        double[]? pos = Floats(args, "camera", 3);
        double[]? look = Floats(args, "look", 3);
        double? heading = Num(args, "heading"), pitch = Num(args, "pitch");
        if (look is not null && (heading is not null || pitch is not null))
        {
            throw Usage("look and heading / pitch are alternatives: give one way to aim the camera.");
        }

        if (pos is not null || look is not null || heading is not null || pitch is not null)
        {
            if (cam is null && pos is null)
            {
                throw Usage("The reference has no camera yet: give its position (camera = x, y, z).");
            }

            cam ??= new ReferenceCameraRecord { Position = pos!, Look = new[] { pos![0], pos[1], pos[2] + 10 } };
            Vector3 oldEye = V(cam.Position), oldDir = V(cam.Look) - oldEye;
            if (pos is not null)
            {
                cam.Position = pos;
                if (look is null && heading is null && pitch is null && start is not null)
                {
                    cam.Look = D(V(pos) + oldDir); // moving the eye keeps the view direction
                }
            }

            if (look is not null)
            {
                cam.Look = look;
            }
            else if (heading is not null || pitch is not null)
            {
                Vector3 dir = start is null && pos is not null ? Vector3.UnitZ : oldDir;
                if (pitch is < -89 or > 89)
                {
                    throw Usage("pitch must be between -89 and 89 degrees.");
                }

                cam.Look = D(ReferenceCamera.LookAlong(V(cam.Position), heading ?? ReferenceCamera.Heading(dir), pitch ?? ReferenceCamera.Pitch(dir)));
            }

            if (Vector3.DistanceSquared(V(cam.Position), V(cam.Look)) < 1e-4f)
            {
                throw Usage("The camera position and look point must differ.");
            }
        }

        double[]? move = Floats(args, "move", 3);
        double[]? turn = Floats(args, "turn", 2);
        if (move is not null || turn is not null)
        {
            if (cam is null)
            {
                throw Usage("move / turn nudge an existing camera: set camera (and look) first.");
            }

            (Vector3 e, Vector3 l) = ReferenceCamera.Nudge(V(cam.Position), V(cam.Look),
                move is null ? Vector3.Zero : new Vector3((float)move[0], (float)move[1], (float)move[2]),
                turn is null ? Vector2.Zero : new Vector2((float)turn[0], (float)turn[1]));
            cam.Position = D(e);
            cam.Look = D(l);
        }

        if (Num(args, "hfov") is double hfov)
        {
            CheckHfov(hfov);
            if (cam is null)
            {
                throw Usage("hfov belongs to the camera: give the camera position as well.");
            }

            cam.Hfov = hfov;
        }

        if (EyeHeightArg(args) is double eye)
        {
            if (cam is null)
            {
                throw Usage("eyeHeight belongs to the camera: give the camera position as well.");
            }

            cam.EyeHeight = eye <= 0 ? null : eye;
            if (eye <= 0)
            {
                notes.Add("eyeHeight cleared: renders use the camera's own height.");
            }
        }

        return cam;
    }

    private static double? EyeHeightArg(JsonObject args)
    {
        if (args["eyeHeight"] is not JsonValue v)
        {
            return null;
        }

        if (RenderSpec.TryNumber(v, out double d))
        {
            return d <= 0 ? 0 : d is >= 0.05 and <= 200 ? d : throw Usage("eyeHeight must be 0.05-200 metres (0 clears it).");
        }

        if (v.TryGetValue(out string? s))
        {
            return s.Trim().ToLowerInvariant() is "none" or "off" or "0" ? 0 : ReferenceCamera.ParseHeight(s);
        }

        throw Usage("eyeHeight must be metres or a preset (eye, crouch, jump).");
    }

    private static void CheckHfov(double hfov)
    {
        if (hfov is < 10 or > 170)
        {
            throw Usage("hfov must be between 10 and 170 degrees.");
        }
    }

    private static JsonObject CameraNode(ReferenceCameraRecord cam, double aspect)
    {
        Vector3 dir = V(cam.Look) - V(cam.Position);
        return new JsonObject
        {
            ["position"] = Arr(cam.Position.Select(v => Math.Round(v, 3)).ToArray()),
            ["look"] = Arr(cam.Look.Select(v => Math.Round(v, 3)).ToArray()),
            ["heading"] = Math.Round(ReferenceCamera.Heading(dir), 2),
            ["pitch"] = Math.Round(ReferenceCamera.Pitch(dir), 2),
            ["hfov"] = cam.Hfov,
            ["vfov"] = Math.Round(ReferenceCamera.VerticalFov(cam.Hfov, aspect), 2),
            ["eyeHeight"] = cam.EyeHeight,
        };
    }

    private static string RenderFlags(ReferenceCameraRecord cam, ReferenceRecord r)
    {
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        int w = DefaultPanel, h = PanelHeight(w, r.Aspect);
        return $"--camera {string.Join(",", cam.Position.Select(N))} --look {string.Join(",", cam.Look.Select(N))} --hfov {N(cam.Hfov)}" +
               (cam.EyeHeight is double e ? $" --eye-height {N(e)}" : string.Empty) + $" --size {w}x{h}";
    }

    // ---- compare ---------------------------------------------------------------------------------------

    /// <summary>
    /// Prepares a comparison: the reference's camera with any overrides from <paramref name="args"/> (camera, look,
    /// heading, pitch, move, turn, hfov, eyeHeight; not saved), the panel size (<c>panel</c>, at the reference's
    /// aspect), and the render request to run. Throws <see cref="AutomationException"/> for a bad request.
    /// </summary>
    public static ComparePlan PlanCompare(JsonObject args, ReferenceIo io, int? maxSheetWidth = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        ReferenceStore store = Store(args, io, forRead: true);
        ReferenceRecord r = store.Get(Str(args, "id") ?? throw Usage("compare needs the reference id."));
        var notes = new List<string>();
        ReferenceCameraRecord cam = ApplyCameraArgs(r.Camera, args, notes)
            ?? throw Usage($"Reference '{r.Id}' has no camera: set one with camera (camera = x, y, z and look = x, y, z, or heading / pitch) first.");
        int panel = Int(args, "panel") ?? DefaultPanel;
        if (panel is < MinPanel or > MaxPanel)
        {
            throw Usage($"panel (the width of each picture in the sheet) must be {MinPanel}-{MaxPanel} pixels.");
        }

        if (maxSheetWidth is int cap && SheetWidth(panel) > cap)
        {
            panel = Math.Max(MinPanel, (cap - 24) / 2);
        }

        int ph = PanelHeight(panel, r.Aspect);
        string sheet = OutPath(args, io, "compare");
        io.RequireWrite(sheet);
        string renderPath = io.RequireWrite(Path.ChangeExtension(sheet, null) + ".render.png");
        string mode = (Str(args, "mode") ?? "lit").ToLowerInvariant();
        var renderArgs = new JsonObject
        {
            ["view"] = "camera",
            ["camera"] = Arr(cam.Position),
            ["look"] = Arr(cam.Look),
            ["hfov"] = cam.Hfov,
            ["width"] = (double)panel,
            ["height"] = (double)ph,
            ["overlays"] = "none",
            ["mode"] = mode,
            ["out"] = renderPath,
        };
        if (cam.EyeHeight is double eye)
        {
            renderArgs["eyeHeight"] = eye;
        }

        if (Str(args, "build") is { } build)
        {
            renderArgs["build"] = build;
        }

        return new ComparePlan(store, r, renderArgs, sheet, panel, ph, cam.Hfov);
    }

    /// <summary>The sheet's width for a panel width.</summary>
    public static int SheetWidth(int panel) => (2 * panel) + 24;

    /// <summary>The panel height of a reference at <paramref name="aspect"/>.</summary>
    public static int PanelHeight(int panelWidth, double aspect) => Math.Max(16, (int)Math.Round(panelWidth / aspect));

    /// <summary>Builds the comparison from the finished render (the render's failure passes through). Never throws.</summary>
    public static AutomationResponse FinishCompare(ComparePlan plan, AutomationResponse render)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(render);
        if (!render.Ok)
        {
            return new AutomationResponse { Ok = false, Command = Name, Result = render.Result, Error = render.Error };
        }

        try
        {
            ReferenceRecord r = plan.Record;
            JsonNode res = render.Result!;
            string renderPath = res["path"]?.GetValue<string>() ?? plan.RenderArgs["out"]!.GetValue<string>();
            RasterImage rendered = RasterImage.Load(renderPath);
            RasterImage refPicture = RasterImage.Load(plan.Store.ImagePath(r));
            int pw = rendered.Width, ph = rendered.Height;
            RasterImage refPanel = refPicture.Resize(pw, ph);

            int aw = ImageCues.AnalysisWidth(r.Width), ah = Math.Max(16, (int)Math.Round(aw / r.Aspect));
            RasterImage refA = refPicture.Resize(aw, ah), renA = rendered.Resize(aw, ah);
            bool[] refEdges = ImageCues.Edges(refA), renEdges = ImageCues.Edges(renA);
            bool[] ignore = ImageCues.IgnoreMask(aw, ah, r.Marks?.Ignore);

            double[] fwd = res["camera"]?["forward"]?.AsArray().Select(n => n!.GetValue<double>()).ToArray() ?? new double[] { 0, 0, 1 };
            double pitch = ReferenceCamera.Pitch(new Vector3((float)fwd[0], (float)fwd[1], (float)fwd[2]));
            double heading = ReferenceCamera.Heading(new Vector3((float)fwd[0], (float)fwd[1], (float)fwd[2]));
            double renderHorizon = ReferenceCamera.HorizonRow(pitch, plan.Hfov, (double)pw / ph);
            double? refHorizon = ReferenceSheets.HorizonOf(r.Marks);

            PictureCues refCues = ImageCues.Measure(refA, refEdges, ignore, refHorizon);
            PictureCues renCues = ImageCues.Measure(renA, renEdges, ignore, renderHorizon is > 0 and < 1 ? renderHorizon : null);
            var cues = new CompareCues
            {
                AnalysisSize = new[] { aw, ah },
                Reference = refCues,
                Render = renCues,
                EdgeOverlapReference = ImageCues.Overlap(refEdges, renEdges, ignore, aw, ah),
                EdgeOverlapRender = ImageCues.Overlap(renEdges, refEdges, ignore, aw, ah),
            };
            cues.Hints = ImageCues.Hints(refCues, renCues, plan.Hfov, r.Aspect, pitch);

            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            double[] pos = res["camera"]?["position"]?.AsArray().Select(n => n!.GetValue<double>()).ToArray() ?? plan.RenderArgs["camera"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
            double? floor = res["camera"]?["floor"]?.GetValue<double>();
            var header = new List<string>
            {
                $"{r.Id}{(r.Area is null ? string.Empty : ": " + r.Area)}  -  reference vs render ({res["modeUsed"]?.GetValue<string>() ?? "lit"})",
                $"camera {N(pos[0])},{N(pos[1])},{N(pos[2])}  heading {N(heading)}  pitch {N(pitch)}  hfov {N(plan.Hfov)}" +
                    (floor is double fl ? $"  eye {N(pos[1] - fl)} m over floor {N(fl)}" : string.Empty),
            };
            var footer = new List<string>
            {
                $"detail (edge density) ref {F(refCues.EdgeDensity)} render {F(renCues.EdgeDensity)}   plain top ref {F(refCues.PlainTop)} render {F(renCues.PlainTop)}",
                $"brightness ref {F(refCues.Brightness)} render {F(renCues.Brightness)}   warmth ref {F(refCues.Warmth)} render {F(renCues.Warmth)}   horizon ref {(refHorizon is double rh ? F(rh) : "-")} render {F(renderHorizon)}",
                "orange = reference, cyan = render, white = both; dashed: horizons (yellow marked, cyan render)",
            };
            RasterImage sheet = ReferenceSheets.CompareSheet(refPanel, rendered, (refEdges, ImageCues.FirstEdgeRows(refEdges, ignore, aw, ah)),
                (renEdges, ImageCues.FirstEdgeRows(renEdges, ignore, aw, ah)), aw, ah, r.Marks, renderHorizon, header, footer);
            sheet.SavePng(plan.SheetPath);

            var notes = new List<string>();
            if (res["notes"] is JsonArray rn)
            {
                notes.AddRange(rn.Select(n => n!.GetValue<string>()));
            }

            notes.Add("The cues and hints are measured the same way on both pictures but are hints, not a score: concept art and a game render differ in style. Judge massing, openings, heights, structure, detail and materials by eye on the sheet, write the differences down, fix the biggest, and compare again.");
            if (r.Marks?.Ignore is null)
            {
                notes.Add("Mark a first-person weapon or caption in the reference with camera ignore = x, y, w, h (fractions) so the cues leave it out.");
            }

            var result = new JsonObject
            {
                ["op"] = "compare",
                ["id"] = r.Id,
                ["area"] = r.Area,
                ["path"] = plan.SheetPath,
                ["width"] = sheet.Width,
                ["height"] = sheet.Height,
                ["render"] = renderPath,
                ["reference"] = plan.Store.ImagePath(r),
                ["panel"] = Arr(new[] { pw, ph }),
                ["camera"] = new JsonObject
                {
                    ["position"] = res["camera"]?["position"]?.DeepClone(),
                    ["look"] = res["camera"]?["look"]?.DeepClone(),
                    ["heading"] = Math.Round(heading, 2),
                    ["pitch"] = Math.Round(pitch, 2),
                    ["hfov"] = plan.Hfov,
                    ["vfov"] = res["camera"]?["fovDegrees"]?.DeepClone(),
                    ["floor"] = floor,
                },
                ["cues"] = AutomationJson.ToNode(cues),
                ["geometry"] = res["geometry"]?.DeepClone(),
                ["sky"] = res["sky"]?.DeepClone(),
                ["notes"] = StrArr(notes),
            };
            return new AutomationResponse { Ok = true, Command = Name, Result = result };
        }
        catch (AutomationException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
    }

    // ---- scale -----------------------------------------------------------------------------------------

    private static JsonObject Scale(JsonObject args)
    {
        var result = new JsonObject
        {
            ["op"] = "scale",
            ["cues"] = AutomationJson.ToNode(ReferenceScale.Cues),
        };
        double? count = Num(args, "count");
        string? unit = Str(args, "unit");
        if (count is not null || unit is not null)
        {
            ScaleCue cue = ReferenceScale.Find(unit ?? throw Usage("count needs a unit (e.g. door_height)."));
            double n = count ?? 1;
            result["conversion"] = new JsonObject
            {
                ["count"] = n,
                ["unit"] = cue.Unit,
                ["metres"] = Math.Round(n * cue.Metres, 2),
                ["range"] = Arr(new[] { Math.Round(n * cue.Range[0], 2), Math.Round(n * cue.Range[1], 2) }),
                ["what"] = cue.What,
            };
        }

        result["method"] = "Count a known thing in the picture (doors, a player, stair steps), multiply by its size here, and cross-check with a second cue. " +
            "For a matched camera, camera with object = x, baseY, topY, metres (a known-height thing standing on the floor) estimates the eye height, and measure = x, baseY, topY gives other heights in metres.";
        return result;
    }

    // ---- shared ----------------------------------------------------------------------------------------

    private static ReferenceStore Store(JsonObject args, ReferenceIo io, bool forRead = false)
    {
        string folder = Str(args, "refs") ?? io.DefaultRefs
            ?? throw Usage("Name the reference set's folder (refs).");
        string full = forRead ? io.RequireRead(folder) : PathAllowList.Normalize(folder);
        if (!forRead)
        {
            io.RequireWrite(Path.Combine(full, ReferenceStore.IndexFile));
        }

        return ReferenceStore.Load(full);
    }

    private static string OutPath(JsonObject args, ReferenceIo io, string kind)
    {
        string? o = Str(args, "out");
        if (o is null)
        {
            return io.DefaultOut?.Invoke(kind) ?? throw Usage($"{kind} needs an output image path (out, a .png file).");
        }

        if (!o.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw Usage($"The output must be a .png file: {o}");
        }

        return PathAllowList.Normalize(o);
    }

    private static JsonObject RecordNode(ReferenceStore store, ReferenceRecord r)
    {
        var node = (JsonObject)AutomationJson.ToNode(r)!;
        node["image"] = store.ImagePath(r);
        node["aspect"] = Math.Round(r.Aspect, 4);
        if (r.Camera is not null)
        {
            node["camera"] = CameraNode(r.Camera, r.Aspect);
        }

        return node;
    }

    private static Vector3 V(double[] a) => new((float)a[0], (float)a[1], (float)a[2]);

    private static double[] D(Vector3 v) => new[] { Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3) };

    private static JsonArray Arr(IEnumerable<double> v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    private static JsonArray Arr(IEnumerable<int> v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    private static JsonArray StrArr(IEnumerable<string> v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    private static AutomationException Usage(string message) => new(AutomationErrorCodes.Usage, message);

    private static AutomationResponse Fail(string code, string message) =>
        new() { Ok = false, Command = Name, Error = new AutomationError(code, message) };

    private static string? Str(JsonObject a, string name) =>
        a[name] is null ? null
        : a[name] is JsonValue v && v.TryGetValue(out string? s) ? s
        : throw Usage($"Argument '{name}' must be a string.");

    private static bool? Bool(JsonObject a, string name)
    {
        if (a[name] is not JsonValue v)
        {
            return a[name] is null ? null : throw Usage($"Argument '{name}' must be true or false.");
        }

        if (v.TryGetValue(out bool b))
        {
            return b;
        }

        return v.TryGetValue(out string? s) && s.ToLowerInvariant() is "true" or "false" or "on" or "off"
            ? s.ToLowerInvariant() is "true" or "on"
            : throw Usage($"Argument '{name}' must be true or false.");
    }

    private static double? Num(JsonObject a, string name)
    {
        if (a[name] is null)
        {
            return null;
        }

        if (a[name] is JsonValue v)
        {
            if (RenderSpec.TryNumber(v, out double d) && double.IsFinite(d))
            {
                return d;
            }

            if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && double.IsFinite(p))
            {
                return p;
            }
        }

        throw Usage($"Argument '{name}' must be a number.");
    }

    private static int? Int(JsonObject a, string name) =>
        Num(a, name) is double d ? d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? (int)d : throw Usage($"Argument '{name}' must be a whole number.") : null;

    /// <summary>A number list: a JSON array or a comma-separated string (the CLI form); <paramref name="count"/> 0 = any length.</summary>
    private static double[]? Floats(JsonObject a, string name, int count = 0)
    {
        if (a[name] is not JsonNode n)
        {
            return null;
        }

        double[] values;
        if (n is JsonValue sv && sv.TryGetValue(out string? text))
        {
            string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                {
                    throw Usage($"{name}: '{parts[i]}' is not a number.");
                }
            }
        }
        else if (n is JsonValue one && RenderSpec.TryNumber(one, out double single))
        {
            values = new[] { single };
        }
        else if (n is JsonArray arr)
        {
            values = new double[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonValue v || !RenderSpec.TryNumber(v, out values[i]) || !double.IsFinite(values[i]))
                {
                    throw Usage($"Argument '{name}' must be numbers.");
                }
            }
        }
        else
        {
            throw Usage($"Argument '{name}' must be numbers.");
        }

        if (count > 0 && values.Length != count)
        {
            throw Usage($"{name} needs {count} numbers (got {values.Length}).");
        }

        return values;
    }

    private static int[]? Ints(JsonObject a, string name, int count) =>
        Floats(a, name, count) is { } f
            ? f.All(v => v == Math.Floor(v)) ? f.Select(v => (int)v).ToArray() : throw Usage($"{name} needs whole numbers (pixels).")
            : null;
}
