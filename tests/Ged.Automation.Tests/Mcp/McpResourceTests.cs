using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Automation.Tools;
using Ged.Cli.Mcp;
using Ged.Core.Assets;
using Ged.Core.IO.Vpp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// The MCP server's resources and prompts (MCP-SERVER-SPEC §8, §9 "Resources / prompts", "Guide drift"), and the
/// install mount the server's renders share (each render still opens a fresh private copy of the level).
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class McpResourceTests
{
    private static string RepoFile(params string[] parts) => Path.Combine(new[] { TestLevels.RepoRoot! }.Concat(parts).ToArray());

    // ---- handshake / listings -------------------------------------------------------------------------

    [Fact]
    public async Task Handshake_Advertises_Tools_Resources_And_Prompts_And_Points_At_The_Guide()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        Assert.NotNull(s.Client.ServerCapabilities.Tools);
        Assert.NotNull(s.Client.ServerCapabilities.Resources);
        Assert.NotNull(s.Client.ServerCapabilities.Prompts);
        string instructions = s.Client.ServerInstructions ?? string.Empty;
        Assert.InRange(instructions.Length, 200, 1200);
        Assert.Contains("glacier://guide", instructions, StringComparison.Ordinal);
        Assert.Contains("headless", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resource_And_Prompt_Listings_Match_The_Snapshot()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        IList<McpClientResource> resources = await s.Client.ListResourcesAsync();
        IList<McpClientResourceTemplate> templates = await s.Client.ListResourceTemplatesAsync();
        IList<McpClientPrompt> prompts = await s.Client.ListPromptsAsync();

        Assert.Equal(McpResources.List(levelSummary: true).Select(r => r.Uri), resources.Select(r => r.Uri));
        // The norms resources are listed exactly when this build carries the norms (it does: they are embedded).
        Assert.True(Ged.Core.Analysis.CorpusNorms.Available);
        Assert.Contains(resources, r => r.Uri == McpResources.NormsUri);
        Assert.Contains(templates, t => t.UriTemplate == McpResources.NormsMetricTemplate);
        Assert.Equal(new[] { "audit_level", "blockout_from_brief", "extend_level", "fix_leaks" }, prompts.Select(p => p.Name).Order(StringComparer.Ordinal));

        var snapshot = new JsonObject
        {
            ["resources"] = new JsonArray(resources.Select(r => (JsonNode?)JsonSerializer.SerializeToNode(r.ProtocolResource, McpJsonUtilities.DefaultOptions)).ToArray()),
            ["resourceTemplates"] = new JsonArray(templates.Select(t => (JsonNode?)JsonSerializer.SerializeToNode(t.ProtocolResourceTemplate, McpJsonUtilities.DefaultOptions)).ToArray()),
            ["prompts"] = new JsonArray(prompts.Select(p => (JsonNode?)JsonSerializer.SerializeToNode(p.ProtocolPrompt, McpJsonUtilities.DefaultOptions)).ToArray()),
        };
        string current = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        string path = RepoFile("tests", "Ged.Automation.Tests", "Mcp", "mcp-resources.snapshot.json");
        if (Environment.GetEnvironmentVariable("GED_REGEN_SCRIPT_DOCS") == "1")
        {
            File.WriteAllText(path, current);
        }

        Assert.True(File.Exists(path), "mcp-resources.snapshot.json is missing. Regenerate with GED_REGEN_SCRIPT_DOCS=1.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), current);

        foreach (McpResourceInfo r in McpResources.List(true))
        {
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\b(phase|coming soon|will be added|later|legacy|not yet|TODO)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), r.Description);
        }
    }

    [Fact]
    public async Task The_Level_Summary_Resource_Follows_The_Level_Summary_Tool()
    {
        await using McpTestSession readOnly = await McpTestSession.StartAsync(new McpLaunchOptions { ReadOnly = true });
        Assert.Contains(await readOnly.Client.ListResourcesAsync(), r => r.Uri == McpResources.LevelSummaryUri);

        await using McpTestSession denied = await McpTestSession.StartAsync(new McpLaunchOptions { DenyTools = new HashSet<string> { "level_summary" } });
        Assert.DoesNotContain(await denied.Client.ListResourcesAsync(), r => r.Uri == McpResources.LevelSummaryUri);
        await Assert.ThrowsAnyAsync<McpException>(() => denied.Client.ReadResourceAsync(McpResources.LevelSummaryUri).AsTask());
    }

    // ---- reads ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_Resource_Reads_And_Reading_Changes_Nothing()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();

        // Before a level is open the summary says so (and how to open one) instead of failing.
        JsonObject empty = JsonNode.Parse(await ReadText(s, McpResources.LevelSummaryUri, "application/json"))!.AsObject();
        Assert.Null(empty["document"]);
        Assert.Contains("open_level", empty["next"]!.GetValue<string>(), StringComparison.Ordinal);

        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        await s.OkAsync("run_lua", new { code = "level.brush{ shape = \"box\", pos = {0, 2, 0}, size = {4, 4, 4} }" });
        int depth = s.Document!.Undo.Position;
        bool dirty = s.Document.IsDirty;

        foreach (McpResourceInfo r in McpResources.List(levelSummary: true))
        {
            string text = await ReadText(s, r.Uri, r.MimeType);
            Assert.True(text.Length > 20, $"{r.Uri} is nearly empty");
        }

        Assert.StartsWith("# Glacier agent guide", await ReadText(s, McpResources.GuideUri, "text/markdown"), StringComparison.Ordinal);
        Assert.Contains("left-handed", await ReadText(s, McpResources.ConventionsUri, "text/plain"), StringComparison.Ordinal);
        Assert.Contains("kit.room", await ReadText(s, McpResources.ApiIndexUri, "text/plain"), StringComparison.Ordinal);
        Assert.Contains("function kit.room", await ReadText(s, McpResources.KitSourceUri, "text/x-lua"), StringComparison.Ordinal);
        Assert.Contains("room", await ReadText(s, McpResources.KitUri, "text/plain"), StringComparison.Ordinal);
        JsonObject summary = JsonNode.Parse(await ReadText(s, McpResources.LevelSummaryUri, "application/json"))!.AsObject();
        Assert.NotNull(summary["brushes"]);

        Assert.Equal(depth, s.Document.Undo.Position);
        Assert.Equal(dirty, s.Document.IsDirty);
    }

    [Fact]
    public async Task The_Api_Template_Resolves_Members_And_Names_The_Nearest_For_Unknown_Ones()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        Assert.Contains(await s.Client.ListResourceTemplatesAsync(), t => t.UriTemplate == McpResources.ApiMemberTemplate);

        string brush = await ReadText(s, "glacier://api/lua/level.brush", "text/plain");
        Assert.Contains("level.brush", brush, StringComparison.Ordinal);
        Assert.Contains("kit.room", await ReadText(s, "glacier://api/lua/kit.room", "text/plain"), StringComparison.Ordinal);
        Assert.Equal(ApiReferenceText.Member("brush:set_pos").Text, await ReadText(s, "glacier://api/lua/brush:set_pos", "text/plain"));

        McpException unknown = await Assert.ThrowsAnyAsync<McpException>(() => s.Client.ReadResourceAsync("glacier://api/lua/level.brsh").AsTask());
        Assert.Contains("brush", unknown.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<McpException>(() => s.Client.ReadResourceAsync("glacier://nothing-here").AsTask());
    }

    [Fact]
    public async Task The_Guide_Reads_By_Section_Each_Small_Enough_For_One_Read()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        Assert.Contains(await s.Client.ListResourceTemplatesAsync(), t => t.UriTemplate == McpResources.GuideSectionTemplate);
        Assert.Contains("glacier://guide/index", s.Client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);

        string index = await ReadText(s, McpResources.GuideIndexUri, "text/markdown");
        Assert.StartsWith("# Glacier agent guide", index, StringComparison.Ordinal);
        Assert.True(index.Length < McpResources.MaxSectionChars);
        IReadOnlyList<McpResources.GuideSectionInfo> sections = McpResources.GuideSections();
        Assert.Equal(Enumerable.Range(1, 12).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            sections.Select(x => new string(x.Id.TakeWhile(char.IsDigit).ToArray())).Distinct());
        foreach (McpResources.GuideSectionInfo section in sections)
        {
            string uri = McpResources.GuideSectionPrefix + section.Id;
            Assert.Contains($"`{uri}`", index, StringComparison.Ordinal);
            string text = await ReadText(s, uri, "text/markdown");
            Assert.Equal(section.Text, text);
            Assert.True(text.Length <= McpResources.MaxSectionChars, $"{uri} is {text.Length} characters");
            Assert.StartsWith($"## {new string(section.Id.TakeWhile(char.IsDigit).ToArray())}. ", text, StringComparison.Ordinal);
        }

        // Together the sections hold every line of the guide from section 1 on.
        string guide = McpResources.Guide();
        string all = string.Join("\n", sections.Select(x => x.Text));
        foreach (string line in guide[guide.IndexOf("\n## 1. ", StringComparison.Ordinal)..].Split('\n').Where(l => l.Trim().Length > 0))
        {
            Assert.Contains(line, all, StringComparison.Ordinal);
        }

        // A split section asked for by its number lists its parts; an unknown one is a protocol error naming the sections.
        if (sections.FirstOrDefault(x => x.Id.EndsWith('a')) is { } split)
        {
            string number = split.Id[..^1];
            string parts = await ReadText(s, McpResources.GuideSectionPrefix + number, "text/markdown");
            Assert.Contains(McpResources.GuideSectionPrefix + number + "b", parts, StringComparison.Ordinal);
        }

        McpException unknown = await Assert.ThrowsAnyAsync<McpException>(() => s.Client.ReadResourceAsync("glacier://guide/99").AsTask());
        Assert.Contains("guide/index", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Long_Guide_Section_Splits_At_Its_Subheadings_Into_Parts_Under_The_Limit()
    {
        string filler = string.Concat(Enumerable.Repeat("Some guide text that fills the section up.\n", 160)); // ~7 KB
        string guide = "# Title\n\nIntro.\n\n## 1. Short\n\nOne line.\n\n## 2. Long\n\nLead-in.\n\n" +
                       $"### First\n\n{filler}\n### Second\n\n```lua\n### not a heading inside code\n```\n{filler}\n### Third\n\n{filler}\n### Fourth\n\n{filler}\n";
        IReadOnlyList<McpResources.GuideSectionInfo> sections = McpResources.SplitGuide(guide);
        Assert.Equal("1", sections[0].Id);
        Assert.All(sections.Skip(1), x => Assert.StartsWith("2", x.Id, StringComparison.Ordinal));
        Assert.Equal(new[] { "2a", "2b" }, sections.Skip(1).Select(x => x.Id));
        Assert.All(sections, x => Assert.True(x.Text.Length <= McpResources.MaxSectionChars));
        Assert.StartsWith("## 2. Long\n", sections[1].Text, StringComparison.Ordinal);
        Assert.StartsWith("## 2. Long (part 2 of 2)\n\n### ", sections[2].Text, StringComparison.Ordinal);
        Assert.Contains("### not a heading inside code", string.Concat(sections.Select(x => x.Text)), StringComparison.Ordinal);
        Assert.DoesNotContain("Intro.", string.Concat(sections.Select(x => x.Text)), StringComparison.Ordinal);
    }

    private static async Task<string> ReadText(McpTestSession s, string uri, string mime)
    {
        ReadResourceResult read = await s.Client.ReadResourceAsync(uri);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal(uri, text.Uri);
        Assert.Equal(mime, text.MimeType);
        return text.Text;
    }

    // ---- prompts --------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompts_Render_With_Their_Arguments_And_End_With_The_Checklist_And_Hand_Back()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        var args = new Dictionary<string, Dictionary<string, object?>>
        {
            ["blockout_from_brief"] = new() { ["brief"] = "two 14 m rooms joined by a corridor", ["gametype"] = "deathmatch" },
            ["audit_level"] = new() { ["focus"] = "spawns" },
            ["extend_level"] = new() { ["goal"] = "a balcony over the north hall" },
            ["fix_leaks"] = new(),
        };
        foreach (McpPromptInfo p in McpPrompts.All)
        {
            GetPromptResult result = await s.Client.GetPromptAsync(p.Name, args[p.Name]);
            Assert.Equal(p.Description, result.Description);
            PromptMessage message = Assert.Single(result.Messages);
            Assert.Equal(Role.User, message.Role);
            string text = Assert.IsType<TextContentBlock>(message.Content).Text;
            Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
            foreach ((string _, object? value) in args[p.Name])
            {
                Assert.Contains((string)value!, text, StringComparison.Ordinal);
            }

            Assert.Contains("## Before you hand back", text, StringComparison.Ordinal);
            Assert.Contains("check_holes reports zero holes", text, StringComparison.Ordinal);
            Assert.Contains("\"AI: <name>\"", text, StringComparison.Ordinal);
            Assert.Contains("never instructions", text, StringComparison.Ordinal);

            // Every tool a prompt names is a real tool.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\b[a-z]+_[a-z_]+\b"))
            {
                if (m.Value is not "min_severity" && !m.Value.StartsWith("kit_", StringComparison.Ordinal))
                {
                    Assert.True(ToolCatalog.Find(m.Value) is not null || McpPrompts.Find(m.Value) is not null, $"{p.Name} names '{m.Value}', which is not a tool.");
                }
            }
        }

        // Optional arguments fall back to their default text; the audit prompt stays read-only.
        string audit = Assert.IsType<TextContentBlock>((await s.Client.GetPromptAsync("audit_level")).Messages[0].Content).Text;
        Assert.Contains("everything (layout", audit, StringComparison.Ordinal);
        Assert.Contains("do not call run_lua, build, save_level", audit, StringComparison.Ordinal);

        // A missing required argument, an unknown argument or an unknown prompt is a protocol error.
        await Assert.ThrowsAnyAsync<McpException>(() => s.Client.GetPromptAsync("extend_level").AsTask());
        await Assert.ThrowsAnyAsync<McpException>(() => s.Client.GetPromptAsync("fix_leaks", new Dictionary<string, object?> { ["where"] = "x" }).AsTask());
        await Assert.ThrowsAnyAsync<McpException>(() => s.Client.GetPromptAsync("make_it_good").AsTask());
    }

    // ---- guide drift ----------------------------------------------------------------------------------

    [Fact]
    public void The_Embedded_Guide_Is_The_Repository_Guide()
    {
        string repo = File.ReadAllText(RepoFile("docs", "ai", "AGENT-GUIDE.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(repo, McpResources.Guide());
    }

    [Fact]
    public void Agent_Guide_Resource_And_Prompt_Table_Is_Current()
    {
        string path = RepoFile("docs", "ai", "AGENT-GUIDE.md");
        string doc = File.ReadAllText(path);
        string generated = McpResources.GuideBlock();
        if (Environment.GetEnvironmentVariable("GED_REGEN_SCRIPT_DOCS") == "1")
        {
            File.WriteAllText(path, ToolDocs.ReplaceBlock(doc, generated, McpResources.GuideBegin, McpResources.GuideEnd));
            doc = File.ReadAllText(path);
        }

        Assert.Equal(generated, ToolDocs.CurrentBlock(doc, McpResources.GuideBegin, McpResources.GuideEnd));
    }

    // ---- the renders' shared install mount ------------------------------------------------------------

    /// <summary>A minimal install folder: one packfile holding <paramref name="name"/>.</summary>
    private static string FakeInstall(TempDir dir, string name = "probe.txt", string content = "one")
    {
        string rf = Path.Combine(dir.Path, "rf");
        Directory.CreateDirectory(Path.Combine(rf, "user_maps"));
        new VppBuilder().Add(name, System.Text.Encoding.ASCII.GetBytes(content)).Write(Path.Combine(rf, "levels1.vpp"));
        return rf;
    }

    [Fact]
    public void The_Mount_Is_Reused_Until_Its_Install_Changes()
    {
        using var dir = new TempDir();
        string rf = FakeInstall(dir);
        using var cache = new RenderMountCache();

        using (RenderMountLease first = cache.Acquire(rf))
        {
            Assert.False(first.Reused);
            Assert.Equal("one", System.Text.Encoding.ASCII.GetString(first.Assets.ReadFile("probe.txt")!));
        }

        AssetVfs firstVfs;
        using (RenderMountLease again = cache.Acquire(rf))
        {
            Assert.True(again.Reused);
            firstVfs = again.Assets;
            Assert.Equal("one", System.Text.Encoding.ASCII.GetString(again.Assets.ReadFile("probe.txt")!));
        }

        Assert.Equal(1, cache.MountCount);

        // An added packfile is a new key: the next lease mounts afresh and sees its files (levels0.vpp sorts first,
        // so its probe.txt wins). A mounted packfile stays open, so it cannot be rewritten in place on Windows.
        new VppBuilder().Add("probe.txt", System.Text.Encoding.ASCII.GetBytes("two, longer")).Write(Path.Combine(rf, "levels0.vpp"));
        using (RenderMountLease changed = cache.Acquire(rf))
        {
            Assert.False(changed.Reused);
            Assert.NotSame(firstVfs, changed.Assets);
            Assert.Equal("two, longer", System.Text.Encoding.ASCII.GetString(changed.Assets.ReadFile("probe.txt")!));
        }

        Assert.Equal(2, cache.MountCount);
    }

    [Fact]
    public void The_Mount_Key_Tracks_Packfiles_And_Loose_Folders()
    {
        using var dir = new TempDir();
        string rf = FakeInstall(dir);
        string key = RenderMountCache.KeyFor(rf);
        Assert.Equal(key, RenderMountCache.KeyFor(rf + Path.DirectorySeparatorChar));

        // A rewritten packfile (size and time).
        string vpp = Path.Combine(rf, "levels1.vpp");
        new VppBuilder().Add("probe.txt", System.Text.Encoding.ASCII.GetBytes("two, longer")).Write(vpp);
        File.SetLastWriteTimeUtc(vpp, DateTime.UtcNow.AddMinutes(1));
        string rewritten = RenderMountCache.KeyFor(rf);
        Assert.NotEqual(key, rewritten);

        // A new custom-texture folder (user_maps changes, and the folder is listed).
        Directory.CreateDirectory(Path.Combine(rf, "user_maps", "textures", "mine"));
        Directory.SetLastWriteTimeUtc(Path.Combine(rf, "user_maps"), DateTime.UtcNow.AddMinutes(2));
        Assert.NotEqual(rewritten, RenderMountCache.KeyFor(rf));

        // Another install is another key.
        using var other = new TempDir();
        Assert.NotEqual(RenderMountCache.KeyFor(rf), RenderMountCache.KeyFor(FakeInstall(other)));
    }

    [Fact]
    public async Task A_Mount_Is_Used_By_One_Render_At_A_Time()
    {
        using var dir = new TempDir();
        string rf = FakeInstall(dir);
        using var cache = new RenderMountCache();
        RenderMountLease first = cache.Acquire(rf);
        Task<RenderMountLease> second = Task.Run(() => cache.Acquire(rf));
        await Task.Delay(200);
        Assert.False(second.IsCompleted);
        first.Dispose();
        first.Dispose(); // releasing twice is harmless
        using RenderMountLease got = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(got.Reused);
    }

    [Fact]
    public async Task Server_Renders_Share_One_Mount_And_Remount_When_The_Install_Changes()
    {
        if (!McpServerTests.HasGpu())
        {
            return; // no GPU device on this machine
        }

        using var dir = new TempDir();
        string rf = FakeInstall(dir);
        await using McpTestSession s = await McpTestSession.StartAsync(new McpLaunchOptions { RfDir = rf });
        Assert.NotNull(s.Service.RenderMounts);
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        await s.OkAsync("render_plan", new { size = "128x128" });
        await s.OkAsync("render_view", new { view = "top", size = "128x128" });
        Assert.Equal(1, s.Service.RenderMounts!.MountCount);

        // Renders draw a fresh private copy each time: the session's level and history stay untouched.
        Assert.False(s.Document!.IsDirty);

        // A different level renders from its own copy, not the previous render's document.
        await s.OkAsync("new_level");
        JsonObject fresh = await s.OkAsync("render_plan", new { size = "128x128" });
        Assert.Equal("none", fresh["geometry"]!["before"]!.GetValue<string>());
        Assert.Equal(1, s.Service.RenderMounts.MountCount);

        new VppBuilder().Add("extra.txt", new byte[] { 1 }).Write(Path.Combine(rf, "levels2.vpp"));
        await s.OkAsync("render_plan", new { size = "128x128" });
        Assert.Equal(2, s.Service.RenderMounts.MountCount);
    }
}
