using System;
using System.Diagnostics;
using System.IO;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The measurement budget (DESIGN-QUALITY-SPEC §(g) Q1): a full grade — every measurement section plus the grader — of a
/// level with up to 10,000 m² of playable floor takes at most 5 s in a Release build (larger levels: 5 s per 10,000 m²). `dotnet test` runs Debug (unoptimized JIT, about 4x
/// slower on this ray-heavy code), whose ceiling is correspondingly generous, as for the QA corpus sweep's bake ceiling;
/// the measured number is always printed. Run serially on an idle machine (TESTING-PROTOCOL Perf stage).
/// </summary>
[Trait("Category", "Perf")]
public sealed class SpatialPerfTests
{
#if DEBUG
    private const double SecondsPer10k = 20.0;
#else
    private const double SecondsPer10k = 5.0;
#endif

    private readonly ITestOutputHelper _out;

    public SpatialPerfTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("ctf06.rfl")] // the largest stock level (about 38,700 m²)
    [InlineData("ctf07.rfl")]
    [InlineData("dm17.rfl")]
    [InlineData("dm01.rfl")]
    public void A_Full_Grade_Takes_At_Most_5_Seconds_Per_10000_Square_Metres(string file)
    {
        if (Corpus.Directory is null || !File.Exists(Path.Combine(Corpus.Directory, file)))
        {
            return;
        }

        SpatialLevel level = SpatialLevel.FromBytes(File.ReadAllBytes(Path.Combine(Corpus.Directory, file)), file);
        SpatialAnalysis.Run(level).Report(); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        SpatialAnalysis a = SpatialAnalysis.Run(level);
        DesignGrader.Grade(a);
        sw.Stop();
        // A level of up to 10,000 m² within the budget; larger levels scale with their floor.
        double limit = SecondsPer10k * Math.Max(1, a.PlayableArea / 10_000);
        _out.WriteLine($"{file}: {a.PlayableArea:0} m², {sw.Elapsed.TotalSeconds:0.00} s (limit {limit:0.0} s)");
        Assert.True(sw.Elapsed.TotalSeconds <= limit, $"{file}: {sw.Elapsed.TotalSeconds:0.00} s for {a.PlayableArea:0} m² (limit {limit:0.0} s)");
    }
}
