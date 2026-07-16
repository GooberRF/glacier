using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Tests.Compiler;
using Xunit;

namespace Ged.Core.Tests.Lighting;

public sealed class LevelLightingTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    private static (Geometry G, List<Lightmap> Pages) CompileRoom()
    {
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 12, 8, 12) };
        CompiledLevel c = GeometryCompiler.Compile(brushes);
        return (c.Geometry, c.Lightmaps);
    }

    private static Light PointLight(int uid, Vec3 pos, float range, byte lum = 255) => new()
    {
        Uid = uid,
        Position = pos,
        Rotation = Mat3.Identity,
        Flags = 0x8 | (1u << 4) | (2u << 8), // enabled, omni, state=on
        Color = new RfColor(lum, lum, lum, 255),
        Range = range,
        OnIntensity = 1f,
        DropoffType = 0,
    };

    private static long Sum(List<Lightmap> pages)
    {
        long s = 0;
        foreach (Lightmap p in pages)
        {
            foreach (byte b in p.Pixels)
            {
                s += b;
            }
        }

        return s;
    }

    [Fact]
    public void Full_Bake_Lights_The_Room()
    {
        (Geometry g, List<Lightmap> template) = CompileRoom();
        var lights = new List<Light> { PointLight(100, V(0, 0, 0), 20f) };
        List<Lightmap> pages = LevelLighting.FreshPages(template);

        BakeStats stats = LevelLighting.BakeInto(g, pages, lights, new RfColor(0, 0, 0, 255), new LightingOptions());

        Assert.Equal(g.Surfaces.Count, stats.Surfaces);
        Assert.Equal(1, stats.Lights);
        Assert.True(Sum(pages) > 0, "a light in the room should raise texels above zero ambient");
    }

    [Fact]
    public void Incremental_Region_Only_Touches_Overlapping_Surfaces()
    {
        (Geometry g, List<Lightmap> template) = CompileRoom();
        var lights = new List<Light> { PointLight(100, V(0, 0, 0), 20f) };

        // A region far outside the room overlaps no surface → nothing is baked.
        List<Lightmap> outside = LevelLighting.FreshPages(template);
        var far = new Aabb(V(1000, 1000, 1000), V(1001, 1001, 1001));
        LevelLighting.BakeInto(g, outside, lights, new RfColor(0, 0, 0, 255), new LightingOptions(), far);
        Assert.Equal(0, Sum(outside));

        // A region covering the whole room matches the full bake.
        List<Lightmap> whole = LevelLighting.FreshPages(template);
        var all = new Aabb(V(-100, -100, -100), V(100, 100, 100));
        LevelLighting.BakeInto(g, whole, lights, new RfColor(0, 0, 0, 255), new LightingOptions(), all);

        List<Lightmap> full = LevelLighting.FreshPages(template);
        LevelLighting.BakeInto(g, full, lights, new RfColor(0, 0, 0, 255), new LightingOptions());
        Assert.Equal(Sum(full), Sum(whole));
    }
}
