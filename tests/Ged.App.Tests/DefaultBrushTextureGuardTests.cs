using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The new-brush white-texture fix (repeat-class bug). The factory/table plumbing was
/// always correct (EnsureTexture on both paths); the divergence was the NAME source: the
/// create path stamped the raw persisted settings default onto every face without VFS
/// validation, so a stale/dead persisted name (the shipped-then-fixed "Rck_Default01.tga")
/// showed in face properties but bound the renderer's white fallback — while manual apply
/// used a browser name the texture catalog had already VFS-verified. These tests cover the
/// create-time guard (ResolveDefaultBrushTexture) and the rendered-level path through
/// BrushEditor.CreateBrush that the earlier hardcoded-literal render test sidestepped.
/// </summary>
public sealed class DefaultBrushTextureGuardTests
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

    [Fact]
    public void Unresolvable_Configured_Default_Falls_Back_To_Stock()
    {
        using var vfs = new AssetVfs(new[] { new FakeSource("Rck_Default.tga") });

        Assert.Equal(
            BrushCreateParams.DefaultTexture,
            EditorSession.ResolveDefaultBrushTexture(vfs, "Rck_Default01.tga")); // the historical dead name
        Assert.Equal(
            BrushCreateParams.DefaultTexture,
            EditorSession.ResolveDefaultBrushTexture(vfs, "some_typo.tga"));
    }

    [Fact]
    public void Resolvable_Configured_Default_Is_Kept()
    {
        using var vfs = new AssetVfs(new[] { new FakeSource("my_wall.tga", "Rck_Default.tga") });

        Assert.Equal("my_wall.tga", EditorSession.ResolveDefaultBrushTexture(vfs, "my_wall.tga"));
        // The supercede chain resolves a base name / different-extension reference too.
        Assert.Equal("my_wall.dds", EditorSession.ResolveDefaultBrushTexture(vfs, "my_wall.dds"));
    }

    [Fact]
    public void No_Vfs_Keeps_The_Configured_Name_And_Blank_Falls_Back_To_Stock()
    {
        // Unverifiable (no VFS) → keep the user's name rather than second-guess it.
        Assert.Equal("anything.tga", EditorSession.ResolveDefaultBrushTexture(null, "anything.tga"));
        Assert.Equal(BrushCreateParams.DefaultTexture, EditorSession.ResolveDefaultBrushTexture(null, ""));
        Assert.Equal(BrushCreateParams.DefaultTexture, EditorSession.ResolveDefaultBrushTexture(vfs: null, "   "));
    }

    // ---- Rendered-level regression through the REAL create entry point ----------
    // The pre-existing render test hardcoded BrushFactory.Box(..., "Rck_Default.tga") and so
    // could never catch a bad settings-sourced name. This drives BrushEditor.CreateBrush (the
    // entry point the brush panel + draw tool use) and proves at the pixel level that the
    // dead name renders as the white fallback while the guarded name renders textured.
    [AvaloniaFact]
    public void CreateBrush_With_Dead_Name_Renders_White_While_Guarded_Name_Renders_Textured()
    {
        string? install = RfTestPaths.LocateRfInstall();
        GraphicsDevice gd;
        try
        {
            gd = new GraphicsDevice();
        }
        catch
        {
            return; // no D3D11 device → skip gracefully
        }

        using (gd)
        {
            if (install is null)
            {
                return; // needs a real install to bind textures
            }

            using AssetVfs vfs = GameMount.Mount(install);
            var cam = new Ged.Rendering.Camera { Position = new Vector3(3f, 3f, -6f), AspectRatio = 320f / 240f };
            cam.LookAt(cam.Position, Vector3.Zero);

            // Dead persisted name straight onto the create path (the pre-fix behaviour):
            // with the VFS mounted the face still renders exactly like the no-VFS white
            // fallback — the reported bug.
            byte[] deadVfs = RenderCreatedBrush(vfs, "Rck_Default01.tga", gd, cam);
            byte[] deadNoVfs = RenderCreatedBrush(null, "Rck_Default01.tga", gd, cam);
            Assert.False(PixelsDiffer(deadVfs, deadNoVfs),
                "a dead texture name should render as the white fallback (identical to no-VFS)");

            // The same create path through the guard resolves to the stock texture and
            // renders textured (differs from the white fallback).
            string guarded = EditorSession.ResolveDefaultBrushTexture(vfs, "Rck_Default01.tga");
            byte[] guardedVfs = RenderCreatedBrush(vfs, guarded, gd, cam);
            Assert.True(PixelsDiffer(guardedVfs, deadNoVfs),
                "the guarded name must render textured, not the white fallback");
        }
    }

    private static byte[] RenderCreatedBrush(AssetVfs? vfs, string defaultTexture, GraphicsDevice gd, Ged.Rendering.Camera cam)
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        int uid = be.CreateBrush(
            new BrushCreateParams
            {
                Shape = BrushShape.Box,
                Width = 4f,
                Height = 4f,
                Depth = 4f,
                FloorTexture = defaultTexture,
                WallTexture = defaultTexture,
                CeilingTexture = defaultTexture,
            },
            default, Mat3.Identity);
        Assert.True(uid > 0);
        be.SetMode(EditMode.Brush); // edit mode → solid textured overlay fill

        RenderScene scene = session.BuildScene();
        return OffscreenRenderer.Render(gd, scene, vfs, cam, RenderMode.JustTextures, 320, 240);
    }

    private static bool PixelsDiffer(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return true;
        }

        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                changed++;
            }
        }

        return changed > a.Length / 500; // > ~0.2% of channel bytes differ
    }
}
