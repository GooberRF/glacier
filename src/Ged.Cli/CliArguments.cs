using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ged.Cli;

/// <summary>A command-line usage error (exit code 2).</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message, string? verb = null)
        : base(message)
    {
        Verb = verb;
    }

    /// <summary>The verb whose usage to print, when known.</summary>
    public string? Verb { get; }
}

/// <summary>
/// The parsed command line: <c>glacier-cli [global flags] &lt;verb&gt; [arguments] [flags]</c>. Flags may
/// appear anywhere after the program name; <c>--flag=value</c> and <c>--flag value</c> are equivalent.
/// </summary>
internal sealed class CliArguments
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        "--json", "--attach", "--dry-run", "--allow-destructive", "--sealed", "--light", "--no-shadows",
        "--fail-on-holes", "--no-tag", "--no-build", "--build", "--markdown", "--lua", "--help", "-h", "--version",
        "--no-collapse", "--no-snapshot", "--no-install", "--stock-only", "--headless", "--read-only", "--promote", "--compare",
        "--verbose",
    };

    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "--rf-dir", "--timeout", "--budget", "--out", "--detail", "--member", "--steps", "--allow-import",
        "--jobs", "--limit", "--root", "--snapshot", "--debug-render",
        "--since", "--max-changes", "--what", "--kind", "--class", "--name", "--tag", "--offset", "--filter",
        "--label", "--min-severity", "--metric", "--gametype", "--bucket",
        "--allow-write", "--scratch", "--tools", "--deny-tools", "--max-image", "--log",
    };

    /// <summary>The <c>render</c> command's switches (kept in <see cref="RenderOptions"/>).</summary>
    private static readonly HashSet<string> RenderSwitches = new(StringComparer.Ordinal)
    {
        "--plan", "--no-overlays", "--rebuild", "--icons", "--no-icons",
    };

    /// <summary>The <c>render</c> command's valued options (kept in <see cref="RenderOptions"/>).</summary>
    private static readonly HashSet<string> RenderValued = new(StringComparer.Ordinal)
    {
        "--view", "--y", "--thickness", "--below", "--camera", "--look", "--fov", "--region", "--room",
        "--uid", "--mode", "--size", "--overlays",
    };

    /// <summary>The <c>spatial</c> and <c>grade</c> commands' switches (kept in <see cref="SpatialOptions"/>).</summary>
    private static readonly HashSet<string> SpatialSwitches = new(StringComparer.Ordinal)
    {
        "--metrics",
    };

    /// <summary>The <c>spatial</c> and <c>grade</c> commands' valued options (kept in <see cref="SpatialOptions"/>).</summary>
    private static readonly HashSet<string> SpatialValued = new(StringComparer.Ordinal)
    {
        "--from", "--dir", "--a", "--b", "--pos", "--profile", "--k", "--scale", "--around", "--radius", "--max-nodes",
        "--player-class", "--max", "--players", "--fresh", "--max-samples", "--max-locations", "--fail-on",
    };

    /// <summary>The spatial / grade options given (flag → value; switches map to an empty string).</summary>
    public Dictionary<string, string> SpatialOptions { get; } = new(StringComparer.Ordinal);

    public string? Verb { get; private set; }

    public List<string> Positionals { get; } = new();

    public bool Json { get; private set; }

    public bool Attach { get; private set; }

    public bool DryRun { get; private set; }

    public bool AllowDestructive { get; private set; }

    public bool Sealed { get; private set; }

    public bool Light { get; private set; }

    public bool NoShadows { get; private set; }

    public bool FailOnHoles { get; private set; }

    /// <summary><c>--no-tag</c>: do not stamp provenance tags on what a <c>run</c> creates.</summary>
    public bool NoTag { get; private set; }

    /// <summary>Save without building first, even when the level has no compiled geometry (it will not load in the game).</summary>
    public bool NoBuild { get; private set; }

    /// <summary><c>run --build</c>: a sealed build after the run, before any save.</summary>
    public bool Build { get; private set; }

    /// <summary><c>api --markdown</c> (the default format).</summary>
    public bool Markdown { get; private set; }

    /// <summary><c>api --lua</c>: the Lua completion stub instead of the Markdown reference.</summary>
    public bool Lua { get; private set; }

    /// <summary><c>api --member &lt;name&gt;</c>: one global/type (<c>brush</c>) or member (<c>brush.set_pos</c>).</summary>
    public string? Member { get; private set; }

    /// <summary><c>undo --steps &lt;n&gt;</c>: how many command-line runs to undo (default 1).</summary>
    public int? Steps { get; private set; }

    public bool Help { get; private set; }

    public bool Version { get; private set; }

    public string? RfDir { get; private set; }

    /// <summary>Model files a headless <c>run</c> may read (<c>--allow-import &lt;file&gt;</c>, repeatable).</summary>
    public List<string> AllowImports { get; } = new();

    public double? TimeoutSeconds { get; private set; }

    /// <summary><c>run --budget &lt;instructions&gt;</c>: the Lua instruction budget (0 = unlimited).</summary>
    public long? Budget { get; private set; }

    /// <summary><c>--verbose</c>: report diagnostics (which RF install was mounted, and why) on stderr.</summary>
    public bool Verbose { get; private set; }

    public string? Out { get; private set; }

    public string? Detail { get; private set; }

    /// <summary><c>corpus --jobs &lt;n&gt;</c>: parallel workers.</summary>
    public int? Jobs { get; private set; }

    /// <summary><c>corpus sweep --debug-render &lt;n&gt;</c>: plan images of n seeded-random levels.</summary>
    public int? DebugRender { get; private set; }

    /// <summary><c>corpus sweep --limit &lt;n&gt;</c>: fingerprint at most n distinct levels.</summary>
    public int? Limit { get; private set; }

    /// <summary><c>corpus --root &lt;dir&gt;</c> (repeatable): the corpus roots, instead of the configured ones.</summary>
    public List<string> Roots { get; } = new();

    /// <summary><c>corpus verify --snapshot &lt;file&gt;</c>: the snapshot to compare with.</summary>
    public string? SnapshotPath { get; private set; }

    /// <summary><c>corpus sweep --no-collapse</c>: every level votes (no version-series collapse).</summary>
    public bool NoCollapse { get; private set; }

    /// <summary><c>corpus aggregate --promote</c>: copy the (size-checked) candidate norms into the source tree.</summary>
    public bool Promote { get; private set; }

    /// <summary><c>corpus sweep --no-snapshot</c>: do not take corpus-snapshot.json before reading.</summary>
    public bool NoSnapshot { get; private set; }

    /// <summary><c>corpus --no-install</c>: do not include the RF install's packfiles.</summary>
    public bool NoInstall { get; private set; }

    /// <summary><c>diff --since &lt;name&gt;</c>: the checkpoint to compare with.</summary>
    public string? Since { get; private set; }

    /// <summary><c>diff --max-changes &lt;n&gt;</c>.</summary>
    public int? MaxChanges { get; private set; }

    /// <summary><c>query --what objects|brushes</c>.</summary>
    public string? What { get; private set; }

    /// <summary><c>query / catalog --kind &lt;kind&gt;</c>.</summary>
    public string? Kind { get; private set; }

    /// <summary><c>query / catalog --class &lt;class or glob&gt;</c>.</summary>
    public string? Class { get; private set; }

    /// <summary><c>query --name &lt;name or glob&gt;</c>.</summary>
    public string? Name { get; private set; }

    /// <summary><c>query --tag &lt;key[=value]&gt;</c>.</summary>
    public string? Tag { get; private set; }

    /// <summary><c>query --offset &lt;n&gt;</c>.</summary>
    public int? Offset { get; private set; }

    /// <summary><c>catalog --filter &lt;substring or glob&gt;</c>.</summary>
    public string? Filter { get; private set; }

    /// <summary><c>catalog textures --stock-only</c>.</summary>
    public bool StockOnly { get; private set; }

    /// <summary><c>run --label &lt;text&gt;</c>: appended to the undo label.</summary>
    public string? Label { get; private set; }

    /// <summary><c>norms --metric &lt;id&gt;</c>.</summary>
    public string? Metric { get; private set; }

    /// <summary><c>norms --gametype &lt;g&gt;</c> / <c>--bucket &lt;b&gt;</c>: one bucket of a metric.</summary>
    public string? Bucket { get; private set; }

    /// <summary><c>norms --compare [level]</c>: compare a level with the norms.</summary>
    public bool Compare { get; private set; }

    /// <summary><c>lint --min-severity info|warning|error</c>.</summary>
    public string? MinSeverity { get; private set; }

    /// <summary><c>mcp --headless</c>.</summary>
    public bool Headless { get; private set; }

    /// <summary><c>mcp --read-only</c>: mutating tools are not offered.</summary>
    public bool ReadOnly { get; private set; }

    /// <summary><c>mcp --allow-write &lt;dir&gt;</c> (repeatable): extra folders tools may write under.</summary>
    public List<string> AllowWrites { get; } = new();

    /// <summary><c>mcp --scratch &lt;dir&gt;</c>: the session's working folder (renders land here).</summary>
    public string? Scratch { get; private set; }

    /// <summary><c>mcp --tools a,b</c>: offer only these tools.</summary>
    public string? Tools { get; private set; }

    /// <summary><c>mcp --deny-tools a,b</c>: never offer these tools.</summary>
    public string? DenyTools { get; private set; }

    /// <summary><c>mcp --max-image &lt;px&gt;</c>: the largest rendered image side.</summary>
    public int? MaxImage { get; private set; }

    /// <summary><c>mcp --log &lt;file&gt;</c>: diagnostics go to this file instead of standard error.</summary>
    public string? LogFile { get; private set; }

    /// <summary>The render options given (flag → value; switches map to an empty string).</summary>
    public Dictionary<string, string> RenderOptions { get; } = new(StringComparer.Ordinal);

    /// <summary>True when <c>--save</c> was given (with or without a path).</summary>
    public bool Save { get; private set; }

    /// <summary>The <c>--save</c> path, when one was given.</summary>
    public string? SavePath { get; private set; }

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var a = new CliArguments();
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (token == "--")
            {
                for (i++; i < args.Count; i++)
                {
                    a.AddPositional(args[i]);
                }

                break;
            }

            if (!token.StartsWith('-') || token == "-")
            {
                a.AddPositional(token);
                continue;
            }

            string name = token;
            string? inline = null;
            int eq = token.IndexOf('=');
            if (token.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                name = token[..eq];
                inline = token[(eq + 1)..];
            }

            if (name == "--save")
            {
                a.Save = true;
                // A save path is only ever "--save=<path>" (or --out <path>): a bare "--save" never
                // consumes the next token, so "run a.lua --save level.rfl" can never be misread as
                // "save a NEW level over level.rfl".
                if (inline is not null)
                {
                    a.SavePath = inline.Length > 0 ? inline : null;
                }

                continue;
            }

            if (Switches.Contains(name))
            {
                if (inline is not null)
                {
                    throw new UsageException($"'{name}' does not take a value.");
                }

                a.SetSwitch(name);
                continue;
            }

            if (RenderSwitches.Contains(name))
            {
                if (inline is not null)
                {
                    throw new UsageException($"'{name}' does not take a value.");
                }

                a.RenderOptions[name] = string.Empty;
                continue;
            }

            if (SpatialSwitches.Contains(name))
            {
                if (inline is not null)
                {
                    throw new UsageException($"'{name}' does not take a value.");
                }

                a.SpatialOptions[name] = string.Empty;
                continue;
            }

            if (SpatialValued.Contains(name))
            {
                a.SpatialOptions[name] = inline ?? (i + 1 < args.Count ? args[++i] : throw new UsageException($"'{name}' needs a value."));
                continue;
            }

            if (RenderValued.Contains(name))
            {
                a.RenderOptions[name] = inline ?? (i + 1 < args.Count ? args[++i] : throw new UsageException($"'{name}' needs a value."));
                continue;
            }

            if (Valued.Contains(name))
            {
                string value = inline ?? (i + 1 < args.Count ? args[++i] : throw new UsageException($"'{name}' needs a value."));
                a.SetValue(name, value);
                continue;
            }

            throw new UsageException($"Unknown option '{token}'.");
        }

        return a;
    }

    private void AddPositional(string value)
    {
        if (Verb is null)
        {
            Verb = value;
        }
        else
        {
            Positionals.Add(value);
        }
    }

    private void SetSwitch(string name)
    {
        switch (name)
        {
            case "--json": Json = true; break;
            case "--attach": Attach = true; break;
            case "--dry-run": DryRun = true; break;
            case "--allow-destructive": AllowDestructive = true; break;
            case "--sealed": Sealed = true; break;
            case "--light": Light = true; break;
            case "--no-shadows": NoShadows = true; break;
            case "--fail-on-holes": FailOnHoles = true; break;
            case "--no-tag": NoTag = true; break;
            case "--no-build": NoBuild = true; break;
            case "--build": Build = true; break;
            case "--markdown": Markdown = true; break;
            case "--lua": Lua = true; break;
            case "--help":
            case "-h": Help = true; break;
            case "--version": Version = true; break;
            case "--no-collapse": NoCollapse = true; break;
            case "--promote": Promote = true; break;
            case "--compare": Compare = true; break;
            case "--no-snapshot": NoSnapshot = true; break;
            case "--no-install": NoInstall = true; break;
            case "--stock-only": StockOnly = true; break;
            case "--headless": Headless = true; break;
            case "--read-only": ReadOnly = true; break;
            case "--verbose": Verbose = true; break;
        }
    }

    private void SetValue(string name, string value)
    {
        switch (name)
        {
            case "--rf-dir": RfDir = value; break;
            case "--out": Out = value; break;
            case "--allow-import": AllowImports.Add(value); break;
            case "--detail": Detail = value; break;
            case "--member": Member = value; break;
            case "--root": Roots.Add(value); break;
            case "--snapshot": SnapshotPath = value; break;
            case "--since": Since = value; break;
            case "--what": What = value; break;
            case "--kind": Kind = value; break;
            case "--class": Class = value; break;
            case "--name": Name = value; break;
            case "--tag": Tag = value; break;
            case "--filter": Filter = value; break;
            case "--label": Label = value; break;
            case "--min-severity": MinSeverity = value; break;
            case "--metric": Metric = value; break;
            case "--gametype":
            case "--bucket":
                Bucket = Bucket is null || Bucket == value ? value : throw new UsageException("Give the norms bucket once (--gametype or --bucket).");
                break;
            case "--allow-write": AllowWrites.Add(value); break;
            case "--scratch": Scratch = value; break;
            case "--tools": Tools = value; break;
            case "--deny-tools": DenyTools = value; break;
            case "--log": LogFile = value; break;
            case "--max-changes":
            case "--max-image":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int positive) || positive < 1)
                {
                    throw new UsageException($"{name} needs a whole number of at least 1 (got '{value}').");
                }

                if (name == "--max-changes")
                {
                    MaxChanges = positive;
                }
                else
                {
                    MaxImage = positive;
                }

                break;
            case "--offset":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
                {
                    throw new UsageException($"--offset needs a whole number of at least 0 (got '{value}').");
                }

                Offset = offset;
                break;
            case "--jobs":
            case "--limit":
            case "--debug-render":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1)
                {
                    throw new UsageException($"{name} needs a whole number of at least 1 (got '{value}').");
                }

                if (name == "--jobs")
                {
                    Jobs = count;
                }
                else if (name == "--limit")
                {
                    Limit = count;
                }
                else
                {
                    DebugRender = count;
                }

                break;
            case "--steps":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1)
                {
                    throw new UsageException($"--steps needs a whole number of at least 1 (got '{value}').");
                }

                Steps = n;
                break;
            case "--timeout":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || s < 0)
                {
                    throw new UsageException($"--timeout needs a number of seconds (got '{value}').");
                }

                TimeoutSeconds = s;
                break;
            case "--budget":
                if (!long.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal),
                        NumberStyles.None, CultureInfo.InvariantCulture, out long budget))
                {
                    throw new UsageException($"--budget needs a whole number of Lua instructions, 0 for unlimited (got '{value}').");
                }

                Budget = budget;
                break;
        }
    }
}
