using System;
using System.Linq;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Render half of the SCRIPTED white-brush regression (the interactive half lives in
/// <see cref="DefaultBrushTextureGuardTests"/>): a box created through the scripting API
/// (<c>level.place_box</c>) on the live session must render TEXTURED with a mounted VFS —
/// pixels must differ from the no-VFS white fallback. This is exactly what Goober re-checks
/// in the console.
/// </summary>
public sealed class ScriptedBrushRenderTests
{
    [AvaloniaFact]
    public void Scripted_PlaceBox_Renders_Textured_Not_White()
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

            // The live-session path the console uses: session document + session brush editor.
            var session = new EditorSession();
            session.NewLevel();
            var services = new ScriptServices
            {
                Document = session.Document!,
                Brushes = session.BrushEditor,
                Assets = vfs,
                Confirmation = new AllowAllConfirmation(),
            };
            var ctx = new ScriptContext(services, new ScriptRunOptions { AllowDestructive = true }, new ScriptLog());
            int uid = ctx.Level.PlaceBox(0, 0, 0, 4, 4, 4).Uid;
            Assert.True(uid > 0);

            session.BrushEditor!.SetMode(EditMode.Brush); // edit mode → solid textured overlay fill
            RenderScene scene = session.BuildScene();

            var cam = new Ged.Rendering.Camera { Position = new Vector3(3f, 3f, -6f), AspectRatio = 320f / 240f };
            cam.LookAt(cam.Position, Vector3.Zero);

            byte[] textured = OffscreenRenderer.Render(gd, scene, vfs, cam, RenderMode.JustTextures, 320, 240);
            byte[] white = OffscreenRenderer.Render(gd, scene, null, cam, RenderMode.JustTextures, 320, 240);

            Assert.True(PixelsDiffer(textured, white),
                "a scripted place_box brush rendered identically with and without a VFS — " +
                "its faces are bound to the white fallback (the scripted white-brush regression).");
        }
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
