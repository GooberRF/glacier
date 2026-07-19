using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Vfx;

/// <summary>
/// Reads Red Faction VFX effect files (magic "VSFX", vfx.ksy versions 0x30000+; the
/// game ships 0x30008-0x40006) into a <see cref="VfxFile"/>. Meshes and materials
/// are parsed in full — geometry (decoded compressed positions), per-frame
/// transforms, per-frame UVs, and mix / self-illumination / opacity sample arrays —
/// while lights, dummies, particle systems, spacewarps and cameras are retained by
/// identity (particle systems also keep their material, for dependency scanning).
///
/// Structure and field order follow research/rf_decomp/file-formats/vfx.ksy
/// (reverse-engineered by Rafał Harabień); the C# below is an independent port,
/// validated to consume every section of every one of the 61 stock .vfx files
/// byte-exactly (0 trailing bytes) across versions 0x30008, 0x3000D-0x3000F,
/// 0x30012 and 0x40006. The compressed-position decode
/// (position = center + short * multiplier) was confirmed empirically against the
/// stored bounding spheres (e.g. grabber_thrusterfx / Lil_RedEyeFlare match exactly).
/// </summary>
public static class VfxReader
{
    private const int Magic = 0x58465356; // 'VSFX' little-endian ('V','S','F','X')

    // Section fourcc codes (vfx.ksy enum section_type), stored little-endian.
    private const int SecMesh = 0x4F584653;             // 'sfxo'
    private const int SecMaterial = 0x4C54414D;         // 'matl'
    private const int SecParticleSystem = 0x54524150;   // 'part'
    private const int SecSelset = 0x534C4553;           // 'sels'
    private const int SecLight = 0x54474C41;            // 'algt'
    private const int SecSpacewarp = 0x50524157;        // 'warp'
    private const int SecChain = 0x454E4843;            // 'chne'
    private const int SecMaterialModifier = 0x444F4D4D; // 'mmod'
    private const int SecCamera = 0x41524D43;           // 'cmra'
    private const int SecDummy = 0x594D4D44;            // 'dmmy'

    private const int MaxCount = 1 << 24; // guard against absurd allocation counts on malformed input

    /// <summary>Returns true when <paramref name="data"/> begins with the "VSFX" magic.</summary>
    public static bool IsVfx(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'V' && data[1] == (byte)'S' && data[2] == (byte)'F' && data[3] == (byte)'X';

    public static VfxFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ReadCore(data);
        }
        catch (EndOfStreamException ex)
        {
            throw new VfxFormatException("Unexpected end of VFX data.", ex);
        }
    }

    private static VfxFile ReadCore(byte[] data)
    {
        var r = new RfReader(data);
        if (r.ReadI32() != Magic)
        {
            throw new VfxFormatException("Not a VFX file (missing 'VSFX' magic).");
        }

        var file = new VfxFile();
        int ver = r.ReadI32();
        file.Version = ver;

        if (ver >= 0x30008)
        {
            file.Flags = r.ReadI32();
        }

        file.EndFrame = r.ReadI32();
        file.HdrNumMeshes = r.ReadI32();
        file.HdrNumLights = r.ReadI32();
        file.HdrNumDummies = r.ReadI32();
        file.HdrNumParticleSystems = r.ReadI32();
        file.HdrNumSpacewarps = r.ReadI32();
        file.HdrNumCameras = r.ReadI32();
        if (ver >= 0x3000F)
        {
            r.ReadI32(); // num_selsets
        }

        if (ver >= 0x40000)
        {
            file.HdrNumMaterials = r.ReadI32();
        }

        if (ver >= 0x40002)
        {
            r.ReadI32(); // num_mix_frames
        }

        if (ver >= 0x40003)
        {
            r.ReadI32(); // num_self_illumination_frames
        }

        if (ver >= 0x40005)
        {
            r.ReadI32(); // num_opacity_frames
        }

        if (ver < 0x3000A)
        {
            r.ReadI32(); // unk_1 (unused)
        }

        file.HdrNumFaces = r.ReadI32();
        r.ReadI32(); // num_mesh_material_indices
        r.ReadI32(); // num_vertex_normals
        r.ReadI32(); // num_adjacent_faces
        r.ReadI32(); // num_mesh_frames
        if (ver >= 0x3000D)
        {
            r.ReadI32(); // num_uv_frames
        }

        if (ver >= 0x30009)
        {
            r.ReadI32(); // num_mesh_transform_frames
            r.ReadI32(); // num_mesh_transform_keyframe_lists
            r.ReadI32(); // num_mesh_translation_keys
            r.ReadI32(); // num_mesh_rotation_keys
            r.ReadI32(); // num_mesh_scale_keys
        }

        r.ReadI32(); // num_light_frames
        r.ReadI32(); // num_dummy_frames
        r.ReadI32(); // num_part_sys_frames
        r.ReadI32(); // num_spacewarp_frames
        r.ReadI32(); // num_camera_frames
        if (ver >= 0x3000F)
        {
            r.ReadI32(); // num_selset_objects
        }

        // Sections repeat to end-of-file; each carries its own byte length so an
        // unrecognised or retained-only section resyncs the cursor exactly.
        while (r.Remaining >= 8)
        {
            int type = r.ReadI32();
            int len = r.ReadI32();
            if (len < 4)
            {
                break; // malformed length; stop rather than loop
            }

            int bodyLen = len - 4;
            if (bodyLen > r.Remaining)
            {
                bodyLen = r.Remaining;
            }

            var body = new RfReader(r.ReadBytes(bodyLen));
            DispatchSection(file, type, body, ver);
        }

        return file;
    }

    private static void DispatchSection(VfxFile file, int type, RfReader body, int ver)
    {
        switch (type)
        {
            case SecMesh:
                file.Meshes.Add(ReadMesh(body, ver));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecMaterial:
                file.Materials.Add(ReadGlobalMaterial(body, ver));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecParticleSystem:
                file.ParticleSystems.Add(ReadParticleSystem(body, ver));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecSpacewarp:
                file.Spacewarps.Add(ReadSpacewarp(body));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecDummy:
                file.Dummies.Add(ReadDummy(body));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecLight:
                file.Lights.Add(ReadLight(body));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecCamera:
                file.Cameras.Add(ReadNamedHeaderOnly(body));
                file.SectionTrailingBytes.Add(body.Remaining);
                break;
            case SecChain:
                file.ChainCount++;
                break;
            case SecMaterialModifier:
                file.MaterialModifierCount++;
                break;
            case SecSelset:
                file.SelsetCount++;
                break;
            default:
                break; // unknown section: already resynced by length
        }
    }

    // ---- Mesh ----------------------------------------------------------------

    private static VfxMesh ReadMesh(RfReader r, int ver)
    {
        var mesh = new VfxMesh
        {
            Name = r.ReadZString(),
            ParentName = r.ReadZString(),
        };

        r.ReadU8(); // save_parent (unused by the engine)

        int nv = ClampCount(r.ReadI32());
        mesh.NumVertices = nv;
        if (ver < 0x3000A)
        {
            r.Position += 12 * nv; // unk_0 legacy positions (unused)
        }

        int nf = ClampCount(r.ReadI32());
        for (int i = 0; i < nf; i++)
        {
            mesh.Faces.Add(ReadFace(r, ver));
        }

        if (ver >= 0x30009)
        {
            mesh.FramesPerSecond = r.ReadI32();
        }

        int numFrames;
        if (ver >= 0x40004)
        {
            r.ReadF32(); // start_time
            r.ReadF32(); // end_time
            numFrames = r.ReadI32();
        }
        else
        {
            int start = r.ReadI32();
            int end = r.ReadI32();
            numFrames = ver >= 0x3000C ? end - start + 1 : end - start;
        }

        numFrames = Math.Clamp(numFrames, 0, MaxCount);

        int nm = ClampCount(r.ReadI32());
        if (ver >= 0x40000)
        {
            var indices = new int[nm];
            for (int i = 0; i < nm; i++)
            {
                indices[i] = r.ReadI32();
            }

            mesh.MaterialIndices = indices;
        }
        else
        {
            for (int i = 0; i < nm; i++)
            {
                mesh.EmbeddedMaterials.Add(ReadEmbeddedMaterial(r, ver, numFrames));
            }
        }

        mesh.BoundingCenter = r.ReadVec3();
        mesh.BoundingRadius = r.ReadF32();
        if (ver < 0x30002)
        {
            r.ReadI32(); // flags_old
        }

        var flags = new VfxMeshFlags(r.ReadU32());
        mesh.Flags = flags;
        if (flags.Facing && ver == 0x3000A)
        {
            r.ReadF32(); // width
            r.ReadF32(); // height
        }

        int nfv = ClampCount(r.ReadI32());
        for (int i = 0; i < nfv; i++)
        {
            SkipFaceVertex(r);
        }

        if (ver >= 0x30009)
        {
            mesh.IsKeyframed = r.ReadU8() != 0;
        }

        for (int i = 0; i < numFrames; i++)
        {
            mesh.Frames.Add(ReadMeshFrame(r, ver, i, nv, nf, flags, mesh.IsKeyframed));
        }

        if (mesh.IsKeyframed && ver >= 0x3000A)
        {
            mesh.PivotTranslation = r.ReadVec3();
            mesh.PivotRotation = ReadQuat(r);
            mesh.PivotScale = r.ReadVec3();
        }

        if (mesh.IsKeyframed)
        {
            mesh.Keyframes = ReadKeyframeList(r);
        }

        mesh.TrailingBytes = r.Remaining;
        return mesh;
    }

    private static VfxFace ReadFace(RfReader r, int ver)
    {
        var face = new VfxFace
        {
            I0 = r.ReadI32(),
            I1 = r.ReadI32(),
            I2 = r.ReadI32(),
        };

        if (ver < 0x3000D)
        {
            face.Uv0 = r.ReadUv();
            face.Uv1 = r.ReadUv();
            face.Uv2 = r.ReadUv();
        }

        r.Position += 3 * 12; // colors (rgb_f4 * 3)
        face.Normal = r.ReadVec3();
        r.Position += 12; // face center
        r.ReadF32();      // radius
        face.MaterialIndex = r.ReadI32();
        face.SmoothingGroup = r.ReadI32();
        r.ReadI32(); // face_vertex_indices[0]
        r.ReadI32(); // face_vertex_indices[1]
        r.ReadI32(); // face_vertex_indices[2]
        return face;
    }

    private static void SkipFaceVertex(RfReader r)
    {
        r.Position += 4 + 4 + 4 + 4; // smoothing_group, vertex_index, u, v
        int n = ClampCount(r.ReadI32());
        r.Position += 4 * n; // adjacent_faces
    }

    private static VfxMeshFrame ReadMeshFrame(
        RfReader r, int ver, int index, int nv, int nf, VfxMeshFlags flags, bool isKeyframed)
    {
        var frame = new VfxMeshFrame();
        bool hasGeometry = flags.Morph || index == 0;
        if (hasGeometry)
        {
            frame.HasGeometry = true;
            Vec3 center = r.ReadVec3();
            Vec3 mult = r.ReadVec3();
            frame.Center = center;
            frame.Multiplier = mult;

            var positions = new Vec3[nv];
            for (int i = 0; i < nv; i++)
            {
                short sx = r.ReadI16();
                short sy = r.ReadI16();
                short sz = r.ReadI16();
                positions[i] = new Vec3(
                    center.X + (sx * mult.X),
                    center.Y + (sy * mult.Y),
                    center.Z + (sz * mult.Z));
            }

            frame.Positions = positions;

            if ((flags.Facing || flags.FacingRod) && ver >= 0x3000B)
            {
                r.ReadF32(); // width
                r.ReadF32(); // height
            }

            if (flags.FacingRod && index == 0 && ver >= 0x40001)
            {
                r.Position += 12; // up_vector
            }
        }

        if ((flags.DumpUvs || index == 0) && ver >= 0x3000D)
        {
            int uvCount = 3 * nf;
            var uvs = new Uv[uvCount];
            for (int i = 0; i < uvCount; i++)
            {
                uvs[i] = r.ReadUv();
            }

            frame.Uvs = uvs;
        }

        if (!flags.Morph && (!isKeyframed || (ver < 0x3000E && index == 0)))
        {
            frame.HasTransform = true;
            frame.Translation = r.ReadVec3();
            frame.Rotation = ReadQuat(r);
            frame.Scale = r.ReadVec3();
        }

        if (ver < 0x30009)
        {
            r.ReadU8(); // unk_0
        }

        if (ver < 0x40005)
        {
            frame.Opacity = r.ReadF32();
        }

        return frame;
    }

    private static VfxTransformKeyframes ReadKeyframeList(RfReader r)
    {
        var kf = new VfxTransformKeyframes();

        int nt = ClampCount(r.ReadI32());
        for (int i = 0; i < nt; i++)
        {
            kf.Translation.Add(new VfxVec3Key(r.ReadI32(), r.ReadVec3(), r.ReadVec3(), r.ReadVec3()));
        }

        int nr = ClampCount(r.ReadI32());
        for (int i = 0; i < nr; i++)
        {
            kf.Rotation.Add(new VfxQuatKey(
                r.ReadI32(), ReadQuat(r), r.ReadF32(), r.ReadF32(), r.ReadF32(), r.ReadF32(), r.ReadF32()));
        }

        int ns = ClampCount(r.ReadI32());
        for (int i = 0; i < ns; i++)
        {
            kf.Scale.Add(new VfxVec3Key(r.ReadI32(), r.ReadVec3(), r.ReadVec3(), r.ReadVec3()));
        }

        return kf;
    }

    // ---- Materials -----------------------------------------------------------

    private static VfxTexture ReadMaterialTexture(RfReader r, int ver)
    {
        var t = new VfxTexture { Name = r.ReadZString() };
        if (ver >= 0x30012)
        {
            t.StartFrame = r.ReadI32();
            t.PlaybackRate = r.ReadF32();
            t.AnimType = (VfxAnimType)r.ReadI32();
        }

        return t;
    }

    /// <summary>Reads a legacy in-mesh material (vfx.ksy mesh_material_old), versions &lt; 0x40000.</summary>
    private static VfxMaterial ReadEmbeddedMaterial(RfReader r, int ver, int numFrames)
    {
        var m = new VfxMaterial { Type = (VfxMaterialType)r.ReadI32() };
        bool imageOrVmix = m.Type is VfxMaterialType.Image or VfxMaterialType.Vmix;

        if (ver >= 0x30003 && imageOrVmix)
        {
            m.Additive = r.ReadU8() != 0;
        }

        if (imageOrVmix)
        {
            m.Tex0 = ReadMaterialTexture(r, ver);
        }

        if (m.Type == VfxMaterialType.Vmix)
        {
            m.Tex1 = ReadMaterialTexture(r, ver);
        }

        if (imageOrVmix && ver < 0x30012)
        {
            r.ReadI32(); // start_frame_old
            r.ReadI32(); // anim_type_old
        }

        if (imageOrVmix && ver >= 0x30007)
        {
            m.SpecularLevel = r.ReadF32();
            m.Glossiness = r.ReadF32();
            m.ReflectionAmount = r.ReadF32();
        }

        if (imageOrVmix)
        {
            m.ReflTextureName = r.ReadZString();
        }

        if (m.Type == VfxMaterialType.Vmix)
        {
            m.MixFrames = ReadFloats(r, numFrames);
        }

        if (m.Type == VfxMaterialType.ColorOnly)
        {
            m.SolidR = r.ReadI32();
            m.SolidG = r.ReadI32();
            m.SolidB = r.ReadI32();
        }

        if (ver >= 0x30011)
        {
            m.SelfIllumination = new[] { r.ReadF32() };
        }

        return m;
    }

    /// <summary>Reads a top-level material section (vfx.ksy material), versions &gt;= 0x40000.</summary>
    private static VfxMaterial ReadGlobalMaterial(RfReader r, int ver)
    {
        var m = new VfxMaterial { Type = (VfxMaterialType)r.ReadI32() };
        bool imageOrVmix = m.Type is VfxMaterialType.Image or VfxMaterialType.Vmix;

        if (ver >= 0x40003)
        {
            r.ReadI32(); // frames_per_second
        }

        if (imageOrVmix || ver >= 0x40006)
        {
            m.Additive = r.ReadU8() != 0;
        }

        if (imageOrVmix)
        {
            m.Tex0 = ReadMaterialTexture(r, ver);
        }

        if (m.Type == VfxMaterialType.Vmix)
        {
            m.Tex1 = ReadMaterialTexture(r, ver);
            int numMix = ClampCount(r.ReadI32());
            if (ver < 0x40003)
            {
                r.ReadI32(); // frames_per_second_legacy
            }

            m.MixFrames = ReadFloats(r, numMix);
        }

        if (imageOrVmix)
        {
            m.SpecularLevel = r.ReadF32();
            m.Glossiness = r.ReadF32();
            m.ReflectionAmount = r.ReadF32();
            m.ReflTextureName = r.ReadZString();
        }

        if (m.Type == VfxMaterialType.ColorOnly)
        {
            m.SolidR = r.ReadI32();
            m.SolidG = r.ReadI32();
            m.SolidB = r.ReadI32();
        }

        if (ver >= 0x40003)
        {
            int n = ClampCount(r.ReadI32());
            m.SelfIllumination = ReadFloats(r, n);
        }
        else
        {
            m.SelfIllumination = new[] { r.ReadF32() };
        }

        if (ver >= 0x40005)
        {
            int n = ClampCount(r.ReadI32());
            m.Opacity = ReadFloats(r, n);
        }

        return m;
    }

    // ---- Retained sections ---------------------------------------------------

    private static VfxParticleSystem ReadParticleSystem(RfReader r, int ver)
    {
        var ps = new VfxParticleSystem
        {
            Name = r.ReadZString(),
            ParentName = r.ReadZString(),
        };

        r.ReadU8(); // save_parent

        bool drops = false;
        if (ver >= 0x30010)
        {
            uint flags = r.ReadU32();
            drops = (flags & 0x100) != 0;
        }

        int numWarps = ClampCount(r.ReadI32());
        for (int i = 0; i < numWarps; i++)
        {
            r.ReadZString(); // warp name
        }

        r.ReadI32(); // start_time
        int numFrames = Math.Clamp(r.ReadI32(), 0, MaxCount);

        if (ver >= 0x40000)
        {
            ps.MaterialIndex = r.ReadI32();
        }
        else
        {
            ps.EmbeddedMaterial = ReadParticleMaterial(r, ver, numFrames, drops);
        }

        ps.ParticleCount = r.ReadI32();
        r.ReadI32(); // start
        r.ReadI32(); // lifetime
        r.ReadF32(); // lifetime_variation
        r.ReadI32(); // emitter_type
        if (ver < 0x30010)
        {
            r.ReadI32(); // flags_old
        }

        if (ver >= 0x30005)
        {
            r.ReadF32(); // shrink_at_birth
            r.ReadF32(); // shrink_at_death
        }
        else
        {
            r.ReadI32(); // shrink_at_birth_old
            r.ReadI32(); // shrink_at_death_old
        }

        if (ver >= 0x30006)
        {
            r.ReadF32(); // fade_at_birth
            r.ReadF32(); // fade_at_death
        }

        if (drops)
        {
            r.ReadF32(); // tail_distance
        }

        if (ver < 0x3000D)
        {
            r.Position += 56; // unk_1
        }

        // particle_frame: pos(12) + orient(16) + 6 floats (+ opacity for ver < 0x40005)
        int frameSize = 12 + 16 + (6 * 4) + (ver < 0x40005 ? 4 : 0);
        r.Position += numFrames * frameSize;

        ps.TrailingBytes = r.Remaining;
        return ps;
    }

    private static VfxMaterial ReadParticleMaterial(RfReader r, int ver, int numFrames, bool drops)
    {
        var m = new VfxMaterial();
        bool imageOrVmix = false;
        if (!drops)
        {
            m.Type = (VfxMaterialType)r.ReadI32();
            imageOrVmix = m.Type is VfxMaterialType.Image or VfxMaterialType.Vmix;
        }

        if (ver >= 0x30003 && imageOrVmix)
        {
            m.Additive = r.ReadU8() != 0;
        }

        if (imageOrVmix)
        {
            m.Tex0 = new VfxTexture { Name = r.ReadZString() };
            if (ver >= 0x30012)
            {
                r.ReadI32(); // tex_0_playback_rate
            }
        }

        if (m.Type == VfxMaterialType.Vmix)
        {
            m.Tex1 = new VfxTexture { Name = r.ReadZString() };
            if (ver >= 0x30012)
            {
                r.ReadI32(); // tex_1_playback_rate
            }
        }

        if (m.Type == VfxMaterialType.Vmix)
        {
            m.MixFrames = ReadFloats(r, numFrames);
        }

        if (drops)
        {
            m.Type = VfxMaterialType.ColorOnly;
            m.SolidR = r.ReadI32();
            m.SolidG = r.ReadI32();
            m.SolidB = r.ReadI32();
        }

        if (ver >= 0x30011)
        {
            m.SelfIllumination = new[] { r.ReadF32() };
        }

        return m;
    }

    private static VfxNamedObject ReadSpacewarp(RfReader r)
    {
        var o = new VfxNamedObject
        {
            Name = r.ReadZString(),
            ParentName = r.ReadZString(),
        };

        r.ReadI32(); // type
        int numFrames = ClampCount(r.ReadI32());
        r.Position += numFrames * 48; // spacewarp_frame
        o.TrailingBytes = r.Remaining;
        return o;
    }

    private static VfxNamedObject ReadDummy(RfReader r)
    {
        var o = new VfxNamedObject
        {
            Name = r.ReadZString(),
            ParentName = r.ReadZString(),
        };

        r.ReadU8();       // save_parent
        r.Position += 12; // pos
        r.Position += 16; // orient
        int numFrames = ClampCount(r.ReadI32());
        r.Position += numFrames * 28; // vec3_quat
        o.TrailingBytes = r.Remaining;
        return o;
    }

    private static VfxNamedObject ReadLight(RfReader r)
    {
        var o = new VfxNamedObject
        {
            Name = r.ReadZString(),
            ParentName = r.ReadZString(),
        };

        r.ReadU8();       // save_parent
        r.Position += 33; // params (light_params)
        int numFrames = ClampCount(r.ReadI32());
        r.Position += numFrames * 33; // light_params frames
        o.TrailingBytes = r.Remaining;
        return o;
    }

    private static VfxNamedObject ReadNamedHeaderOnly(RfReader r) => new()
    {
        Name = r.ReadZString(),
        ParentName = r.ReadZString(),
        TrailingBytes = r.Remaining,
    };

    // ---- Helpers -------------------------------------------------------------

    private static VfxQuat ReadQuat(RfReader r) => new(r.ReadF32(), r.ReadF32(), r.ReadF32(), r.ReadF32());

    private static float[] ReadFloats(RfReader r, int count)
    {
        count = Math.Clamp(count, 0, MaxCount);
        var arr = new float[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = r.ReadF32();
        }

        return arr;
    }

    private static int ClampCount(int count) => count < 0 ? 0 : Math.Min(count, MaxCount);
}
