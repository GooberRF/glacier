using System.Collections.Generic;
using System.Numerics;
using Ged.App.Services;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The CPU nearest-vertex pick search (item 2): a near-miss on a tiny vertex dot still resolves to
/// the nearest registered vertex within a screen radius, ties by nearest, rejects beyond the radius,
/// and ignores vertices behind the camera — all off the pick ray, independent of the id buffer.
/// </summary>
public sealed class VertexPickSearchTests
{
    // Camera at the origin looking down +Z; a constant 0.01 m per screen pixel.
    private static readonly Vector3 Origin = Vector3.Zero;
    private static readonly Vector3 Dir = Vector3.UnitZ;
    private static float Wpp(Vector3 _) => 0.01f;

    [Fact]
    public void Picks_The_Nearest_Vertex_Within_The_Radius()
    {
        var verts = new List<BrushPickRegistry.VertexRef>
        {
            new(7, 2, new Vector3(0.03f, 0f, 10f)), // 3 px off the ray
            new(7, 5, new Vector3(0.06f, 0f, 10f)), // 6 px off the ray
        };

        bool ok = VertexPickSearch.TryNearest(verts, Origin, Dir, Wpp, 8f, out int uid, out int vi);

        Assert.True(ok);
        Assert.Equal(7, uid);
        Assert.Equal(2, vi); // the 3 px vertex beats the 6 px vertex
    }

    [Fact]
    public void Rejects_A_Vertex_Beyond_The_Radius()
    {
        var verts = new List<BrushPickRegistry.VertexRef> { new(1, 0, new Vector3(0.2f, 0f, 10f)) }; // 20 px

        Assert.False(VertexPickSearch.TryNearest(verts, Origin, Dir, Wpp, 8f, out _, out _));
    }

    [Fact]
    public void Ignores_Vertices_Behind_The_Camera()
    {
        var verts = new List<BrushPickRegistry.VertexRef> { new(4, 9, new Vector3(0.01f, 0f, -5f)) }; // on-axis but behind

        Assert.False(VertexPickSearch.TryNearest(verts, Origin, Dir, Wpp, 8f, out _, out _));
    }

    [Fact]
    public void Empty_Registry_Returns_Nothing()
    {
        Assert.False(VertexPickSearch.TryNearest(
            new List<BrushPickRegistry.VertexRef>(), Origin, Dir, Wpp, 8f, out _, out _));
    }

    [Fact]
    public void Overlapping_Dots_Resolve_To_The_Nearer_Camera_Vertex()
    {
        // Two dots project to nearly the same screen point (both ~1 px off the ray) at different
        // depths. The nearer-camera vertex must win regardless of list order (B2): clicking a dot
        // never grabs the vertex hidden behind it.
        var farFirst = new List<BrushPickRegistry.VertexRef>
        {
            new(7, 1, new Vector3(0.01f, 0f, 30f)), // far
            new(7, 2, new Vector3(0.01f, 0f, 10f)), // near, same screen position
        };
        Assert.True(VertexPickSearch.TryNearest(farFirst, Origin, Dir, Wpp, 8f, out _, out int a));
        Assert.Equal(2, a);

        var nearFirst = new List<BrushPickRegistry.VertexRef>
        {
            new(7, 2, new Vector3(0.01f, 0f, 10f)), // near
            new(7, 1, new Vector3(0.01f, 0f, 30f)), // far
        };
        Assert.True(VertexPickSearch.TryNearest(nearFirst, Origin, Dir, Wpp, 8f, out _, out int b));
        Assert.Equal(2, b);
    }
}
