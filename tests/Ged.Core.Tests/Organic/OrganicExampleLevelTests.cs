using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// The organic kit's shipped examples (<c>scripts/examples/kit-canyon.lua</c>, <c>kit-cave.lua</c>) run unmodified
/// and headless on a File ▸ New level and pass the organic corpus gates: watertight sealed builds under both build
/// methods, no duplicate horizontal coverage on either build, lint clean against the new-level baseline, every
/// light in open air, one exact undo step, and byte-identical reruns. (<see cref="ScriptExamplesTests"/> also runs
/// them, with their own hole assertion, on its fixture level.)
/// </summary>
public sealed class OrganicExampleLevelTests
{
    public static IEnumerable<object[]> Examples() => new[] { new object[] { "kit-canyon" }, new object[] { "kit-cave" } };

    private static KitRun RunExample(string name)
    {
        string root = ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        string source = File.ReadAllText(Path.Combine(root, "scripts", "examples", name + ".lua"));
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        HashSet<string> lintBefore = LintKeys(doc);
        int nodes = doc.Undo.NodeCount;
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), BuildSettings = new BuildSettings { UseSharedBsp = true } },
            source,
            new ScriptRunOptions { ChunkName = name + ".lua", AllowDestructive = true, DeclaredApiVersion = ScriptMetadata.Parse(source).ApiVersion, Limits = ScriptTestFixture.LoadTolerantLimits },
            log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        return new KitRun(doc, before, lintBefore, r, log);
    }

    /// <summary>True when <paramref name="p"/> is open space under the time-ordered CSG fold (the world outside every brush is solid).</summary>
    private static bool IsOpenAir(IReadOnlyList<BrushVolume> volumes, Vec3 p)
    {
        bool open = false;
        foreach (BrushVolume v in volumes)
        {
            if (v.Contains(p))
            {
                open = v.IsAir;
            }
        }

        return open;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_Passes_The_Organic_Gates(string name)
    {
        KitRun run = RunExample(name);

        CompiledLevel shared = AssertWatertight(run.Doc, sharedBsp: true);
        CompiledLevel incremental = AssertWatertight(run.Doc, sharedBsp: false);
        foreach ((string method, CompiledLevel c) in new[] { ("Shared BSP", shared), ("Incremental", incremental) })
        {
            List<(float X, float Z, float Y)> dup = FloorCoverage.Duplicates(c.Geometry);
            Assert.True(dup.Count == 0, $"{name} ({method}): duplicate horizontal coverage at {string.Join("; ", dup.Take(4).Select(d => $"({d.X}, {d.Y}, {d.Z})"))}");
        }

        AssertLintClean(run);

        List<BrushVolume> volumes = new BrushEditor(run.Doc).Brushes
            .Select((b, i) => BrushVolume.From(i, (b.Flags & (uint)BrushFlags.Air) != 0, BrushWorld.ToOutwardWorldFaces(b)))
            .ToList();
        List<LevelObject> lights = run.Doc.Objects.Where(o => o.Kind == LevelObjectKind.Light).ToList();
        Assert.NotEmpty(lights);
        foreach (LevelObject o in lights.Concat(run.Doc.Objects.Where(o => o.Kind is LevelObjectKind.MpRespawnPoint or LevelObjectKind.PlayerStart or LevelObjectKind.Item)))
        {
            Assert.True(IsOpenAir(volumes, o.Position), $"{name}: {o.Kind} {o.ScriptName} at {o.Position} is not in open air");
        }

        byte[] bytes = run.Doc.SaveToBytes(updateTimestamp: false);
        Assert.True(bytes.AsSpan().SequenceEqual(RunExample(name).Doc.SaveToBytes(updateTimestamp: false)), $"{name}: two runs differ");

        AssertUndoRestores(run);
    }
}
