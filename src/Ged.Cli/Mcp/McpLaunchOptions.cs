using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Automation.Tools;

namespace Ged.Cli.Mcp;

/// <summary>How the MCP server chooses its session (fixed for the server's lifetime).</summary>
internal enum McpMode
{
    /// <summary>Probe for the editor once at startup (1 s): attach when it answers, else headless.</summary>
    Auto,

    /// <summary><c>--attach</c>: always the running editor; with no editor, every call reports it and retries.</summary>
    Attach,

    /// <summary><c>--headless</c>: a window-less session in the server's process.</summary>
    Headless,
}

/// <summary>
/// How <c>glacier-cli mcp</c> was started. These options are the ONLY source of the session's permissions:
/// nothing a tool call sends can change them (MCP-SERVER-SPEC §7).
/// </summary>
internal sealed class McpLaunchOptions
{
    /// <summary>Where tool calls run: the running editor, a headless session, or (the default) whichever is available at startup.</summary>
    public McpMode Mode { get; init; } = McpMode.Auto;

    /// <summary>Mutating tools are not offered (and refused if called).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Discarding unsaved changes, overwriting other existing files, and script delete / save / package are allowed.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Extra folders tools may write under.</summary>
    public IReadOnlyList<string> AllowWrite { get; init; } = Array.Empty<string>();

    /// <summary>Exact model files <c>level.import_mesh</c> may read.</summary>
    public IReadOnlyList<string> AllowImport { get; init; } = Array.Empty<string>();

    /// <summary>The session's working folder (renders); null = a temporary folder removed on exit.</summary>
    public string? Scratch { get; init; }

    /// <summary>Offer only these tools (null = all).</summary>
    public IReadOnlySet<string>? Tools { get; init; }

    /// <summary>Never offer these tools.</summary>
    public IReadOnlySet<string> DenyTools { get; init; } = new HashSet<string>();

    /// <summary>The largest rendered image side.</summary>
    public int MaxImage { get; init; } = ToolCatalog.DefaultImageSide;

    /// <summary>The RF install to mount (null = GED_RF_DIR, then the editor's configured install).</summary>
    public string? RfDir { get; init; }

    /// <summary>Diagnostics file (null = standard error).</summary>
    public string? LogFile { get; init; }

    /// <summary>Builds the options from the parsed command line (usage errors for bad values).</summary>
    public static McpLaunchOptions From(CliArguments a)
    {
        if (a.Attach && a.Headless)
        {
            throw new UsageException("Use --attach or --headless, not both (without either, the server attaches when an editor answers).", "mcp");
        }

        if (a.Positionals.Count > 0)
        {
            throw new UsageException($"Unexpected argument '{a.Positionals[0]}'.", "mcp");
        }

        if (a.Json || a.Out is not null || a.Save || a.DryRun || a.Sealed || a.Light || a.Steps is not null || a.Detail is not null
            || a.Member is not null || a.Markdown || a.Lua || a.NoTag || a.NoBuild || a.FailOnHoles || a.RenderOptions.Count > 0
            || a.TimeoutSeconds is not null || a.Build || a.NoShadows || a.Jobs is not null || a.Limit is not null || a.Roots.Count > 0
            || a.Since is not null || a.MaxChanges is not null || a.What is not null || a.Kind is not null || a.Class is not null
            || a.Name is not null || a.Tag is not null || a.Offset is not null || a.Filter is not null || a.StockOnly || a.Label is not null
            || a.MinSeverity is not null)
        {
            throw new UsageException("mcp takes only --attach, --headless, --read-only, --allow-destructive, --allow-write, --allow-import, --scratch, " +
                "--tools, --deny-tools, --max-image, --rf-dir and --log.", "mcp");
        }

        if (a.MaxImage is int px && px > ToolCatalog.MaxImageSide)
        {
            throw new UsageException($"--max-image is at most {ToolCatalog.MaxImageSide}.", "mcp");
        }

        HashSet<string>? tools = a.Tools is { } t ? Names(t, "--tools") : null;
        HashSet<string> deny = a.DenyTools is { } d ? Names(d, "--deny-tools") : new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in a.AllowWrites)
        {
            if (!Directory.Exists(dir))
            {
                throw new UsageException($"--allow-write: folder not found: {dir}", "mcp");
            }
        }

        return new McpLaunchOptions
        {
            Mode = a.Attach ? McpMode.Attach : a.Headless ? McpMode.Headless : McpMode.Auto,
            ReadOnly = a.ReadOnly,
            AllowDestructive = a.AllowDestructive,
            AllowWrite = a.AllowWrites.Select(Path.GetFullPath).ToList(),
            AllowImport = a.AllowImports.Select(Path.GetFullPath).ToList(),
            Scratch = a.Scratch is null ? null : Path.GetFullPath(a.Scratch),
            Tools = tools,
            DenyTools = deny,
            MaxImage = a.MaxImage ?? ToolCatalog.DefaultImageSide,
            RfDir = a.RfDir,
            LogFile = a.LogFile,
        };
    }

    private static HashSet<string> Names(string list, string flag)
    {
        var names = new HashSet<string>(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
        foreach (string n in names)
        {
            if (ToolCatalog.Find(n) is null)
            {
                throw new UsageException($"{flag}: unknown tool '{n}'. Tools: {string.Join(", ", ToolCatalog.All.Select(x => x.Name))}.", "mcp");
            }
        }

        return names;
    }
}
