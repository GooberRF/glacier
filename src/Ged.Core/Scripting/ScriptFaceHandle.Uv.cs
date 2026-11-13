using System;
using System.Globalization;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>Edge- and neighbour-aware UV operations on one face (<see cref="UvAlignOps"/>).</summary>
public sealed partial class ScriptFaceHandle
{
    [ScriptDoc("Texture on an edge: U along it, a tile boundary on it (offset m in). edge: number, \"bottom\" (stands on it), \"top\" (hangs) or \"+x\"..\"-z\"; ppm: px/m by size (0 = keep scale). Returns the face.",
        Example = "f:align_to_edge(\"bottom\") f:align_to_edge(\"bottom\", 0, 64)", Since = 2)]
    public ScriptFaceHandle AlignToEdge(object edge, double offset = 0, double ppm = 0)
    {
        const string Api = "face:align_to_edge";
        ScriptBrushHandle.RequireFinite(Api, offset, ppm);
        if (ppm < 0)
        {
            throw new ScriptApiException($"{Api}: ppm must be 0 (keep the current scale) or a positive number of pixels per metre (got {ScriptArgs.Format(ppm)}).");
        }

        float clamped = ppm > 0 ? ScriptBrushHandle.ClampPpm(Api, ppm) : 0f;
        EditUv("Align face texture to edge", (b, f, w, h) =>
        {
            System.Collections.Generic.List<UvAlignOps.Vec3d> corners = UvAlignOps.WorldCorners(b.Geometry, f, b.Rotation, b.Position);
            (int index, bool hang) = ResolveEdge(Api, edge, corners);
            double? su = clamped > 0 ? clamped / (double)Math.Max(1, w) : null;
            double? sv = clamped > 0 ? clamped / (double)Math.Max(1, h) : null;
            if (!UvAlignOps.AlignToEdge(b.Geometry, f, b.Rotation, b.Position, index, hang, offset, su, sv))
            {
                throw new ScriptApiException($"{Api}: face {_faceIndex} of brush {_brushUid} is degenerate (no area, or a zero-length edge {index + 1}).");
            }
        });
        return this;
    }

    [ScriptDoc("Continues another face's texture around the shared corner (same scale and direction, matching UVs along the common edge); parallel faces copy its mapping. Returns this face.",
        Example = "right_wall:wrap_from(front_wall)", Since = 2)]
    public ScriptFaceHandle WrapFrom(ScriptFaceHandle source)
    {
        const string Api = "face:wrap_from";
        if (source is null)
        {
            throw new ScriptApiException($"{Api}: the source face is required (a face handle).");
        }

        Brush src = source.Brush;
        Face srcFace = source.FaceOf(src);
        if (!ScriptBrushHandle.IsMappable(src.Geometry, srcFace))
        {
            throw new ScriptApiException($"{Api}: the source face is a portal face and has no texture mapping.");
        }

        // Read the source before the edit (it may be this brush), then map the destination.
        Geometry srcGeometry = GeometryClone.Deep(src.Geometry);
        Face srcCopy = srcGeometry.Faces[source._faceIndex];
        Mat3 srcRot = src.Rotation;
        Vec3 srcPos = src.Position;
        EditUv("Wrap face texture", (b, f, _, _) =>
        {
            if (!UvAlignOps.WrapFrom(b.Geometry, f, b.Rotation, b.Position, srcGeometry, srcCopy, srcRot, srcPos))
            {
                throw new ScriptApiException($"{Api}: the source face's UVs are degenerate (every corner has the same UV, or the face has no area).",
                    "Map the source first (box_map, planar_map or align_to_edge).");
            }
        });
        return this;
    }

    /// <summary>The 0-based edge index and stand/hang mode an <c>align_to_edge</c> selector names.</summary>
    private (int Index, bool Hang) ResolveEdge(string api, object? edge, System.Collections.Generic.IReadOnlyList<UvAlignOps.Vec3d> corners)
    {
        int n = corners.Count;
        switch (edge)
        {
            case double d:
                if (d != Math.Floor(d) || d < 1 || d > n)
                {
                    throw new ScriptApiException($"{api}: edge {ScriptArgs.Format(d)} is not an edge of this face (it has {n}, numbered 1 to {n}: corner i to corner i + 1).");
                }

                return ((int)d - 1, false);
            case string s:
                (UvAlignOps.Vec3d dir, bool hang) = s.Trim().ToLowerInvariant() switch
                {
                    "bottom" => (new UvAlignOps.Vec3d(0, -1, 0), false),
                    "top" => (new UvAlignOps.Vec3d(0, 1, 0), true),
                    "+x" => (new UvAlignOps.Vec3d(1, 0, 0), false),
                    "-x" => (new UvAlignOps.Vec3d(-1, 0, 0), false),
                    "+z" => (new UvAlignOps.Vec3d(0, 0, 1), false),
                    "-z" => (new UvAlignOps.Vec3d(0, 0, -1), false),
                    _ => throw new ScriptApiException($"{api}: unknown edge '{s}'.",
                        "Use a 1-based edge number, \"bottom\", \"top\", \"+x\", \"-x\", \"+z\" or \"-z\"."),
                };
                int index = UvAlignOps.ExtremeEdge(corners, dir);
                if (index < 0)
                {
                    throw new ScriptApiException($"{api}: no edge of this face is furthest toward '{s}' (every edge is level that way, e.g. \"bottom\" on a floor).",
                        "Name the edge by number (1 to " + n.ToString(CultureInfo.InvariantCulture) + ") or by a horizontal direction such as \"-z\".");
                }

                return (index, hang);
            default:
                throw new ScriptApiException($"{api}: edge must be a 1-based edge number or \"bottom\", \"top\", \"+x\", \"-x\", \"+z\", \"-z\" (got {ScriptArgs.LuaTypeOf(edge)}).");
        }
    }
}
