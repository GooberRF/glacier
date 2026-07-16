using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// A polygon flowing through the compiler: a convex/planar loop of
/// <see cref="CsgVertex"/> plus the attributes inherited from its source brush
/// face (texture name, 16-bit flags, smoothing groups, stable face id, source
/// brush uid) and the classification bookkeeping filled in by later stages
/// (room index, portal index). Split fragments clone all attributes verbatim and
/// interpolate only per-vertex position/UV.
/// </summary>
public sealed class CsgFace
{
    public List<CsgVertex> Vertices { get; set; } = new();

    public CsgPlane Plane { get; set; }

    /// <summary>Resolved texture name, or empty for a portal (written as texture -1).</summary>
    public string Texture { get; set; } = string.Empty;

    public ushort Flags { get; set; }

    public uint SmoothingGroups { get; set; }

    /// <summary>Stable id shared by all fragments of one source brush face.</summary>
    public int FaceId { get; set; }

    public int SourceBrushUid { get; set; }

    /// <summary>
    /// Time index (CSG add order) of the source brush, stamped by the solver. RED's
    /// build is strictly linear in this order; coincident-face resolution uses it to
    /// tell the earlier (accumulated "world") operand from the later ("brush") operand.
    /// </summary>
    public int BrushTime { get; set; }

    /// <summary>True when the source face came from an air brush (vs a solid brush).</summary>
    public bool FromAir { get; set; }

    public bool IsPortal { get; set; }

    /// <summary>Authored texture-scroll velocity (U/V per second), if the face scrolls.</summary>
    public Uv? Scroll { get; set; }

    // ---- Filled in by later stages ----
    public int RoomIndex { get; set; } = -1;

    public int PortalIndexPlus2 { get; set; }

    /// <summary>Surface (lightmap) binding, or -1 for no lightmap. Set by the surface stage.</summary>
    public int SurfaceIndex { get; set; } = -1;

    /// <summary>Per-vertex lightmap UVs (parallel to <see cref="Vertices"/>), or null when unbound.</summary>
    public Uv[]? LightmapUvs { get; set; }

    /// <summary>A shallow copy that shares nothing mutable except the attribute values.</summary>
    public CsgFace CloneAttributes() => new()
    {
        Plane = Plane,
        Texture = Texture,
        Flags = Flags,
        SmoothingGroups = SmoothingGroups,
        FaceId = FaceId,
        SourceBrushUid = SourceBrushUid,
        BrushTime = BrushTime,
        FromAir = FromAir,
        IsPortal = IsPortal,
        Scroll = Scroll,
        RoomIndex = RoomIndex,
        PortalIndexPlus2 = PortalIndexPlus2,
    };

    public CsgFace With(List<CsgVertex> verts)
    {
        CsgFace f = CloneAttributes();
        f.Vertices = verts;
        return f;
    }

    /// <summary>Reverses winding and flips the plane (used by BSP inversion + the final open-space flip).</summary>
    public void Flip()
    {
        Vertices.Reverse();
        Plane = Plane.Flipped();
    }

    public Vec3 Centroid()
    {
        var sum = new Vec3(0, 0, 0);
        foreach (CsgVertex v in Vertices)
        {
            sum = sum.Add(v.Position);
        }

        return Vertices.Count == 0 ? sum : sum.Scale(1f / Vertices.Count);
    }

    /// <summary>Fan-triangulation area of the polygon.</summary>
    public float Area()
    {
        if (Vertices.Count < 3)
        {
            return 0f;
        }

        Vec3 c = Centroid();
        float area = 0f;
        for (int i = 0; i < Vertices.Count; i++)
        {
            Vec3 a = Vertices[i].Position.Sub(c);
            Vec3 b = Vertices[(i + 1) % Vertices.Count].Position.Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    public void GrowAabb(ref Vec3 min, ref Vec3 max)
    {
        foreach (CsgVertex v in Vertices)
        {
            min = Vec3Math.Min(min, v.Position);
            max = Vec3Math.Max(max, v.Position);
        }
    }
}
