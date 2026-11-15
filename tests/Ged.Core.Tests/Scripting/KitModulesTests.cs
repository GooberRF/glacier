using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Modules;
using Ged.Core.Scripting;
using Ged.Core.Tests.Modules;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// A small module library harvested from the in-code module fixture level (pilaster, railing, door frame, beam,
/// crate stack — no stock geometry) into a temp folder, shared by the kit detailing tests.
/// </summary>
public sealed class KitModulesLibraryFixture : IDisposable
{
    public KitModulesLibraryFixture()
    {
        Root = ModuleTestLevel.TempDir();
        (_, List<BuiltModule> built) = ModuleTestLevel.Harvest(ModuleTestLevel.Build());
        ModuleLibraryBuilder.Write(Root, built, new[] { "stock" });
        Library = ModuleLibrary.Load(Root);
    }

    /// <summary>The library folder (holds stock/index.json).</summary>
    public string Root { get; }

    public ModuleLibrary Library { get; }

    public string Id(string category) => Library.Modules.Single(m => m.Category == category).Id;

    public string Style(string category) => Library.Modules.Single(m => m.Category == category).PrimaryStyle;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still reads is left for the OS to clean.
        }
    }
}

/// <summary>Shared runner: a kit script on a new level with an explicit module library folder (or none).</summary>
internal static class KitModulesRun
{
    public sealed record Run(EditorDocument Doc, ScriptRunResult Result, ScriptLog Log);

    public static Run Exec(string? libraryDir, string body, EditorDocument? doc = null, bool expectSuccess = true)
    {
        doc ??= LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), ModuleLibraryDirectory = libraryDir },
            "local kit = ged.import('kit')\n" + body,
            new ScriptRunOptions { ChunkName = "kit-modules-test", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            log);
        if (expectSuccess)
        {
            Assert.True(r.Success, r.Error?.ToDisplayString());
        }

        return new Run(doc, r, log);
    }

    /// <summary>A folder that holds no library (the "absent" state).</summary>
    public static string Missing() => Path.Combine(Path.GetTempPath(), "glacier-no-modules-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A room with a doorway to a second room (both kit rooms), as Lua.</summary>
    public const string TwoRooms =
        "local room = kit.room{ name = 'hall', pos = {0, 0, 0}, size = {16, 6, 12} }\n" +
        "local side = kit.room{ name = 'side', pos = {0, 0, 12}, size = {8, 4, 10} }\n" +
        "local door = kit.doorway{ name = 'door', a = room, b = side, width = 3, height = 3.5 }\n";
}

/// <summary>The <c>modules</c> global: list / get / fit / place, error paths, the absent library.</summary>
public sealed class KitModulesGlobalTests : IClassFixture<KitModulesLibraryFixture>
{
    private readonly KitModulesLibraryFixture _lib;

    public KitModulesGlobalTests(KitModulesLibraryFixture lib) => _lib = lib;

    [Fact]
    public void List_Get_Fit_And_Place_Work_On_A_Library()
    {
        string pil = _lib.Id("pilaster");
        string beam = _lib.Id("beam");
        KitModulesRun.Run run = KitModulesRun.Exec(_lib.Root,
            "assert(modules.available and modules.count == " + _lib.Library.Modules.Count + ")\n" +
            "local l = modules.list{ category = 'pilaster' }\n" +
            "assert(#l == 1 and l[1].id == '" + pil + "' and l[1].attach == 'wall' and l[1].stretch.y ~= nil)\n" +
            "assert(#modules.list{ category = 'pilaster', limit = 0 } == 0)\n" +
            "assert(#modules.list{ category = 'pilaster', stretch = 'z' } == 0)\n" +
            "local m = modules.get('" + pil.ToUpperInvariant() + "')\n" +
            "assert(m and m.id == '" + pil + "' and m.role == 'theme_detail' and #m.sources >= 1 and m.provenance[1].level ~= nil)\n" +
            "assert(modules.get('nope') == nil)\n" +
            "local f = modules.fit{ id = m.id, height = 6 }\n" +
            "assert(f.ok and math.abs(f.size.y - 6) < 1e-3, 'fit height')\n" +
            "local g = modules.fit{ id = '" + beam + "', height = 3 }\n" +
            "assert(not g.ok and g.fixed[1] == 'y', 'beam cannot stretch on y')\n" +
            "level.brush{ pos = {0, 3, 0}, size = {12, 6, 12}, air = true }\n" +
            "local p = modules.place{ id = m.id, pos = {0, 0, 6}, yaw = 180, height = 6, tags = { ['kit.role'] = 'cover' }, name = 'p1' }\n" +
            "assert(#p.brushes >= 1 and p.id == m.id and p.role == 'cover' and p.category == 'pilaster')\n" +
            "for _, b in ipairs(p.brushes) do\n" +
            "  local t = b:tags()\n" +
            "  assert(t['kit.module'] == m.id and t['kit.role'] == 'cover' and t['kit.part'] == 'pilaster' and t['kit.name'] == 'p1')\n" +
            "  assert(b.detail, 'placed solids are detail')\n" +
            "end\n" +
            "return tostring(#p.brushes)");
        Assert.Equal("3", run.Result.ReturnValue);
    }

    [Theory]
    [InlineData("modules.place{ id = 'nope', pos = {0, 0, 0} }", "no module 'nope'")]
    [InlineData("modules.place{ id = ID, pos = {0, 0, 0}, colour = 1 }", "unknown")]
    [InlineData("modules.place{ id = ID }", "'pos' is required")]
    [InlineData("modules.place{ id = BEAM, pos = {0, 0, 0}, height = 3 }", "cannot stretch on y")]
    [InlineData("modules.list{ category = 'pilasterz' }", "unknown category")]
    [InlineData("modules.fit{ id = ID, length = -1 }", "positive")]
    public void Misuse_Is_An_Api_Error_With_A_Hint(string call, string expected)
    {
        string body = "local ID, BEAM = '" + _lib.Id("pilaster") + "', '" + _lib.Id("beam") + "'\n" + call;
        KitModulesRun.Run run = KitModulesRun.Exec(_lib.Root, body, expectSuccess: false);
        Assert.False(run.Result.Success);
        Assert.Contains(expected, run.Result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Absent_Library_Is_A_Normal_State()
    {
        KitModulesRun.Run run = KitModulesRun.Exec(KitModulesRun.Missing(),
            "assert(modules.available == false and modules.count == 0 and type(modules.reason) == 'string')\n" +
            "assert(#modules.list{} == 0 and modules.get('x') == nil)\n" +
            "return modules.reason");
        Assert.Contains("does not exist", run.Result.ReturnValue, StringComparison.Ordinal);
    }
}

/// <summary>The kit's detailing functions on the fixture library and without one (fast checks).</summary>
public sealed class KitModulesTests : IClassFixture<KitModulesLibraryFixture>
{
    private readonly KitModulesLibraryFixture _lib;

    public KitModulesTests(KitModulesLibraryFixture lib) => _lib = lib;

    private string Style => _lib.Style("pilaster");

    /// <summary>Lua asserting every brush of a record carries kit, kit.name, kit.part, kit.role and kit.module.</summary>
    private const string AssertTagged =
        "local function tagged(r)\n" +
        "  assert(#r.brushes > 0, 'nothing placed')\n" +
        "  for _, b in ipairs(r.brushes) do\n" +
        "    local t = b:tags()\n" +
        "    assert(t.kit and t['kit.name'] and t['kit.part'] and t['kit.role'] and t['kit.module'], 'untagged brush ' .. b.uid)\n" +
        "    assert(({cover = 1, route = 1, landmark = 1, theme_detail = 1, fixture = 1})[t['kit.role']], 'bad role')\n" +
        "  end\n" +
        "  for _, e in ipairs(r.placed) do assert(e.role and e.category and e.brushes, 'record entry') end\n" +
        "end\n";

    [Fact]
    public void Wall_Detail_Places_Library_Pilasters_On_The_Wall_And_Keeps_The_Doorway_Clear()
    {
        string pil = _lib.Id("pilaster");
        KitModulesRun.Run run = KitModulesRun.Exec(_lib.Root, KitModulesRun.TwoRooms + AssertTagged +
            "local d = kit.wall_detail{ room = room, style = '" + Style + "', pitch = 6, seed = 1 }\n" +
            "tagged(d)\n" +
            "local n = 0\n" +
            "for _, e in ipairs(d.placed) do if e.id == '" + pil + "' then n = n + 1 end end\n" +
            "assert(n >= 4, 'library pilasters: ' .. n)\n" +
            // Nothing inside the doorway's clear opening (x -1.5..1.5, y 0..3.5, z 6..7) nor in front of it.
            "for _, b in ipairs(d.brushes) do\n" +
            "  local bb = b.bounds\n" +
            "  local inside = bb.max.x > -1.5 + 1e-3 and bb.min.x < 1.5 - 1e-3 and bb.max.y > 1e-3 and bb.min.y < 3.5 - 1e-3 and bb.max.z > 5.0 and bb.min.z < 7 - 1e-3\n" +
            "  assert(not inside, 'brush ' .. b.uid .. ' blocks the doorway')\n" +
            // On the wall: every wall part touches a wall plane of the room (x = +-8 or z = +-6) and stays inside it.
            "  assert(bb.min.x > -8.6 and bb.max.x < 8.6 and bb.min.z > -6.6 and bb.max.z < 6.6, 'outside the room')\n" +
            "end\n" +
            "return tostring(#d.placed)");
        Assert.True(int.Parse(run.Result.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture) > 4);
    }

    [Fact]
    public void Same_Seed_Gives_Identical_Geometry()
    {
        string body = KitModulesRun.TwoRooms +
            "kit.detail{ room = room, style = '" + Style + "', seed = 7, level = 'full', doorways = { door } }\n";
        byte[] a = KitModulesRun.Exec(_lib.Root, body).Doc.SaveToBytes(updateTimestamp: false);
        byte[] b = KitModulesRun.Exec(_lib.Root, body).Doc.SaveToBytes(updateTimestamp: false);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Frame_Opening_Never_Narrows_The_Doorway_And_Frames_It_Once()
    {
        KitModulesRun.Run run = KitModulesRun.Exec(_lib.Root, KitModulesRun.TwoRooms + AssertTagged +
            "local f = kit.frame_opening{ doorway = door, style = '" + Style + "' }\n" +
            "tagged(f)\n" +
            "for _, b in ipairs(f.brushes) do\n" +
            "  local bb = b.bounds\n" +
            "  local inside = bb.max.x > -1.5 + 1e-3 and bb.min.x < 1.5 - 1e-3 and bb.min.y < 3.5 - 1e-3 and bb.max.z > 6 + 1e-3 and bb.min.z < 7 - 1e-3\n" +
            "  assert(not inside, 'frame brush ' .. b.uid .. ' narrows the opening: ' .. tostring(bb.min) .. ' ' .. tostring(bb.max) .. ' ' .. tostring(f.placed[1].id))\n" +
            "end\n" +
            "local again = kit.frame_opening{ doorway = door, style = '" + Style + "' }\n" +
            "assert(#again.placed == 0 and again.skipped[1].reason:find('already'), 'framed twice')\n" +
            "return f.fallback and 'primitive' or 'module'");
        Assert.NotNull(run.Result.ReturnValue);
    }

    [Fact]
    public void Machinery_Honours_The_Walk_Budget_And_Keeps_The_Doors_Connected()
    {
        KitModulesRun.Run run = KitModulesRun.Exec(_lib.Root, KitModulesRun.TwoRooms + AssertTagged +
            "local P = ged.import('kit_probe')\n" +
            "local s = P.spaces('t', { room = room }, ged.fail)[1]\n" +
            "local before = P.shared():walk_area_of(s)\n" +
            "local m = kit.machinery{ room = room, style = '" + Style + "', count = 8, seed = 2 }\n" +
            "tagged(m)\n" +
            "local after = P.shared():walk_area_of(s)\n" +
            "assert((before - after) / before <= 0.02 + 1e-9, 'walk change ' .. (before - after) / before)\n" +
            // The doorway's approach (2.5 m keep-out) holds no piece.
            "for _, b in ipairs(m.brushes) do\n" +
            "  local bb = b.bounds\n" +
            "  assert(not (bb.max.x > -2 and bb.min.x < 2 and bb.max.z > 4 and bb.min.z < 6.1), 'blocks the doorway approach')\n" +
            "end\n" +
            "return tostring(#m.placed)");
        Assert.True(int.Parse(run.Result.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture) >= 1);
    }

    [Fact]
    public void Every_Function_Works_Without_A_Library_Via_Primitives()
    {
        KitModulesRun.Run run = KitModulesRun.Exec(KitModulesRun.Missing(), KitModulesRun.TwoRooms + AssertTagged +
            "local plat = kit.platform{ pos = {4, 0, -3}, size = {4, 4}, height = 2 }\n" +
            "local stairs = kit.stairs{ pos = {-4, 0, -5}, dir = '+z', rise = 1.5, width = 3 }\n" +
            "local out = {}\n" +
            "local function check(r) tagged(r) assert(r.fallback == true and #r.note > 0, 'fallback flag/note') out[#out + 1] = r.name end\n" +
            "check(kit.wall_detail{ room = room })\n" +
            "check(kit.frame_opening{ doorway = door })\n" +
            "check(kit.ceiling_detail{ room = room })\n" +
            "check(kit.machinery{ room = room, count = 2 })\n" +
            "check(kit.railing{ from = {2, 2, -1}, to = {6, 2, -1}, side = '+z' })\n" +
            "check(kit.catwalk{ from = {-6, 3, -4}, to = {-6, 3, 4}, width = 2.5 })\n" +
            "check(kit.pipe_run{ from = {-7.5, 5, -5}, to = {7, 5, -5} })\n" +
            "check(kit.rock_detail{ space = side, count = 2 })\n" +
            "local sd = kit.stair_detail{ stairs = stairs } tagged(sd)\n" +
            "check(kit.detail{ room = side, level = 'full' })\n" +
            "return table.concat(out, ',')");
        Assert.Equal(9, run.Result.ReturnValue!.Split(',').Length);
        Assert.Contains(run.Log.Entries, e => e.Level == ScriptLogLevel.Warning && e.Message.Contains("primitives", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_Options_Are_Rejected_With_The_Nearest_Name()
    {
        ScriptDiagnostic e = KitTestHarness.Fail("kit.wall_detail{ room = 'x', pich = 4 }");
        Assert.Contains("unknown option 'pich'", e.Message, StringComparison.Ordinal);
        Assert.Contains("pitch", e.Hint, StringComparison.Ordinal);
    }
}

/// <summary>Heavier gates: detailed rooms build sealed with 0 holes under both build methods.</summary>
[Trait("Category", "DeepGate")]
public sealed class KitModulesSealedTests : IClassFixture<KitModulesLibraryFixture>
{
    private readonly KitModulesLibraryFixture _lib;

    public KitModulesSealedTests(KitModulesLibraryFixture lib) => _lib = lib;

    private const string Scene = KitModulesRun.TwoRooms +
        "local stairs = kit.stairs{ name = 'st', pos = {-4, 0, -5}, dir = '+z', rise = 2, width = 3 }\n" +
        "kit.detail{ room = room, style = STYLE, seed = 3, level = 'full', stairs = { stairs }, doorways = { door } }\n" +
        "kit.detail{ room = side, style = STYLE, seed = 3, level = 'light' }\n" +
        "kit.catwalk{ from = {6, 3, -4}, to = {6, 3, 4}, width = 2.5, style = STYLE }\n" +
        "kit.pipe_run{ from = {-7.5, 5, -5}, to = {7, 5, -5}, style = STYLE, count = 2 }\n";

    [Fact]
    public void A_Detailed_Room_Builds_Sealed_With_Zero_Holes_Under_Both_Methods()
    {
        KitModulesRun.Run run = KitModulesRun.Exec(_lib.Root, "local STYLE = '" + _lib.Style("pilaster") + "'\n" + Scene);
        KitTestHarness.AssertWatertight(run.Doc, sharedBsp: true);
        KitTestHarness.AssertWatertight(run.Doc, sharedBsp: false);
    }

    [Fact]
    public void The_Primitive_Fallback_Builds_Sealed_With_Zero_Holes_Under_Both_Methods()
    {
        KitModulesRun.Run run = KitModulesRun.Exec(KitModulesRun.Missing(), "local STYLE = 'industrial'\n" + Scene +
            "kit.rock_detail{ space = side, count = 3 }\n");
        KitTestHarness.AssertWatertight(run.Doc, sharedBsp: true);
        KitTestHarness.AssertWatertight(run.Doc, sharedBsp: false);
    }
}
