namespace Ged.Core.Model;

/// <summary>
/// One corner of a <see cref="Face"/>: an index into the geometry vertex pool,
/// texture UVs, and — only when the owning face binds a lightmap surface —
/// lightmap UVs.
/// </summary>
public sealed class FaceVertex
{
    /// <summary>Index into the geometry vertex pool.</summary>
    public int Index { get; set; }

    public Uv TextureCoords { get; set; }

    /// <summary>
    /// Lightmap UVs. Present only in static geometry, and only for faces that
    /// are neither full-bright nor invisible; null otherwise. Serialization
    /// writes these exactly when present.
    /// </summary>
    public Uv? LightmapCoords { get; set; }
}
