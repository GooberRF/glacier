using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Pins RED's surface-binding rule: sky, invisible, liquid and full-bright faces
/// carry NO lightmap surface (surface_index = -1). Verified against 12 corpus
/// levels' original compiles (0 bound of 185 sky / 1084 invisible / 2 liquid /
/// 128 full-bright faces). Binding a baked surface to a sky face was the
/// dm01 regression: the sky texture rendered modulated by a near-black lightmap.
/// </summary>
public sealed class SurfaceBindingRuleTests
{
    private const FaceFlags Unbindable =
        FaceFlags.ShowSky | FaceFlags.IsInvisible | FaceFlags.LiquidSurface | FaceFlags.FullBright;

    [Fact]
    public void Red_Corpus_Never_Binds_Surfaces_To_Excluded_Faces()
    {
        if (Corpus.Directory is null)
        {
            return;
        }

        foreach (string name in new[] { "dm01.rfl", "dm04.rfl", "glass_house.rfl", "ctf01.rfl" })
        {
            string path = Path.Combine(Corpus.Directory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            RflFile rfl = RflFile.Load(path);
            rfl.ParseAllKnownSections();
            Geometry g = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
            foreach (Face f in g.Faces)
            {
                if (f.Texture < 0 || ((FaceFlags)f.Flags & Unbindable) == 0)
                {
                    continue;
                }

                Assert.True(f.SurfaceIndex < 0 || (f.SurfaceIndex & 0xFFFF) == 0xFFFF,
                    $"{name}: RED bound a surface to face flags 0x{f.Flags:X}");
            }
        }
    }

    [Fact]
    public void Ged_Compile_Never_Binds_Surfaces_To_Excluded_Faces()
    {
        if (Corpus.Directory is null)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory, "dm01.rfl");
        RflFile rfl = RflFile.Load(path);
        CompiledLevel c = GeometryBuildService.Build(rfl, new CompileOptions());
        Geometry g = c.Geometry;

        int excluded = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || ((FaceFlags)f.Flags & Unbindable) == 0)
            {
                continue;
            }

            excluded++;
            Assert.True(f.SurfaceIndex < 0 || (f.SurfaceIndex & 0xFFFF) == 0xFFFF,
                $"GED bound a surface to face flags 0x{f.Flags:X}");
        }

        Assert.True(excluded > 0, "dm01 should contain sky/invisible faces to exercise the rule");
    }
}
