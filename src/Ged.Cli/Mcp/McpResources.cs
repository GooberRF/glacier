using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Ged.Automation;
using Ged.Automation.Tools;
using Ged.Core.Scripting;

namespace Ged.Cli.Mcp;

/// <summary>One listed resource: its URI, short name, title, what it holds, and its media type.</summary>
internal sealed record McpResourceInfo(string Uri, string Name, string Title, string Description, string MimeType);

/// <summary>One resource template (RFC 6570 level 1): the URI pattern and what each expansion holds.</summary>
internal sealed record McpResourceTemplateInfo(string UriTemplate, string Name, string Title, string Description, string MimeType);

/// <summary>The text of one resource read.</summary>
internal sealed record McpResourceText(string Uri, string MimeType, string Text);

/// <summary>
/// The MCP server's resources (MCP-SERVER-SPEC §8): the agent guide (embedded in glacier-cli at build), the scripting
/// conventions, the Lua reference index / one entry / completion stub, the design kit's documentation and source, and
/// a brief summary of the open level. Everything except the level summary is static for a build and served here; the
/// level summary goes through the tool service (one call at a time, like a tool). SDK-free, so it is unit-testable.
/// </summary>
internal static class McpResources
{
    public const string GuideUri = "glacier://guide";
    public const string GuideIndexUri = "glacier://guide/index";
    public const string GuideSectionPrefix = "glacier://guide/";
    public const string GuideSectionTemplate = "glacier://guide/{section}";
    public const string ConventionsUri = "glacier://conventions";
    public const string ApiIndexUri = "glacier://api/lua";
    public const string ApiMemberPrefix = "glacier://api/lua/";
    public const string ApiMemberTemplate = "glacier://api/lua/{member}";
    public const string ApiStubUri = "glacier://api/stub";
    public const string KitUri = "glacier://kit";
    public const string KitSourceUri = "glacier://kit/source";
    public const string LevelSummaryUri = "glacier://level/current/summary";
    public const string NormsUri = "glacier://norms";
    public const string NormsMetricPrefix = "glacier://norms/";
    public const string NormsMetricTemplate = "glacier://norms/{metric}";
    public const string ReferenceScaleUri = "glacier://reference/scale";

    /// <summary>The manifest-resource name of the embedded agent guide.</summary>
    internal const string GuideResource = "Ged.Cli.Mcp.AGENT-GUIDE.md";

    /// <summary>The resources this server lists (the level summary only when <paramref name="levelSummary"/>: its tool is offered).</summary>
    public static IReadOnlyList<McpResourceInfo> List(bool levelSummary)
    {
        var list = new List<McpResourceInfo>
        {
            new(GuideIndexUri, "guide-index", "Glacier agent guide: sections",
                "The agent guide's sections with their numbers, titles and sizes. Start here: read the sections you need with " +
                "glacier://guide/<section> (1-3 and 9 before your first edit), each small enough for one read.",
                "text/markdown"),
            new(GuideUri, "guide", "Glacier agent guide",
                "How to build and edit Red Faction levels with Glacier, as one document: the build-look-check loop, the world model, the design kit, " +
                "discovery, editing existing levels, the verification checklist, pitfalls and a worked example. It is long; a client " +
                "that cuts long reads short should read glacier://guide/index and the sections instead.",
                "text/markdown"),
            new(ConventionsUri, "conventions", "Scripting conventions",
                "Units, axes, rotation, CSG time order, texture scale and the other rules every script follows (the same text as ged.conventions).",
                "text/plain"),
            new(ApiIndexUri, "api-index", "Lua API index",
                "One line per global, type and design-kit function. Read one entry with glacier://api/lua/<name> (e.g. level.brush, kit.room).",
                "text/plain"),
            new(ApiStubUri, "api-stub", "Lua API stub",
                "The scripting API as an annotated Lua stub (signatures and types of every global and member).",
                "text/x-lua"),
            new(KitUri, "kit", "Design kit reference",
                "Every design-kit function (local kit = ged.import(\"kit\")) with its option keys, types, defaults and meaning.",
                "text/plain"),
            new(KitSourceUri, "kit-source", "Design kit source",
                "The design kit's Lua source as shipped with this build (scripts/lib/kit.lua, then each kit_* module it installs functions from): read it to see exactly what a kit function builds.",
                "text/x-lua"),
        };
        list.Add(new McpResourceInfo(ReferenceScaleUri, "reference-scale", "Known sizes for reading scale",
            "Red Faction dimensions measured on the stock levels (door panel, doorway, corridor, room height, stair step) plus " +
            "player, eye and jump heights, with ranges and sources: count them in a reference screenshot to read its scale.",
            "application/json"));
        if (Ged.Core.Analysis.CorpusNorms.Available)
        {
            list.Add(new McpResourceInfo(NormsUri, "norms", "Corpus norms: metrics",
                "The corpus norms built into Glacier: every level metric (doorway and corridor sizes, headroom, light density, texture tiling, ...) " +
                "with unit, reliability, use and published buckets. Read one metric with glacier://norms/<metric> (e.g. opening.width).",
                "application/json"));
        }

        if (levelSummary)
        {
            list.Add(new McpResourceInfo(LevelSummaryUri, "level-summary", "Open level summary",
                "A brief structured summary of the level open in this session (the level_summary tool with detail brief). " +
                "Read it again after a change; it is not pushed.",
                "application/json"));
        }

        return list;
    }

    /// <summary>The resource templates this server lists.</summary>
    public static IReadOnlyList<McpResourceTemplateInfo> Templates { get; } = new[]
    {
        new McpResourceTemplateInfo(GuideSectionTemplate, "guide-section", "Glacier agent guide section",
            "One section of the agent guide by number (glacier://guide/3 is the core loop, 9 the checklist); a long section is split into lettered parts (6a, 6b). glacier://guide/index lists them all.",
            "text/markdown"),
        new McpResourceTemplateInfo(ApiMemberTemplate, "api-entry", "Lua API entry",
            "One global, type or member of the Lua scripting API, or a design-kit function: level, brush, level.brush, brush:set_pos, kit.room.",
            "text/plain"),
        new McpResourceTemplateInfo(NormsMetricTemplate, "norms-metric", "Corpus norms: one metric",
            "One metric of the corpus norms with its percentiles (p5, p25, p50, p75, p95, n) in every published bucket (exemplar, overall, dm, ctf, sp, ...).",
            "application/json"),
    };

    /// <summary>
    /// Reads a static resource. Returns null for the level summary (served by the tool service) and for an unknown URI;
    /// throws <see cref="AutomationException"/> (<c>usage</c>) for an unknown API name, naming the nearest names.
    /// </summary>
    public static McpResourceText? ReadStatic(string uri)
    {
        switch (uri)
        {
            case GuideUri:
                return new McpResourceText(uri, "text/markdown", Guide());
            case GuideIndexUri:
                return new McpResourceText(uri, "text/markdown", GuideIndex());
            case ConventionsUri:
                return new McpResourceText(uri, "text/plain", ScriptConventions.Text);
            case ApiIndexUri:
                return new McpResourceText(uri, "text/plain", ApiReferenceText.Index());
            case ApiStubUri:
                return new McpResourceText(uri, "text/x-lua", ScriptApiReference.GenerateLuaStub());
            case KitUri:
                return new McpResourceText(uri, "text/plain", KitText());
            case KitSourceUri:
                return new McpResourceText(uri, "text/x-lua", ScriptKitSource.Combined()
                    ?? throw new AutomationException(AutomationErrorCodes.Usage, "This build ships no design kit."));
            case ReferenceScaleUri:
                return new McpResourceText(uri, "application/json",
                    Ged.Automation.Reference.ReferenceCommand.Execute(new System.Text.Json.Nodes.JsonObject { ["op"] = "scale" },
                        new Ged.Automation.Reference.ReferenceIo { RequireWrite = p => p }).Result!.ToJsonString(AutomationJson.Options));
        }

        if (uri.StartsWith(GuideSectionPrefix, StringComparison.Ordinal) && uri.Length > GuideSectionPrefix.Length)
        {
            return new McpResourceText(uri, "text/markdown", GuideSection(Uri.UnescapeDataString(uri[GuideSectionPrefix.Length..])));
        }

        if (uri == NormsUri && Ged.Core.Analysis.CorpusNorms.Available)
        {
            return new McpResourceText(uri, "application/json", NormsIndex());
        }

        if (uri.StartsWith(NormsMetricPrefix, StringComparison.Ordinal) && uri.Length > NormsMetricPrefix.Length && Ged.Core.Analysis.CorpusNorms.Available)
        {
            return new McpResourceText(uri, "application/json", NormsMetric(Uri.UnescapeDataString(uri[NormsMetricPrefix.Length..])));
        }

        if (uri.StartsWith(ApiMemberPrefix, StringComparison.Ordinal) && uri.Length > ApiMemberPrefix.Length)
        {
            string member = Uri.UnescapeDataString(uri[ApiMemberPrefix.Length..]);
            return new McpResourceText(uri, "text/plain", ApiEntry(member, ToolCatalog.DefaultResultBudget));
        }

        return null;
    }

    /// <summary>
    /// One API entry as text. A type whose full entry is over <paramref name="budget"/> characters is returned as its
    /// member index (read one member for the details), like the <c>api_reference</c> tool.
    /// </summary>
    public static string ApiEntry(string member, int budget)
    {
        (string name, string text) = ApiReferenceText.Member(member);
        if (text.Length <= budget)
        {
            return text;
        }

        if (!name.Contains('.', StringComparison.Ordinal) && ScriptApiReference.Describe(name) is { } type)
        {
            return $"{type.Name}: {type.Summary}\nThe full entry is long; read one member (e.g. {ApiMemberPrefix}{type.Name}.{type.Members.FirstOrDefault()?.Name}):\n  "
                + string.Join("\n  ", type.Members.Select(m => m.Name).Distinct(StringComparer.Ordinal));
        }

        return text[..Math.Max(0, budget - 2)] + "\n…";
    }

    /// <summary>The norms metric index (<c>glacier://norms</c>).</summary>
    public static string NormsIndex()
    {
        var metrics = new JsonArray();
        foreach (string id in Ged.Core.Analysis.CorpusNorms.MetricIds)
        {
            Ged.Core.Analysis.NormMetric m = Ged.Core.Analysis.CorpusNorms.Metric(id)!;
            metrics.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["unit"] = m.Unit,
                ["reliability"] = m.Reliability,
                ["use"] = m.Use,
                ["doc"] = m.Doc,
                ["buckets"] = new JsonArray(m.Buckets.Select(b => (JsonNode?)b).ToArray()),
            });
        }

        return new JsonObject
        {
            ["generated"] = Ged.Core.Analysis.CorpusNorms.Generated,
            ["referenceBucket"] = Ged.Core.Analysis.CorpusNorms.ReferenceBucket,
            ["corpus"] = Ged.Core.Analysis.CorpusNorms.Document?["corpus"]?.DeepClone(),
            ["metrics"] = metrics,
        }.ToJsonString(AutomationJson.Indented);
    }

    /// <summary>One metric of the norms (<c>glacier://norms/{metric}</c>); an unknown id names the nearest ids.</summary>
    public static string NormsMetric(string id)
    {
        if (Ged.Core.Analysis.CorpusNorms.Document?["metrics"]?[id] is not JsonObject m)
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"Unknown norms metric '{id}'. {ScriptSuggest.Hint(id, Ged.Core.Analysis.CorpusNorms.MetricIds, "metric")}");
        }

        JsonObject copy = (JsonObject)m.DeepClone();
        copy["id"] = id;
        copy["reference"] = Ged.Core.Analysis.CorpusNorms.Get(id)?.Bucket;
        return copy.ToJsonString(AutomationJson.Indented);
    }

    /// <summary>The design kit's function reference (each function with its option keys).</summary>
    public static string KitText() =>
        ScriptApiReference.Describe("kit")?.Text
        ?? throw new AutomationException(AutomationErrorCodes.Usage, "This build ships no design kit.");

    /// <summary>The agent guide embedded at build time (docs/ai/AGENT-GUIDE.md), line endings normalized to \n.</summary>
    public static string Guide() => Embedded(GuideResource);

    /// <summary>The longest guide section (characters) served in one read; a longer section is split into parts.</summary>
    internal const int MaxSectionChars = 20_000;

    /// <summary>One readable piece of the guide: its id (<c>3</c>, or <c>6a</c> for a part), title and Markdown text.</summary>
    internal sealed record GuideSectionInfo(string Id, string Title, string Text);

    /// <summary>
    /// The guide split at its numbered <c>## N. Title</c> headings (code fences respected). A section longer than
    /// <see cref="MaxSectionChars"/> is split at its <c>###</c> headings into lettered parts (<c>6a</c>, <c>6b</c>),
    /// each packed as full as the limit allows; a later part starts with the section heading marked "(part k of n)".
    /// The text before section 1 (title and introduction) is not a section: <see cref="GuideIndex"/> carries it.
    /// </summary>
    internal static IReadOnlyList<GuideSectionInfo> GuideSections() => SplitGuide(Guide());

    internal static IReadOnlyList<GuideSectionInfo> SplitGuide(string guide)
    {
        var sections = new List<(string Number, string Title, List<string> Lines)>();
        bool fence = false;
        foreach (string line in guide.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
            }

            System.Text.RegularExpressions.Match m = fence ? System.Text.RegularExpressions.Match.Empty
                : System.Text.RegularExpressions.Regex.Match(line, @"^## (?<n>\d+)\. (?<t>.+)$");
            if (m.Success)
            {
                sections.Add((m.Groups["n"].Value, m.Groups["t"].Value.Trim(), new List<string>()));
            }

            if (sections.Count > 0)
            {
                sections[^1].Lines.Add(line);
            }
        }

        var result = new List<GuideSectionInfo>();
        foreach ((string number, string title, List<string> lines) in sections)
        {
            string text = string.Join("\n", lines).TrimEnd('\n') + "\n";
            if (text.Length <= MaxSectionChars)
            {
                result.Add(new GuideSectionInfo(number, title, text));
                continue;
            }

            // Split at ### headings (outside code fences) and pack the blocks into parts under the limit.
            var blocks = new List<List<string>> { new() };
            bool inFence = false;
            foreach (string line in text.TrimEnd('\n').Split('\n'))
            {
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                }

                if (!inFence && line.StartsWith("### ", StringComparison.Ordinal) && blocks[^1].Count > 0)
                {
                    blocks.Add(new List<string>());
                }

                blocks[^1].Add(line);
            }

            var parts = new List<List<string>> { new() };
            foreach (List<string> block in blocks)
            {
                int current = parts[^1].Sum(l => l.Length + 1), size = block.Sum(l => l.Length + 1);
                if (parts[^1].Count > 0 && current + size > MaxSectionChars - 200)
                {
                    parts.Add(new List<string>());
                }

                parts[^1].AddRange(block);
            }

            for (int k = 0; k < parts.Count; k++)
            {
                string id = number + (char)('a' + k);
                string first = parts[k].FirstOrDefault(l => l.StartsWith("### ", StringComparison.Ordinal))?[4..].Trim() ?? "overview";
                string body = string.Join("\n", parts[k]).Trim('\n') + "\n";
                if (k > 0)
                {
                    body = $"## {number}. {title} (part {k + 1} of {parts.Count})\n\n" + body;
                }

                result.Add(new GuideSectionInfo(id, $"{title} (part {k + 1} of {parts.Count}: {(k == 0 ? "overview" : first)})", body));
            }
        }

        return result;
    }

    /// <summary>The guide's title and introduction, then a table of its sections with their URIs and sizes.</summary>
    public static string GuideIndex()
    {
        string guide = Guide();
        int start = guide.IndexOf("\n## 1. ", StringComparison.Ordinal);
        string intro = (start > 0 ? guide[..start] : string.Empty).TrimEnd('\n', '-', ' ').TrimEnd();
        intro = System.Text.RegularExpressions.Regex.Replace(intro, @"<!--.*?-->\n*", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var sb = new StringBuilder();
        sb.Append(intro).Append("\n\n");
        sb.Append("## Sections\n\n");
        sb.Append($"Read a section with `{GuideSectionPrefix}<section>`. Before your first edit read 1-3 (role, setup and tools, the core loop) and 9 (the checklist); ");
        sb.Append("read 5-6 before building, 8 before editing an existing level, 11 for a complete worked example. ");
        sb.Append($"`{GuideUri}` is the whole guide ({guide.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters) in one read.\n\n");
        sb.Append("| Section | Title | Characters |\n|---|---|---|\n");
        foreach (GuideSectionInfo s in GuideSections())
        {
            sb.Append($"| `{GuideSectionPrefix}{s.Id}` | {Cell(s.Title)} | {s.Text.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} |\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// One guide section by id (<c>3</c>, <c>6a</c>). A section that was split, asked for by its number, answers with
    /// the list of its parts. Throws <see cref="AutomationException"/> (<c>usage</c>) naming the valid ids otherwise.
    /// </summary>
    public static string GuideSection(string id)
    {
        IReadOnlyList<GuideSectionInfo> sections = GuideSections();
        string key = id.Trim().ToLowerInvariant();
        if (sections.FirstOrDefault(s => s.Id == key) is { } found)
        {
            return found.Text;
        }

        List<GuideSectionInfo> parts = sections.Where(s => s.Id.Length > key.Length && s.Id.StartsWith(key, StringComparison.Ordinal) && char.IsLetter(s.Id[key.Length])).ToList();
        if (parts.Count > 0 && key.All(char.IsDigit))
        {
            return $"Section {key} is in {parts.Count} parts; read each:\n" + string.Join("\n", parts.Select(p => $"- {GuideSectionPrefix}{p.Id}: {p.Title}")) + "\n";
        }

        throw new AutomationException(AutomationErrorCodes.Usage,
            $"Unknown guide section '{id}'. Sections: {string.Join(", ", sections.Select(s => s.Id))} (see {GuideIndexUri}).");
    }

    /// <summary>A text resource embedded in glacier-cli, line endings normalized to \n.</summary>
    internal static string Embedded(string name)
    {
        using Stream stream = typeof(McpResources).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"glacier-cli was built without its embedded '{name}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The generated Markdown list of resources and prompts for the agent guide (its <c>mcp-resources</c> block).</summary>
    public static string GuideBlock()
    {
        var sb = new StringBuilder();
        sb.Append("| Resource | What it holds |\n");
        sb.Append("|---|---|\n");
        foreach (McpResourceInfo r in List(levelSummary: true))
        {
            sb.Append($"| `{r.Uri}` | {Cell(r.Description)} |\n");
        }

        foreach (McpResourceTemplateInfo t in Templates)
        {
            sb.Append($"| `{t.UriTemplate}` | {Cell(t.Description)} |\n");
        }

        sb.Append('\n');
        sb.Append("| Prompt | Arguments | What it does |\n");
        sb.Append("|---|---|---|\n");
        foreach (McpPromptInfo p in McpPrompts.All)
        {
            string args = p.Args.Count == 0 ? "-" : string.Join(", ", p.Args.Select(a => a.Required ? $"`{a.Name}`" : $"`{a.Name}` (optional)"));
            sb.Append($"| `{p.Name}` | {args} | {Cell(p.Description)} |\n");
        }

        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>The marker opening the guide's generated resources/prompts block.</summary>
    public const string GuideBegin = "<!-- mcp-resources:begin -->";

    /// <summary>The marker closing the guide's generated resources/prompts block.</summary>
    public const string GuideEnd = "<!-- mcp-resources:end -->";
}
