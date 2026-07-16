using System;
using System.Collections.Generic;
using Ged.Core.IO.Mesh;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>A named mesh tag point (V3D prop point) in the mesh's local space.</summary>
public readonly record struct MeshTag(string Name, Vec3 Position, Mat3 Orientation);

/// <summary>
/// Resolves the tag points of a mesh file — the <c>corona_N</c> / <c>thruster_N</c> prop points the
/// object→mesh conversion spawns child objects at. Abstracted so the converter is testable without a
/// live VFS (a fake source supplies tags directly).
/// </summary>
public interface IMeshTagSource
{
    /// <summary>The tag points of a mesh file (empty when the file is unavailable/unreadable).</summary>
    IReadOnlyList<MeshTag> ReadTags(string meshFilename);
}

/// <summary>
/// V3D-backed <see cref="IMeshTagSource"/>: reads prop points from the base LOD of each submesh via a
/// byte[] resolver (the shell supplies a VFS-backed one). Prop-point orientations are stored as
/// quaternions in the V3D; they are converted to RF's (forward,right,up) <see cref="Mat3"/> here.
/// </summary>
public sealed class V3dMeshTagSource : IMeshTagSource
{
    private readonly Func<string, byte[]?> _resolve;

    public V3dMeshTagSource(Func<string, byte[]?> resolve) => _resolve = resolve;

    public IReadOnlyList<MeshTag> ReadTags(string meshFilename)
    {
        if (string.IsNullOrWhiteSpace(meshFilename))
        {
            return Array.Empty<MeshTag>();
        }

        byte[]? data;
        try
        {
            data = _resolve(meshFilename);
        }
        catch (Exception)
        {
            return Array.Empty<MeshTag>();
        }

        if (data is null)
        {
            return Array.Empty<MeshTag>();
        }

        V3dFile v3d;
        try
        {
            v3d = V3dReader.Read(data);
        }
        catch (Exception)
        {
            return Array.Empty<MeshTag>();
        }

        var tags = new List<MeshTag>();
        foreach (V3dSubmesh sm in v3d.Submeshes)
        {
            if (sm.Lods.Count == 0)
            {
                continue;
            }

            foreach (V3dPropPoint pp in sm.Lods[0].PropPoints)
            {
                tags.Add(new MeshTag(pp.Name, pp.Position, QuatToMat3(pp.Orientation)));
            }
        }

        return tags;
    }

    /// <summary>Converts a V3D prop-point quaternion to RF's (forward, right, up) matrix.</summary>
    internal static Mat3 QuatToMat3(V3dQuat q)
    {
        float len = MathF.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
        if (len < 1e-8f)
        {
            return Mat3.Identity;
        }

        float x = q.X / len, y = q.Y / len, z = q.Z / len, w = q.W / len;

        // Standard quaternion→rotation columns (images of the local X/Y/Z axes).
        var right = new Vec3(1f - (2f * ((y * y) + (z * z))), 2f * ((x * y) + (w * z)), 2f * ((x * z) - (w * y)));
        var up = new Vec3(2f * ((x * y) - (w * z)), 1f - (2f * ((x * x) + (z * z))), 2f * ((y * z) + (w * x)));
        var forward = new Vec3(2f * ((x * z) + (w * y)), 2f * ((y * z) - (w * x)), 1f - (2f * ((x * x) + (y * y))));
        return new Mat3(forward, right, up);
    }
}
