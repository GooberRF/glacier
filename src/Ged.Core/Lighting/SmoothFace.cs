using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// One face of a smoothed surface, in the lightmapper's projected form: the
/// polygon's world vertices plus their smoothing-group-averaged vertex normals,
/// so a texel that lands inside the polygon can barycentrically interpolate the
/// normal (RED's should_smooth path uses interpolated normals + raw N·L).
/// </summary>
public sealed class SmoothFace
{
    public SmoothFace(Vec3[] positions, Vec3[] normals)
    {
        Positions = positions;
        Normals = normals;
    }

    public Vec3[] Positions { get; }

    public Vec3[] Normals { get; }
}

/// <summary>
/// Computes per-vertex normals for smooth-surface lighting, reproducing RED's
/// baker exactly (RED.exe FUN_004aded0): for each vertex of a smoothed face, the
/// normal is the UNWEIGHTED mean of the face's own plane normal plus the plane
/// normal of every vertex-sharing face that (a) carries smoothing data and
/// (b) lies strictly within 90° of the current face
/// (<c>dot(currentFace.N, otherFace.N) &gt; 0</c> — the hemisphere cutoff that
/// keeps a smoothed wall from bending its base normals toward a perpendicular
/// floor). The mean is then normalized.
/// </summary>
public static class SmoothNormals
{
    private const float Quantum = 1e-3f;

    /// <summary>Builds, per lit face, its <see cref="SmoothFace"/> with interpolated vertex normals.</summary>
    public static Dictionary<CsgFaceKey, SmoothFace> Build(IReadOnlyList<Compiler.CsgFace> faces, bool angleWeighted = false)
    {
        // Position → plane normals of the smoothing-capable faces using that vertex
        // (RED: the vertex object's face-adjacency list, filtered to faces whose
        // smoothing data is set).
        var atPos = new Dictionary<long, List<Vec3>>();
        foreach (Compiler.CsgFace f in faces)
        {
            if (f.SmoothingGroups == 0)
            {
                continue;
            }

            foreach (Compiler.CsgVertex v in f.Vertices)
            {
                long k = Key(v.Position);
                if (!atPos.TryGetValue(k, out List<Vec3>? list))
                {
                    list = new List<Vec3>(4);
                    atPos[k] = list;
                }

                list.Add(f.Plane.Normal);
            }
        }

        var result = new Dictionary<CsgFaceKey, SmoothFace>();
        foreach (Compiler.CsgFace f in faces)
        {
            if (f.SmoothingGroups == 0)
            {
                continue;
            }

            int vc = f.Vertices.Count;
            var pos = new Vec3[vc];
            var nrm = new Vec3[vc];
            for (int i = 0; i < vc; i++)
            {
                pos[i] = f.Vertices[i].Position;
                nrm[i] = AverageAt(f.Plane.Normal, atPos.GetValueOrDefault(Key(pos[i])), angleWeighted);
            }

            result[new CsgFaceKey(f)] = new SmoothFace(pos, nrm);
        }

        return result;
    }

    /// <summary>
    /// RED's per-vertex rule: start from the current face's plane normal (count 1),
    /// add each other adjacent smooth face's plane normal whose dot with the CURRENT
    /// face's normal is &gt; 0, take the unweighted mean, normalize.
    /// </summary>
    /// <param name="angleWeighted">
    /// Smooth Gutter Normals option: weight each contributing adjacent normal by its cosine with
    /// the current face (dot, in (0,1]) instead of the raw unweighted include/exclude. A face meeting
    /// near-perpendicular (dot→0) then contributes little rather than either fully counting or hard
    /// flipping out at the 90° cutoff — softening the shared-vertex normal flip at near-cutoff joins.
    /// Default false = RED's exact unweighted &gt;0 cutoff (byte-parity).
    /// </param>
    public static Vec3 AverageAt(Vec3 faceNormal, IReadOnlyList<Vec3>? adjacentNormals, bool angleWeighted = false)
    {
        Vec3 sum = faceNormal;
        float weight = 1f;
        bool skippedSelf = false;
        if (adjacentNormals is not null)
        {
            foreach (Vec3 n in adjacentNormals)
            {
                // The adjacency list contains the current face itself once — RED
                // excludes it by identity; we exclude the first exact match.
                if (!skippedSelf && n.Sub(faceNormal).LengthSquared() < 1e-12f)
                {
                    skippedSelf = true;
                    continue;
                }

                float dot = faceNormal.Dot(n);
                if (dot > 0f)
                {
                    float wn = angleWeighted ? dot : 1f;
                    sum = sum.Add(n.Scale(wn));
                    weight += wn;
                }
            }
        }

        Vec3 mean = sum.Scale(1f / weight).Normalized();
        return mean.LengthSquared() > 1e-8f ? mean : faceNormal;
    }

    private static long Key(Vec3 p)
    {
        long qx = (long)MathF.Round(p.X / Quantum) & 0x1FFFFF;
        long qy = (long)MathF.Round(p.Y / Quantum) & 0x1FFFFF;
        long qz = (long)MathF.Round(p.Z / Quantum) & 0x1FFFFF;
        return (qx << 42) | (qy << 21) | qz;
    }
}

/// <summary>Identity key for a <see cref="Compiler.CsgFace"/> (reference identity).</summary>
public readonly struct CsgFaceKey : IEquatable<CsgFaceKey>
{
    private readonly Compiler.CsgFace _face;

    public CsgFaceKey(Compiler.CsgFace face) => _face = face;

    public bool Equals(CsgFaceKey other) => ReferenceEquals(_face, other._face);

    public override bool Equals(object? obj) => obj is CsgFaceKey k && Equals(k);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_face);
}
