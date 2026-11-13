using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// A Payload level is configurable entirely from Lua (and so from MCP's <c>run_lua</c>): the payload rules and Legacy
/// Movers off on <c>level.alpine</c>, a Payload_Handler linked to the cart's moving group, Payload_Checkpoint events
/// named by cart keyframes and linked to team respawns. The configuration pattern is the one the owner's functional
/// reference uses (<c>research/fixtures/payload/pay_test01aa.rfl</c>: one handler at the cart linked to its moving
/// group, checkpoints each named by a cart keyframe and linked to 8 respawns, 4 red and 4 blue; One Way cart). This is
/// configuration only — the route below is not a layout.
/// </summary>
public sealed class PayloadSetupScriptTests
{
    private const string Script = """
        level.alpine:set_many{ legacy_movers = false, payload_push_speed = 2, payload_checkpoint_time_bonus = 120 }

        -- The track: the cart's start keyframe, then one keyframe per corner (world positions, metres).
        local route = { {0, 0.5, 0}, {0, 0.5, 16}, {12, 0.5, 28}, {12, 0.5, 48} }
        local body = level.brush{ pos = route[1], size = {2, 1, 3}, detail = true }
        local cart = level.create_mover{ name = "payload cart", brushes = { body },
                                         props = { movement_type = "One Way" } }
        for i = 2, #route do
          cart:add_keyframe{ pos = route[i], props = { travel_time_to_next = 8, return_travel = 8 } }
        end

        level.place{ kind = "event", class = "Payload_Handler", name = "cart", pos = {0, 2, 0},
                     props = { attacking_team = "Blue", push_radius = 5, rollback_delay = 30 },
                     links = { cart } }

        -- Each checkpoint is named by a cart keyframe and links the respawns it activates.
        for n, k in ipairs({ 3, 4 }) do
          local kf = cart.keyframes[k]
          local cp = level.place{ kind = "event", class = "Payload_Checkpoint", pos = { kf.pos.x, 2, kf.pos.z },
                                  props = { checkpoint_name = "Checkpoint " .. string.char(64 + n) } }
          kf:set("triggered_event_uid", cp.uid)
          local spawns = {}
          for j = 1, 8 do
            local red = j <= 4
            spawns[j] = level.place{ kind = "respawn", pos = { kf.pos.x + (red and -6 or 6), 0, kf.pos.z + j },
                                     props = { red_team = red, blue_team = not red } }
          end
          assert(cp:link_many(spawns).ok)
        end
        """;

    [Fact]
    public void A_Payload_Level_Configured_From_Lua_Matches_The_Reference_Pattern_Lints_Clean_And_Saves_As_V306()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, Script);
        Assert.True(r.Success, r.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries.Select(e => e.Message)));

        AssertPayloadPattern(doc.Rfl, expectedCheckpoints: 2);

        AlpineLevelPropertiesSection props = Props(doc.Rfl);
        Assert.Equal(0, props.LegacyMovers);
        Assert.Equal(2f, props.PayloadPushSpeed);
        Assert.Equal(120f, props.PayloadCheckpointTimeBonus);
        Assert.Equal(0.5f, props.PayloadRollbackSpeed); // untouched rules keep Alpine's defaults

        RflEvent handler = Events(doc.Rfl).Single(e => e.ClassName == "Payload_Handler");
        Assert.Equal(0, handler.Int1); // attacking team: Blue (dropdown index 0)
        Assert.Equal(5f, handler.Float1);
        Assert.Equal(30, handler.Int2);
        Assert.Equal(new[] { "Checkpoint A", "Checkpoint B" },
            Events(doc.Rfl).Where(e => e.ClassName == "Payload_Checkpoint").Select(e => e.Str1).ToArray());

        Assert.DoesNotContain(LevelLinter.Lint(doc.Rfl).Findings,
            f => f.Category is LintCategory.AlpinePrerequisite or LintCategory.BrokenLink or LintCategory.DuplicateUid);

        string path = Path.Combine(Path.GetTempPath(), $"ged-payload-{Guid.NewGuid():N}.rfl");
        try
        {
            doc.Save(path);
            RflFile saved = RflFile.Load(path);
            saved.ParseAllKnownSections();
            Assert.Equal(306, saved.Header.Version);
            Assert.Equal(AlpineLevelPropertiesSection.CurrentVersion, Props(saved).Version);
            Assert.Equal(0, Props(saved).LegacyMovers);
            Assert.Equal(2f, Props(saved).PayloadPushSpeed);
            AssertPayloadPattern(saved, expectedCheckpoints: 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Payload_Prerequisite_Lints_Follow_Alpines_Cart_Checks()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, Script + """

            level.alpine:set("legacy_movers", true)
            level.find_mover("payload cart"):set("movement_type", "Ping Pong Once")
            local stray = level.place{ kind = "event", class = "Payload_Checkpoint", pos = {40, 2, 40} }
            """);
        Assert.True(r.Success, r.Error?.ToDisplayString());

        string[] messages = LevelLinter.Lint(doc.Rfl).Findings
            .Where(f => f.Category == LintCategory.AlpinePrerequisite)
            .Select(f => f.Message)
            .ToArray();
        Assert.Contains(messages, m => m.Contains("needs Legacy Movers off", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("must be One Way", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("is not on the cart's track", StringComparison.Ordinal));
        Assert.Equal(3, messages.Length);
    }

    /// <summary>The owner's functional reference follows the same pattern (skips when the research fixture is absent).</summary>
    [Fact]
    public void The_Reference_Payload_Level_Has_The_Same_Configuration_Pattern()
    {
        string? path = TestPaths.ResearchFixtures is { } dir ? Path.Combine(dir, "payload", "pay_test01aa.rfl") : null;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        AssertPayloadPattern(rfl, expectedCheckpoints: 3);
        Assert.Equal(24, rfl.Sections.Select(s => s.Content).OfType<MpRespawnPointsSection>().Sum(s => s.Points.Count));
        Assert.DoesNotContain(LevelLinter.Lint(rfl).Findings,
            f => f.Category is LintCategory.AlpinePrerequisite or LintCategory.BrokenLink or LintCategory.DuplicateUid);
    }

    /// <summary>
    /// The payload configuration the game needs (game_patch/multi/payload.cpp:218-330, 1016-1066) and the reference
    /// uses: Legacy Movers off; one Payload_Handler linked (only) to a One Way, non-rotating moving group's start
    /// keyframe; every Payload_Checkpoint named by exactly one non-start cart keyframe and linked to 8 respawns, 4 per team.
    /// </summary>
    private static void AssertPayloadPattern(RflFile rfl, int expectedCheckpoints)
    {
        Assert.Equal(0, Props(rfl).LegacyMovers);

        RflEvent handler = Assert.Single(Events(rfl), e => e.ClassName == "Payload_Handler");
        Group cart = Assert.Single(MovingGroups(rfl), g => g.MovingData!.Keyframes[0].Uid == Assert.Single(handler.Links));
        MovingGroupData motion = cart.MovingData!;
        Assert.Equal(1, motion.MovementType); // One Way
        Assert.Equal(0, motion.RotateInPlace);
        Assert.NotEmpty(cart.Brushes);

        List<RflEvent> checkpoints = Events(rfl).Where(e => e.ClassName == "Payload_Checkpoint").ToList();
        Assert.Equal(expectedCheckpoints, checkpoints.Count);
        var respawns = rfl.Sections.Select(s => s.Content).OfType<MpRespawnPointsSection>()
            .SelectMany(s => s.Points).ToDictionary(p => p.Uid);
        foreach (RflEvent cp in checkpoints)
        {
            int namedBy = Assert.Single(Enumerable.Range(0, motion.Keyframes.Count), i => motion.Keyframes[i].EventUid == cp.Uid);
            Assert.True(namedBy > 0, "a checkpoint is never named by the cart's start keyframe");

            Assert.Equal(8, cp.Links.Count);
            List<MpRespawnPoint> linked = cp.Links.Select(uid => respawns[uid]).ToList();
            Assert.Equal(4, linked.Count(p => p.RedTeam != 0 && p.BlueTeam == 0));
            Assert.Equal(4, linked.Count(p => p.BlueTeam != 0 && p.RedTeam == 0));
        }
    }

    private static AlpineLevelPropertiesSection Props(RflFile rfl) =>
        rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();

    private static IEnumerable<RflEvent> Events(RflFile rfl) =>
        rfl.Sections.Select(s => s.Content).OfType<EventsSection>().SelectMany(s => s.Events);

    private static IEnumerable<Group> MovingGroups(RflFile rfl) =>
        rfl.Sections.Select(s => s.Content).OfType<GroupsSection>()
            .Where(s => s.Type == SectionType.MovingGroups)
            .SelectMany(s => s.Groups)
            .Where(g => g.MovingData is { Keyframes.Count: > 0 });
}
