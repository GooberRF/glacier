using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>How an imported model's axes map onto RF's coordinate frame (+X right, +Y up, +Z forward, meters).</summary>
public enum MeshAxisConversion
{
    /// <summary>The source is already in RF coordinates — no change.</summary>
    RfNative,

    /// <summary>
    /// glTF / right-handed Y-up, −Z forward → RF's left-handed Y-up, +Z forward.
    /// Negates Z (the forward flip) which also reverses winding.
    /// </summary>
    GltfYUp,

    /// <summary>
    /// Z-up (Blender / 3ds Max / many FBX) right-handed → RF Y-up. Swaps Y and Z,
    /// which reverses winding.
    /// </summary>
    ZUp,
}

/// <summary>
/// The axis-conversion transform: how to remap a source-space position/normal into
/// RF space, and whether the remap reverses triangle winding (so the pipeline can
/// keep front faces outward). Pure and unit-tested via a known asymmetric fixture.
/// </summary>
public static class MeshAxis
{
    /// <summary>Maps a source-space position into RF space.</summary>
    public static Vec3 Convert(Vec3 v, MeshAxisConversion mode) => mode switch
    {
        MeshAxisConversion.GltfYUp => new Vec3(v.X, v.Y, -v.Z),
        MeshAxisConversion.ZUp => new Vec3(v.X, v.Z, v.Y),
        _ => v,
    };

    /// <summary>True when the axis remap flips handedness (and therefore triangle winding).</summary>
    public static bool FlipsWinding(MeshAxisConversion mode) =>
        mode is MeshAxisConversion.GltfYUp or MeshAxisConversion.ZUp;

    /// <summary>The axis conversion a format defaults to (glTF is detectable; others are asked).</summary>
    public static MeshAxisConversion DefaultFor(ImportedFormat format) => format switch
    {
        ImportedFormat.Gltf => MeshAxisConversion.GltfYUp,
        _ => MeshAxisConversion.RfNative,
    };
}

/// <summary>What an import produces.</summary>
public enum MeshImportTarget
{
    /// <summary>One brush per material group (triangulated faces, UVs preserved).</summary>
    Brushes,

    /// <summary>An Alpine mesh object: a .v3m written to disk + a Mesh object placed referencing it.</summary>
    MeshObject,
}

/// <summary>User-chosen options for a mesh import (scale, axis, winding, target).</summary>
public sealed class MeshImportOptions
{
    /// <summary>Uniform scale factor applied to every position (default 1.0; cm→m 0.01, inches 0.0254).</summary>
    public float Scale { get; set; } = 1f;

    public MeshAxisConversion Axis { get; set; } = MeshAxisConversion.RfNative;

    /// <summary>User winding override, XORed with the axis conversion's implicit flip.</summary>
    public bool FlipWinding { get; set; }

    public MeshImportTarget Target { get; set; } = MeshImportTarget.Brushes;

    /// <summary>Tile size (meters) for the planar-UV fallback applied to groups that ship no UVs.</summary>
    public float PlanarTileMeters { get; set; } = 2f;

    /// <summary>The effective winding flip = axis-implicit flip XOR the user toggle.</summary>
    public bool EffectiveFlipWinding => MeshAxis.FlipsWinding(Axis) ^ FlipWinding;
}
