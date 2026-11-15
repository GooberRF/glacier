using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>Level-wide texture alignment (<see cref="UvAutoAlign"/>) and UV quality measurement (<see cref="UvQuality"/>).</summary>
public sealed partial class ScriptLevel
{
    [ScriptDoc("Lines textures up like a mapper (default every brush): walls on the floor line, wrapped round corners, never mirrored; slopes and rock at true length; girders run up posts. Returns uv_align_report.",
        Example = "level.uv_align{}  level.uv_align{brushes = level.brushes_tagged(\"kit\"), ppm = 64}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.UvAlign))]
    public Dictionary<string, object?> UvAlign(ScriptArgs? options = null)
    {
        const string Api = "level.uv_align";
        options ??= new ScriptArgs();
        options.RejectUnknown(Api, ScriptOptionSets.UvAlign);
        List<int> uids = TargetBrushes(Api, options);
        return AlignBrushes(_ctx, Api, uids, options);
    }

    [ScriptDoc("Measures texture alignment on brush faces (default every brush): seams, density, stretch, mirrored walls, tile lines, thin members, the worst places (issues). Read-only; grader G21 checks the same.",
        Example = "local r = level.uv_report{} log.info(r.walls.handedness_major, r.seams.wall_corner.aligned)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.UvReport))]
    public Dictionary<string, object?> UvReport(ScriptArgs? options = null)
    {
        const string Api = "level.uv_report";
        options ??= new ScriptArgs();
        options.RejectUnknown(Api, ScriptOptionSets.UvReport);
        List<int> uids = TargetBrushes(Api, options);
        int max = options.Int(Api, "issues", 20);
        if (max < 0 || max > 500)
        {
            throw new ScriptApiException($"{Api}: 'issues' must be between 0 and 500 (got {max}).");
        }

        var brushes = uids.Select(u => _ctx.Brushes.FindBrush(u)).OfType<Brush>().ToList();
        var issues = new List<UvQualityIssue>();
        var all = new List<UvQualityIssue>();
        JsonObject report = UvQuality.Measure(UvQuality.FacesOf(brushes, Array.Empty<string>()), t => _ctx.TextureSizeOrNull(t), all, 500);
        report["members"] = UvMembers.Measure(brushes, t => _ctx.TextureSizeOrNull(t), t => _ctx.TextureGrainOf(t), all);
        issues.AddRange(all.OrderByDescending(i => i.Severity * Math.Sqrt(Math.Max(1e-3, i.Extent))).Take(max));
        report["issues"] = new JsonArray(issues.Select(i => (JsonNode)i.ToJson()).ToArray());
        return ScriptSpatial.ToLua(report);
    }

    /// <summary>The brushes an option table names: brushes = (brush, list, UIDs or brush_query), selection = true, else every brush.</summary>
    private List<int> TargetBrushes(string api, ScriptArgs options)
    {
        bool selection = options.Bool(api, "selection", false);
        if (selection && options.Has("brushes"))
        {
            throw new ScriptApiException($"{api}: give either 'brushes' or selection = true, not both.");
        }

        if (selection)
        {
            return _ctx.Brushes.SelectedBrushes.ToList();
        }

        if (!options.Named.TryGetValue("brushes", out object? raw) || raw is null)
        {
            return _ctx.Brushes.Brushes.Select(b => b.Uid).ToList();
        }

        return raw switch
        {
            ScriptBrushHandle h => new List<int> { h.Uid },
            ScriptBrushQuery q => q.Uids.Distinct().ToList(),
            double d => new List<int> { ScriptArgs.ToInt(api, "brushes", d) },
            ScriptArgs list => ScriptMembers.Brushes(_ctx, api, "brushes", list),
            _ => throw new ScriptApiException($"{api}: 'brushes' must be a brush, a list of brushes or UIDs, or a brush_query (got {ScriptArgs.LuaTypeOf(raw)})."),
        };
    }

    /// <summary>Plans <see cref="UvAutoAlign"/> over <paramref name="uids"/> and applies it as one undo step; returns the report table.</summary>
    internal static Dictionary<string, object?> AlignBrushes(ScriptContext ctx, string api, IReadOnlyList<int> uids, ScriptArgs options)
    {
        double ppm = options.Number(api, "ppm", 64);
        double tolerance = options.Number(api, "fit_tolerance", 0.15);
        double maxFold = options.Number(api, "max_fold", 100);
        double maxTwist = options.Number(api, "max_twist", 30);
        string membersOpt = options.String(api, "members", "auto") ?? "auto";
        UvMemberMode members = membersOpt switch
        {
            "auto" => UvMemberMode.Auto,
            "off" => UvMemberMode.Off,
            "only" => UvMemberMode.Only,
            _ => throw new ScriptApiException($"{api}: 'members' must be \"auto\", \"off\" or \"only\" (got \"{membersOpt}\")."),
        };
        if (!double.IsFinite(ppm) || ppm < 1 || ppm > UvOps.MaxPixelsPerMeter)
        {
            throw new ScriptApiException($"{api}: 'ppm' must be between 1 and {UvOps.MaxPixelsPerMeter} pixels per metre (got {ScriptArgs.Format(ppm)}).");
        }

        if (!double.IsFinite(tolerance) || tolerance < 0 || tolerance > 0.5)
        {
            throw new ScriptApiException($"{api}: 'fit_tolerance' must be between 0 and 0.5 (got {ScriptArgs.Format(tolerance)}).");
        }

        if (!double.IsFinite(maxFold) || maxFold < 0 || maxFold > 179 || !double.IsFinite(maxTwist) || maxTwist < 0 || maxTwist > 180)
        {
            throw new ScriptApiException($"{api}: 'max_fold' must be 0 to 179 degrees and 'max_twist' 0 to 180.");
        }

        var live = uids.Distinct().Select(u => ctx.Brushes.FindBrush(u)).OfType<Brush>().ToList();
        var opts = new UvAutoAlignOptions
        {
            PixelsPerMetre = ppm,
            FitHeight = options.Bool(api, "fit", true),
            FitTolerance = tolerance,
            MaxFoldDegrees = maxFold,
            MaxTwistDegrees = maxTwist,
            KeepFitted = options.Bool(api, "keep_fitted", true),
            TextureSize = ctx.TextureSize,
            MeasuredGrain = ctx.TextureGrainOf,
            Members = members,
        };
        (Dictionary<(int Brush, int Face), Uv[]> plan, UvAutoAlignStats stats) = UvAutoAlign.Plan(live, opts);
        var byUid = new Dictionary<int, List<(int Face, Uv[] Uvs)>>();
        foreach (((int bi, int fi), Uv[] list) in plan)
        {
            int uid = live[bi].Uid;
            if (!byUid.TryGetValue(uid, out List<(int, Uv[])>? l))
            {
                byUid[uid] = l = new List<(int, Uv[])>();
            }

            l.Add((fi, list));
        }

        var edited = byUid.Keys.ToList();
        if (edited.Count > 0)
        {
            OpResult r = ctx.Brushes.EditBrushes(edited, "Align textures", b =>
            {
                if (byUid.TryGetValue(b.Uid, out List<(int Face, Uv[] Uvs)>? faces))
                {
                    foreach ((int fi, Uv[] list) in faces)
                    {
                        if (fi < b.Geometry.Faces.Count)
                        {
                            UvAutoAlign.Write(b.Geometry.Faces[fi], list);
                        }
                    }
                }

                return OpResult.Ok();
            });
            if (!r.Success)
            {
                throw new ScriptApiException($"{api}: aligning textures failed: {r.Message}");
            }

            foreach (int uid in edited)
            {
                ctx.NoteBrushEdited(uid);
            }

            ctx.Changes.Record("uv", edited.Count);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["faces"] = (double)stats.Faces,
            ["floors"] = (double)stats.Horizontal,
            ["groups"] = (double)stats.Groups,
            ["folded"] = (double)stats.Folded,
            ["reset"] = (double)stats.Reset,
            ["fitted"] = (double)stats.FittedGroups,
            ["kept"] = (double)stats.Kept,
            ["members"] = (double)stats.MemberFaces,
            ["members_kept"] = (double)stats.MembersKept,
            ["brushes"] = (double)edited.Count,
        };
    }
}

/// <summary>The option sets of level-wide texture alignment and measurement.</summary>
public static partial class ScriptOptionSets
{
    public static readonly ScriptOptionSet UvAlign = new("level.uv_align",
        new("brushes", "brush | brush[] | uid[] | brush_query", "every brush", "The brushes to align (groups never reach outside them)."),
        new("selection", "boolean", "false", "Align the selected brushes instead."),
        new("ppm", "number", "64", "Texel density: pixels of each texture per metre (RED's stock density)."),
        new("fit", "boolean", "true", "Scale each wall group so its height spans whole repeats when that changes the density by at most fit_tolerance."),
        new("fit_tolerance", "number", "0.15", "Largest density change a height fit may make (0 to 0.5)."),
        new("keep_fitted", "boolean", "true", "Leave faces already fitted to whole tiles (crate sides, panels: both axes span whole repeats from a tile corner)."),
        new("max_fold", "number", "100", "Largest angle in degrees between two faces' normals for the texture to continue around their shared edge."),
        new("max_twist", "number", "30", "Largest turn away from upright (degrees) a texture folded around an edge may take before the face is set upright again."),
        new("members", "string", "\"auto\"", "Thin members (beams, posts, pilasters, rails: at least 3 times longer than their widest side, at most 1.25 m wide): \"auto\" lays a grained texture (girder, trim strip, post) along each member, one repeat at most across it, and leaves members already laid that way; \"only\" does just that and leaves every other face; \"off\" aligns members like walls."));

    public static readonly ScriptOptionSet UvReport = new("level.uv_report",
        new("brushes", "brush | brush[] | uid[] | brush_query", "every brush", "The brushes to measure."),
        new("selection", "boolean", "false", "Measure the selected brushes instead."),
        new("issues", "integer", "20", "List at most this many worst problems (0 to 500), worst first."));

    public static readonly ScriptOptionSet UvAlignQuery = new("brush_query:uv_align",
        new("ppm", "number", "64", "Texel density: pixels of each texture per metre (RED's stock density)."),
        new("fit", "boolean", "true", "Scale each wall group so its height spans whole repeats when that changes the density by at most fit_tolerance."),
        new("fit_tolerance", "number", "0.15", "Largest density change a height fit may make (0 to 0.5)."),
        new("keep_fitted", "boolean", "true", "Leave faces already fitted to whole tiles."),
        new("max_fold", "number", "100", "Largest angle in degrees between two faces' normals for the texture to continue around their shared edge."),
        new("max_twist", "number", "30", "Largest turn away from upright (degrees) a folded texture may take before the face is set upright again."),
        new("members", "string", "\"auto\"", "Thin members (beams, posts, pilasters, rails: at least 3 times longer than their widest side, at most 1.25 m wide): \"auto\" lays a grained texture (girder, trim strip, post) along each member, one repeat at most across it, and leaves members already laid that way; \"only\" does just that and leaves every other face; \"off\" aligns members like walls."));
}
