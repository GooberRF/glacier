namespace Ged.Core.Model;

/// <summary>
/// One of the four saved editor viewport configurations (RFL
/// <c>editor_view_config</c>). A free-look view stores a 3D position; the ortho
/// views store four floats instead.
/// </summary>
public sealed class EditorViewConfig
{
    /// <summary>0 free_look, 1 top, 2 bottom, 3 front, 4 back, 5 left, 6 right.</summary>
    public int ViewType { get; set; }

    /// <summary>Present iff <see cref="ViewType"/> == 0 (free_look).</summary>
    public Vec3? Position3d { get; set; }

    /// <summary>Four floats, present for non-free-look views.</summary>
    public float[]? Position2d { get; set; }

    public Mat3 Rotation { get; set; }
}
