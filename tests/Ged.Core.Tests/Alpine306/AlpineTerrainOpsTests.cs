using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The terrain operations and reader rules on synthetic data: height mapping growth, resampling, the generator, the
/// reader's rejections, and the geoable lints (ALPINE-COMPAT-SPEC §1.6, §4).
/// </summary>
public sealed class AlpineTerrainOpsTests
{
    [Fact]
    public void Widening_The_Height_Mapping_Keeps_The_Old_Range_And_A_Geoable_Bottom()
    {
        // alpine_terrain.h:403-453: the side that grows gets the need plus 25% of the old range.
        (float min, float range, bool clamped) = TerrainOps.GrowHeightMapping(0f, 10f, -2f, 5f);
        Assert.Equal(-4.5f, min);
        Assert.Equal(14.5f, range);
        Assert.False(clamped);
        Assert.Equal((0f, 10f, false), TerrainOps.GrowHeightMapping(0f, 10f, 1f, 9f));

        // A geoable terrain's bottom (height_min - thickness) stays where it is (:455-472).
        AlpineTerrain t = AlpineTerrainEdit.Create(1, Vec3.Zero, 9, 9, 1f);
        t.Geoable = true;
        t.HeightRange = 10f;
        t.Thickness = 16f;
        float bottom = TerrainChunkEmitter.BottomY(t);
        TerrainOps.SetHeightOffsets(t, (x, z) => x == 0 ? -3 : 2, fit: false);
        Assert.True(t.HeightMin < 0f);
        Assert.Equal(bottom, TerrainChunkEmitter.BottomY(t), 3);
        Assert.Equal(-3, TerrainOps.HeightOffset(t, 0, 4), 2);
        Assert.Equal(2, TerrainOps.HeightOffset(t, 5, 4), 2);
    }

    [Fact]
    public void Resizing_Keeps_The_Corners_And_Normalised_Weights()
    {
        AlpineTerrain t = AlpineTerrainEdit.Create(1, Vec3.Zero, 65, 65, 1f);
        TerrainOps.SetLayers(t, new[] { "a.tga", "b.tga" });
        TerrainOps.SetNormalizedHeights(t, (x, z) => (x + z) / 128.0);
        AlpineTerrainGrid g = t.Grid.Clone();
        int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
        for (int texel = 0; texel < map / 4; texel += 3)
        {
            g.Weights[texel * 4] = 100;
            g.Weights[(texel * 4) + 1] = 155;
        }

        t.Grid = g;
        TerrainOps.Resize(t, 33, 17, 4);
        Assert.Equal((33, 17, 4), (t.Nx, t.Nz, t.WeightResolutionMultiplier));
        // Downsampling box-filters (terrain.cpp:159-198), so the corners land near, not exactly on, the old ones.
        Assert.InRange(t.Grid.Height(0, 0), 0, 1500);
        Assert.InRange(t.Grid.Height(32, 16), 64000, 65535);
        Assert.True(AlpineTerrainEdit.GridConsistent(t.Grid));
        int newMap = AlpineTerrainRules.WeightMapBytes(33, 17, 4);
        for (int texel = 0; texel < newMap / 4; texel++)
        {
            int sum = 0;
            for (int c = 0; c < 4; c++)
            {
                sum += t.Grid.Weights[(texel * 4) + c] + t.Grid.Weights[newMap + (texel * 4) + c];
            }

            Assert.Equal(255, sum);
        }
    }

    [Fact]
    public void The_Generator_Is_Deterministic_And_Shapes_The_Edges()
    {
        var island = new TerrainGenSettings { Seed = 7, Falloff = TerrainEdgeFalloff.Island };
        double[] a = TerrainGenerator.Generate(island, 33, 33, 2f);
        Assert.Equal(a, TerrainGenerator.Generate(island, 33, 33, 2f));
        Assert.NotEqual(a, TerrainGenerator.Generate(island with { Seed = 8 }, 33, 33, 2f));
        Assert.Equal(1.0, a.Max(), 6);
        Assert.Equal(0.0, a.Min(), 6);
        double edge = Enumerable.Range(0, 33).Average(i => a[i]);
        double middle = a[(16 * 33) + 16];
        Assert.True(middle > edge, $"island: middle {middle} should rise above the edge {edge}");
    }

    [Fact]
    public void The_Reader_Rejects_What_Alpine_Rejects_And_Keeps_The_Bytes()
    {
        AlpineTerrain t = AlpineTerrainEdit.Create(5, Vec3.Zero, 33, 33, 1f);
        var section = new AlpineTerrainSection();
        section.Terrains.Add(t);
        byte[] good = AlpineTerrainTests.Bytes(section);

        // A mapping whose length is not the chunk count loads, is kept byte for byte, and counts as unbuilt.
        // (33 x 33 vertices make one chunk; two entries do not match it.)
        t.BuildMapping.Add(new AlpineTerrainChunkMapping(7, 3, 9));
        t.BuildMapping.Add(new AlpineTerrainChunkMapping(8, 3, 9));
        t.NoteBuilt();
        byte[] odd = AlpineTerrainTests.Bytes(section);
        var parsed = (AlpineTerrainSection)AlpineTerrainTests.Parse(odd);
        Assert.Null(parsed.StructureProblem);
        Assert.Equal(odd, AlpineTerrainTests.Bytes(parsed));
        Assert.Equal("it was saved without Build Geometry", TerrainBuildMapping.Resolve(parsed.Terrains, null).Single().Problem);

        // A wrong declared size, an unknown flag bit, or a terrain count past 64: the chunk is kept raw and read-only.
        foreach (byte[] bad in new[] { Patch(good, 4 + 4 + 12 + 2 + 7 + 4 + 4 + 4 + 4 + 3, 0x40), Count(good, 65) })
        {
            var s = (AlpineTerrainSection)AlpineTerrainTests.Parse(bad);
            Assert.True(s.IsReadOnly);
            Assert.NotNull(s.ReadOnlyReason);
            Assert.Equal(bad, AlpineTerrainTests.Bytes(s));
        }
    }

    private static byte[] Patch(byte[] b, int at, byte value)
    {
        byte[] c = (byte[])b.Clone();
        c[at] |= value;
        return c;
    }

    private static byte[] Count(byte[] b, uint count)
    {
        byte[] c = (byte[])b.Clone();
        BitConverter.GetBytes(count).CopyTo(c, 0);
        return c;
    }

    [Fact]
    public void Geoable_Terrain_Lints_Mirror_Alpine_REDs_Build_Reports()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15));
        LevelObject obj = TerrainOps.Create(doc, Vec3.Zero, 33, 33, 2f, "geo");
        TerrainOps.Apply(doc, obj, "geoable", t =>
        {
            t.Geoable = true;
            t.Thickness = 4f;
        });
        doc.PlaceObject(LevelObjectKind.GeoRegion, new Vec3(0, 0, 0));
        var messages = LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings.Select(f => f.Message).ToList();
        Assert.Contains(messages, m => m.Contains("RF2-style geomod is off", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("overlaps Geo Region", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("craters may punch through", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("has no compiled geometry", StringComparison.Ordinal));
    }
}
