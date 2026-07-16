using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>Tests for the undo-integrated brush-editing service and round-trip safety.</summary>
public sealed class BrushEditorTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static int CreateBox(BrushEditor ed, Vec3 pos = default) =>
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, pos, Mat3.Identity);

    [Fact]
    public void CreateBrush_Creates_Section_And_Undo_Removes_It()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        Assert.Empty(ed.Brushes);

        int uid = CreateBox(ed, new Vec3(1, 2, 3));
        Assert.Single(ed.Brushes);
        Assert.NotNull(ed.FindBrush(uid));
        Assert.Contains(doc.Rfl.Sections, s => s.Content is BrushesSection);
        Assert.True(doc.IsDirty);

        doc.Undo.Undo();
        Assert.Empty(ed.Brushes);
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is BrushesSection);

        doc.Undo.Redo();
        Assert.Single(ed.Brushes);
    }

    [Fact]
    public void EditBrushes_Transform_Is_Undoable_Exactly()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        Vec3 before = ed.FindBrush(uid)!.Position;

        ed.EditBrushes(new[] { uid }, "Move", b => { BrushTransform.Move(b, new Vec3(5, 0, 0)); return OpResult.Ok(); });
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(before.Add(new Vec3(5, 0, 0))));

        doc.Undo.Undo();
        Assert.True(ed.FindBrush(uid)!.Position.ApproxEquals(before));
    }

    [Fact]
    public void EditBrushes_Rolls_Back_On_Failure_Without_Undo_Entry()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        int undoDepth = doc.Undo.Position;
        int vertsBefore = ed.FindBrush(uid)!.Geometry.Vertices.Count;

        OpResult r = ed.EditBrushes(new[] { uid }, "Bad op", b =>
        {
            b.Geometry.Vertices.Clear(); // corrupt, then report failure
            return OpResult.Fail("nope");
        });

        Assert.False(r.Success);
        Assert.Equal(undoDepth, doc.Undo.Position); // no entry recorded
        Assert.Equal(vertsBefore, ed.FindBrush(uid)!.Geometry.Vertices.Count); // restored
    }

    [Fact]
    public void Clip_Split_Replaces_One_Brush_With_Two()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        ed.SelectBrush(uid);

        OpResult r = ed.Clip(new Vec3(0, 0, 0), new Vec3(1, 0, 0), ClipMode.Split, flipNormal: false);
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, ed.Brushes.Count);

        doc.Undo.Undo();
        Assert.Single(ed.Brushes);
    }

    [Fact]
    public void Fuse_Merges_Selected_Brushes()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = CreateBox(ed, new Vec3(0, 0, 0));
        int b = CreateBox(ed, new Vec3(2, 0, 0));
        ed.SelectBrush(a);
        ed.SelectBrush(b, additive: true);

        OpResult r = ed.Fuse();
        Assert.True(r.Success, r.Message);
        Assert.Single(ed.Brushes);

        doc.Undo.Undo();
        Assert.Equal(2, ed.Brushes.Count);
    }

    [Fact]
    public void Time_Reorder_Moves_Brush_To_Start_And_End()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = CreateBox(ed);
        int b = CreateBox(ed);
        int c = CreateBox(ed);
        Assert.Equal(2, ed.TimeIndex(c));

        ed.MoveToStartOfTime(new[] { c });
        Assert.Equal(0, ed.TimeIndex(c));
        Assert.Equal(1, ed.TimeIndex(a));

        ed.MoveToEndOfTime(new[] { c });
        Assert.Equal(2, ed.TimeIndex(c));
    }

    [Fact]
    public void Copy_Paste_Yields_Independent_Brush_With_New_Uid()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = CreateBox(ed);
        ed.SelectBrush(uid);
        ed.CopySelected();

        var newUids = ed.Paste(new Vec3(10, 0, 0));
        Assert.Single(newUids);
        Assert.NotEqual(uid, newUids[0]);
        Assert.Equal(2, ed.Brushes.Count);

        // Independence: mutating the paste's geometry must not touch the original.
        Brush original = ed.FindBrush(uid)!;
        Brush pasted = ed.FindBrush(newUids[0])!;
        Assert.NotSame(original.Geometry, pasted.Geometry);
        pasted.Geometry.Vertices[0] = new Vec3(999, 999, 999);
        Assert.DoesNotContain(original.Geometry.Vertices, v => v.ApproxEquals(new Vec3(999, 999, 999)));
    }

    [Fact]
    public void Created_Brush_Serializes_And_Reloads_Intact()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4 },
            new Vec3(1, 2, 3), Mat3.Identity);

        byte[] bytes = doc.SaveToBytes(updateTimestamp: false);
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();
        BrushesSection bs = reloaded.Sections.Select(s => s.Content).OfType<BrushesSection>().Single();

        Assert.Single(bs.Brushes);
        Brush b = bs.Brushes[0];
        Assert.Equal(uid, b.Uid);
        Assert.True(b.Position.ApproxEquals(new Vec3(1, 2, 3)));
        Assert.Equal(6, b.Geometry.Faces.Count);
        Assert.Equal(8, b.Geometry.Vertices.Count);
        // Brush faces carry no lightmap surface, so no phantom lightmap UVs are written.
        Assert.All(b.Geometry.Faces, f => Assert.Equal(-1, f.SurfaceIndex));
        Assert.All(b.Geometry.Faces, f => Assert.All(f.Vertices, v => Assert.Null(v.LightmapCoords)));
    }

    // ---- Round-trip invariant: only the brushes section changes -----------------

    [Fact]
    public void Editing_A_Brush_Changes_Only_The_Brushes_Section()
    {
        string? path = FindCorpusWithBrushes();
        if (path is null)
        {
            return; // corpus unavailable or no brush-bearing level
        }

        var doc = EditorDocument.Open(path);
        var ed = new BrushEditor(doc);
        Assert.NotEmpty(ed.Brushes);

        // Snapshot every non-brushes section's bytes before editing.
        RflFile original = RflFile.Load(path);
        var before = original.Sections
            .Select((s, i) => (i, s.TypeId, Bytes: s.RawBytes))
            .ToList();

        int uid = ed.Brushes[0].Uid;
        ed.SelectBrush(uid);
        ed.EditBrushes(new[] { uid }, "Nudge", b => { BrushTransform.Move(b, new Vec3(0.25f, 0, 0)); return OpResult.Ok(); });

        byte[] saved = doc.SaveToBytes(updateTimestamp: false);
        RflFile reloaded = RflFile.Load(saved);

        Assert.Equal(before.Count, reloaded.Sections.Count);
        bool brushesChanged = false;
        for (int i = 0; i < reloaded.Sections.Count; i++)
        {
            RflSection sec = reloaded.Sections[i];
            byte[] originalBytes = before[i].Bytes;
            if (sec.TypeId == (uint)SectionType.Brushes)
            {
                brushesChanged = !sec.RawBytes.AsSpan().SequenceEqual(originalBytes);
            }
            else
            {
                Assert.True(sec.RawBytes.AsSpan().SequenceEqual(originalBytes),
                    $"Section[{i}] 0x{sec.TypeId:X8} changed but should not have.");
            }
        }

        Assert.True(brushesChanged, "The brushes section should have changed.");
    }

    private static string? FindCorpusWithBrushes()
    {
        if (!Corpus.Available)
        {
            return null;
        }

        foreach (string p in Corpus.RflFiles)
        {
            RflFile f = RflFile.Load(p);
            f.ParseAllKnownSections();
            if (f.Sections.Any(s => s.Content is BrushesSection bs && bs.Brushes.Count > 0))
            {
                return p;
            }
        }

        return null;
    }
}
