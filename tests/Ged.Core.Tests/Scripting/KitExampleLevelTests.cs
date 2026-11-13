using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The design kit's golden example: <c>scripts/examples/kit-tiny-dm.lua</c> runs
/// unmodified and headless on a File ▸ New level and builds a watertight three-room deathmatch level —
/// verified on sealed builds under both build methods, lint-clean against the new-level baseline,
/// one exact undo step, deterministic, and stable across save → reload.
/// </summary>
[Trait("Category", "DeepGate")]
public sealed class KitExampleLevelTests
{
    private static string ExampleSource()
    {
        string root = ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "scripts", "examples", "kit-tiny-dm.lua"));
    }

    private static ScriptRunResult RunExample(EditorDocument doc, bool sharedBsp = true, AssetVfs? vfs = null, ScriptLog? log = null)
    {
        string source = ExampleSource();
        return ScriptTestFixture.Runner().Run(
            new ScriptServices
            {
                Document = doc,
                Confirmation = new AllowAllConfirmation(),
                Assets = vfs,
                BuildSettings = new BuildSettings { UseSharedBsp = sharedBsp },
            },
            source,
            new ScriptRunOptions { ChunkName = "kit-tiny-dm.lua", AllowDestructive = true, DeclaredApiVersion = ScriptMetadata.Parse(source).ApiVersion, Limits = ScriptTestFixture.LoadTolerantLimits },
            log ?? new ScriptLog());
    }

    private static void AssertGoldenContent(EditorDocument doc)
    {
        Assert.Equal(34, new BrushEditor(doc).Brushes.Count); // 20 + a detail lamp fixture per light (no game tables loaded)
        List<LevelObject> objects = doc.Objects.ToList();
        Assert.Equal(14, objects.Count(o => o.Kind == LevelObjectKind.Light)); // rooms by floor area and headroom (about 2 per 100 m2), corridors every 4 m
        Assert.Equal(6, objects.Count(o => o.Kind == LevelObjectKind.MpRespawnPoint));
        Assert.Equal(3, objects.Count(o => o.Kind == LevelObjectKind.Item));
        Assert.Equal(new[] { "Assault Rifle", "Medical Kit", "Shotgun" },
            objects.Where(o => o.Kind == LevelObjectKind.Item).Select(o => o.ClassName).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        List<LevelObject> triggers = objects.Where(o => o.Kind == LevelObjectKind.Trigger).ToList();
        Assert.Equal(2, triggers.Count);
        LevelObject start = Assert.Single(objects, o => o.Kind == LevelObjectKind.PlayerStart);
        Assert.True(MathF.Abs(start.Position.X) < 1e-4f && MathF.Abs(start.Position.Y - 1f) < 1e-4f && MathF.Abs(start.Position.Z + 4f) < 1e-4f,
            $"Player Start at {start.Position}");

        var movers = new MoverService(doc);
        Group mover = Assert.Single(movers.Movers);
        Assert.Equal("annex_door", mover.Name);
        Assert.NotNull(mover.MovingData);
        Assert.Equal(2, mover.MovingData!.Keyframes.Count);
        int startKeyframe = mover.MovingData.Keyframes[mover.MovingData.StartingKeyframe].Uid;
        Assert.All(triggers, t => Assert.Equal(new[] { startKeyframe }, LinkModel.LinksOf(t)!.ToArray()));
    }

    [Fact]
    public void Golden_Example_Builds_The_Documented_Level()
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        HashSet<string> lintBefore = LintKeys(doc);
        int nodes = doc.Undo.NodeCount;

        ScriptRunResult r = RunExample(doc);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("brushes=34 movers=1", r.ReturnValue);
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        AssertGoldenContent(doc);

        AssertWatertight(doc, sharedBsp: true);
        AssertWatertight(doc, sharedBsp: false);
        var run = new KitRun(doc, before, lintBefore, r, new ScriptLog());
        AssertLintClean(run);
        AssertUndoRestores(run);
    }

    /// <summary>
    /// True when <paramref name="p"/> is open space under the time-ordered CSG fold: the latest brush
    /// (in time order) containing the point decides, the world outside every brush is solid.
    /// </summary>
    private static bool IsOpenAir(IReadOnlyList<Brush> brushes, Vec3 p)
    {
        bool open = false;
        foreach (Brush b in brushes)
        {
            List<CsgFace> faces = BrushWorld.ToOutwardWorldFaces(b);
            bool air = (b.Flags & (uint)BrushFlags.Air) != 0;
            if (faces.Count > 0 && BrushVolume.From(0, air, faces).Contains(p))
            {
                open = air;
            }
        }

        return open;
    }

    [Fact]
    public void Golden_Example_Lights_Are_All_In_Open_Air_And_Every_Room_Is_Lit()
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        Assert.True(RunExample(doc).Success);
        var editor = new BrushEditor(doc);
        IReadOnlyList<Brush> brushes = editor.Brushes;
        var movers = new MoverService(doc);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(doc);
        List<LevelObject> lights = doc.Objects.Where(o => o.Kind == LevelObjectKind.Light).ToList();
        Assert.NotEmpty(lights);

        foreach (LevelObject light in lights)
        {
            // Never inside any solid brush (mover brushes included: a light in a door slab is just as dark).
            foreach (Brush b in brushes.Where(b => (b.Flags & (uint)BrushFlags.Air) == 0))
            {
                List<CsgFace> faces = BrushWorld.ToOutwardWorldFaces(b);
                Assert.False(faces.Count > 0 && BrushVolume.From(0, false, faces).Contains(light.Position),
                    $"light {light.ScriptName} at {light.Position} is inside solid brush {b.Uid} ({(tags.TryGetValue(b.Uid, out var t) ? t.GetValueOrDefault("kit.name") : "?")})");
            }

            Assert.True(IsOpenAir(brushes.Where(b => movers.FindGroupForMember(b.Uid) is null).ToList(), light.Position),
                $"light {light.ScriptName} at {light.Position} is not in open air");
        }

        foreach (string room in new[] { "hall", "annex", "vault" })
        {
            Assert.Contains(lights, l => tags.TryGetValue(l.Uid, out var t) && t.GetValueOrDefault("kit.name") == room);
        }
    }

    [Fact]
    public void Golden_Example_Is_Watertight_In_Script_Under_Incremental()
    {
        // The example's own ops.holes() assertion, run with the Incremental build method selected.
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        ScriptRunResult r = RunExample(doc, sharedBsp: false);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("brushes=34 movers=1", r.ReturnValue);
    }

    [Fact]
    public void Golden_Example_Is_Deterministic()
    {
        EditorDocument a = LevelTemplates.CreateNew(FixedDate);
        EditorDocument b = LevelTemplates.CreateNew(FixedDate);
        Assert.True(RunExample(a).Success);
        Assert.True(RunExample(b).Success);
        Assert.Equal(a.SaveToBytes(updateTimestamp: false), b.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Golden_Example_Survives_Save_And_Reload()
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        Assert.True(RunExample(doc).Success);
        byte[] saved = doc.SaveToBytes(updateTimestamp: false);

        EditorDocument reloaded = EditorDocument.OpenBytes(saved, "kit-tiny-dm.rfl");
        AssertGoldenContent(reloaded);
        Assert.Equal(saved, reloaded.SaveToBytes(updateTimestamp: false));

        (ScriptRunResult holes, _) = ScriptTestFixture.Run(reloaded, "return ops.holes().count");
        Assert.True(holes.Success, holes.Error?.ToDisplayString());
        Assert.Equal("0", holes.ReturnValue);
        AssertWatertight(reloaded, sharedBsp: false);
    }

    [Fact]
    public void Golden_Example_With_An_Install_Validates_Item_Classes_And_Stays_Lint_Clean()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install (GED_RF_DIR / research/rf-dirs.txt): skip
        }

        using AssetVfs vfs = GameMount.Mount(install);
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        HashSet<string> lintBefore = LevelLinter.Lint(doc.Rfl, new LintOptions { Vfs = vfs }).Findings
            .Select(f => $"{f.Category}|{f.Uid}|{f.Message}").ToHashSet(StringComparer.Ordinal);
        var log = new ScriptLog();
        ScriptRunResult r = RunExample(doc, vfs: vfs, log: log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("not validated", StringComparison.Ordinal));

        string[] added = LevelLinter.Lint(doc.Rfl, new LintOptions { Vfs = vfs }).Findings
            .Where(f => f.Category != LintCategory.MissingGeometry) // build state, not content: the example is not built here
            .Select(f => $"{f.Category}|{f.Uid}|{f.Message}")
            .Where(k => !lintBefore.Contains(k)).ToArray();
        Assert.True(added.Length == 0, "lint findings with the install mounted:\n" + string.Join("\n", added));
    }
}
