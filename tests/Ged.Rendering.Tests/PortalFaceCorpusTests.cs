using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Regression for the "Don't Draw Portal Faces" bug: community maps mark portal
/// faces with a REAL texture index but a nonzero <c>portal_index_plus_2</c>, so the
/// old <c>Texture &lt; 0</c> classification missed them and they kept rendering in
/// the None mode. ctfstockintradeb1.rfl carries 111 such faces (none with texture
/// −1). The scene builder must classify by <see cref="Face.IsPortalFace"/> (either
/// marker), hide them all under None, and emit them all into the right pass.
/// </summary>
public sealed class PortalFaceCorpusTests
{
    private const string LevelName = "ctfstockintradeb1.rfl";

    private static Geometry? LoadStaticGeometry()
    {
        string? path = RenderTestSupport.CorpusFile(LevelName);
        if (path is null)
        {
            return null;
        }

        RflFile file = RflFile.Load(path);
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

    /// <summary>Portal faces that survive the shared emit gates (drawn when not in None mode).</summary>
    private static int ExpectedDrawnPortalFaces(Geometry g) =>
        g.Faces.Count(f => f.IsPortalFace
            && f.Vertices.Count >= 3
            && (f.Flags & (ushort)FaceFlags.IsInvisible) == 0);

    private static int ExpectedPortalTriangles(Geometry g) =>
        g.Faces.Where(f => f.IsPortalFace
            && f.Vertices.Count >= 3
            && (f.Flags & (ushort)FaceFlags.IsInvisible) == 0)
            .Sum(f => f.Vertices.Count - 2);

    private static RflFile Level()
    {
        string path = RenderTestSupport.CorpusFile(LevelName)!;
        return RflFile.Load(path);
    }

    private static RenderScene Build(PortalFaceDrawMode mode) => SceneBuilder.Build(Level(),
        new SceneBuildOptions
        {
            IncludeObjects = false,
            IncludeMovers = false,
            PortalFaces = mode,
        });

    [Fact]
    public void Level_Has_Portal_Faces_Marked_Only_By_PortalIndex()
    {
        Geometry? g = LoadStaticGeometry();
        if (g is null)
        {
            return; // corpus unavailable
        }

        int byPortalIndex = g.Faces.Count(f => f.PortalIndexPlus2 >= 2);
        int byTexture = g.Faces.Count(f => f.Texture < 0);
        int byPredicate = g.Faces.Count(f => f.IsPortalFace);

        Assert.True(byPortalIndex > 0, "expected portal_index_plus_2 portal faces in this level");
        Assert.Equal(0, byTexture);                 // none are marked by texture −1
        Assert.Equal(byPortalIndex, byPredicate);   // the shared predicate catches them all
        Assert.True(ExpectedDrawnPortalFaces(g) > 0);
    }

    [Fact]
    public void None_Mode_Hides_Every_Portal_Face()
    {
        if (LoadStaticGeometry() is null)
        {
            return;
        }

        RenderScene scene = Build(PortalFaceDrawMode.None);
        Assert.DoesNotContain(scene.Batches, b => b.IsPortal);
    }

    [Fact]
    public void SeeThru_Mode_Emits_All_Portal_Faces_In_Alpha_Pass()
    {
        Geometry? g = LoadStaticGeometry();
        if (g is null)
        {
            return;
        }

        RenderScene scene = Build(PortalFaceDrawMode.SeeThru);
        var portalBatches = scene.Batches.Where(b => b.IsPortal).ToList();
        Assert.NotEmpty(portalBatches);
        Assert.All(portalBatches, b => Assert.Equal(RenderPass.Alpha, b.Pass));

        int drawnTriangles = portalBatches.Sum(b => b.Indices.Count) / 3;
        Assert.Equal(ExpectedPortalTriangles(g), drawnTriangles);
    }

    [Fact]
    public void Opaque_Mode_Emits_All_Portal_Faces_In_Opaque_Pass()
    {
        Geometry? g = LoadStaticGeometry();
        if (g is null)
        {
            return;
        }

        RenderScene scene = Build(PortalFaceDrawMode.Opaque);
        var portalBatches = scene.Batches.Where(b => b.IsPortal).ToList();
        Assert.NotEmpty(portalBatches);
        Assert.All(portalBatches, b => Assert.Equal(RenderPass.Opaque, b.Pass));

        int drawnTriangles = portalBatches.Sum(b => b.Indices.Count) / 3;
        Assert.Equal(ExpectedPortalTriangles(g), drawnTriangles);
    }
}
