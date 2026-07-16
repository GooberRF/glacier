namespace Ged.Core.Model;

/// <summary>Per-face UV scroll velocity, keyed by face id (RFL <c>face_scroll_data</c>).</summary>
public sealed class FaceScrollData
{
    public int FaceId { get; set; }

    /// <summary>U velocity.</summary>
    public float UVelocity { get; set; }

    /// <summary>V velocity.</summary>
    public float VVelocity { get; set; }
}
