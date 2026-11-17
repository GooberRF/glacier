using System.IO;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The Alpine Faction 1.5 (RFL v306) chunks on synthetic, Glacier-generated data (ALPINE-COMPAT-SPEC §8): every typed
/// chunk with its optional branches round-trips exactly; truncation at every byte keeps exactly the records Alpine's
/// reader keeps and leaves the chunk raw and read-only; count caps clamp or reject as Alpine does; the mesh trailing
/// blocks, the props v5/v6 prefixes, the lightmap and terrain structure, and foreign / unknown chunks behave as specified.
/// </summary>
public sealed class Alpine306ChunkTests
{
    private static readonly RflContext V306 = new(RflFile.AlpineSaveVersion);
    private static readonly RflContext V305 = new(305);

    public static IEnumerable<object[]> RecordChunkTypes() =>
        Alpine306Synthetic.RecordChunks.Select(t => new object[] { t });

    private static IRflSectionContent Parse(SectionType type, byte[] body, RflContext? ctx = null)
    {
        var section = new RflSection((uint)type, body);
        Assert.True(RflSectionRegistry.TryParse(section, ctx ?? V306, out IRflSectionContent? content));
        return content!;
    }

    private static byte[] Write(IRflSectionContent content, RflContext? ctx = null)
    {
        var w = new RfWriter();
        content.Write(w, ctx ?? V306);
        return w.ToArray();
    }

    private static (bool ReadOnly, int Records) State(IRflSectionContent c) => c switch
    {
        AlpineWeatherRegionsSection s => (s.IsReadOnly, s.Records.Count),
        AlpineVehicleFactoriesSection s => (s.IsReadOnly, s.Records.Count),
        AlpineProjectionCamerasSection s => (s.IsReadOnly, s.Records.Count),
        AlpineRopeEmittersSection s => (s.IsReadOnly, s.Records.Count),
        AlpineDirectionalLightsSection s => (s.IsReadOnly, s.Records.Count),
        AlpineWaypointHelpersSection s => (s.IsReadOnly, s.Records.Count),
        AlpineLiquidEmissionSection s => (s.IsReadOnly, s.Records.Count),
        AlpineMoverPathsSection s => (s.IsReadOnly, s.Movers.Count + s.Keyframes.Count),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    // ---- typed chunks ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RecordChunkTypes))]
    public void Every_Chunk_With_Its_Optional_Branches_Round_Trips_Exactly(SectionType type)
    {
        IRflSectionContent model = Alpine306Synthetic.Chunk(type);
        byte[] bytes = Alpine306Synthetic.Bytes(model);
        IRflSectionContent parsed = Parse(type, bytes);
        (bool readOnly, int records) = State(parsed);
        Assert.False(readOnly);
        Assert.Equal(State(model).Records, records);
        Assert.Equal(bytes, Write(parsed));
    }

    [Fact]
    public void Rope_Decoration_Slots_Read_The_Effect_Blocks_Their_Masked_Flags_Declare()
    {
        var rope = (AlpineRopeEmittersSection)Parse(SectionType.AlpineRopeEmitters, Alpine306Synthetic.Bytes(Alpine306Synthetic.RopeEmitters()));
        AlpineRopeEmitter lamp = rope.Records[1];
        Assert.True(lamp.IsZipline);
        AlpineRopeDecorations deco = Assert.IsType<AlpineRopeDecorations>(lamp.Decorations);
        Assert.Equal(4, deco.Meshes.Count);
        Assert.Equal("vol.tga", deco.Meshes[0].VolumetricBitmap);
        Assert.Equal(3f, deco.Meshes[0].VolumetricLength);
        Assert.Equal(180, deco.Meshes[0].LightB);
        Assert.True(deco.Meshes[1].HasGlare && !deco.Meshes[1].HasLight);
        Assert.True(deco.Meshes[2].HasLight && !deco.Meshes[2].HasGlare);
        Assert.Equal(0x4, deco.Meshes[3].FxFlags); // unknown bit kept, no block read for it
        Assert.Null(rope.Records[0].Decorations);
    }

    [Fact]
    public void Object_Orientation_Rows_Land_In_Their_Named_Slots()
    {
        // Alpine object records store right, up, forward (weather_region.cpp:61-73).
        var cams = (AlpineProjectionCamerasSection)Parse(SectionType.AlpineProjectionCameras, Alpine306Synthetic.Bytes(Alpine306Synthetic.ProjectionCameras()));
        Mat3 o = cams.Records[0].Orientation;
        Assert.Equal(new Vec3(0, 0, -1), o.Forward);
        Assert.Equal(new Vec3(-1, 0, 0), o.Right);
        byte[] body = Alpine306Synthetic.Bytes(cams);
        var r = new RfReader(body, 4 + 4 + 12);
        Assert.Equal(new Vec3(-1, 0, 0), r.ReadVec3()); // right is the first row on disk
    }

    // ---- truncation -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RecordChunkTypes))]
    public void Truncation_At_Every_Byte_Keeps_Alpines_Records_And_The_Raw_Bytes(SectionType type)
    {
        IRflSectionContent model = Alpine306Synthetic.Chunk(type);
        byte[] full = Alpine306Synthetic.Bytes(model);
        List<int> starts = RecordStarts(type, model, out List<int> ends);
        for (int len = 0; len < full.Length; len++)
        {
            byte[] cut = full[..len];
            IRflSectionContent parsed = Parse(type, cut);
            (bool readOnly, int records) = State(parsed);
            Assert.True(readOnly, $"{type} cut at {len} should be read-only");
            Assert.Equal(cut, Write(parsed));
            Assert.Equal(ExpectedRecords(type, len, starts, ends), records);
        }
    }

    /// <summary>
    /// How many records Alpine keeps from a chunk cut at <paramref name="len"/>: those that fit whole — except the
    /// projection camera, whose only field after the header is a lenient string, so a record whose header fits is kept
    /// (projection_camera.cpp:88-104).
    /// </summary>
    private static int ExpectedRecords(SectionType type, int len, List<int> starts, List<int> ends)
    {
        if (type == SectionType.AlpineProjectionCameras)
        {
            return starts.Count(s => s + 52 <= len);
        }

        return ends.Count(e => e <= len);
    }

    /// <summary>The byte offsets each record starts and ends at in the written chunk.</summary>
    private static List<int> RecordStarts(SectionType type, IRflSectionContent model, out List<int> ends)
    {
        var starts = new List<int>();
        ends = new List<int>();
        if (model is AlpineMoverPathsSection mp)
        {
            int at = 4;
            foreach (AlpineMoverPathMover _ in mp.Movers)
            {
                starts.Add(at);
                ends.Add(at += 8);
            }

            at += 4;
            foreach (AlpineMoverPathKeyframe _ in mp.Keyframes)
            {
                starts.Add(at);
                ends.Add(at += 32);
            }

            return starts;
        }

        // Re-write prefixes of the record list to find each record's end.
        int count = State(model).Records;
        for (int n = 1; n <= count; n++)
        {
            IRflSectionContent prefix = Alpine306Synthetic.Chunk(type);
            Trim(prefix, n);
            int end = Alpine306Synthetic.Bytes(prefix).Length;
            starts.Add(n == 1 ? 4 : ends[^1]);
            ends.Add(end);
        }

        return starts;
    }

    private static void Trim(IRflSectionContent c, int n)
    {
        switch (c)
        {
            case AlpineWeatherRegionsSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
            case AlpineVehicleFactoriesSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
            case AlpineProjectionCamerasSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
            case AlpineRopeEmittersSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
            case AlpineDirectionalLightsSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
            case AlpineWaypointHelpersSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
            case AlpineLiquidEmissionSection s: s.Records.RemoveRange(n, s.Records.Count - n); break;
        }
    }

    [Theory]
    [MemberData(nameof(RecordChunkTypes))]
    public void Bytes_After_The_Last_Record_Keep_The_Chunk_Raw(SectionType type)
    {
        byte[] full = Alpine306Synthetic.Bytes(Alpine306Synthetic.Chunk(type));
        byte[] longer = full.Concat(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }).ToArray();
        IRflSectionContent parsed = Parse(type, longer);
        Assert.True(State(parsed).ReadOnly);
        Assert.Equal(State(Alpine306Synthetic.Chunk(type)).Records, State(parsed).Records);
        Assert.Equal(longer, Write(parsed));
    }

    // ---- count caps -----------------------------------------------------------------------------

    [Fact]
    public void Counts_Over_Alpines_Cap_Clamp_Or_Reject_As_Alpine_Does()
    {
        // Vehicle factories clamp to 1000 (vehicle_meshes.h:6).
        var vf = new AlpineVehicleFactoriesSection();
        for (int i = 0; i < 1001; i++)
        {
            vf.Records.Add(new AlpineVehicleFactory { Uid = i, ScriptName = "f", VehicleClass = "jeep" });
        }

        var vfParsed = (AlpineVehicleFactoriesSection)Parse(SectionType.AlpineVehicleFactories, Alpine306Synthetic.Bytes(vf));
        Assert.Equal(1000, vfParsed.Records.Count);
        Assert.Equal(1001u, vfParsed.StoredCount);
        Assert.True(vfParsed.IsReadOnly);

        // Directional lights clamp to 1024, waypoint helpers to 4096.
        var dl = new AlpineDirectionalLightsSection();
        for (int i = 0; i < 1025; i++)
        {
            dl.Records.Add(new AlpineDirectionalLight { Uid = i });
        }

        Assert.Equal(1024, ((AlpineDirectionalLightsSection)Parse(SectionType.AlpineDirectionalLights, Alpine306Synthetic.Bytes(dl))).Records.Count);

        var wh = new AlpineWaypointHelpersSection();
        for (int i = 0; i < 4097; i++)
        {
            wh.Records.Add(new AlpineWaypointHelper { Uid = i });
        }

        Assert.Equal(4096, ((AlpineWaypointHelpersSection)Parse(SectionType.AlpineWaypointHelpers, Alpine306Synthetic.Bytes(wh))).Records.Count);

        // Liquid emission and mover paths reject a count over 65536 outright (liquid_emission.cpp:129, mover_path.h:216).
        byte[] liquid = BitConverter.GetBytes(65537u).Concat(new byte[16]).ToArray();
        var lq = (AlpineLiquidEmissionSection)Parse(SectionType.AlpineLiquidEmission, liquid);
        Assert.Empty(lq.Records);
        Assert.True(lq.IsReadOnly);
        Assert.Equal(liquid, Write(lq));

        byte[] path = BitConverter.GetBytes(65537u).Concat(new byte[8]).ToArray();
        var mp = (AlpineMoverPathsSection)Parse(SectionType.AlpineMoverPaths, path);
        Assert.Empty(mp.Movers);
        Assert.True(mp.IsReadOnly);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void A_Rope_Decoration_Count_Outside_1_To_6_Ends_The_Load(int meshCount)
    {
        AlpineRopeEmittersSection ropes = Alpine306Synthetic.RopeEmitters();
        byte[] bytes = Alpine306Synthetic.Bytes(ropes);

        // The second rope's decoration block: patch its mesh count byte.
        var first = Alpine306Synthetic.RopeEmitters();
        first.Records.RemoveAt(1);
        int secondStart = Alpine306Synthetic.Bytes(first).Length;
        int countOffset = FindDecorationCount(bytes, secondStart);
        bytes[countOffset] = (byte)meshCount;

        var parsed = (AlpineRopeEmittersSection)Parse(SectionType.AlpineRopeEmitters, bytes);
        Assert.Single(parsed.Records); // the first rope is kept, the rest of the chunk is dropped
        Assert.True(parsed.IsReadOnly);
        Assert.Equal(bytes, Write(parsed));
    }

    private static int FindDecorationCount(byte[] bytes, int recordStart)
    {
        var r = new RfReader(bytes, recordStart);
        r.Position += 4 + 48; // uid, pos, orientation
        r.ReadVString();
        r.Position += 4 * 7; // target, dangle, slack, weight, thickness, segments, uv
        r.Position += 4; // color
        r.ReadVString();
        r.Position += 4 + 4 + 4 + 1; // sway amplitude, sway speed, flags, initially on
        Assert.Equal(1, r.ReadU8()); // has decoration block
        r.ReadU8(); // enabled
        return r.Position;
    }

    [Fact]
    public void A_Non_Canonical_Decoration_Flag_Keeps_The_Rope_Chunk_Raw()
    {
        var one = Alpine306Synthetic.RopeEmitters();
        one.Records.RemoveAt(0);
        byte[] bytes = Alpine306Synthetic.Bytes(one);
        int flagAt = FindDecorationCount(bytes, 4) - 2;
        bytes[flagAt] = 2; // Alpine reads any non-zero value as "block present"
        var parsed = (AlpineRopeEmittersSection)Parse(SectionType.AlpineRopeEmitters, bytes);
        Assert.Single(parsed.Records);
        Assert.True(parsed.IsReadOnly);
        Assert.Equal(bytes, Write(parsed));
    }

    // ---- mesh trailing blocks -------------------------------------------------------------------

    public static IEnumerable<object[]> MeshBlockLayouts() =>
        Enum.GetValues<AlpineMeshTrailingBlocks>().Select(b => new object[] { b });

    [Theory]
    [MemberData(nameof(MeshBlockLayouts))]
    public void Mesh_Trailing_Block_Layouts_Round_Trip_And_Are_Remembered(AlpineMeshTrailingBlocks layout)
    {
        var mesh = new AlpineMeshObjectsSection { Blocks = layout };
        mesh.Meshes.Add(new AlpineMeshObject { Uid = 1, MeshFilename = "a.v3m", CollisionMode = 3, NoShadowCast = 1, BrushGeoSource = 2, CollisionMeshFilename = "c.v3m", DrawScale = 2 });
        mesh.Meshes.Add(new AlpineMeshObject { Uid = 2, MeshFilename = "b.v3m", CollisionMode = 2 });
        byte[] bytes = Write(mesh);

        var parsed = (AlpineMeshObjectsSection)Parse(SectionType.AlpineMeshObjects, bytes);
        Assert.Equal(layout, parsed.Blocks);
        Assert.Equal(bytes, Write(parsed));
        if (layout != AlpineMeshTrailingBlocks.None)
        {
            Assert.Equal(1, parsed.Meshes[0].NoShadowCast);
        }

        if (layout == AlpineMeshTrailingBlocks.Full)
        {
            Assert.Equal("c.v3m", parsed.Meshes[0].CollisionMeshFilename);
            Assert.Equal(2f, parsed.Meshes[0].DrawScale);
            Assert.Equal(1f, parsed.Meshes[1].DrawScale);
        }

        // Below RFL 306 the blocks are not read (mesh.cpp:491, 500): a 305 chunk keeps them as unread bytes or
        // refuses a layout Alpine 305 does not have.
        if (layout is AlpineMeshTrailingBlocks.None or AlpineMeshTrailingBlocks.Flags)
        {
            var old = (AlpineMeshObjectsSection)Parse(SectionType.AlpineMeshObjects, bytes, V305);
            Assert.Equal(AlpineMeshTrailingBlocks.None, old.Blocks);
            Assert.Equal(bytes, Write(old, V305));
        }
    }

    [Fact]
    public void Mesh_Upgrade_Adds_Both_Blocks_With_Defaults()
    {
        var mesh = new AlpineMeshObjectsSection();
        mesh.Meshes.Add(new AlpineMeshObject { Uid = 1, MeshFilename = "a.v3m" });
        byte[] v305 = Write(mesh, V305);
        var parsed = (AlpineMeshObjectsSection)Parse(SectionType.AlpineMeshObjects, v305, V305);
        parsed.RewriteWithTrailingBlocks();
        byte[] v306 = Write(parsed);
        // A: no-shadow-cast 0; B: geometry source 0, empty collision mesh name, draw scale 1.0.
        Assert.Equal(v305.Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0x80, 0x3F }).ToArray(), v306);
        var back = (AlpineMeshObjectsSection)Parse(SectionType.AlpineMeshObjects, v306);
        Assert.Equal(AlpineMeshTrailingBlocks.Full, back.Blocks);
        Assert.Equal(1f, back.Meshes[0].DrawScale);
    }

    // ---- props v5 / v6 --------------------------------------------------------------------------

    [Fact]
    public void Props_V6_Every_Field_Prefix_Round_Trips_And_Keeps_Defaults_Beyond_It()
    {
        var full = new AlpineLevelPropertiesSection
        {
            Version = 6, LegacyCyclicTimers = 0, LegacyMovers = 0, StartsWithHeadlamp = 0, StaticMeshAmbientLightModifier = 2f,
            EnableSun = 1, SunYaw = 12, SunPitch = 34, LegacyLighting = 1, NoShadowCastBrushUids = { 7, 8, 9 }, LightmapDensity = 32,
            D3D11OnlyLightmaps = 3, LightmapCompression = 2, VehicleFlightCeilingEnabled = 1, VehicleFlightCeiling = 99,
            MinimapEnabled = 1, MinimapBitmap = "m.tga", MinimapWorldMin = new Vec3(-1, -2, -3), MinimapWorldMax = new Vec3(1, 2, 3),
            MinimapCutHeight = 5, RequireD3D11 = 2, CameraFarClip = 800, AlphaTestedOcclusion = 1,
        };
        full.PayloadRules[0] = 3f;
        byte[] bytes = Write(full);
        Assert.Equal(4 + 1 + 2 + 1 + 4 + 1 + 4 + 4 + 4 + 38 + 12 + 1 + 4 + 1 + 2 + 5 + 24 + 4 + 1 + 4 + 1 + 48, bytes.Length);

        for (int len = 0; len <= bytes.Length; len++)
        {
            byte[] cut = bytes[..len];
            var p = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties, cut);
            Assert.Equal(cut, Write(p));
        }

        // A v6 chunk ending after the far clip (the corpus has such files): payload rules at Alpine's defaults.
        var noPayload = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties, bytes[..^49]);
        Assert.True(noPayload.IsShortLayout);
        Assert.Equal(800f, noPayload.CameraFarClip);
        Assert.Equal(AlpineLevelPropertiesSection.DefaultPayloadRules, noPayload.PayloadRules);
        Assert.Equal(0, noPayload.AlphaTestedOcclusion);
    }

    [Fact]
    public void Props_V5_Short_Layout_Reads_Alpine_Defaults_And_Grows_Only_As_Far_As_An_Edit_Needs()
    {
        // v4 block (25 bytes) + only the sun toggle and yaw: a pre-release v5 the corpus holds (30 bytes).
        byte[] body = Hex("05000000 01 01 01 00 00000040 00 00000000 00000000 00000000 01 0000F041");
        var p = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties, body);
        Assert.Equal(1, p.EnableSun);
        Assert.Equal(30f, p.SunYaw);
        Assert.Equal(90f, p.SunPitch);
        Assert.Equal(255, p.SunColorG);
        Assert.Equal(0, p.LegacyLighting);
        Assert.True(p.IsShortLayout);
        Assert.Equal(body, Write(p));

        p.SunPitch = 45f; // the next field: exactly 4 bytes appended
        Assert.Equal(body.Concat(BitConverter.GetBytes(45f)).ToArray(), Write(p));
    }

    [Fact]
    public void Props_Partial_Payload_Block_Is_Kept_As_Tail()
    {
        var p6 = new AlpineLevelPropertiesSection { Version = 6 };
        byte[] full = Write(p6);
        byte[] partial = full[..^24]; // six of the twelve payload floats
        var p = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties, partial);
        Assert.Equal(24, p.UnparsedTail.Length);
        Assert.Equal(partial, Write(p));
    }

    [Fact]
    public void Props_V7_Reads_As_A_V6_Prefix_And_Any_Save_Rewrites_It_As_V6()
    {
        var v6 = new AlpineLevelPropertiesSection { Version = 6, EnableSun = 1, MinimapEnabled = 1, MinimapBitmap = "x.tga" };
        byte[] v6Bytes = Write(v6);
        byte[] v7Short = v6Bytes[..(v6Bytes.Length - 48 - 1 - 4 - 1)].ToArray(); // ends after the cut height, as the corpus v7 files do
        BitConverter.GetBytes(7u).CopyTo(v7Short, 0);

        RflFile rfl = LevelTemplates.CreateNewRfl(Alpine306Synthetic.FixedDate);
        rfl.Sections.RemoveAll(s => s.TypeId == (uint)SectionType.AlpineLevelProperties); // replaced by the v7 chunk
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineLevelProperties, v7Short));
        byte[] original = rfl.Save();
        RflFile loaded = RflFile.Load(original);
        loaded.ParseAllKnownSections();
        var p = loaded.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
        Assert.Equal(7u, p.Version);
        Assert.Equal("x.tga", p.MinimapBitmap);
        Assert.Contains(loaded.LoadNotes(), n => n.Contains("pre-release layout (version 7)", StringComparison.Ordinal));

        IReadOnlyList<string> notes = loaded.UpgradeToAlpine();
        Assert.Contains(notes, n => n.Contains("version 7", StringComparison.Ordinal));
        byte[] saved = loaded.Save();
        var back = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties,
            RflFile.Load(saved).Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLevelProperties).RawBytes);
        Assert.Equal(6u, back.Version);
        Assert.False(back.IsShortLayout);
        Assert.Equal("x.tga", back.MinimapBitmap);
        Assert.Equal(1, back.EnableSun);

        RflFile again = RflFile.Load(saved);
        Assert.Empty(again.UpgradeToAlpine());
        Assert.Equal(saved, again.Save());
    }

    // ---- lightmaps and terrain ------------------------------------------------------------------

    [Fact]
    public void Lightmap_Section_Structure_Parses_And_Is_Kept_Verbatim()
    {
        byte[] body = Alpine306Synthetic.LightmapSection();
        var lm = (AlpineLightmapsSection)Parse(SectionType.AlpineLightmaps, body);
        Assert.True(lm.IsUsable, lm.StructureProblem);
        Assert.Equal(3u, lm.ChartCount);
        Assert.Equal(2, lm.ChartedSurfaceCount);
        Assert.Equal(new uint[] { 1, 2, 3, 99 }, lm.Tables.Select(t => t.Tag).ToArray()); // an unknown tag is skipped, not fatal
        Assert.Equal(1u, lm.Tables[0].RecordCount);
        AlpineLightmapLayer layer = Assert.Single(lm.Layers);
        Assert.Equal(256 * 256 * 3, (int)layer.UncompressedSize);
        Assert.Equal(lm.PayloadBytes, (int)layer.StoredSize);
        Assert.Equal(0x0BADF00Du, lm.SourceSurfaceHash);
        Assert.Equal(body, Write(lm));
    }

    [Fact]
    public void Lightmap_Section_Version_1_And_Damaged_Sections_Are_Kept_But_Not_Used()
    {
        byte[] v1 = Alpine306Synthetic.LightmapSection(version: 1);
        var old = (AlpineLightmapsSection)Parse(SectionType.AlpineLightmaps, v1);
        Assert.False(old.IsUsable);
        Assert.Equal(1u, old.SectionVersion);
        Assert.Equal(v1, Write(old));

        byte[] full = Alpine306Synthetic.LightmapSection();
        foreach (int len in new[] { 0, 10, 38, 50, 120, full.Length - 1 })
        {
            var cut = (AlpineLightmapsSection)Parse(SectionType.AlpineLightmaps, full[..len]);
            Assert.False(cut.IsUsable);
            Assert.NotNull(cut.StructureProblem);
            Assert.Equal(full[..len], Write(cut));
        }
    }

    [Fact]
    public void Terrain_Structure_Parses_Every_Optional_Part_And_Is_Kept_Verbatim()
    {
        byte[] body = Alpine306Synthetic.TerrainSection();
        var t = (AlpineTerrainSection)Parse(SectionType.AlpineTerrain, body);
        Assert.Null(t.StructureProblem);
        AlpineTerrain terrain = Assert.Single(t.Terrains);
        Assert.Equal(9801, terrain.Uid);
        Assert.Equal("hills", terrain.ScriptName);
        Assert.Equal(2, terrain.Layers.Count);
        Assert.Single(terrain.Overlays);
        Assert.Equal("bush.v3m", Assert.Single(terrain.Decorations).Mesh);
        Assert.Equal(4, terrain.BuildMapping.Count);
        Assert.Equal(65, terrain.Nx);
        Assert.True(terrain.CompressedSize > 0);
        Assert.True(terrain.Grid.IsHole(34, 12));
        Assert.False(terrain.Grid.IsHole(33, 12));
        Assert.True(terrain.Grid.Diagonal(0, 0));
        Assert.NotNull(terrain.Grid.OverlayCoverage);
        Assert.NotNull(terrain.Grid.DecorationCoverage);
        Assert.True(terrain.BuildIsCurrent);
        Assert.Equal(body, Write(t));

        byte[] unknownFlag = Alpine306Synthetic.TerrainSection(flags: 0x41);
        var bad = (AlpineTerrainSection)Parse(SectionType.AlpineTerrain, unknownFlag);
        Assert.Contains("unknown flags", bad.StructureProblem, StringComparison.Ordinal);
        Assert.True(bad.IsReadOnly);
        Assert.Equal(unknownFlag, Write(bad));

        for (int len = 0; len < body.Length; len++)
        {
            var cut = (AlpineTerrainSection)Parse(SectionType.AlpineTerrain, body[..len]);
            Assert.NotNull(cut.StructureProblem);
            Assert.Empty(cut.Terrains);
            Assert.Equal(body[..len], Write(cut));
        }
    }

    // ---- whole level --------------------------------------------------------------------------

    [Fact]
    public void Synthetic_Level_Matches_The_Committed_Fixture()
    {
        byte[] generated = Alpine306Synthetic.Level();
        string path = Alpine306Synthetic.FixturePath;
        if (Environment.GetEnvironmentVariable("GED_REGEN_ALPINE306_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generated);
        }

        Assert.True(File.Exists(path), $"missing {path}; regenerate with GED_REGEN_ALPINE306_FIXTURES=1");
        Assert.Equal(File.ReadAllBytes(path), generated);
    }

    [Fact]
    public void Synthetic_Level_Loads_Every_Chunk_Typed_And_Saves_Byte_Identically()
    {
        byte[] original = Alpine306Synthetic.Level();
        RflFile rfl = RflFile.Load(original);
        rfl.ParseAllKnownSections();
        Assert.Equal(306, rfl.Header.Version);
        Assert.Empty(rfl.LoadNotes());
        foreach (SectionType t in Alpine306Synthetic.RecordChunks)
        {
            RflSection s = rfl.Sections.Single(x => x.TypeId == (uint)t);
            Assert.False(State(s.Content!).ReadOnly, t.ToString());
        }

        Assert.True(rfl.Sections.Select(s => s.Content).OfType<AlpineLightmapsSection>().Single().IsUsable);
        Assert.Null(rfl.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().Single().StructureProblem);
        Assert.Equal(AlpineMeshTrailingBlocks.Full, rfl.Sections.Select(s => s.Content).OfType<AlpineMeshObjectsSection>().Single().Blocks);

        // Chunk order: Alpine objects before level_properties (0x900), the foreign / unknown chunks raw in place.
        List<uint> ids = rfl.Sections.Select(s => s.TypeId).ToList();
        Assert.True(ids.IndexOf((uint)SectionType.AlpineLiquidEmission) < ids.IndexOf((uint)SectionType.LevelProperties));
        Assert.Contains(0x5ED00001u, ids);
        Assert.Contains(0x0AFBAE20u, ids);
        Assert.Null(rfl.Sections.Single(s => s.TypeId == 0x5ED00001u).Content);

        Assert.Empty(rfl.UpgradeToAlpine());
        Assert.Equal(original, rfl.Save());
    }

    [Fact]
    public void New_Alpine_Chunks_Insert_In_Alpine_Reds_Order()
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(Alpine306Synthetic.FixedDate);
        rfl.InsertSection(new RflSection((uint)SectionType.Movers, Array.Empty<byte>()));
        rfl.InsertSection(new RflSection((uint)SectionType.MovingGroups, Array.Empty<byte>()));
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineLightmaps, Alpine306Synthetic.LightmapSection()));
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineWeatherRegions, Array.Empty<byte>()));
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineDirectionalLights, Array.Empty<byte>()));
        List<uint> ids = rfl.Sections.Select(s => s.TypeId).ToList();
        int lm = ids.IndexOf((uint)SectionType.AlpineLightmaps);
        Assert.Equal((uint)SectionType.Movers, ids[lm - 1]); // the "after movers" slot (alpine_lightmaps.cpp:2045-2060)
        Assert.Equal((uint)SectionType.MovingGroups, ids[lm + 1]);
        Assert.True(ids.IndexOf((uint)SectionType.AlpineWeatherRegions) < ids.IndexOf((uint)SectionType.AlpineDirectionalLights));
        Assert.True(ids.IndexOf((uint)SectionType.AlpineDirectionalLights) < ids.IndexOf((uint)SectionType.LevelProperties));
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));
}
