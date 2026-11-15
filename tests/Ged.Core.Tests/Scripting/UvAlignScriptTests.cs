using System;
using System.Globalization;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The Lua face of mapper-style alignment: level.uv_align (one undo step, every brush / brushes = / selection =),
/// brush_query:uv_align, level.uv_report (read-only, issues with positions), their option checks, and the kit's
/// kit.defaults.uv_align (on by default: nothing mirrored; off: the plain world box map).
/// </summary>
public sealed class UvAlignScriptTests
{
    private static string Ret(KitRun run) => run.Result.ReturnValue ?? string.Empty;

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Wall handedness of every brush in the document (1 = nothing mirrored, about 0.5 = box mapped).</summary>
    private static double Handedness(KitRun run) =>
        UvQuality.Measure(UvQuality.FacesOf(new BrushEditor(run.Doc).Brushes, Array.Empty<string>()), _ => (256, 256))["walls"]!["handedness_major"]!.GetValue<double>();

    [Fact]
    public void Uv_Align_Lines_Up_Every_Brush_As_One_Undo_Step_And_Reports_What_It_Did()
    {
        KitRun run = Run(
            "local r = level.brush{pos = {0, 3.75, 0}, size = {12, 4.5, 10}, air = true}\n" +
            "level.brush{pos = {3, 2, 0}, size = {1, 1, 1}}\n" +
            "level.all_brushes():box_map{ppm = 64}\n" +
            "local rep = level.uv_align{}\n" +
            "return string.format('%d %d %d %d', rep.brushes, rep.faces, rep.floors, rep.groups)");
        Assert.True(run.Result.Success, run.Result.Error?.ToDisplayString());
        string[] p = Ret(run).Split(' ');
        Assert.Equal("2", p[0]);
        Assert.Equal("12", p[1]);
        Assert.Equal(1.0, Handedness(run), 3);
        AssertUndoRestores(run);
    }

    [Fact]
    public void Uv_Align_Takes_Brushes_Or_A_Query_And_Leaves_The_Rest()
    {
        KitRun run = Run(
            "local a = level.brush{pos = {0, 2, 0}, size = {4, 4, 4}}\n" +
            "local b = level.brush{pos = {8, 2, 0}, size = {4, 4, 4}}\n" +
            "level.all_brushes():box_map{ppm = 64}\n" +
            "local before = b.faces[1].uvs[1].u\n" +
            "level.uv_align{brushes = a}\n" +
            "level.all_brushes():where(function(x) return x.uid == a.uid end):uv_align{fit = false}\n" +
            "return string.format('%.5f', b.faces[1].uvs[1].u - before)");
        Assert.True(run.Result.Success, run.Result.Error?.ToDisplayString());
        Assert.Equal(0, D(Ret(run)), 5); // b untouched
    }

    [Fact]
    public void Uv_Report_Measures_Without_Changing_Anything_And_Locates_Mirrored_Walls()
    {
        KitRun run = Run(
            "local r = level.brush{pos = {0, 2, 0}, size = {12, 4, 10}, air = true}\n" +
            "r:box_map{ppm = 64}\n" +
            "local rep = level.uv_report{issues = 10}\n" +
            "local mirrored = 0\n" +
            "for _, i in ipairs(rep.issues) do if i.kind == 'mirrored' then mirrored = mirrored + 1 assert(i.pos.y == 2) end end\n" +
            "local after = level.uv_align{} \n" +
            "local rep2 = level.uv_report{}\n" +
            "return string.format('%.2f %d %.2f', rep.walls.handedness_major, mirrored, rep2.walls.handedness_major)");
        Assert.True(run.Result.Success, run.Result.Error?.ToDisplayString());
        Assert.Equal("0.50 2 1.00", Ret(run));
    }

    [Theory]
    [InlineData("level.uv_align{ppm = 0}", "'ppm' must be between 1")]
    [InlineData("level.uv_align{fit_tolerance = 0.9}", "'fit_tolerance' must be between 0 and 0.5")]
    [InlineData("level.uv_align{max_fold = 200}", "'max_fold' must be 0 to 179")]
    [InlineData("level.uv_align{selection = true, brushes = {}}", "either 'brushes' or selection = true")]
    [InlineData("level.uv_align{brushes = 'x'}", "'brushes' must be a brush")]
    [InlineData("level.uv_align{tile = 4}", "tile")]
    [InlineData("level.uv_report{issues = 900}", "'issues' must be between 0 and 500")]
    public void Uv_Align_And_Report_Reject_Bad_Options(string call, string message)
    {
        ScriptDiagnostic e = Fail("level.brush{}\n" + call);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Kit_Aligns_By_Default_And_Box_Maps_When_Told_Not_To()
    {
        const string Body =
            "local r = kit.room{name = 'r', pos = {0, 1.5, 0}, size = {12, 4.5, 10}}\n" +
            "kit.platform{name = 'p', pos = {2, 1.5, 0}, size = {4, 3}, height = 1.5}\n" +
            "kit.stairs{name = 's', pos = {-4, 1.5, -2}, dir = '+z', rise = 1.5}\n";
        KitRun on = Run("local kit = ged.import('kit')\n" + Body);
        Assert.True(on.Result.Success, on.Result.Error?.ToDisplayString());
        Assert.True(Handedness(on) > 0.95, $"aligned kit handedness {Handedness(on):0.00}");
        AssertAccepted(on);

        KitRun off = Run("local kit = ged.import('kit')\nkit.defaults.uv_align = false\n" + Body);
        Assert.True(off.Result.Success, off.Result.Error?.ToDisplayString());
        Assert.InRange(Handedness(off), 0.4, 0.7); // the world box map mirrors about half the walls
    }
}
