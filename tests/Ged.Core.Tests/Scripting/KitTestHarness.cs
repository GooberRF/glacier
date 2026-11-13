using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Shared plumbing for the design-kit acceptance tests: run a kit script on a fresh
/// File ▸ New level, then measure the result the only valid way — a SEALED compile (never a preview
/// build) under a chosen build method — plus lint-cleanliness and exact undo.
/// </summary>
internal static class KitTestHarness
{
    public static readonly DateTime FixedDate = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    /// <summary>The outcome of one kit run: the document, its pre-run bytes and lint, and the run result.</summary>
    public sealed record KitRun(EditorDocument Doc, byte[] Before, HashSet<string> LintBefore, ScriptRunResult Result, ScriptLog Log);

    /// <summary>Runs <paramref name="body"/> (with <c>kit</c> already imported) on a new-level document.</summary>
    public static KitRun Run(string body, EditorDocument? doc = null)
    {
        doc ??= LevelTemplates.CreateNew(FixedDate);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        HashSet<string> lintBefore = LintKeys(doc);
        int nodes = doc.Undo.NodeCount;
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation() },
            "local kit = ged.import('kit')\n" + body,
            new ScriptRunOptions { ChunkName = "kit-test", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        return new KitRun(doc, before, lintBefore, r, log);
    }

    /// <summary>Runs <paramref name="body"/> expecting it to fail; returns the diagnostic and asserts nothing changed.</summary>
    public static ScriptDiagnostic Fail(string body)
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local kit = ged.import('kit')\n" + body);
        Assert.False(r.Success, "expected the kit call to fail: " + body);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        return r.Error!;
    }

    /// <summary>A sealed compile of the document under the chosen build method (not applied).</summary>
    public static CompiledLevel Sealed(EditorDocument doc, bool sharedBsp)
    {
        CompileOptions options = CompileOptionsFactory.CreateForDocument(
            doc, vfs: null, new BuildSettings { UseSharedBsp = sharedBsp }, new CompileRequest { Sealed = true });
        CompiledLevel c = GeometryBuildService.Build(doc.Rfl, options);
        Assert.Equal(sharedBsp, c.Report.SharedBspUsed);
        return c;
    }

    /// <summary>Asserts a sealed compile under <paramref name="sharedBsp"/> has zero holes; returns it.</summary>
    public static CompiledLevel AssertWatertight(EditorDocument doc, bool sharedBsp)
    {
        CompiledLevel c = Sealed(doc, sharedBsp);
        Assert.NotEmpty(c.Geometry.Faces);
        List<Vec3> holes = HoleDetector.Detect(c.Geometry);
        Assert.True(holes.Count == 0,
            $"{(sharedBsp ? "Shared BSP" : "Incremental")} sealed build has {holes.Count} hole(s), e.g. " +
            string.Join("; ", holes.Take(5).Select(h => $"({h.X:0.###}, {h.Y:0.###}, {h.Z:0.###})")));
        return c;
    }

    /// <summary>"Lint clean": the findings after the run are a subset of the findings before it.</summary>
    public static void AssertLintClean(KitRun run)
    {
        HashSet<string> after = LintKeys(run.Doc);
        string[] added = after.Where(k => !run.LintBefore.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0, "the kit introduced lint findings:\n" + string.Join("\n", added));
    }

    /// <summary>The run is one undo node, and undoing it restores the pre-run bytes exactly.</summary>
    public static void AssertUndoRestores(KitRun run)
    {
        run.Doc.Undo.Undo();
        Assert.Equal(run.Before, run.Doc.SaveToBytes(updateTimestamp: false));
    }

    /// <summary>Full acceptance: watertight under both build methods, lint clean, exact undo.</summary>
    public static (CompiledLevel Shared, CompiledLevel Incremental) AssertAccepted(KitRun run)
    {
        CompiledLevel shared = AssertWatertight(run.Doc, sharedBsp: true);
        CompiledLevel incremental = AssertWatertight(run.Doc, sharedBsp: false);
        AssertLintClean(run);
        AssertUndoRestores(run);
        return (shared, incremental);
    }

    /// <summary>The lint findings as keys. The missing-compiled-geometry error is left out: it reports the build
    /// state (kit runs are measured on separate sealed builds), not the content the kit made.</summary>
    public static HashSet<string> LintKeys(EditorDocument doc) =>
        LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings
            .Where(f => f.Category != LintCategory.MissingGeometry)
            .Select(f => $"{f.Category}|{f.Uid}|{f.Message}")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every brush tag set, keyed by brush UID.</summary>
    public static IReadOnlyDictionary<int, Dictionary<string, string>> Tags(EditorDocument doc) =>
        new Ged.Core.Editing.GedObjectMetadataService(doc).AllTags();
}
