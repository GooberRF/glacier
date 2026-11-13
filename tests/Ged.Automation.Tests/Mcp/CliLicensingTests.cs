using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>Every package glacier-cli ships (its resolved dependency list) has a notice in licensing-info.txt.</summary>
public sealed class CliLicensingTests
{
    [Fact]
    public void Every_Glacier_Cli_Package_Is_Listed_In_The_Licensing_Notices()
    {
        string deps = Path.Combine(AppContext.BaseDirectory, "glacier-cli.deps.json");
        Assert.True(File.Exists(deps), deps);
        string licensing = File.ReadAllText(Path.Combine(TestLevels.RepoRoot!, "licensing-info.txt"));
        JsonObject libraries = JsonNode.Parse(File.ReadAllText(deps))!["libraries"]!.AsObject();
        string[] packages = libraries
            .Where(kv => kv.Value?["type"]?.GetValue<string>() == "package")
            .Select(kv => kv.Key)
            .ToArray();
        Assert.Contains(packages, p => p.StartsWith("ModelContextProtocol.Core/", StringComparison.Ordinal));
        foreach (string p in packages)
        {
            string[] parts = p.Split('/');
            Assert.True(licensing.Contains(parts[0], StringComparison.OrdinalIgnoreCase), $"{p} has no entry in licensing-info.txt");
            if (parts[0].StartsWith("ModelContextProtocol", StringComparison.Ordinal) || parts[1].StartsWith("10.", StringComparison.Ordinal))
            {
                Assert.True(licensing.Contains(parts[1], StringComparison.Ordinal), $"{p}: the listed version is not {parts[1]}");
            }
        }

        Assert.Contains("Apache License", licensing, StringComparison.Ordinal);
    }
}
