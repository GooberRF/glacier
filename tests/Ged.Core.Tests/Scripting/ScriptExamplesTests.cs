using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Smoke-runs every bundled example script against a fixture level so a shipped example that no
/// longer matches the API surface fails CI. Skips gracefully when the scripts folder is absent.
/// </summary>
public sealed class ScriptExamplesTests
{
    public static System.Collections.Generic.IEnumerable<object[]> ExampleFiles()
    {
        string? dir = ScriptTestFixture.ExamplesDir();
        if (dir is null)
        {
            yield return new object[] { string.Empty };
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(dir, "*.lua").OrderBy(p => p))
        {
            yield return new object[] { path };
        }
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Example_Runs_Without_Error(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return; // no examples folder in this checkout
        }

        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3, boxes: 3);
        doc.SelectAll(); // so selection-based examples have something to act on

        string source = File.ReadAllText(path);
        ScriptMetadata meta = ScriptMetadata.Parse(source);
        var log = new ScriptLog();

        ScriptRunResult result = ScriptTestFixture.Runner().Run(
            ScriptTestFixture.Services(doc),
            source,
            new ScriptRunOptions
            {
                ChunkName = Path.GetFileName(path),
                AllowDestructive = true,
                DeclaredApiVersion = meta.ApiVersion,
                Seed = 1,
            },
            log);

        Assert.True(result.Success,
            $"{Path.GetFileName(path)} failed: {result.Error?.ToDisplayString()}");
    }
}
