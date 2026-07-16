using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Records the full-bake timing on the largest corpus level and the before/after
/// atlas page counts (per-face vs grouped surfaces) for dm01/dm04 to
/// tests/artifacts/lighting/bake_perf.txt. Not a hard perf gate (CI machines
/// vary); it asserts only that a bake completes and reports the numbers.
/// </summary>
[Trait("Category", "Perf")] // load-sensitive wall-clock bake gate; quarantined (docs/internal/TESTING-PROTOCOL.md)
public sealed class LightBakePerfTests
{
    private readonly ITestOutputHelper _out;

    public LightBakePerfTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Bake_Timing_And_Page_Counts()
    {
        if (Corpus.Directory is null || TestPaths.RepoRoot is null)
        {
            return;
        }

        var sb = new StringBuilder();
        AssetVfs? vfs = TestPaths.RfInstall is { } ins ? GameMount.Mount(ins) : null;
        Func<string, TextureTraits?>? traits = vfs is null ? null : new TextureTraitsCache(vfs).Get;

        try
        {
            // Page counts: per-face vs grouped, for two levels.
            foreach (string name in new[] { "dm01.rfl", "dm04.rfl" })
            {
                string path = Path.Combine(Corpus.Directory, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                CompiledLevel perFace = GeometryBuildService.Build(RflFile.Load(path),
                    new CompileOptions { GroupSurfaces = false, TextureTraits = traits });
                CompiledLevel grouped = GeometryBuildService.Build(RflFile.Load(path),
                    new CompileOptions { GroupSurfaces = true, TextureTraits = traits });
                sb.AppendLine($"{name}: surfaces {perFace.Report.Surfaces}->{grouped.Report.Surfaces}, " +
                              $"pages {perFace.Report.LightmapPages}->{grouped.Report.LightmapPages}");
            }

            // Bake timing on the largest available level.
            foreach (string name in new[] { "ctf07.rfl", "ctf06.rfl", "dmabruptdecayrc2a27.rfl", "dm04.rfl", "dm01.rfl" })
            {
                string path = Path.Combine(Corpus.Directory, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                RflFile rfl = RflFile.Load(path);
                var sw = Stopwatch.StartNew();
                CompiledLevel baked = GeometryBuildService.Build(rfl, new CompileOptions
                {
                    GroupSurfaces = true,
                    BakeLighting = true,
                    TextureTraits = traits,
                });
                sw.Stop();
                var bs = baked.BakeStats!;
                sb.AppendLine($"{name}: full compile+bake {baked.Report.ElapsedMs:F0} ms " +
                              $"({bs.Lights} lights, {baked.Report.Surfaces} surfaces, {bs.Texels} texels, " +
                              $"max {bs.MaxLightsOnAnyFace} lights/face, {baked.Report.LightmapPages} pages)");
                Assert.True(sw.Elapsed.TotalSeconds < 30, $"{name} bake took {sw.Elapsed.TotalSeconds:F1}s");
                break; // largest available only
            }

            string outDir = Path.Combine(TestPaths.RepoRoot, "tests", "artifacts", "lighting");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "bake_perf.txt"), sb.ToString());
            _out.WriteLine(sb.ToString());
        }
        finally
        {
            vfs?.Dispose();
        }
    }
}
