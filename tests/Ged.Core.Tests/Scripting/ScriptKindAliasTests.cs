using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// One kind parser for placing and finding: every kind name <c>level.place{kind=}</c> accepts (aliases,
/// any case, underscores) also works for <c>level.objects_of</c>, <c>selection.of_kind</c> and positional
/// <c>level.place</c>, which additionally accept the non-placeable kinds (keyframes, movers, …).
/// </summary>
public sealed class ScriptKindAliasTests
{
    [Fact]
    public void Objects_Of_Accepts_The_Same_Kind_Names_As_Place()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, """
            level.place{ kind = "respawn", pos = {0, 1, 0} }
            level.place{ kind = "Spawn", pos = {2, 1, 0} }
            level.place("mp_respawn_point", 4, 1, 0)
            assert(#level.objects_of("respawn") == 3, "respawn")
            assert(#level.objects_of("MpRespawnPoint") == 3, "MpRespawnPoint")
            assert(#level.objects_of("mp_respawn_point") == 3, "mp_respawn_point")
            assert(#level.objects_of("SPAWN") == 3, "SPAWN")
            assert(selection.of_kind("respawn").count == 3, "of_kind")
            assert(#level.objects_of("player_start") == 1, "player_start")
            assert(#level.objects_of("Keyframe") == 0, "Keyframe")
            return "ok"
            """);
        Assert.True(r.Success, r.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries));
        Assert.Equal("ok", r.ReturnValue);
    }

    [Fact]
    public void Unknown_Kind_Names_The_API_And_The_Nearest_Kind()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "return #level.objects_of('lights')");
        Assert.False(r.Success);
        Assert.Contains("level.objects_of: unknown kind 'lights'", r.Error!.Message, System.StringComparison.Ordinal);
        Assert.Contains("Light", r.Error.Hint ?? string.Empty, System.StringComparison.Ordinal);

        (r, _) = ScriptTestFixture.Run(doc, "return selection.of_kind('keyfram').count");
        Assert.False(r.Success);
        Assert.Contains("selection.of_kind", r.Error!.Message, System.StringComparison.Ordinal);
        Assert.Contains("Keyframe", r.Error.Hint ?? string.Empty, System.StringComparison.Ordinal);
    }
}
