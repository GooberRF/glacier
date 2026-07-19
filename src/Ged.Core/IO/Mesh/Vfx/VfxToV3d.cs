using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Vfx;

/// <summary>
/// Adapts a parsed <see cref="VfxFile"/> into the mesh-shaped <see cref="V3dFile"/>
/// that the whole GED mesh pipeline already consumes (GpuScene, thumbnails, hover
/// previews, drag ghosts, the dependency scanner). One VFX mesh becomes one
/// <see cref="V3dSubmesh"/> with a single LOD; faces are expanded to unique
/// (position, UV, normal) triangle vertices and grouped into one batch per material.
///
/// The rendered pose is RED's static snapshot — frame 0 / time 0 — matching how
/// RED.exe builds the effect object for the viewport. The frame-0 geometry is posed
/// with the same composition RED's pose routine uses (RF.exe FUN_0053f060 @0x0053f060,
/// RED.exe @0x004f9??0):
///   - morph meshes  -> frame-0 vertices, no transform;
///   - keyframed     -> pivot (scale, rotation, translation) then the keyframe TRS
///                      sampled at frame 0, applied to frame-0 vertices;
///   - otherwise     -> the per-frame TRS stored on frame 0.
/// Positions decode as center + short * multiplier (FUN_0053cca0 @0x0053cca0, no
/// /32767); vec3/quat are used as-is with no axis swap and w un-negated
/// (FUN_0052ca00 / FUN_0052ca50). Blending follows RED's load-time flag derivation
/// (FUN_0054b5b0 @0x0054b5b0): material additive (default true) -> additive pass;
/// self-illumination / fullbright (mesh flag 0x10) -> unlit; seethrough (0x20) or
/// opacity &lt; 1 -> alpha; else opaque. Effect faces render double-sided (RED poses
/// facing sprites toward the camera at runtime; a static editor preview shows the
/// geometry from any angle). See docs/internal/FEATURES.md for the approximations.
/// </summary>
public static class VfxToV3d
{
    /// <summary>Below this opacity a non-additive material routes to the alpha pass.</summary>
    private const float OpaqueThreshold = 0.999f;

    public static V3dFile Convert(VfxFile vfx)
    {
        ArgumentNullException.ThrowIfNull(vfx);
        var file = new V3dFile { Signature = V3dSignature.V3m };

        foreach (VfxMesh mesh in vfx.Meshes)
        {
            V3dSubmesh? submesh = ConvertMesh(vfx, mesh);
            if (submesh is not null)
            {
                file.Submeshes.Add(submesh);
            }
        }

        return file;
    }

    private static V3dSubmesh? ConvertMesh(VfxFile vfx, VfxMesh mesh)
    {
        VfxMeshFrame? rest = mesh.RestFrame;
        if (rest is null || rest.Positions.Length == 0 || mesh.Faces.Count == 0)
        {
            return null;
        }

        int ver = vfx.Version;
        Trs xf = RestTransform(mesh, rest, ver);

        // Pose the frame-0 vertices once (indexed by the mesh's vertex index).
        var worldPos = new Vec3[rest.Positions.Length];
        for (int i = 0; i < worldPos.Length; i++)
        {
            worldPos[i] = xf.Apply(rest.Positions[i]);
        }

        // RED marks the whole mesh additive if ANY of its face materials is additive
        // (FUN_0054b5b0). Fullbright likewise is a whole-mesh property.
        bool meshAdditive = false;
        bool meshFullbright = mesh.Flags.Fullbright;
        foreach (VfxMaterial m in EnumerateUsedMaterials(vfx, mesh))
        {
            meshAdditive |= m.Additive;
            meshFullbright |= m.SelfIlluminationAt0 > 0f;
        }

        bool useFrameUvs = ver >= 0x3000D && rest.Uvs.Length > 0;

        // One batch per distinct material (reference identity); null = untextured.
        var groups = new List<(VfxMaterial? Material, BatchBuilder Builder)>();
        var index = new Dictionary<VfxMaterial, BatchBuilder>(ReferenceEqualityComparer.Instance);
        BatchBuilder? nullBucket = null;

        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            VfxFace face = mesh.Faces[fi];
            VfxMaterial? mat = ResolveMaterial(vfx, mesh, face.MaterialIndex);

            BatchBuilder builder;
            if (mat is null)
            {
                builder = nullBucket ??= AddGroup(groups, null);
            }
            else if (!index.TryGetValue(mat, out builder!))
            {
                builder = AddGroup(groups, mat);
                index[mat] = builder;
            }

            Vec3 n = xf.RotateNormal(face.Normal);
            AppendCorner(builder, worldPos, face.I0, Uv0(face, rest, fi, 0, useFrameUvs), n);
            AppendCorner(builder, worldPos, face.I1, Uv0(face, rest, fi, 1, useFrameUvs), n);
            AppendCorner(builder, worldPos, face.I2, Uv0(face, rest, fi, 2, useFrameUvs), n);
        }

        var submesh = new V3dSubmesh
        {
            Name = mesh.Name,
            Offset = mesh.BoundingCenter,
            Radius = mesh.BoundingRadius,
        };
        var lod = new V3dLod();

        int slot = 0;
        foreach ((VfxMaterial? material, BatchBuilder builder) in groups)
        {
            if (builder.Triangles.Count == 0)
            {
                continue;
            }

            string tex = material?.DiffuseTextureName ?? string.Empty;
            float opacity = material is null ? 1f : material.OpacityAt0 * rest.Opacity;
            V3dBatchBlend blend = meshAdditive
                ? V3dBatchBlend.Additive
                : mesh.Flags.SeeThrough || opacity < OpaqueThreshold
                    ? V3dBatchBlend.Alpha
                    : V3dBatchBlend.Opaque;

            submesh.Materials.Add(new V3dMaterial { DiffuseMapName = tex });
            lod.Textures.Add(new V3dLodTexture { Id = (byte)slot, Filename = tex });
            lod.Batches.Add(new V3dBatch
            {
                TextureIndex = slot,
                NumVertices = builder.Positions.Count,
                NumTriangles = builder.Triangles.Count,
                Positions = builder.Positions.ToArray(),
                Normals = builder.Normals.ToArray(),
                TexCoords = builder.TexCoords.ToArray(),
                Triangles = builder.Triangles.ToArray(),
                Blend = blend,
                Unlit = meshFullbright,
                Opacity = Math.Clamp(opacity, 0f, 1f),
                SolidColor = material is { Type: VfxMaterialType.ColorOnly }
                    ? new RfColor((byte)Clamp255(material.SolidR), (byte)Clamp255(material.SolidG), (byte)Clamp255(material.SolidB), 255)
                    : null,
            });
            slot++;
        }

        if (lod.Batches.Count == 0)
        {
            return null;
        }

        submesh.Lods.Add(lod);
        return submesh;
    }

    private static BatchBuilder AddGroup(List<(VfxMaterial?, BatchBuilder)> groups, VfxMaterial? mat)
    {
        var b = new BatchBuilder();
        groups.Add((mat, b));
        return b;
    }

    private static void AppendCorner(BatchBuilder b, Vec3[] worldPos, int vertexIndex, Uv uv, Vec3 normal)
    {
        // A malformed/out-of-range index still needs a vertex so the triangle stays
        // well-formed; clamp to the origin rather than throwing.
        Vec3 p = vertexIndex >= 0 && vertexIndex < worldPos.Length ? worldPos[vertexIndex] : default;
        var tri = (ushort)b.Positions.Count;
        b.Positions.Add(p);
        b.Normals.Add(normal);
        b.TexCoords.Add(uv);
        if ((b.Positions.Count % 3) == 0)
        {
            // Effect faces render double-sided (0x20): a static preview shows them from any angle.
            b.Triangles.Add(new V3dTriangle((ushort)(tri - 2), (ushort)(tri - 1), tri, V3dTriangle.DoubleSided));
        }
    }

    private static Uv Uv0(VfxFace face, VfxMeshFrame rest, int faceIndex, int corner, bool useFrameUvs)
    {
        if (useFrameUvs)
        {
            int idx = (3 * faceIndex) + corner;
            return idx >= 0 && idx < rest.Uvs.Length ? rest.Uvs[idx] : default;
        }

        return corner switch
        {
            0 => face.Uv0,
            1 => face.Uv1,
            _ => face.Uv2,
        };
    }

    private static IEnumerable<VfxMaterial> EnumerateUsedMaterials(VfxFile vfx, VfxMesh mesh)
    {
        var seen = new HashSet<VfxMaterial>(ReferenceEqualityComparer.Instance);
        foreach (VfxFace f in mesh.Faces)
        {
            VfxMaterial? m = ResolveMaterial(vfx, mesh, f.MaterialIndex);
            if (m is not null && seen.Add(m))
            {
                yield return m;
            }
        }
    }

    private static VfxMaterial? ResolveMaterial(VfxFile vfx, VfxMesh mesh, int materialIndex)
    {
        if (vfx.Version >= 0x40000)
        {
            // 0-based index into the mesh's material-slot table, then into the global materials.
            if (materialIndex < 0 || materialIndex >= mesh.MaterialIndices.Length)
            {
                return null;
            }

            int gi = mesh.MaterialIndices[materialIndex];
            return gi >= 0 && gi < vfx.Materials.Count ? vfx.Materials[gi] : null;
        }

        // Legacy: 1-based index into the mesh's embedded materials.
        int idx = materialIndex - 1;
        return idx >= 0 && idx < mesh.EmbeddedMaterials.Count ? mesh.EmbeddedMaterials[idx] : null;
    }

    private static Trs RestTransform(VfxMesh mesh, VfxMeshFrame rest, int ver)
    {
        if (mesh.Flags.Morph)
        {
            return Trs.Identity;
        }

        if (mesh.IsKeyframed)
        {
            // RED's keyframed path: pivot first, then the keyframe TRS at frame 0.
            Trs pivot = ver >= 0x3000A
                ? new Trs(mesh.PivotTranslation, mesh.PivotRotation, mesh.PivotScale)
                : Trs.Identity;
            Trs key = KeyframeAtRest(mesh.Keyframes);
            return Trs.Compose(key, pivot);
        }

        if (rest.HasTransform)
        {
            return new Trs(rest.Translation, rest.Rotation, rest.Scale);
        }

        return Trs.Identity;
    }

    private static Trs KeyframeAtRest(VfxTransformKeyframes? kf)
    {
        if (kf is null)
        {
            return Trs.Identity;
        }

        Vec3 t = kf.Translation.Count > 0 ? kf.Translation[0].Value : default;
        VfxQuat r = kf.Rotation.Count > 0 ? kf.Rotation[0].Value : VfxQuat.Identity;
        Vec3 s = kf.Scale.Count > 0 ? kf.Scale[0].Value : new Vec3(1f, 1f, 1f);
        return new Trs(t, r, s);
    }

    private static int Clamp255(int v) => Math.Clamp(v, 0, 255);

    /// <summary>A translation/rotation/scale transform: world = T + R * (S ⊙ v).</summary>
    private readonly struct Trs
    {
        private readonly Vec3 _t;
        private readonly VfxQuat _r;
        private readonly Vec3 _s;

        public Trs(Vec3 t, VfxQuat r, Vec3 s)
        {
            _t = t;
            _r = r;
            _s = s;
        }

        public static Trs Identity => new(default, VfxQuat.Identity, new Vec3(1f, 1f, 1f));

        public Vec3 Apply(Vec3 v) => _t.Add(Rotate(_r, _s.Mul(v)));

        public Vec3 RotateNormal(Vec3 n) => Rotate(_r, n).Normalized();

        /// <summary>Compose so that <c>Compose(outer, inner).Apply(v) == outer.Apply(inner.Apply(v))</c>.</summary>
        public static Trs Compose(Trs outer, Trs inner) => new(
            outer.Apply(inner._t),
            Mul(outer._r, inner._r),
            outer._s.Mul(inner._s));

        private static Vec3 Rotate(VfxQuat q, Vec3 v)
        {
            // v' = v + 2w(u × v) + 2(u × (u × v)),  u = (x, y, z)
            var u = new Vec3(q.X, q.Y, q.Z);
            Vec3 t = u.Cross(v).Scale(2f);
            return v.Add(t.Scale(q.W)).Add(u.Cross(t));
        }

        private static VfxQuat Mul(VfxQuat a, VfxQuat b) => new(
            (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
            (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
            (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
            (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));
    }

    private sealed class BatchBuilder
    {
        public List<Vec3> Positions { get; } = new();

        public List<Vec3> Normals { get; } = new();

        public List<Uv> TexCoords { get; } = new();

        public List<V3dTriangle> Triangles { get; } = new();
    }
}
