namespace Ged.Core.Model;

/// <summary>
/// Lightmap binding info for one or more faces (RFL <c>surface</c>, 96 bytes).
/// All fields, including the reserved/unused ones, are preserved so the model
/// re-serializes losslessly.
/// </summary>
public sealed class Surface
{
    public int LightmapIndex { get; set; }

    public byte X { get; set; }

    public byte Y { get; set; }

    public byte W { get; set; }

    public byte H { get; set; }

    public float XPixelsPerMeter { get; set; }

    public float YPixelsPerMeter { get; set; }

    public Aabb BoundingBox { get; set; }

    public RfPlane Plane { get; set; }

    /// <summary>1 if the face belongs to any smoothing group.</summary>
    public int ShouldSmooth { get; set; }

    /// <summary>Typically zero (rfl.ksy <c>unknown_zero</c>).</summary>
    public int UnknownZero { get; set; }

    /// <summary>Unused (rfl.ksy <c>dropped_coefficient</c>).</summary>
    public int DroppedCoefficient { get; set; }

    /// <summary>Position component used as lightmap U (x=0, y=1, z=2).</summary>
    public int UCoefficient { get; set; }

    /// <summary>Position component used as lightmap V (x=0, y=1, z=2).</summary>
    public int VCoefficient { get; set; }

    public Uv UvAdd { get; set; }

    public Uv UvScale { get; set; }

    public int RoomIndex { get; set; }
}
