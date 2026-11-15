using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The thin-member reading (<see cref="UvMembers.Measure"/>, uv_report members, grader G21) calibrated on the exemplar
/// study's levels: across the levels with grained members the median reading is near 1 (the grain runs along beams,
/// posts and pilasters), for vertical members too; and a few stock levels stack a strip texture on purpose (dm01's
/// ribbed columns), which is why the reading is reported and not gated. Skipped without the study fixtures.
/// </summary>
[Trait("Category", "DeepGate")]
public sealed class MemberCalibrationTests
{
    private readonly ITestOutputHelper _out;

    public MemberCalibrationTests(ITestOutputHelper output) => _out = output;


    [Fact]
    public void Study_Levels_Lay_Strip_Textures_Along_Their_Beams_Posts_And_Pilasters()
    {
        if (!StudyData.Available || StudyData.TextureSizes is not { } sizes)
        {
            return; // no study fixtures: skipped
        }

        using AssetVfs? vfs = TestPaths.RfInstall is { } install ? GameMount.Mount(install) : null;
        var lookup = new TextureSizeLookup(vfs);
        var along = new List<double>();
        var vertical = new List<double>();
        foreach (string stem in StudyData.Labels.Keys.Where(k => StudyData.LevelPath(k) is not null).OrderBy(k => k, StringComparer.Ordinal))
        {
            (string label, string role, _) = StudyData.Labels[stem];
            if (role is not ("exemplar" or "counter"))
            {
                continue;
            }

            SpatialLevel level = StudyData.Load(StudyData.LevelPath(stem)!);
            JsonObject? byShape = UvMembers.Measure(level.Brushes(), n => sizes(n), null, null);
            JsonObject? byPixels = vfs is null ? null : UvMembers.Measure(level.Brushes(), n => lookup.Get(n), n => lookup.GetGrain(n) ?? TextureGrain.None, null);
            _out.WriteLine($"{label,-12} {stem,-26} strips: {Line(byShape)}   pixels: {Line(byPixels)}");
            if (byShape?["vertical_grain_along"] is { } v)
            {
                vertical.Add(v.GetValue<double>());
            }

            if (byShape?["grain_along"] is { } g)
            {
                along.Add(g.GetValue<double>());
            }
        }

        along.Sort();
        vertical.Sort();
        double median = along[along.Count / 2], vmedian = vertical[vertical.Count / 2];
        _out.WriteLine($"grain along (strip textures): all members median {median:0.000} over {along.Count} levels, vertical members median {vmedian:0.000} over {vertical.Count}");
        Assert.True(median >= 0.95, $"study median grain along {median}");
        Assert.True(vmedian >= 0.95, $"study median vertical grain along {vmedian}");
    }

    private static string Line(JsonObject? m) => m is null
        ? "-"
        : $"members {m["members"]} along {m["grain_along"]} vertical {m["vertical_grain_along"]?.ToString() ?? "-"} ({m["vertical_area"]} m²) no-repeats {m["no_repeats_across"]} mapped {m["mapped"]}";
}
