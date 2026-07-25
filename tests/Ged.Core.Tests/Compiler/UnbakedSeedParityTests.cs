using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Fix-4 parity: an unbaked build seeds a room-with-no-ambient fragment from the LEVEL ambient
/// (halved to the bake's ambient floor, == AmbientField.ForRoom + the Lightmapper's amb×0.5),
/// matching how RED seeds the room/level ambient into unbaked fragments — instead of the old flat
/// 64 grey. Baked builds overwrite the seed, so this only affects a no-bake/preview save. The seed
/// rect sampled here is exactly the rect the bake would overwrite (SeedTexels writes the surface's
/// x/y/w/h), so the stock-bake byte-identity gates are unaffected.
/// </summary>
public sealed class UnbakedSeedParityTests
{
    private static (int R, int G, int B) FirstSeedTexel(CompiledLevel c)
    {
        Surface s = c.Geometry.Surfaces[0];
        Lightmap page = c.Lightmaps[s.LightmapIndex];
        int o = ((s.Y * page.Width) + s.X) * 3;
        return (page.Pixels[o], page.Pixels[o + 1], page.Pixels[o + 2]);
    }

    [Fact]
    public void Unbaked_Seed_Uses_Level_Ambient_When_The_Room_Has_None()
    {
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, new Vec3(0, 0, 0), 8, 8, 8) };

        CompiledLevel c = GeometryCompiler.Compile(brushes, null,
            new CompileOptions { BuildSurfaces = true, LevelAmbient = new RfColor(40, 40, 40, 255) });

        // 40 >> 1 == 20 (the same ambient×0.5 floor the Lightmapper bakes with zero lights),
        // not the old flat 64.
        Assert.Equal((20, 20, 20), FirstSeedTexel(c));
    }

    [Fact]
    public void Unbaked_Seed_Falls_Back_To_Neutral_Grey_Without_A_Level_Ambient()
    {
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, new Vec3(0, 0, 0), 8, 8, 8) };

        CompiledLevel c = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = true });

        // No level ambient supplied (e.g. a synthetic build) keeps the historical neutral grey.
        Assert.Equal((64, 64, 64), FirstSeedTexel(c));
    }
}
