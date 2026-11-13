using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_level_properties (0x0AFBA5ED): Alpine-specific level flags, the geoable / breakable / hold-open tables
/// and — from chunk version 5 — the lighting (sun, lightmap settings, no-shadow-cast brushes) and version 6
/// level settings (vehicle flight ceiling, minimap, require-D3D11, camera far clip, alpha-tested occlusion,
/// payload rules). The internal chunk version gates the fields, independent of the file version. Field order and
/// defaults: Alpine editor_patch/level.h:959-1016 (fields), :1133-1180 (defaults for a field a chunk does not
/// carry), :1244-1331 (write), :1333-1612 (read).
///
/// <para>
/// The chunk is read the way Alpine reads it: fields are consumed in order while bytes remain, a field the chunk
/// ends before keeps Alpine's default, and bytes after the last field Alpine's version knows about are skipped.
/// Alpine grew versions 5 and 6 in place during development (a v5 chunk with only the sun toggle and yaw, v6
/// chunks without the far clip or the payload rules, a transient version 7 whose bytes are a v6 prefix), so the
/// parser models the field PREFIX actually present, not just the version: <see cref="StoredFieldCount"/>.
/// </para>
///
/// <para>
/// Round-trip: every byte not modelled (a newer version's fields, a partial table or payload block, a stray
/// fragment) is kept as <see cref="UnparsedTail"/>. <see cref="Write"/> emits the same field prefix — extended
/// only as far as needed to store a value that differs from Alpine's default for a missing field — followed by
/// the tail, so an unmodified chunk re-serializes byte for byte. A changed <see cref="Version"/> (an upgrade, or
/// the version 7 → 6 rewrite) or a section created in memory writes the full layout of its version. Values are
/// kept exactly as stored (Alpine sanitizes the sun fields, far clip and payload rules on read).
/// </para>
/// </summary>
public sealed class AlpineLevelPropertiesSection : IRflSectionContent
{
    /// <summary>The chunk version Alpine writes (level.h:1059) and Glacier writes for an edited chunk.</summary>
    public const uint CurrentVersion = 6;

    // Field groups in chunk order (each group is one field, or one counted table / fixed block that Alpine
    // either reads whole or discards). Group index → member: see the switch tables below.
    private const int GroupsV1 = 1;
    private const int GroupsV2 = 3;
    private const int GroupsV3 = 5;
    private const int GroupsV4 = 9;
    private const int GroupsV5 = 32;
    private const int GroupsV6 = 42;

    // Alpine's defaults for v1–v4 fields a chunk does not carry (LoadDefaults, level.h:1133-1140).
    private const byte DefaultLegacyCyclicTimers = 1;
    private const byte DefaultLegacyMovers = 1;
    private const byte DefaultStartsWithHeadlamp = 1;
    private const byte DefaultOverrideModifier = 0;
    private const float DefaultStaticMeshAmbientLightModifier = 2.0f;
    private const byte DefaultRf2StyleGeomod = 0;

    /// <summary>Chunk version the bytes were parsed with; null for a section created in memory.</summary>
    private uint? _parsedVersion;

    /// <summary>Number of field groups present in the parsed chunk; -1 when even the version was missing.</summary>
    private int _parsedGroups;

    public SectionType Type => SectionType.AlpineLevelProperties;

    /// <summary>Chunk version (1–6 modelled; 7 — a pre-release layout — reads as a version 6 prefix).</summary>
    public uint Version { get; set; }

    // ---- v1–v4 ----------------------------------------------------------------------------------
    // A section created in memory starts at Alpine's existing-level defaults (LoadDefaults, level.h:1133-1140):
    // the stock behaviour a level without the chunk already has. A NEW level uses CreateForNewLevel instead.

    public byte LegacyCyclicTimers { get; set; } = DefaultLegacyCyclicTimers;

    public byte LegacyMovers { get; set; } = DefaultLegacyMovers;

    public byte StartsWithHeadlamp { get; set; } = DefaultStartsWithHeadlamp;

    public byte OverrideStaticMeshAmbientLightModifier { get; set; } = DefaultOverrideModifier;

    public float StaticMeshAmbientLightModifier { get; set; } = DefaultStaticMeshAmbientLightModifier;

    public byte Rf2StyleGeomod { get; set; } = DefaultRf2StyleGeomod;

    public List<AlpineGeoableEntry> GeoableEntries { get; set; } = new();

    public List<AlpineBreakableEntry> BreakableEntries { get; set; } = new();

    public List<int> HoldOpenKeyframeUids { get; set; } = new();

    // ---- v5: lighting ---------------------------------------------------------------------------

    public byte EnableSun { get; set; }

    /// <summary>Degrees.</summary>
    public float SunYaw { get; set; }

    /// <summary>Degrees above the horizon; 90 = zenith.</summary>
    public float SunPitch { get; set; } = 90f;

    public byte SunColorR { get; set; } = 255;

    public byte SunColorG { get; set; } = 255;

    public byte SunColorB { get; set; } = 255;

    public byte SunColorA { get; set; } = 255;

    public float SunIntensity { get; set; } = 1f;

    /// <summary>Penumbra half-angle in degrees for baked soft shadows.</summary>
    public float SunSpreadAngle { get; set; }

    public byte SunCastBakedShadows { get; set; } = 1;

    public byte SunAffectsMeshes { get; set; } = 1;

    /// <summary>0 = scale by the sampled lightmap luminance, 1 = everywhere.</summary>
    public byte SunMeshMode { get; set; }

    public byte SunDrivesShadowmapDir { get; set; } = 1;

    /// <summary>1 = bake with the stock pipeline; 0 (Alpine's default) = Alpine's fixed lighting pipeline.</summary>
    public byte LegacyLighting { get; set; }

    public byte HighresLightmaps { get; set; }

    public byte SunLiquidOccludes { get; set; } = 1;

    public byte InvisibleFacesOcclude { get; set; }

    public byte AlphaFacesOcclude { get; set; }

    /// <summary>Brushes whose faces never occlude a baked ray.</summary>
    public List<int> NoShadowCastBrushUids { get; set; } = new();

    public byte MeshesOcclude { get; set; }

    /// <summary>Texels per world unit for the Alpine bake: 0 = default (8), 1–128, 255 = off.</summary>
    public byte LightmapDensity { get; set; }

    /// <summary>Bit field: bit0 = the file carries no stock 0x1200 lightmaps, bit1 = the mapper's setting.</summary>
    public byte D3D11OnlyLightmaps { get; set; }

    /// <summary><see cref="D3D11OnlyLightmaps"/> bit0: the stock lightmaps section is omitted (alpine_lightmap.h:63).</summary>
    public const byte D3D11OnlyStockOmitted = 1 << 0;

    /// <summary><see cref="D3D11OnlyLightmaps"/> bit1: the mapper's "D3D11-only lightmaps" setting (alpine_lightmap.h:64).</summary>
    public const byte D3D11OnlySetting = 1 << 1;

    /// <summary>0 quality, 1 balanced, 2 compact.</summary>
    public byte LightmapCompression { get; set; }

    // ---- v6: level settings ---------------------------------------------------------------------

    public byte VehicleFlightCeilingEnabled { get; set; }

    /// <summary>World Y of the flight ceiling.</summary>
    public float VehicleFlightCeiling { get; set; }

    public byte MinimapEnabled { get; set; }

    public string MinimapBitmap { get; set; } = string.Empty;

    public Vec3 MinimapWorldMin { get; set; }

    public Vec3 MinimapWorldMax { get; set; }

    public float MinimapCutHeight { get; set; }

    /// <summary>Bit field: bit0 = needed (recomputed on every save), bit1 = the mapper's setting.</summary>
    public byte RequireD3D11 { get; set; }

    /// <summary><see cref="RequireD3D11"/> bit0: the level needs Direct3D 11 for any reason (level.h:1062).</summary>
    public const byte RequireD3D11Needed = 1 << 0;

    /// <summary><see cref="RequireD3D11"/> bit1: the mapper's "Require Direct3D 11" setting (level.h:1063).</summary>
    public const byte RequireD3D11Setting = 1 << 1;

    /// <summary>0 = the stock far clip.</summary>
    public float CameraFarClip { get; set; }

    public byte AlphaTestedOcclusion { get; set; }

    /// <summary>The 12 payload-rule floats in file order (common/include/common/payload.h:66-97).</summary>
    public float[] PayloadRules { get; set; } = DefaultPayloadRules.ToArray();

    /// <summary>Alpine's payload-rule defaults: push 1.5, rollback 0.5, accel 2, bonus 180, heal 10, grace 5, respawns.</summary>
    public static IReadOnlyList<float> DefaultPayloadRules { get; } = new[]
    {
        1.5f, 0.5f, 2.0f, 180f, 10f, 5f, 0f, 0f, 0f, 1.0f, 0.5f, 1.0f,
    };

    // The payload rules by name (payload::LevelRules::file_fields order, common/include/common/payload.h:66-97;
    // Alpine RED's Level Properties "Payload" group, editor_patch/level.cpp:2360-2385).

    /// <summary>Cart speed in m/s with one attacker pushing.</summary>
    public float PayloadPushSpeed { get => PayloadRule(0); set => SetPayloadRule(0, value); }

    /// <summary>Rollback speed as a fraction of the push speed.</summary>
    public float PayloadRollbackSpeed { get => PayloadRule(1); set => SetPayloadRule(1, value); }

    /// <summary>Cart acceleration in m/s².</summary>
    public float PayloadCartAccel { get => PayloadRule(2); set => SetPayloadRule(2, value); }

    /// <summary>Seconds added to the round time when the cart reaches a checkpoint (a checkpoint's own bonus wins).</summary>
    public float PayloadCheckpointTimeBonus { get => PayloadRule(3); set => SetPayloadRule(3, value); }

    /// <summary>Health and ammo per second the cart gives attackers near it.</summary>
    public float PayloadHealPerSecond { get => PayloadRule(4); set => SetPayloadRule(4, value); }

    /// <summary>Overtime grace in seconds.</summary>
    public float PayloadOvertimeGrace { get => PayloadRule(5); set => SetPayloadRule(5, value); }

    /// <summary>Attackers' extra respawn delay (s): base.</summary>
    public float PayloadAttackerRespawnBase { get => PayloadRule(6); set => SetPayloadRule(6, value); }

    /// <summary>Attackers' extra respawn delay (s) per checkpoint passed.</summary>
    public float PayloadAttackerRespawnPerCheckpoint { get => PayloadRule(7); set => SetPayloadRule(7, value); }

    /// <summary>Attackers' extra respawn delay (s) on the final stretch.</summary>
    public float PayloadAttackerRespawnFinalStretch { get => PayloadRule(8); set => SetPayloadRule(8, value); }

    /// <summary>Defenders' extra respawn delay (s): base.</summary>
    public float PayloadDefenderRespawnBase { get => PayloadRule(9); set => SetPayloadRule(9, value); }

    /// <summary>Defenders' extra respawn delay (s) per checkpoint passed.</summary>
    public float PayloadDefenderRespawnPerCheckpoint { get => PayloadRule(10); set => SetPayloadRule(10, value); }

    /// <summary>Defenders' extra respawn delay (s) on the final stretch.</summary>
    public float PayloadDefenderRespawnFinalStretch { get => PayloadRule(11); set => SetPayloadRule(11, value); }

    private float PayloadRule(int i) => i < PayloadRules.Length ? PayloadRules[i] : DefaultPayloadRules[i];

    private void SetPayloadRule(int i, float value)
    {
        if (PayloadRules.Length != 12)
        {
            float[] rules = DefaultPayloadRules.ToArray();
            Array.Copy(PayloadRules, rules, Math.Min(PayloadRules.Length, rules.Length));
            PayloadRules = rules;
        }

        PayloadRules[i] = value;
    }

    /// <summary>
    /// The chunk Alpine RED gives a NEW level (File ▸ New): version 6, every field at Alpine's defaults except
    /// Legacy Cyclic Timers and Legacy Movers, which are off (AlpineLevelProperties::LoadNewLevelDefaults,
    /// editor_patch/level.h:1123-1128; applied on CDedLevel construction and File ▸ New, level.cpp:155-191).
    /// Written in the full version 6 layout, as Alpine RED's save writes it (level.h:1244-1331).
    /// </summary>
    public static AlpineLevelPropertiesSection CreateForNewLevel() => new()
    {
        Version = CurrentVersion,
        LegacyCyclicTimers = 0,
        LegacyMovers = 0,
    };

    /// <summary>
    /// Chunk bytes after the last modelled field (fields of a newer chunk version, a partial table or block, or
    /// bytes of an older layout), preserved verbatim. Empty for a fully modelled chunk.
    /// </summary>
    public byte[] UnparsedTail { get; private set; } = Array.Empty<byte>();

    /// <summary>The number of field groups the parsed chunk stored; null for a section created in memory.</summary>
    public int? StoredFieldCount => _parsedVersion is null ? null : Math.Max(_parsedGroups, 0);

    /// <summary>
    /// True when the parsed chunk ended before the last field its version defines — a shorter layout Alpine
    /// accepts, with the missing fields at Alpine's defaults.
    /// </summary>
    public bool IsShortLayout => _parsedVersion is not null && _parsedGroups < GroupsFor(_parsedVersion.Value);

    public static IRflSectionContent Parse(RfReader reader, RflContext ctx)
    {
        byte[] body = reader.ReadBytes(reader.Remaining);
        var r = new AlpineChunkReader(body);
        var section = new AlpineLevelPropertiesSection
        {
            // Alpine's defaults stand for every field the chunk does not reach.
            LegacyCyclicTimers = DefaultLegacyCyclicTimers,
            LegacyMovers = DefaultLegacyMovers,
            StartsWithHeadlamp = DefaultStartsWithHeadlamp,
            OverrideStaticMeshAmbientLightModifier = DefaultOverrideModifier,
            StaticMeshAmbientLightModifier = DefaultStaticMeshAmbientLightModifier,
            Rf2StyleGeomod = DefaultRf2StyleGeomod,
        };

        if (!r.TryU32(out uint version))
        {
            // Not even a version: Alpine ignores such a chunk.
            section._parsedVersion = 0;
            section._parsedGroups = -1;
            section.UnparsedTail = body;
            return section;
        }

        section.Version = version;
        section._parsedVersion = version;
        int groups = GroupsFor(version);
        int read = 0;
        while (read < groups && section.TryReadGroup(r, read))
        {
            read++;
        }

        section._parsedGroups = read;
        r.TryBytes(r.Remaining, out byte[] tail);
        section.UnparsedTail = tail;
        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        int full = GroupsFor(Version);
        bool sameLayout = _parsedVersion is uint pv && pv == Version;

        if (sameLayout && _parsedGroups < 0)
        {
            // The chunk had no version; nothing modelled can have changed it (Version is still 0).
            w.WriteBytes(UnparsedTail);
            return;
        }

        int emit = full;
        if (sameLayout)
        {
            emit = Math.Min(_parsedGroups, full);
            for (int g = full - 1; g >= emit; g--)
            {
                if (!GroupIsDefault(g))
                {
                    emit = g + 1;
                    break;
                }
            }
        }

        w.WriteU32(Version);
        for (int g = 0; g < emit; g++)
        {
            WriteGroup(w, g);
        }

        // The tail belongs right after the fields it followed on disk; once the layout
        // changes (version changed, or a missing field now written) it no longer lines up.
        if (sameLayout && emit == _parsedGroups)
        {
            w.WriteBytes(UnparsedTail);
        }
    }

    /// <summary>
    /// Re-targets the chunk at <see cref="CurrentVersion"/> with the full layout: every field Alpine's version 6
    /// knows is written (missing ones at Alpine's defaults) and the unmodelled tail is dropped. This is what Alpine
    /// RED writes for any props chunk it saves (level.h:1244-1331), used by the v306 upgrade and the version 7
    /// rewrite (ALPINE-COMPAT-SPEC §2, Q3).
    /// </summary>
    public void RewriteAsCurrentLayout()
    {
        Version = CurrentVersion;
        _parsedVersion = null;
        _parsedGroups = 0;
        UnparsedTail = Array.Empty<byte>();
    }

    private static int GroupsFor(uint version) => version switch
    {
        0 => 0,
        1 => GroupsV1,
        2 => GroupsV2,
        3 => GroupsV3,
        4 => GroupsV4,
        5 => GroupsV5,
        _ => GroupsV6,
    };

    private bool TryReadGroup(AlpineChunkReader r, int group)
    {
        switch (group)
        {
            case 0: return U8(r, v => LegacyCyclicTimers = v);
            case 1: return U8(r, v => LegacyMovers = v);
            case 2: return U8(r, v => StartsWithHeadlamp = v);
            case 3: return U8(r, v => OverrideStaticMeshAmbientLightModifier = v);
            case 4: return F32(r, v => StaticMeshAmbientLightModifier = v);
            case 5: return U8(r, v => Rf2StyleGeomod = v);
            case 6:
                return TryReadTable(r, 8, rr =>
                {
                    rr.TryI32(out int brush);
                    rr.TryI32(out int room);
                    GeoableEntries.Add(new AlpineGeoableEntry { BrushUid = brush, RoomUid = room });
                });
            case 7:
                return TryReadTable(r, 9, rr =>
                {
                    rr.TryI32(out int brush);
                    rr.TryI32(out int room);
                    rr.TryU8(out byte material);
                    BreakableEntries.Add(new AlpineBreakableEntry { BrushUid = brush, RoomUid = room, Material = material });
                });
            case 8:
                return TryReadTable(r, 4, rr =>
                {
                    rr.TryI32(out int uid);
                    HoldOpenKeyframeUids.Add(uid);
                });

            // v5 (level.h:1488-1562)
            case 9: return U8(r, v => EnableSun = v);
            case 10: return F32(r, v => SunYaw = v);
            case 11: return F32(r, v => SunPitch = v);
            case 12: return U8(r, v => SunColorR = v);
            case 13: return U8(r, v => SunColorG = v);
            case 14: return U8(r, v => SunColorB = v);
            case 15: return U8(r, v => SunColorA = v);
            case 16: return F32(r, v => SunIntensity = v);
            case 17: return F32(r, v => SunSpreadAngle = v);
            case 18: return U8(r, v => SunCastBakedShadows = v);
            case 19: return U8(r, v => SunAffectsMeshes = v);
            case 20: return U8(r, v => SunMeshMode = v);
            case 21: return U8(r, v => SunDrivesShadowmapDir = v);
            case 22: return U8(r, v => LegacyLighting = v);
            case 23: return U8(r, v => HighresLightmaps = v);
            case 24: return U8(r, v => SunLiquidOccludes = v);
            case 25: return U8(r, v => InvisibleFacesOcclude = v);
            case 26: return U8(r, v => AlphaFacesOcclude = v);
            case 27:
                return TryReadTable(r, 4, rr =>
                {
                    rr.TryI32(out int uid);
                    NoShadowCastBrushUids.Add(uid);
                });
            case 28: return U8(r, v => MeshesOcclude = v);
            case 29: return U8(r, v => LightmapDensity = v);
            case 30: return U8(r, v => D3D11OnlyLightmaps = v);
            case 31: return U8(r, v => LightmapCompression = v);

            // v6 (level.h:1564-1609)
            case 32: return U8(r, v => VehicleFlightCeilingEnabled = v);
            case 33: return F32(r, v => VehicleFlightCeiling = v);
            case 34: return U8(r, v => MinimapEnabled = v);
            case 35:
                // read_rfl_string: a name that does not fit leaves nothing behind it Alpine could read.
                if (!r.TryString(out string bitmap))
                {
                    return false;
                }

                MinimapBitmap = bitmap;
                return true;
            case 36:
                // Six floats Alpine assigns only once all six are read (level.h:1581-1590).
                if (r.Remaining < 24)
                {
                    return false;
                }

                r.TryVec3(out Vec3 min);
                r.TryVec3(out Vec3 max);
                MinimapWorldMin = min;
                MinimapWorldMax = max;
                return true;
            case 37: return F32(r, v => MinimapCutHeight = v);
            case 38: return U8(r, v => RequireD3D11 = v);
            case 39: return F32(r, v => CameraFarClip = v);
            case 40: return U8(r, v => AlphaTestedOcclusion = v);
            case 41:
                // Twelve floats Alpine assigns only once all twelve are read (level.h:1602-1608).
                if (r.Remaining < 48)
                {
                    return false;
                }

                var rules = new float[12];
                for (int i = 0; i < rules.Length; i++)
                {
                    r.TryF32(out rules[i]);
                }

                PayloadRules = rules;
                return true;
            default:
                return false;
        }
    }

    private static bool U8(AlpineChunkReader r, Action<byte> set)
    {
        if (!r.TryU8(out byte v))
        {
            return false;
        }

        set(v);
        return true;
    }

    private static bool F32(AlpineChunkReader r, Action<float> set)
    {
        if (!r.TryF32(out float v))
        {
            return false;
        }

        set(v);
        return true;
    }

    /// <summary>
    /// Reads a counted table only when the whole table is present; otherwise leaves the
    /// reader where it was so the fragment stays in <see cref="UnparsedTail"/>.
    /// </summary>
    private static bool TryReadTable(AlpineChunkReader r, int entrySize, Action<AlpineChunkReader> readEntry)
    {
        int start = r.Position;
        if (!r.TryU32(out uint count))
        {
            return false;
        }

        if ((ulong)count * (ulong)entrySize > (ulong)r.Remaining)
        {
            r.Position = start;
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            readEntry(r);
        }

        return true;
    }

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    /// <summary>True when group <paramref name="group"/> holds the value Alpine assumes for a missing field.</summary>
    private bool GroupIsDefault(int group) => group switch
    {
        0 => LegacyCyclicTimers == DefaultLegacyCyclicTimers,
        1 => LegacyMovers == DefaultLegacyMovers,
        2 => StartsWithHeadlamp == DefaultStartsWithHeadlamp,
        3 => OverrideStaticMeshAmbientLightModifier == DefaultOverrideModifier,
        4 => Same(StaticMeshAmbientLightModifier, DefaultStaticMeshAmbientLightModifier),
        5 => Rf2StyleGeomod == DefaultRf2StyleGeomod,
        6 => GeoableEntries.Count == 0,
        7 => BreakableEntries.Count == 0,
        8 => HoldOpenKeyframeUids.Count == 0,
        9 => EnableSun == 0,
        10 => Same(SunYaw, 0f),
        11 => Same(SunPitch, 90f),
        12 => SunColorR == 255,
        13 => SunColorG == 255,
        14 => SunColorB == 255,
        15 => SunColorA == 255,
        16 => Same(SunIntensity, 1f),
        17 => Same(SunSpreadAngle, 0f),
        18 => SunCastBakedShadows == 1,
        19 => SunAffectsMeshes == 1,
        20 => SunMeshMode == 0,
        21 => SunDrivesShadowmapDir == 1,
        22 => LegacyLighting == 0,
        23 => HighresLightmaps == 0,
        24 => SunLiquidOccludes == 1,
        25 => InvisibleFacesOcclude == 0,
        26 => AlphaFacesOcclude == 0,
        27 => NoShadowCastBrushUids.Count == 0,
        28 => MeshesOcclude == 0,
        29 => LightmapDensity == 0,
        30 => D3D11OnlyLightmaps == 0,
        31 => LightmapCompression == 0,
        32 => VehicleFlightCeilingEnabled == 0,
        33 => Same(VehicleFlightCeiling, 0f),
        34 => MinimapEnabled == 0,
        35 => MinimapBitmap.Length == 0,
        36 => MinimapWorldMin == default && MinimapWorldMax == default,
        37 => Same(MinimapCutHeight, 0f),
        38 => RequireD3D11 == 0,
        39 => Same(CameraFarClip, 0f),
        40 => AlphaTestedOcclusion == 0,
        41 => PayloadRules.Length == 12 && PayloadRules.Select((v, i) => Same(v, DefaultPayloadRules[i])).All(x => x),
        _ => true,
    };

    private void WriteGroup(RfWriter w, int group)
    {
        switch (group)
        {
            case 0: w.WriteU8(LegacyCyclicTimers); break;
            case 1: w.WriteU8(LegacyMovers); break;
            case 2: w.WriteU8(StartsWithHeadlamp); break;
            case 3: w.WriteU8(OverrideStaticMeshAmbientLightModifier); break;
            case 4: w.WriteF32(StaticMeshAmbientLightModifier); break;
            case 5: w.WriteU8(Rf2StyleGeomod); break;
            case 6:
                w.WriteU32((uint)GeoableEntries.Count);
                foreach (AlpineGeoableEntry e in GeoableEntries)
                {
                    w.WriteI32(e.BrushUid);
                    w.WriteI32(e.RoomUid);
                }

                break;
            case 7:
                w.WriteU32((uint)BreakableEntries.Count);
                foreach (AlpineBreakableEntry e in BreakableEntries)
                {
                    w.WriteI32(e.BrushUid);
                    w.WriteI32(e.RoomUid);
                    w.WriteU8(e.Material);
                }

                break;
            case 8:
                w.WriteU32((uint)HoldOpenKeyframeUids.Count);
                foreach (int uid in HoldOpenKeyframeUids)
                {
                    w.WriteI32(uid);
                }

                break;
            case 9: w.WriteU8(EnableSun); break;
            case 10: w.WriteF32(SunYaw); break;
            case 11: w.WriteF32(SunPitch); break;
            case 12: w.WriteU8(SunColorR); break;
            case 13: w.WriteU8(SunColorG); break;
            case 14: w.WriteU8(SunColorB); break;
            case 15: w.WriteU8(SunColorA); break;
            case 16: w.WriteF32(SunIntensity); break;
            case 17: w.WriteF32(SunSpreadAngle); break;
            case 18: w.WriteU8(SunCastBakedShadows); break;
            case 19: w.WriteU8(SunAffectsMeshes); break;
            case 20: w.WriteU8(SunMeshMode); break;
            case 21: w.WriteU8(SunDrivesShadowmapDir); break;
            case 22: w.WriteU8(LegacyLighting); break;
            case 23: w.WriteU8(HighresLightmaps); break;
            case 24: w.WriteU8(SunLiquidOccludes); break;
            case 25: w.WriteU8(InvisibleFacesOcclude); break;
            case 26: w.WriteU8(AlphaFacesOcclude); break;
            case 27:
                w.WriteU32((uint)NoShadowCastBrushUids.Count);
                foreach (int uid in NoShadowCastBrushUids)
                {
                    w.WriteI32(uid);
                }

                break;
            case 28: w.WriteU8(MeshesOcclude); break;
            case 29: w.WriteU8(LightmapDensity); break;
            case 30: w.WriteU8(D3D11OnlyLightmaps); break;
            case 31: w.WriteU8(LightmapCompression); break;
            case 32: w.WriteU8(VehicleFlightCeilingEnabled); break;
            case 33: w.WriteF32(VehicleFlightCeiling); break;
            case 34: w.WriteU8(MinimapEnabled); break;
            case 35: w.WriteVString(MinimapBitmap); break;
            case 36:
                w.WriteVec3(MinimapWorldMin);
                w.WriteVec3(MinimapWorldMax);
                break;
            case 37: w.WriteF32(MinimapCutHeight); break;
            case 38: w.WriteU8(RequireD3D11); break;
            case 39: w.WriteF32(CameraFarClip); break;
            case 40: w.WriteU8(AlphaTestedOcclusion); break;
            case 41:
                for (int i = 0; i < 12; i++)
                {
                    w.WriteF32(i < PayloadRules.Length ? PayloadRules[i] : DefaultPayloadRules[i]);
                }

                break;
        }
    }
}
