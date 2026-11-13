using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests;

/// <summary>
/// Decals over long compiled faces (research/alpine-decal-clip-overflow.md): a decal whose box touches a compiled
/// face with more than 19 vertex records crashes the game and the editor while the level loads. The linter reports
/// each such decal as an Error naming the decal and the face size (an unbuilt level is only noted), and kit.dress
/// never places one.
/// </summary>
public sealed class DecalClipLintTests
{
    private readonly ITestOutputHelper _out;

    public DecalClipLintTests(ITestOutputHelper output) => _out = output;

    /// <summary>A hall whose floor gains t-junction points from a row of pilasters along its north wall.</summary>
    private const string Hall = """
        local kit = ged.import('kit')
        local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {24, 6, 24} }
        for i = 0, 11 do level.brush{ pos = { -11 + i * 2, 3, 11.85 }, size = { 0.5, 6, 0.3 } } end
        """;

    private static EditorDocument NewDoc() => LevelTemplates.CreateNew(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local));

    private static void Run(EditorDocument doc, string script)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }

    private static CompiledLevel BuildSealed(EditorDocument doc, bool apply)
    {
        CompiledLevel built = GeometryBuildService.Build(doc.Rfl,
            CompileOptionsFactory.CreateForDocument(doc, vfs: null, new BuildSettings(), new CompileRequest { Sealed = true }));
        if (apply)
        {
            GeometryBuildService.Apply(doc.Rfl, built);
        }

        return built;
    }

    private static List<LintFinding> DecalFindings(LintReport report) =>
        report.Findings.Where(f => f.Category == LintCategory.DecalOnComplexFace).ToList();

    private static string Place(string name, Vec3 p, Mat3 r) => string.Format(CultureInfo.InvariantCulture,
        "level.place{{ kind = 'decal', name = '{0}', pos = {{ {1}, {2}, {3} }}, rot = {{ forward = {{ {4}, {5}, {6} }}, up = {{ {7}, {8}, {9} }} }}, " +
        "props = {{ texture = 'blastmark02.tga', extents = {{ 1, 1, 1 }} }} }}\n",
        name, p.X, p.Y, p.Z, r.Forward.X, r.Forward.Y, r.Forward.Z, r.Up.X, r.Up.Y, r.Up.Z);

    [Fact]
    public void The_Fixture_Has_A_Face_Over_19_Vertex_Records()
    {
        EditorDocument doc = NewDoc();
        Run(doc, Hall);
        Geometry g = BuildSealed(doc, apply: false).Geometry;
        int worst = g.Faces.Where(f => !f.IsPortalFace).Max(f => f.Vertices.Count);
        _out.WriteLine($"worst face: {worst} vertex records");
        Assert.True(worst > DecalClipCheck.MaxFaceVertexRecords, $"the pilaster hall's longest face has only {worst} vertex records");
    }

    [Fact]
    public void A_Decal_On_A_Long_Face_Is_An_Error_Naming_The_Decal_And_Face_Size_Once_Built()
    {
        EditorDocument doc = NewDoc();
        Run(doc, Hall);
        Geometry g = BuildSealed(doc, apply: false).Geometry;
        var index = new DecalClipCheck.FaceIndex(g);

        // A decal on the long face (at its centre, projecting onto it), and one on a short face clear of every long one.
        (Vec3 Centre, Vec3 Normal) At(Face f)
        {
            var c = new Vec3(0, 0, 0);
            foreach (FaceVertex v in f.Vertices)
            {
                c = c.Add(g.Vertices[v.Index]);
            }

            return (c.Scale(1f / f.Vertices.Count), f.Plane.Normal);
        }

        static Mat3 Facing(Vec3 n)
        {
            Vec3 fwd = n.Scale(-1f);
            Vec3 up = Math.Abs(n.Y) > 0.9f ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0);
            return new Mat3(fwd, up.Cross(fwd), up);
        }

        Face longFace = g.Faces.Where(f => !f.IsPortalFace).OrderByDescending(f => f.Vertices.Count).First();
        (Vec3 bad, Vec3 badN) = At(longFace);
        (Vec3 good, Vec3 goodN) = g.Faces.Where(f => !f.IsPortalFace && f.Vertices.Count <= 8).Select(At)
            .First(p => index.WorstTouched(p.Centre, Facing(p.Normal), new Vec3(1, 1, 1)).Records is > 0 and <= DecalClipCheck.MaxFaceVertexRecords);
        Assert.True(index.WorstTouched(bad, Facing(badN), new Vec3(1, 1, 1)).Records > DecalClipCheck.MaxFaceVertexRecords);
        Run(doc, Place("bad", bad, Facing(badN)) + Place("good", good, Facing(goodN)));

        // Unbuilt: noted, not reported.
        LintReport unbuilt = LevelLinter.Lint(doc.Rfl, new LintOptions { CheckGeometryLeaks = false });
        Assert.Empty(DecalFindings(unbuilt));
        Assert.Contains(unbuilt.Notes, n => n.Contains("not built", StringComparison.Ordinal) && n.Contains("decal", StringComparison.Ordinal));

        BuildSealed(doc, apply: true);
        LintReport built = LevelLinter.Lint(doc.Rfl, new LintOptions { CheckGeometryLeaks = false });
        LintFinding f = Assert.Single(DecalFindings(built));
        _out.WriteLine(f.ToString());
        Assert.Equal(LintSeverity.Error, f.Severity);
        Assert.Contains("'bad'", f.Message);
        Assert.Contains($"{longFace.Vertices.Count} vertex records", f.Message);
        Assert.Contains("crash", f.Message);
        Assert.Contains("when the level loads", f.Message);
        Assert.NotNull(f.Uid);
        Vec3 at = Assert.IsType<Vec3>(f.Location);
        Assert.Equal(bad.X, at.X, 2);
        Assert.Equal(bad.Z, at.Z, 2);
    }

    [Fact]
    public void Kit_Dress_Never_Places_A_Decal_On_A_Long_Face()
    {
        EditorDocument doc = NewDoc();
        Run(doc, Hall);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, """
            local kit = ged.import('kit')
            local total, unsafe = 0, 0
            for seed = 1, 6 do
              local d = kit.dress{ room = "hall", theme = "canyon", seed = seed, name = "d" .. seed, cover = false, overhead = false }
              total = total + d.by_kind.decals
              unsafe = unsafe + d.by_kind.decals_unsafe
            end
            return total .. ";" .. unsafe
            """);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] parts = r.ReturnValue!.Split(';');
        _out.WriteLine($"decals placed {parts[0]}, unsafe spots left out {parts[1]}");
        Assert.True(int.Parse(parts[0], CultureInfo.InvariantCulture) > 0, "kit.dress placed no decals at all");
        Assert.True(int.Parse(parts[1], CultureInfo.InvariantCulture) > 0, "no candidate spot touched the long face: the fixture does not exercise the check");

        // Every placed decal touches no long face of the sealed build of the dressed level.
        BuildSealed(doc, apply: true);
        LintReport report = LevelLinter.Lint(doc.Rfl, new LintOptions { CheckGeometryLeaks = false });
        Assert.Empty(DecalFindings(report));
        Assert.NotEmpty(doc.Rfl.Sections.Select(s => s.Content).OfType<DecalsSection>().SelectMany(s => s.Decals));
    }
}
