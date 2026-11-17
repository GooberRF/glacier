using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Linting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The owner's spawn rule (<see cref="SpawnClearance"/>): multiplayer respawn points, the Player Start and non-Air
/// waypoint helpers belong about 1 m above the floor, Air helpers in open air. The linter warns
/// (<see cref="LintCategory.SpawnHeight"/>) when one sits less than 0.5 m above a walkable floor or inside solid,
/// measured on the compiled geometry; scripted placement keeps literal coordinates and the lint reports them.
/// </summary>
public sealed class SpawnHeightLintTests
{
    /// <summary>A built level: one 20 × 6 × 20 m air room whose floor is y = 0.</summary>
    private static EditorDocument Room()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc));
        var be = new BrushEditor(doc);
        be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 20, Height = 6, Depth = 20, Air = true }, new Vec3(0, 3, 0), Mat3.Identity);
        CompiledLevel result = GeometryBuildService.Build(doc.Rfl, new CompileOptions { Alpine = true, SharedBsp = true, BuildSurfaces = true, FixTJoints = true });
        GeometryBuildService.Apply(doc.Rfl, result);
        doc.RefreshObjects();
        return doc;
    }

    private static List<LintFinding> SpawnFindings(EditorDocument doc) =>
        LevelLinter.Lint(doc.Rfl, new LintOptions { CheckGeometryLeaks = false, CheckOrphans = false })
            .Findings.Where(f => f.Category == LintCategory.SpawnHeight).ToList();

    private static void Move(EditorDocument doc, LevelObject o, Vec3 p)
    {
        switch (o.Model)
        {
            case IO.Rfl.Sections.PlayerStartSection ps:
                ps.Position = p;
                break;
            case AlpineWaypointHelper h:
                h.Position = p;
                break;
            case MpRespawnPoint rp:
                rp.Position = p;
                break;
        }

        o.MarkDirty();
        doc.RefreshObjects();
    }

    [Fact]
    public void Respawns_And_The_Player_Start_Near_Or_Under_The_Floor_Are_Reported_And_One_Metre_Up_Is_Clean()
    {
        EditorDocument doc = Room();
        LevelObject low = doc.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(-5, 0.2f, 0))!;
        LevelObject good = doc.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(5, 1f, 0))!;
        LevelObject buried = doc.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(0, -0.5f, 5))!;
        Move(doc, doc.Objects.Single(o => o.Kind == LevelObjectKind.PlayerStart), new Vec3(0, 0f, -5));

        List<LintFinding> findings = SpawnFindings(doc);
        Assert.Contains(findings, f => f.Uid == low.Uid && f.Message.Contains("0.2 m above the floor", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Uid == buried.Uid && f.Message.Contains("inside solid", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Message.StartsWith("The Player Start sits 0 m above the floor", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Uid == good.Uid);
        Assert.All(findings, f => Assert.Equal(LintSeverity.Warning, f.Severity));
        Assert.All(findings, f => Assert.False(f.BlocksSave));
        Assert.Equal(3, findings.Count);

        Move(doc, doc.Objects.Single(o => o.Kind == LevelObjectKind.PlayerStart), new Vec3(0, 1f, -5));
        Assert.Equal(2, SpawnFindings(doc).Count);
    }

    [Fact]
    public void Ground_Helpers_Stand_A_Metre_Up_And_Air_Helpers_Belong_In_Open_Air()
    {
        EditorDocument doc = Room();
        LevelObject ground = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(-5, 0.1f, 3))!;
        LevelObject groundOk = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(-5, 1.1f, -3))!;
        LevelObject air = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(5, 0.3f, 3))!;
        LevelObject airOk = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(5, 3f, -3))!;
        ((AlpineWaypointHelper)air.Model).HelperType = SpawnClearance.AirHelperType;
        ((AlpineWaypointHelper)airOk.Model).HelperType = SpawnClearance.AirHelperType;
        air.MarkDirty();
        doc.RefreshObjects();

        List<LintFinding> findings = SpawnFindings(doc);
        Assert.Contains(findings, f => f.Uid == ground.Uid && f.Message.StartsWith("Waypoint helper sits 0.1 m", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Uid == air.Uid && f.Message.Contains("Air helpers belong in open air", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Uid == groundOk.Uid || f.Uid == airOk.Uid);
    }

    [Fact]
    public void Scripted_Placement_Keeps_Literal_Coordinates_And_An_Unbuilt_Level_Is_Not_Checked()
    {
        EditorDocument doc = Room();
        LevelObject rp = doc.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(1, 0, 1))!;
        Assert.Equal(new Vec3(1, 0, 1), rp.Position); // no lift outside the GUI placement paths
        Assert.Contains(SpawnFindings(doc), f => f.Uid == rp.Uid);

        EditorDocument unbuilt = LevelTemplates.CreateNew();
        unbuilt.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(0, 0, 0));
        Assert.Empty(SpawnFindings(unbuilt));
    }

    [Fact]
    public void Lift_Raises_Along_A_Floor_Normal_Only()
    {
        Assert.Equal(new Vec3(2, 1, 3), SpawnClearance.Lift(new Vec3(2, 0, 3), new Vec3(0, 1, 0)));
        Assert.Equal(new Vec3(2, 0, 3), SpawnClearance.Lift(new Vec3(2, 0, 3), new Vec3(1, 0, 0))); // a wall: unchanged
        Vec3 slope = SpawnClearance.Lift(Vec3.Zero, new Vec3(0, 8, 6)); // unnormalised 0.8 / 0.6 slope
        Assert.Equal(0.8f, slope.Y, 4);
        Assert.Equal(0.6f, slope.Z, 4);
        Assert.True(SpawnClearance.StandsOnFloor(LevelObjectKind.PlayerStart));
        Assert.True(SpawnClearance.StandsOnFloor(LevelObjectKind.WaypointHelper)); // a new helper is Ground
        Assert.False(SpawnClearance.StandsOnFloor(LevelObjectKind.WaypointHelper, new AlpineWaypointHelper { HelperType = SpawnClearance.AirHelperType }));
        Assert.False(SpawnClearance.StandsOnFloor(LevelObjectKind.Item));
    }

    /// <summary>The rule fits the stock and community levels: no multiplayer respawn point in the example corpus is flagged.</summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void No_Example_Level_Respawn_Point_Is_Flagged()
    {
        if (TestPaths.RepoRoot is not { } root || !Directory.Exists(Path.Combine(root, "research", "example_rfls")))
        {
            return; // corpus unavailable
        }

        string dir = Path.Combine(root, "research", "example_rfls");
        int checkedPoints = 0;
        var flagged = new List<string>();
        foreach (string file in Directory.GetFiles(dir, "*.rfl").Where(f => !f.EndsWith(".autosave.rfl", StringComparison.OrdinalIgnoreCase)))
        {
            RflFile rfl = RflFile.Load(file);
            List<LintFinding> findings = LevelLinter.Lint(rfl, new LintOptions { CheckGeometryLeaks = false, CheckOrphans = false })
                .Findings.Where(f => f.Category == LintCategory.SpawnHeight && f.Message.StartsWith("Multiplayer", StringComparison.Ordinal)).ToList();
            checkedPoints += LevelObjectEnumerator.Enumerate(rfl, new HashSet<int>()).Count(o => o.Kind == LevelObjectKind.MpRespawnPoint);
            flagged.AddRange(findings.Select(f => $"{Path.GetFileName(file)}: {f.Message} at {f.Location}"));
        }

        Assert.True(checkedPoints > 100, $"only {checkedPoints} respawn points checked");
        Assert.True(flagged.Count == 0, string.Join("\n", flagged));
    }
}
