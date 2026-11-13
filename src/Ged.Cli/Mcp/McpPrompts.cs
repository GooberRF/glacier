using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Automation;

namespace Ged.Cli.Mcp;

/// <summary>One prompt argument: its name, what it means, whether it is required, and the text used when it is left out.</summary>
internal sealed record McpPromptArg(string Name, string Description, bool Required, string Default = "");

/// <summary>One prompt: its name, title, description and arguments; the body is the embedded <c>Prompts/&lt;name&gt;.md</c>.</summary>
internal sealed record McpPromptInfo(string Name, string Title, string Description, IReadOnlyList<McpPromptArg> Args);

/// <summary>
/// The MCP server's prompts (MCP-SERVER-SPEC §8): Markdown templates embedded from <c>src/Ged.Cli/Mcp/Prompts/*.md</c>,
/// with <c>{{argument}}</c> placeholders, each followed by the shared verification checklist and hand-back rule
/// (<c>Prompts/_checklist.md</c>). SDK-free.
/// </summary>
internal static class McpPrompts
{
    /// <summary>The longest argument value accepted (characters).</summary>
    public const int MaxArgumentLength = 8_000;

    public static IReadOnlyList<McpPromptInfo> All { get; } = new[]
    {
        new McpPromptInfo("blockout_from_brief", "Block out a level from a brief",
            "Plan, then block out a new level with the design kit from a written brief, verify it, and report what the designer should refine.",
            new[]
            {
                new McpPromptArg("brief", "What the level should be: size, rooms or areas, connections, storeys, items, mood.", Required: true),
                new McpPromptArg("gametype", "The game type it is for, e.g. deathmatch, CTF, team deathmatch, single player.", Required: false,
                    Default: "not given; infer it from the brief, and ask the designer if it changes the layout"),
            }),
        new McpPromptInfo("audit_level", "Review the open level",
            "Read-only review of the open level: summary, plan renders of every storey, holes and lint; findings with UIDs and positions, no edits.",
            new[]
            {
                new McpPromptArg("focus", "What to look at most closely, e.g. spawns, lighting, leaks, links, item placement.", Required: false,
                    Default: "everything (layout, leaks, lint, spawns, lighting, links)"),
            }),
        new McpPromptInfo("extend_level", "Extend the open level",
            "Add to the open level: checkpoint, study the surroundings, build the addition with the design kit, diff to confirm, verify.",
            new[]
            {
                new McpPromptArg("goal", "What to add and where, e.g. \"a balcony over the north hall reached by stairs from the east corridor\".", Required: true),
            }),
        new McpPromptInfo("fix_leaks", "Fix leaks",
            "Find the open level's holes with check_holes, render around them, make the smallest repair, and re-check until it is watertight.",
            Array.Empty<McpPromptArg>()),
    };

    /// <summary>The prompt named <paramref name="name"/>, or null.</summary>
    public static McpPromptInfo? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// The prompt's text with its arguments substituted and the checklist appended. Throws
    /// <see cref="AutomationException"/> (<c>usage</c>) for an unknown prompt, a missing required argument, an
    /// unknown argument or an over-long value.
    /// </summary>
    public static string Render(string name, IReadOnlyDictionary<string, string>? arguments)
    {
        McpPromptInfo prompt = Find(name)
            ?? throw new AutomationException(AutomationErrorCodes.Usage,
                $"Unknown prompt '{name}'. The prompts are: {string.Join(", ", All.Select(p => p.Name))}.");
        arguments ??= new Dictionary<string, string>();
        foreach (string key in arguments.Keys)
        {
            if (prompt.Args.All(a => a.Name != key))
            {
                throw new AutomationException(AutomationErrorCodes.Usage,
                    $"Prompt '{name}' has no argument '{key}'" + (prompt.Args.Count == 0 ? "; it takes none." : $"; it takes {string.Join(", ", prompt.Args.Select(a => a.Name))}."));
            }
        }

        string text = McpResources.Embedded($"Ged.Cli.Mcp.Prompts.{name}.md").TrimEnd('\n');
        foreach (McpPromptArg arg in prompt.Args)
        {
            string value = arguments.TryGetValue(arg.Name, out string? given) ? given.Trim() : string.Empty;
            if (value.Length == 0)
            {
                if (arg.Required)
                {
                    throw new AutomationException(AutomationErrorCodes.Usage, $"Prompt '{name}' needs the argument '{arg.Name}': {arg.Description}");
                }

                value = arg.Default;
            }

            if (value.Length > MaxArgumentLength)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The argument '{arg.Name}' is longer than {MaxArgumentLength} characters.");
            }

            text = text.Replace("{{" + arg.Name + "}}", value, StringComparison.Ordinal);
        }

        return text + "\n" + McpResources.Embedded("Ged.Cli.Mcp.Prompts._checklist.md");
    }
}
