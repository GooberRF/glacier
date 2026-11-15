using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The function checks' calibration gate (G27-G31, the intent layer): on every exemplar of the study, measured with the
/// product defaults, no function check fails (their limits are the exemplars' own range), and the function report is
/// written for review (bridges with their route catchments, mismatched and scattered rock props, orphan cover,
/// landmarks, flights of stairs and ramps with their faults, route ledges). Counter-examples and generated levels may fail them. Skipped without the local study data.
/// </summary>
[Trait("Category", "DeepGate")]
public sealed class PurposeCalibrationTests
{
    private static readonly string[] FunctionChecks = { "G27", "G28", "G29", "G30", "G31" };

    private readonly ITestOutputHelper _out;

    public PurposeCalibrationTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object?[]> AllLevels() => StudyParityTests.AllLevels();

    [Theory]
    [MemberData(nameof(AllLevels))]
    public void Exemplars_Pass_The_Function_Checks(string? stem)
    {
        if (stem is null)
        {
            return;
        }

        (string label, string role, _) = StudyData.Labels[stem];
        SpatialAnalysis a = SpatialAnalysis.Run(StudyData.Load(StudyData.LevelPath(stem)!));
        JsonObject p = a.Purpose(200);
        _out.WriteLine($"{label} {role} {stem}: bridges {p["bridges_checked"]} purposeless {p["purposeless_bridges"]} props {p["props"]} free {p["free_standing_props"]} mismatched {p["mismatched_props"]} scattered {p["scattered_rocks"]} cover {p["cover_pieces"]} orphan {p["orphan_cover"]} frac {p["orphan_cover_fraction"]} lm {p["landmarks_checked"]} hidden {p["hidden_landmarks"]}");
        foreach (JsonNode? b in p["bridges"]!.AsArray())
        {
            _out.WriteLine($"  BRIDGE {b!["pos"]!.ToJsonString()} span {b["span"]} A {b["catchment_a"]} B {b["catchment_b"]} detour {b["detour"]} ok {b["catchment_ok"]} fault {b["fault"]}");
        }

        foreach (JsonNode? m in p["mismatched"]!.AsArray().Concat(p["orphans"]!.AsArray()))
        {
            _out.WriteLine($"  {m!["fault"]} {m["pos"]!.ToJsonString()} h {m["height"]} rock {m["rock_share"]} ctx {m["context"]} clr {m["clearance"]} lane {m["lane"]}");
        }

        _out.WriteLine($"  flights {p["flights_checked"]} purposeless {p["purposeless_flights"]} noted {p["flights_noted"]} route ledges {p["route_ledges"]}");
        foreach (JsonNode? f in p["flights"]!.AsArray().Where(f => f!["fault"] is not null))
        {
            _out.WriteLine($"  FLIGHT {f!["pos"]!.ToJsonString()} rise {f["rise"]} counted {f["counted"]} {f["kind"]}: {f["fault"]}");
        }

        foreach (JsonNode? l in p["ledges"]!.AsArray())
        {
            _out.WriteLine($"  LEDGE {l!["pos"]!.ToJsonString()} step {l["step"]} landings {l["low_landing"]}/{l["high_landing"]}");
        }

        foreach (JsonNode? m in p["landmarks"]!.AsArray())
        {
            _out.WriteLine($"  LANDMARK {m!["pos"]!.ToJsonString()} h {m["height"]} area {m["seen_from_area"]} spaces {m["seen_from_spaces"]}");
        }

        // The function checks on the purpose metrics alone (the rest of the grade is the study gate's business).
        var report = new JsonObject { ["purpose"] = p.DeepClone() };
        SortedDictionary<string, double?> metrics = DesignMetrics.Extract(report);
        var failed = new List<string>();
        foreach (JsonNode? c in DesignGrader.Table["checks"]!.AsArray())
        {
            string id = c!["id"]!.GetValue<string>();
            if (FunctionChecks.Contains(id) && DesignGrader.Evaluate(c["rule"]!, metrics) == false)
            {
                failed.Add(id);
            }
        }

        _out.WriteLine($"  function checks failing: {(failed.Count == 0 ? "none" : string.Join(",", failed))}");
        if (role == "exemplar")
        {
            Assert.True(failed.Count == 0, $"{label} is an exemplar but fails {string.Join(", ", failed)}");
        }
    }
}
