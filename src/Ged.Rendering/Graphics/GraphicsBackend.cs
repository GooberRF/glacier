namespace Ged.Rendering.Graphics;

/// <summary>
/// Selects the GPU backend a <see cref="GraphicsDevice"/> runs on. Direct3D 11 is
/// the reference/default backend on Windows; OpenGL 3.3 core is the cross-platform
/// backend (L2) that is pixel-faithful to it. The scene-building and rendering
/// code above the RHI is identical for both.
/// </summary>
public enum GraphicsBackend
{
    /// <summary>Direct3D 11 (Windows reference backend; the default).</summary>
    Direct3D11,

    /// <summary>OpenGL 3.3 core (cross-platform backend). For L2 an offscreen WGL context hosts it.</summary>
    OpenGl,
}
