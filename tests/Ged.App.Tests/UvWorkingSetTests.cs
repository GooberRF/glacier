using System;
using System.Collections.Generic;
using System.Linq;
using Ged.App.Dialogs;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 3b(a) — VERIFY the UV Unwrap working set loads EVERY selected face (across brushes) into its
/// own ring partitioning the shared UV list. This is the data the canvas draws and every edit op
/// works over, so proving it multi-face-complete here backs the "draws all faces" audit.
/// </summary>
public sealed class UvWorkingSetTests
{
    [Fact]
    public void Build_Loads_Every_Selected_Face_Into_Its_Own_Ring()
    {
        BrushEditor ed = NewEditor();
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "wall.tga" }, default, Mat3.Identity);
        ed.SetMode(EditMode.Face);
        ed.SelectFace(uid, 0);
        ed.SelectFace(uid, 2, additive: true);
        ed.SelectFace(uid, 4, additive: true);

        UvWorkingSet.Data data = UvWorkingSet.Build(ed);

        Assert.Equal(3, data.Rings.Count);              // one ring per selected face
        Assert.Equal(3, data.Faces.Count);
        Assert.Equal(data.Uvs.Count, data.Refs.Count);  // one back-reference per corner UV

        // The rings partition the UV list exactly: every index covered once, nothing shared.
        List<int> all = data.Rings.SelectMany(r => r).OrderBy(i => i).ToList();
        Assert.Equal(Enumerable.Range(0, data.Uvs.Count).ToList(), all);
        Assert.Equal(data.Uvs.Count, data.Rings.Sum(r => r.Count));

        // Every loaded face records its brush, face index and texture.
        Assert.All(data.Faces, f => Assert.Equal(uid, f.BrushUid));
        Assert.Equal(new[] { 0, 2, 4 }, data.Faces.Select(f => f.FaceIndex).ToArray());
        Assert.All(data.Faces, f => Assert.Equal("wall.tga", f.Texture));
        Assert.Equal("wall.tga", data.FirstTexture);
    }

    [Fact]
    public void Build_Spans_Multiple_Brushes_With_Mixed_Textures()
    {
        BrushEditor ed = NewEditor();
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "a.tga" }, default, Mat3.Identity);
        int b = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "b.tga" }, default, Mat3.Identity);
        ed.SetMode(EditMode.Face);
        ed.SelectFace(a, 1);
        ed.SelectFace(b, 3, additive: true);

        UvWorkingSet.Data data = UvWorkingSet.Build(ed);

        Assert.Equal(2, data.Rings.Count);
        HashSet<(int, int)> pairs = data.Faces.Select(f => (f.BrushUid, f.FaceIndex)).ToHashSet();
        Assert.Contains((a, 1), pairs);
        Assert.Contains((b, 3), pairs);

        // Mixed textures are captured per face (the readout surfaces this even though the backdrop
        // shows only the first face's texture).
        List<string?> textures = data.Faces.Select(f => f.Texture).ToList();
        Assert.Contains("a.tga", textures);
        Assert.Contains("b.tga", textures);
    }

    private static BrushEditor NewEditor()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "t.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new BrushEditor(new EditorDocument(rfl));
    }
}
