using System;
using System.Linq;
using System.Text;

namespace Ged.Automation.Tools;

/// <summary>
/// Documentation generated from <see cref="ToolCatalog"/>: the agent guide's tool table (the block between
/// <see cref="Begin"/> and <see cref="End"/> in docs/ai/AGENT-GUIDE.md, regenerated under GED_REGEN_SCRIPT_DOCS=1
/// and checked by a drift test).
/// </summary>
public static class ToolDocs
{
    /// <summary>The marker opening the generated block.</summary>
    public const string Begin = "<!-- tools:begin -->";

    /// <summary>The marker closing the generated block.</summary>
    public const string End = "<!-- tools:end -->";

    /// <summary>The Markdown table of every tool: name, matching glacier-cli verb, safety class, summary.</summary>
    public static string GuideTable()
    {
        var sb = new StringBuilder();
        sb.Append("| MCP tool | glacier-cli | Changes the level? | What it does |\n");
        sb.Append("|---|---|---|---|\n");
        foreach (ToolSpec t in ToolCatalog.All)
        {
            string verb = t.CliVerb is null ? "-" : $"`{t.CliVerb}`";
            string safety = t.Safety switch
            {
                ToolSafety.Read => "no",
                ToolSafety.Mutate => "yes",
                _ => "yes (needs destructive actions allowed)",
            };
            sb.Append($"| `{t.Name}` | {verb} | {safety} | {t.Summary.Replace("|", "\\|", StringComparison.Ordinal)} |\n");
        }

        return sb.ToString();
    }

    /// <summary>Replaces the generated block in <paramref name="document"/> (which must contain both markers).</summary>
    public static string ReplaceBlock(string document, string generated) => ReplaceBlock(document, generated, Begin, End);

    /// <summary>Replaces the block between <paramref name="begin"/> and <paramref name="end"/> in <paramref name="document"/>.</summary>
    public static string ReplaceBlock(string document, string generated, string begin, string end)
    {
        int b = document.IndexOf(begin, StringComparison.Ordinal);
        int e = document.IndexOf(end, StringComparison.Ordinal);
        if (b < 0 || e < b)
        {
            throw new InvalidOperationException($"The document has no {begin} ... {end} block.");
        }

        string nl = document.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return document[..(b + begin.Length)] + nl + generated.Replace("\n", nl, StringComparison.Ordinal) + document[e..];
    }

    /// <summary>The generated block currently in <paramref name="document"/> (line endings normalized to \n).</summary>
    public static string CurrentBlock(string document) => CurrentBlock(document, Begin, End);

    /// <summary>The block between <paramref name="begin"/> and <paramref name="end"/> in <paramref name="document"/> (line endings normalized to \n).</summary>
    public static string CurrentBlock(string document, string begin, string end)
    {
        int b = document.IndexOf(begin, StringComparison.Ordinal);
        int e = document.IndexOf(end, StringComparison.Ordinal);
        if (b < 0 || e < b)
        {
            return string.Empty;
        }

        return document[(b + begin.Length)..e].Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\n');
    }
}
