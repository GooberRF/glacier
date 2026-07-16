using System.Numerics;

namespace Ged.Rendering;

/// <summary>
/// Distance-fog parameters for the world/mesh shaders: geometry fades toward
/// <see cref="Color"/> between <see cref="Start"/> and <see cref="End"/> (the far
/// clip). Mirrors the level's <c>level_properties</c> fog colour + far-clip.
/// </summary>
public readonly record struct FogSettings(bool Enabled, Vector3 Color, float Start, float End)
{
    /// <summary>Fog disabled (the renderer default).</summary>
    public static FogSettings Off => new(false, Vector3.Zero, 0f, 1f);

    /// <summary>Fog from an RGB colour (0..1) and a far-clip end distance; start defaults to 10% of end.</summary>
    public static FogSettings FromLevel(Vector3 color, float farClip) =>
        new(true, color, farClip * 0.1f, farClip);
}

/// <summary>
/// Viewport render modes, mirroring stock RED's View menu (see
/// docs/research/red-stock-inventory.md §2). Per-mode rendering behaviour
/// (shader branch, wireframe fill, global alpha) is described by
/// <see cref="RenderModeExtensions"/>.
/// </summary>
public enum RenderMode
{
    /// <summary>Diffuse textures only ("Just Textures").</summary>
    JustTextures = 0,

    /// <summary>Diffuse modulated by the baked lightmap, game-accurate 2x combine ("Textures w Lightmaps").</summary>
    TexturesAndLightmaps = 1,

    /// <summary>Lightmaps only, 2x combine against white ("Just Lightmaps").</summary>
    JustLightmaps = 2,

    /// <summary>Each room a distinct flat color ("Rooms in Different Colors").</summary>
    RoomColors = 3,

    /// <summary>Wireframe overlay of all geometry ("Wireframe").</summary>
    Wireframe = 4,

    /// <summary>Everything drawn semi-transparent, global alpha 0.5 ("Everything See-through").</summary>
    SeeThrough = 5,
}

/// <summary>Helpers describing per-mode rendering behaviour.</summary>
public static class RenderModeExtensions
{
    /// <summary>The global alpha applied to opaque geometry in this mode (1 except see-through).</summary>
    public static float GlobalAlpha(this RenderMode mode) => mode == RenderMode.SeeThrough ? 0.5f : 1.0f;

    /// <summary>True when geometry is rasterized in wireframe fill mode.</summary>
    public static bool IsWireframe(this RenderMode mode) => mode == RenderMode.Wireframe;

    /// <summary>The shading branch the world pixel shader selects for this mode.</summary>
    public static int ShaderBranch(this RenderMode mode) => mode switch
    {
        RenderMode.JustTextures => 0,
        RenderMode.TexturesAndLightmaps => 1,
        RenderMode.JustLightmaps => 2,
        RenderMode.RoomColors => 3,
        RenderMode.Wireframe => 4,
        RenderMode.SeeThrough => 0,
        _ => 0,
    };
}
