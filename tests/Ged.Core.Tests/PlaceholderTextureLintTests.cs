using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The placeholder-texture lint: faces the player can see that still carry the texture every new brush starts with
/// (or no texture) are a warning with a location and a fix hint, measured on the compiled geometry; portal, sky,
/// invisible and hidden faces are not, and an unbuilt level is only noted.
/// </summary>
public sealed class PlaceholderTextureLintTests
{
    private const string Real = "rck_wall01.tga";

    private static EditorDocument NewDoc() => LevelTemplates.CreateNew(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local));

    private static int Box(EditorDocument doc, Vec3 centre, float w, float h, float d, bool air, string? texture = null, bool portal = false) =>
        new BrushEditor(doc).CreateBrush(new BrushCreateParams
        {
            Shape = BrushShape.Box,
            Width = w,
            Height = h,
            Depth = d,
            Air = air,
            Portal = portal,
            FloorTexture = texture,
            WallTexture = texture,
            CeilingTexture = texture,
        }, centre, Mat3.Identity);

    private static Brush BrushOf(EditorDocument doc, int uid) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().SelectMany(b => b.Brushes).Single(b => b.Uid == uid);

    private static List<LintFinding> Placeholders(EditorDocument doc, LintOptions? options = null) =>
        LevelLinter.Lint(doc.Rfl, options ?? new LintOptions { CheckGeometryLeaks = false })
            .Findings.Where(f => f.Category == LintCategory.PlaceholderTexture).ToList();

    private static void BuildSealed(EditorDocument doc)
    {
        CompiledLevel built = GeometryBuildService.Build(doc.Rfl,
            CompileOptionsFactory.CreateForDocument(doc, vfs: null, new BuildSettings(), new CompileRequest { Sealed = true }));
        GeometryBuildService.Apply(doc.Rfl, built);
    }

    [Fact]
    public void A_New_Brush_With_The_Default_Texture_Is_A_Located_Warning_With_A_Fix_Once_Built()
    {
        EditorDocument doc = NewDoc();
        int room = Box(doc, new Vec3(0, 2, 0), 8, 4, 8, air: true);
        Assert.Equal(BrushCreateParams.DefaultTexture, BrushOf(doc, room).Geometry.Textures.Single());

        // Unbuilt: which faces will be seen is unknown, so nothing is reported, only noted.
        Assert.Empty(Placeholders(doc));
        LintReport unbuilt = LevelLinter.Lint(doc.Rfl, new LintOptions { CheckGeometryLeaks = false });
        Assert.Contains(unbuilt.Notes, n => n.Contains("not built", StringComparison.Ordinal) && n.Contains("placeholder", StringComparison.Ordinal));

        BuildSealed(doc);
        Assert.DoesNotContain(LevelLinter.Lint(doc.Rfl, new LintOptions { CheckGeometryLeaks = false }).Notes, n => n.Contains("placeholder", StringComparison.Ordinal));
        LintFinding f = Assert.Single(Placeholders(doc));
        Assert.Equal(LintSeverity.Warning, f.Severity);
        Assert.False(f.BlocksSave);
        Assert.Equal(room, f.Uid);
        Assert.Contains($"Air brush {room} has 6 visible face(s)", f.Message);
        Assert.Contains("Rck_Default.tga", f.Message);
        Assert.Contains("set_texture", f.Message);
        Vec3 at = Assert.IsType<Vec3>(f.Location);
        Assert.InRange(at.X, -4.01f, 4.01f);
        Assert.InRange(at.Y, -0.01f, 4.01f);
        Assert.InRange(at.Z, -4.01f, 4.01f);
    }

    [Fact]
    public void Textured_Portal_Sky_And_Invisible_Faces_Are_Not_Reported()
    {
        EditorDocument doc = NewDoc();
        Box(doc, new Vec3(0, 2, 0), 8, 4, 8, air: true, texture: Real);
        Box(doc, new Vec3(4, 1.5f, 0), 0.25f, 3, 2, air: true, portal: true); // portal brush, default texture
        int sky = Box(doc, new Vec3(0, 6, 0), 4, 1, 4, air: true);
        int hidden = Box(doc, new Vec3(0, 9, 0), 2, 1, 2, air: true);
        foreach (Face face in BrushOf(doc, sky).Geometry.Faces)
        {
            face.Flags |= (ushort)FaceFlags.ShowSky;
        }

        foreach (Face face in BrushOf(doc, hidden).Geometry.Faces)
        {
            face.Flags |= (ushort)FaceFlags.IsInvisible;
        }

        BuildSealed(doc);
        Assert.Empty(Placeholders(doc));
    }

    [Fact]
    public void A_Face_With_No_Texture_Is_Reported()
    {
        EditorDocument doc = NewDoc();
        int room = Box(doc, new Vec3(0, 2, 0), 8, 4, 8, air: true, texture: Real);
        Brush b = BrushOf(doc, room);
        b.Geometry.Textures.Add(string.Empty);
        b.Geometry.Faces[0].Texture = b.Geometry.Textures.Count - 1; // an empty texture name
        BuildSealed(doc);

        LintFinding f = Assert.Single(Placeholders(doc));
        Assert.Equal(room, f.Uid);
        Assert.Contains("1 visible face(s) with no texture", f.Message);
    }

    [Fact]
    public void On_A_Built_Level_Only_Faces_The_Build_Keeps_Are_Reported()
    {
        EditorDocument doc = NewDoc();
        Box(doc, new Vec3(0, 2, 0), 12, 4, 12, air: true, texture: Real);
        int pillar = Box(doc, new Vec3(2, 1, 2), 1, 2, 1, air: false);           // stands in the room: seen
        Box(doc, new Vec3(40, 2, 40), 2, 2, 2, air: false);                         // solid inside solid: never seen
        int roomCeiling = Box(doc, new Vec3(-3, 1, -3), 2, 2, 2, air: true, texture: Real);
        Brush r = BrushOf(doc, roomCeiling);
        r.Geometry.Textures.Add(BrushCreateParams.DefaultTexture);
        int last = r.Geometry.Textures.Count - 1;
        foreach (Face face in r.Geometry.Faces)
        {
            face.Texture = last; // an air box fully inside the room: its faces merge away in the build
        }

        BuildSealed(doc);
        List<LintFinding> found = Placeholders(doc);
        LintFinding f = Assert.Single(found, x => x.Uid == pillar);
        Assert.Contains("visible face(s)", f.Message);
        Vec3 at = Assert.IsType<Vec3>(f.Location);
        Assert.InRange(at.X, 1.49f, 2.51f);
        Assert.InRange(at.Z, 1.49f, 2.51f);

        // The inner air box shares the room's floor plane: the later air brush owns the coincident floor (RED's
        // survival rule), so of its six placeholder faces only that one floor patch survives the build and is reported.
        LintFinding inner = Assert.Single(found, x => x.Uid == roomCeiling);
        Assert.Contains("1 visible face(s)", inner.Message);
        Assert.Equal(2, found.Count);

        // Texturing the pillar clears it.
        Brush p = BrushOf(doc, pillar);
        p.Geometry.Textures[0] = Real;
        BuildSealed(doc);
        Assert.DoesNotContain(Placeholders(doc), x => x.Uid == pillar);
    }

    [Fact]
    public void Kit_Pieces_Are_Grouped_And_Name_The_Kit_Call()
    {
        EditorDocument doc = NewDoc();
        Box(doc, new Vec3(0, 3, 0), 40, 6, 12, air: true, texture: Real);
        var meta = new GedObjectMetadataService(doc);
        var uids = new List<int>();
        for (int i = 0; i < 7; i++)
        {
            int uid = Box(doc, new Vec3(i * 2, 0.25f * (i + 1), 0), 1, 0.5f * (i + 1), 1, air: false);
            meta.SetBlock(uid, GedMetadataType.Tags, ObjectTags.Encode(new Dictionary<string, string>
            {
                ["kit"] = "stairs",
                ["kit.name"] = "west_stairs",
                ["kit.part"] = "step" + i,
            }));
            uids.Add(uid);
        }

        int loose = Box(doc, new Vec3(-10, 1, 0), 1, 1, 1, air: false);
        BuildSealed(doc);

        List<LintFinding> found = Placeholders(doc);
        Assert.Equal(2, found.Count);
        LintFinding kit = Assert.Single(found, f => f.Uid == uids[0]);
        Assert.Contains("7 brushes (", kit.Message);
        Assert.Contains("made by kit.stairs \"west_stairs\"", kit.Message);
        Assert.Contains("textures = {floor", kit.Message);
        Assert.Contains("kit.defaults.texture", kit.Message);
        LintFinding single = Assert.Single(found, f => f.Uid == loose);
        Assert.DoesNotContain("kit.", single.Message);

        // The per-finding cap summarizes the rest.
        List<LintFinding> capped = Placeholders(doc, new LintOptions { CheckGeometryLeaks = false, MaxPlaceholderFindings = 1 });
        Assert.Equal(2, capped.Count);
        Assert.Contains("1 more brush(es)", capped[1].Message);
    }

    [Fact]
    public void Mover_Faces_Always_Count_Because_Movers_Are_Not_In_The_Static_Build()
    {
        EditorDocument doc = NewDoc();
        Box(doc, new Vec3(0, 2, 0), 12, 4, 12, air: true, texture: Real);
        int door = Box(doc, new Vec3(0, 1.5f, 0), 2, 3, 0.25f, air: false);
        new MoverService(doc).CreateMover(new[] { door }, Array.Empty<int>(), "door1");
        BuildSealed(doc);

        LintFinding f = Assert.Single(Placeholders(doc));
        Assert.Equal(door, f.Uid);
    }
}
