using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Mesh;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Cookie-cutter primitive generators. Each produces a <see cref="Brush"/> with
/// local-space geometry centred on the origin: a shared vertex pool, faces with a
/// per-face texture slot and default planar UVs, and outward-facing plane normals.
/// Splits control tessellation. Pure and fully unit-tested — no GPU, VFS or IO
/// dependency beyond reading an already-parsed mesh for <see cref="FromMesh"/>.
/// </summary>
public static class BrushFactory
{
    /// <summary>Builds a brush of the requested shape from the given parameters.</summary>
    public static Brush Create(BrushCreateParams p, int uid, V3dFile? mesh = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        Geometry g = p.Shape switch
        {
            BrushShape.Box => Box(p.Width, p.Height, p.Depth, p.WidthSplits, p.HeightSplits, p.DepthSplits, p.Texture),
            BrushShape.Cylinder => Cylinder(p.Width, p.Height, p.Depth, RadialSides(p.WidthSplits), Stacks(p.HeightSplits), p.Texture),
            BrushShape.Cone => Cone(p.Width, p.Height, p.Depth, RadialSides(p.WidthSplits), p.Texture),
            BrushShape.Sphere => Sphere(p.Width, p.Height, p.Depth, RadialSides(p.WidthSplits), Math.Max(2, p.HeightSplits + 1), p.Texture),
            BrushShape.Wedge => Wedge(p.Width, p.Height, p.Depth, p.Texture),
            BrushShape.Face => FaceQuad(p.Width, p.Height, p.WidthSplits, p.HeightSplits, p.Texture),
            BrushShape.Mesh => FromMesh(mesh ?? throw new ArgumentNullException(nameof(mesh), "Mesh shape requires a parsed V3D file.")),
            _ => Box(p.Width, p.Height, p.Depth, 0, 0, 0, p.Texture),
        };

        // Every non-mesh creation path gets per-face orientation textures (ceiling/wall/
        // floor). Blank preferences resolve to the single authoring texture and then to
        // the stock rock default, so a brush is never left untextured. Mesh-cutter brushes
        // keep their per-material textures.
        if (p.Shape != BrushShape.Mesh)
        {
            ApplyOrientationTextures(g, p.EffectiveFloorTexture, p.EffectiveWallTexture, p.EffectiveCeilingTexture);
        }

        return new Brush
        {
            Uid = uid,
            Position = default,
            Rotation = Mat3.Identity,
            Geometry = g,
            Flags = p.ToFlags(),
            Life = p.Life,
            State = BrushState.Normal,
        };
    }

    // Radial side count from Width Splits (min 3, default 12 when unset).
    private static int RadialSides(int widthSplits) => widthSplits >= 3 ? widthSplits : 12;

    private static int Stacks(int heightSplits) => Math.Max(1, heightSplits + 1);

    // ---- Box ------------------------------------------------------------------

    /// <summary>A closed box centred on the origin. Each face is subdivided into a split grid.</summary>
    public static Geometry Box(float w, float h, float d, int wSplits, int hSplits, int dSplits, string texture)
    {
        var g = NewGeometry("box", texture);
        float hx = MathF.Abs(w) * 0.5f, hy = MathF.Abs(h) * 0.5f, hz = MathF.Abs(d) * 0.5f;
        int nx = wSplits + 1, ny = hSplits + 1, nz = dSplits + 1;

        // Each face: origin corner, u-edge vector, v-edge vector, u-segments, v-segments.
        AddGrid(g, new Vec3(hx, -hy, -hz), new Vec3(0, 2 * hy, 0), new Vec3(0, 0, 2 * hz), ny, nz); // +X
        AddGrid(g, new Vec3(-hx, -hy, -hz), new Vec3(0, 0, 2 * hz), new Vec3(0, 2 * hy, 0), nz, ny); // -X
        AddGrid(g, new Vec3(-hx, hy, -hz), new Vec3(0, 0, 2 * hz), new Vec3(2 * hx, 0, 0), nz, nx); // +Y
        AddGrid(g, new Vec3(-hx, -hy, -hz), new Vec3(2 * hx, 0, 0), new Vec3(0, 0, 2 * hz), nx, nz); // -Y
        AddGrid(g, new Vec3(-hx, -hy, hz), new Vec3(2 * hx, 0, 0), new Vec3(0, 2 * hy, 0), nx, ny); // +Z
        AddGrid(g, new Vec3(-hx, -hy, -hz), new Vec3(0, 2 * hy, 0), new Vec3(2 * hx, 0, 0), ny, nx); // -Z

        Finish(g);
        return g;
    }

    // ---- Cylinder -------------------------------------------------------------

    /// <summary>An (elliptical) cylinder along Y with polygonal caps.</summary>
    public static Geometry Cylinder(float w, float h, float d, int sides, int stacks, string texture)
    {
        var g = NewGeometry("cylinder", texture);
        sides = Math.Max(3, sides);
        stacks = Math.Max(1, stacks);
        float rx = MathF.Abs(w) * 0.5f, rz = MathF.Abs(d) * 0.5f, hy = MathF.Abs(h) * 0.5f;

        // Side ring vertices (stacks+1 rings), each ring closed.
        var rings = new int[stacks + 1][];
        for (int j = 0; j <= stacks; j++)
        {
            float y = -hy + (2 * hy * j / stacks);
            rings[j] = new int[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = MathF.Tau * i / sides;
                rings[j][i] = GeometryUtil.AddVertex(g, new Vec3(rx * MathF.Cos(a), y, rz * MathF.Sin(a)));
            }
        }

        for (int j = 0; j < stacks; j++)
        {
            for (int i = 0; i < sides; i++)
            {
                int i2 = (i + 1) % sides;
                AddFace(g, rings[j][i], rings[j][i2], rings[j + 1][i2], rings[j + 1][i]);
            }
        }

        AddPolygon(g, Enumerable.Range(0, sides).Select(i => rings[stacks][i]).ToArray()); // top cap
        AddPolygon(g, Enumerable.Range(0, sides).Select(i => rings[0][i]).ToArray()); // bottom cap

        Finish(g);
        return g;
    }

    // ---- Cone -----------------------------------------------------------------

    /// <summary>A cone: a polygonal base at -Y with triangular sides rising to an apex at +Y.</summary>
    public static Geometry Cone(float w, float h, float d, int sides, string texture)
    {
        var g = NewGeometry("cone", texture);
        sides = Math.Max(3, sides);
        float rx = MathF.Abs(w) * 0.5f, rz = MathF.Abs(d) * 0.5f, hy = MathF.Abs(h) * 0.5f;

        var baseRing = new int[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = MathF.Tau * i / sides;
            baseRing[i] = GeometryUtil.AddVertex(g, new Vec3(rx * MathF.Cos(a), -hy, rz * MathF.Sin(a)));
        }

        int apex = GeometryUtil.AddVertex(g, new Vec3(0, hy, 0));
        for (int i = 0; i < sides; i++)
        {
            int i2 = (i + 1) % sides;
            AddFace(g, baseRing[i], baseRing[i2], apex);
        }

        AddPolygon(g, baseRing); // base
        Finish(g);
        return g;
    }

    // ---- Sphere ---------------------------------------------------------------

    /// <summary>A UV ellipsoid: <paramref name="lon"/> longitude sides, <paramref name="lat"/> latitude bands.</summary>
    public static Geometry Sphere(float w, float h, float d, int lon, int lat, string texture)
    {
        var g = NewGeometry("sphere", texture);
        lon = Math.Max(3, lon);
        lat = Math.Max(2, lat);
        float rx = MathF.Abs(w) * 0.5f, ry = MathF.Abs(h) * 0.5f, rz = MathF.Abs(d) * 0.5f;

        int top = GeometryUtil.AddVertex(g, new Vec3(0, ry, 0));
        int bottom = GeometryUtil.AddVertex(g, new Vec3(0, -ry, 0));

        // Rings for latitude bands 1..lat-1 (exclude poles).
        var ringVerts = new int[lat - 1][];
        for (int j = 1; j < lat; j++)
        {
            float phi = MathF.PI * j / lat; // 0..PI from top
            float y = ry * MathF.Cos(phi);
            float rr = MathF.Sin(phi);
            ringVerts[j - 1] = new int[lon];
            for (int i = 0; i < lon; i++)
            {
                float theta = MathF.Tau * i / lon;
                ringVerts[j - 1][i] = GeometryUtil.AddVertex(g,
                    new Vec3(rx * rr * MathF.Cos(theta), y, rz * rr * MathF.Sin(theta)));
            }
        }

        // Top cap triangles.
        for (int i = 0; i < lon; i++)
        {
            AddFace(g, top, ringVerts[0][i], ringVerts[0][(i + 1) % lon]);
        }

        // Middle quads.
        for (int j = 0; j < lat - 2; j++)
        {
            for (int i = 0; i < lon; i++)
            {
                int i2 = (i + 1) % lon;
                AddFace(g, ringVerts[j][i], ringVerts[j][i2], ringVerts[j + 1][i2], ringVerts[j + 1][i]);
            }
        }

        // Bottom cap triangles.
        int last = lat - 2;
        for (int i = 0; i < lon; i++)
        {
            AddFace(g, bottom, ringVerts[last][(i + 1) % lon], ringVerts[last][i]);
        }

        Finish(g);
        return g;
    }

    // ---- Wedge ----------------------------------------------------------------

    /// <summary>A right-triangular prism (ramp) whose bounding box is centred on the origin.</summary>
    public static Geometry Wedge(float w, float h, float d, string texture)
    {
        var g = NewGeometry("wedge", texture);
        float hx = MathF.Abs(w) * 0.5f, hy = MathF.Abs(h) * 0.5f, hz = MathF.Abs(d) * 0.5f;

        // Triangle cross-section in XY, right angle at (-hx,-hy); extruded along Z.
        int a0 = GeometryUtil.AddVertex(g, new Vec3(-hx, -hy, -hz));
        int b0 = GeometryUtil.AddVertex(g, new Vec3(hx, -hy, -hz));
        int c0 = GeometryUtil.AddVertex(g, new Vec3(-hx, hy, -hz));
        int a1 = GeometryUtil.AddVertex(g, new Vec3(-hx, -hy, hz));
        int b1 = GeometryUtil.AddVertex(g, new Vec3(hx, -hy, hz));
        int c1 = GeometryUtil.AddVertex(g, new Vec3(-hx, hy, hz));

        AddFace(g, a0, b0, c0); // -Z cap
        AddFace(g, a1, b1, c1); // +Z cap
        AddFace(g, a0, b0, b1, a1); // bottom (-Y)
        AddFace(g, a0, c0, c1, a1); // back (-X)
        AddFace(g, b0, c0, c1, b1); // hypotenuse (ramp)

        Finish(g);
        return g;
    }

    // ---- Face -----------------------------------------------------------------

    /// <summary>A single planar quad in the XY plane facing +Z, optionally subdivided.</summary>
    public static Geometry FaceQuad(float w, float h, int wSplits, int hSplits, string texture)
    {
        var g = NewGeometry("face", texture);
        float hx = MathF.Abs(w) * 0.5f, hy = MathF.Abs(h) * 0.5f;
        AddGrid(g, new Vec3(-hx, -hy, 0), new Vec3(2 * hx, 0, 0), new Vec3(0, 2 * hy, 0), wSplits + 1, hSplits + 1);

        // Planar shape: set the normal explicitly (+Z) rather than via the outward test.
        GeometryUtil.CompactUnusedVertices(g);
        foreach (Face f in g.Faces)
        {
            f.Plane = new RfPlane(new Vec3(0, 0, 1), 0f);
            GeometryUtil.AssignPlanarUv(g, f);
        }

        return g;
    }

    // ---- Mesh -----------------------------------------------------------------

    /// <summary>
    /// Converts a mesh's LOD0 triangles into brush faces (one 3-vertex face per
    /// triangle), pulling textures from the mesh materials. Alpine's mesh-cutter
    /// parity: the result is a brush whose geometry mirrors the mesh surface.
    /// </summary>
    public static Geometry FromMesh(V3dFile mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var g = NewGeometry("mesh", BrushCreateParams.DefaultTexture);
        g.Textures.Clear();

        foreach (V3dSubmesh sm in mesh.Submeshes)
        {
            if (sm.Lods.Count == 0)
            {
                continue;
            }

            V3dLod lod = sm.Lods[0];
            foreach (V3dBatch batch in lod.Batches)
            {
                string tex = sm.ResolveBatchTexture(lod, batch);
                int texIndex = GeometryUtil.EnsureTexture(g, string.IsNullOrEmpty(tex) ? BrushCreateParams.DefaultTexture : tex);

                for (int t = 0; t < batch.NumTriangles; t++)
                {
                    V3dTriangle tri = batch.Triangles[t];
                    if (tri.I0 >= batch.NumVertices || tri.I1 >= batch.NumVertices || tri.I2 >= batch.NumVertices)
                    {
                        continue;
                    }

                    int v0 = GeometryUtil.AddVertex(g, batch.Positions[tri.I0]);
                    int v1 = GeometryUtil.AddVertex(g, batch.Positions[tri.I1]);
                    int v2 = GeometryUtil.AddVertex(g, batch.Positions[tri.I2]);
                    if (v0 == v1 || v1 == v2 || v0 == v2)
                    {
                        continue; // degenerate triangle
                    }

                    var face = new Face { Texture = texIndex, SurfaceIndex = -1, RoomIndex = -1, FaceId = g.Faces.Count };
                    AddCornerUv(face, v0, batch.TexCoords, tri.I0);
                    AddCornerUv(face, v1, batch.TexCoords, tri.I1);
                    AddCornerUv(face, v2, batch.TexCoords, tri.I2);
                    g.Faces.Add(face);
                }
            }
        }

        if (g.Textures.Count == 0)
        {
            g.Textures.Add(BrushCreateParams.DefaultTexture);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return g;
    }

    private static void AddCornerUv(Face face, int poolIndex, Uv[] uvs, int srcIndex)
    {
        Uv uv = srcIndex < uvs.Length ? uvs[srcIndex] : default;
        face.Vertices.Add(new FaceVertex { Index = poolIndex, TextureCoords = uv });
    }

    // ---- Default textures by orientation --------------------------------------

    /// <summary>Cosine threshold above which a face counts as horizontal (floor/ceiling vs wall).</summary>
    private const float HorizontalDot = 0.7f;

    /// <summary>
    /// Assigns each face the floor / wall / ceiling default texture by its outward
    /// normal (RED convention): a face pointing up is a floor, pointing down is a
    /// ceiling, and anything nearer vertical is a wall. Rebuilds the texture table
    /// so only the used defaults remain.
    /// </summary>
    public static void ApplyOrientationTextures(Geometry g, string floor, string wall, string ceiling)
    {
        g.Textures.Clear();
        foreach (Face f in g.Faces)
        {
            float ny = f.Plane.Normal.Y;
            string tex = ny >= HorizontalDot ? floor : (ny <= -HorizontalDot ? ceiling : wall);
            f.Texture = GeometryUtil.EnsureTexture(g, tex);
        }

        if (g.Textures.Count == 0)
        {
            g.Textures.Add(wall);
        }
    }

    // ---- Shared builders ------------------------------------------------------

    private static Geometry NewGeometry(string name, string texture)
    {
        var g = new Geometry { Name = name };
        g.Textures.Add(texture);
        return g;
    }

    /// <summary>Adds a subdivided quad: origin + u-edge + v-edge split into nu×nv cells.</summary>
    private static void AddGrid(Geometry g, Vec3 origin, Vec3 uEdge, Vec3 vEdge, int nu, int nv)
    {
        nu = Math.Max(1, nu);
        nv = Math.Max(1, nv);
        var grid = new int[nu + 1, nv + 1];
        for (int i = 0; i <= nu; i++)
        {
            for (int j = 0; j <= nv; j++)
            {
                Vec3 p = origin.Add(uEdge.Scale((float)i / nu)).Add(vEdge.Scale((float)j / nv));
                grid[i, j] = GeometryUtil.AddVertex(g, p);
            }
        }

        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++)
            {
                AddFace(g, grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]);
            }
        }
    }

    private static void AddFace(Geometry g, params int[] indices) => AddPolygon(g, indices);

    private static void AddPolygon(Geometry g, int[] indices)
    {
        var face = new Face { Texture = 0, SurfaceIndex = -1, RoomIndex = -1, FaceId = g.Faces.Count };
        foreach (int idx in indices)
        {
            face.Vertices.Add(new FaceVertex { Index = idx });
        }

        g.Faces.Add(face);
    }

    /// <summary>Compacts the pool, orients all faces outward, then assigns default planar UVs.</summary>
    private static void Finish(Geometry g)
    {
        GeometryUtil.CompactUnusedVertices(g);
        OrientOutward(g);
        GeometryUtil.AssignAllPlanarUv(g);
    }

    /// <summary>
    /// Reverses any face whose plane normal points toward the interior (the mean
    /// of all vertices), then stores the corrected outward plane. Correct for the
    /// convex primitives here, whose vertex mean is strictly inside.
    /// </summary>
    public static void OrientOutward(Geometry g)
    {
        Vec3 interior = GeometryUtil.Centroid(g.Vertices);
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            List<Vec3> poly = GeometryUtil.Corners(g, f);
            Vec3 n = GeometryUtil.Normal(poly);
            Vec3 c = GeometryUtil.Centroid(poly);
            if (n.Dot(c.Sub(interior)) < 0f)
            {
                f.Vertices.Reverse();
            }

            GeometryUtil.RecomputePlane(g, f);
        }
    }
}
