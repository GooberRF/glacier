using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Automation;
using Ged.Automation.Analysis;
using Ged.Core.Modules;

namespace Ged.Cli;

/// <summary>
/// <c>glacier-cli modules harvest | list | show | stats | sheet</c>: the detail/architecture module library.
/// <c>harvest</c> reads level files READ-ONLY (never writing inside a source folder; an output folder that is, contains
/// or lies inside one is refused) and writes the library; <c>sheet</c> renders thumbnails and the curation contact sheet
/// (<see cref="ModuleSheet"/>) into its own folder; the others read the library. Runs headlessly; the JSON results are
/// the same as the <c>harvest_modules</c> / <c>module_library</c> tools'.
/// </summary>
internal static class ModulesCli
{
    private const string Verb = "modules";

    public static int Run(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env)
    {
        if (a.Attach)
        {
            throw new UsageException("'modules' runs without --attach (use --attach <kind> after 'modules list' to filter by attachment).", Verb);
        }

        string sub = a.Positionals.FirstOrDefault()
            ?? throw new UsageException("modules needs a subcommand: harvest, list, show, stats or sheet.", Verb);
        if (a.Save || a.DryRun || a.Build || a.Light || a.Sealed || a.Steps is not null || a.Detail is not null || a.NoTag || a.NoBuild
            || a.FailOnHoles || a.RenderOptions.Count > 0 || a.Markdown || a.Lua || a.Member is not null || a.AllowImports.Count > 0
            || a.AllowDestructive || a.NoShadows || a.SpatialOptions.Count > 0)
        {
            throw new UsageException("modules takes only its own options (see 'glacier-cli help modules').", Verb);
        }

        string? Opt(string name) => a.ModuleOptions.TryGetValue(name, out string? v) ? v : null;
        if (sub != "sheet" && (Opt("--top") is not null || Opt("--size") is not null))
        {
            throw new UsageException("--top and --size apply to 'modules sheet'.", Verb);
        }

        if (sub != "list" && a.IncludeRejected)
        {
            throw new UsageException("--include-rejected applies to 'modules list'.", Verb);
        }

        switch (sub)
        {
            case "harvest":
                return Harvest(a, stdout, stderr, env, Opt);
            case "sheet":
                return Sheet(a, stdout, stderr, env, Opt);
            case "list":
            case "show":
            case "stats":
                return Read(sub, a, stdout, env, Opt);
            default:
                throw new UsageException($"Unknown modules subcommand '{sub}' (use harvest, list, show, stats or sheet).", Verb);
        }
    }

    private static int Harvest(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env, Func<string, string?> opt)
    {
        if (a.Positionals.Count > 2)
        {
            throw new UsageException($"Unexpected argument '{a.Positionals[2]}'.", Verb);
        }

        if (new[] { "--category", "--attach", "--role", "--style", "--modules" }.Any(f => opt(f) is not null) || a.StockOnly || a.Limit is not null)
        {
            throw new UsageException("--category, --attach, --role, --style, --modules, --stock-only and --limit apply to 'modules list'.", Verb);
        }

        if (a.Roots.Count > 1)
        {
            throw new UsageException("Give --root once.", Verb);
        }

        if (a.RfDir is not null && !Directory.Exists(a.RfDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"--rf-dir: directory not found: {a.RfDir}");
        }

        string? source = a.Positionals.Count > 1 ? a.Positionals[1] : null;
        string? rf = CorpusConfig.Resolve(null, a.RfDir, a.NoInstall, env.GetEnvironmentVariable, env.RepoRoot()).RfInstall;
        int jobs = a.Jobs ?? Math.Max(1, Environment.ProcessorCount - 1);
        (ModuleHarvestReport report, JsonObject result) = ModuleLibraryCommands.Harvest(
            source, opt("--sources"), a.Roots.FirstOrDefault(), a.Out, opt("--group"), rf, jobs,
            line => stderr.WriteLine($"glacier-cli: {line}"), env.GetEnvironmentVariable, CancellationToken.None);
        if (a.Json)
        {
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse { Ok = true, Command = "modules harvest", Result = result }, indented: true));
        }
        else
        {
            stdout.Write(report.ToText());
        }

        return ExitCodes.Ok;
    }

    private static int Sheet(CliArguments a, TextWriter stdout, TextWriter stderr, CliEnvironment env, Func<string, string?> opt)
    {
        Max(a, 1);
        if (a.Roots.Count > 0 || a.Jobs is not null || opt("--sources") is not null || opt("--role") is not null || opt("--attach") is not null
            || a.StockOnly || a.Limit is not null)
        {
            throw new UsageException("modules sheet takes --modules, --group, --category, --style, --top, --size, --rf-dir and --out.", Verb);
        }

        string outDir = a.Out ?? throw new UsageException("modules sheet needs an output folder (--out <dir>).", Verb);
        int top = Whole(opt("--top"), "--top", ModuleSheet.DefaultTop);
        int size = Whole(opt("--size"), "--size", ModuleSheet.DefaultSize);
        if (a.RfDir is not null && !Directory.Exists(a.RfDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"--rf-dir: directory not found: {a.RfDir}");
        }

        ModuleLibrary lib = ModuleLibrary.Locate(opt("--modules"), env.GetEnvironmentVariable);
        if (lib.AllModules.Count == 0)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"No module library: {lib.Reason}");
        }

        string? category = opt("--category")?.Trim().ToLowerInvariant();
        string? style = opt("--style")?.Trim().ToLowerInvariant();
        if (category is not null && !ModuleVocabulary.Categories.Contains(category))
        {
            throw new UsageException($"Unknown category '{category}' ({string.Join(", ", ModuleVocabulary.Categories)}).", Verb);
        }

        if (style is not null && !ModuleVocabulary.Styles.Contains(style))
        {
            throw new UsageException($"Unknown style '{style}' ({string.Join(", ", ModuleVocabulary.Styles)}).", Verb);
        }

        string? rf = CorpusConfig.Resolve(null, a.RfDir, a.NoInstall, env.GetEnvironmentVariable, env.RepoRoot()).RfInstall;
        if (rf is null)
        {
            stderr.WriteLine("glacier-cli: warning: no RF install (--rf-dir, GED_RF_DIR, research/rf-dirs.txt): thumbnails are untextured.");
        }

        ModuleSheetReport report = ModuleSheet.BuildAsync(new ModuleSheetRequest
        {
            Library = lib,
            OutDir = outDir,
            Group = opt("--group"),
            Category = category,
            Style = style,
            Top = top,
            Size = size,
            RfInstall = rf,
            Device = env.RenderDevice,
            Log = a.Verbose ? line => stderr.WriteLine($"glacier-cli: {line}") : null,
        }).GetAwaiter().GetResult();
        if (a.Json)
        {
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse { Ok = true, Command = "modules sheet", Result = report.ToJson() }, indented: true));
        }
        else
        {
            stdout.Write(report.ToText());
        }

        return ExitCodes.Ok;
    }

    private static int Whole(string? text, string flag, int fallback)
    {
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 1)
        {
            throw new UsageException($"{flag} needs a whole number of at least 1 (got '{text}').", Verb);
        }

        return v;
    }

    private static int Read(string sub, CliArguments a, TextWriter stdout, CliEnvironment env, Func<string, string?> opt)
    {
        if (a.Out is not null || a.Roots.Count > 0 || a.Jobs is not null || a.RfDir is not null || opt("--sources") is not null)
        {
            throw new UsageException("--out, --root, --jobs, --rf-dir and --sources apply to 'modules harvest'.", Verb);
        }

        ModuleLibrary lib = ModuleLibrary.Locate(opt("--modules"), env.GetEnvironmentVariable);
        JsonObject result;
        switch (sub)
        {
            case "show":
                if (a.Positionals.Count != 2)
                {
                    throw new UsageException("modules show needs one module id.", Verb);
                }

                result = ModuleLibraryCommands.Show(lib, a.Positionals[1]);
                break;
            case "stats":
                Max(a, 1);
                result = ModuleLibraryCommands.Stats(lib);
                break;
            default:
                Max(a, 1);
                result = ModuleLibraryCommands.List(lib, new ModuleQuery
                {
                    Category = opt("--category"),
                    Attach = opt("--attach"),
                    Role = opt("--role"),
                    Style = opt("--style"),
                    Group = opt("--group"),
                    StockOnly = a.StockOnly,
                    IncludeRejected = a.IncludeRejected,
                }, a.Limit ?? ModuleLibraryCommands.DefaultLimit);
                break;
        }

        if (a.Json)
        {
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse { Ok = true, Command = "modules " + sub, Result = result }, indented: true));
            return ExitCodes.Ok;
        }

        WriteText(sub, result, lib, stdout);
        return ExitCodes.Ok;
    }

    private static void Max(CliArguments a, int count)
    {
        if (a.Positionals.Count > count)
        {
            throw new UsageException($"Unexpected argument '{a.Positionals[count]}'.", Verb);
        }
    }

    private static void WriteText(string sub, JsonObject result, ModuleLibrary lib, TextWriter o)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        if (lib.AllModules.Count == 0)
        {
            o.WriteLine($"No module library: {lib.Reason}");
            return;
        }

        o.WriteLine($"Module library: {lib.Directory} ({lib.Modules.Count} modules, located: {lib.How})");
        switch (sub)
        {
            case "stats":
            {
                JsonObject s = result["stats"]!.AsObject();
                o.WriteLine($"{s["modules"]} modules, {s["occurrences"]} occurrences, {s["custom_texture_modules"]} with custom textures.");
                foreach (string key in new[] { "by_group", "by_category", "by_attach", "by_role", "by_style" })
                {
                    o.WriteLine($"{key.Replace('_', ' ')}: " + string.Join(", ", s[key]!.AsObject().Select(kv => $"{kv.Key} {kv.Value}")));
                }

                JsonObject c = s["curation"]!.AsObject();
                o.WriteLine($"curation: {c["kept"]} kept, {c["rejected"]} rejected (left out of the counts above), {c["relabelled"]} relabelled, {c["undecided"]} undecided"
                    + (c["unknown_ids"]!.GetValue<int>() > 0 ? $", {c["unknown_ids"]} unknown id(s)" : string.Empty)
                    + (c["ignored_entries"]!.GetValue<int>() > 0 ? $", {c["ignored_entries"]} bad entr(ies) ignored" : string.Empty) + ".");
                foreach (string w in lib.Warnings)
                {
                    o.WriteLine($"warning: {w}");
                }

                break;
            }

            case "show":
            {
                JsonObject m = result["module"]!.AsObject();
                ModuleInfo info = lib.Find(m["id"]!.GetValue<string>(), includeRejected: true)!;
                o.WriteLine($"{info.Id}: {info.Category} ({info.Role}), attach {info.Attach}, styles {string.Join("/", info.Styles)}");
                if (info.Verdict is not null || info.Relabelled || info.CurationNote is not null)
                {
                    string was = info.Harvested is { } h ? $" (harvested as {h.Category}, {h.Role}, {string.Join("/", h.Styles)})" : string.Empty;
                    o.WriteLine($"  curation: {info.Verdict ?? "undecided"}{was}{(info.CurationNote is null ? string.Empty : " - " + info.CurationNote)}");
                }
                o.WriteLine(string.Create(inv, $"  size {info.Size.X:0.###} x {info.Size.Y:0.###} x {info.Size.Z:0.###} m, {info.Brushes} brush(es), {info.Faces} faces, score {info.Score:0.###}, {info.Occurrences} occurrence(s)"));
                o.WriteLine($"  anchor: {info.Anchor}");
                for (int axis = 0; axis < 3; axis++)
                {
                    if (info.Stretch(axis) is { } s)
                    {
                        o.WriteLine(string.Create(inv, $"  stretch {ModuleVocabulary.AxisName(axis)}: {s.A:0.###} .. {s.B:0.###}"));
                    }
                }

                foreach (ModuleTileFamily t in info.Tiles)
                {
                    o.WriteLine(string.Create(inv, $"  tiles on {t.Axis}: {t.Count} units at {t.Pitch:0.###} m (margins {t.MarginStart:0.###} / {t.MarginEnd:0.###})"));
                }

                if (info.MountHeight is double mh)
                {
                    o.WriteLine(string.Create(inv, $"  mount height {mh:0.###} m"));
                }

                if (info.Opening is { } op)
                {
                    o.WriteLine(string.Create(inv, $"  opening {op.Width:0.###} x {op.Height:0.###} m in a {op.WallThickness:0.###} m wall"));
                }

                o.WriteLine("  textures: " + string.Join(", ", info.Textures.Select(t => t.Stock ? t.Name : $"{t.Name} ({t.Source ?? "custom"})")));
                foreach (ModuleProvenance p in info.Provenance)
                {
                    o.WriteLine(string.Create(inv, $"  from {p.Level} at ({p.Position.X:0.##}, {p.Position.Y:0.##}, {p.Position.Z:0.##}) yaw {p.Yaw:0.#}"));
                }

                o.WriteLine($"  file: {info.RfgPath}");
                break;
            }

            default:
            {
                o.WriteLine($"{result["total"]} match(es); showing {result["returned"]} by score:");
                foreach (JsonNode? row in result["modules"]!.AsArray())
                {
                    ModuleInfo m = lib.Find(row!["id"]!.GetValue<string>(), includeRejected: true)!;
                    string stretch = m.StretchAxes.Count == 0 ? "-" : string.Join(string.Empty, m.StretchAxes);
                    string tiles = m.Tiles.Count == 0 ? string.Empty : " tiles " + string.Join(",", m.Tiles.Select(t => $"{t.Axis}x{t.Count}"));
                    o.WriteLine(string.Create(inv, $"  {m.Id,-26} {m.Category + "/" + m.Attach,-22} {m.Size.X,6:0.00} x {m.Size.Y,5:0.00} x {m.Size.Z,5:0.00}  stretch {stretch,-3} score {m.SelectionScore:0.000}{(m.Verdict is null ? string.Empty : " " + m.Verdict)} x{m.Occurrences}{tiles}  {m.Provenance.FirstOrDefault()?.Level}"));
                }

                break;
            }
        }
    }
}
