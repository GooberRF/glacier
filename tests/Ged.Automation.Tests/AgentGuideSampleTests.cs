using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Every Lua sample in the agent guide (<c>docs/ai/AGENT-GUIDE.md</c>: a <c>```lua</c> block whose first
/// line is <c>-- sample: &lt;id&gt;</c>) runs exactly as an agent would run it — <c>glacier-cli run</c> on a
/// File ▸ New level, headless — and succeeds; a sample that builds geometry then passes the guide's own
/// checklist: zero holes on the sealed build, a clean sealed build, and no lint errors or warnings. When
/// an RF install is available (GED_RF_DIR / research/rf-dirs.txt) it is mounted, so class names are
/// validated against the game tables; otherwise the samples run on the unvalidated path. A failing sample
/// means the guide is wrong: fix the guide.
/// </summary>
public sealed class AgentGuideSampleTests
{
    private static readonly Regex LuaBlock = new(@"^```lua[ \t]*\r?\n(?<body>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SampleId = new(@"^-- sample: (?<id>[a-z0-9-]+)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static string GuidePath =>
        Path.Combine(TestLevels.RepoRoot ?? throw new InvalidOperationException("repo root (Glacier.sln) not found"), "docs", "ai", "AGENT-GUIDE.md");

    /// <summary>Every sample as (id, source), in guide order.</summary>
    private static IReadOnlyList<(string Id, string Source)> Samples()
    {
        var samples = new List<(string, string)>();
        foreach (Match m in LuaBlock.Matches(File.ReadAllText(GuidePath)))
        {
            string body = m.Groups["body"].Value;
            Match id = SampleId.Match(body);
            Assert.True(id.Success && id.Index == 0, "every ```lua block in the agent guide must start with '-- sample: <id>':\n" + body);
            samples.Add((id.Groups["id"].Value, body));
        }

        return samples;
    }

    public static IEnumerable<object[]> SampleIds() => Samples().Select(s => new object[] { s.Id });

    /// <summary>
    /// The Claude Code skill, the README, the user help and the packaging script agree on where the
    /// guide lives: <c>docs/ai</c> in the repository, staged as <c>ai\</c> beside the executables, with a
    /// copy of the guide inside the skill folder.
    /// </summary>
    [Fact]
    public void Skill_Readme_Help_And_Package_Agree_On_The_Guide_Location()
    {
        string root = TestLevels.RepoRoot!;
        string skill = File.ReadAllText(Path.Combine(root, "docs", "ai", "claude-skill", "SKILL.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith("---\nname: glacier-level-design\ndescription: ", skill, StringComparison.Ordinal);
        Assert.Contains("ai/AGENT-GUIDE.md", skill, StringComparison.Ordinal);
        Assert.Contains("docs/ai/AGENT-GUIDE.md", skill, StringComparison.Ordinal);

        Assert.Contains("claude-skill", File.ReadAllText(Path.Combine(root, "docs", "ai", "README.md")), StringComparison.Ordinal);

        string help = File.ReadAllText(Path.Combine(root, "docs", "help.html"));
        Assert.Contains(@"ai\claude-skill", help, StringComparison.Ordinal);
        Assert.Contains(@"ai\AGENT-GUIDE.md", help, StringComparison.Ordinal);
        Assert.Contains("<section id=\"ai\">", help, StringComparison.Ordinal);

        string package = File.ReadAllText(Path.Combine(root, "tools", "package.ps1"));
        Assert.Contains(@"'docs\ai'", package, StringComparison.Ordinal);
        Assert.Contains("'claude-skill'", package, StringComparison.Ordinal);
        Assert.DoesNotContain("changelog.md'", package, StringComparison.Ordinal); // the changelog is never packaged
    }

    [Fact]
    public void Guide_Has_Uniquely_Named_Samples()
    {
        IReadOnlyList<(string Id, string Source)> samples = Samples();
        Assert.True(samples.Count >= 8, $"expected the guide's samples, found {samples.Count}");
        Assert.Equal(samples.Count, samples.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(SampleIds))]
    public async Task Sample_Runs_And_Passes_The_Checklist(string id)
    {
        string source = Samples().Single(s => s.Id == id).Source;
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        string? rf = RfInstall();
        if (rf is not null)
        {
            host.Mount(rf);
        }

        await host.NewAsync(default);
        var commands = new AutomationCommands(host);

        AutomationResponse run = await commands.ExecuteAsync(new AutomationRequest("run", new JsonObject
        {
            ["script"] = source,
            ["name"] = id + ".lua",
            // The production instruction budget (a deterministic guard: a sample over it fails on every machine) with a
            // load-tolerant wall clock: on a loaded box the 30 s default measures the machine, not the sample.
            ["timeoutSeconds"] = 240,
        }));
        string log = run.Result?["log"] is JsonArray lines ? string.Join("\n", lines.Select(l => l!["message"]!.GetValue<string>())) : string.Empty;
        Assert.True(run.Ok, $"sample '{id}' failed: {run.Error?.Message}\n{log}");

        // A sample must not log warnings. Without an install, class names cannot be validated against the
        // game tables; that expected warning is the only one tolerated (and only then).
        string[] warnings = run.Result!["log"]!.AsArray()
            .Where(l => l!["level"]!.GetValue<string>() is "warning" or "error")
            .Select(l => l!["message"]!.GetValue<string>())
            .Where(m => rf is not null || !m.Contains("not validated", StringComparison.Ordinal))
            .ToArray();
        Assert.True(warnings.Length == 0, $"sample '{id}' logged warnings:\n{string.Join("\n", warnings)}");

        if (Ged.Core.Compiler.CompiledGeometryState.HasBrushes(host.Document!.Rfl))
        {
            AutomationResponse holes = await commands.ExecuteAsync(new AutomationRequest("holes"));
            Assert.True(holes.Ok, holes.Error?.Message);
            Assert.True(holes.Result!["watertight"]!.GetValue<bool>(),
                $"sample '{id}' leaks: {holes.Result["count"]} hole(s) at {holes.Result["positions"]?.ToJsonString()}");

            AutomationResponse build = await commands.ExecuteAsync(new AutomationRequest("build", new JsonObject { ["sealed"] = true }));
            Assert.True(build.Ok, $"sample '{id}' sealed build failed: {build.Error?.Message}");

            AutomationResponse lint = await commands.ExecuteAsync(new AutomationRequest("lint"));
            Assert.True(lint.Ok, lint.Error?.Message);
            JsonNode r = lint.Result!;
            string findings = string.Join("\n", r["findings"]!.AsArray()
                .Select(f => $"{f!["severity"]} {f["category"]}: {f["message"]} [uid {f["uid"]}]"));
            Assert.True(r["errors"]!.GetValue<int>() == 0 && r["warnings"]!.GetValue<int>() == 0,
                $"sample '{id}' is not lint-clean:\n{findings}");
        }
    }

    /// <summary>The RF install (GED_RF_DIR, then research/rf-dirs.txt), or null.</summary>
    private static string? RfInstall()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("GED_RF_DIR") is { Length: > 0 } env)
        {
            candidates.Add(env);
        }

        if (TestLevels.RepoRoot is { } root && File.Exists(Path.Combine(root, "research", "rf-dirs.txt")))
        {
            candidates.AddRange(File.ReadAllLines(Path.Combine(root, "research", "rf-dirs.txt"))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        return candidates.FirstOrDefault(c => Directory.Exists(c) && Directory.EnumerateFiles(c, "*.vpp").Any());
    }
}
