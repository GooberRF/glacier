using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using MoonSharp.Interpreter;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The API-version-2 contract: the frozen v1 surface is preserved line for line,
/// every v2 member and type carries a <see cref="ScriptDocAttribute"/> whose example compiles,
/// <c>ged.api_version</c> is 2, <c>ged.describe</c> covers every registered global/type, the
/// conventions text is shared with the generated reference, and suggestions are deterministic.
/// </summary>
public sealed class ScriptApiV2ContractTests
{
    private static string RepoRoot() =>
        ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("Could not locate repo root (Glacier.sln).");

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void V1_Surface_Is_Preserved()
    {
        string frozen = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "Ged.Core.Tests", "Scripting", "api-surface.v1.snapshot.txt"));
        var current = new HashSet<string>(Lines(ScriptApiReference.GenerateSurfaceSnapshot()), StringComparer.Ordinal);
        string[] missing = Lines(frozen).Where(l => !current.Contains(l)).ToArray();
        Assert.True(missing.Length == 0, "V1 surface lines missing from the current surface:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void V2_Mvp_Surface_Is_Preserved()
    {
        // The frozen API-version-2 MVP surface (before the organic-geometry batch) stays a subset: later v2
        // additions are additive only.
        string frozen = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "Ged.Core.Tests", "Scripting", "api-surface.v2-mvp.snapshot.txt"));
        var current = new HashSet<string>(Lines(ScriptApiReference.GenerateSurfaceSnapshot()), StringComparer.Ordinal);
        string[] missing = Lines(frozen).Where(l => !current.Contains(l)).ToArray();
        Assert.True(missing.Length == 0, "V2-MVP surface lines missing from the current surface:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Organic_Hull_Surface_Is_Present()
    {
        string[] lines = Lines(ScriptApiReference.GenerateSurfaceSnapshot());
        foreach (string expected in new[]
        {
            "level.brush_from_points(table):brush",
            "level.brushes_from_points(table):brush_query",
            "geo.check(brush):geo_report",
            "geo_report.ok:boolean",
            "geo_report.issues:string[]",
        })
        {
            Assert.Contains(expected, lines);
        }

        Assert.Contains("geo", ScriptApiV1.Globals);
    }

    [Fact]
    public void Organic_Edit_Import_And_Provenance_Surface_Is_Present()
    {
        string[] lines = Lines(ScriptApiReference.GenerateSurfaceSnapshot());
        foreach (string expected in new[]
        {
            "geo.jitter(table):brush_query",
            "geo.twist(table):brush_query",
            "geo.bend(table):brush_query",
            "geo.taper(table):brush_query",
            "geo.extrude(table):brush_query",
            "geo.bevel(table):brush_query",
            "geo.weld(table):brush_query",
            "geo.snap(table):brush_query",
            "level.import_mesh(table):mesh_import",
            "catalog.texture_info(string):texture_info",
            "catalog.textures(table):texture_info[]",
            "catalog.textures(string?,number?):string[]",
            "assets.texture_sources():texture_info[]",
            "texture_info.stock:boolean",
            "mesh_import.missing_textures:string[]",
        })
        {
            Assert.Contains(expected, lines);
        }

        foreach (string set in new[] { "GeoJitter", "GeoTwist", "GeoBend", "GeoTaper", "GeoExtrude", "GeoBevel", "GeoWeld", "GeoSnap", "ImportMesh", "TextureQuery" })
        {
            Assert.NotNull(ScriptOptionSets.Find(set));
        }
    }

    [Fact]
    public void Every_Member_Has_ScriptDoc()
    {
        string frozen = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "Ged.Core.Tests", "Scripting", "api-surface.v1.snapshot.txt"));
        var v1 = new HashSet<string>(Lines(frozen), StringComparer.Ordinal);
        var problems = new List<string>();
        int v2Count = 0;
        foreach ((string receiver, string member, string line, ScriptDocAttribute? doc) in ScriptApiReference.DocumentedSurface())
        {
            bool isNew = !v1.Contains(line);
            if (isNew)
            {
                v2Count++;
            }

            if (doc is null)
            {
                problems.Add($"{line}: missing [ScriptDoc]");
                continue;
            }

            if (isNew && doc.Since < 2)
            {
                problems.Add($"{line}: new member must declare Since = 2");
            }
            else if (!isNew && doc.Since != 1)
            {
                problems.Add($"{line}: a version-1 member must keep Since = 1");
            }

            CheckDoc($"{receiver}.{member}", doc, problems);
        }

        // Types introduced in v2 must carry a type-level doc as well.
        foreach ((string lua, Type type) in ScriptApiReference.SurfaceTypes())
        {
            bool newType = !v1.Any(l => l.StartsWith(lua + ".", StringComparison.Ordinal));
            if (newType && type.GetCustomAttribute<ScriptDocAttribute>() is not { Since: >= 2 })
            {
                problems.Add($"type {lua}: missing [ScriptDoc(Since = 2)]");
            }
        }

        Assert.True(v2Count > 50, $"expected the v2 surface to be present, found {v2Count} new members");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static void CheckDoc(string name, ScriptDocAttribute doc, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(doc.Summary))
        {
            problems.Add($"{name}: empty summary");
        }
        else if (doc.Summary.Length > 200)
        {
            problems.Add($"{name}: summary is {doc.Summary.Length} chars (max 200)");
        }

        if (doc.Example is { Length: > 0 } ex)
        {
            try
            {
                new Script(CoreModules.Preset_HardSandbox).LoadString(ex);
            }
            catch (SyntaxErrorException se)
            {
                problems.Add($"{name}: example does not compile: {se.DecoratedMessage ?? se.Message}");
            }
        }
    }

    [Fact]
    public void Api_Version_Is_2()
    {
        Assert.Equal(2, ScriptApiV1.Version);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 0), "ged.require_api(2) return ged.api_version");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("2", r.ReturnValue);
    }

    [Fact]
    public void Api_1_And_Headerless_Scripts_Still_Run()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(ScriptTestFixture.Services(doc),
            "ged.require_api(1) level.place('Light', 0, 0, 0) return level.count",
            new ScriptRunOptions { ChunkName = "v1", DeclaredApiVersion = 1 }, log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("4", r.ReturnValue);

        ScriptRunResult future = ScriptTestFixture.Runner().Run(ScriptTestFixture.Services(doc), "return 1",
            new ScriptRunOptions { ChunkName = "v3", DeclaredApiVersion = 3 }, log);
        Assert.False(future.Success);
    }

    [Fact]
    public void Describe_Covers_Every_Type()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1, boxes: 1);
        foreach (string name in ScriptApiReference.DescribableNames())
        {
            (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
                $"local t = ged.describe('{name}') return t.name .. '|' .. #t.members .. '|' .. tostring(#t.text > 0)");
            Assert.True(r.Success, $"{name}: {r.Error?.ToDisplayString()}");
            Assert.StartsWith(name + "|", r.ReturnValue, StringComparison.Ordinal);
            Assert.EndsWith("|true", r.ReturnValue, StringComparison.Ordinal);
        }

        // Values describe as their type; globals describe as themselves.
        (ScriptRunResult byValue, _) = ScriptTestFixture.Run(doc,
            "return ged.describe(level.brushes[1]).name .. ',' .. ged.describe(level).name .. ',' .. ged.describe(ged.rot(1)).name .. ',' .. ged.describe(ged.vec(1,2,3)).name");
        Assert.True(byValue.Success, byValue.Error?.ToDisplayString());
        Assert.Equal("brush,level,rotation,vec", byValue.ReturnValue);

        // The member docs flow through (a v2 member carries its summary and since).
        (ScriptRunResult doc2, _) = ScriptTestFixture.Run(doc,
            "for _, m in ipairs(ged.describe('brush').members) do if m.name == 'set_pos' then return m.since .. ':' .. m.kind .. ':' .. m.returns end end");
        Assert.True(doc2.Success, doc2.Error?.ToDisplayString());
        Assert.Equal("2:method:brush", doc2.ReturnValue);

        (ScriptRunResult unknown, _) = ScriptTestFixture.Run(doc, "ged.describe('brsh')");
        Assert.False(unknown.Success);
        Assert.Contains("brush", unknown.Error!.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Conventions_Is_Shared()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 0), "return ged.conventions");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(ScriptConventions.Text, r.ReturnValue);
        Assert.Contains(ScriptConventions.Text, ScriptApiReference.GenerateMarkdown(), StringComparison.Ordinal);
        Assert.True(Lines(ScriptConventions.Text).Length <= 40, "the conventions block must stay within 40 lines");
    }

    [Fact]
    public void Suggest_Is_Deterministic()
    {
        string[] keys = { "shape", "pos", "size", "rot", "sides", "stacks", "splits", "air", "portal", "detail", "texture", "textures" };
        Assert.Equal(new[] { "pos" }, ScriptSuggest.Nearest("position", keys, 1));
        Assert.Equal(new[] { "texture", "textures" }, ScriptSuggest.Nearest("textur", keys));
        Assert.Equal(new[] { "size", "sides" }, ScriptSuggest.Nearest("sizes", keys, 2));
        Assert.Equal(new[] { "box" }, ScriptSuggest.Nearest("bxo", new[] { "cone", "box", "wedge" }));
        Assert.Empty(ScriptSuggest.Nearest("zzzzzz", keys));
        Assert.Equal("Did you mean: pos?", ScriptSuggest.Hint("Pos", keys, "option"));
        Assert.StartsWith("Valid shapes: box, cone", ScriptSuggest.Hint("qqqq", new[] { "cone", "box" }, "shape"), StringComparison.Ordinal);
        string[] many = Enumerable.Range(0, 50).Select(i => $"Class{i:00}").ToArray();
        Assert.Equal("Use catalog.classes(\"Item\") to list valid classes.", ScriptSuggest.Hint("zzzz", many, "class", "Item"));

        // Same inputs in any order → the same list.
        for (int i = 0; i < 5; i++)
        {
            string[] shuffled = keys.OrderBy(k => (k.GetHashCode() * (i + 1)) & 0xff).ToArray();
            Assert.Equal(ScriptSuggest.Nearest("s", keys), ScriptSuggest.Nearest("s", shuffled));
        }
    }

    [Fact]
    public void Args_Marshalling()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult ok, _) = ScriptTestFixture.Run(doc,
            "local a = level.brush{pos = {1, 2, 3}, size = {x = 2, y = 3, z = 4}}\n" +
            "local b = level.brush{pos = ged.vec(4, 5, 6), size = 2, splits = {1, 0, 2}}\n" +
            "return a.pos.x .. a.pos.y .. a.pos.z .. ':' .. a.size.x .. a.size.y .. a.size.z .. ':' .. b.pos.z .. ':' .. b.face_count");
        Assert.True(ok.Success, ok.Error?.ToDisplayString());
        Assert.Equal("123:234:6:22", ok.ReturnValue); // box splits {1,0,2}: 3+3+6+6+2+2 faces

        AssertApiError(doc, "level.brush{position = {0, 0, 0}}", "unknown option 'position'", "'pos'");
        AssertApiError(doc, "level.brush{pos = {0, 0}}", "'pos' must be a vec", null);
        AssertApiError(doc, "level.brush{pos = 'here'}", "'pos' must be a vec", null);
        AssertApiError(doc, "level.brush{air = 1}", "'air' must be a boolean (got number)", null);
        AssertApiError(doc, "level.brush{sides = 7.5, shape = 'cylinder'}", "must be an integer", null);
        AssertApiError(doc, "level.brush{shape = 3}", "'shape' must be a string (got number)", null);
        AssertApiError(doc, "level.brush{ {1, 2, 3} }", "positional", null);
        AssertApiError(doc, "level.brush{textures = {flor = 'a.tga'}}", "unknown option 'flor'", "'floor'");
    }

    internal static void AssertApiError(EditorDocument doc, string src, string messageFragment, string? hintFragment)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, src);
        Assert.False(r.Success, $"expected failure for: {src}");
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Contains(messageFragment, r.Error.Message, StringComparison.Ordinal);
        if (hintFragment is not null)
        {
            Assert.Contains(hintFragment, r.Error.Hint ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
