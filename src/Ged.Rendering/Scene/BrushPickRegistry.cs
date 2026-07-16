using System.Collections.Generic;
using System.Numerics;

namespace Ged.Rendering.Scene;

/// <summary>
/// Per-scene mapping from a brush face/vertex pick payload back to the
/// <c>(brushUid, index)</c> it refers to. Brush face and vertex pick ids use
/// dedicated <see cref="Picking.PickKind"/> values whose 28-bit payload is an
/// index into this registry, so they never collide with compiled-geometry face
/// picks or object UIDs. Rebuilt whenever the scene is rebuilt for a new mode.
/// </summary>
public sealed class BrushPickRegistry
{
    private readonly List<(int BrushUid, int FaceIndex)> _faces = new();
    private readonly List<VertexRef> _vertices = new();

    /// <summary>A registered brush vertex: its owning brush, pool index and world position.</summary>
    public readonly record struct VertexRef(int BrushUid, int VertexIndex, Vector3 World);

    public IReadOnlyList<VertexRef> Vertices => _vertices;

    /// <summary>Registers a brush face and returns its pick payload index.</summary>
    public int AddFace(int brushUid, int faceIndex)
    {
        _faces.Add((brushUid, faceIndex));
        return _faces.Count - 1;
    }

    /// <summary>Registers a brush vertex and returns its pick payload index.</summary>
    public int AddVertex(int brushUid, int vertexIndex, Vector3 world)
    {
        _vertices.Add(new VertexRef(brushUid, vertexIndex, world));
        return _vertices.Count - 1;
    }

    public bool TryResolveFace(int payload, out int brushUid, out int faceIndex)
    {
        if (payload >= 0 && payload < _faces.Count)
        {
            (brushUid, faceIndex) = _faces[payload];
            return true;
        }

        brushUid = faceIndex = -1;
        return false;
    }

    public bool TryResolveVertex(int payload, out int brushUid, out int vertexIndex)
    {
        if (payload >= 0 && payload < _vertices.Count)
        {
            VertexRef v = _vertices[payload];
            brushUid = v.BrushUid;
            vertexIndex = v.VertexIndex;
            return true;
        }

        brushUid = vertexIndex = -1;
        return false;
    }
}
