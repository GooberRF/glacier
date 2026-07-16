using System;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Scripting;

namespace Ged.Core.Tests.Scripting;

/// <summary>Shared helpers for building a scriptable document + runner in tests.</summary>
internal static class ScriptTestFixture
{
    /// <summary>A document with <paramref name="entities"/> guard entities and <paramref name="boxes"/> box brushes.</summary>
    public static EditorDocument NewDoc(int entities = 3, int boxes = 0, string levelName = "test.rfl")
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = levelName;

        if (entities > 0)
        {
            var es = new EntitiesSection();
            for (int i = 0; i < entities; i++)
            {
                es.Entities.Add(new Entity
                {
                    Uid = i + 1,
                    ClassName = "Guard",
                    ScriptName = $"e{i + 1}",
                    Position = new Vec3(i, 0, 0),
                });
            }

            rfl.Sections.Add(new RflSection((uint)SectionType.Entities, Array.Empty<byte>()) { Content = es });
        }

        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        var doc = new EditorDocument(rfl, levelName);

        if (boxes > 0)
        {
            var ed = new BrushEditor(doc);
            for (int i = 0; i < boxes; i++)
            {
                ed.CreateBrush(
                    new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Texture = "metal01.tga" },
                    new Vec3(i * 4, 0, 0),
                    Mat3.Identity);
            }

            // Reset the clean baseline so tests measure only their own undo steps.
            doc.MarkSaved();
        }

        return doc;
    }

    public static ScriptServices Services(EditorDocument doc, BrushEditor? brushes = null, IScriptOperations? ops = null) => new()
    {
        Document = doc,
        Brushes = brushes,
        Operations = ops,
        Confirmation = new AllowAllConfirmation(),
    };

    /// <summary>A runner backed by the real MoonSharp host.</summary>
    public static ScriptRunner Runner() => new(new MoonSharpHost());

    /// <summary>The repo root (GED_REPO_ROOT override, else walk up to Glacier.sln), or null.</summary>
    public static string? RepoRoot()
    {
        string? env = Environment.GetEnvironmentVariable("GED_REPO_ROOT");
        if (!string.IsNullOrEmpty(env) && System.IO.File.Exists(System.IO.Path.Combine(env, "Glacier.sln")))
        {
            return env;
        }

        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Glacier.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>The bundled examples directory, or null when the checkout lacks it.</summary>
    public static string? ExamplesDir()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            return null;
        }

        string dir = System.IO.Path.Combine(root, "scripts", "examples");
        return System.IO.Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Runs <paramref name="source"/> against a fresh context and returns the result + log.</summary>
    public static (ScriptRunResult Result, ScriptLog Log) Run(
        EditorDocument doc,
        string source,
        bool dryRun = false,
        int seed = 0,
        BrushEditor? brushes = null,
        IScriptOperations? ops = null)
    {
        var log = new ScriptLog();
        ScriptRunResult result = Runner().Run(
            Services(doc, brushes, ops),
            source,
            new ScriptRunOptions { ChunkName = "test", DryRun = dryRun, AllowDestructive = true, Seed = seed },
            log);
        return (result, log);
    }
}
