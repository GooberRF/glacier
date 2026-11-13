using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// What a run leaves behind besides the level: a rolled-back run (an error or a dry run) restores the
/// session state that lives outside undo (hidden brushes, selections, the UID allocator), handles to
/// deleted objects refuse to act, and renaming a flag-encoded trigger keeps its multiplayer flags.
/// </summary>
public sealed class ScriptRunStateTests
{
    private static EditorDocument NewLevel() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    [Fact]
    public void A_Dry_Run_Or_A_Failed_Run_Leaves_Hidden_Brushes_And_Selections_As_They_Were()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 2, boxes: 3);
        var brushes = new BrushEditor(doc);
        int first = brushes.Brushes[0].Uid;
        brushes.SelectBrush(first);
        doc.SelectMany(new[] { doc.Objects[0] });
        int[] objectSelection = doc.Selection.Select(o => o.Uid).ToArray();

        const string Lua =
            "for _, b in ipairs(level.brushes) do b:set_hidden(true) end\n" +
            "selection.set_brushes(level.brushes) selection.select_all()\n";
        foreach (bool dryRun in new[] { true, false })
        {
            var log = new ScriptLog();
            ScriptRunResult r = ScriptTestFixture.Runner().Run(
                ScriptTestFixture.Services(doc, brushes), Lua + (dryRun ? string.Empty : "error('stop')"),
                new ScriptRunOptions { ChunkName = "t", DryRun = dryRun, AllowDestructive = true }, log);
            Assert.Equal(dryRun, r.Success);
            Assert.Empty(brushes.HiddenBrushes);
            Assert.Equal(new[] { first }, brushes.SelectedBrushes.ToArray());
            Assert.Equal(objectSelection, doc.Selection.Select(o => o.Uid).ToArray());
        }

        // A committed run keeps what it did.
        ScriptRunResult real = ScriptTestFixture.Runner().Run(ScriptTestFixture.Services(doc, brushes), Lua,
            new ScriptRunOptions { ChunkName = "t", AllowDestructive = true }, new ScriptLog());
        Assert.True(real.Success, real.Error?.ToDisplayString());
        Assert.Equal(3, brushes.HiddenBrushes.Count);
    }

    [Fact]
    public void A_Rolled_Back_Run_Returns_The_Uids_It_Used()
    {
        const string Lua = "return level.place{kind = 'light', pos = {0, 3, 0}}.uid .. ',' .. level.brush{pos = {0, 2, 0}, size = 2}.uid";
        EditorDocument doc = NewLevel();
        (ScriptRunResult dry, _) = ScriptTestFixture.Run(doc, Lua, dryRun: true);
        (ScriptRunResult failed, _) = ScriptTestFixture.Run(doc, Lua.Replace("return ", "local x = ") + " error('stop')");
        Assert.False(failed.Success);
        (ScriptRunResult real, _) = ScriptTestFixture.Run(doc, Lua);
        (ScriptRunResult fresh, _) = ScriptTestFixture.Run(NewLevel(), Lua);
        Assert.True(dry.Success && real.Success && fresh.Success);
        Assert.Equal(fresh.ReturnValue, dry.ReturnValue);
        Assert.Equal(fresh.ReturnValue, real.ReturnValue);
    }

    [Fact]
    public void A_Handle_To_A_Deleted_Object_Refuses_To_Act()
    {
        foreach (string call in new[] { "o:set('size_range', 5)", "o:get('size_range')", "o:tag('k', 'v')", "o:set_rot{yaw = 90}", "o:fields()", "o:tags()" })
        {
            ScriptApiV2ContractTests.AssertApiError(NewLevel(),
                "local o = level.place{kind = 'light', pos = {0, 3, 0}} o:delete() " + call,
                "no longer exists", "deleted");
        }

        // A recreated object at the same spot is a different object; the old handle stays dead.
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local o = level.place{kind = 'light', pos = {0, 3, 0}} local uid = o.uid o:delete()\n" +
            "local n = level.place{kind = 'light', pos = {0, 3, 0}} n:set('size_range', 7)\n" +
            "return o:get('size_range')");
        Assert.False(r.Success);
        Assert.True(r.Error!.Message.Contains("no longer exists", StringComparison.Ordinal), r.Error.ToDisplayString());
    }

    [Fact]
    public void Renaming_A_Trigger_Keeps_Its_Multiplayer_Flags()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local t = level.place{kind = 'trigger', pos = {0, 1, 0}, name = 'gate', props = {mp_solo = true}}\n" +
            "assert(t.name == 'gate' and t:get('name') == 'gate' and t:get('mp_solo') == true)\n" +
            "t:set_name('gate_a') assert(t:get('mp_solo') == true and t.name == 'gate_a')\n" +
            "t:set('name', 'gate_b') assert(t:get('mp_solo') == true and t:get('name') == 'gate_b')\n" +
            "assert(#level.objects_named('gate_b') == 1)\n" +
            "return t.uid");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        var trigger = (Trigger)doc.FindByUid(int.Parse(r.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture))!.Model;
        Assert.True(trigger.IsPureFactionEncoded);
        Assert.True(trigger.PfSolo);
        Assert.EndsWith("gate_b", trigger.ScriptName, StringComparison.Ordinal);
    }

    [Fact]
    public void Queries_Iterate_With_Ipairs_And_Other_Userdata_Gets_A_Hint()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 2, boxes: 3),
            "local n, m, p = 0, 0, 0\n" +
            "for _, b in ipairs(level.all_brushes()) do n = n + b.uid * 0 + 1 end\n" +
            "for _, o in ipairs(level.objects_in{min = {-50, -50, -50}, max = {50, 50, 50}}) do m = m + 1 end\n" +
            "for _, b in pairs(level.all_brushes()) do p = p + 1 end\n" +
            "return n .. ':' .. m .. ':' .. p");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("3:2:3", r.ReturnValue);

        ScriptApiV2ContractTests.AssertApiError(ScriptTestFixture.NewDoc(entities: 0, boxes: 1),
            "for _, f in ipairs(level.brushes[1]) do end", "ipairs: expected a table, got a brush.", "q:each(fn)");
    }

    [Fact]
    public void A_Solid_Portal_Brush_Warns_And_Is_A_Lint_Finding()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc,
            "local a = level.brush{pos = {0, 2, 0}, size = 2} a:set_portal(true)\n" +
            "local b = level.brush{pos = {4, 2, 0}, size = 2} level.all_brushes():where(function(x) return x.uid == b.uid end):set_flags{portal = true}\n" +
            "local c = level.brush{pos = {8, 2, 0}, size = 2, portal = true}\n" +
            "return a.uid .. ',' .. b.uid");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        int[] solid = r.ReturnValue!.Split(',').Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        string[] warnings = log.Entries.Where(e => e.Level == ScriptLogLevel.Warning).Select(e => e.Message).ToArray();
        Assert.Contains(warnings, w => w.StartsWith("brush:set_portal: brush " + solid[0] + " is now a solid portal brush", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("brush_query:set_flags: brush " + solid[1] + " is now a solid portal brush", StringComparison.Ordinal));
        Assert.Equal(2, warnings.Count(w => w.Contains("solid portal", StringComparison.Ordinal)));

        int[] flagged = Linting.LevelLinter.Lint(doc.Rfl).Findings
            .Where(f => f.Category == Linting.LintCategory.SolidPortal).Select(f => f.Uid!.Value).OrderBy(u => u).ToArray();
        Assert.Equal(solid, flagged);
    }
}
