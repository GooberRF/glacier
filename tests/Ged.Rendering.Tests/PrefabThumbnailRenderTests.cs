using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Prefabs;
using Ged.Core.Tables;
using Ged.Rendering.Graphics;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Evidence + gate for prefabs: renders a prefab's brush geometry through the
/// offscreen path, saves a .gedprefab package (manifest + payload.rfg + the rendered
/// thumbnail), reloads it, and writes the thumbnail PNG artifact.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class PrefabThumbnailRenderTests
{
    private readonly ITestOutputHelper _out;

    public PrefabThumbnailRenderTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Prefab_Thumbnail_Renders_And_Packages()
    {
        if (RenderTestSupport.RepoRoot is null)
        {
            return;
        }

        if (RenderTestSupport.FixtureFile("tex", "mtl_bluefiller01.tga") is null ||
            RenderTestSupport.FixtureFile("tex", "Disk_P01.tga") is null)
        {
            return; // retail-derived fixtures not present
        }

        // A small textured-brush selection in a document.
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "src.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);
        var editor = new BrushEditor(doc);
        int u1 = editor.CreateBrush(BoxParams("mtl_bluefiller01.tga"), new Vec3(-1.2f, 0f, 0f), Mat3.Identity);
        int u2 = editor.CreateBrush(BoxParams("Disk_P01.tga"), new Vec3(1.2f, 0f, 0f), Mat3.Identity);
        var brushes = new List<Brush> { editor.FindBrush(u1)!, editor.FindBrush(u2)! };

        RfgFile rfg = RfgInterop.Export(doc, new[] { u1, u2 }, System.Array.Empty<int>(), alpine: true);

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out string reason);
        if (gd is null)
        {
            _out.WriteLine($"No GPU device ({reason}); prefab thumbnail render skipped.");
            return;
        }

        var sources = new List<IAssetSource>();
        foreach (string dir in RenderTestSupport.FixtureDirs("tex"))
        {
            sources.Add(new DirectoryAssetSource(dir));
        }

        using var vfs = new AssetVfs(sources.ToArray());

        byte[] thumb = PrefabThumbnail.Render(gd, vfs, brushes, size: 128);
        Assert.True(StbTextureDecoder.IsPng(thumb));
        Assert.True(RenderTestSupport.IsNonTrivial(ToRgba(StbTextureDecoder.Decode(thumb)), out int distinct),
            $"prefab thumbnail was trivial ({distinct} colors).");

        // Package + reload the prefab with the rendered thumbnail.
        string prefabPath = Path.Combine(Path.GetTempPath(), "ged_prefab_" + System.Guid.NewGuid().ToString("N") + PrefabPackage.Extension);
        try
        {
            var manifest = new PrefabManifest { Name = "Two Boxes", BrushCount = 2 };
            PrefabPackage.Save(prefabPath, manifest, rfg, thumb);
            (PrefabManifest hm, byte[]? ht) = PrefabPackage.LoadHeader(prefabPath);
            Assert.Equal("Two Boxes", hm.Name);
            Assert.Equal(thumb, ht);
        }
        finally
        {
            if (File.Exists(prefabPath))
            {
                File.Delete(prefabPath);
            }
        }

        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "prefab_thumb.png"), thumb);
        _out.WriteLine($"prefab thumbnail: {distinct} colors");
    }

    private static BrushCreateParams BoxParams(string texture) => new()
    {
        Shape = BrushShape.Box,
        Width = 2f,
        Height = 2f,
        Depth = 2f,
        Texture = texture,
    };

    private static byte[] ToRgba(DecodedTexture tex)
    {
        var img = tex.Primary;
        var rgba = new byte[img.Width * img.Height * 4];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                (byte r, byte g, byte b, byte a) = img.GetPixel(x, y);
                int o = ((y * img.Width) + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }

        return rgba;
    }
}
