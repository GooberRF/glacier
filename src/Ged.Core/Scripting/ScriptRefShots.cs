using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Editing.Organic;
using Ged.Core.Spatial;

namespace Ged.Core.Scripting;

/// <summary>
/// The eye-level screenshot half of <c>ref</c>: locate a screenshot's camera in the current build (shot_locate),
/// measure heights along a pixel's ray (shot_measure), and render the build from a pose beside the shot
/// (shot_compare) so heights and shapes can be iterated until the render looks like the shot.
/// </summary>
public sealed partial class ScriptRef
{
    [ScriptDoc("Finds the camera of an eye-level screenshot in the current build (edge match, coarse to fine): {best = {pose, score}, poses = {...}, seconds}; out writes shot | render | edges.",
        Example = "local l = ref.shot_locate{shot = \"s1.png\", fov = 90, out = \"loc1.png\"} print(l.best.x, l.best.z, l.best.yaw, l.best.score)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefShotLocate))]
    public Dictionary<string, object?> ShotLocate(ScriptArgs args)
    {
        const string Api = "ref.shot_locate";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefShotLocate);
        RefImage shot = ShotImage(Api, args);
        double fov = Fov(Api, args);
        double eye = args.Number(Api, "eye", 1.7);
        if (eye is < 0.2 or > 20)
        {
            throw new ScriptApiException($"{Api}: eye must be 0.2 to 20 m above the floor (got {ScriptArgs.Format(eye)}).");
        }

        (double, double, double, double)? box = null;
        double step = 1.5;
        double? yawHint = null, floorY = null;
        double yawRange = 180;
        if (args.Table(Api, "search") is { } s)
        {
            s.RejectUnknown(Api + ".search", "box", "step", "yaw", "floor");
            if (s.Table(Api, "box") is { } b)
            {
                double[] v = Nums(Api, "search.box", b, 4);
                box = (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
            }

            step = s.Number(Api, "step", step);
            if (step is < 0.25 or > 16)
            {
                throw new ScriptApiException($"{Api}: search.step must be 0.25 to 16 m (got {ScriptArgs.Format(step)}).");
            }

            if (s.Table(Api, "yaw") is { } y)
            {
                double[] v = Nums(Api, "search.yaw", y, 2);
                (yawHint, yawRange) = (v[0], Math.Clamp(Math.Abs(v[1]), 1, 180));
            }

            floorY = s.NumberOrNull(Api, "floor");
        }

        double pitch = Math.Clamp(args.Number(Api, "pitch", 10), 0, 30);
        int keep = Math.Clamp(args.Int(Api, "keep", 3), 1, 6);
        GeometryFreshness fresh = FreshGeometry(Api, args);
        ShotScene scene = Scene(Api);
        List<ShotScene.Candidate> best;
        ShotScene.SearchStats stats;
        using (_ctx.EnterHostOperation())
        {
            (best, stats) = scene.Locate(shot, new ShotScene.SearchOptions(box, step, eye, fov, pitch, keep, yawHint, yawRange, floorY), _ctx.Cancellation);
        }

        if (best.Count == 0)
        {
            throw new ScriptApiException($"{Api}: no walkable floor position to search{(box is null ? string.Empty : " in search.box")}.", "Build the level (ops.build()) or widen search.box / remove search.floor.");
        }

        var poses = best.Select(c => PoseTable(c.Pose, c.Score, c.Floor)).ToArray();
        var t = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["best"] = poses[0],
            ["poses"] = poses,
            ["positions"] = stats.Positions,
            ["refined"] = stats.Refined,
            ["seconds"] = Math.Round(stats.Seconds, 2),
            ["geometry"] = fresh.State,
            ["read"] = "score is the edge correlation (-1..1): above ~0.35 is a usable match; compare the top poses' scores and check out's render panel by eye.",
        };
        if (stats.Note.Length > 0)
        {
            t["note"] = stats.Note;
        }

        if (Out(Api, args, required: false) is { } o)
        {
            ShotPanels.Result p = Panels(Api, shot, best[0].Pose, scene, args.Int(Api, "panel", 300), edges: args.Bool(Api, "edges", true), args.String(Api, "mode", "lit")!);
            Write(Api, o, p.Png);
            t["path"] = o;
            t["render"] = p.Render;
        }

        return t;
    }

    [ScriptDoc("From a pose and a shot pixel: the ray, what it hits in the build {hit, distance, height (above the camera's floor), surface}, and at_distance = the pixel's height at a given distance.",
        Example = "local m = ref.shot_measure{pose = l.best, shot = \"s1.png\", px = {412, 160}, distance = 8} print(m.height, m.at_distance)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefShotMeasure))]
    public Dictionary<string, object?> ShotMeasure(ScriptArgs args)
    {
        const string Api = "ref.shot_measure";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefShotMeasure);
        int w, h;
        if (args.Table(Api, "size") is { } sz)
        {
            double[] v = Nums(Api, "size", sz, 2);
            (w, h) = ((int)v[0], (int)v[1]);
            if (w < 8 || h < 8)
            {
                throw new ScriptApiException($"{Api}: size must be at least 8 x 8 pixels.");
            }
        }
        else if (args.Has("shot"))
        {
            RefImage img = ShotImage(Api, args);
            (w, h) = (img.Width, img.Height);
        }
        else
        {
            throw new ScriptApiException($"{Api}: give shot = \"<png>\" or size = {{w, h}} (the screenshot's pixel size).");
        }

        double[] px = Nums(Api, "px", args.Table(Api, "px") ?? throw new ScriptApiException($"{Api}: px = {{x, y}} (a shot pixel) is required."), 2);
        ShotScene? scene = SpatialLevel.StoredGeometry(_ctx.Document.Rfl) is { } geo ? ShotScene.Of(geo, _ctx.Cancellation) : null;
        ShotPose pose = Pose(Api, args, scene, args.Number(Api, "eye", 1.7));
        var o = new Vec3d(pose.X, pose.Y, pose.Z);
        Vec3d d = pose.Ray(px[0], px[1], w, h);
        double horiz = Math.Sqrt((d.X * d.X) + (d.Z * d.Z));
        double? camFloor = scene?.FloorBelow(pose.X, pose.Y, pose.Z);
        var t = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dir"] = new[] { R3(d.X), R3(d.Y), R3(d.Z) },
            ["elevation"] = Math.Round(Math.Atan2(d.Y, horiz) * 180 / Math.PI, 2),
            ["heading"] = Math.Round(((Math.Atan2(d.X, d.Z) * 180 / Math.PI) + 360) % 360, 2),
            ["camera_floor"] = camFloor is double cf ? Math.Round(cf, 3) : null,
        };
        if (args.NumberOrNull(Api, "distance") is double dist)
        {
            if (dist <= 0 || horiz < 1e-6)
            {
                throw new ScriptApiException($"{Api}: distance must be positive and the pixel must not look straight up or down.");
            }

            double y = pose.Y + (dist * d.Y / horiz);
            t["at_distance"] = Math.Round(y - (camFloor ?? (pose.Y - 1.7)), 3);
            t["at_distance_y"] = Math.Round(y, 3);
        }

        if (scene?.Cast(o, d) is { } hit)
        {
            Vec3d p = o.Add(d.Scale(hit.T));
            t["hit"] = new[] { R3(p.X), R3(p.Y), R3(p.Z) };
            t["distance"] = Math.Round(hit.T, 3);
            t["horizontal"] = Math.Round(hit.T * horiz, 3);
            t["height"] = Math.Round(p.Y - (camFloor ?? (pose.Y - 1.7)), 3);
            t["normal"] = new[] { R3(hit.Normal.X), R3(hit.Normal.Y), R3(hit.Normal.Z) };
            t["surface"] = hit.Normal.Y > 0.7 ? "floor" : hit.Normal.Y < -0.7 ? "ceiling" : "wall";
        }
        else
        {
            t["note"] = scene is null ? "The level has no compiled geometry: only the ray and at_distance are given (ops.build() adds the hit)." : "The ray hits nothing in the build (open sky or a gap).";
        }

        return t;
    }

    [ScriptDoc("Renders the build from a pose (lit, textured, the shot's aspect) beside the shot with an edge score: {score, offset_px, render, path}; edges = true adds an edge-overlay panel.",
        Example = "local c = ref.shot_compare{shot = \"s1.png\", pose = {x = 4, z = -10, yaw = 90, pitch = 0, fov = 90}, out = \"cmp_s1.png\"} print(c.score)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefShotCompare))]
    public Dictionary<string, object?> ShotCompare(ScriptArgs args)
    {
        const string Api = "ref.shot_compare";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefShotCompare);
        RefImage shot = ShotImage(Api, args);
        GeometryFreshness fresh = FreshGeometry(Api, args);
        ShotScene scene = Scene(Api);
        ShotPose pose = Pose(Api, args, scene, args.Number(Api, "eye", 1.7));
        string outPath = Out(Api, args, required: true)!;
        ShotPanels.Result p;
        using (_ctx.EnterHostOperation())
        {
            p = Panels(Api, shot, pose, scene, args.Int(Api, "panel", 360), args.Bool(Api, "edges", false), args.String(Api, "mode", "lit")!);
        }

        Write(Api, outPath, p.Png);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["score"] = Math.Round(p.Score, 4),
            ["offset_px"] = new[] { p.OffsetX, p.OffsetY },
            ["pose"] = PoseTable(pose, p.Score, scene.FloorBelow(pose.X, pose.Y, pose.Z) ?? pose.Y - 1.7),
            ["render"] = p.Render,
            ["path"] = outPath,
            ["geometry"] = fresh.State,
            ["read"] = "offset_px: the build's edges sit this many shot pixels right/down of the shot's (positive y = the build reads lower: raise the geometry or lower the eye). Score above ~0.35 = shapes line up.",
        };
    }

    // ---- shot plumbing ----------------------------------------------------------------------------------------

    private RefImage ShotImage(string api, ScriptArgs args) =>
        Load(api, args.String(api, "shot") ?? throw new ScriptApiException($"{api}: shot = \"<screenshot png>\" is required."));

    private static double Fov(string api, ScriptArgs args)
    {
        double fov = args.Number(api, "fov", 90);
        return fov is < 20 or > 150 ? throw new ScriptApiException($"{api}: fov (horizontal, degrees) must be 20 to 150 (got {ScriptArgs.Format(fov)}).") : fov;
    }

    private ShotScene Scene(string api)
    {
        Model.Geometry geo = SpatialLevel.StoredGeometry(_ctx.Document.Rfl)
            ?? throw new ScriptApiException($"{api}: the level has no compiled geometry.", "Run ops.build() first, or pass build = \"sealed\".");
        return ShotScene.Of(geo, _ctx.Cancellation);
    }

    // pose = {x, y?, z, yaw, pitch?, fov?}: y is the eye's world height; without it the eye stands eye m over the floor.
    private static ShotPose Pose(string api, ScriptArgs args, ShotScene? scene, double eye)
    {
        ScriptArgs p = args.Table(api, "pose") ?? throw new ScriptApiException($"{api}: pose = {{x, y?, z, yaw, pitch?, fov?}} is required (a ref.shot_locate pose works as is).");
        p.RejectUnknown(api + ".pose", "x", "y", "z", "yaw", "pitch", "fov", "score", "floor");
        double x = p.NumberOrNull(api, "x") ?? throw new ScriptApiException($"{api}: pose.x is required.");
        double z = p.NumberOrNull(api, "z") ?? throw new ScriptApiException($"{api}: pose.z is required.");
        double yaw = p.NumberOrNull(api, "yaw") ?? throw new ScriptApiException($"{api}: pose.yaw (heading in degrees: 0 = +Z, 90 = +X) is required.");
        double pitch = p.Number(api, "pitch", 0);
        double fov = args.Has("fov") ? Fov(api, args) : p.Number(api, "fov", 90);
        if (fov is < 20 or > 150)
        {
            throw new ScriptApiException($"{api}: pose.fov must be 20 to 150 degrees.");
        }

        double y;
        if (p.NumberOrNull(api, "y") is double yy)
        {
            y = yy;
        }
        else
        {
            double? floor = scene?.FloorBelow(x, 1000, z);
            for (double probe = 200; floor is null && probe > -200 && scene is not null; probe -= 25)
            {
                floor = scene.FloorBelow(x, probe, z);
            }

            y = (floor ?? 0) + eye;
        }

        return new ShotPose(x, y, z, yaw, Math.Clamp(pitch, -89, 89), fov);
    }

    private static Dictionary<string, object?> PoseTable(ShotPose p, double score, double floor) => new(StringComparer.Ordinal)
    {
        ["x"] = Math.Round(p.X, 3),
        ["y"] = Math.Round(p.Y, 3),
        ["z"] = Math.Round(p.Z, 3),
        ["yaw"] = Math.Round(p.Yaw, 2),
        ["pitch"] = Math.Round(p.Pitch, 2),
        ["fov"] = Math.Round(p.Hfov, 2),
        ["floor"] = Math.Round(floor, 3),
        ["score"] = Math.Round(score, 4),
    };

    private static double R3(double v) => Math.Round(v, 3);

    private ShotPanels.Result Panels(string api, RefImage shot, ShotPose pose, ShotScene scene, int panelH, bool edges, string mode)
    {
        if (panelH is < 64 or > 1080)
        {
            throw new ScriptApiException($"{api}: panel (the panel height in pixels) must be 64 to 1080.");
        }

        if (mode is not ("lit" or "textured" or "shaded"))
        {
            throw new ScriptApiException($"{api}: mode must be \"lit\", \"textured\" or \"shaded\" (got \"{mode}\").");
        }

        int panelW = Math.Max(16, (int)Math.Round(panelH * (double)shot.Width / shot.Height));
        byte[]? render = null;
        string how = "shaded";
        if (mode != "shaded" && _ctx.Services.ShotRender is { } renderer)
        {
            try
            {
                render = renderer(new ShotRenderRequest(pose.X, pose.Y, pose.Z, pose.Yaw, pose.Pitch, pose.Hfov, panelW, panelH, mode));
                how = render is null ? "shaded" : mode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or NotSupportedException)
            {
                render = null;
                how = "shaded (the renderer failed: " + ex.Message + ")";
            }
        }
        else if (mode != "shaded")
        {
            how = "shaded (no game renderer in this host)";
        }

        return ShotPanels.Compose(shot, pose, scene, panelW, panelH, render, how, edges);
    }
}

/// <summary>The side-by-side picture of a shot and the build from a pose: shot | render | (edges).</summary>
internal static class ShotPanels
{
    public sealed record Result(byte[] Png, double Score, int OffsetX, int OffsetY, string Render);

    public static Result Compose(RefImage shot, ShotPose pose, ShotScene scene, int pw, int ph, byte[]? render, string how, bool edges)
    {
        // the score on a 160-wide edge map, and the shift of the build's edges that best fits the shot's
        ShotScene.ShotEdges se = ShotScene.ShotEdges.Of(shot, 160);
        double score = scene.Score(pose, se, out float[] re, out _);
        float[] sb = ShotScene.Blur(se.M, se.W, se.H, false), rb = ShotScene.Blur(re, se.W, se.H, false);
        (int bx, int by, double bs) = (0, 0, score);
        for (int dy = -8; dy <= 8; dy++)
        {
            for (int dx = -8; dx <= 8; dx++)
            {
                var shifted = new float[rb.Length];
                for (int y = 0; y < se.H; y++)
                {
                    for (int x = 0; x < se.W; x++)
                    {
                        int sx = x - dx, sy = y - dy;
                        shifted[(y * se.W) + x] = sx < 0 || sy < 0 || sx >= se.W || sy >= se.H ? float.NaN : rb[(sy * se.W) + sx];
                    }
                }

                double s = ShotScene.Ncc(shifted, sb);
                if (s > bs + 1e-4)
                {
                    (bx, by, bs) = (dx, dy, s);
                }
            }
        }

        double toShot = (double)shot.Width / se.W;
        int offX = (int)Math.Round(bx * toShot), offY = (int)Math.Round(by * toShot);

        render ??= ShotScene.Shade(scene.Perspective(pose, pw, ph), null);
        int panels = edges ? 3 : 2;
        const int Gap = 6, Head = 22;
        var c = new RefCanvas((panels * pw) + ((panels + 1) * Gap), ph + Head + Gap);
        Blit(c, Scale(shot.Rgba, shot.Width, shot.Height, pw, ph), pw, ph, Gap, Head);
        Blit(c, render, pw, ph, (2 * Gap) + pw, Head);
        c.Text(Gap, 5, "SHOT", 1, 230, 230, 230);
        c.Text((2 * Gap) + pw, 5, $"BUILD ({how.ToUpperInvariant()})  {RefCanvas.N(pose.X)},{RefCanvas.N(pose.Y)},{RefCanvas.N(pose.Z)} YAW {RefCanvas.N(pose.Yaw)} PITCH {RefCanvas.N(pose.Pitch)} FOV {RefCanvas.N(pose.Hfov)}", 1, 230, 230, 230);
        if (edges)
        {
            int x0 = (3 * Gap) + (2 * pw);
            byte[] grey = Scale(shot.Rgba, shot.Width, shot.Height, pw, ph);
            for (int i = 0; i < pw * ph; i++)
            {
                byte l = (byte)(((grey[i * 4] * 0.3) + (grey[(i * 4) + 1] * 0.59) + (grey[(i * 4) + 2] * 0.11)) * 0.35);
                grey[i * 4] = grey[(i * 4) + 1] = grey[(i * 4) + 2] = l;
            }

            Blit(c, grey, pw, ph, x0, Head);
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int ex = Math.Min(se.W - 1, x * se.W / pw), ey = Math.Min(se.H - 1, y * se.H / ph);
                    bool sEdge = se.M[(ey * se.W) + ex] > 0.45f, rEdge = re[(ey * se.W) + ex] > 0;
                    if (sEdge && rEdge)
                    {
                        c.Set(x0 + x, Head + y, 250, 230, 60);
                    }
                    else if (rEdge)
                    {
                        c.Set(x0 + x, Head + y, 60, 220, 240);
                    }
                    else if (sEdge)
                    {
                        c.Set(x0 + x, Head + y, 220, 60, 50);
                    }
                }
            }

            c.Text(x0, 5, $"EDGES: SHOT RED  BUILD CYAN  BOTH YELLOW  SCORE {score:0.000}", 1, 230, 230, 230);
        }

        return new Result(c.Png(), score, offX, offY, how);
    }

    private static void Blit(RefCanvas c, byte[] rgba, int w, int h, int x0, int y0)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                c.Set(x0 + x, y0 + y, rgba[o], rgba[o + 1], rgba[o + 2]);
            }
        }
    }

    // bilinear resample of an RGBA image
    internal static byte[] Scale(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh)
        {
            return (byte[])src.Clone();
        }

        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            double fy = Math.Clamp((((y + 0.5) * sh) / dh) - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1);
            double ty = fy - y0;
            for (int x = 0; x < dw; x++)
            {
                double fx = Math.Clamp((((x + 0.5) * sw) / dw) - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1);
                double tx = fx - x0;
                for (int k = 0; k < 4; k++)
                {
                    double a = src[(((y0 * sw) + x0) * 4) + k], b = src[(((y0 * sw) + x1) * 4) + k];
                    double cc = src[(((y1 * sw) + x0) * 4) + k], d = src[(((y1 * sw) + x1) * 4) + k];
                    dst[(((y * dw) + x) * 4) + k] = (byte)Math.Round(((a * (1 - tx)) + (b * tx)) * (1 - ty) + (((cc * (1 - tx)) + (d * tx)) * ty));
                }
            }
        }

        return dst;
    }
}

/// <summary>The option tables of ref.shot_locate / shot_measure / shot_compare.</summary>
public static partial class ScriptOptionSets
{
    private const string ShotDoc = "Required: the eye-level screenshot (png, jpg...), relative to the level's folder.";
    private const string PoseDoc = "Required: {x, y?, z, yaw, pitch?, fov?} (a shot_locate pose as is); y = eye height in world (default: floor below + eye); yaw 0 = +Z, 90 = +X.";

    public static readonly ScriptOptionSet RefShotLocate = new("ref.shot_locate",
        new("shot", "string", null, ShotDoc),
        new("fov", "number", "90", "The shot's horizontal field of view in degrees (RF's default view is 90)."),
        new("eye", "number", "1.7", "Camera height above the floor it stands on (m)."),
        new("search", "{box?, step?, yaw?, floor?}", "whole floor, 1.5 m", "box = {x0, z0, x1, z1}; step = position spacing (m); yaw = {heading, +-range}; floor = only floors within 0.75 m of this y."),
        new("pitch", "number", "10", "Largest |pitch| searched (degrees); 0 = level camera."),
        new("keep", "number", "3", "How many distinct poses to return (1-6)."),
        new("out", "string", "none", "Write shot | build render | edge overlay (.png) for the best pose."),
        new("panel", "number", "300", "Panel height in pixels for out."),
        new("edges", "boolean", "true", "Add the edge-overlay panel to out."),
        new("mode", "string", "\"lit\"", "Render panel: lit | textured (the game renderer) | shaded (ray-cast grey)."),
        new("build", "string", "\"none\"", "\"sealed\" runs ops.build() first."));

    public static readonly ScriptOptionSet RefShotMeasure = new("ref.shot_measure",
        new("pose", "table", null, PoseDoc),
        new("px", "{x, y}", null, "Required: a pixel of the shot (its own pixel coordinates)."),
        new("shot", "string", null, "The screenshot (for its size); or give size."),
        new("size", "{w, h}", null, "The screenshot's pixel size instead of shot."),
        new("distance", "number", "none", "Also return at_distance: the pixel's height above the camera's floor at this horizontal distance (m)."),
        new("fov", "number", "pose.fov or 90", "Horizontal field of view override (degrees)."),
        new("eye", "number", "1.7", "Eye height over the floor when pose.y is not given."));

    public static readonly ScriptOptionSet RefShotCompare = new("ref.shot_compare",
        new("shot", "string", null, ShotDoc),
        new("pose", "table", null, PoseDoc),
        new("out", "string", null, "Required: the side-by-side .png (shot | build render | edges?)."),
        new("fov", "number", "pose.fov or 90", "Horizontal field of view override (degrees)."),
        new("eye", "number", "1.7", "Eye height over the floor when pose.y is not given."),
        new("panel", "number", "360", "Panel height in pixels (width follows the shot's aspect)."),
        new("edges", "boolean", "false", "Add a third panel: shot edges red, build edges cyan, both yellow."),
        new("mode", "string", "\"lit\"", "lit | textured (the game renderer; grey ray-cast shading where none) | shaded."),
        new("build", "string", "\"none\"", "\"sealed\" runs ops.build() first."));
}
