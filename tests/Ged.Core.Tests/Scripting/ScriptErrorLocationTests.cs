using System;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Every API error — a misused built-in, a design-kit misuse, a <c>ged.fail</c> raised by a module —
/// is reported at the line of the user's own script that made the call (the first frame outside an
/// imported module), with the hint in its own field rather than glued into the message.
/// </summary>
public sealed class ScriptErrorLocationTests
{
    private static ScriptDiagnostic Fail(string source, EditorDocument? doc = null)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc ?? LevelTemplates.CreateNew(KitTestHarness.FixedDate), source);
        Assert.False(r.Success, "expected the script to fail");
        return r.Error!;
    }

    [Fact]
    public void A_Top_Level_Api_Error_Reports_The_Calling_Line()
    {
        ScriptDiagnostic e = Fail("local a = 1\n\nlevel.brush{position = {0, 0, 0}}\nlocal b = 2");
        Assert.Equal(ScriptErrorKind.Api, e.Kind);
        Assert.Equal("test", e.Chunk);
        Assert.Equal(3, e.Line);
        Assert.Contains("unknown option 'position'", e.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'pos'?", e.Hint, StringComparison.Ordinal);
        Assert.StartsWith("test:3: level.brush: unknown option 'position'.", e.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Api_Error_Inside_A_Callback_Reports_The_Line_In_The_Callback()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0, boxes: 2);
        ScriptDiagnostic e = Fail("local q = level.all_brushes()\nq:each(function(b)\n  local s = 0\n  b:scale(0, 1, 1)\nend)", doc);
        Assert.Equal(ScriptErrorKind.Api, e.Kind);
        Assert.Equal(4, e.Line);

        ScriptDiagnostic nested = Fail("ged.group('outer', function()\n  ged.group('inner', function()\n\n    level.place{kind = 'light'}\n  end)\nend)");
        Assert.Equal(ScriptErrorKind.Api, nested.Kind);
        Assert.Equal(4, nested.Line);
    }

    [Fact]
    public void A_Kit_Misuse_Reports_The_Callers_Line_And_A_Separate_Hint()
    {
        ScriptDiagnostic e = Fail("local kit = ged.import('kit')\n\nkit.room{pos = {0, 0, 0}, size = {4, 4, 4}, sise = 1}");
        Assert.Equal(ScriptErrorKind.Api, e.Kind);
        Assert.Equal("test", e.Chunk);
        Assert.Equal(3, e.Line);
        Assert.Equal("kit.room: unknown option 'sise'.", e.Message);
        Assert.StartsWith("Did you mean 'size'?", e.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Hint:", e.Message, StringComparison.Ordinal);

        // A kit call made from inside a user callback still points at the user's line.
        ScriptDiagnostic inCallback = Fail("local kit = ged.import('kit')\nged.group('rooms', function()\n  kit.room{pos = {0, 0, 0}}\nend)");
        Assert.Equal(ScriptErrorKind.Api, inCallback.Kind);
        Assert.Equal(3, inCallback.Line);
        Assert.Contains("'size' is required", inCallback.Message, StringComparison.Ordinal);

        // An API error the kit itself triggers deep inside (a bad texture role) also reports the caller.
        ScriptDiagnostic deep = Fail("local kit = ged.import('kit')\nlocal x = 1\nkit.room{pos = {0, 0, 0}, size = {4, 4, 4}, textures = {flor = 'a.tga'}}");
        Assert.Equal(ScriptErrorKind.Api, deep.Kind);
        Assert.Equal(3, deep.Line);
        Assert.Contains("unknown option 'flor'", deep.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ged_Fail_Raises_An_Api_Error_With_Its_Hint()
    {
        ScriptDiagnostic e = Fail("local n = 0\nged.fail('mykit.wall: too thin.', 'Use at least 0.5 m.')");
        Assert.Equal(ScriptErrorKind.Api, e.Kind);
        Assert.Equal(2, e.Line);
        Assert.Equal("mykit.wall: too thin.", e.Message);
        Assert.Equal("Use at least 0.5 m.", e.Hint);

        ScriptDiagnostic bare = Fail("ged.fail('stop here')");
        Assert.Equal(1, bare.Line);
        Assert.Null(bare.Hint);
    }

    [Fact]
    public void A_Console_Line_Error_Reports_Its_Line()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        using ScriptReplSession repl = ScriptTestFixture.Runner().CreateRepl(ScriptTestFixture.Services(doc), new ScriptRunOptions(), new ScriptLog());
        ScriptRunResult r = repl.Eval("local a = 1\nlevel.place{kind = 'light', pos = {0, 1, 0}, nmae = 'x'}");
        Assert.False(r.Success);
        Assert.Equal("console", r.Error!.Chunk);
        Assert.Equal(2, r.Error.Line);
        Assert.Contains("Did you mean 'name'?", r.Error.Hint, StringComparison.Ordinal);
    }
}
