using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Shared plumbing for the layout-graph tests (DESIGN-QUALITY-SPEC §(g) Q4): generate a seeded <c>kit.layout</c>,
/// build it with <c>kit.build_layout</c> on a File ▸ New level, then measure it the only valid way — holes on a
/// SEALED build, and the design grader on that same sealed geometry.
/// </summary>
internal static class KitLayoutHarness
{
    /// <summary>The seven gametype × class combinations of the Q4 gate.</summary>
    public static readonly (string Gametype, string Class)[] Combos =
    {
        ("dm", "standard"), ("dm", "open_air"), ("ctf", "standard"), ("ctf", "open_air"),
        ("koth", "standard"), ("koth", "open_air"), ("payload", "payload"),
    };

    /// <summary>The player count a seed is generated for (cycles through the size classes).</summary>
    public static int Players(string gametype, int seed) => gametype switch
    {
        "dm" => new[] { 8, 12, 16, 6, 20 }[seed % 5],
        "payload" => new[] { 12, 16, 20, 8 }[seed % 4],
        _ => new[] { 8, 12, 16 }[seed % 3],
    };

    /// <summary>The kit.layout call for a combination.</summary>
    public static string LayoutCall(string gametype, string cls, int seed, int players) =>
        gametype == "payload"
            ? FormattableString.Invariant($"kit.layout{{ gametype = 'payload', seed = {seed}, players = {players} }}")
            : FormattableString.Invariant($"kit.layout{{ gametype = '{gametype}', class = '{cls}', seed = {seed}, players = {players} }}");

    private static ScriptExecutionLimits Limits => new() { Timeout = TimeSpan.FromSeconds(240), InstructionBudget = 4_000_000_000 };

    /// <summary>Runs Lua (kit imported) on a new level; returns the document and the run.</summary>
    public static (EditorDocument Doc, ScriptRunResult Result) Run(string body)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation() },
            "local kit = ged.import('kit')\n" + body,
            new ScriptRunOptions { ChunkName = "layout-test", AllowDestructive = true, Limits = Limits },
            new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return (doc, r);
    }

    /// <summary>Generates and builds one layout; the script returns "zones:links:violations:spawns".</summary>
    public static (EditorDocument Doc, string Summary) Build(string gametype, string cls, int seed, int players)
    {
        (EditorDocument doc, ScriptRunResult r) = Run(
            $"local g = {LayoutCall(gametype, cls, seed, players)}\n" +
            "local v = kit.check_layout{ graph = g }\n" +
            "local rep = kit.build_layout{ graph = g, name = 'L' }\n" +
            "return #g.zones .. ':' .. #g.links .. ':' .. #v .. ':' .. rep.spawns");
        return (doc, r.ReturnValue ?? string.Empty);
    }

    /// <summary>Holes of a sealed build under the chosen method, and the build.</summary>
    public static (CompiledLevel Build, List<Vec3> Holes) Holes(EditorDocument doc, bool sharedBsp)
    {
        CompiledLevel c = KitTestHarness.Sealed(doc, sharedBsp);
        return (c, HoleDetector.Detect(c.Geometry));
    }

    /// <summary>The design grade of a document measured on its sealed build.</summary>
    public static JsonObject Grade(EditorDocument doc, CompiledLevel sealedBuild, string gametype, string cls, int players)
    {
        SpatialLevel level = SpatialLevel.FromDocument(doc, sealedBuild.Geometry, GeometryFreshness.StoredFile, gametype + ".rfl");
        SpatialAnalysis a = SpatialAnalysis.Run(level);
        return DesignGrader.Grade(a, new GradeOptions { Gametype = gametype, Players = players, Class = cls == "payload" ? "payload" : cls });
    }

    /// <summary>The grader's flow checks for a combination (§(g) Q4 gate): G12-G14, C01 (CTF), K01 (KOTH), and the
    /// payload flow/structure checks (P01-P03, P05-P07; P04 is population, chunk Q5). G12 is replaced by P05 on payload.</summary>
    public static string[] FlowChecks(string gametype) => gametype switch
    {
        "ctf" => new[] { "G12", "G13", "G14", "C01" },
        "koth" => new[] { "G12", "G13", "G14", "K01" },
        "payload" => new[] { "G13", "G14", "P01", "P02", "P03", "P05", "P06", "P07" },
        _ => new[] { "G12", "G13", "G14" },
    };

    public static string ArtifactsDir()
    {
        string root = ScriptTestFixture.RepoRoot() ?? Path.GetTempPath();
        string dir = Path.Combine(root, "tests", "artifacts", "kit_layout");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
