using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Vfx;

/// <summary>Material class of a VFX material (vfx.ksy enum material_type).</summary>
public enum VfxMaterialType
{
    /// <summary>Single texture.</summary>
    Image = 0,

    /// <summary>Two textures cross-blended by a per-frame mix factor.</summary>
    Vmix = 1,

    /// <summary>Flat solid colour, no texture.</summary>
    ColorOnly = 2,
}

/// <summary>Texture-animation playback mode (vfx.ksy enum anim_type). PingPong is unused by RF.</summary>
public enum VfxAnimType
{
    Loop = 0,
    PingPong = 1,
    Once = 2,
}

/// <summary>A quaternion as stored on disk (x, y, z, w).</summary>
public readonly record struct VfxQuat(float X, float Y, float Z, float W)
{
    public static VfxQuat Identity => new(0f, 0f, 0f, 1f);
}

/// <summary>
/// A texture reference inside a material (vfx.ksy type material_texture). The
/// <see cref="Name"/> is a bare bitmap file name resolved through the normal VFS
/// texture supersede chain; <c>$original_map</c>/<c>$original_map_rgb</c> are
/// engine placeholders that carry no real file.
/// </summary>
public sealed class VfxTexture
{
    public string Name { get; set; } = string.Empty;

    public int StartFrame { get; set; }

    public float PlaybackRate { get; set; } = 1f;

    public VfxAnimType AnimType { get; set; }

    /// <summary>True when the name is a real bitmap reference (not an engine placeholder / empty).</summary>
    public bool IsRealFile =>
        !string.IsNullOrEmpty(Name) && !Name.StartsWith('$');
}

/// <summary>
/// A VFX material, unified across the legacy per-mesh form (vfx.ksy
/// mesh_material_old, versions &lt; 0x40000) and the top-level material section
/// (vfx.ksy material, versions &gt;= 0x40000). Only the fields GED needs to render
/// and to gather dependencies are surfaced; per-frame mix / self-illumination /
/// opacity sample arrays are retained in full.
/// </summary>
public sealed class VfxMaterial
{
    public VfxMaterialType Type { get; set; }

    /// <summary>Additive blending was requested in the "Advanced Transparency" section.</summary>
    public bool Additive { get; set; }

    public VfxTexture? Tex0 { get; set; }

    public VfxTexture? Tex1 { get; set; }

    /// <summary>Per-frame cross-blend factors for <see cref="VfxMaterialType.Vmix"/> (0 = tex0, 1 = tex1).</summary>
    public float[] MixFrames { get; set; } = Array.Empty<float>();

    /// <summary>Per-frame self-illumination samples in [0, 1]; a single value for legacy materials.</summary>
    public float[] SelfIllumination { get; set; } = Array.Empty<float>();

    /// <summary>Per-frame opacity samples in [0, 1] (version &gt;= 0x40005); empty otherwise.</summary>
    public float[] Opacity { get; set; } = Array.Empty<float>();

    public float SpecularLevel { get; set; }

    public float Glossiness { get; set; }

    public float ReflectionAmount { get; set; }

    public string ReflTextureName { get; set; } = string.Empty;

    /// <summary>Solid colour (0-255) for <see cref="VfxMaterialType.ColorOnly"/>.</summary>
    public int SolidR { get; set; }

    public int SolidG { get; set; }

    public int SolidB { get; set; }

    /// <summary>The first self-illumination sample (0 when none), used as the static self-illum level.</summary>
    public float SelfIlluminationAt0 => SelfIllumination.Length > 0 ? SelfIllumination[0] : 0f;

    /// <summary>The first opacity sample (1 when none authored), used as the static opacity level.</summary>
    public float OpacityAt0 => Opacity.Length > 0 ? Opacity[0] : 1f;

    /// <summary>The diffuse bitmap this material draws with, or empty for a color-only material.</summary>
    public string DiffuseTextureName => Tex0 is { IsRealFile: true } ? Tex0.Name : string.Empty;
}

/// <summary>Mesh flags word (vfx.ksy mesh_flags).</summary>
public readonly record struct VfxMeshFlags(uint Raw)
{
    /// <summary>Camera-facing sprite (billboard).</summary>
    public bool Facing => (Raw & 0x00000001) != 0;

    public bool NoInterp => (Raw & 0x00000002) != 0;

    /// <summary>Per-frame morphed geometry (positions stored every frame).</summary>
    public bool Morph => (Raw & 0x00000004) != 0;

    public bool Fire => (Raw & 0x00000008) != 0;

    /// <summary>Rendered unlit at full brightness.</summary>
    public bool Fullbright => (Raw & 0x00000010) != 0;

    /// <summary>Draws translucent / see-through.</summary>
    public bool SeeThrough => (Raw & 0x00000020) != 0;

    public bool Corona => (Raw & 0x00000040) != 0;

    public bool Sky => (Raw & 0x00000080) != 0;

    /// <summary>Per-frame UVs are stored (animated texture coordinates).</summary>
    public bool DumpUvs => (Raw & 0x00000100) != 0;

    /// <summary>Camera-facing rod (a facing sprite with a fixed up axis).</summary>
    public bool FacingRod => (Raw & 0x00000800) != 0;
}

/// <summary>One triangle of a VFX mesh (vfx.ksy mesh_face).</summary>
public sealed class VfxFace
{
    /// <summary>Vertex indices into the mesh position array.</summary>
    public int I0 { get; set; }

    public int I1 { get; set; }

    public int I2 { get; set; }

    /// <summary>Per-corner UVs, present only for legacy versions (&lt; 0x3000D); newer versions carry UVs per frame.</summary>
    public Uv Uv0 { get; set; }

    public Uv Uv1 { get; set; }

    public Uv Uv2 { get; set; }

    public Vec3 Normal { get; set; }

    /// <summary>
    /// Material selector. For version &gt;= 0x40000 this is a 0-based index into the
    /// mesh's <see cref="VfxMesh.MaterialIndices"/> array (or -1 for none); for older
    /// versions it is a 1-based index into <see cref="VfxMesh.EmbeddedMaterials"/>.
    /// </summary>
    public int MaterialIndex { get; set; }

    public int SmoothingGroup { get; set; }
}

/// <summary>
/// One animation frame of a VFX mesh (vfx.ksy mesh_frame). Frame 0 (and every frame
/// of a morphed mesh) carries decoded vertex <see cref="Positions"/>; other frames
/// carry only a transform. Newer versions store per-frame <see cref="Uvs"/>.
/// </summary>
public sealed class VfxMeshFrame
{
    /// <summary>True when this frame stores geometry (positions/UVs) rather than only a transform.</summary>
    public bool HasGeometry { get; set; }

    public Vec3 Center { get; set; }

    public Vec3 Multiplier { get; set; }

    /// <summary>Decoded vertex positions (center + short * multiplier); empty on transform-only frames.</summary>
    public Vec3[] Positions { get; set; } = Array.Empty<Vec3>();

    /// <summary>Per-face-corner UVs (3 * num_faces), for version &gt;= 0x3000D; empty otherwise.</summary>
    public Uv[] Uvs { get; set; } = Array.Empty<Uv>();

    public bool HasTransform { get; set; }

    public Vec3 Translation { get; set; }

    public VfxQuat Rotation { get; set; } = VfxQuat.Identity;

    public Vec3 Scale { get; set; } = new(1f, 1f, 1f);

    /// <summary>Whole-mesh opacity for this frame in [0, 1] (version &lt; 0x40005).</summary>
    public float Opacity { get; set; } = 1f;
}

/// <summary>A vector keyframe (vfx.ksy vec3_keyframe): time is frame-number * 320.</summary>
public readonly record struct VfxVec3Key(int Time, Vec3 Value, Vec3 InTangent, Vec3 OutTangent);

/// <summary>A quaternion keyframe (vfx.ksy quat_keyframe) with TCB spline parameters.</summary>
public readonly record struct VfxQuatKey(
    int Time, VfxQuat Value, float Tension, float Continuity, float Bias, float EaseIn, float EaseOut);

/// <summary>The transform keyframe track lists of a keyframed mesh (vfx.ksy mesh_transform_keyframe_list).</summary>
public sealed class VfxTransformKeyframes
{
    public List<VfxVec3Key> Translation { get; } = new();

    public List<VfxQuatKey> Rotation { get; } = new();

    public List<VfxVec3Key> Scale { get; } = new();
}

/// <summary>A single VFX mesh object (vfx.ksy mesh section).</summary>
public sealed class VfxMesh
{
    public string Name { get; set; } = string.Empty;

    public string ParentName { get; set; } = string.Empty;

    public int NumVertices { get; set; }

    public VfxMeshFlags Flags { get; set; }

    public List<VfxFace> Faces { get; } = new();

    /// <summary>All animation frames; frame 0 always carries geometry.</summary>
    public List<VfxMeshFrame> Frames { get; } = new();

    /// <summary>Version &gt;= 0x40000: local material slot -&gt; global material index (into <see cref="VfxFile.Materials"/>).</summary>
    public int[] MaterialIndices { get; set; } = Array.Empty<int>();

    /// <summary>Version &lt; 0x40000: materials embedded in the mesh, indexed 1-based by faces.</summary>
    public List<VfxMaterial> EmbeddedMaterials { get; } = new();

    public Vec3 BoundingCenter { get; set; }

    public float BoundingRadius { get; set; }

    public int FramesPerSecond { get; set; } = 15;

    public bool IsKeyframed { get; set; }

    /// <summary>Pivot transform applied before the keyframe transform (keyframed meshes, version &gt;= 0x3000A).</summary>
    public Vec3 PivotTranslation { get; set; }

    public VfxQuat PivotRotation { get; set; } = VfxQuat.Identity;

    public Vec3 PivotScale { get; set; } = new(1f, 1f, 1f);

    public VfxTransformKeyframes? Keyframes { get; set; }

    /// <summary>Bytes left unconsumed after parsing this section body — 0 for a correct parse.</summary>
    public int TrailingBytes { get; set; }

    /// <summary>The geometry frame used for a static render (frame 0), or null if none.</summary>
    public VfxMeshFrame? RestFrame => Frames.Count > 0 ? Frames[0] : null;
}

/// <summary>A retained (parsed but not simulated) particle-system section (vfx.ksy particle_system).</summary>
public sealed class VfxParticleSystem
{
    public string Name { get; set; } = string.Empty;

    public string ParentName { get; set; } = string.Empty;

    public int ParticleCount { get; set; }

    /// <summary>Version &gt;= 0x40000: global material index; -1 for the legacy embedded form.</summary>
    public int MaterialIndex { get; set; } = -1;

    /// <summary>Embedded material (legacy versions &lt; 0x40000), or null.</summary>
    public VfxMaterial? EmbeddedMaterial { get; set; }

    public int TrailingBytes { get; set; }
}

/// <summary>A retained (parsed, not simulated) named sub-object with only its identity kept.</summary>
public sealed class VfxNamedObject
{
    public string Name { get; set; } = string.Empty;

    public string ParentName { get; set; } = string.Empty;

    public int TrailingBytes { get; set; }
}

/// <summary>
/// A fully-parsed Red Faction VFX effect file (magic "VSFX"). Meshes and materials
/// are parsed completely (geometry, per-frame transforms, UV frames, mix /
/// self-illumination / opacity sample arrays); lights, dummies, particle systems,
/// spacewarps and cameras are retained with their identity and (for particle
/// systems) their material, but not simulated. See research/rf_decomp/file-formats/vfx.ksy.
/// </summary>
public sealed class VfxFile
{
    public int Version { get; set; }

    public int Flags { get; set; }

    /// <summary>Total animation length: number of frames - 1, at 15 fps.</summary>
    public int EndFrame { get; set; }

    public List<VfxMesh> Meshes { get; } = new();

    /// <summary>Top-level materials (version &gt;= 0x40000). Empty for legacy files (materials live in the mesh).</summary>
    public List<VfxMaterial> Materials { get; } = new();

    public List<VfxParticleSystem> ParticleSystems { get; } = new();

    public List<VfxNamedObject> Dummies { get; } = new();

    public List<VfxNamedObject> Lights { get; } = new();

    public List<VfxNamedObject> Spacewarps { get; } = new();

    public List<VfxNamedObject> Cameras { get; } = new();

    /// <summary>Count of chain (spline) sections seen — retained by count only.</summary>
    public int ChainCount { get; set; }

    /// <summary>Count of material-modifier sections seen — retained by count only.</summary>
    public int MaterialModifierCount { get; set; }

    /// <summary>Count of selection-set sections seen — retained by count only.</summary>
    public int SelsetCount { get; set; }

    // ---- Header allocation counts (used for corpus invariants) ----

    public int HdrNumMeshes { get; set; }

    public int HdrNumLights { get; set; }

    public int HdrNumDummies { get; set; }

    public int HdrNumParticleSystems { get; set; }

    public int HdrNumSpacewarps { get; set; }

    public int HdrNumCameras { get; set; }

    public int HdrNumMaterials { get; set; }

    public int HdrNumFaces { get; set; }

    /// <summary>
    /// Bytes left unconsumed after parsing each fully-parsed section body (mesh,
    /// material, particle system, spacewarp, dummy, light, camera). A correct parse
    /// leaves 0 for every entry — the strongest per-section integrity invariant,
    /// asserted across the whole stock corpus.
    /// </summary>
    public List<int> SectionTrailingBytes { get; } = new();

    /// <summary>Every real bitmap this effect references, across mesh + global + particle materials.</summary>
    public IReadOnlyList<string> ReferencedTextures()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(VfxMaterial? m)
        {
            if (m is null)
            {
                return;
            }

            foreach (VfxTexture? t in new[] { m.Tex0, m.Tex1 })
            {
                if (t is { IsRealFile: true } && seen.Add(t.Name))
                {
                    result.Add(t.Name);
                }
            }

            if (!string.IsNullOrEmpty(m.ReflTextureName) && !m.ReflTextureName.StartsWith('$') && seen.Add(m.ReflTextureName))
            {
                result.Add(m.ReflTextureName);
            }
        }

        foreach (VfxMaterial m in Materials)
        {
            Add(m);
        }

        foreach (VfxMesh mesh in Meshes)
        {
            foreach (VfxMaterial m in mesh.EmbeddedMaterials)
            {
                Add(m);
            }
        }

        foreach (VfxParticleSystem ps in ParticleSystems)
        {
            Add(ps.EmbeddedMaterial);
        }

        return result;
    }
}
