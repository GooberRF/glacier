using System;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The shared per-member golden check: a mutating script (a) succeeds and its
/// verification holds; (b) adds exactly one undo node; (c) undoing that node restores the saved
/// bytes exactly; (d) a dry run leaves the bytes unchanged; (e) a run that throws after the
/// mutation leaves the bytes unchanged.
/// </summary>
internal static class ScriptMutationAssert
{
    public static ScriptRunResult AssertMutation(Func<EditorDocument> make, string script, Action<EditorDocument, ScriptRunResult>? verify = null, Func<EditorDocument, ScriptServices>? services = null)
    {
        // (a) + (b) + (c)
        EditorDocument doc = make();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, script, services: services);
        Assert.True(r.Success, $"{r.Error?.ToDisplayString()}\nscript: {script}");
        verify?.Invoke(doc, r);
        Assert.Equal(1, doc.Undo.NodeCount - nodes);
        Assert.Equal(1, r.UndoNodesAdded);
        doc.Undo.Undo();
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), $"undo did not restore the bytes exactly: {script}");

        // (d) dry run
        EditorDocument dry = make();
        byte[] dryBefore = dry.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult dr, _) = ScriptTestFixture.Run(dry, script, dryRun: true, services: services);
        Assert.True(dr.Success, dr.Error?.ToDisplayString());
        Assert.True(dryBefore.AsSpan().SequenceEqual(dry.SaveToBytes(updateTimestamp: false)), $"dry run changed the bytes: {script}");

        // (e) a throw after the mutation rolls everything back
        EditorDocument thrown = make();
        byte[] thrownBefore = thrown.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult tr, _) = ScriptTestFixture.Run(thrown, $"local _probe = (function()\n{script}\nend)()\nerror('rollback probe')", services: services);
        Assert.False(tr.Success);
        Assert.Contains("rollback probe", tr.Error!.Message, StringComparison.Ordinal);
        Assert.True(thrownBefore.AsSpan().SequenceEqual(thrown.SaveToBytes(updateTimestamp: false)), $"a failed run changed the bytes: {script}");
        _ = log;
        return r;
    }
}
