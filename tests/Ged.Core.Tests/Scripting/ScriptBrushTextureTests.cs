using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The scripted white-brush regression (repeat-class bug, see DefaultBrushTextureGuardTests for
/// the interactive half): <c>level.place_box</c> must route its default textures through the SAME
/// guard the Draw Brush tool uses — the editor's configured per-orientation defaults resolved
/// against the mounted VFS, dead/blank names falling back to the stock rock default — and every
/// face must carry a VALID texture-table index (an out-of-range/-1 index or a dead name is
/// exactly what renders as the white fallback).
/// </summary>
public sealed class ScriptBrushTextureTests
{
    /// <summary>A minimal in-memory VFS source containing exactly the given file names.</summary>
    private sealed class FakeSource : IAssetSource
    {
        private readonly HashSet<string> _files;

        public FakeSource(params string[] files) =>
            _files = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        public string Description => "fake";

        public AssetSourceKind Kind => AssetSourceKind.LooseDirectory;

        public string? Category => null;

        public bool Contains(string name) => _files.Contains(name);

        public byte[]? Read(string name) => _files.Contains(name) ? Array.Empty<byte>() : null;

        public long? GetSize(string name) => _files.Contains(name) ? 0L : null;

        public IEnumerable<string> EnumerateFiles() => _files;

        public void Rescan()
        {
        }
    }

    private static ScriptContext Context(EditorDocument doc, AssetVfs? vfs = null,
        string? floor = null, string? wall = null, string? ceiling = null)
    {
        var services = new ScriptServices
        {
            Document = doc,
            Assets = vfs,
            Confirmation = new AllowAllConfirmation(),
            DefaultFloorTexture = floor,
            DefaultWallTexture = wall,
            DefaultCeilingTexture = ceiling,
        };
        return new ScriptContext(services, new ScriptRunOptions { AllowDestructive = true }, new ScriptLog());
    }

    /// <summary>Every face must index a live texture-table entry; returns the per-face names.</summary>
    private static List<string> FaceTextures(ScriptContext ctx, int uid)
    {
        Brush? b = ctx.Brushes.FindBrush(uid);
        Assert.NotNull(b);
        Geometry g = b!.Geometry;
        var names = new List<string>();
        foreach (Face f in g.Faces)
        {
            Assert.InRange(f.Texture, 0, g.Textures.Count - 1); // the white-render regression class
            names.Add(g.Textures[f.Texture]);
        }

        return names;
    }

    [Fact]
    public void PlaceBox_Without_Tex_Applies_The_Stock_Default_With_Valid_Indices()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        ScriptContext ctx = Context(doc);

        int uid = ctx.Level.PlaceBox(0, 0, 0, 4, 4, 4).Uid;

        List<string> names = FaceTextures(ctx, uid);
        Assert.Equal(6, names.Count);
        Assert.All(names, n => Assert.Equal(BrushCreateParams.DefaultTexture, n));
    }

    [Fact]
    public void PlaceBox_Uses_The_Editors_Configured_Defaults_When_They_Resolve()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        using var vfs = new AssetVfs(new[] { new FakeSource("my_floor.tga", "my_wall.tga", "my_ceiling.tga", "Rck_Default.tga") });
        ScriptContext ctx = Context(doc, vfs, floor: "my_floor.tga", wall: "my_wall.tga", ceiling: "my_ceiling.tga");

        int uid = ctx.Level.PlaceBox(0, 0, 0, 4, 4, 4).Uid;

        // A box has 1 up face (floor), 1 down face (ceiling), 4 walls — the configured
        // per-orientation defaults must land exactly like a Draw Brush box.
        List<string> names = FaceTextures(ctx, uid);
        Assert.Equal(1, names.Count(n => n == "my_floor.tga"));
        Assert.Equal(1, names.Count(n => n == "my_ceiling.tga"));
        Assert.Equal(4, names.Count(n => n == "my_wall.tga"));
    }

    [Fact]
    public void PlaceBox_Falls_Back_When_The_Configured_Default_Is_Dead()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        using var vfs = new AssetVfs(new[] { new FakeSource("Rck_Default.tga") });

        // The historical dead persisted name: unresolvable in the VFS → the guard must fall
        // back to the stock default instead of stamping a name that renders white.
        ScriptContext ctx = Context(doc, vfs,
            floor: "Rck_Default01.tga", wall: "Rck_Default01.tga", ceiling: "Rck_Default01.tga");

        int uid = ctx.Level.PlaceBox(0, 0, 0, 4, 4, 4).Uid;

        List<string> names = FaceTextures(ctx, uid);
        Assert.All(names, n => Assert.Equal(BrushCreateParams.DefaultTexture, n));
    }

    [Fact]
    public void PlaceBox_With_Tex_Registers_And_Applies_It_To_Every_Face()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        ScriptContext ctx = Context(doc);

        int uid = ctx.Level.PlaceBox(0, 0, 0, 4, 4, 4, "metal01.tga").Uid;

        Brush b = (Brush)ctx.Brushes.FindBrush(uid)!;
        Assert.Contains("metal01.tga", b.Geometry.Textures); // registered in the texture table
        List<string> names = FaceTextures(ctx, uid);
        Assert.All(names, n => Assert.Equal("metal01.tga", n));
    }

    [Fact]
    public void PlaceBox_With_Unresolvable_Tex_Keeps_It_But_Warns()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        using var vfs = new AssetVfs(new[] { new FakeSource("Rck_Default.tga") });
        var services = new ScriptServices { Document = doc, Assets = vfs, Confirmation = new AllowAllConfirmation() };
        var log = new ScriptLog();
        var ctx = new ScriptContext(services, new ScriptRunOptions { AllowDestructive = true }, log);

        int uid = ctx.Level.PlaceBox(0, 0, 0, 4, 4, 4, "not_in_library.tga").Uid;

        // Explicit author intent is kept (the texture may be added to the mod next), but the
        // white render is called out in the log.
        List<string> names = FaceTextures(ctx, uid);
        Assert.All(names, n => Assert.Equal("not_in_library.tga", n));
        Assert.Contains(log.Entries, e => e.Level == ScriptLogLevel.Warning && e.Message.Contains("not_in_library.tga"));
    }

    [Fact]
    public void PlaceBox_Via_Lua_Console_Line_Produces_Textured_Faces()
    {
        // End-to-end through the real engine — the exact line Goober re-checks in the console.
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "return level.place_box(0,0,0,4,4,4).uid");

        Assert.True(r.Success, r.Error?.ToDisplayString());
        int uid = int.Parse(r.ReturnValue!);

        var ed = new BrushEditor(doc);
        Brush b = ed.FindBrush(uid)!;
        Assert.Equal(6, b.Geometry.Faces.Count);
        foreach (Face f in b.Geometry.Faces)
        {
            Assert.InRange(f.Texture, 0, b.Geometry.Textures.Count - 1);
            Assert.Equal(BrushCreateParams.DefaultTexture, b.Geometry.Textures[f.Texture]);
        }
    }
}
