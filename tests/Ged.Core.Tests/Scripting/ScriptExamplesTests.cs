using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
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

        // A realistic File ▸ New level (level_info + level_properties, so level.props works) plus the
        // fixture's guard entities and 2 m solid boxes. The boxes are earlier in time than anything an
        // example adds, so a later air brush simply carves them.
        EditorDocument doc = NewLevelWithFixtureContent(entities: 3, boxes: 3);
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
                Limits = ScriptTestFixture.LoadTolerantLimits,
            },
            log);

        Assert.True(result.Success,
            $"{Path.GetFileName(path)} failed: {result.Error?.ToDisplayString()}");
    }

    /// <summary>
    /// The new-level template (<see cref="LevelTemplates.CreateNewRfl"/>, fixed date) with
    /// <paramref name="entities"/> guard entities and <paramref name="boxes"/> 2 m solid boxes added.
    /// </summary>
    internal static EditorDocument NewLevelWithFixtureContent(int entities, int boxes)
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(new System.DateTime(2026, 1, 1, 12, 0, 0, System.DateTimeKind.Local));
        var es = new EntitiesSection();
        for (int i = 0; i < entities; i++)
        {
            es.Entities.Add(new Entity { Uid = i + 1, ClassName = "Guard", ScriptName = $"e{i + 1}", Position = new Vec3(i, 0, 0) });
        }

        if (entities > 0)
        {
            int end = rfl.Sections.FindIndex(s => s.TypeId == (uint)SectionType.End);
            rfl.Sections.Insert(end < 0 ? rfl.Sections.Count : end, new RflSection((uint)SectionType.Entities, System.Array.Empty<byte>()) { Content = es, Dirty = true });
        }

        var doc = new EditorDocument(rfl, "examples.rfl");
        var ed = new BrushEditor(doc);
        for (int i = 0; i < boxes; i++)
        {
            ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Texture = "metal01.tga" },
                new Vec3(i * 4, 0, 0), Mat3.Identity);
        }

        doc.MarkSaved();
        return doc;
    }
}
