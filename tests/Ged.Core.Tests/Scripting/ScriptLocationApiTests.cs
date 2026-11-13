using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Locations reach Lua: <c>ops.holes()</c> returns leak positions from a sealed
/// build (alongside the unchanged <c>ops.check_holes()</c> count), and <c>lint_finding</c> exposes
/// <c>has_location</c> / <c>pos</c>.
/// </summary>
public sealed class ScriptLocationApiTests
{
    /// <summary>An operations double that reports fixed leak positions (no compile).</summary>
    private sealed class FixedHolesOps : IScriptOperations
    {
        public IReadOnlyList<Vec3> Holes { get; init; } = Array.Empty<Vec3>();

        public ScriptOpReport Build(bool bakeLighting, bool shadows) => new(true, "built");

        public IReadOnlyList<Vec3> CheckHoles() => Holes;

        public void Save(string? path)
        {
        }

        public string CompatibilitySummary() => string.Empty;

        public ScriptOpReport Package(string? path, bool multiplayer) => new(true, "packed");

        public void Playtest(bool multiplayer)
        {
        }
    }

    [Fact]
    public void Ops_Holes_Returns_Count_And_Positions()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        var ops = new FixedHolesOps { Holes = new[] { new Vec3(1, 2, 3), new Vec3(-4, 5.5f, 6) } };

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, @"
            local h = ops.holes()
            assert(h.count == 2 and not h.is_watertight)
            assert(#h.positions == 2)
            assert(ops.check_holes() == 2) -- the count API is unchanged
            local p = h.positions[2]
            return string.format('%g,%g,%g', p.x, p.y, p.z)", ops: ops);

        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("-4,5.5,6", r.ReturnValue);
    }

    [Fact]
    public void Ops_Holes_On_A_Sealed_Room_Is_Watertight()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 1, 1));
        new BrushEditor(doc).CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 8, Height = 4, Depth = 8, Air = true },
            new Vec3(0, 2, 0), Mat3.Identity);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, @"
            local h = ops.holes()
            return tostring(h.is_watertight) .. ':' .. h.count .. ':' .. ops.check_holes()");

        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("true:0:0", r.ReturnValue);
    }

    [Fact]
    public void Lint_Finding_Exposes_Location()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.Triggers, Array.Empty<byte>())
        {
            Content = new TriggersSection { Triggers = { new Trigger { Uid = 42, Position = new Vec3(1.5f, 2, -3) } } },
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        var doc = new EditorDocument(rfl, "lint.rfl");

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, @"
            local located, unlocated
            for _, f in ipairs(lint.run().findings) do
              if f.uid == 42 then located = f end
              if f.category == 'MissingPlayerStart' then unlocated = f end
            end
            assert(located and located.has_location)
            assert(unlocated and not unlocated.has_location and unlocated.pos == nil)
            local p = located.pos
            return string.format('%g,%g,%g', p.x, p.y, p.z)");

        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("1.5,2,-3", r.ReturnValue);
    }
}
