namespace Ged.Core.Model;

/// <summary>A projected decal (RFL <c>decal</c>).</summary>
public sealed class Decal
{
    public ObjectHeader Header { get; set; } = new();

    public Vec3 Extents { get; set; }

    public string Texture { get; set; } = string.Empty;

    public int Alpha { get; set; }

    public byte SelfIlluminated { get; set; }

    /// <summary>0 none, 1 u, 2 v (decal_tiling enum).</summary>
    public int Tiling { get; set; }

    public float Scale { get; set; }
}
