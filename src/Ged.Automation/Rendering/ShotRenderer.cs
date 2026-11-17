using System;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Rendering.Graphics;

namespace Ged.Automation.Rendering;

/// <summary>
/// The game-look camera render behind ref.shot_compare / shot_locate's render panel: the level's current compiled
/// geometry drawn by <see cref="LevelRenderer"/> from an eye-level pose (no build, no icons), returned as RGBA.
/// </summary>
internal static class ShotRenderer
{
    /// <summary>Renders the request; null when no GPU device can be created (the caller falls back to ray-cast shading).</summary>
    public static byte[]? Render(EditorDocument doc, AssetVfs? vfs, ShotRenderRequest r)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(r);
        double yaw = r.Yaw * Math.PI / 180, pitch = r.Pitch * Math.PI / 180;
        var eye = new Vector3((float)r.X, (float)r.Y, (float)r.Z);
        var dir = new Vector3((float)(Math.Cos(pitch) * Math.Sin(yaw)), (float)Math.Sin(pitch), (float)(Math.Cos(pitch) * Math.Cos(yaw)));
        var spec = new RenderSpec
        {
            OutPath = "shot.png",
            View = RenderView.Camera,
            CameraPosition = eye,
            CameraLook = eye + (dir * 10f),
            HFovDegrees = (float)r.Hfov,
            Width = r.Width,
            Height = r.Height,
            Mode = r.Mode == "textured" ? "textured" : "lit",
            Build = "none",
        };

        (GraphicsDevice device, string name) dev;
        try
        {
            dev = RenderDevices.Create();
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            var state = new RenderGeometryDto { Before = "built", BuiltForRender = "none", Rendered = "built" };
            (byte[] rgba, RenderResult result) = LevelRenderer.Render(dev.device, dev.name, doc, vfs, spec, null, state, spec.OutPath);
            return result.Width == r.Width && result.Height == r.Height ? rgba : null;
        }
        finally
        {
            dev.device.Dispose();
        }
    }
}
