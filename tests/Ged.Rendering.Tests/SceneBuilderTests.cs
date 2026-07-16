using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Scene-build logic against a real corpus level. Skips gracefully (passes) when
/// the untracked corpus is absent.
/// </summary>
public sealed class SceneBuilderTests
{
    private static RflFile? LoadDm01()
    {
        string? path = RenderTestSupport.CorpusFile("dm01.rfl");
        return path is null ? null : RflFile.Load(path);
    }

    private static Geometry? StaticGeometry(RflFile file)
    {
        file.ParseAllKnownSections();
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                return g.Geometry;
            }
        }

        return null;
    }

    [Fact]
    public void Build_ProducesBatchesAndPreservesVertexCounts()
    {
        RflFile? file = LoadDm01();
        if (file is null)
        {
            return;
        }

        var options = new SceneBuildOptions { IncludeMovers = false, IncludeObjects = false };
        RenderScene scene = SceneBuilder.Build(file, options);
        Geometry geo = StaticGeometry(file)!;

        // Independently recompute the expected vertex total under the same
        // default inclusion rules: skip portal faces (EITHER marker — texture -1 or
        // portal_index_plus_2 >= 2), invisible and degenerate faces; keep detail faces.
        int expected = 0;
        foreach (Face f in geo.Faces)
        {
            if (f.IsPortalFace)
            {
                continue;
            }

            if (((FaceFlags)f.Flags & FaceFlags.IsInvisible) != 0)
            {
                continue;
            }

            if (f.Vertices.Count < 3)
            {
                continue;
            }

            expected += f.Vertices.Count;
        }

        Assert.NotEmpty(scene.Batches);
        Assert.True(expected > 0);
        Assert.Equal(expected, scene.TotalVertexCount);
        Assert.True(scene.TotalTriangleCount > 0);
    }

    [Fact]
    public void Build_PreservesLightmapUvsExactly()
    {
        RflFile? file = LoadDm01();
        if (file is null)
        {
            return;
        }

        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions { IncludeMovers = false, IncludeObjects = false });
        Geometry geo = StaticGeometry(file)!;

        // Find the first renderable face that carries lightmap UVs and locate its
        // first vertex (by pool position) in the built scene, then compare UVs.
        Face? sample = null;
        FaceVertex? sampleVertex = null;
        foreach (Face f in geo.Faces)
        {
            if (f.IsPortalFace || ((FaceFlags)f.Flags & FaceFlags.IsInvisible) != 0 || f.Vertices.Count < 3)
            {
                continue;
            }

            FaceVertex fv0 = f.Vertices[0];
            if (fv0.LightmapCoords is not null)
            {
                sample = f;
                sampleVertex = fv0;
                break;
            }
        }

        if (sample is null || sampleVertex is null)
        {
            // Level has no lightmaps; nothing to assert.
            return;
        }

        Vec3 pool = geo.Vertices[sampleVertex.Index];
        Uv expected = sampleVertex.LightmapCoords!.Value;

        bool found = false;
        foreach (GeometryBatch batch in scene.Batches)
        {
            if (!batch.HasLightmap)
            {
                continue;
            }

            foreach (WorldVertex v in batch.Vertices)
            {
                if (Near(v.Position.X, pool.X) && Near(v.Position.Y, pool.Y) && Near(v.Position.Z, pool.Z) &&
                    Near(v.LightmapCoord.X, expected.U) && Near(v.LightmapCoord.Y, expected.V))
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                break;
            }
        }

        Assert.True(found, "The face's lightmap UVs should pass through to a scene vertex unchanged.");
    }

    [Fact]
    public void Build_EmitsBillboardsForObjects()
    {
        RflFile? file = LoadDm01();
        if (file is null)
        {
            return;
        }

        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());
        // Retail DM levels always have respawn points / items / player start, so
        // at least some point-object billboards must be emitted.
        Assert.NotEmpty(scene.Billboards);
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;
}
