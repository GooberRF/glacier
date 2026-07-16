using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>How a clip resolves the two sides of the cutting plane.</summary>
public enum ClipMode
{
    /// <summary>Keep both sides as separate brushes, capping the cut on each.</summary>
    Split,

    /// <summary>Discard the side the plane normal points toward, keeping the rest.</summary>
    Cut,
}

/// <summary>The pieces produced by a clip (1 for Cut, up to 2 for Split).</summary>
public sealed class ClipResult
{
    public ClipResult(bool success, string message, List<Geometry> pieces)
    {
        Success = success;
        Message = message;
        Pieces = pieces;
    }

    public bool Success { get; }

    public string Message { get; }

    public List<Geometry> Pieces { get; }
}

/// <summary>
/// Brush-mode operators that change a brush's topology: Clip (robust convex plane
/// clip with capping — the Alpine fix, not stock's line/AABB test), Fuse (merge
/// several brushes' surfaces into one) and Mirror X/Y/Z. All pure; the App wraps
/// results in undo commands.
/// </summary>
public static class BrushOps
{
    private const float PlaneEps = 1e-4f;

    /// <summary>
    /// Clips a brush by a world-space plane. <paramref name="flipNormal"/> swaps
    /// which side a Cut discards. Split returns both capped halves; Cut returns
    /// the surviving half. Local geometry is produced in the source brush's frame.
    /// </summary>
    public static ClipResult Clip(Brush brush, Vec3 planePoint, Vec3 planeNormal, ClipMode mode, bool flipNormal)
    {
        ArgumentNullException.ThrowIfNull(brush);
        Vec3 n = planeNormal.Normalized();
        if (n.LengthSquared() < 0.5f)
        {
            return new ClipResult(false, "The clip plane is degenerate.", new List<Geometry>());
        }

        if (flipNormal)
        {
            n = n.Negate();
        }

        // Move the world plane into the brush's local frame.
        Vec3 localNormal = brush.Rotation.InverseTransform(n);
        Vec3 localPoint = brush.Rotation.InverseTransform(planePoint.Sub(brush.Position));
        var plane = new RfPlane(localNormal, localNormal.Dot(localPoint));

        Geometry? negative = ClipGeometry(brush.Geometry, plane, keepPositive: false);
        Geometry? positive = ClipGeometry(brush.Geometry, plane, keepPositive: true);

        if (negative is null || positive is null)
        {
            return new ClipResult(false, "The clip plane does not pass through the brush.", new List<Geometry>());
        }

        if (mode == ClipMode.Cut)
        {
            // Discard the side the (possibly flipped) normal points toward: keep negative half.
            return new ClipResult(true, "Clip: cut", new List<Geometry> { negative });
        }

        return new ClipResult(true, "Clip: split", new List<Geometry> { negative, positive });
    }

    /// <summary>
    /// Clips a convex brush geometry to one side of a plane and caps the cut.
    /// Returns null when the plane leaves nothing on the kept side or does not
    /// actually cross the brush (caller treats that as a no-op / error).
    /// </summary>
    public static Geometry? ClipGeometry(Geometry g, RfPlane plane, bool keepPositive)
    {
        float sign = keepPositive ? 1f : -1f;
        var dist = new float[g.Vertices.Count];
        int strictKept = 0, strictCut = 0;
        for (int i = 0; i < g.Vertices.Count; i++)
        {
            float d = (plane.Normal.Dot(g.Vertices[i]) - plane.Offset) * sign;
            dist[i] = d;
            if (d > PlaneEps)
            {
                strictKept++;
            }
            else if (d < -PlaneEps)
            {
                strictCut++;
            }
        }

        if (strictCut == 0)
        {
            return GeometryClone.Deep(g); // nothing removed
        }

        if (strictKept == 0)
        {
            return null; // nothing survives
        }

        var result = new Geometry { Name = g.Name };
        result.Textures.AddRange(g.Textures);

        foreach (Face f in g.Faces)
        {
            var clipped = ClipFace(g, f, dist);
            if (clipped.Count >= 3)
            {
                AddWorldFace(result, clipped, f.Texture, f.Flags, f.SmoothingGroups);
            }
        }

        // Cap: every result vertex lying on the plane forms the boundary ring.
        AddCap(result, plane, keepPositive);

        GeometryUtil.WeldVertices(result);
        BrushFactory.OrientOutward(result);
        GeometryUtil.AssignAllPlanarUv(result);
        return GeometryUtil.Validate(result) ? result : null;
    }

    private static List<Vec3> ClipFace(Geometry g, Face f, float[] dist)
    {
        var output = new List<Vec3>();
        int n = f.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            int ai = f.Vertices[i].Index;
            int bi = f.Vertices[(i + 1) % n].Index;
            float da = dist[ai];
            float db = dist[bi];
            bool aIn = da >= -PlaneEps;
            bool bIn = db >= -PlaneEps;
            Vec3 a = g.Vertices[ai];
            Vec3 b = g.Vertices[bi];

            if (aIn)
            {
                output.Add(a);
            }

            if (aIn != bIn && MathF.Abs(da - db) > 1e-9f)
            {
                float t = da / (da - db);
                output.Add(Vec3Math.Lerp(a, b, t));
            }
        }

        return output;
    }

    private static void AddCap(Geometry result, RfPlane plane, bool keepPositive)
    {
        var onPlane = new List<Vec3>();
        foreach (Vec3 v in result.Vertices)
        {
            if (MathF.Abs(plane.Normal.Dot(v) - plane.Offset) <= 1e-3f && !onPlane.Any(p => p.ApproxEquals(v, 1e-3f)))
            {
                onPlane.Add(v);
            }
        }

        if (onPlane.Count < 3)
        {
            return;
        }

        // Order the ring by angle in the plane about its centroid.
        Vec3 c = GeometryUtil.Centroid(onPlane);
        Vec3 nrm = plane.Normal.Normalized();
        Vec3 u = MathF.Abs(nrm.X) < 0.9f ? nrm.Cross(new Vec3(1, 0, 0)).Normalized() : nrm.Cross(new Vec3(0, 1, 0)).Normalized();
        Vec3 w = nrm.Cross(u);
        onPlane.Sort((p, q) =>
            MathF.Atan2(p.Sub(c).Dot(w), p.Sub(c).Dot(u))
                .CompareTo(MathF.Atan2(q.Sub(c).Dot(w), q.Sub(c).Dot(u))));

        AddWorldFace(result, onPlane, 0, 0, 0);
    }

    private static void AddWorldFace(Geometry g, List<Vec3> corners, int texture, ushort flags, uint smoothing)
    {
        var face = new Face { Texture = texture, SurfaceIndex = -1, Flags = flags, SmoothingGroups = smoothing, RoomIndex = -1, FaceId = g.Faces.Count };
        foreach (Vec3 p in corners)
        {
            face.Vertices.Add(new FaceVertex { Index = GeometryUtil.AddVertex(g, p) });
        }

        g.Faces.Add(face);
    }

    /// <summary>
    /// Merges several brushes' surfaces into one brush in the first brush's frame,
    /// welding shared vertices and dropping the coincident internal walls between
    /// touching brushes. A pragmatic surface fuse; a full CSG union lands with the
    /// compiler. Requires at least two brushes.
    /// </summary>
    public static (OpResult Result, Brush? Fused) Fuse(IReadOnlyList<Brush> brushes)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        if (brushes.Count < 2)
        {
            return (OpResult.Fail("Select at least two brushes to fuse."), null);
        }

        Brush first = brushes[0];
        var g = new Geometry { Name = "fused" };

        foreach (Brush b in brushes)
        {
            foreach (Face f in b.Geometry.Faces)
            {
                int tex = GeometryUtil.EnsureTexture(g, ResolveTexture(b.Geometry, f.Texture));
                var nf = new Face { Texture = tex, SurfaceIndex = -1, Flags = f.Flags, SmoothingGroups = f.SmoothingGroups, RoomIndex = -1 };
                foreach (FaceVertex fv in f.Vertices)
                {
                    Vec3 world = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[fv.Index]));
                    Vec3 local = first.Rotation.InverseTransform(world.Sub(first.Position));
                    // AddVertex welds by position, so shared walls between touching brushes coincide.
                    int poolIndex = GeometryUtil.AddVertex(g, local);
                    nf.Vertices.Add(new FaceVertex { Index = poolIndex, TextureCoords = fv.TextureCoords });
                }

                g.Faces.Add(nf);
            }
        }

        GeometryUtil.WeldVertices(g);
        RemoveCoincidentFaces(g);
        GeometryUtil.RecomputeAllPlanes(g);

        OpResult validation = GeometryUtil.Validate(g);
        if (!validation)
        {
            return (validation, null);
        }

        var fused = new Brush
        {
            Uid = first.Uid,
            Position = first.Position,
            Rotation = first.Rotation,
            Geometry = g,
            Flags = first.Flags,
            Life = first.Life,
            State = BrushState.Normal,
        };
        return (OpResult.Ok("Fused"), fused);
    }

    /// <summary>Drops pairs of faces sharing the identical vertex set (internal shared walls).</summary>
    private static void RemoveCoincidentFaces(Geometry g)
    {
        var toRemove = new HashSet<int>();
        for (int i = 0; i < g.Faces.Count; i++)
        {
            if (toRemove.Contains(i))
            {
                continue;
            }

            HashSet<int> a = g.Faces[i].Vertices.Select(v => v.Index).ToHashSet();
            for (int j = i + 1; j < g.Faces.Count; j++)
            {
                if (toRemove.Contains(j) || g.Faces[j].Vertices.Count != a.Count)
                {
                    continue;
                }

                if (a.SetEquals(g.Faces[j].Vertices.Select(v => v.Index)))
                {
                    toRemove.Add(i);
                    toRemove.Add(j);
                    break;
                }
            }
        }

        if (toRemove.Count > 0)
        {
            g.Faces = g.Faces.Where((_, idx) => !toRemove.Contains(idx)).ToList();
            GeometryUtil.CompactUnusedVertices(g);
        }
    }

    /// <summary>
    /// Mirrors a brush across the plane through its world centroid perpendicular to
    /// a world axis (0=X, 1=Y, 2=Z). Position is preserved; the local geometry is
    /// reflected and re-oriented. [ALPINE] Group and object mirroring have dedicated operators.
    /// </summary>
    public static void Mirror(Brush brush, int axis)
    {
        ArgumentNullException.ThrowIfNull(brush);
        // Mirror across the plane through the brush origin (its pivot), so a
        // symmetric brush maps onto itself and the result is predictable.
        Vec3 c = brush.Position;
        for (int i = 0; i < brush.Geometry.Vertices.Count; i++)
        {
            Vec3 world = brush.Position.Add(brush.Rotation.Transform(brush.Geometry.Vertices[i]));
            world = world.WithComponent(axis, (2f * c.Component(axis)) - world.Component(axis));
            brush.Geometry.Vertices[i] = brush.Rotation.InverseTransform(world.Sub(brush.Position));
        }

        BrushFactory.OrientOutward(brush.Geometry);
        GeometryUtil.AssignAllPlanarUv(brush.Geometry);
    }

    private static string ResolveTexture(Geometry g, int index) =>
        index >= 0 && index < g.Textures.Count ? g.Textures[index] : BrushCreateParams.DefaultTexture;
}
