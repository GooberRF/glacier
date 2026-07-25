using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the level linter + statistics: an all-defects fixture must
/// produce the exact findings list, budgets must be target-aware and blocking on
/// over-cap, and budget/geometry counts must match a known corpus level (dm01).
/// </summary>
public sealed class LinterTests
{
    // ─── One-of-each-defect fixture ──────────────────────────────────────────

    [Fact]
    public void Lint_Fixture_Reports_Every_Defect_Kind()
    {
        RflFile level = AllDefectsLevel();
        LintReport report = LevelLinter.Lint(level, new LintOptions { Target = SaveTarget.Alpine });

        // Duplicate UID (two lights share 500).
        Assert.Contains(report.Findings, f =>
            f.Category == LintCategory.DuplicateUid && f.Uid == 500 && f.Severity == LintSeverity.Error);

        // Broken link: trigger 501 -> missing 9999.
        Assert.Contains(report.Findings, f =>
            f.Category == LintCategory.BrokenLink && f.Uid == 501 && f.SecondaryUid == 9999);

        // Trigger without links (502).
        Assert.Contains(report.Findings, f =>
            f.Category == LintCategory.TriggerWithoutLinks && f.Uid == 502);

        // Event orphan (503, disconnected).
        Assert.Contains(report.Findings, f =>
            f.Category == LintCategory.EventOrphan && f.Uid == 503);

        // Two isolated nav points (504, 505).
        Assert.Contains(report.Findings, f => f.Category == LintCategory.NavPoint && f.Uid == 504);
        Assert.Contains(report.Findings, f => f.Category == LintCategory.NavPoint && f.Uid == 505);

        // Geometry leak (a lone triangle has open edges).
        Assert.Contains(report.Findings, f => f.Category == LintCategory.GeometryLeak);

        // Budget over cap: 26 ambient sounds > 25.
        Assert.Contains(report.Findings, f =>
            f.Category == LintCategory.LimitBudget && f.Message.Contains("Ambient Sounds") && f.Severity == LintSeverity.Error);

        // No player_start / MP respawns in the fixture -> void-spawn error.
        Assert.Contains(report.Findings, f =>
            f.Category == LintCategory.MissingPlayerStart && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Lint_Fixture_Report_Text_Artifact()
    {
        RflFile level = AllDefectsLevel();
        LintReport report = LevelLinter.Lint(level, new LintOptions { Target = SaveTarget.StockRf });

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("GED Level Linter — all-defects fixture report");
        sb.AppendLine(report.Summary());
        sb.AppendLine();
        foreach (LintFinding f in report.Findings)
        {
            sb.AppendLine(f.ToString());
        }

        if (TestPaths.RepoRoot is string root)
        {
            string dir = Path.Combine(root, "tests", "artifacts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "linter_report.txt"), sb.ToString());
        }

        Assert.False(report.IsClean);
    }

    [Fact]
    public void Lint_Clean_Level_Is_Clean()
    {
        var rfl = NewLevel();
        AddSection(rfl, SectionType.PlayerStart, new PlayerStartSection { Position = new Vec3(0, 1, 0), Rotation = Mat3.Identity });
        AddSection(rfl, SectionType.Triggers, new TriggersSection
        {
            Triggers = { new Trigger { Uid = 10, Links = { 11 } } },
        });
        AddSection(rfl, SectionType.Events, new EventsSection
        {
            Events = { new RflEvent { Uid = 11, ClassName = "Play_Sound" } },
        });

        LintReport report = LevelLinter.Lint(rfl, new LintOptions { Target = SaveTarget.Alpine, CheckGeometryLeaks = false });

        Assert.DoesNotContain(report.Findings, f => f.Severity == LintSeverity.Error);
        Assert.DoesNotContain(report.Findings, f => f.Category == LintCategory.BrokenLink);
    }

    // ─── Missing player start (void spawn / black screen) ─────────────────────

    [Fact]
    public void Lint_Flags_Level_With_No_Spawn_Point()
    {
        // A level with neither a player_start nor MP respawn points spawns the player in the void
        // — a fully black screen in-game. Reported as a (non-blocking) Error.
        var rfl = NewLevel();
        AddSection(rfl, SectionType.Triggers, new TriggersSection { Triggers = { new Trigger { Uid = 10 } } });

        LintReport report = LevelLinter.Lint(rfl, new LintOptions { Target = SaveTarget.Alpine, CheckGeometryLeaks = false });

        LintFinding finding = Assert.Single(report.Findings, f => f.Category == LintCategory.MissingPlayerStart);
        Assert.Equal(LintSeverity.Error, finding.Severity);
        Assert.False(finding.BlocksSave); // serious but non-blocking (RED permits saving a startless level)
        Assert.Contains("Player Start", finding.Message);
    }

    [Fact]
    public void Lint_Passes_Level_With_A_Player_Start()
    {
        var rfl = NewLevel();
        AddSection(rfl, SectionType.PlayerStart, new PlayerStartSection { Position = new Vec3(0, 1, 0), Rotation = Mat3.Identity });

        LintReport report = LevelLinter.Lint(rfl, new LintOptions { Target = SaveTarget.Alpine, CheckGeometryLeaks = false });

        Assert.DoesNotContain(report.Findings, f => f.Category == LintCategory.MissingPlayerStart);
    }

    [Fact]
    public void Lint_Passes_Multiplayer_Level_With_Respawn_Points()
    {
        // An MP level legitimately spawns from mp_respawn_points instead of a single-player start.
        var rfl = NewLevel();
        AddSection(rfl, SectionType.MpRespawnPoints, new MpRespawnPointsSection
        {
            Points = { new MpRespawnPoint { Uid = 20, Position = new Vec3(0, 1, 0), Rotation = Mat3.Identity } },
        });

        LintReport report = LevelLinter.Lint(rfl, new LintOptions { Target = SaveTarget.Alpine, CheckGeometryLeaks = false });

        Assert.DoesNotContain(report.Findings, f => f.Category == LintCategory.MissingPlayerStart);
    }

    // ─── Missing assets (needs a VFS) ────────────────────────────────────────

    [Fact]
    public void Lint_Reports_Missing_Assets_Against_Vfs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged_lint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "present.tga"), new byte[] { 1, 2, 3, 4 });

            var rfl = NewLevel();
            var brush = new Brush { Uid = 200 };
            brush.Geometry.Textures.Add("present.tga");
            brush.Geometry.Textures.Add("absent.tga"); // missing everywhere
            AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { brush } });

            using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(dir) });
            LintReport report = LevelLinter.Lint(rfl, new LintOptions
            {
                Target = SaveTarget.Alpine,
                Vfs = vfs,
                CheckGeometryLeaks = false,
            });

            Assert.Contains(report.Findings, f =>
                f.Category == LintCategory.MissingAsset && f.Message.Contains("absent.tga"));
            Assert.DoesNotContain(report.Findings, f =>
                f.Category == LintCategory.MissingAsset && f.Message.Contains("present.tga"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    // ─── Target-aware budgets ────────────────────────────────────────────────

    [Fact]
    public void Budget_Decals_Are_Target_Aware()
    {
        var rfl = NewLevel();
        var decals = new DecalsSection();
        for (int i = 0; i < 97; i++)
        {
            decals.Decals.Add(new Decal { Header = new ObjectHeader { Uid = 1000 + i }, Texture = "d.tga" });
        }

        AddSection(rfl, SectionType.Decals, decals);

        BudgetLine line = LevelBudget.Compute(rfl).Single(b => b.Kind == BudgetKind.Decals);
        Assert.Equal(97, line.Count);
        Assert.Equal(96, line.StockCap);
        Assert.Equal(384, line.AlpineCap);

        // 97 > 96 stock -> blocking error on a stock save; 97 < 384 alpine -> fine.
        Assert.True(line.Over(SaveTarget.StockRf));
        Assert.False(line.Over(SaveTarget.Alpine));
        Assert.Equal(LintSeverity.Error, line.Severity(SaveTarget.StockRf));
        Assert.Equal(LintSeverity.Info, line.Severity(SaveTarget.Alpine));

        LintReport stock = LevelLinter.Lint(rfl, new LintOptions { Target = SaveTarget.StockRf, CheckGeometryLeaks = false });
        Assert.True(stock.HasBlockingIssues);
        Assert.Contains(stock.Blocking, f => f.Category == LintCategory.LimitBudget && f.Message.Contains("Decals"));

        LintReport alpine = LevelLinter.Lint(rfl, new LintOptions { Target = SaveTarget.Alpine, CheckGeometryLeaks = false });
        Assert.False(alpine.HasBlockingIssues);
    }

    [Fact]
    public void Budget_Warns_At_Ninety_Percent()
    {
        // 90 decals of a 96 stock cap = 93.75% -> warning, not error.
        var rfl = NewLevel();
        var decals = new DecalsSection();
        for (int i = 0; i < 90; i++)
        {
            decals.Decals.Add(new Decal { Header = new ObjectHeader { Uid = 2000 + i }, Texture = "d.tga" });
        }

        AddSection(rfl, SectionType.Decals, decals);
        BudgetLine line = LevelBudget.Compute(rfl).Single(b => b.Kind == BudgetKind.Decals);
        Assert.Equal(LintSeverity.Warning, line.Severity(SaveTarget.StockRf));
    }

    // ─── Corpus counts (dm01) ────────────────────────────────────────────────

    [Fact]
    public void Statistics_Match_Known_Corpus_dm01()
    {
        string? path = Corpus.RflFiles.FirstOrDefault(p => Path.GetFileName(p) == "dm01.rfl");
        if (path is null)
        {
            return; // corpus not present in this checkout
        }

        RflFile rfl = RflFile.Load(path);
        LevelStatistics stats = LevelStatisticsBuilder.Compute(rfl);

        // Geometry is present and self-consistent.
        Assert.True(stats.Faces > 0);
        Assert.True(stats.Vertices > 0);
        Assert.True(stats.Rooms > 0);
        Assert.Equal(stats.Rooms, stats.MainRooms + stats.Subrooms);
        Assert.True(stats.LightmapPages > 0);

        BudgetLine lights = stats.Budgets.Single(b => b.Kind == BudgetKind.Lights);
        Assert.Equal(Dm01LightCount(rfl), lights.Count);
        Assert.True(lights.Count > 0);
    }

    private static int Dm01LightCount(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections
            .Select(s => s.Content)
            .OfType<LightsSection>()
            .Where(s => s.Type == SectionType.Lights)
            .Sum(s => s.Lights.Count);
    }

    // ─── Fixture builders ────────────────────────────────────────────────────

    private static RflFile AllDefectsLevel()
    {
        var rfl = NewLevel();

        // Compiled geometry: a single triangle => three open (single-use) edges.
        var geo = new Geometry();
        geo.Textures.Add("wall.tga");
        geo.Vertices.AddRange(new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) });
        var face = new Face { Texture = 0, RoomIndex = 0 };
        face.Vertices.Add(new FaceVertex { Index = 0 });
        face.Vertices.Add(new FaceVertex { Index = 1 });
        face.Vertices.Add(new FaceVertex { Index = 2 });
        geo.Faces.Add(face);
        geo.Rooms.Add(new Room { Id = 0x7FFFFFFE, Aabb = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1)) });
        AddSection(rfl, SectionType.StaticGeometry, new GeometrySection { Geometry = geo });

        // Duplicate UID (two lights at 500).
        AddSection(rfl, SectionType.Lights, new LightsSection(SectionType.Lights)
        {
            Lights =
            {
                new Light { Uid = 500 },
                new Light { Uid = 500 },
            },
        });

        // Triggers: broken link + no-link.
        AddSection(rfl, SectionType.Triggers, new TriggersSection
        {
            Triggers =
            {
                new Trigger { Uid = 501, Links = { 9999 } },
                new Trigger { Uid = 502 },
            },
        });

        // Orphan event (nothing links to it, it links to nothing).
        AddSection(rfl, SectionType.Events, new EventsSection
        {
            Events = { new RflEvent { Uid = 503, ClassName = "Play_Sound" } },
        });

        // Two isolated nav points.
        AddSection(rfl, SectionType.NavPoints, new NavPointsSection
        {
            NavPoints =
            {
                new NavPoint { Uid = 504 },
                new NavPoint { Uid = 505 },
            },
        });

        // 26 ambient sounds -> over the 25 cap.
        var sounds = new AmbientSoundsSection();
        for (int i = 0; i < 26; i++)
        {
            sounds.Sounds.Add(new AmbientSound { Uid = 600 + i });
        }

        AddSection(rfl, SectionType.AmbientSounds, sounds);

        return rfl;
    }

    private static RflFile NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D; // Alpine so Alpine sections serialize
        rfl.Header.LevelName = "mylevel.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static void AddSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        var s = new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true };
        rfl.Sections.Insert(rfl.Sections.Count - 1, s);
    }
}
