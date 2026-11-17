using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Reference;
using Ged.Automation.Rendering;

namespace Ged.Cli;

/// <summary>
/// <c>glacier-cli ref add|list|view|camera|compare|scale</c>: reference matching (<see cref="ReferenceCommand"/>). Every
/// operation but <c>compare</c> is file work in this process; <c>compare</c> renders the level (headless, or a private
/// copy of the editor's open level with <c>--attach</c>) exactly like <c>render</c>, then builds the comparison sheet.
/// Files are written where the command line names them (the reference set's folder, <c>--out</c>).
/// </summary>
internal static class RefCli
{
    private const string Verb = "ref";

    /// <summary>The ref flags and the command arguments they set.</summary>
    private static readonly (string Flag, string Arg)[] Valued =
    {
        ("--refs", "refs"), ("--id", "id"), ("--image", "image"), ("--crop", "crop"), ("--area", "area"), ("--notes", "notes"),
        ("--heading", "heading"), ("--pitch", "pitch"), ("--move", "move"), ("--turn", "turn"), ("--horizon", "horizon"),
        ("--vp", "vp"), ("--vp-dir", "vpDir"), ("--object", "object"), ("--measure", "measure"), ("--ignore", "ignore"),
        ("--panel", "panel"), ("--side", "size"), ("--count", "count"), ("--unit", "unit"), ("--step", "step"), ("--zoom", "zoom"),
    };

    private static readonly (string Flag, string Arg)[] Switches =
    {
        ("--snap", "snap"), ("--replace", "replace"), ("--clear-marks", "clearMarks"), ("--apply-estimate", "applyEstimate"),
    };

    private static readonly (string Flag, string Arg)[] RenderFlags =
    {
        ("--camera", "camera"), ("--look", "look"), ("--hfov", "hfov"), ("--eye-height", "eyeHeight"), ("--mode", "mode"),
    };

    public static async Task<int> RunAsync(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env)
    {
        string op = a.Positionals.FirstOrDefault()
            ?? throw new UsageException($"ref needs an operation: {string.Join(", ", ReferenceCommand.Ops)}.", Verb);
        if (!ReferenceCommand.Ops.Contains(op))
        {
            throw new UsageException($"Unknown ref operation '{op}' (use {string.Join(", ", ReferenceCommand.Ops)}).", Verb);
        }

        if (a.SpatialOptions.Count > 0 || a.Save || a.Light || a.DryRun || a.Sealed || a.NoBuild)
        {
            throw new UsageException("That option does not apply to 'ref'.", Verb);
        }

        foreach (string flag in a.RenderOptions.Keys.Where(k => RenderFlags.All(r => r.Flag != k)))
        {
            throw new UsageException($"{flag} does not apply to 'ref' (it takes --camera, --look, --hfov, --eye-height and --mode).", Verb);
        }

        if (a.Attach && op != "compare")
        {
            throw new UsageException("Only 'ref compare' uses --attach (to render the editor's open level).", Verb);
        }

        var args = new JsonObject { ["op"] = op };
        foreach ((string flag, string key) in Valued)
        {
            if (a.RefOptions.TryGetValue(flag, out string? v))
            {
                args[key] = v;
            }
        }

        foreach ((string flag, string key) in Switches)
        {
            if (a.RefOptions.ContainsKey(flag))
            {
                args[key] = true;
            }
        }

        foreach ((string flag, string key) in RenderFlags)
        {
            if (a.RenderOptions.TryGetValue(flag, out string? v))
            {
                args[key] = v;
            }
        }

        if (a.Out is not null)
        {
            args["out"] = a.Out;
        }

        string? level = Positionals(a, op, args);
        var io = new ReferenceIo { RequireWrite = PathAllowList.Normalize };
        AutomationResponse response;
        try
        {
            if (op != "compare")
            {
                response = ReferenceCommand.Execute(args, io);
            }
            else
            {
                ComparePlan plan = ReferenceCommand.PlanCompare(args, io);
                var renderArgs = (JsonObject)plan.RenderArgs.DeepClone();
                var explicitPaths = new List<string> { renderArgs["out"]!.GetValue<string>() };
                if (level is not null)
                {
                    string full = PathAllowList.Normalize(level);
                    renderArgs["level"] = full;
                    explicitPaths.Add(full);
                }
                else if (!a.Attach)
                {
                    throw new UsageException("ref compare needs a level file (or --attach to render the level open in the editor).", Verb);
                }

                AutomationResponse render = await CliApp.RenderAsync(a, new AutomationRequest(RenderCommand.Name, renderArgs), explicitPaths, env, stderr);
                response = ReferenceCommand.FinishCompare(plan, render);
            }
        }
        catch (AutomationException ex) when (ex.Code == AutomationErrorCodes.Usage)
        {
            throw new UsageException(ex.Message, Verb);
        }
        catch (AutomationException ex)
        {
            response = new AutomationResponse { Ok = false, Command = ReferenceCommand.Name, Error = new AutomationError(ex.Code, ex.Message) };
        }

        if (!response.Ok && response.Error?.Code == AutomationErrorCodes.Usage)
        {
            throw new UsageException(response.Error.Message, Verb);
        }

        return CliApp.Report(a, response, stdout, stderr);
    }

    /// <summary>Maps the positional arguments: <c>add &lt;image&gt;</c>, <c>view &lt;image|id&gt;</c>, <c>camera &lt;id&gt;</c>,
    /// <c>compare &lt;id&gt; [&lt;level&gt;]</c>, <c>scale [&lt;count&gt; &lt;unit&gt;]</c>. Returns the level of a compare.</summary>
    private static string? Positionals(CliArguments a, string op, JsonObject args)
    {
        List<string> p = a.Positionals.Skip(1).ToList();
        void Set(string key, string value)
        {
            if (args.ContainsKey(key))
            {
                throw new UsageException($"Give the {key} once (as an argument or with --{key}).", Verb);
            }

            args[key] = value;
        }

        void Max(int n)
        {
            if (p.Count > n)
            {
                throw new UsageException($"Unexpected argument '{p[n]}'.", Verb);
            }
        }

        switch (op)
        {
            case "add":
                Max(1);
                if (p.Count == 1)
                {
                    Set("image", p[0]);
                }

                break;
            case "view":
                Max(1);
                if (p.Count == 1)
                {
                    bool isImage = ReferenceCommand.ImageExtensions.Contains(Path.GetExtension(p[0]).ToLowerInvariant());
                    Set(isImage ? "image" : "id", p[0]);
                }

                break;
            case "camera":
                Max(1);
                if (p.Count == 1)
                {
                    Set("id", p[0]);
                }

                break;
            case "compare":
                Max(2);
                if (p.Count >= 1)
                {
                    Set("id", p[0]);
                }

                return p.Count == 2 ? p[1] : null;
            case "scale":
                Max(2);
                if (p.Count >= 1)
                {
                    Set("count", p[0]);
                }

                if (p.Count == 2)
                {
                    Set("unit", p[1]);
                }

                break;
            default:
                Max(0);
                break;
        }

        return null;
    }

    /// <summary>The readable output of a <c>reference</c> result.</summary>
    public static void Text(JsonNode x, TextWriter o)
    {
        string op = x["op"]?.GetValue<string>() ?? string.Empty;
        string S(JsonNode? n) => n?.ToJsonString(AutomationJson.Options) ?? "-";
        switch (op)
        {
            case "add":
                JsonNode r = x["reference"]!;
                o.WriteLine($"Registered '{r["id"]}'{(r["area"] is { } area ? $" ({area})" : string.Empty)}: {r["width"]}x{r["height"]} px -> {r["image"]}");
                if (r["crop"] is { } crop)
                {
                    o.WriteLine($"Crop:     {S(crop)}{(x["requestedCrop"] is { } asked ? $" (snapped from {S(asked)})" : string.Empty)}");
                }

                break;
            case "list":
                o.WriteLine($"{x["count"]} reference(s) in {x["refs"]}");
                foreach (JsonNode? e in x["references"]!.AsArray())
                {
                    o.WriteLine($"  {e!["id"],-14} {e["width"]}x{e["height"]}  {(e["camera"] is { } c ? $"camera {S(c["position"])} heading {c["heading"]} pitch {c["pitch"]}" : "no camera")}  {e["area"]}");
                }

                break;
            case "view":
                o.WriteLine($"Wrote {x["path"]} ({x["width"]}x{x["height"]}): {x["read"]}");
                break;
            case "camera":
                if (x["camera"] is { } cam)
                {
                    o.WriteLine($"Camera:   {S(cam["position"])} look {S(cam["look"])}  heading {cam["heading"]} pitch {cam["pitch"]}  hfov {cam["hfov"]} (vfov {cam["vfov"]}){(cam["eyeHeight"] is { } eh ? $"  eye {eh} m above the floor" : string.Empty)}");
                    o.WriteLine($"Render:   {x["renderFlags"]}");
                }
                else
                {
                    o.WriteLine("Camera:   not set");
                }

                if (x["estimate"] is { } est)
                {
                    o.WriteLine($"Estimate: pitch {S(est["pitchDeg"])}  roll {S(est["rollDeg"])}  horizon {S(est["horizon"])}  vp offset {S(est["vpOffsetDeg"])}  heading {S(est["headingDeg"])}  eye height {S(est["eyeHeight"])}  object distance {S(est["objectDistance"])}");
                    foreach (JsonNode? m in est["measurements"]?.AsArray() ?? new JsonArray())
                    {
                        o.WriteLine($"  measure {S(m!["mark"])}: height {S(m["height"])} m at {S(m["distance"])} m");
                    }
                }

                break;
            case "compare":
                o.WriteLine($"Wrote {x["path"]} ({x["width"]}x{x["height"]}); render {x["render"]}");
                JsonNode cues = x["cues"]!;
                foreach (string k in new[] { "edgeDensity", "plainTop", "brightness", "warmth", "horizon" })
                {
                    o.WriteLine($"  {k,-12} reference {S(cues["reference"]![k])}  render {S(cues["render"]![k])}");
                }

                o.WriteLine($"  edge overlap reference->render {S(cues["edgeOverlapReference"])}  render->reference {S(cues["edgeOverlapRender"])}");
                foreach (JsonNode? h in cues["hints"]?.AsArray() ?? new JsonArray())
                {
                    o.WriteLine($"  - {h}");
                }

                break;
            case "scale":
                if (x["conversion"] is { } conv)
                {
                    o.WriteLine($"{conv["count"]} x {conv["unit"]} = {conv["metres"]} m (range {S(conv["range"])})");
                }

                foreach (JsonNode? c in x["cues"]!.AsArray())
                {
                    o.WriteLine($"  {c!["unit"],-17} {c["metres"],5} m  ({S(c["range"])})  {c["what"]}");
                }

                break;
            default:
                o.WriteLine(x.ToJsonString(AutomationJson.Indented));
                break;
        }

        foreach (JsonNode? n in x["notes"]?.AsArray() ?? new JsonArray())
        {
            o.WriteLine($"note: {n}");
        }
    }
}
